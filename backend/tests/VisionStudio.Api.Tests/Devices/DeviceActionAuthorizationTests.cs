using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Robot;

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
    [Fact]
    public void RequiresManualReconciliation_RejectsAcceptedButUnconfirmedDeviceAction()
    {
        // C01 回归：流程/节点返回成功 ≠ 物理动作完成。只有"已受理"（SendTarget / 未等待到位，
        // inPosition=false）时必须要求人工核对——旧实现按 result.Success 直接解除意图。
        var acceptedWorkflow = RobotWorkflow(action: "SendTarget", waitForInPosition: false);
        Assert.True(DeviceSideEffectClassifier.RequiresManualReconciliation(acceptedWorkflow, RobotResult("run-c01", "TargetAccepted", inPosition: false)));

        // 对照：等待到位且协议确认 InPosition ⇒ 允许解除。
        var confirmedWorkflow = RobotWorkflow(action: "Handshake", waitForInPosition: true);
        Assert.False(DeviceSideEffectClassifier.RequiresManualReconciliation(confirmedWorkflow, RobotResult("run-c01-ok", "InPosition", inPosition: true)));

        // 节点成功但快照缺失（无法证明到位）⇒ 保守要求核对。
        Assert.True(DeviceSideEffectClassifier.RequiresManualReconciliation(confirmedWorkflow, RobotResult("run-c01-bare", null, inPosition: null)));

        // 空报告集无法证明异步命令被跳过 ⇒ 保守要求核对。
        var empty = new WorkflowRunResult("run-c01-empty", true, 1, false, 0, 0, [], [], null, QualityDisposition: "OK");
        Assert.True(DeviceSideEffectClassifier.RequiresManualReconciliation(acceptedWorkflow, empty));
    }

    [Fact]
    public void RequiresManualReconciliation_AcceptsAcknowledgedHandshake()
    {
        // N02 回归：默认 autoAck=true 的握手在 Ack 后协议复位为 Ready（inPosition/complete 清零）——
        // 节点报告的只是"Ack 已确认 + 不再忙碌 + 无错误"的历史结果，正常成功不得被误判为未确认。
        var workflow = RobotWorkflow(action: "Handshake", waitForInPosition: true);
        var acknowledged = new WorkflowRunResult("run-n02-ack", true, 1, false, 0, 0,
            [HandshakeReport(ack: true, busy: false, error: false)], [], null, QualityDisposition: "OK");
        Assert.False(DeviceSideEffectClassifier.RequiresManualReconciliation(workflow, acknowledged));

        // 对照：Ack 后仍在忙碌（命令尚未物理结束）⇒ 保守要求核对。
        var stillBusy = new WorkflowRunResult("run-n02-busy", true, 1, false, 0, 0,
            [HandshakeReport(ack: true, busy: true, error: false)], [], null, QualityDisposition: "OK");
        Assert.True(DeviceSideEffectClassifier.RequiresManualReconciliation(workflow, stillBusy));

        // 对照：握手出错 ⇒ 必须要求核对。
        var errored = new WorkflowRunResult("run-n02-error", true, 1, false, 0, 0,
            [HandshakeReport(ack: false, busy: false, error: true)], [], null, QualityDisposition: "OK");
        Assert.True(DeviceSideEffectClassifier.RequiresManualReconciliation(workflow, errored));
    }

    /// <summary>N02：构造带手势快照摘要的机器人节点报告。</summary>
    private static NodeRunReport HandshakeReport(bool ack, bool busy, bool error)
        => new("r1", "robot.executeTarget", true, 1,
            new Dictionary<string, object?>
            {
                ["handshake"] = new RobotHandshakeSignals(
                    TargetReady: false, Execute: false, Busy: busy, Complete: false,
                    Error: error, Ack: ack, CommandId: 1, ErrorCode: null, UpdatedAt: DateTimeOffset.UtcNow)
            },
            Phase: NodeExecutionPhase.Run);

    [Fact]
    public void RuntimeOnlyPolicy_AllowsDeviceActionReconciliationEndpoints()
    {
        // C04 回归：正式 RuntimeOnly 模式必须允许受管理员认证 + 审计的设备动作核对与清单读取——
        // 否则 Unknown 只能先 resolve、而 resolve 被模式白名单拒绝（探针：403 runtime_only），
        // 故障恢复在真实交付模式下形成死路。
        Assert.True(RuntimeOnlyPolicy.IsMutationAllowed("/api/production/device-actions/resolve", "POST"));
        Assert.True(RuntimeOnlyPolicy.IsMutationAllowed("/api/production/device-actions/pending", "GET"));
        Assert.False(RuntimeOnlyPolicy.IsMutationAllowed("/api/workflows", "POST")); // 工程编辑接口仍受限
    }

    private static WorkflowDefinition RobotWorkflow(string action, bool waitForInPosition)
        => new(
            "c01-probe",
            "C01 Probe",
            [new NodeDefinition("r1", "robot.executeTarget", "Send", null, new Dictionary<string, JsonElement>
            {
                ["action"] = JsonSerializer.SerializeToElement(action),
                ["waitForInPosition"] = JsonSerializer.SerializeToElement(waitForInPosition)
            })],
            []);

    private static WorkflowRunResult RobotResult(string runId, string? state, bool? inPosition)
    {
        var outputs = new Dictionary<string, VisionValueSnapshot>();
        if (inPosition is { } position)
            outputs["inPosition"] = new VisionValueSnapshot("Boolean", position ? "True" : "False", position);
        if (state is not null)
            outputs["state"] = new VisionValueSnapshot("String", state, state);
        return new WorkflowRunResult(runId, true, 1, false, 0, 0,
            [new NodeRunReport("r1", "robot.executeTarget", true, 1, new Dictionary<string, object?>(),
                Phase: NodeExecutionPhase.Run,
                Outputs: outputs)],
            [], null, QualityDisposition: "OK");
    }

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

    [Fact]
    public async Task IntentStateTransitionFailure_EngagesInProcessLock_AndBlocksFurtherActions()
    {
        // Fix1 回归：意图状态变更（MarkUnknown/Complete）落盘失败时，只记日志会让同进程继续放行动作。
        // 现在必须置**同进程故障锁**：本进程所有动作入口立即拒绝，直到重启或管理员核对。
        using var factory = new StorageVisionStudioApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ManualActionIntentStore>(sp =>
                new MarkUnknownFailingStore(
                    sp.GetRequiredService<IWebHostEnvironment>(),
                    sp.GetRequiredService<IConfiguration>(),
                    NullLogger<ManualActionIntentStore>.Instance))));
        using var client = factory.CreateClient();

        // 触发一次"登记成功、随后失败"的动作：写入不存在的标签 → 异常 → Dispose → MarkUnknown 失败 → 故障锁。
        var failing = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags/NONEXISTENT",
            new { value = "probe" });
        Assert.True((int)failing.StatusCode >= 400, $"expected failure, got {(int)failing.StatusCode}");

        // 同进程故障锁生效：下一次动作必须被拒（旧实现：200）。
        var blocked = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var body = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("in-process safety-state failure lock", body);

        // health 同步表达。
        var health = await client.GetAsync("/api/health");
        var healthBody = await health.Content.ReadAsStringAsync();
        Assert.Contains("in-process device-action failure lock", healthBody);
        Assert.Contains("\"productionReady\":false", healthBody);
    }

    [Fact]
    public async Task NonBlockingHandshake_DoesNotClearIntent()
    {
        // Fix2 回归：非等待握手会返回 Completed=false（机器人仍 Busy）——"指令已受理"不等于动作完成，
        // 意图必须保留为 Unknown（旧实现无条件 Complete，直接把未确认动作标记成已完成）。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();

        var handshake = await client.PostAsJsonAsync("/api/robots/virtual-abb-1/target", new
        {
            x = 10,
            y = 20,
            rDeg = 0,
            frame = "RobotBase",
            unit = "mm",
            robot = "virtual-abb-1",
            guidanceMode = "Manual",
            action = "Handshake",
            waitForInPosition = false,
            maxRetries = 0,
            retryDelayMs = 0,
            autoAck = false,
            timeoutMs = 200
        });
        Assert.True(handshake.IsSuccessStatusCode, await handshake.Content.ReadAsStringAsync());
        var handshakeBody = await handshake.Content.ReadAsStringAsync();
        var completed = handshakeBody.Contains("\"completed\":true");
        var busy = handshakeBody.Contains("\"busy\":true");

        var pending = await client.GetAsync("/api/production/device-actions/pending");
        var pendingBody = await pending.Content.ReadAsStringAsync();
        if (!completed || busy)
            Assert.Contains("robot.command", pendingBody);   // 未确认 ⇒ 意图保留、后续动作被阻断
    }

    [Fact]
    public async Task ManualResolution_RetainsEvidenceBeforeClearing_AndRefusesWhenEvidenceCannotPersist()
    {
        // Fix3 回归：核对证据必须**先耐久落盘、后解除意图**，且保存证据全文（不只是审计元数据）。
        using var factory = new StorageVisionStudioApiFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ManualActionIntentStore>();
        store.Add(new ManualActionIntent(
            "legacy-evidence", "device.writeTag", ["virtual-modbus-1"], [], [],
            DateTimeOffset.UtcNow, ProcessId: "previous-process"));

        var resolved = await client.PostAsJsonAsync("/api/production/device-actions/resolve", new
        {
            runId = "",
            manifestHash = "",
            deviceIds = Array.Empty<string>(),
            robotIds = Array.Empty<string>(),
            reason = "现场核对完成",
            evidence = "PLC 触发位已复位为 0，机器人回到安全位",
            deviceIdentityEvidence = "铭牌 SN-12345，端点 192.168.0.10 一致",
            manualIntentId = "legacy-evidence"
        });
        Assert.True(resolved.IsSuccessStatusCode, await resolved.Content.ReadAsStringAsync());

        // 证据文件存在且保存了全文与操作者信息。
        var evidenceFiles = Directory.GetFiles(store.ResolutionDirectory, "*.json");
        Assert.NotEmpty(evidenceFiles);
        var evidence = await File.ReadAllTextAsync(evidenceFiles.OrderBy(x => x).Last());
        Assert.Contains("PLC 触发位已复位为 0", evidence);
        Assert.Contains("铭牌 SN-12345", evidence);
        Assert.Contains("legacy-evidence", evidence);
        Assert.Contains("originalState", evidence);
        Assert.Empty(store.Load()); // 证据落盘成功后意图才被解除（台账保留为空）

        // 存证失败（模拟磁盘故障）⇒ 拒绝解除：意图必须保留。
        using var failing = new StorageVisionStudioApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ManualActionIntentStore>(sp =>
                new ResolutionSaveFailingStore(
                    sp.GetRequiredService<IWebHostEnvironment>(),
                    sp.GetRequiredService<IConfiguration>(),
                    NullLogger<ManualActionIntentStore>.Instance))));
        using var failingClient = failing.CreateClient();
        var failingStore = failing.Services.GetRequiredService<ManualActionIntentStore>();
        failingStore.Add(new ManualActionIntent(
            "legacy-evidence-2", "device.writeTag", ["virtual-modbus-1"], [], [],
            DateTimeOffset.UtcNow, ProcessId: "previous-process"));

        var refused = await failingClient.PostAsJsonAsync("/api/production/device-actions/resolve", new
        {
            runId = "",
            manifestHash = "",
            deviceIds = Array.Empty<string>(),
            robotIds = Array.Empty<string>(),
            reason = "现场核对完成",
            evidence = "evidence",
            deviceIdentityEvidence = "identity",
            manualIntentId = "legacy-evidence-2"
        });
        Assert.True((int)refused.StatusCode >= 400, $"expected refusal, got {(int)refused.StatusCode}");
        Assert.True(failingStore.Load().Any(x => x.RunId == "legacy-evidence-2"), "存证失败时意图绝不能被解除");
    }

    /// <summary>模拟"意图文件不可写"：登记可成功，但状态变更（MarkUnknown）落盘失败。</summary>
    private sealed class MarkUnknownFailingStore(IWebHostEnvironment env, IConfiguration configuration, ILogger<ManualActionIntentStore> logger)
        : ManualActionIntentStore(env, configuration, logger)
    {
        public override void MarkUnknown(string runId, string reason)
            => throw new IOException("injected: intent file is locked");
    }

    /// <summary>模拟"核对证据无法落盘"。</summary>
    private sealed class ResolutionSaveFailingStore(IWebHostEnvironment env, IConfiguration configuration, ILogger<ManualActionIntentStore> logger)
        : ManualActionIntentStore(env, configuration, logger)
    {
        public override void SaveResolution(ManualActionResolution resolution)
            => throw new IOException("injected: resolution evidence could not be persisted");
    }

    /// <summary>A04：模拟"意图台账只读"——证据可落盘，但台账闭合（Remove）失败。</summary>
    private sealed class RemoveFailingStore(IWebHostEnvironment env, IConfiguration configuration, ILogger<ManualActionIntentStore> logger)
        : ManualActionIntentStore(env, configuration, logger)
    {
        public override void Remove(string runId)
            => throw new IOException("injected: intent ledger is read-only");
    }

    [Fact]
    public async Task MultipleSettlementFailures_KeepIndependentLocks_EachResolveClosesOnlyItsOwn()
    {
        // A01 回归（2026-10-11 复审）：两个动作状态提交失败时，旧实现只保留第一把故障锁——
        // 核对第一个后唯一锁清空、系统放行，而第二条仍为本进程 Issued 被过滤出待核对清单
        // （探针：failureLock=False secondState=Issued unresolved=0 nextAction=200）。
        // 现在逐意图独立登记：一个核对只闭合**对应**失败，最后一个未闭合故障消除前新动作一直被拒。
        using var factory = new StorageVisionStudioApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ManualActionIntentStore>(sp =>
                new MarkUnknownFailingStore(
                    sp.GetRequiredService<IWebHostEnvironment>(),
                    sp.GetRequiredService<IConfiguration>(),
                    NullLogger<ManualActionIntentStore>.Instance))));
        using var client = factory.CreateClient();
        var auth = factory.Services.GetRequiredService<DeviceActionAuthorizationService>();

        // 两个动作都在故障发生前登记；随后各自的状态提交（MarkUnknown）落盘失败 ⇒ 两把独立故障锁。
        var first = auth.BeginManualIntent("probe.first-failed", ["virtual-modbus-1"], []);
        var second = auth.BeginManualIntent("probe.second-failed", ["virtual-abb-1"], []);
        first.Dispose();
        second.Dispose();
        Assert.Equal(2, auth.StateTransitionFailures.Count);

        // 待核对清单必须同时列出两条（旧实现第二条被过滤：unresolved=0）。
        var pending = await client.GetAsync("/api/production/device-actions/pending");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingBody = await pending.Content.ReadAsStringAsync();
        Assert.Contains(first.RunId, pendingBody);
        Assert.Contains(second.RunId, pendingBody);

        // 核对第一条 ⇒ 只闭合对应失败；第二条仍然阻断新动作。
        var resolveFirst = await PostResolveAsync(client, first.RunId);
        Assert.True(resolveFirst.IsSuccessStatusCode, await resolveFirst.Content.ReadAsStringAsync());
        Assert.Single(auth.StateTransitionFailures);
        var stillBlocked = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, stillBlocked.StatusCode); // 旧实现：200 放行

        // 核对第二条 ⇒ 最后一把锁清除，动作入口恢复。
        var resolveSecond = await PostResolveAsync(client, second.RunId);
        Assert.True(resolveSecond.IsSuccessStatusCode, await resolveSecond.Content.ReadAsStringAsync());
        Assert.Empty(auth.StateTransitionFailures);
        var allowed = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe2" } });
        Assert.NotEqual(HttpStatusCode.Conflict, allowed.StatusCode);
    }

    [Fact]
    public async Task ResolutionLedgerFailure_DoesNotReportSuccess_AndStartupAppliesDurableEvidence()
    {
        // A04 回归（2026-10-11 复审）：核对证据先落盘正确，但台账删除失败时旧实现仅记警告、
        // 仍清内存锁并返回成功——进程内显示已恢复且放行，重启后同一记录又要求核对
        // （探针：resolve-remove=200 nextAction=200，重启后 nextAction=409）。
        // 现在：闭合持久化失败 ⇒ 不返回"完成恢复"；启动时一致识别耐久证据，重启后不再重复要求核对。
        using var factory = new StorageVisionStudioApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ManualActionIntentStore>(sp =>
                new RemoveFailingStore(
                    sp.GetRequiredService<IWebHostEnvironment>(),
                    sp.GetRequiredService<IConfiguration>(),
                    NullLogger<ManualActionIntentStore>.Instance))));
        using var client = factory.CreateClient();
        var store = (RemoveFailingStore)factory.Services.GetRequiredService<ManualActionIntentStore>();
        store.Add(new ManualActionIntent(
            "resolve-remove-probe", "device.writeTag", ["virtual-modbus-1"], [], [],
            DateTimeOffset.UtcNow, ProcessId: "previous-process"));

        var resolve = await PostResolveAsync(client, "resolve-remove-probe");
        Assert.True((int)resolve.StatusCode >= 400, $"expected failure, got {(int)resolve.StatusCode}"); // 旧实现：200

        // 证据已耐久保存，但闭合未应用 ⇒ 动作继续阻断（旧实现：放行）。
        Assert.NotEmpty(Directory.GetFiles(store.ResolutionDirectory, "*.json"));
        var blocked = await client.PutAsJsonAsync(
            "/api/devices/virtual-modbus-1/tags",
            new { values = new Dictionary<string, object?> { ["statusText"] = "probe" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        // "重启"：新服务实例读同一台账/证据 ⇒ 启动时一致识别为已核对，不再重新要求核对（旧实现：409）。
        var restarted = new DeviceActionAuthorizationService(
            factory.Services.GetRequiredService<DeviceActionSafetyStore>(),
            store,
            factory.Services.GetRequiredService<RuntimeDependencyManifestService>(),
            NullLogger<DeviceActionAuthorizationService>.Instance);
        Assert.Empty(restarted.UnresolvedManualIntents());
        restarted.EnsureActionsAllowed("probe.after-restart"); // 不得抛出

        // 重复核对幂等：返回既有核对结果，而不是报"不存在"或再次失败。
        var again = restarted.ResolveManualIntent(
            "resolve-remove-probe", "probe-operator", "checked", "checked", "checked");
        Assert.Equal("resolve-remove-probe", again.RunId);
    }

    private static Task<HttpResponseMessage> PostResolveAsync(HttpClient client, string manualIntentId)
        => client.PostAsJsonAsync("/api/production/device-actions/resolve", new
        {
            runId = "",
            manifestHash = "",
            deviceIds = Array.Empty<string>(),
            robotIds = Array.Empty<string>(),
            reason = "probe: verified on site",
            evidence = "probe: device state checked",
            deviceIdentityEvidence = "probe: identity checked",
            manualIntentId
        });

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
