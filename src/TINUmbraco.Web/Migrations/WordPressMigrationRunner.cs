using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationRunner(
    WordPressMigrationContentService migrationContentService,
    IHostEnvironment hostEnvironment,
    IOptions<WordPressMigrationOptions> options,
    ILogger<WordPressMigrationRunner> logger)
{
    private readonly int _checkpointInterval = Math.Max(options.Value.ProgressInterval, 1);
    private readonly bool _logItemStarts = options.Value.LogItemStarts;

    public async Task<MigrationRunResult> RunFromJsonFileAsync(
        string jsonPath,
        int userId = -1,
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
            return new MigrationRunResult(0, 0, 0, []);
        }

        logger.LogInformation("Starting WordPress migration for {Count} items from {Path}.", items.Count, resolvedPath);
        return Run(items, userId);
    }

    public MigrationRunResult Run(
        IEnumerable<WordPressMigrationItem> items,
        int userId = -1,
        IProgress<MigrationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<WordPressMigrationItem> itemList = items as List<WordPressMigrationItem> ?? items.ToList();
        List<string> errors = [];
        int total = itemList.Count;
        int succeeded = 0;
        int current = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (total == 0)
        {
            logger.LogInformation("WordPress migration completed. No items were provided.");
            return new MigrationRunResult(0, 0, 0, []);
        }

        foreach (WordPressMigrationItem item in itemList)
        {
            current++;

            MigrationProgress startProgress = new(
                Current: current,
                Total: total,
                Succeeded: succeeded,
                Failed: errors.Count,
                ItemName: item.Name,
                ItemSlug: item.Slug,
                WordPressType: item.WordPressType,
                ContentTypeAlias: item.ContentTypeAlias,
                Status: MigrationItemStatus.Started,
                ErrorMessage: null);

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
                migrationContentService.Upsert(item, userId);
                succeeded++;

                MigrationProgress successProgress = new(
                    Current: current,
                    Total: total,
                    Succeeded: succeeded,
                    Failed: errors.Count,
                    ItemName: item.Name,
                    ItemSlug: item.Slug,
                    WordPressType: item.WordPressType,
                    ContentTypeAlias: item.ContentTypeAlias,
                    Status: MigrationItemStatus.Succeeded,
                    ErrorMessage: null);

                progress?.Report(successProgress);
                logger.LogInformation(
                    "[{Current}/{Total}] ({Percent}%) Succeeded '{ItemName}'",
                    successProgress.Current,
                    successProgress.Total,
                    successProgress.Percentage,
                    successProgress.ItemName);
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
                    ItemName: item.Name,
                    ItemSlug: item.Slug,
                    WordPressType: item.WordPressType,
                    ContentTypeAlias: item.ContentTypeAlias,
                    Status: MigrationItemStatus.Failed,
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
                    "Checkpoint {Current}/{Total} ({Percent}%): Succeeded {Succeeded}, Failed {Failed}, Rate {Rate:F2} items/s, ETA {Eta}",
                    current,
                    total,
                    (int)Math.Round((double)current * 100 / total),
                    succeeded,
                    errors.Count,
                    itemsPerSecond,
                    eta.ToString(@"hh\:mm\:ss"));
            }
        }

        int failed = total - succeeded;

        logger.LogInformation(
            "WordPress migration completed. Total: {Total}, Succeeded: {Succeeded}, Failed: {Failed}.",
            total,
            succeeded,
            failed);

        return new MigrationRunResult(total, succeeded, failed, errors);
    }

    public sealed record MigrationRunResult(
        int Total,
        int Succeeded,
        int Failed,
        IReadOnlyList<string> Errors);

    public sealed record MigrationProgress(
        int Current,
        int Total,
        int Succeeded,
        int Failed,
        string ItemName,
        string? ItemSlug,
        string WordPressType,
        string ContentTypeAlias,
        MigrationItemStatus Status,
        string? ErrorMessage)
    {
        public int Percentage => Total == 0 ? 0 : (int)Math.Round((double)Current * 100 / Total);
    }

    public enum MigrationItemStatus
    {
        Started,
        Succeeded,
        Failed
    }
}