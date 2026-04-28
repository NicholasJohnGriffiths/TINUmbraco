namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationOptions
{
    public string? JsonPath { get; set; }

    public int ProgressInterval { get; set; } = 10;

    public bool LogItemStarts { get; set; } = true;
}