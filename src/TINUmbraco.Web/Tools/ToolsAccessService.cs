namespace TINUmbraco.Web.Tools;

public sealed class ToolsAccessService(IHostEnvironment hostEnvironment)
{
    public bool CanAccess(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Keep tools strictly local/dev-only and never expose on live environments.
        return hostEnvironment.IsDevelopment();
    }
}