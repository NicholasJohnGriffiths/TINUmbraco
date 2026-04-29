namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationOptions
{
    public bool RunOnStartup { get; set; }

    public string? JsonPath { get; set; }

    public string[] AvailableJsonPaths { get; set; } = [];

    public int ProgressInterval { get; set; } = 10;

    public bool LogItemStarts { get; set; } = true;

    public bool UpdateOnlyExisting { get; set; }

    public string[] SkipTypes { get; set; } = [];

    public bool FailOnMissingContentTypeAliases { get; set; } = true;
}