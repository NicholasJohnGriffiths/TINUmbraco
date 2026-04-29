using Microsoft.Extensions.Options;

namespace TINUmbraco.Web.Migrations;

public sealed class MigrationDashboardService(
    IServiceScopeFactory serviceScopeFactory,
    IHostEnvironment hostEnvironment,
    IOptions<WordPressMigrationOptions> options,
    ILogger<MigrationDashboardService> logger)
{
    private const int MaxLogEntries = 200;
    private readonly object _sync = new();
    private MigrationDashboardSnapshot _snapshot = CreateIdleSnapshot(options.Value, hostEnvironment);

    public MigrationDashboardSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot.Copy();
        }
    }

    public MigrationPreflightResult GetPreflight(string? requestedJsonPath)
    {
        using IServiceScope scope = serviceScopeFactory.CreateScope();
        WordPressMigrationMediaService mediaService = scope.ServiceProvider.GetRequiredService<WordPressMigrationMediaService>();

        string selectedJsonPath = ResolveJsonPath(requestedJsonPath, options.Value, hostEnvironment);
        if (string.IsNullOrWhiteSpace(selectedJsonPath))
        {
            return new MigrationPreflightResult(
                SelectedJsonPath: string.Empty,
                ResolvedPath: string.Empty,
                JsonFound: false,
                TotalItems: 0,
                MediaUrlCount: 0,
                MediaRootExists: mediaService.MediaRootExists(),
                Folders: BuildFolderPreflight([], mediaService),
                Messages: ["No JSON path selected."]);
        }

        string resolvedPath = Path.IsPathRooted(selectedJsonPath)
            ? selectedJsonPath
            : Path.Combine(hostEnvironment.ContentRootPath, selectedJsonPath);

        if (!File.Exists(resolvedPath))
        {
            return new MigrationPreflightResult(
                SelectedJsonPath: selectedJsonPath,
                ResolvedPath: resolvedPath,
                JsonFound: false,
                TotalItems: 0,
                MediaUrlCount: 0,
                MediaRootExists: mediaService.MediaRootExists(),
                Folders: BuildFolderPreflight([], mediaService),
                Messages: [$"JSON file not found: {resolvedPath}"]);
        }

        List<WordPressMigrationItem>? items = System.Text.Json.JsonSerializer.Deserialize<List<WordPressMigrationItem>>(
            File.ReadAllText(resolvedPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        List<WordPressMigrationItem> itemList = items ?? [];
        List<string> referencedFolders = [];
        int mediaUrlCount = 0;

        foreach (WordPressMigrationItem item in itemList)
        {
            string folder = mediaService.GetFolderNameForWordPressType(item.WordPressType);
            referencedFolders.Add(folder);

            foreach (object? value in item.Values.Values)
            {
                mediaUrlCount += CountImageUrls(value);
            }
        }

        List<string> messages = [];
        if (mediaUrlCount == 0)
        {
            messages.Add("No image URLs were detected in migration values.");
        }

        if (!mediaService.MediaRootExists())
        {
            messages.Add("Media root folder was not found; it will be created automatically when importing media.");
        }

        return new MigrationPreflightResult(
            SelectedJsonPath: selectedJsonPath,
            ResolvedPath: resolvedPath,
            JsonFound: true,
            TotalItems: itemList.Count,
            MediaUrlCount: mediaUrlCount,
            MediaRootExists: mediaService.MediaRootExists(),
            Folders: BuildFolderPreflight(referencedFolders, mediaService),
            Messages: messages);
    }

    public bool TryStartRun(MigrationDashboardRunRequest? request, out string message)
    {
        string selectedJsonPath = ResolveJsonPath(request?.JsonPath, options.Value, hostEnvironment);
        bool dryRun = request?.DryRun ?? false;

        lock (_sync)
        {
            if (_snapshot.IsRunning)
            {
                message = "A migration run is already in progress.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(selectedJsonPath))
            {
                _snapshot = _snapshot.WithMessage(
                    status: "Configuration required",
                    message: "Set or choose a migration JSON path before starting a run.",
                    isRunning: false,
                    startedUtc: null,
                    finishedUtc: DateTimeOffset.UtcNow,
                    resetCounts: false);

                message = "Migration JSON path is not configured.";
                return false;
            }

            _snapshot = MigrationDashboardSnapshot.CreateRunning(
                    configuredJsonPath: options.Value.JsonPath,
                    selectedJsonPath: selectedJsonPath,
                    availableJsonPaths: GetAvailableJsonPaths(options.Value, hostEnvironment),
                    dryRun: dryRun)
                .AppendLog($"Queued {(dryRun ? "dry run" : "migration run")} for '{selectedJsonPath}'.");

            message = dryRun ? "Dry run started." : "Migration run started.";
        }

        _ = Task.Run(() => RunAsync(selectedJsonPath, dryRun));
        return true;
    }

    private async Task RunAsync(string jsonPath, bool dryRun)
    {
        try
        {
            using IServiceScope scope = serviceScopeFactory.CreateScope();
            WordPressMigrationRunner runner = scope.ServiceProvider.GetRequiredService<WordPressMigrationRunner>();
            Progress<WordPressMigrationRunner.MigrationProgress> progress = new(ReportProgress);

            WordPressMigrationRunner.MigrationRunResult result = await runner.RunFromJsonFileAsync(
                jsonPath,
                progress: progress,
                dryRun: dryRun);

            lock (_sync)
            {
                _snapshot = _snapshot.WithCompletion(result)
                    .AppendLog($"{_snapshot.RunModeLabel} completed. Succeeded {result.Succeeded}, Failed {result.Failed}, Skipped {result.Skipped}.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual migration run failed.");

            lock (_sync)
            {
                _snapshot = _snapshot.WithFailure(ex.Message)
                    .AppendLog($"{_snapshot.RunModeLabel} failed: {ex.Message}");
            }
        }
    }

    private void ReportProgress(WordPressMigrationRunner.MigrationProgress progress)
    {
        lock (_sync)
        {
            _snapshot = _snapshot.WithProgress(progress)
                .AppendLog(FormatProgressMessage(progress));
        }
    }

    private static string FormatProgressMessage(WordPressMigrationRunner.MigrationProgress progress)
    {
        return progress.Status switch
        {
            WordPressMigrationRunner.MigrationItemStatus.Started =>
                $"[{progress.Current}/{progress.Total}] Starting '{progress.ItemName}' ({progress.WordPressType}/{progress.ContentTypeAlias}).",
            WordPressMigrationRunner.MigrationItemStatus.Succeeded =>
                $"[{progress.Current}/{progress.Total}] {(progress.IsDryRun ? "Validated" : "Succeeded")} '{progress.ItemName}'.",
            WordPressMigrationRunner.MigrationItemStatus.Skipped =>
                $"[{progress.Current}/{progress.Total}] Skipped '{progress.ItemName}'. {progress.ErrorMessage}",
            WordPressMigrationRunner.MigrationItemStatus.Failed =>
                $"[{progress.Current}/{progress.Total}] Failed '{progress.ItemName}'. {progress.ErrorMessage}",
            _ => $"[{progress.Current}/{progress.Total}] {progress.Status} '{progress.ItemName}'."
        };
    }

    private static MigrationDashboardSnapshot CreateIdleSnapshot(
        WordPressMigrationOptions optionsValue,
        IHostEnvironment hostEnvironment)
    {
        string[] availableJsonPaths = GetAvailableJsonPaths(optionsValue, hostEnvironment);
        string selectedJsonPath = ResolveJsonPath(optionsValue.JsonPath, optionsValue, hostEnvironment);

        return MigrationDashboardSnapshot.CreateIdle(
            configuredJsonPath: optionsValue.JsonPath,
            selectedJsonPath: selectedJsonPath,
            availableJsonPaths: availableJsonPaths);
    }

    private static string ResolveJsonPath(
        string? requestedJsonPath,
        WordPressMigrationOptions optionsValue,
        IHostEnvironment hostEnvironment)
    {
        string candidate = string.IsNullOrWhiteSpace(requestedJsonPath)
            ? optionsValue.JsonPath?.Trim() ?? string.Empty
            : requestedJsonPath.Trim();

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        return GetAvailableJsonPaths(optionsValue, hostEnvironment).FirstOrDefault() ?? string.Empty;
    }

    private static string[] GetAvailableJsonPaths(
        WordPressMigrationOptions optionsValue,
        IHostEnvironment hostEnvironment)
    {
        HashSet<string> jsonPaths = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(optionsValue.JsonPath))
        {
            jsonPaths.Add(optionsValue.JsonPath.Trim());
        }

        foreach (string configuredPath in optionsValue.AvailableJsonPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                jsonPaths.Add(configuredPath.Trim());
            }
        }

        if (Directory.Exists(hostEnvironment.ContentRootPath))
        {
            foreach (string file in Directory.EnumerateFiles(hostEnvironment.ContentRootPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Contains("migration", StringComparison.OrdinalIgnoreCase))
                {
                    jsonPaths.Add(fileName);
                }
            }
        }

        return jsonPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<MigrationFolderPreflight> BuildFolderPreflight(
        IReadOnlyCollection<string> referencedFolders,
        WordPressMigrationMediaService mediaService)
    {
        Dictionary<string, int> referencedByFolder = referencedFolders
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        return mediaService
            .GetKnownFolderNames()
            .Select(folderName => new MigrationFolderPreflight(
                Name: folderName,
                Exists: mediaService.FolderExists(folderName),
                ReferencedItemCount: referencedByFolder.TryGetValue(folderName, out int count) ? count : 0))
            .ToList();
    }

    private static int CountImageUrls(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is string str)
        {
            return LooksLikeImageUrl(str) ? 1 : 0;
        }

        if (value is System.Text.Json.JsonElement element)
        {
            return CountImageUrlsFromElement(element);
        }

        return 0;
    }

    private static int CountImageUrlsFromElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => LooksLikeImageUrl(element.GetString()) ? 1 : 0,
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Sum(CountImageUrlsFromElement),
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().Sum(x => CountImageUrlsFromElement(x.Value)),
            _ => 0
        };
    }

    private static bool LooksLikeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        string ext = Path.GetExtension(uri.LocalPath);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".avif", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record MigrationPreflightResult(
        string SelectedJsonPath,
        string ResolvedPath,
        bool JsonFound,
        int TotalItems,
        int MediaUrlCount,
        bool MediaRootExists,
        IReadOnlyList<MigrationFolderPreflight> Folders,
        IReadOnlyList<string> Messages);

    public sealed record MigrationFolderPreflight(
        string Name,
        bool Exists,
        int ReferencedItemCount);

    public sealed record MigrationDashboardSnapshot(
        string? ConfiguredJsonPath,
        string? SelectedJsonPath,
        IReadOnlyList<string> AvailableJsonPaths,
        bool IsDryRun,
        bool IsRunning,
        string Status,
        string? LastMessage,
        string? CurrentItemName,
        string? CurrentWordPressType,
        string? CurrentContentTypeAlias,
        int Current,
        int Total,
        int Succeeded,
        int Failed,
        int Skipped,
        DateTimeOffset? StartedUtc,
        DateTimeOffset? FinishedUtc,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> LogEntries)
    {
        public bool CanStart => !IsRunning && !string.IsNullOrWhiteSpace(SelectedJsonPath);
        public string RunModeLabel => IsDryRun ? "Dry run" : "Migration";

        public static MigrationDashboardSnapshot CreateIdle(
            string? configuredJsonPath,
            string? selectedJsonPath,
            IReadOnlyList<string> availableJsonPaths)
        {
            return new MigrationDashboardSnapshot(
                ConfiguredJsonPath: configuredJsonPath,
                SelectedJsonPath: selectedJsonPath,
                AvailableJsonPaths: availableJsonPaths,
                IsDryRun: false,
                IsRunning: false,
                Status: "Ready",
                LastMessage: string.IsNullOrWhiteSpace(selectedJsonPath)
                    ? "Set or choose a migration JSON file to enable manual runs."
                    : "Ready to run the configured migration dataset.",
                CurrentItemName: null,
                CurrentWordPressType: null,
                CurrentContentTypeAlias: null,
                Current: 0,
                Total: 0,
                Succeeded: 0,
                Failed: 0,
                Skipped: 0,
                StartedUtc: null,
                FinishedUtc: null,
                Errors: [],
                LogEntries: []);
        }

        public static MigrationDashboardSnapshot CreateRunning(
            string? configuredJsonPath,
            string selectedJsonPath,
            IReadOnlyList<string> availableJsonPaths,
            bool dryRun)
        {
            return new MigrationDashboardSnapshot(
                ConfiguredJsonPath: configuredJsonPath,
                SelectedJsonPath: selectedJsonPath,
                AvailableJsonPaths: availableJsonPaths,
                IsDryRun: dryRun,
                IsRunning: true,
                Status: dryRun ? "Dry run in progress" : "Running",
                LastMessage: dryRun ? "Dry run started." : "Migration started.",
                CurrentItemName: null,
                CurrentWordPressType: null,
                CurrentContentTypeAlias: null,
                Current: 0,
                Total: 0,
                Succeeded: 0,
                Failed: 0,
                Skipped: 0,
                StartedUtc: DateTimeOffset.UtcNow,
                FinishedUtc: null,
                Errors: [],
                LogEntries: []);
        }

        public MigrationDashboardSnapshot Copy()
        {
            return this with
            {
                AvailableJsonPaths = AvailableJsonPaths.ToList(),
                Errors = Errors.ToList(),
                LogEntries = LogEntries.ToList()
            };
        }

        public MigrationDashboardSnapshot WithProgress(WordPressMigrationRunner.MigrationProgress progress)
        {
            return this with
            {
                Status = progress.Status switch
                {
                    WordPressMigrationRunner.MigrationItemStatus.Started => IsDryRun ? "Dry run in progress" : "Running",
                    WordPressMigrationRunner.MigrationItemStatus.Succeeded => IsDryRun ? "Dry run in progress" : "Running",
                    WordPressMigrationRunner.MigrationItemStatus.Skipped => IsDryRun ? "Dry run in progress" : "Running",
                    WordPressMigrationRunner.MigrationItemStatus.Failed => IsDryRun ? "Dry run with errors" : "Running with errors",
                    _ => Status
                },
                LastMessage = progress.ErrorMessage ?? progress.Status.ToString(),
                CurrentItemName = progress.ItemName,
                CurrentWordPressType = progress.WordPressType,
                CurrentContentTypeAlias = progress.ContentTypeAlias,
                Current = progress.Current,
                Total = progress.Total,
                Succeeded = progress.Succeeded,
                Failed = progress.Failed,
                Skipped = progress.Skipped,
                FinishedUtc = null
            };
        }

        public MigrationDashboardSnapshot WithCompletion(WordPressMigrationRunner.MigrationRunResult result)
        {
            return this with
            {
                IsRunning = false,
                Status = result.Failed > 0
                    ? (IsDryRun ? "Dry run completed with errors" : "Completed with errors")
                    : (IsDryRun ? "Dry run completed" : "Completed"),
                LastMessage = $"Processed {result.Total} items.",
                Current = result.Total,
                Total = result.Total,
                Succeeded = result.Succeeded,
                Failed = result.Failed,
                Skipped = result.Skipped,
                FinishedUtc = DateTimeOffset.UtcNow,
                Errors = result.Errors.ToList()
            };
        }

        public MigrationDashboardSnapshot WithFailure(string errorMessage)
        {
            List<string> errors = Errors.ToList();
            errors.Add(errorMessage);

            return this with
            {
                IsRunning = false,
                Status = IsDryRun ? "Dry run failed" : "Failed",
                LastMessage = errorMessage,
                FinishedUtc = DateTimeOffset.UtcNow,
                Errors = errors
            };
        }

        public MigrationDashboardSnapshot WithMessage(
            string status,
            string message,
            bool isRunning,
            DateTimeOffset? startedUtc,
            DateTimeOffset? finishedUtc,
            bool resetCounts)
        {
            return this with
            {
                Status = status,
                LastMessage = message,
                IsRunning = isRunning,
                StartedUtc = startedUtc,
                FinishedUtc = finishedUtc,
                Current = resetCounts ? 0 : Current,
                Total = resetCounts ? 0 : Total,
                Succeeded = resetCounts ? 0 : Succeeded,
                Failed = resetCounts ? 0 : Failed,
                Skipped = resetCounts ? 0 : Skipped
            };
        }

        public MigrationDashboardSnapshot AppendLog(string entry)
        {
            List<string> logEntries = LogEntries.ToList();
            logEntries.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {entry}");

            if (logEntries.Count > MaxLogEntries)
            {
                logEntries = logEntries.Skip(logEntries.Count - MaxLogEntries).ToList();
            }

            return this with { LogEntries = logEntries };
        }
    }
}