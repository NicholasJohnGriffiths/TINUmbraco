using System.Globalization;
using Umbraco.Cms.Core.Services;

namespace TINUmbraco.Web.Stripe;

public sealed class UmbracoReportPricingService(IContentService contentService) : IReportPricingService
{
    public ReportPricingResult GetPricing(int contentId, string selectionType)
    {
        var report = contentService.GetById(contentId);
        if (report is null)
        {
            return new ReportPricingResult(false, "Report not found.", string.Empty, 0m);
        }

        string? priceRaw = selectionType switch
        {
            "single-user" => report.GetValue("singleUserEbookPrice")?.ToString(),
            "multi-user" => report.GetValue("multiUserEbookPrice")?.ToString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(priceRaw))
        {
            return new ReportPricingResult(false, "Selected pricing option is not available for this report.", report.Name ?? string.Empty, 0m);
        }

        if (!TryParsePrice(priceRaw, out decimal amount))
        {
            return new ReportPricingResult(false, "Invalid price configuration on this report.", report.Name ?? string.Empty, 0m);
        }

        return new ReportPricingResult(true, null, report.Name ?? string.Empty, amount);
    }

    private static bool TryParsePrice(string raw, out decimal amount)
    {
        string cleaned = new string(raw.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) && amount > 0;
    }
}
