using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VisionStudio.Api.Tests;

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task ApiResponse_CarriesSecurityHeaders()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/host-mode");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        var csp = Header(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("style-src 'self' 'unsafe-inline'", csp); // antd CSS-in-JS
        Assert.Contains("img-src 'self' data: blob:", csp);       // 预览/下载使用 blob:
        Assert.Contains("connect-src 'self' ws: wss:", csp);      // SignalR WebSocket
        Assert.Contains("frame-ancestors 'none'", csp);
        // HTTP 上不发送 HSTS（浏览器也会忽略，但探针/代理不应看到误导性声明）。
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task ProblemDetailsErrorResponse_AlsoCarriesSecurityHeaders()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();

        // 该 404 由异常处理器生成（problem+json）——安全头必须在错误路径同样存在。
        var response = await client.GetAsync("/api/devices/not-registered");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Contains("frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task HttpsRequest_CarriesHsts()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/api/host-mode");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("max-age=31536000", Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task DisabledConfiguration_EmitsNoSecurityHeaders()
    {
        using var factory = new NoSecurityHeadersApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/host-mode");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Content-Type-Options"));
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        Assert.True(response.Headers.TryGetValues(name, out var values), $"missing header {name}");
        return Assert.Single(values!);
    }
}

public sealed class NoSecurityHeadersApiFactory : IsolatedApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Security:Enabled", "false");
        builder.UseSetting("SecurityHeaders:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Port", "0");
    }
}