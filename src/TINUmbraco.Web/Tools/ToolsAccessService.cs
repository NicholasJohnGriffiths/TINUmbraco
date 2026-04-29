using Umbraco.Cms.Core;

namespace TINUmbraco.Web.Tools;

public sealed class ToolsAccessService(IHostEnvironment hostEnvironment)
{
    public bool CanAccess(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return hostEnvironment.IsDevelopment()
            || (httpContext.User.Identity?.IsAuthenticated == true
                && httpContext.User.IsInRole(Constants.Security.AdminGroupAlias));
    }
}