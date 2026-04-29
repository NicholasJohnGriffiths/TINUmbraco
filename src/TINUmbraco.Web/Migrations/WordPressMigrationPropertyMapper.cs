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
    private static readonly Dictionary<string, string[]> FallbackAliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
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

        ["featuredImage"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["featured_image"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["featuredMedia"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["heroImage"] = ["heroImage", "featuredImage", "coverImage", "thumbnail"],
        ["hero_image"] = ["heroImage", "featuredImage", "coverImage", "thumbnail"],
        ["coverImage"] = ["coverImage", "featuredImage", "heroImage", "thumbnail"],
        ["cover_image"] = ["coverImage", "featuredImage", "heroImage", "thumbnail"],
        ["thumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage"],
        ["post_thumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage"],
        ["image"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["imageUrl"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["image_url"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"]
    };

    private static readonly Dictionary<string, string[]> MediaSourceAliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["featuredimage"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["featuredmedia"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["featuredimagelarge"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["heroimage"] = ["heroImage", "featuredImage", "coverImage", "thumbnail"],
        ["bannerimage"] = ["heroImage", "featuredImage", "coverImage", "thumbnail"],
        ["coverimage"] = ["coverImage", "featuredImage", "heroImage", "thumbnail"],
        ["thumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage"],
        ["postthumbnail"] = ["thumbnail", "coverImage", "featuredImage", "heroImage"],
        ["image"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["imageurl"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["primaryimage"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["mainimage"] = ["featuredImage", "heroImage", "coverImage", "thumbnail"],
        ["headerimage"] = ["heroImage", "featuredImage", "coverImage", "thumbnail"],
        ["reportcover"] = ["coverImage", "featuredImage", "heroImage", "thumbnail"]
    };

    public void ApplyValues(IContent content, IReadOnlyDictionary<string, object?> values, string wordPressType, bool dryRun)
    {
        Dictionary<string, string> contentAliases = content.Properties
            .ToDictionary(x => x.Alias, x => x.Alias, StringComparer.OrdinalIgnoreCase);

        foreach ((string alias, object? value) in values)
        {
            string[] targetAliases = GetCandidateAliases(alias);

            foreach (string targetAlias in targetAliases)
            {
                if (!contentAliases.TryGetValue(targetAlias, out string? actualAlias)
                    || string.IsNullOrWhiteSpace(actualAlias))
                {
                    continue;
                }

                if (mediaService.TryMapMediaPickerValue(content, actualAlias, value, wordPressType, dryRun, out object? mappedMediaValue))
                {
                    content.SetValue(actualAlias, mappedMediaValue);
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
                    break;
                }

                content.SetValue(actualAlias, NormalizeValue(value));
                break;
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