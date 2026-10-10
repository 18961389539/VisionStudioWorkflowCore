using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>R01/F02：一条手动/临时运行的设备动作意图（耐久登记，进程崩溃后仍可追溯）。</summary>
/// <param name="State">
/// F02：意图生命周期。Issued = 动作已下发、结果未确认（同进程视为活跃，其它进程视为遗留）；
/// Unknown = 结果未确认或失败（**任何进程**都必须先核对才能继续动作）。
/// </param>
public sealed record ManualActionIntent(
    string RunId,
    string Action,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    IReadOnlyList<string> CameraIds,
    DateTimeOffset SetAt,
    string? ProcessId,
    ManualActionIntentState State = ManualActionIntentState.Issued,
    string? ClosedReason = null,
    DateTimeOffset? ClosedAt = null);

public enum ManualActionIntentState
{
    /// <summary>动作已下发但结果尚未确认；同进程内的活跃意图不阻断其它入口（并发由租约仲裁）。</summary>
    Issued = 0,
    /// <summary>F02：结果未确认（失败/取消/异步未完成/进程中断）——必须核对，任何进程都阻断。</summary>
    Unknown = 1
}

/// <summary>
/// R01/F02：手动/临时运行动作意图的持久化存储（独立文件、原子写、显式格式版本）。
///
/// 与生产安全状态（device-action-safety.json，单条语义）分开保存，避免手动动作与生产周期
/// 互相覆盖对方的意图；两者都由 <see cref="DeviceActionAuthorizationService"/> 统一读取。
///
/// 语义约束（F02 加固）：
/// · 空文件 / JSON null / 结构缺字段 ⇒ 抛异常（调用方按"未闭合"处理，保守阻断），
///   绝不当作"没有意图"（此前 null 被解析为"无意图"，是探针确认的绕过路径）；
/// · 写入使用 FileStream + Flush(true)（WriteThrough），并为文件加格式版本；
/// · 读取兼容历史裸数组格式。
/// </summary>
public sealed class ManualActionIntentStore
{
    private const int FormatVersion = 1;
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
                var text = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("the intent file is empty");
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Null)
                    throw new InvalidOperationException("the intent file contains JSON null");
                var list = root.ValueKind switch
                {
                    JsonValueKind.Array => JsonSerializer.Deserialize<List<ManualActionIntent>>(text, Json) ?? throw new InvalidOperationException("array payload deserialized to null"),
                    JsonValueKind.Object when root.TryGetProperty("intents", out _) => DeserializeWrapped(root) ?? throw new InvalidOperationException("wrapped payload has no intents array"),
                    _ => throw new InvalidOperationException($"unexpected JSON payload kind '{root.ValueKind}'")
                };
                foreach (var intent in list)
                {
                    if (string.IsNullOrWhiteSpace(intent.RunId) || string.IsNullOrWhiteSpace(intent.Action))
                        throw new InvalidOperationException("an intent entry is missing required fields (runId/action)");
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual action intent file at {Path} could not be read; device actions stay blocked until it is resolved.", _path);
                throw new InvalidOperationException($"The manual action intent file could not be read ({ex.Message}).", ex);
            }
        }
    }

    private static List<ManualActionIntent>? DeserializeWrapped(JsonElement root)
    {
        if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var value) || value < 1)
            throw new InvalidOperationException("the intent file has no valid formatVersion");
        return root.GetProperty("intents").Deserialize<List<ManualActionIntent>>(Json);
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

    /// <summary>F02：把意图标记为"结果未确认"——比删除更保守，重启/同进程都会继续阻断。</summary>
    public void MarkUnknown(string runId, string reason)
    {
        lock (_sync)
        {
            var list = Load().ToList();
            var index = list.FindIndex(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));
            if (index < 0) return;
            list[index] = list[index] with
            {
                State = ManualActionIntentState.Unknown,
                ClosedReason = reason,
                ClosedAt = DateTimeOffset.UtcNow
            };
            Write(list);
        }
    }

    /// <summary>F02：耐久写入（WriteThrough）——动作前记录必须在断电/崩溃前确定落盘。</summary>
    private void Write(List<ManualActionIntent> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = FormatVersion, intents = list }, Json);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(true);
        }
        File.Move(temp, _path, true);
    }
}

/// <summary>
/// R01/F02：统一的设备动作授权闸门 + 动作意图生命周期。
///
/// 生产周期有自己的意图/核对机制，但手动写入、机器人指令、相机触发、临时/调试/已发布作业
/// 运行此前只做资源仲裁——失败后保留的 Unknown、或崩溃遗留的动作意图不会阻断这些入口，
/// 用户可以在未核对上一动作的情况下继续下发新的物理动作。
///
/// 本服务把"未闭合的安全意图"变成**所有**设备动作入口的共同契约：
///   · 生产状态 Unknown / IntentRecorded 来自更早进程 ⇒ 阻断；
///   · 手动意图 State=Unknown（**任何进程**）或 Issued 来自更早进程 ⇒ 阻断；
///   · 本进程仍在执行（Issued）的意图不阻断其它入口，并发由设备租约仲裁。
/// 安全停止、断开、只读诊断**不经过**本闸门。
///
/// F02：动作句柄是显式生命周期——<see cref="DeviceActionHandle.Complete"/> 只在动作结果确认
/// 且必要证据已持久化后调用；失败/取消/未确认（含调用方忘记 Complete）保留为 Unknown，
/// 绝不会在 using 收尾时被静默清除。
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

    /// <summary>所有未闭合的手动意图（Unknown 任意进程 + Issued 来自更早进程）——用于 pending/health/闸门。</summary>
    public IReadOnlyList<ManualActionIntent> UnresolvedManualIntents()
        => manualIntents.Load().Where(IsUnresolved).ToArray();

    /// <summary>全部在册意图（含本进程活跃）——供运维查看当前有哪些动作在飞行中。</summary>
    public IReadOnlyList<ManualActionIntent> AllManualIntents() => manualIntents.Load();

    private static bool IsUnresolved(ManualActionIntent intent)
        => intent.State == ManualActionIntentState.Unknown ||
           !string.Equals(intent.ProcessId, DeviceActionSafetyStore.CurrentProcessId, StringComparison.Ordinal);

    /// <summary>手动/临时/调试/作业运行入口的闸门：存在未闭合意图时抛出 409。安全停止/只读路径不得调用。</summary>
    public void EnsureActionsAllowed(string action)
    {
        if (UnresolvedProductionState() is { } state)
            Block(action, $"an unresolved production device-action intent exists ({state.Phase}: {state.Reason})");

        IReadOnlyList<ManualActionIntent> unresolved;
        try { unresolved = UnresolvedManualIntents(); }
        catch (Exception ex) { Block(action, $"the manual action intent file could not be read ({ex.Message})"); return; }

        if (unresolved.Count > 0)
            Block(action, $"an unresolved manual device action exists " +
                $"({string.Join("; ", unresolved.Take(3).Select(x => $"{x.Action} [{x.State}] {x.ClosedReason ?? "awaiting reconciliation"}"))})");
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
    /// F02：动作前耐久登记手动意图。登记失败会抛出（调用方绝不能执行设备动作）。
    /// 返回的句柄必须显式 Complete（成功且证据已提交）或 AbortBeforeIssue（未下发）；
    /// 其余情况（失败/取消/忘记确认）在 Dispose 时保留为 Unknown 并阻断后续动作。
    /// </summary>
    public DeviceActionHandle BeginManualIntent(
        string action,
        IReadOnlyList<string> deviceIds,
        IReadOnlyList<string> robotIds,
        IReadOnlyList<string>? cameraIds = null)
    {
        EnsureActionsAllowed(action);
        return RecordIntent(action, deviceIds, robotIds, cameraIds ?? []);
    }

    /// <summary>F02：临时/调试/作业运行的意图——资源清单来自工作流的运行依赖捕获。</summary>
    public async Task<DeviceActionHandle> BeginWorkflowIntentAsync(string action, WorkflowDefinition workflow, CancellationToken ct)
    {
        EnsureActionsAllowed(action);
        var manifest = await dependencies.CaptureAsync(workflow, ct);
        return RecordIntent(
            action,
            manifest.Devices.Select(x => x.Id).ToArray(),
            manifest.Robots.Select(x => x.Id).ToArray(),
            manifest.Cameras.Select(x => x.Id).ToArray());
    }

    /// <summary>
    /// F03：管理员核对后精确清除一条**未闭合**的手动/临时运行动作意图。
    /// 正在本进程执行（Issued）的意图不得被清除——那会把"动作仍在飞行"当成"已核对"。
    /// 证据必填由调用端（受管理员认证的端点）校验并写入审计。
    /// </summary>
    public ManualActionIntent ResolveManualIntent(string runId)
    {
        var target = manualIntents.Load().FirstOrDefault(x => string.Equals(x.RunId, runId, StringComparison.Ordinal))
            ?? throw new ApiNotFoundException($"Manual action intent '{runId}' does not exist.");
        if (!IsUnresolved(target))
            throw new ApiConflictException(
                $"Manual action intent '{runId}' ({target.Action}) is still active in this process; wait for it to finish or stop it before reconciling.");
        manualIntents.Remove(runId);
        logger.LogWarning(
            "Manual action intent {RunId} ({Action}) was reconciled and cleared by an administrator.", runId, target.Action);
        return target;
    }

    private DeviceActionHandle RecordIntent(string action, IReadOnlyList<string> deviceIds, IReadOnlyList<string> robotIds, IReadOnlyList<string> cameraIds)
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
        return new DeviceActionHandle(manualIntents, logger, runId, action);
    }

    private void Block(string action, string reason)
    {
        logger.LogWarning("Device action '{Action}' blocked: {Reason}.", action, reason);
        throw new ApiConflictException(
            $"Device actions are blocked: {reason}. Verify the physical device state and resolve it via " +
            "'POST /api/production/device-actions/resolve' (manual intents are listed by 'GET /api/production/device-actions/pending') before issuing new actions.");
    }
}

/// <summary>
/// F02：手动动作的显式生命周期句柄。
///   · <see cref="Complete"/>：动作结果已确认且必要证据已提交 ⇒ 清除意图；
///   · <see cref="AbortBeforeIssue"/>：动作未下发（例如闸门后校验失败）⇒ 安全清除；
///   · <see cref="MarkUnknown"/> / <see cref="Dispose"/>（未确认）：保留意图并标记 Unknown ⇒
///     同进程与重启后都阻断，直到管理员核对。
/// </summary>
public sealed class DeviceActionHandle : IDisposable
{
    private readonly ManualActionIntentStore _store;
    private readonly ILogger _logger;
    private readonly string _runId;
    private readonly string _action;
    private int _settled;
    private int _disposed;

    internal DeviceActionHandle(ManualActionIntentStore store, ILogger logger, string runId, string action)
    {
        _store = store;
        _logger = logger;
        _runId = runId;
        _action = action;
    }

    public string RunId => _runId;

    /// <summary>动作已确认成功且必要证据（追溯/审计）已持久化。</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return;
        try { _store.Remove(_runId); }
        catch (Exception ex)
        {
            // 清除失败 ⇒ 意图残留：重启后阻断（保守方向）。
            _logger.LogError(ex, "Manual action intent for '{Action}' ({RunId}) could not be cleared; it will block new actions after a restart.", _action, _runId);
        }
    }

    /// <summary>动作尚未下发（参数校验失败、闸门后中止）：安全清除。</summary>
    public void AbortBeforeIssue() => Complete();

    /// <summary>失败 / 取消 / 异步未确认 / 追溯提交失败：保留意图并标记 Unknown。</summary>
    public void MarkUnknown(string reason)
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return;
        try { _store.MarkUnknown(_runId, reason); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual action intent for '{Action}' ({RunId}) could not be marked as unknown; it stays registered.", _action, _runId);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Volatile.Read(ref _settled) != 0) return;
        // 调用方没有显式确认结果：绝不能假定成功——保留意图并标记 Unknown（F02 的核心约束）。
        _settled = 1;
        try { _store.MarkUnknown(_runId, $"action '{_action}' ended without an explicit completion confirmation"); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual action intent for '{Action}' ({RunId}) could not be marked as unknown on dispose; it stays registered as issued.", _action, _runId);
        }
    }
}

/// <summary>
/// R01/扩展治理：设备副作用节点的单一分类来源。
/// 生产运行时与临时/调试/作业运行必须用同一份判定——此前只存在于生产循环内部，
/// 其它入口无法据它决定"起始证据写失败时不得盲跑"或"是否需要动作授权"。
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
