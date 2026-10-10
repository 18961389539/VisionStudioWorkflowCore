using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>R01：一条手动/临时运行的设备动作意图（耐久登记，进程崩溃后仍可追溯）。</summary>
public sealed record ManualActionIntent(
    string RunId,
    string Action,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    IReadOnlyList<string> CameraIds,
    DateTimeOffset SetAt,
    string? ProcessId);

/// <summary>
/// R01：手动/临时运行动作意图的持久化存储（独立文件，数组结构，原子写）。
///
/// 与生产安全状态（device-action-safety.json，单条语义）分开保存，避免手动动作与生产周期
/// 互相覆盖对方的意图；两者都由 <see cref="DeviceActionAuthorizationService"/> 统一读取。
///
/// 语义约束：文件不可读 ⇒ 抛异常（调用方按"未闭合"处理，保守阻断），绝不当作"没有意图"。
/// </summary>
public sealed class ManualActionIntentStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly ILogger<ManualActionIntentStore> _logger;
    private readonly object _sync = new();

    public ManualActionIntentStore(IWebHostEnvironment env, IConfiguration configuration, ILogger<ManualActionIntentStore> logger)
    {
        var configured = configuration.GetValue<string>("Production:ManualActionIntentFile");
        _path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "production", "manual-action-intents.json")
            : configured;
        _logger = logger;
    }

    public string StatePath => _path;

    public IReadOnlyList<ManualActionIntent> Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return [];
            try
            {
                var list = JsonSerializer.Deserialize<List<ManualActionIntent>>(File.ReadAllText(_path), Json);
                return list ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual action intent file at {Path} could not be read; device actions stay blocked until it is resolved.", _path);
                throw new InvalidOperationException($"The manual action intent file could not be read ({ex.Message}).", ex);
            }
        }
    }

    public void Add(ManualActionIntent intent)
    {
        lock (_sync)
        {
            var list = Load().ToList();
            list.Add(intent);
            Write(list);
        }
    }

    public void Remove(string runId)
    {
        lock (_sync)
        {
            var list = Load().Where(x => !string.Equals(x.RunId, runId, StringComparison.Ordinal)).ToList();
            Write(list);
        }
    }

    private void Write(List<ManualActionIntent> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, _path, true);
    }
}

/// <summary>
/// R01：统一的设备动作授权闸门。
///
/// 生产周期有自己的意图/核对机制，但手动写入、机器人指令、相机触发与临时运行此前只做资源
/// 仲裁——失败后保留的 Unknown、或崩溃遗留的动作意图不会阻断这些入口，用户可以在未核对
/// 上一动作的情况下继续下发新的物理动作。
///
/// 本服务把"未闭合的安全意图"变成**所有**设备动作入口的共同契约：
///   · 生产状态 Unknown（副作用失败后保留）→ 一律阻断，直到管理员核对；
///   · 生产状态 IntentRecorded 且来自更早的进程 → 崩溃遗留 → 阻断；
///   · 手动意图来自更早进程 → 崩溃遗留 → 阻断；
///   · 本进程活跃的意图（生产周期运行中 / 正在进行的手动动作）→ 不阻断其它入口，
///     并发由设备租约仲裁处理。
/// 安全停止、断开、只读诊断**不经过**本闸门（报告关闭条件：安全停止必须保持可达）。
/// </summary>
public sealed class DeviceActionAuthorizationService(
    DeviceActionSafetyStore safety,
    ManualActionIntentStore manualIntents,
    RuntimeDependencyManifestService dependencies,
    ILogger<DeviceActionAuthorizationService> logger)
{
    /// <summary>生产安全状态是否仍未闭合（跨进程遗留或 Unknown / 不可读）。</summary>
    public DeviceActionSafetyState? UnresolvedProductionState()
    {
        var state = safety.Load();
        return state is not null && DeviceActionSafetyStore.IsUnresolved(state) ? state : null;
    }

    /// <summary>崩溃遗留（来自更早进程）的手动动作意图。</summary>
    public IReadOnlyList<ManualActionIntent> StaleManualIntents()
        => manualIntents.Load()
            .Where(x => !string.Equals(x.ProcessId, DeviceActionSafetyStore.CurrentProcessId, StringComparison.Ordinal))
            .ToArray();

    /// <summary>手动/临时运行入口的闸门：存在未闭合意图时抛出 409。安全停止/只读路径不得调用。</summary>
    public void EnsureActionsAllowed(string action)
    {
        if (UnresolvedProductionState() is { } state)
            Block(action, $"an unresolved production device-action intent exists ({state.Phase}: {state.Reason})");

        IReadOnlyList<ManualActionIntent> stale;
        try { stale = StaleManualIntents(); }
        catch (Exception ex) { Block(action, $"the manual action intent file could not be read ({ex.Message})"); return; }

        if (stale.Count > 0)
            Block(action, $"a previous process left {stale.Count} unresolved manual action(s): {string.Join("; ", stale.Select(x => x.Action).Take(3))}");
    }

    /// <summary>生产 Start/Recover 的附加闸门：任何手动意图（含本进程活跃）都阻断启动。</summary>
    public void EnsureProductionStartAllowed()
    {
        IReadOnlyList<ManualActionIntent> intents;
        try { intents = manualIntents.Load(); }
        catch (Exception ex)
        {
            throw new ApiConflictException($"Production start blocked: the manual action intent file could not be read ({ex.Message}).");
        }
        if (intents.Count > 0)
            throw new ApiConflictException(
                $"Production start blocked: {intents.Count} manual device action(s) are in flight or unresolved: " +
                string.Join("; ", intents.Select(x => x.Action).Take(3)) + ".");
    }

    /// <summary>
    /// R01：动作前耐久登记手动意图。登记失败会抛出（调用方绝不能执行设备动作）。
    /// 返回的句柄在动作完成/失败时解除；进程被杀则意图残留并在重启后阻断所有入口。
    /// </summary>
    public IDisposable BeginManualIntent(
        string action,
        IReadOnlyList<string> deviceIds,
        IReadOnlyList<string> robotIds,
        IReadOnlyList<string>? cameraIds = null)
    {
        EnsureActionsAllowed(action);
        return RecordIntent(action, deviceIds, robotIds, cameraIds ?? []);
    }

    /// <summary>R01：临时/调试运行的意图——资源清单来自工作流的运行依赖捕获。</summary>
    public async Task<IDisposable> BeginWorkflowIntentAsync(string action, WorkflowDefinition workflow, CancellationToken ct)
    {
        EnsureActionsAllowed(action);
        var manifest = await dependencies.CaptureAsync(workflow, ct);
        return RecordIntent(
            action,
            manifest.Devices.Select(x => x.Id).ToArray(),
            manifest.Robots.Select(x => x.Id).ToArray(),
            manifest.Cameras.Select(x => x.Id).ToArray());
    }

    private IDisposable RecordIntent(string action, IReadOnlyList<string> deviceIds, IReadOnlyList<string> robotIds, IReadOnlyList<string> cameraIds)
    {
        var runId = "manual-" + Guid.NewGuid().ToString("N");
        try
        {
            manualIntents.Add(new ManualActionIntent(
                runId, action, deviceIds, robotIds, cameraIds,
                DateTimeOffset.UtcNow, DeviceActionSafetyStore.CurrentProcessId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual action intent for '{Action}' could not be persisted; refusing to execute the action.", action);
            throw new ApiUnavailableException(
                $"Refusing to execute device action '{action}': its safety intent could not be persisted ({ex.Message}). Resolve the storage problem first.");
        }
        return new ManualIntentHandle(manualIntents, logger, runId, action);
    }

    private void Block(string action, string reason)
    {
        logger.LogWarning("Device action '{Action}' blocked: {Reason}.", action, reason);
        throw new ApiConflictException(
            $"Device actions are blocked: {reason}. Verify the physical device state and resolve it via " +
            "'POST /api/production/device-actions/resolve' before issuing new actions.");
    }

    private sealed class ManualIntentHandle(
        ManualActionIntentStore store,
        ILogger logger,
        string runId,
        string action) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { store.Remove(runId); }
            catch (Exception ex)
            {
                // 清除失败 ⇒ 意图残留：重启后会阻断所有动作入口（保守方向），必须留痕。
                logger.LogError(ex, "Manual action intent for '{Action}' ({RunId}) could not be cleared; it will block new actions after a restart.", action, runId);
            }
        }
    }
}

/// <summary>
/// R01/扩展治理：设备副作用节点的单一分类来源。
/// 生产运行时与临时/调试运行必须用同一份判定——此前只存在于生产循环内部，
/// 临时运行无法据它决定"起始证据写失败时不得盲跑"。
/// </summary>
public static class DeviceSideEffectClassifier
{
    private static readonly HashSet<string> SideEffectNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "device.writeTag",
        "device.writeVisionResult",
        "robot.executeTarget"
    };

    /// <summary>该工作流是否包含会驱动物理设备的节点。</summary>
    public static bool HasDeviceSideEffects(WorkflowDefinition workflow)
        => workflow.Nodes.Any(x => SideEffectNodeTypes.Contains(x.Type));

    public static bool IsDeviceSideEffectNode(string nodeType) => SideEffectNodeTypes.Contains(nodeType);
}
