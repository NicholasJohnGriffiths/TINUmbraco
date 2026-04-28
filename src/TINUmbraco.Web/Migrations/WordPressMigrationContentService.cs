using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationContentService(
    IContentService contentService,
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

        MigrationContentRootLookup.MigrationSectionRoots roots = rootLookup.GetRequiredSectionRoots();
        string normalizedType = NormalizeToken(wordPressType);

        return normalizedType switch
        {
            "report" or "reports" => roots.Reports,
            "membership" or "memberships" => roots.Memberships,
            "directory" or "directories" or "sponsor" or "sponsors" => roots.Memberships,
            "reference" or "references" => roots.References,
            "event" or "events" => roots.Events,
            "news" or "post" or "posts" => roots.News,
            _ => throw new InvalidOperationException($"No migration section mapping exists for WordPress type '{wordPressType}'.")
        };
    }

    public IContent UpsertUnderWordPressSection(
        string wordPressType,
        string contentTypeAlias,
        string name,
        string? slug,
        Action<IContent>? mapValues = null,
        bool publish = true,
        int userId = -1)
    {
        IContent parent = GetParentForWordPressType(wordPressType);
        return UpsertChild(parent, contentTypeAlias, name, slug, mapValues, publish, userId);
    }

    public IContent Upsert(WordPressMigrationItem item, int userId = -1)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        return UpsertUnderWordPressSection(
            item.WordPressType,
            item.ContentTypeAlias,
            item.Name,
            item.Slug,
            mapValues: content => propertyMapper.ApplyValues(content, item.Values),
            publish: item.Publish,
            userId: userId);
    }

    public IContent UpsertChild(
        IContent parent,
        string contentTypeAlias,
        string name,
        string? slug,
        Action<IContent>? mapValues = null,
        bool publish = true,
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

        IContent content = existing
            ?? contentService.Create(name, parent.Id, contentTypeAlias, userId);

        content.Name = name.Trim();

        if (HasProperty(content, UrlNamePropertyAlias))
        {
            content.SetValue(UrlNamePropertyAlias, normalizedSlug);
        }

        mapValues?.Invoke(content);

        contentService.Save([content], userId);

        if (publish)
        {
            contentService.Publish(content, Array.Empty<string>(), userId);
        }

        return content;
    }

    private IContent? FindExistingChildBySlug(int parentId, string contentTypeAlias, string normalizedSlug)
    {
        const int pageSize = 200;
        long pageIndex = 0;

        while (true)
        {
            IReadOnlyList<IContent> page = contentService
                .GetPagedChildren(parentId, pageIndex, pageSize, out _, [UrlNamePropertyAlias], null, null, false)
                .ToList();

            if (page.Count == 0)
            {
                return null;
            }

            IContent? match = page.FirstOrDefault(child =>
                string.Equals(child.ContentType.Alias, contentTypeAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeSlug(GetUrlName(child)), normalizedSlug, StringComparison.OrdinalIgnoreCase));

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
}