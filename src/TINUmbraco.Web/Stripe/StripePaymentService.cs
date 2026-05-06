using System.Globalization;
using Stripe;

namespace TINUmbraco.Web.Stripe;

public sealed class StripePaymentService : IStripePaymentService
{
    public async Task<PaymentIntent> CreatePaymentIntentAsync(PaymentIntentCreateOptions options, CancellationToken cancellationToken = default)
    {
        var service = new PaymentIntentService();
        return await service.CreateAsync(options, cancellationToken: cancellationToken);
    }

    public async Task<StripePromotionCodeResult> ResolvePromotionCodeAsync(string code, string currency, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new StripePromotionCodeResult(false, "Promotion code is empty.", null, null, null, null);
        }

        var promotionCodeService = new PromotionCodeService();
        StripeList<PromotionCode> promotionCodes = await promotionCodeService.ListAsync(new PromotionCodeListOptions
        {
            Code = code.Trim(),
            Active = true,
            Limit = 1
        }, cancellationToken: cancellationToken);

        PromotionCode? promo = promotionCodes.Data.FirstOrDefault();
        if (promo is null)
        {
            return new StripePromotionCodeResult(false, "Invalid or inactive promotion code.", null, null, null, null);
        }

        string? couponId = promo.Promotion?.CouponId;
        if (string.IsNullOrWhiteSpace(couponId))
        {
            return new StripePromotionCodeResult(false, "Promotion code does not have a valid coupon.", null, null, null, null);
        }

        var couponService = new CouponService();
        Coupon coupon = await couponService.GetAsync(couponId, cancellationToken: cancellationToken);

        // Prefer percent_off; otherwise use amount_off only when currency matches.
        if (coupon.PercentOff.HasValue)
        {
            return new StripePromotionCodeResult(true, null, promo.Code, coupon.PercentOff.Value, null, null);
        }

        if (coupon.AmountOff.HasValue)
        {
            if (!string.Equals(coupon.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                return new StripePromotionCodeResult(false, "Promotion code currency does not match the report currency.", null, null, null, null);
            }

            return new StripePromotionCodeResult(true, null, promo.Code, null, coupon.AmountOff.Value, coupon.Currency);
        }

        return new StripePromotionCodeResult(false, "Promotion code has no supported discount type.", null, null, null, null);
    }

    public Event ConstructWebhookEvent(string payload, string signatureHeader, string webhookSecret)
    {
        return EventUtility.ConstructEvent(payload, signatureHeader, webhookSecret);
    }
}
