using System.Net.Http;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;
using static Umbraco.Cms.Core.Constants;

namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationMediaService(
    IMediaService mediaService,
    MediaFileManager mediaFileManager,
    MediaUrlGeneratorCollection mediaUrlGenerators,
    IShortStringHelper shortStringHelper,
    IContentTypeBaseServiceProvider contentTypeBaseServiceProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<WordPressMigrationMediaService> logger)
{
    private const string MediaRootName = "Media";
    private static readonly string[] KnownFolderNames = ["Reports", "News", "Events", "Members", "References"];

    public IReadOnlyList<string> GetKnownFolderNames()
    {
        return KnownFolderNames;
    }

    public bool MediaRootExists()
    {
        var rootMedia = mediaService.GetRootMedia();
        
        // Check if "Media" folder exists
        if (rootMedia.Any(x => string.Equals(x.Name, MediaRootName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        
        // Check if any known folders exist directly at root
        return rootMedia.Any(x => 
            string.Equals(x.ContentType.Alias, Conventions.MediaTypes.Folder, StringComparison.OrdinalIgnoreCase)
            && KnownFolderNames.Any(kf => string.Equals(x.Name, kf, StringComparison.OrdinalIgnoreCase)));
    }

    public bool FolderExists(string sectionFolderName)
    {
        var rootMedia = mediaService.GetRootMedia();
        
        // Check inside "Media" folder if it exists
        IMedia? mediaRoot = rootMedia
            .FirstOrDefault(x => string.Equals(x.Name, MediaRootName, StringComparison.OrdinalIgnoreCase));
        
        if (mediaRoot is not null && FindChildFolderByName(mediaRoot.Id, sectionFolderName) is not null)
        {
            return true;
        }
        
        // Check directly at media root level
        return rootMedia.Any(x => 
            string.Equals(x.ContentType.Alias, Conventions.MediaTypes.Folder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, sectionFolderName, StringComparison.OrdinalIgnoreCase));
    }

    public string GetFolderNameForWordPressType(string wordPressType)
    {
        return MapFolderNameForWordPressType(wordPressType);
    }

    public bool TryMapMediaPickerValue(
        IContent content,
        string propertyAlias,
        object? rawValue,
        string wordPressType,
        bool dryRun,
        out object? mappedValue)
    {
        mappedValue = rawValue;

        IProperty? property = content.Properties
            .FirstOrDefault(x => string.Equals(x.Alias, propertyAlias, StringComparison.OrdinalIgnoreCase));

        if (property is null)
        {
            return false;
        }

        if (!string.Equals(property.PropertyType.PropertyEditorAlias, "Umbraco.MediaPicker3", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? imageUrl = ExtractUrl(rawValue);
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri)
            || (imageUri.Scheme != Uri.UriSchemeHttp && imageUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        string folderName = MapFolderNameForWordPressType(wordPressType);
        IMedia targetFolder = GetOrCreateFolder(folderName);

        if (dryRun)
        {
            mappedValue = rawValue;
            return true;
        }

        IMedia mediaItem = ImportImage(imageUri, targetFolder.Id);
        mappedValue = $"umb://media/{mediaItem.Key:N}";

        return true;
    }

    private IMedia ImportImage(Uri imageUri, int parentFolderId)
    {
        string fileName = GetFileName(imageUri);

        IMedia? existing = FindExistingMediaByName(parentFolderId, fileName);
        if (existing is not null)
        {
            return existing;
        }

        HttpClient httpClient = httpClientFactory.CreateClient(nameof(WordPressMigrationMediaService));
        using Stream stream = httpClient.GetStreamAsync(imageUri).GetAwaiter().GetResult();

        IMedia mediaItem = mediaService.CreateMedia(fileName, parentFolderId, Conventions.MediaTypes.Image);
        mediaItem.SetValue(
            mediaFileManager,
            mediaUrlGenerators,
            shortStringHelper,
            contentTypeBaseServiceProvider,
            Conventions.Media.File,
            fileName,
            stream);

        mediaService.Save(mediaItem);

        logger.LogInformation("Imported media {FileName} into folder id {FolderId} from {Url}", fileName, parentFolderId, imageUri);
        return mediaItem;
    }

    private IMedia GetOrCreateFolder(string sectionFolderName)
    {
        IReadOnlyList<IMedia> rootMedia = mediaService.GetRootMedia().ToList();

        // Prefer existing top-level section folders (Reports/News/Events/Members/References)
        IMedia? existingRootSectionFolder = rootMedia.FirstOrDefault(x =>
            string.Equals(x.ContentType.Alias, Conventions.MediaTypes.Folder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, sectionFolderName, StringComparison.OrdinalIgnoreCase));

        if (existingRootSectionFolder is not null)
        {
            return existingRootSectionFolder;
        }

        // Fall back to section folders under a "Media" container if one exists
        IMedia? mediaRoot = rootMedia.FirstOrDefault(x =>
            string.Equals(x.Name, MediaRootName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ContentType.Alias, Conventions.MediaTypes.Folder, StringComparison.OrdinalIgnoreCase));

        if (mediaRoot is not null)
        {
            IMedia? existingSectionFolder = FindChildFolderByName(mediaRoot.Id, sectionFolderName);
            if (existingSectionFolder is not null)
            {
                return existingSectionFolder;
            }

            IMedia createdUnderMediaRoot = mediaService.CreateMedia(sectionFolderName, mediaRoot.Id, Conventions.MediaTypes.Folder);
            mediaService.Save(createdUnderMediaRoot);
            return createdUnderMediaRoot;
        }

        // If no matching folder exists, create at root (no implicit "Media" container)
        IMedia createdAtRoot = mediaService.CreateMedia(sectionFolderName, Umbraco.Cms.Core.Constants.System.Root, Conventions.MediaTypes.Folder);
        mediaService.Save(createdAtRoot);
        return createdAtRoot;
    }

    private IMedia? FindChildFolderByName(int parentId, string folderName)
    {
        return mediaService
            .GetPagedChildren(parentId, 0, int.MaxValue, out _, null)
            .FirstOrDefault(x =>
                string.Equals(x.ContentType.Alias, Conventions.MediaTypes.Folder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Name, folderName, StringComparison.OrdinalIgnoreCase));
    }

    private IMedia? FindExistingMediaByName(int parentId, string fileName)
    {
        return mediaService
            .GetPagedChildren(parentId, 0, int.MaxValue, out _, null)
            .FirstOrDefault(x =>
                string.Equals(x.ContentType.Alias, Conventions.MediaTypes.Image, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string MapFolderNameForWordPressType(string wordPressType)
    {
        return NormalizeToken(wordPressType) switch
        {
            "report" or "reports" => "Reports",
            "news" or "post" or "posts" => "News",
            "event" or "events" => "Events",
            "membership" or "memberships" or "member" or "members" or "directory" or "directories" or "sponsor" or "sponsors" => "Members",
            "reference" or "references" => "References",
            _ => "References"
        };
    }

    private static string? ExtractUrl(object? rawValue)
    {
        if (rawValue is null)
        {
            return null;
        }

        if (rawValue is string str)
        {
            return str.Trim();
        }

        if (rawValue is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return element.GetString()?.Trim();
        }

        return null;
    }

    private static string GetFileName(Uri imageUri)
    {
        string fileName = Path.GetFileName(imageUri.LocalPath).Trim();
        if (!string.IsNullOrWhiteSpace(fileName) && Path.HasExtension(fileName))
        {
            return fileName;
        }

        string derivedStem = GetBestFileStem(imageUri);
        return $"{derivedStem}.jpg";
    }

    private static string GetBestFileStem(Uri imageUri)
    {
        string[] segments = imageUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = segments.Length - 1; i >= 0; i--)
        {
            string candidate = Uri.UnescapeDataString(segments[i]).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (string.Equals(candidate, "seed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.All(char.IsDigit))
            {
                continue;
            }

            if (Path.HasExtension(candidate))
            {
                candidate = Path.GetFileNameWithoutExtension(candidate);
            }

            string sanitized = SanitizeFileToken(candidate);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                return sanitized;
            }
        }

        return $"imported-{Guid.NewGuid():N}";
    }

    private static string SanitizeFileToken(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[value.Length];
        int written = 0;

        foreach (char ch in value)
        {
            if (invalidChars.Contains(ch))
            {
                buffer[written++] = '-';
                continue;
            }

            buffer[written++] = ch;
        }

        string sanitized = new string(buffer[..written]).Trim('-', '.', ' ');
        if (sanitized.Length > 120)
        {
            sanitized = sanitized[..120];
        }

        return sanitized;
    }

    private static string NormalizeToken(string value)
    {
        return value.Trim().ToLowerInvariant();
    }
}