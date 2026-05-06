using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stripe;
using TINUmbraco.Web.Stripe;
using TINUmbraco.Web.Tools;
using System.Text;
using System.Text.Json;
using Umbraco.Cms.Core.Services;

namespace TINUmbraco.Web.Controllers;

[Route("api/report")]
public sealed class ReportPurchaseController(
    IOptions<StripeOptions> stripeOptions,
    IReportPricingService reportPricingService,
    IStripePaymentService stripePaymentService,
    ToolsAccessService toolsAccessService,
    IKeyValueService keyValueService,
    IHostEnvironment hostEnvironment,
    ILogger<ReportPurchaseController> logger) : Controller
{
    private const decimal GstRate = 0.15m;
    private const string Currency = "nzd";

    [HttpGet("stripe-status")]
    public IActionResult StripeStatus()
    {
        return Ok(new
        {
            environment = hostEnvironment.EnvironmentName,
            publishableKeyConfigured = !string.IsNullOrWhiteSpace(stripeOptions.Value.PublishableKey),
            secretKeyConfigured = !string.IsNullOrWhiteSpace(stripeOptions.Value.SecretKey),
            webhookSecretConfigured = !string.IsNullOrWhiteSpace(stripeOptions.Value.WebhookSecret)
        });
    }

    [HttpGet("stripe-webhook-audit")]
    public IActionResult StripeWebhookAudit([FromQuery] int limit = 20)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        int safeLimit = Math.Clamp(limit, 1, 200);
        IReadOnlyDictionary<string, string?> auditEntries =
            keyValueService.FindByKeyPrefix("stripe:webhook:payment_intent.succeeded:")
            ?? new Dictionary<string, string?>();

        var results = auditEntries
            .Select(kvp =>
            {
                DateTimeOffset? createdUtc = null;

                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(kvp.Value);
                        if (doc.RootElement.TryGetProperty("createdUtc", out JsonElement created) &&
                            created.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(created.GetString(), out DateTimeOffset parsed))
                        {
                            createdUtc = parsed;
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed payloads and return raw value.
                    }
                }

                return new
                {
                    key = kvp.Key,
                    createdUtc,
                    value = kvp.Value
                };
            })
            .OrderByDescending(x => x.createdUtc)
            .Take(safeLimit)
            .ToArray();

        return Ok(results);
    }

    [HttpPost("create-payment-intent")]
    public async Task<IActionResult> CreatePaymentIntent([FromBody] CreatePaymentIntentRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Invalid request." });
        }

        if (string.IsNullOrWhiteSpace(stripeOptions.Value.SecretKey))
        {
            return StatusCode(503, new { error = "Stripe secret key is not configured." });
        }

        if (string.IsNullOrWhiteSpace(request.SelectionType) || string.IsNullOrWhiteSpace(request.CustomerEmail))
        {
            return BadRequest(new { error = "Selection type and customer email are required." });
        }

        ReportPricingResult pricing = reportPricingService.GetPricing(request.ContentId, request.SelectionType);
        if (!pricing.Found)
        {
            return BadRequest(new { error = pricing.Error ?? "Unable to determine report pricing." });
        }

        decimal discountedBaseAmount = pricing.BaseAmount;
        string appliedPromotionCode = string.Empty;
        string discountType = "none";
        decimal discountAmount = 0m;

        if (!string.IsNullOrWhiteSpace(request.PromotionCode))
        {
            StripeConfiguration.ApiKey = stripeOptions.Value.SecretKey;
            StripePromotionCodeResult promoResult = await stripePaymentService.ResolvePromotionCodeAsync(
                request.PromotionCode,
                Currency,
                HttpContext.RequestAborted);

            if (!promoResult.IsValid)
            {
                return BadRequest(new { error = promoResult.Error ?? "Invalid promotion code." });
            }

            if (promoResult.PercentOff.HasValue)
            {
                decimal percent = promoResult.PercentOff.Value / 100m;
                discountAmount = decimal.Round(pricing.BaseAmount * percent, 2, MidpointRounding.AwayFromZero);
                discountedBaseAmount = Math.Max(pricing.BaseAmount - discountAmount, 0m);
                discountType = "percent";
            }
            else if (promoResult.AmountOffMinor.HasValue)
            {
                decimal amountOff = promoResult.AmountOffMinor.Value / 100m;
                discountAmount = Math.Min(amountOff, pricing.BaseAmount);
                discountedBaseAmount = Math.Max(pricing.BaseAmount - discountAmount, 0m);
                discountType = "amount";
            }

            appliedPromotionCode = promoResult.PromotionCode ?? request.PromotionCode;
        }

        decimal gstAmount = decimal.Round(discountedBaseAmount * GstRate, 2, MidpointRounding.AwayFromZero);
        decimal totalAmount = discountedBaseAmount + gstAmount;
        long amountCents = (long)Math.Round(totalAmount * 100, MidpointRounding.AwayFromZero);

        if (amountCents <= 0)
        {
            return BadRequest(new { error = "Total amount must be greater than zero." });
        }

        StripeConfiguration.ApiKey = stripeOptions.Value.SecretKey;

        PaymentIntent paymentIntent = await stripePaymentService.CreatePaymentIntentAsync(new PaymentIntentCreateOptions
        {
            Amount = amountCents,
            Currency = Currency,
            PaymentMethodTypes = ["card"],
            ReceiptEmail = request.CustomerEmail,
            Description = $"Report purchase: {pricing.ReportTitle} ({request.SelectionType})",
            Metadata = new Dictionary<string, string>
            {
                ["reportTitle"] = pricing.ReportTitle,
                ["reportContentId"] = request.ContentId.ToString(),
                ["selectionType"] = request.SelectionType,
                ["customerName"] = request.CustomerName,
                ["customerEmail"] = request.CustomerEmail,
                ["customerCompany"] = request.CustomerCompany ?? string.Empty,
                ["customerPhone"] = request.CustomerPhone ?? string.Empty,
                ["promotionCode"] = appliedPromotionCode,
                ["baseAmount"] = pricing.BaseAmount.ToString("F2"),
                ["discountType"] = discountType,
                ["discountAmount"] = discountAmount.ToString("F2"),
                ["discountedBaseAmount"] = discountedBaseAmount.ToString("F2"),
                ["gstAmount"] = gstAmount.ToString("F2"),
                ["totalAmount"] = totalAmount.ToString("F2")
            }
        }, HttpContext.RequestAborted);

        return Ok(new
        {
            clientSecret = paymentIntent.ClientSecret,
            pricing = new
            {
                baseAmount = pricing.BaseAmount,
                discountAmount,
                discountedBaseAmount,
                gstAmount,
                totalAmount,
                appliedPromotionCode
            }
        });
    }

    [HttpPost("stripe-webhook")]
    public async Task<IActionResult> StripeWebhook()
    {
        if (string.IsNullOrWhiteSpace(stripeOptions.Value.WebhookSecret))
        {
            logger.LogWarning("Stripe webhook called but webhook secret is not configured.");
            return StatusCode(503);
        }

        string signatureHeader = Request.Headers["Stripe-Signature"].ToString();
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return BadRequest();
        }

        string payload;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            payload = await reader.ReadToEndAsync();
        }

        Event stripeEvent;
        try
        {
            stripeEvent = stripePaymentService.ConstructWebhookEvent(payload, signatureHeader, stripeOptions.Value.WebhookSecret);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe webhook signature verification failed.");
            return BadRequest();
        }

        if (stripeEvent.Type == "payment_intent.succeeded")
        {
            PaymentIntent? paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent is not null)
            {
                string auditKey = $"stripe:webhook:{stripeEvent.Type}:{paymentIntent.Id}";
                string auditValue = JsonSerializer.Serialize(new
                {
                    eventId = stripeEvent.Id,
                    eventType = stripeEvent.Type,
                    paymentIntentId = paymentIntent.Id,
                    amountReceivedMinor = paymentIntent.AmountReceived,
                    currency = paymentIntent.Currency,
                    createdUtc = DateTimeOffset.UtcNow,
                    metadata = paymentIntent.Metadata
                });

                keyValueService.SetValue(auditKey, auditValue);

                logger.LogInformation(
                    "Stripe payment succeeded. PaymentIntentId={PaymentIntentId}, Amount={Amount}, Currency={Currency}, Metadata={Metadata}",
                    paymentIntent.Id,
                    paymentIntent.AmountReceived,
                    paymentIntent.Currency,
                    JsonSerializer.Serialize(paymentIntent.Metadata));
            }
        }

        return Ok();
    }
}

public sealed record CreatePaymentIntentRequest(
    int ContentId,
    string SelectionType,
    string CustomerName,
    string CustomerEmail,
    string? CustomerCompany,
    string? CustomerPhone,
    string? PromotionCode);
