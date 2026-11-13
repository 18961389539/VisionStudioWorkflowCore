using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 生产安全边界：免密码管理员自动登录（开发便利开关）不得随配置继承进入 Production；
/// 生产环境检测到该组合时直接拒绝启动，非生产环境关闭开关时端点返回 404。
/// </summary>
public sealed class ProductionSecurityGuardTests
{
    [Fact]
    public void ProductionEnvironment_WithAutoLoginAdmin_RefusesToStart()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("Security:Enabled", "true");
                builder.UseSetting("Security:AutoLoginAdmin", "true");
                builder.UseSetting("RobotTcpSimulator:Enabled", "false");
                builder.UseSetting("RobotTcpSimulator:Port", "0");
            });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("AutoLoginAdmin", ex.ToString());
    }

    [Fact]
    public async Task AutoLoginEndpoint_IsNotFound_WhenDisabled()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Security:Enabled", "true");
                builder.UseSetting("Security:AutoLoginAdmin", "false");
                builder.UseSetting("RobotTcpSimulator:Enabled", "false");
                builder.UseSetting("RobotTcpSimulator:Port", "0");
            });
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/auth/auto-login", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
