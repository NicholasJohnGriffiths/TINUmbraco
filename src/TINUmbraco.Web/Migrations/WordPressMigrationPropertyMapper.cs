using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.PropertyEditors.ValueConverters;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Serialization;

namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationPropertyMapper(
    WordPressMigrationMediaService mediaService,
    ILogger<WordPressMigrationPropertyMapper> logger,
    IJsonSerializer jsonSerializer) : IWordPressMigrationPropertyMapper
{
    private static readonly HashSet<string> BodySourceTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "bodytext", "bodycontent", "content", "description", "excerpt", "summary"
    };

    private static readonly Dictionary<string, string[]> FallbackAliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Company Name"] = ["companyName", "title", "pageTitle", "pageHeading"],
        ["company_name"] = ["companyName", "title", "pageTitle", "pageHeading"],
        ["companyName"] = ["companyName", "title", "pageTitle", "pageHeading"],

        ["About"] = ["description", "bodyContent", "mainContent", "heroText", "summary", "excerpt"],
        ["about"] = ["description", "bodyContent", "mainContent", "heroText", "summary", "excerpt"],

        ["Website"] = ["websiteUrl", "website", "link", "url"],
        ["website"] = ["websiteUrl", "website", "link", "url"],
        ["website_url"] = ["websiteUrl", "website", "link", "url"],

        ["Company Logo - Full Color"] = ["logo", "featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["company_logo_-_full_color"] = ["logo", "featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["companyLogoFullColor"] = ["logo", "featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["companyLogo"] = ["logo", "featuredImage", "heroImage", "coverImage", "thumbnail", "image"],

        ["Company Type"] = ["companyType", "category", "type"],
        ["company_type"] = ["companyType", "category", "type"],
        ["companyType"] = ["companyType", "category", "type"],

        ["title"] = ["pageTitle", "pageHeading", "title"],
        ["heading"] = ["pageHeading", "pageTitle", "title"],
        ["summary"] = ["heroText", "summary", "excerpt"],
        ["excerpt"] = ["heroText", "excerpt", "summary"],
        ["body"] = ["bodyContent", "mainContent", "bodyText", "content"],
        ["bodyText"] = ["bodyContent", "mainContent", "bodyText", "content"],
        ["bodyContent"] = ["bodyContent", "mainContent", "bodyText", "content"],
        ["content"] = ["bodyContent", "mainContent", "content", "bodyText"],
        ["description"] = ["bodyContent", "mainContent", "description", "heroText"],
        ["seoTitle"] = ["seoTitle", "metaTitle"],
        ["metaTitle"] = ["seoTitle", "metaTitle"],
        ["seo_title"] = ["seoTitle", "metaTitle"],
        ["yoastTitle"] = ["seoTitle", "metaTitle"],
        ["yoast_title"] = ["seoTitle", "metaTitle"],
        ["ogTitle"] = ["seoTitle", "metaTitle"],
        ["og_title"] = ["seoTitle", "metaTitle"],

        ["seoDescription"] = ["seoDescription", "metaDescription"],
        ["metaDescription"] = ["seoDescription", "metaDescription"],
        ["seo_description"] = ["seoDescription", "metaDescription"],
        ["meta_description"] = ["seoDescription", "metaDescription"],
        ["yoastDescription"] = ["seoDescription", "metaDescription"],
        ["yoast_description"] = ["seoDescription", "metaDescription"],
        ["ogDescription"] = ["seoDescription", "metaDescription"],
        ["og_description"] = ["seoDescription", "metaDescription"],

        ["publishDate"] = ["publishDate", "eventDate", "startDate", "date"],
        ["publishedAtUtc"] = ["publishDate", "publishDateTime", "publishedAt", "publishedOn", "date"],
        ["publishedAt"] = ["publishDate", "publishDateTime", "publishedAt", "publishedOn", "date"],
        ["date"] = ["publishDate", "publishDateTime", "eventDate", "startDate", "date"],
        ["date_gmt"] = ["publishDate", "publishDateTime", "publishedAt", "date"],
        ["modifiedAtUtc"] = ["lastUpdated", "updatedDate", "modifiedDate", "lastModified"],
        ["modified"] = ["lastUpdated", "updatedDate", "modifiedDate", "lastModified"],
        ["modified_gmt"] = ["lastUpdated", "updatedDate", "modifiedDate", "lastModified"],

        ["featuredImage"] = ["featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["featured_image"] = ["featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["featuredMedia"] = ["featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["heroImage"] = ["heroImage", "featuredImage", "coverImage", "thumbnail", "image"],
        ["hero_image"] = ["heroImage", "featuredImage", "coverImage", "thumbnail", "image"],
        ["coverImage"] = ["coverImage", "featuredImage", "heroImage", "thumbnail", "image"],
        ["cover_image"] = ["coverImage", "featuredImage", "heroImage", "thumbnail", "image"],
        ["thumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage", "image"],
        ["post_thumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage", "image"],
        ["image"] = ["image", "featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["imageUrl"] = ["image", "featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["image_url"] = ["image", "featuredImage", "heroImage", "coverImage", "thumbnail"]
    };

    private static readonly Dictionary<string, string[]> MediaSourceAliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["featuredimage"] = ["featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["featuredmedia"] = ["featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["featuredimagelarge"] = ["featuredImage", "heroImage", "coverImage", "thumbnail", "image"],
        ["heroimage"] = ["heroImage", "featuredImage", "coverImage", "thumbnail", "image"],
        ["bannerimage"] = ["heroImage", "featuredImage", "coverImage", "thumbnail", "image"],
        ["coverimage"] = ["coverImage", "featuredImage", "heroImage", "thumbnail", "image"],
        ["thumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage", "image"],
        ["postthumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage", "image"],
        ["image"] = ["image", "featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["imageurl"] = ["image", "featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["primaryimage"] = ["image", "featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["mainimage"] = ["image", "featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["headerimage"] = ["heroImage", "image", "featuredImage", "coverImage", "thumbnail"],
        ["reportcover"] = ["coverImage", "image", "featuredImage", "heroImage", "thumbnail"]
    };

    public void ApplyValues(IContent content, IReadOnlyDictionary<string, object?> values, string wordPressType, bool dryRun)
    {
        Dictionary<string, string> contentAliases = content.Properties
            .ToDictionary(x => x.Alias, x => x.Alias, StringComparer.OrdinalIgnoreCase);

        foreach ((string alias, object? value) in values)
        {
            string[] targetAliases = GetCandidateAliases(alias);
            bool applied = false;

            foreach (string targetAlias in targetAliases)
            {
                if (!contentAliases.TryGetValue(targetAlias, out string? actualAlias)
                    || string.IsNullOrWhiteSpace(actualAlias))
                {
                    continue;
                }

                IProperty? property = content.Properties
                    .FirstOrDefault(x => string.Equals(x.Alias, actualAlias, StringComparison.OrdinalIgnoreCase));
                if (property is null)
                {
                    continue;
                }

                if (mediaService.TryMapMediaPickerValue(content, actualAlias, value, wordPressType, dryRun, out object? mappedMediaValue))
                {
                    content.SetValue(actualAlias, mappedMediaValue);
                    applied = true;
                    break;
                }

                if (IsDateField(targetAlias))
                {
                    string? converted = TryConvertDateValue(value, jsonSerializer);
                    if (converted is not null)
                    {
                        logger.LogInformation(
                            "Mapped date field {SourceAlias} -> {ActualAlias}: {Date}.",
                            alias,
                            actualAlias,
                            converted);
                        content.SetValue(actualAlias, converted);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Skipped date field {SourceAlias} -> {ActualAlias}: could not parse value.",
                            alias,
                            actualAlias);
                    }

                    applied = true;
                    break;
                }

                if (TryMapMultiUrlPickerValue(property, value, out object? mappedLinkValue))
                {
                    content.SetValue(actualAlias, mappedLinkValue);
                    applied = true;
                    break;
                }

                content.SetValue(actualAlias, NormalizeValue(value));
                applied = true;
                break;
            }

            if (!applied)
            {
                TryApplyHeuristicFallback(content, alias, value, wordPressType, dryRun);
            }
        }
    }

    private void TryApplyHeuristicFallback(IContent content, string sourceAlias, object? value, string wordPressType, bool dryRun)
    {
        string normalizedSourceAlias = NormalizeAliasToken(sourceAlias);

        // If the source field looks image-like, map to the first image/media property by editor type.
        if (MediaSourceAliasMap.ContainsKey(normalizedSourceAlias))
        {
            IProperty? mediaProperty = content.Properties.FirstOrDefault(x =>
                string.Equals(x.PropertyType.PropertyEditorAlias, "Umbraco.MediaPicker3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.PropertyType.PropertyEditorAlias, "Umbraco.ImageCropper", StringComparison.OrdinalIgnoreCase));

            if (mediaProperty is not null
                && mediaService.TryMapMediaPickerValue(content, mediaProperty.Alias, value, wordPressType, dryRun, out object? mappedMediaValue))
            {
                content.SetValue(mediaProperty.Alias, mappedMediaValue);
                return;
            }
        }

        // If the source field looks body-like, map to the first likely rich text content property.
        if (BodySourceTokens.Contains(normalizedSourceAlias))
        {
            IProperty? richTextProperty = content.Properties
                .OrderByDescending(x => x.Alias.Contains("body", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Alias.Contains("content", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(x =>
                    x.PropertyType.PropertyEditorAlias.Contains("RichText", StringComparison.OrdinalIgnoreCase)
                    || x.PropertyType.PropertyEditorAlias.Contains("TinyMCE", StringComparison.OrdinalIgnoreCase));

            if (richTextProperty is not null)
            {
                content.SetValue(richTextProperty.Alias, NormalizeValue(value));
            }
        }
    }

    private static string[] GetCandidateAliases(string sourceAlias)
    {
        string normalizedAlias = NormalizeAliasToken(sourceAlias);

        if (MediaSourceAliasMap.TryGetValue(normalizedAlias, out string[]? mediaAliases))
        {
            return [sourceAlias, .. (mediaAliases ?? Array.Empty<string>())];
        }

        if (FallbackAliasMap.TryGetValue(sourceAlias, out string[]? mappedAliases))
        {
            return [sourceAlias, .. (mappedAliases ?? Array.Empty<string>())];
        }

        return [sourceAlias];
    }

    private static string NormalizeAliasToken(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return string.Empty;
        }

        return new string(alias.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static object? NormalizeValue(object? value, string targetAlias = "")
    {
        if (value is null)
        {
            return null;
        }

        if (value is System.Text.Json.JsonElement element)
        {
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out long intValue)
                    ? intValue
                    : element.TryGetDouble(out double doubleValue) ? doubleValue : element.GetRawText(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }

        return value;
    }

    private static bool IsDateField(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        return alias switch
        {
            _ when alias.Equals("publishDate", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("publishDateTime", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("publishedAtUtc", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("publishedAt", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("publishedOn", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("date_gmt", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("eventDate", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("startDate", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("endDate", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("date", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("modifiedAtUtc", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("lastUpdated", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("updatedDate", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("modifiedDate", StringComparison.OrdinalIgnoreCase) => true,
            _ when alias.Equals("lastModified", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    private static bool TryMapMultiUrlPickerValue(IProperty property, object? rawValue, out object? mappedValue)
    {
        mappedValue = rawValue;

        if (!string.Equals(property.PropertyType.PropertyEditorAlias, "Umbraco.MultiUrlPicker", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? url = rawValue switch
        {
            null => null,
            string s => s.Trim(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString()?.Trim(),
            _ => rawValue.ToString()?.Trim()
        };

        if (string.IsNullOrWhiteSpace(url))
        {
            mappedValue = "[]";
            return true;
        }

        // If a JSON payload was already supplied, keep it as-is.
        if (url.StartsWith("[", StringComparison.Ordinal) || url.StartsWith("{", StringComparison.Ordinal))
        {
            mappedValue = url;
            return true;
        }

        mappedValue = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                name = string.Empty,
                target = string.Empty,
                url,
                udi = (string?)null
            }
        });

        return true;
    }

    private static string? TryConvertDateValue(object? value, IJsonSerializer jsonSerializer)
    {
        if (value is null)
        {
            return null;
        }

        DateTimeOffset? normalizedDate = null;

        if (value is DateTime dt)
        {
            normalizedDate = new DateTimeOffset(DateTime.SpecifyKind(dt.Date, DateTimeKind.Utc));
        }
        else if (value is DateTimeOffset dto)
        {
            normalizedDate = new DateTimeOffset(dto.UtcDateTime.Date, TimeSpan.Zero);
        }
        else if (value is DateOnly dateOnly)
        {
            normalizedDate = new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        }

        string? stringValue = null;
        if (normalizedDate is null && value is System.Text.Json.JsonElement element)
        {
            stringValue = element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : null;
        }
        else if (normalizedDate is null && value is string str)
        {
            stringValue = str;
        }

        if (normalizedDate is null)
        {
            if (string.IsNullOrWhiteSpace(stringValue))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(
                    stringValue,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsedOffset))
            {
                normalizedDate = new DateTimeOffset(parsedOffset.UtcDateTime.Date, TimeSpan.Zero);
            }
        }

        if (normalizedDate is null)
        {
            return null;
        }

        var dateDto = new DateTimeValueConverterBase.DateTimeDto
        {
            Date = normalizedDate.Value,
            TimeZone = null
        };

        return jsonSerializer.Serialize(dateDto);
    }
}