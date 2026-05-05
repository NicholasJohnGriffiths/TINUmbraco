using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Models;
using TINUmbraco.Web.Migrations;
using TINUmbraco.Web.Tools;
using System.Text;
using System.Text.RegularExpressions;

namespace TINUmbraco.Web.Controllers;

[Route("tools")]
public sealed class ToolsController(
    MigrationDashboardService migrationDashboardService,
    ToolsAccessService toolsAccessService,
    IContentTypeService contentTypeService,
    IContentService contentService,
    MigrationContentRootLookup rootLookup) : Controller
{
    [HttpGet("migration")]
    public IActionResult Migration()
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        return View("~/Views/Tools/Migration.cshtml", migrationDashboardService.GetSnapshot());
    }

    [HttpGet("migration/status")]
    public IActionResult Status()
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        return Json(migrationDashboardService.GetSnapshot());
    }

    [HttpPost("migration/run")]
    public IActionResult Run([FromBody] MigrationDashboardRunRequest? request)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        bool started = migrationDashboardService.TryStartRun(request, out string message);
        return Json(new { started, message, snapshot = migrationDashboardService.GetSnapshot() });
    }

    [HttpPost("migration/preflight")]
    public IActionResult Preflight([FromBody] MigrationDashboardRunRequest? request)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        MigrationDashboardService.MigrationPreflightResult result = migrationDashboardService.GetPreflight(request?.JsonPath);
        return Json(result);
    }

    [HttpGet("migration/content-types")]
    public IActionResult ContentTypes()
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        string[] aliases = contentTypeService
            .GetAll()
            .Select(x => x.Alias)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Json(new { count = aliases.Length, aliases });
    }

    [HttpGet("migration/content-type-details/{alias}")]
    public IActionResult ContentTypeDetails(string alias)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            return BadRequest(new { found = false, message = "Content type alias is required." });
        }

        IContentType? contentType = contentTypeService.Get(alias);
        if (contentType is null)
        {
            return NotFound(new { found = false, alias, message = "Content type not found." });
        }

        var properties = contentType.PropertyTypes
            .Select(x => new
            {
                alias = x.Alias,
                name = x.Name,
                editorAlias = x.PropertyEditorAlias
            })
            .OrderBy(x => x.alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Json(new
        {
            found = true,
            alias = contentType.Alias,
            name = contentType.Name,
            propertyCount = properties.Length,
            properties
        });
    }

    [HttpGet("migration/news-item/{slug}")]
    public IActionResult NewsItem(string slug)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            return BadRequest(new { found = false, message = "News slug is required." });
        }

        IContent newsRoot = rootLookup.GetRequiredSectionRoot(MigrationContentRootLookup.NewsPageAlias);
        IReadOnlyList<IContent> children = contentService
            .GetPagedChildren(newsRoot.Id, 0, int.MaxValue, out _, null, null, null, false)
            .ToList();

        string normalizedRequestedSlug = NormalizeSlug(slug);
        IContent? item = children.FirstOrDefault(x =>
        {
            if (!string.Equals(x.ContentType.Alias, "newsItemPage", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string urlName = NormalizeSlug(x.GetValue<string>("umbracoUrlName") ?? string.Empty);
            if (string.Equals(urlName, normalizedRequestedSlug, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string normalizedName = NormalizeSlug(x.Name ?? string.Empty);
            return string.Equals(normalizedName, normalizedRequestedSlug, StringComparison.OrdinalIgnoreCase);
        });

        if (item is null)
        {
            return NotFound(new { found = false, slug, message = "News item not found." });
        }

        string body = GetStringPropertyValue(item, "bodyContent");
        string image = GetStringPropertyValue(item, "image");

        return Json(new
        {
            found = true,
            id = item.Id,
            key = item.Key,
            name = item.Name,
            slug,
            bodyLength = body.Length,
            bodyPreview = body.Length > 200 ? body[..200] : body,
            hasImage = !string.IsNullOrWhiteSpace(image),
            imageValue = image
        });
    }

    [HttpGet("migration/news-items")]
    public IActionResult NewsItems([FromQuery] int take = 20, [FromQuery] int skip = 0)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        int max = Math.Clamp(take, 1, 200);
        int offset = Math.Max(skip, 0);
        IContent newsRoot = rootLookup.GetRequiredSectionRoot(MigrationContentRootLookup.NewsPageAlias);
        IReadOnlyList<IContent> children = contentService
            .GetPagedChildren(newsRoot.Id, 0, int.MaxValue, out _, null, null, null, false)
            .ToList();

        IReadOnlyList<IContent> newsItems = children
            .Where(x => string.Equals(x.ContentType.Alias, "newsItemPage", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.UpdateDate)
            .ToList();

        var items = newsItems
            .Skip(offset)
            .Take(max)
            .Select(x => new
            {
                id = x.Id,
                name = x.Name,
                urlName = x.GetValue<string>("umbracoUrlName"),
                normalizedUrlName = NormalizeSlug(x.GetValue<string>("umbracoUrlName") ?? string.Empty),
                normalizedName = NormalizeSlug(x.Name ?? string.Empty),
                cultures = x.AvailableCultures,
                bodyLength = GetStringPropertyValue(x, "bodyContent").Length,
                hasImage = !string.IsNullOrWhiteSpace(GetStringPropertyValue(x, "image"))
            })
            .ToArray();

        return Json(new
        {
            total = newsItems.Count,
            count = items.Length,
            skip = offset,
            items
        });
    }

    [HttpGet("migration/member-item/{slug}")]
    public IActionResult MemberItem(string slug)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            return BadRequest(new { found = false, message = "Member slug is required." });
        }

        IContent membersRoot = rootLookup.GetRequiredSectionRoot(MigrationContentRootLookup.MembershipsPageAlias);
        IReadOnlyList<IContent> children = contentService
            .GetPagedChildren(membersRoot.Id, 0, int.MaxValue, out _, null, null, null, false)
            .ToList();

        string normalizedRequestedSlug = NormalizeSlug(slug);
        IContent? item = children.FirstOrDefault(x =>
        {
            if (!string.Equals(x.ContentType.Alias, "memberPage", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string urlName = NormalizeSlug(x.GetValue<string>("umbracoUrlName") ?? string.Empty);
            if (string.Equals(urlName, normalizedRequestedSlug, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string normalizedName = NormalizeSlug(x.Name ?? string.Empty);
            return string.Equals(normalizedName, normalizedRequestedSlug, StringComparison.OrdinalIgnoreCase);
        });

        if (item is null)
        {
            return NotFound(new { found = false, slug, message = "Member item not found." });
        }

        string description = GetStringPropertyValue(item, "description");
        string websiteUrl = GetStringPropertyValue(item, "websiteUrl");
        string logo = GetStringPropertyValue(item, "logo");
        string companyType = GetStringPropertyValue(item, "companyType");

        return Json(new
        {
            found = true,
            id = item.Id,
            key = item.Key,
            name = item.Name,
            slug,
            descriptionLength = description.Length,
            descriptionPreview = description.Length > 200 ? description[..200] : description,
            websiteUrlValue = websiteUrl,
            logoValue = logo,
            companyType
        });
    }

    [HttpGet("migration/member-items")]
    public IActionResult MemberItems([FromQuery] int take = 20, [FromQuery] int skip = 0)
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        int max = Math.Clamp(take, 1, 200);
        int offset = Math.Max(skip, 0);
        IContent membersRoot = rootLookup.GetRequiredSectionRoot(MigrationContentRootLookup.MembershipsPageAlias);
        IReadOnlyList<IContent> children = contentService
            .GetPagedChildren(membersRoot.Id, 0, int.MaxValue, out _, null, null, null, false)
            .ToList();

        IReadOnlyList<IContent> memberItems = children
            .Where(x => string.Equals(x.ContentType.Alias, "memberPage", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.UpdateDate)
            .ToList();

        var items = memberItems
            .Skip(offset)
            .Take(max)
            .Select(x => new
            {
                id = x.Id,
                name = x.Name,
                urlName = x.GetValue<string>("umbracoUrlName"),
                normalizedUrlName = NormalizeSlug(x.GetValue<string>("umbracoUrlName") ?? string.Empty),
                normalizedName = NormalizeSlug(x.Name ?? string.Empty),
                companyType = GetStringPropertyValue(x, "companyType"),
                hasDescription = !string.IsNullOrWhiteSpace(GetStringPropertyValue(x, "description")),
                hasWebsiteUrl = !string.IsNullOrWhiteSpace(GetStringPropertyValue(x, "websiteUrl")),
                hasLogo = !string.IsNullOrWhiteSpace(GetStringPropertyValue(x, "logo"))
            })
            .ToArray();

        return Json(new
        {
            total = memberItems.Count,
            count = items.Length,
            skip = offset,
            items
        });
    }

    [HttpDelete("migration/delete-news")]
    public IActionResult DeleteNews()
    {
        if (!toolsAccessService.CanAccess(HttpContext))
        {
            return NotFound();
        }

        IContent newsRoot;
        try
        {
            newsRoot = rootLookup.GetRequiredSectionRoot(MigrationContentRootLookup.NewsPageAlias);
        }
        catch
        {
            return NotFound(new { message = "News section root not found." });
        }

        IReadOnlyList<IContent> children = contentService
            .GetPagedChildren(newsRoot.Id, 0, int.MaxValue, out long total, null, null, null, false)
            .ToList();

        int deleted = 0;
        foreach (IContent child in children)
        {
            contentService.Delete(child, -1);
            deleted++;
        }

        return Json(new { deleted, total });
    }

    private static string NormalizeSlug(string value)
    {
        string lowered = (value ?? string.Empty).Trim().ToLowerInvariant();
        string normalized = lowered.Normalize(NormalizationForm.FormKD);
        normalized = Regex.Replace(normalized, "[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    private static string GetStringPropertyValue(IContent content, string alias)
    {
        string invariant = ToStringValue(content.GetValue(alias));
        if (!string.IsNullOrWhiteSpace(invariant))
        {
            return invariant;
        }

        foreach (string culture in content.AvailableCultures)
        {
            string value = ToStringValue(content.GetValue(alias, culture));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ToStringValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string str => str,
            _ => value.ToString() ?? string.Empty
        };
    }
}