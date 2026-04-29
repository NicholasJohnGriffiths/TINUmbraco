namespace TINUmbraco.Web.Migrations;

public sealed record MigrationDashboardRunRequest(
    string? JsonPath,
    bool DryRun);