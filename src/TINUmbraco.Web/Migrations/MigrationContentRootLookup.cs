using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TINUmbraco.Web.Migrations;

public sealed class MigrationContentRootLookup(IContentService contentService)
{
    public const string HomePageAlias = "homePage";
    public const string ReportsPageAlias = "reportsPage";
    public const string MembershipsPageAlias = "membershipsPage";
    public const string ReferencesPageAlias = "referencesPage";
    public const string EventsPageAlias = "eventsPage";
    public const string NewsPageAlias = "newsPage";

    public IContent GetRequiredSectionRoot(string sectionAlias)
    {
        if (string.IsNullOrWhiteSpace(sectionAlias))
        {
            throw new ArgumentException("Section alias cannot be empty.", nameof(sectionAlias));
        }

        IContent home = GetRequiredHome();
        IContent? sectionRoot = contentService
            .GetPagedChildren(home.Id, 0, int.MaxValue, out _, null, null, null, false)
            .FirstOrDefault(x => string.Equals(x.ContentType.Alias, sectionAlias, StringComparison.OrdinalIgnoreCase));

        return sectionRoot
            ?? throw new InvalidOperationException($"Missing expected section root under Home: {sectionAlias}");
    }

    public IContent GetRequiredHome()
    {
        return contentService.GetRootContent()
            .FirstOrDefault(x => string.Equals(x.ContentType.Alias, HomePageAlias, StringComparison.OrdinalIgnoreCase))
            ?? contentService.GetRootContent().FirstOrDefault()
            ?? throw new InvalidOperationException("No root content found.");
    }
}