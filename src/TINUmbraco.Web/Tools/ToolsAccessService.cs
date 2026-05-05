using System.Net;

namespace TINUmbraco.Web.Tools;

public sealed class ToolsAccessService(IHostEnvironment hostEnvironment)
{
    public bool CanAccess(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Keep tools strictly local/dev-only and never expose on live environments.
        return hostEnvironment.IsDevelopment() && IsLoopbackHost(httpContext.Request.Host.Host);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string normalizedHost = host.Trim('[', ']');
        return IPAddress.TryParse(normalizedHost, out IPAddress? parsedIp)
            && IPAddress.IsLoopback(parsedIp);
    }
}