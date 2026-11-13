using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using VisionStudio.Api.Hosting;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 2026-11-12 整改回归：内置 TCP 机器人模拟器的生产边界。
///
/// 背景：<c>appsettings.json</c> 默认 <c>RobotTcpSimulator:Enabled=true</c>，Production 覆盖文件原先没有
/// 这一节，于是现场部署会开 40501 端口并注册 <c>tcp-sim-1</c> 适配器。工艺员选中它、下发目标、
/// 看到 InPosition，产线却没有任何动作——这类"虚拟被当成实机"的误判必须由代码本身堵住，
/// 不能只靠配置文件（现场配置会被复制、会被改）。
///
/// 断言的是纯决策函数：不启宿主、不开端口，因此可以在任何环境稳定运行。
/// </summary>
public sealed class RobotSimulatorPolicyTests
{
    private static IConfiguration Config(bool enabled)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RobotTcpSimulator:Enabled"] = enabled ? "true" : "false" })
            .Build();

    private static IWebHostEnvironment Env(string name) => new StubEnvironment(name);

    [Fact]
    public void Development_WithSimulatorEnabled_Starts()
    {
        Assert.True(RobotSimulatorPolicy.ShouldStart(Config(true), Env(Environments.Development), out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void Production_WithSimulatorEnabled_RefusesAndExplains()
    {
        // 关键用例：配置显式写着 Enabled=true，生产环境也必须拒绝。
        Assert.False(RobotSimulatorPolicy.ShouldStart(Config(true), Env(Environments.Production), out var reason));
        Assert.Contains("Production", reason);
        Assert.Contains("RobotTcpSimulator", reason);
    }

    [Fact]
    public void Testing_WithSimulatorEnabled_StaysHermetic()
    {
        Assert.False(RobotSimulatorPolicy.ShouldStart(Config(true), Env("Testing"), out var reason));
        Assert.Contains("Testing", reason);
    }

    [Fact]
    public void ExplicitlyDisabled_NeverStartsAndDoesNotWarn()
    {
        foreach (var environment in new[] { Environments.Development, Environments.Production, "Testing" })
        {
            Assert.False(RobotSimulatorPolicy.ShouldStart(Config(false), Env(environment), out var reason));
            // 显式关闭是正常配置，不该在日志里刷告警。
            Assert.Empty(reason);
        }
    }

    private sealed class StubEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "VisionStudio.Api.Tests";
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
