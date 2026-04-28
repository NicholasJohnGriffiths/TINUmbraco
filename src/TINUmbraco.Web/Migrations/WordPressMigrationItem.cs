namespace TINUmbraco.Web.Migrations;

public sealed record WordPressMigrationItem(
    string WordPressType,
    string ContentTypeAlias,
    string Name,
    string? Slug,
    IReadOnlyDictionary<string, object?> Values,
    bool Publish = true);