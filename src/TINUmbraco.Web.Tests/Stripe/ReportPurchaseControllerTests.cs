using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Stripe;
using TINUmbraco.Web.Controllers;
using TINUmbraco.Web.Stripe;
using TINUmbraco.Web.Tools;
using Umbraco.Cms.Core.Services;
using Xunit;

namespace TINUmbraco.Web.Tests.Stripe;

public sealed class ReportPurchaseControllerTests
{
    [Fact]
    public async Task CreatePaymentIntent_AppliesPercentPromotion_AndAddsGst()
    {
        var stripeService = new StubStripePaymentService
        {
            PromoResult = new StripePromotionCodeResult(true, null, "SAVE10", 10m, null, null),
            CreatedPaymentIntent = new PaymentIntent { ClientSecret = "cs_test_123" }
        };

        var controller = CreateController(
            options: new StripeOptions { SecretKey = "sk_test_x", WebhookSecret = "whsec_test_x" },
            pricingService: new StubReportPricingService(new ReportPricingResult(true, null, "Example Report", 100m)),
            stripePaymentService: stripeService);

        var result = await controller.CreatePaymentIntent(new CreatePaymentIntentRequest(
            ContentId: 99,
            SelectionType: "single-user",
            CustomerName: "Test User",
            CustomerEmail: "test@example.com",
            CustomerCompany: "Tin100",
            CustomerPhone: "021000000",
            PromotionCode: "SAVE10"));

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        Assert.NotNull(stripeService.LastCreateOptions);
        Assert.Equal(10350L, stripeService.LastCreateOptions!.Amount);

        Assert.Equal("SAVE10", stripeService.LastCreateOptions.Metadata["promotionCode"]);
        Assert.Equal("10.00", stripeService.LastCreateOptions.Metadata["discountAmount"]);
        Assert.Equal("90.00", stripeService.LastCreateOptions.Metadata["discountedBaseAmount"]);
        Assert.Equal("13.50", stripeService.LastCreateOptions.Metadata["gstAmount"]);
        Assert.Equal("103.50", stripeService.LastCreateOptions.Metadata["totalAmount"]);
    }

    [Fact]
    public async Task CreatePaymentIntent_ReturnsBadRequest_WhenPromotionCodeIsInvalid()
    {
        var stripeService = new StubStripePaymentService
        {
            PromoResult = new StripePromotionCodeResult(false, "Invalid or inactive promotion code.", null, null, null, null)
        };

        var controller = CreateController(
            options: new StripeOptions { SecretKey = "sk_test_x", WebhookSecret = "whsec_test_x" },
            pricingService: new StubReportPricingService(new ReportPricingResult(true, null, "Example Report", 100m)),
            stripePaymentService: stripeService);

        var result = await controller.CreatePaymentIntent(new CreatePaymentIntentRequest(
            ContentId: 100,
            SelectionType: "single-user",
            CustomerName: "Test User",
            CustomerEmail: "test@example.com",
            CustomerCompany: null,
            CustomerPhone: null,
            PromotionCode: "BADCODE"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task StripeWebhook_ReturnsOk_ForValidSignedEvent()
    {
        var stripeService = new StubStripePaymentService
        {
            WebhookEvent = new Event
            {
                Type = "payment_intent.succeeded",
                Data = new EventData
                {
                    Object = new PaymentIntent
                    {
                        Id = "pi_123",
                        AmountReceived = 10350,
                        Currency = "nzd",
                        Metadata = new Dictionary<string, string> { ["reportContentId"] = "99" }
                    }
                }
            }
        };

        var controller = CreateController(
            options: new StripeOptions { SecretKey = "sk_test_x", WebhookSecret = "whsec_test_x" },
            pricingService: new StubReportPricingService(new ReportPricingResult(true, null, "Example Report", 100m)),
            stripePaymentService: stripeService);

        string jsonPayload = "{\"id\":\"evt_123\",\"type\":\"payment_intent.succeeded\"}";
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Request.Headers["Stripe-Signature"] = "t=1,v1=abc";
        controller.HttpContext.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonPayload));

        var result = await controller.StripeWebhook();

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void StripeWebhookAudit_ReturnsEntries_ForLocalDevelopmentAccess()
    {
        var keyValueService = new StubKeyValueService();
        keyValueService.SetValue(
            "stripe:webhook:payment_intent.succeeded:pi_1",
            "{\"eventId\":\"evt_1\",\"createdUtc\":\"2026-05-06T09:00:00Z\"}");
        keyValueService.SetValue(
            "stripe:webhook:payment_intent.succeeded:pi_2",
            "{\"eventId\":\"evt_2\",\"createdUtc\":\"2026-05-06T10:00:00Z\"}");

        var controller = CreateController(
            options: new StripeOptions { SecretKey = "sk_test_x", WebhookSecret = "whsec_test_x" },
            pricingService: new StubReportPricingService(new ReportPricingResult(true, null, "Example Report", 100m)),
            stripePaymentService: new StubStripePaymentService(),
            keyValueService: keyValueService,
            hostEnvironment: new StubHostEnvironment { EnvironmentName = Environments.Development });

        controller.HttpContext.Request.Host = new HostString("localhost");

        var result = controller.StripeWebhookAudit(limit: 1);

        Assert.IsType<OkObjectResult>(result);
    }

    private static ReportPurchaseController CreateController(
        StripeOptions options,
        IReportPricingService pricingService,
        IStripePaymentService stripePaymentService,
        StubKeyValueService? keyValueService = null,
        StubHostEnvironment? hostEnvironment = null)
    {
        keyValueService ??= new StubKeyValueService();
        hostEnvironment ??= new StubHostEnvironment();

        var controller = new ReportPurchaseController(
            Options.Create(options),
            pricingService,
            stripePaymentService,
            new ToolsAccessService(hostEnvironment),
            keyValueService,
            hostEnvironment,
            NullLogger<ReportPurchaseController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        return controller;
    }

    private sealed class StubKeyValueService : IKeyValueService
    {
        private readonly Dictionary<string, string> values = new();

        public string? GetValue(string key) => values.TryGetValue(key, out string? value) ? value : null;

        public IReadOnlyDictionary<string, string?> FindByKeyPrefix(string keyPrefix)
            => values
                .Where(x => x.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => (string?)x.Value);

        public void SetValue(string key, string? value)
        {
            if (value is null)
            {
                values.Remove(key);
                return;
            }

            values[key] = value;
        }

        public void SetValue(string key, string? value, string? origin)
            => SetValue(key, value);

        public bool TrySetValue(string key, string? value, string? oldValue)
        {
            string? current = GetValue(key);
            if (!string.Equals(current, oldValue, StringComparison.Ordinal))
            {
                return false;
            }

            SetValue(key, value);
            return true;
        }
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "TINUmbraco.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubReportPricingService(ReportPricingResult result) : IReportPricingService
    {
        public ReportPricingResult GetPricing(int contentId, string selectionType) => result;
    }

    private sealed class StubStripePaymentService : IStripePaymentService
    {
        public StripePromotionCodeResult PromoResult { get; set; } = new(false, "No promo", null, null, null, null);

        public PaymentIntent? CreatedPaymentIntent { get; set; }

        public PaymentIntentCreateOptions? LastCreateOptions { get; private set; }

        public Event? WebhookEvent { get; set; }

        public Task<PaymentIntent> CreatePaymentIntentAsync(PaymentIntentCreateOptions options, CancellationToken cancellationToken = default)
        {
            LastCreateOptions = options;
            return Task.FromResult(CreatedPaymentIntent ?? new PaymentIntent { ClientSecret = "cs_default" });
        }

        public Task<StripePromotionCodeResult> ResolvePromotionCodeAsync(string code, string currency, CancellationToken cancellationToken = default)
            => Task.FromResult(PromoResult);

        public Event ConstructWebhookEvent(string payload, string signatureHeader, string webhookSecret)
            => WebhookEvent ?? new Event { Type = "payment_intent.succeeded" };
    }
}
