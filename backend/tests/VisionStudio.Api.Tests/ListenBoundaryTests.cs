using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VisionStudio.Api.Hosting;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 2026-10-13 R3 审查整改回归：R3-02（HTTP_PORTS 绕过生产明文监听保护）+ R3-05（恢复预算无范围校验）。
/// 守卫的优先级矩阵以本机实测为准（Kestrel 端点 ＞ urls ＞ HTTP_PORTS；HTTP_PORTS 恒为通配明文）。
/// </summary>
public sealed class ListenBoundaryTests : IDisposable
{
    private readonly string _lab = Path.Combine(Path.GetTempPath(), "vs-listen-boundary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_lab, recursive: true); } catch { /* 尽力清理 */ }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();

    // ---------- R3-02：来源枚举 ----------

    [Fact]
    public void HttpPorts_IsEnumeratedAsWildcardPlaintextListener()
    {
        // 复现：仅设 ASPNETCORE_HTTP_PORTS 即可让生产环境以 0.0.0.0 明文监听启动（旧实现不枚举它）。
        var env = VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("ASPNETCORE_HTTP_PORTS", "18080")));
        Assert.Contains(env, s => s.Contains("ASPNETCORE_HTTP_PORTS=18080"));
        var stripped = VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("HTTP_PORTS", "18080")));
        Assert.Contains(stripped, s => s.Contains("HTTP_PORTS=18080"));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://*:5080")]
    [InlineData("http://+:5080")]
    [InlineData("http://:5080")]
    [InlineData("http://192.168.10.20:5080")]
    [InlineData("http://[::]:5080")]
    public void RemotePlaintextUrls_AreDetected_FromEveryConfigKey(string url)
    {
        Assert.NotEmpty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("urls", url))));
        Assert.NotEmpty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("ASPNETCORE_URLS", url))));
    }

    [Fact]
    public void KestrelEndpoint_IsDetected_AndConflictingSourcesAreAllEnumerated()
    {
        Assert.NotEmpty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(
            Config(("Kestrel:Endpoints:Main:Url", "http://0.0.0.0:5080"))));

        // 冲突来源（fail-closed）：生效的是 loopback urls，但被压住的 HTTP_PORTS 仍是未豁免的
        // 远程明文配置——必须拒绝并要求显式清理，而不是推断"哪个来源生效"。
        var conflict = VisionStudioServiceRegistration.InsecurePlaintextListenSources(
            Config(("urls", "http://127.0.0.1:5080"), ("HTTP_PORTS", "18080")));
        Assert.Contains(conflict, s => s.Contains("HTTP_PORTS"));

        // 冲突来源：生效的是 loopback Kestrel 端点，被压住的 urls 是远程明文。
        var conflict2 = VisionStudioServiceRegistration.InsecurePlaintextListenSources(
            Config(("Kestrel:Endpoints:Main:Url", "http://127.0.0.1:5080"), ("urls", "http://0.0.0.0:5080")));
        Assert.NotEmpty(conflict2);
    }

    [Fact]
    public void LoopbackAndTlsSources_RemainCompliant()
    {
        Assert.Empty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config()));
        Assert.Empty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("urls", "http://127.0.0.1:5080"))));
        Assert.Empty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("urls", "http://localhost:5080;http://[::1]:5080"))));
        Assert.Empty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("ASPNETCORE_URLS", "http://127.0.0.1:5080"))));
        Assert.Empty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("Kestrel:Endpoints:Main:Url", "https://0.0.0.0:5443")))); // TLS 合规
        Assert.Empty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("HTTPS_PORTS", "5443")))); // TLS 端口合规
        Assert.Empty(VisionStudioServiceRegistration.InsecurePlaintextListenSources(Config(("urls", "http://127.0.0.1:5080"), ("HTTPS_PORTS", "5443"))));
    }

    [Fact]
    public void Production_HttpPortsOnly_RefusesToStart()
    {
        // 端到端：与审查探针同构（Production、Security 启用、禁止不安全远程传输、仅 HTTP_PORTS）。
        // 修复前该组合会启动并 0.0.0.0 明文监听；现在宿主构建即拒绝。
        using var factory = ProductionHost(("ASPNETCORE_HTTP_PORTS", "18081"));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("HTTP_PORTS", error.ToString());
        Assert.Contains("Refusing to start", error.ToString());
    }

    [Fact]
    public async Task Production_LoopbackUrls_StillStarts_AndServesHealth()
    {
        // 合规路径仍能启动（发布脚本的实际姿态）。
        using var factory = ProductionHost(("urls", "http://127.0.0.1:5087"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Production_ExplicitInsecureExemption_AllowsHttpPorts()
    {
        // 明确测试豁免：Security:AllowInsecureRemoteTransport=true（隔离测试网络）时，
        // HTTP_PORTS 明文监听被允许——守卫只在未豁免的生产姿态下拒绝。
        using var factory = ProductionHost(
            ("Security:AllowInsecureRemoteTransport", "true"),
            ("ASPNETCORE_HTTP_PORTS", "18082"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private WebApplicationFactory<Program> ProductionHost(params (string Key, string Value)[] settings)
    {
        Directory.CreateDirectory(_lab);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseContentRoot(_lab);
            builder.UseSetting("Security:Enabled", "true");
            builder.UseSetting("Security:AutoLoginAdmin", "false");
            builder.UseSetting("Security:AllowInsecureRemoteTransport", "false");
            builder.UseSetting("RobotTcpSimulator:Enabled", "false");
            builder.UseSetting("RobotTcpSimulator:Port", "0");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });
    }
}

/// <summary>R3-05：自动恢复预算（maxRecoveryAttempts）的范围校验与读取收敛。</summary>
public sealed class RecoveryBudgetValidationTests : IDisposable
{
    private readonly string _lab = Path.Combine(Path.GetTempPath(), "vs-recovery-budget-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_lab, recursive: true); } catch { /* 尽力清理 */ }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task OutOfRangeBudget_IsRejected_WithoutChangingTheStoredConfig(int value)
    {
        // 复现：-1 与 2147483647 都曾被 200 保存（负数立即耗尽预算；极大值让"有限重试"失效）。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            await client.GetStringAsync("/api/production/config"))!;
        config["maxRecoveryAttempts"] = JsonSerializer.SerializeToElement(value);

        var response = await client.PutAsJsonAsync("/api/production/config", config);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/production/config"));
        Assert.Equal(3, document.RootElement.GetProperty("maxRecoveryAttempts").GetInt32()); // 默认值未被改动
    }

    [Theory]
    [InlineData(0)]    // 0 = 不自动恢复（失败立即 Faulted 等人工）——明确的边界语义
    [InlineData(100)]  // 上限
    [InlineData(7)]
    public async Task BoundaryBudgetValues_AreAccepted(int value)
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            await client.GetStringAsync("/api/production/config"))!;
        config["maxRecoveryAttempts"] = JsonSerializer.SerializeToElement(value);

        var response = await client.PutAsJsonAsync("/api/production/config", config);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/production/config"));
        Assert.Equal(value, document.RootElement.GetProperty("maxRecoveryAttempts").GetInt32());
    }

    [Fact]
    public async Task LegacyOutOfRangeBudget_IsClampedOnLoad()
    {
        // 读取旧文件同样收敛到合法区间（负数/巨大值不再原样进入运行逻辑）。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();   // 先让宿主启动（建立数据根）
        var runtimePath = Path.Combine(factory.ContentRoot, "data", "production", "runtime.json");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
        await File.WriteAllTextAsync(runtimePath, """{"maxRecoveryAttempts":-5}""");

        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/production/config"));
        Assert.Equal(0, document.RootElement.GetProperty("maxRecoveryAttempts").GetInt32());

        await File.WriteAllTextAsync(runtimePath, """{"maxRecoveryAttempts":2147483647}""");
        using var document2 = JsonDocument.Parse(await client.GetStringAsync("/api/production/config"));
        Assert.Equal(100, document2.RootElement.GetProperty("maxRecoveryAttempts").GetInt32());
    }
}
