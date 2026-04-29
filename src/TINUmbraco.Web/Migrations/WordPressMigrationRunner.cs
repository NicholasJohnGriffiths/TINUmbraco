using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Umbraco.Cms.Core.Services;

namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationRunner(
    WordPressMigrationContentService migrationContentService,
    IContentTypeService contentTypeService,
    IHostEnvironment hostEnvironment,
    IOptions<WordPressMigrationOptions> options,
    ILogger<WordPressMigrationRunner> logger)
{
    private readonly int _checkpointInterval = Math.Max(options.Value.ProgressInterval, 1);
    private readonly bool _logItemStarts = options.Value.LogItemStarts;
    private readonly bool _updateOnlyExisting = options.Value.UpdateOnlyExisting;
    private readonly bool _failOnMissingContentTypeAliases = options.Value.FailOnMissingContentTypeAliases;
    private readonly HashSet<string> _skipTypes = new(
        (options.Value.SkipTypes ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeToken),
        StringComparer.OrdinalIgnoreCase);

    public async Task<MigrationRunResult> RunFromJsonFileAsync(
        string jsonPath,
        int userId = -1,
        IProgress<MigrationProgress>? progress = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            throw new ArgumentException("JSON path cannot be empty.", nameof(jsonPath));
        }

        string resolvedPath = Path.IsPathRooted(jsonPath)
            ? jsonPath
            : Path.Combine(hostEnvironment.ContentRootPath, jsonPath);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"WordPress migration JSON file was not found: {resolvedPath}", resolvedPath);
        }

        await using FileStream stream = File.OpenRead(resolvedPath);
        List<WordPressMigrationItem>? items = await JsonSerializer.DeserializeAsync<List<WordPressMigrationItem>>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        if (items is null || items.Count == 0)
        {
            logger.LogWarning("WordPress migration file {Path} contained no items.", resolvedPath);
            return new MigrationRunResult(0, 0, 0, 0, []);
        }

        logger.LogInformation("Starting WordPress migration for {Count} items from {Path}.", items.Count, resolvedPath);
        return Run(items, userId, progress, dryRun);
    }

    public MigrationRunResult Run(
        IEnumerable<WordPressMigrationItem> items,
        int userId = -1,
        IProgress<MigrationProgress>? progress = null,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<WordPressMigrationItem> itemList = items as List<WordPressMigrationItem> ?? items.ToList();
        List<string> errors = [];
        int total = itemList.Count;
        int succeeded = 0;
        int current = 0;
        int skipped = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (total == 0)
        {
            logger.LogInformation("WordPress migration completed. No items were provided.");
            return new MigrationRunResult(0, 0, 0, 0, []);
        }

        HashSet<string> missingAliases = GetMissingContentTypeAliases(itemList, contentTypeService);

        if (missingAliases.Count > 0)
        {
            string missingAliasList = string.Join(", ", missingAliases.OrderBy(x => x));

            if (_failOnMissingContentTypeAliases)
            {
                string message =
                    $"Migration preflight failed. Missing Umbraco content type aliases: {missingAliasList}";

                logger.LogError("{Message}", message);
                throw new InvalidOperationException(message);
            }

            logger.LogWarning(
                "Migration preflight found missing Umbraco content type aliases: {MissingAliases}. Items using these aliases will be skipped because Migration.WordPress.FailOnMissingContentTypeAliases is false.",
                missingAliasList);
        }

        foreach (WordPressMigrationItem item in itemList)
        {
            current++;

            MigrationProgress startProgress = new(
                Current: current,
                Total: total,
                Succeeded: succeeded,
                Failed: errors.Count,
                Skipped: skipped,
                ItemName: item.Name,
                ItemSlug: item.Slug,
                WordPressType: item.WordPressType,
                ContentTypeAlias: item.ContentTypeAlias,
                Status: MigrationItemStatus.Started,
                IsDryRun: dryRun,
                ErrorMessage: null);

            if (_skipTypes.Contains(NormalizeToken(item.WordPressType)))
            {
                skipped++;

                MigrationProgress skippedProgress = startProgress with
                {
                    Skipped = skipped,
                    Status = MigrationItemStatus.Skipped
                };

                progress?.Report(skippedProgress);
                logger.LogWarning(
                    "[{Current}/{Total}] ({Percent}%) Skipped '{ItemName}' because type '{WordPressType}' is configured in Migration.WordPress.SkipTypes.",
                    skippedProgress.Current,
                    skippedProgress.Total,
                    skippedProgress.Percentage,
                    skippedProgress.ItemName,
                    skippedProgress.WordPressType);

                continue;
            }

            if (missingAliases.Contains(item.ContentTypeAlias))
            {
                skipped++;

                MigrationProgress skippedAliasProgress = startProgress with
                {
                    Skipped = skipped,
                    Status = MigrationItemStatus.Skipped,
                    ErrorMessage =
                        $"Skipped because target content type alias '{item.ContentTypeAlias}' does not exist in Umbraco."
                };

                progress?.Report(skippedAliasProgress);
                logger.LogWarning(
                    "[{Current}/{Total}] ({Percent}%) Skipped '{ItemName}' because content type alias '{ContentTypeAlias}' does not exist.",
                    skippedAliasProgress.Current,
                    skippedAliasProgress.Total,
                    skippedAliasProgress.Percentage,
                    skippedAliasProgress.ItemName,
                    skippedAliasProgress.ContentTypeAlias);

                continue;
            }

            progress?.Report(startProgress);
            if (_logItemStarts)
            {
                logger.LogInformation(
                    "[{Current}/{Total}] ({Percent}%) Starting '{ItemName}' [{WordPressType}/{ContentTypeAlias}]",
                    startProgress.Current,
                    startProgress.Total,
                    startProgress.Percentage,
                    startProgress.ItemName,
                    startProgress.WordPressType,
                    startProgress.ContentTypeAlias);
            }

            try
            {
                migrationContentService.Upsert(item, userId, allowCreate: !_updateOnlyExisting, dryRun: dryRun);
                succeeded++;

                MigrationProgress successProgress = new(
                    Current: current,
                    Total: total,
                    Succeeded: succeeded,
                    Failed: errors.Count,
                    Skipped: skipped,
                    ItemName: item.Name,
                    ItemSlug: item.Slug,
                    WordPressType: item.WordPressType,
                    ContentTypeAlias: item.ContentTypeAlias,
                    Status: MigrationItemStatus.Succeeded,
                    IsDryRun: dryRun,
                    ErrorMessage: null);

                progress?.Report(successProgress);
                logger.LogInformation(
                    "[{Current}/{Total}] ({Percent}%) Succeeded '{ItemName}'",
                    successProgress.Current,
                    successProgress.Total,
                    successProgress.Percentage,
                    successProgress.ItemName);
            }
            catch (WordPressMigrationSkipItemException ex)
            {
                skipped++;

                MigrationProgress skippedProgress = new(
                    Current: current,
                    Total: total,
                    Succeeded: succeeded,
                    Failed: errors.Count,
                    Skipped: skipped,
                    ItemName: item.Name,
                    ItemSlug: item.Slug,
                    WordPressType: item.WordPressType,
                    ContentTypeAlias: item.ContentTypeAlias,
                    Status: MigrationItemStatus.Skipped,
                    IsDryRun: dryRun,
                    ErrorMessage: ex.Message);

                progress?.Report(skippedProgress);
                logger.LogInformation(
                    "[{Current}/{Total}] ({Percent}%) Skipped '{ItemName}': {Reason}",
                    skippedProgress.Current,
                    skippedProgress.Total,
                    skippedProgress.Percentage,
                    skippedProgress.ItemName,
                    ex.Message);
            }
            catch (Exception ex)
            {
                string identifier = string.IsNullOrWhiteSpace(item.Slug) ? item.Name : item.Slug;
                string message = $"Failed item '{identifier}' ({item.WordPressType}/{item.ContentTypeAlias}): {ex.Message}";
                errors.Add(message);
                logger.LogError(ex, "{Message}", message);

                MigrationProgress failureProgress = new(
                    Current: current,
                    Total: total,
                    Succeeded: succeeded,
                    Failed: errors.Count,
                    Skipped: skipped,
                    ItemName: item.Name,
                    ItemSlug: item.Slug,
                    WordPressType: item.WordPressType,
                    ContentTypeAlias: item.ContentTypeAlias,
                    Status: MigrationItemStatus.Failed,
                    IsDryRun: dryRun,
                    ErrorMessage: message);

                progress?.Report(failureProgress);
            }

            if (current % _checkpointInterval == 0 || current == total)
            {
                double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 1);
                double itemsPerSecond = current / elapsedSeconds;
                int remaining = total - current;
                TimeSpan eta = itemsPerSecond <= 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(remaining / itemsPerSecond);

                logger.LogInformation(
                    "Checkpoint {Current}/{Total} ({Percent}%): Succeeded {Succeeded}, Failed {Failed}, Skipped {Skipped}, Rate {Rate:F2} items/s, ETA {Eta}",
                    current,
                    total,
                    (int)Math.Round((double)current * 100 / total),
                    succeeded,
                    errors.Count,
                    skipped,
                    itemsPerSecond,
                    eta.ToString(@"hh\:mm\:ss"));
            }
        }

        int failed = total - succeeded - skipped;

        logger.LogInformation(
            "WordPress migration completed. Total: {Total}, Succeeded: {Succeeded}, Failed: {Failed}, Skipped: {Skipped}.",
            total,
            succeeded,
            failed,
            skipped);

        return new MigrationRunResult(total, succeeded, failed, skipped, errors);
    }

    public sealed record MigrationRunResult(
        int Total,
        int Succeeded,
        int Failed,
        int Skipped,
        IReadOnlyList<string> Errors);

    public sealed record MigrationProgress(
        int Current,
        int Total,
        int Succeeded,
        int Failed,
        int Skipped,
        string ItemName,
        string? ItemSlug,
        string WordPressType,
        string ContentTypeAlias,
        MigrationItemStatus Status,
        bool IsDryRun,
        string? ErrorMessage)
    {
        public int Percentage => Total == 0 ? 0 : (int)Math.Round((double)Current * 100 / Total);
    }

    public enum MigrationItemStatus
    {
        Started,
        Succeeded,
        Failed,
        Skipped
    }

    private static string NormalizeToken(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private HashSet<string> GetMissingContentTypeAliases(
        IReadOnlyCollection<WordPressMigrationItem> items,
        IContentTypeService contentTypeService)
    {
        string[] aliasesToValidate = items
            .Where(x => !_skipTypes.Contains(NormalizeToken(x.WordPressType)))
            .Select(x => x.ContentTypeAlias)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (aliasesToValidate.Length == 0)
        {
            return [];
        }

        int[] existingIds = contentTypeService.GetAllContentTypeIds(aliasesToValidate).ToArray();

        if (existingIds.Length == aliasesToValidate.Length)
        {
            return [];
        }

        HashSet<string> missingAliases = aliasesToValidate
            .Where(alias => !contentTypeService.GetAllContentTypeIds([alias]).Any())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return missingAliases;
    }
}