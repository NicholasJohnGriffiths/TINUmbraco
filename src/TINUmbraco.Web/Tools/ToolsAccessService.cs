namespace TINUmbraco.Web.Tools;

public sealed class ToolsAccessService(IHostEnvironment hostEnvironment)
{
    public bool CanAccess(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Keep tools strictly local/dev-only and never expose on live environments.
        return hostEnvironment.IsDevelopment() && IsLocalRequest(httpContext);
    }

    private static bool IsLocalRequest(HttpContext httpContext)
    {
        if (httpContext.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string host = httpContext.Request.Host.Host;
        if (host == "127.0.0.1" || host == "::1")
        {
            return true;
        }

        IPAddress? remoteIp = httpContext.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remoteIp))
        {
            return true;
        }

        IPAddress? localIp = httpContext.Connection.LocalIpAddress;
        return localIp is not null && remoteIp.Equals(localIp);
    }
}