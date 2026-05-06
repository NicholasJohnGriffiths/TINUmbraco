using Stripe;

namespace TINUmbraco.Web.Stripe;

public interface IStripePaymentService
{
    Task<PaymentIntent> CreatePaymentIntentAsync(PaymentIntentCreateOptions options, CancellationToken cancellationToken = default);

    Task<StripePromotionCodeResult> ResolvePromotionCodeAsync(string code, string currency, CancellationToken cancellationToken = default);

    Event ConstructWebhookEvent(string payload, string signatureHeader, string webhookSecret);
}

public sealed record StripePromotionCodeResult(
    bool IsValid,
    string? Error,
    string? PromotionCode,
    decimal? PercentOff,
    long? AmountOffMinor,
    string? CouponCurrency);
