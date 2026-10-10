using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// R01 / R07 回归（2026-10-11 复审报告）。
///
/// R01：未闭合的设备动作安全意图此前只阻断生产启动——手动写入、机器人指令与临时运行
/// （仅做资源仲裁）在上一动作未核对时仍可执行。本测试断言统一授权闸门覆盖手动写入入口，
/// 且只读诊断与安全路径保持可达。
/// R07：/api/health 的 ready 只表达 API 服务就绪，不能冒充"生产动作就绪"——未闭合动作
/// 必须在 productionReady / productionBlockers 中显式暴露。
/// </summary>
public sealed class DeviceActionAuthorizationTests
{
    private static async Task WriteUnresolvedIntentAsync(StorageVisionStudioApiFactory factory)
    {
        var env = factory.Services.GetRequiredService<IWebHostEnvironment>();
        var path = Path.Combine(env.ContentRootPath, "data", "production", "device-action-safety.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Phase=Unknown（数字 0）：副作用失败后保留的未核对状态——与探针注入的形态一致。
        await File.WriteAllTextAsync(path, """
            {"manifestHash":"probe-manifest","deviceIds":["virtual-modbus-1"],"robotIds":[],
             "setAt":"2026-10-11T00:00:00+00:00","reason":"probe: unresolved device action",
             "phase":0,"processId":null}
            """);
    }

    [Fact]
    public async Task UnresolvedIntent_BlocksManualDeviceWrite_ButKeepsReadOnlyAndHealthReachable()
    {
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        await WriteUnresolvedIntentAsync(factory);

        // 服务层闸门同样生效（同一契约，供临时运行/其它入口复用）。
        var auth = factory.Services.GetRequiredService<DeviceActionAuthorizationService>();
        Assert.Throws<ApiConflictException>(() => auth.EnsureActionsAllowed("probe.manual"));
        Assert.NotNull(auth.UnresolvedProductionState());

        // R01 核心反例：手动 PLC 写入此前返回 200——现在必须是 409。
        var write = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
        var writeBody = await write.Content.ReadAsStringAsync();
        Assert.Contains("device-action", writeBody, StringComparison.OrdinalIgnoreCase);

        // 只读诊断保持可达（安全停止/只读路径不受闸门限制）。
        var read = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // R07：健康接口必须显式表达"生产动作未就绪"，而不是 ready=true 的假象。
        var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var healthBody = await health.Content.ReadAsStringAsync();
        Assert.Contains("\"productionReady\":false", healthBody);
        Assert.Contains("unresolved device-action intent", healthBody);
    }

    [Fact]
    public async Task ManualActionIntentLeftByPreviousProcess_BlocksWrites_UntilResolved()
    {
        // R01 核心反例（崩溃遗留）：上一进程留下的手动动作意图必须阻断所有动作入口，
        // 直到它被核对/清除；清除后入口恢复可用。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        store.Add(new ManualActionIntent(
            "legacy-intent", "device.writeTag", ["virtual-modbus-1"], [], [],
            DateTimeOffset.UtcNow, ProcessId: "previous-process"));

        var blocked = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("unresolved manual action", body, StringComparison.OrdinalIgnoreCase);

        // 核对完成/清除遗留意图 → 动作入口恢复。
        store.Remove("legacy-intent");
        var allowed = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe2" } });
        Assert.NotEqual(HttpStatusCode.Conflict, allowed.StatusCode);
    }
}
