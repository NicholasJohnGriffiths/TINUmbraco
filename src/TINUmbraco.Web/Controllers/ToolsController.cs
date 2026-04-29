using Microsoft.AspNetCore.Mvc;
using TINUmbraco.Web.Migrations;
using TINUmbraco.Web.Tools;

namespace TINUmbraco.Web.Controllers;

[Route("tools")]
public sealed class ToolsController(
    MigrationDashboardService migrationDashboardService,
    ToolsAccessService toolsAccessService) : Controller
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
}