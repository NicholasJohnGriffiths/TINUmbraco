namespace TINUmbraco.Web.Stripe;

public interface IReportPricingService
{
    ReportPricingResult GetPricing(int contentId, string selectionType);
}

public sealed record ReportPricingResult(
    bool Found,
    string? Error,
    string ReportTitle,
    decimal BaseAmount);
