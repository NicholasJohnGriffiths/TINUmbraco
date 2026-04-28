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

    private static readonly string[] RequiredSectionAliases =
    [
        ReportsPageAlias,
        MembershipsPageAlias,
        ReferencesPageAlias,
        EventsPageAlias,
        NewsPageAlias
    ];

    public MigrationSectionRoots GetRequiredSectionRoots()
    {
        IContent home = GetRequiredHome();
        IReadOnlyList<IContent> homeChildren = contentService
            .GetPagedChildren(home.Id, 0, int.MaxValue, out _, null, null, null, false)
            .ToList();

        Dictionary<string, IContent> rootsByAlias = homeChildren
            .Where(x => RequiredSectionAliases.Contains(x.ContentType.Alias, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(x => x.ContentType.Alias, StringComparer.OrdinalIgnoreCase);

        string[] missing = RequiredSectionAliases
            .Where(alias => !rootsByAlias.ContainsKey(alias))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing expected section roots under Home: {string.Join(", ", missing)}");
        }

        return new MigrationSectionRoots(
            rootsByAlias[ReportsPageAlias],
            rootsByAlias[MembershipsPageAlias],
            rootsByAlias[ReferencesPageAlias],
            rootsByAlias[EventsPageAlias],
            rootsByAlias[NewsPageAlias]);
    }

    public IContent GetRequiredHome()
    {
        return contentService.GetRootContent()
            .FirstOrDefault(x => string.Equals(x.ContentType.Alias, HomePageAlias, StringComparison.OrdinalIgnoreCase))
            ?? contentService.GetRootContent().FirstOrDefault()
            ?? throw new InvalidOperationException("No root content found.");
    }

    public sealed record MigrationSectionRoots(
        IContent Reports,
        IContent Memberships,
        IContent References,
        IContent Events,
        IContent News);
}