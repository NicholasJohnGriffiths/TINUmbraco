using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationContentService(
    IContentService contentService,
    IContentTypeService contentTypeService,
    MigrationContentRootLookup rootLookup,
    IWordPressMigrationPropertyMapper propertyMapper)
{
    private const string UrlNamePropertyAlias = "umbracoUrlName";

    public IContent GetParentForWordPressType(string wordPressType)
    {
        if (string.IsNullOrWhiteSpace(wordPressType))
        {
            throw new ArgumentException("WordPress type cannot be empty.", nameof(wordPressType));
        }

        string normalizedType = NormalizeToken(wordPressType);
        string sectionAlias = normalizedType switch
        {
            "report" or "reports" => MigrationContentRootLookup.ReportsPageAlias,
            "membership" or "memberships" => MigrationContentRootLookup.MembershipsPageAlias,
            "directory" or "directories" or "sponsor" or "sponsors" => MigrationContentRootLookup.MembershipsPageAlias,
            "reference" or "references" => MigrationContentRootLookup.ReferencesPageAlias,
            "event" or "events" => MigrationContentRootLookup.EventsPageAlias,
            "news" or "post" or "posts" => MigrationContentRootLookup.NewsPageAlias,
            _ => throw new InvalidOperationException($"No migration section mapping exists for WordPress type '{wordPressType}'.")
        };

        return rootLookup.GetRequiredSectionRoot(sectionAlias);
    }

    public IContent UpsertUnderWordPressSection(
        string wordPressType,
        string contentTypeAlias,
        string name,
        string? slug,
        Action<IContent>? mapValues = null,
        bool publish = true,
        bool allowCreate = true,
        bool dryRun = false,
        int userId = -1)
    {
        IContent parent = GetParentForWordPressType(wordPressType);
        return UpsertChild(parent, contentTypeAlias, name, slug, mapValues, publish, allowCreate, dryRun, userId);
    }

    public IContent Upsert(WordPressMigrationItem item, int userId = -1)
    {
        return Upsert(item, userId, allowCreate: true, dryRun: false);
    }

    public IContent Upsert(WordPressMigrationItem item, int userId = -1, bool allowCreate = true, bool dryRun = false)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        string effectiveName = ResolveItemName(item);

        return UpsertUnderWordPressSection(
            item.WordPressType,
            item.ContentTypeAlias,
            effectiveName,
            item.Slug,
            mapValues: content => propertyMapper.ApplyValues(content, item.Values, item.WordPressType, dryRun),
            publish: item.Publish,
            allowCreate: allowCreate,
            dryRun: dryRun,
            userId: userId);
    }

    public IContent UpsertChild(
        IContent parent,
        string contentTypeAlias,
        string name,
        string? slug,
        Action<IContent>? mapValues = null,
        bool publish = true,
        bool allowCreate = true,
        bool dryRun = false,
        int userId = -1)
    {
        if (parent is null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        if (string.IsNullOrWhiteSpace(contentTypeAlias))
        {
            throw new ArgumentException("Content type alias cannot be empty.", nameof(contentTypeAlias));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Content name cannot be empty.", nameof(name));
        }

        string normalizedSlug = NormalizeSlug(slug ?? name);
        IContent? existing = FindExistingChildBySlug(parent.Id, contentTypeAlias, normalizedSlug);

        if (existing is null && !allowCreate)
        {
            throw new WordPressMigrationSkipItemException(
                $"Skipped because no existing '{contentTypeAlias}' item matched slug '{normalizedSlug}'.");
        }

        IContent content = dryRun
            ? contentService.Create(name, parent.Id, contentTypeAlias, userId)
            : existing ?? contentService.Create(name, parent.Id, contentTypeAlias, userId);

        content.Name = name.Trim();

        if (HasProperty(content, UrlNamePropertyAlias))
        {
            content.SetValue(UrlNamePropertyAlias, normalizedSlug);
        }

        mapValues?.Invoke(content);

        EnsureDefaultTemplateAssigned(content);

        if (!dryRun)
        {
            contentService.Save([content], userId);

            if (publish)
            {
                contentService.Publish(content, Array.Empty<string>(), userId);
            }
        }

        return content;
    }

    private void EnsureDefaultTemplateAssigned(IContent content)
    {
        if (content.TemplateId > 0)
        {
            return;
        }

        IContentType? fullContentType = contentTypeService.Get(content.ContentTypeId);
        int? defaultTemplateId = fullContentType?.DefaultTemplateId;
        if (!defaultTemplateId.HasValue || defaultTemplateId.Value <= 0)
        {
            return;
        }

        content.TemplateId = defaultTemplateId.Value;
    }

    private IContent? FindExistingChildBySlug(int parentId, string contentTypeAlias, string normalizedSlug)
    {
        const int pageSize = 200;
        long pageIndex = 0;

        while (true)
        {
            IReadOnlyList<IContent> page = contentService
                .GetPagedChildren(parentId, pageIndex, pageSize, out _, null, null, null, false)
                .ToList();

            if (page.Count == 0)
            {
                return null;
            }

            // Match by umbracoUrlName first; if missing, fall back to normalized name.
            IContent? match = page.FirstOrDefault(child =>
                string.Equals(child.ContentType.Alias, contentTypeAlias, StringComparison.OrdinalIgnoreCase)
                && (
                    string.Equals(NormalizeSlug(GetUrlName(child)), normalizedSlug, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(NormalizeSlug(child.Name ?? string.Empty), normalizedSlug, StringComparison.OrdinalIgnoreCase)
                ));

            if (match is not null)
            {
                return match;
            }

            if (page.Count < pageSize)
            {
                return null;
            }

            pageIndex++;
        }
    }

    private static bool HasProperty(IContent content, string propertyAlias)
    {
        return content.Properties.Any(p => string.Equals(p.Alias, propertyAlias, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetUrlName(IContent content)
    {
        if (!HasProperty(content, UrlNamePropertyAlias))
        {
            return string.Empty;
        }

        return content.GetValue<string>(UrlNamePropertyAlias) ?? string.Empty;
    }

    private static string NormalizeSlug(string value)
    {
        return NormalizeToken(value).Replace(' ', '-');
    }

    private static string NormalizeToken(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string ResolveItemName(WordPressMigrationItem item)
    {
        if (!IsMembershipLikeType(item.WordPressType))
        {
            return item.Name;
        }

        foreach (string sourceAlias in CompanyNameSourceAliases)
        {
            if (!item.Values.TryGetValue(sourceAlias, out object? value))
            {
                continue;
            }

            string? candidate = value switch
            {
                null => null,
                string s => s.Trim(),
                _ => value.ToString()?.Trim()
            };

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return item.Name;
    }

    private static bool IsMembershipLikeType(string wordPressType)
    {
        string normalized = NormalizeToken(wordPressType);
        return normalized is "membership" or "memberships" or "member" or "members" or "directory" or "directories" or "sponsor" or "sponsors";
    }

    private static readonly string[] CompanyNameSourceAliases =
    [
        "Company Name",
        "company_name",
        "companyName",
        "title"
    ];
}