using System.Net;

namespace TINUmbraco.Web.Tools;

public sealed class ToolsAccessService(IHostEnvironment hostEnvironment)
{
    private const string ForwardedHostHeader = "X-Forwarded-Host";
    private const string OriginalHostHeader = "X-Original-Host";

    public bool CanAccess(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!hostEnvironment.IsDevelopment())
        {
            return false;
        }

        string requestHost = httpContext.Request.Host.Host;
        if (!IsLoopbackHost(requestHost))
        {
            return false;
        }

        string? forwardedHost = GetFirstHeaderHostValue(httpContext, ForwardedHostHeader)
            ?? GetFirstHeaderHostValue(httpContext, OriginalHostHeader);

        // If upstream indicates the original host was non-loopback, deny tools access.
        return string.IsNullOrWhiteSpace(forwardedHost) || IsLoopbackHost(forwardedHost);
    }

    private static bool IsLoopbackHost(string host)
    {
        string normalizedHost = host.Trim();
        if (normalizedHost.Length == 0)
        {
            return false;
        }

        int commaIndex = normalizedHost.IndexOf(',');
        if (commaIndex >= 0)
        {
            normalizedHost = normalizedHost[..commaIndex].Trim();
        }

        if (normalizedHost.StartsWith("[", StringComparison.Ordinal) && normalizedHost.Contains(']'))
        {
            int endBracketIndex = normalizedHost.IndexOf(']');
            normalizedHost = normalizedHost[1..endBracketIndex];
        }
        else if (normalizedHost.Contains(':'))
        {
            int colonCount = normalizedHost.Count(c => c == ':');
            if (colonCount == 1)
            {
                normalizedHost = normalizedHost[..normalizedHost.LastIndexOf(':')];
            }
        }

        if (normalizedHost.Length == 0)
        {
            return false;
        }

        if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(normalizedHost, out IPAddress? parsedIp)
            && IPAddress.IsLoopback(parsedIp);
    }

    private static string? GetFirstHeaderHostValue(HttpContext httpContext, string headerName)
    {
        if (!httpContext.Request.Headers.TryGetValue(headerName, out var headerValues))
        {
            return null;
        }

        string? rawHeaderValue = headerValues.FirstOrDefault();
        return string.IsNullOrWhiteSpace(rawHeaderValue) ? null : rawHeaderValue;
    }
}