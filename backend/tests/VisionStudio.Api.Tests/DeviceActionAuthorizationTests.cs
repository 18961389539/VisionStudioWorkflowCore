using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
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
    /// <summary>含副作用节点且必然失败的运行（写入不存在的标签）——用于验证"失败后意图保留"。</summary>
    private static WorkflowDefinition FailingWriteWorkflow()
        => new(
            "f02-probe",
            "F02 Failed Write Probe",
            [
                new NodeDefinition("w1", "device.writeTag", "WriteMissingTag", null, new Dictionary<string, JsonElement>
                {
                    ["deviceId"] = JsonSerializer.SerializeToElement("virtual-modbus-1"),
                    ["tagId"] = JsonSerializer.SerializeToElement("NONEXISTENT_TAG"),
                    ["valueType"] = JsonSerializer.SerializeToElement("String"),
                    ["fallbackValue"] = JsonSerializer.SerializeToElement("VERIFY")
                })
            ],
            []);

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
    public async Task FailedSideEffectRun_LeavesIntentUnknown_AndBlocksEveryEntry()
    {
        // F02 回归（探针反例转正）：副作用运行失败后，动作意图绝不能在 using 收尾时被静默清除——
        // 旧实现 dispose 无条件删除意图，导致"写成功/失败后下一次手动写入 200"。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var run = await client.PostAsJsonAsync("/api/run", FailingWriteWorkflow());
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);

        // 意图保留为 Unknown（不再是"运行结束即清除"）。
        var pending = await client.GetAsync("/api/production/device-actions/pending");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingBody = await pending.Content.ReadAsStringAsync();
        Assert.Contains("workflow.run.adhoc", pendingBody);
        Assert.Contains("Unknown", pendingBody);

        // 下一次手动写入必须被拒（旧实现：200）。
        var write = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);

        // R07/F03：health 必须同步表达"生产动作未就绪"。
        var health = await client.GetAsync("/api/health");
        var healthBody = await health.Content.ReadAsStringAsync();
        Assert.Contains("\"productionReady\":false", healthBody);
        Assert.Contains("unresolved manual device action", healthBody);

        // F01：调试会话同样被阻断（此前 debug-session-with-pending=200）。
        var debug = await client.PostAsJsonAsync("/api/debug/sessions", new
        {
            workflow = FailingWriteWorkflow(),
            options = new { mode = "Breakpoints", breakpoints = new[] { "w1" }, allowSideEffects = true }
        });
        Assert.Equal(HttpStatusCode.Conflict, debug.StatusCode);
    }

    [Fact]
    public async Task StaleManualIntent_IsVisibleInPending_AndReconcilableByAdministrator()
    {
        // F03 回归：重启遗留的手动意图必须（a）在 pending 中可见，（b）可由管理员带证据核对清除，
        // （c）期间 health 不得声称生产动作就绪——旧实现里它只锁死系统、没有受控恢复入口。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        store.Add(new ManualActionIntent(
            "legacy-intent", "device.writeTag", ["virtual-modbus-1"], [], [],
            DateTimeOffset.UtcNow, ProcessId: "previous-process"));

        var pending = await client.GetAsync("/api/production/device-actions/pending");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingBody = await pending.Content.ReadAsStringAsync();
        Assert.Contains("legacy-intent", pendingBody);
        Assert.Contains("device.writeTag", pendingBody);

        var blocked = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        // 缺少证据时不得清除（核对必须可追溯）。
        var noEvidence = await client.PostAsJsonAsync("/api/production/device-actions/resolve", new
        {
            runId = "",
            manifestHash = "",
            deviceIds = Array.Empty<string>(),
            robotIds = Array.Empty<string>(),
            reason = "probe",
            evidence = "",
            deviceIdentityEvidence = "",
            manualIntentId = "legacy-intent"
        });
        Assert.Equal(HttpStatusCode.BadRequest, noEvidence.StatusCode);

        // 管理员带证据核对 → 精确清除该意图 → 动作入口恢复。
        var resolved = await client.PostAsJsonAsync("/api/production/device-actions/resolve", new
        {
            runId = "",
            manifestHash = "",
            deviceIds = Array.Empty<string>(),
            robotIds = Array.Empty<string>(),
            reason = "probe: verified on site",
            evidence = "probe: device idle, trigger bit reset",
            deviceIdentityEvidence = "probe: nameplate + endpoint verified",
            manualIntentId = "legacy-intent"
        });
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());

        var allowed = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe2" } });
        Assert.NotEqual(HttpStatusCode.Conflict, allowed.StatusCode);
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
        Assert.Contains("unresolved manual device action", body, StringComparison.OrdinalIgnoreCase);

        // 核对完成/清除遗留意图 → 动作入口恢复。
        store.Remove("legacy-intent");
        var allowed = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe2" } });
        Assert.NotEqual(HttpStatusCode.Conflict, allowed.StatusCode);
    }
}
