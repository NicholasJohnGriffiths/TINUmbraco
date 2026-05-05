using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TINUmbraco.Web.Tools;
using Xunit;

namespace TINUmbraco.Web.Tests.Tools;

public sealed class ToolsAccessServiceTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void CanAccess_ReturnsTrue_ForDevelopmentOnLoopbackHost(string host)
    {
        // Arrange
        var service = new ToolsAccessService(new StubHostEnvironment(Environments.Development));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);

        // Act
        bool canAccess = service.CanAccess(httpContext);

        // Assert
        Assert.True(canAccess);
    }

    [Theory]
    [InlineData("tinumbraco.azurewebsites.net")]
    [InlineData("www.tin100.com")]
    [InlineData("staging.tin100.com")]
    public void CanAccess_ReturnsFalse_ForDevelopmentOnNonLoopbackHost(string host)
    {
        // Arrange
        var service = new ToolsAccessService(new StubHostEnvironment(Environments.Development));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);

        // Act
        bool canAccess = service.CanAccess(httpContext);

        // Assert
        Assert.False(canAccess);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void CanAccess_ReturnsFalse_ForNonDevelopmentEnvironment(string host)
    {
        // Arrange
        var service = new ToolsAccessService(new StubHostEnvironment(Environments.Production));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(host);

        // Act
        bool canAccess = service.CanAccess(httpContext);

        // Assert
        Assert.False(canAccess);
    }

    [Fact]
    public void CanAccess_ReturnsFalse_WhenForwardedHostIsPublic()
    {
        // Arrange
        var service = new ToolsAccessService(new StubHostEnvironment(Environments.Development));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Headers["X-Forwarded-Host"] = "www.tin100.com";

        // Act
        bool canAccess = service.CanAccess(httpContext);

        // Assert
        Assert.False(canAccess);
    }

    [Fact]
    public void CanAccess_ReturnsTrue_WhenForwardedHostIsLoopback()
    {
        // Arrange
        var service = new ToolsAccessService(new StubHostEnvironment(Environments.Development));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Headers["X-Forwarded-Host"] = "localhost:5137";

        // Act
        bool canAccess = service.CanAccess(httpContext);

        // Assert
        Assert.True(canAccess);
    }

    [Fact]
    public void CanAccess_ReturnsTrue_ForIpv6LoopbackHostWithPort()
    {
        // Arrange
        var service = new ToolsAccessService(new StubHostEnvironment(Environments.Development));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString("[::1]:44377");

        // Act
        bool canAccess = service.CanAccess(httpContext);

        // Assert
        Assert.True(canAccess);
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "TINUmbraco.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
