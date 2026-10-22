using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api;

/// <summary>
/// F03/Fix3：一次管理员核对的**耐久证据**——被核对意图的完整快照 + 操作者 + 现场与身份证据全文。
/// 只在证据落盘成功后才允许解除意图（顺序不可颠倒，否则"先解除、后写审计"会留下无证据的放行）。
/// </summary>
public sealed record ManualActionResolution(
    string RunId,
    string Action,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    IReadOnlyList<string> CameraIds,
    string? OriginalProcessId,
    ManualActionIntentState OriginalState,
    string? OriginalClosedReason,
    string OperatorName,
    string Reason,
    string Evidence,
    string DeviceIdentityEvidence,
    DateTimeOffset ResolvedAt,
    /// <summary>N07：被核对意图关联的真实执行标识（运行 RunId / 调试会话 ID）——核对记录必须能定位原执行。</summary>
    string? RelatedRunId = null);

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
    DateTimeOffset? ClosedAt = null,
    /// <summary>C07：关联的真实执行标识（运行 RunId / 调试会话 ID）——核对记录必须能定位原执行。</summary>
    string? RelatedRunId = null);

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
public class ManualActionIntentStore
{
    private const int FormatVersion = 1;
    // 审计/台账文件必须**人类可读**：默认编码器会把中文证据转义成 \uXXXX，现场导出后无法直接阅读。
    // UnsafeRelaxedJsonEscaping 只用于落盘 JSON（非 HTML 上下文），保留 UTF-8 明文。
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
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

    /// <summary>F03：核对证据目录（与被核对的意图台账同根，便于现场整体导出）。</summary>
    public string ResolutionDirectory => _path + ".resolutions";

    /// <summary>F03/Fix3：耐久保存核对证据（含被核对意图快照）——必须先于解除意图完成。</summary>
    public virtual void SaveResolution(ManualActionResolution resolution)
    {
        lock (_sync)
        {
            var directory = ResolutionDirectory;
            Directory.CreateDirectory(directory);
            var safeRunId = new string(resolution.RunId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            if (string.IsNullOrWhiteSpace(safeRunId)) safeRunId = "legacy";
            var path = Path.Combine(directory, $"{safeRunId}-{resolution.ResolvedAt:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, resolution, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, true);
        }
    }

    /// <summary>
    /// A04：读取全部**耐久核对证据**（ResolveDirectory 下的记录）。证据是核对闭合的耐久事实——
    /// 启动时据此一致识别已核对意图（台账行删除失败/进程被杀都不会让同一记录重新要求核对）。
    /// 无法解析的证据文件只告警跳过（保守方向：对应意图继续要求核对）。
    /// </summary>
    public virtual IReadOnlyList<ManualActionResolution> LoadResolutions()
    {
        lock (_sync)
        {
            var directory = ResolutionDirectory;
            if (!Directory.Exists(directory)) return [];
            var list = new List<ManualActionResolution>();
            foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    var resolution = JsonSerializer.Deserialize<ManualActionResolution>(File.ReadAllText(file), Json);
                    if (resolution is not null) list.Add(resolution);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Manual action resolution evidence file {Path} could not be read; its intent stays unresolved.", file);
                }
            }
            return list;
        }
    }

    /// <summary>A04：指定意图的最新耐久核对证据（重复核对的幂等依据）。</summary>
    public virtual ManualActionResolution? TryGetResolution(string runId)
        => LoadResolutions().LastOrDefault(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));

    public virtual IReadOnlyList<ManualActionIntent> Load()
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

    public virtual void Add(ManualActionIntent intent)
    {
        lock (_sync)
        {
            var list = Load().ToList();
            list.Add(intent);
            Write(list);
        }
    }

    public virtual void Remove(string runId)
    {
        lock (_sync)
        {
            var list = Load().Where(x => !string.Equals(x.RunId, runId, StringComparison.Ordinal)).ToList();
            Write(list);
        }
    }

    /// <summary>F02：把意图标记为"结果未确认"——比删除更保守，重启/同进程都会继续阻断。</summary>
    public virtual void MarkUnknown(string runId, string reason)
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
    /// <summary>
    /// A01：故障锁**逐意图独立登记**（RunId → 故障）。旧实现只保留第一个故障：两个动作同时
    /// 提交失败时，核对第一个后唯一锁清空，第二个仍为本进程 Issued 被过滤出待核对清单——
    /// 清单、故障锁与真实动作状态无法形成一致事实源。现在每个失败都有独立身份，
    /// 一次核对只能闭合**对应**失败；最后一个未闭合故障消除前新动作一直被拒绝。
    /// </summary>
    private readonly ConcurrentDictionary<string, IntentStateFailure> _stateTransitionFailures = new(StringComparer.Ordinal);

    /// <summary>
    /// A04：启动时按耐久核对证据闭合的意图（台账行删除失败但证据已提交）——
    /// 仅用于启动一致性识别；本进程内解析失败的闭合**不会**进入该集合（保持阻断直到重试成功）。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _resolvedFromEvidence = new(StringComparer.Ordinal);
    private int _resolutionsApplied;

    /// <summary>
    /// N03：存储故障锁**绑定具体意图**——故障原因、原动作与状态必须能与 pending / health / 核对
    /// 共享同一事实源；核对只能解除**匹配**该意图的锁（旧实现是一条字符串：pending 看不到原故障、
    /// 也无法核对，而核对任意无关意图就会把锁清掉）。
    /// </summary>
    public sealed record IntentStateFailure(string RunId, string Action, string Reason, DateTimeOffset At);

    /// <summary>
    /// F02/Fix1/A01：**同进程故障锁**（逐意图）。意图状态变更（MarkUnknown / Complete）落盘失败时登记——
    /// 文件写不进去意味着"意图台账不再可信"，必须在内存里立即阻断本进程所有动作入口，
    /// 直到进程重启（内存锁自然消失）或管理员完成对**对应意图**的核对。
    /// </summary>
    public IReadOnlyList<IntentStateFailure> StateTransitionFailures => _stateTransitionFailures.Values.ToArray();

    private void EngageStateTransitionFailureLock(string runId, string action, string reason)
    {
        // A01：以意图 RunId 为键保存**全部**失败（线程安全），不再互相覆盖。
        var failure = _stateTransitionFailures.GetOrAdd(
            runId, _ => new IntentStateFailure(runId, action, reason, DateTimeOffset.UtcNow));
        logger.LogError("In-process device-action failure lock engaged for intent {RunId} ({Action}): {Reason}", runId, action, failure.Reason);
    }

    /// <summary>
    /// A04：启动时把耐久核对证据应用到意图闭合状态（可恢复协议的恢复端）。
    /// 证据是核对闭合的耐久事实；台账行删除失败时，重启后必须**一致识别**为已核对，
    /// 而不是让同一记录重新要求核对（旧实现进程内放行、重启后重新阻断，恢复不一致）。
    /// 仅在首次访问时执行一次；本进程内失败的闭合不走该路径（保持阻断直到重试成功）。
    /// </summary>
    private void ApplyDurableResolutions()
    {
        if (Volatile.Read(ref _resolutionsApplied) != 0) return;
        lock (_resolvedFromEvidence)
        {
            if (_resolutionsApplied != 0) return;
            IReadOnlyList<ManualActionResolution> resolutions;
            try { resolutions = manualIntents.LoadResolutions(); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Durable resolution evidence could not be read at startup; intents stay unresolved.");
                _resolutionsApplied = 1;
                return;
            }
            foreach (var resolution in resolutions)
            {
                try
                {
                    manualIntents.Remove(resolution.RunId);
                }
                catch (Exception ex)
                {
                    // 证据已耐久 ⇒ 启动时按"已核对"一致识别（进程内不再重复要求核对）；
                    // 台账行在下次存储恢复后由后续核对/清理收尾。
                    logger.LogWarning(ex,
                        "Intent {RunId} has durable resolution evidence but its ledger row could not be cleared at startup; treating it as reconciled.",
                        resolution.RunId);
                    _resolvedFromEvidence[resolution.RunId] = 0;
                }
            }
            _resolutionsApplied = 1;
        }
    }

    /// <summary>生产安全状态是否仍未闭合（跨进程遗留或 Unknown / 不可读）。</summary>
    public DeviceActionSafetyState? UnresolvedProductionState()
    {
        var state = safety.Load();
        return state is not null && DeviceActionSafetyStore.IsUnresolved(state) ? state : null;
    }

    /// <summary>所有未闭合的手动意图（Unknown 任意进程 + Issued 来自更早进程）——用于 pending/health/闸门。</summary>
    public IReadOnlyList<ManualActionIntent> UnresolvedManualIntents()
    {
        ApplyDurableResolutions();
        var all = manualIntents.Load();
        var intents = all.Where(IsUnresolved).Where(x => !_resolvedFromEvidence.ContainsKey(x.RunId)).ToList();
        // N03/A01：每个故障锁绑定的意图即使磁盘上仍是 Issued，也必须出现在待核对清单里（并带故障原因）——
        // 否则现场既看不到故障、也无法核对原动作，只能被迫重启进程。逐故障独立登记，不再只补第一条。
        foreach (var failure in _stateTransitionFailures.Values)
        {
            if (intents.Any(x => string.Equals(x.RunId, failure.RunId, StringComparison.Ordinal))) continue;
            var persisted = all.FirstOrDefault(x => string.Equals(x.RunId, failure.RunId, StringComparison.Ordinal));
            var entry = (persisted ?? new ManualActionIntent(
                failure.RunId, failure.Action, [], [], [], failure.At, DeviceActionSafetyStore.CurrentProcessId)) with
            {
                State = ManualActionIntentState.Unknown,
                ClosedReason = $"safety-state transition failed: {failure.Reason}",
                ClosedAt = failure.At
            };
            intents.Add(entry);
        }
        return intents;
    }

    /// <summary>全部在册意图（含本进程活跃）——供运维查看当前有哪些动作在飞行中。</summary>
    public IReadOnlyList<ManualActionIntent> AllManualIntents()
    {
        ApplyDurableResolutions();
        return manualIntents.Load().Where(x => !_resolvedFromEvidence.ContainsKey(x.RunId)).ToList();
    }

    private static bool IsUnresolved(ManualActionIntent intent)
        => intent.State == ManualActionIntentState.Unknown ||
           !string.Equals(intent.ProcessId, DeviceActionSafetyStore.CurrentProcessId, StringComparison.Ordinal);

    /// <summary>手动/临时/调试/作业运行入口的闸门：存在未闭合意图时抛出 409。安全停止/只读路径不得调用。</summary>
    public void EnsureActionsAllowed(string action)
    {
        ApplyDurableResolutions();
        // Fix1/A01：同进程故障锁优先（逐意图独立登记）——意图台账不可信时绝不继续下发动作；
        // 最后一个未闭合故障消除前，新动作一直被拒绝。
        if (_stateTransitionFailures.Values.OrderBy(x => x.At).FirstOrDefault() is { } processFailure)
            Block(action, $"an in-process safety-state failure lock is engaged ({processFailure}); reconcile the device state or restart the host");

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
        ApplyDurableResolutions();
        if (_stateTransitionFailures.Values.OrderBy(x => x.At).FirstOrDefault() is { } processFailure)
            throw new ApiConflictException(
                $"Production start blocked: an in-process safety-state failure lock is engaged ({processFailure}); reconcile the device state or restart the host.");
        IReadOnlyList<ManualActionIntent> intents;
        try { intents = manualIntents.Load().Where(x => !_resolvedFromEvidence.ContainsKey(x.RunId)).ToList(); }
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
    public async Task<DeviceActionHandle> BeginWorkflowIntentAsync(string action, WorkflowDefinition workflow, string? relatedRunId, CancellationToken ct)
    {
        EnsureActionsAllowed(action);
        var manifest = await dependencies.CaptureAsync(workflow, ct);
        return RecordIntent(
            action,
            manifest.Devices.Select(x => x.Id).ToArray(),
            manifest.Robots.Select(x => x.Id).ToArray(),
            manifest.Cameras.Select(x => x.Id).ToArray(),
            relatedRunId);
    }

    /// <summary>
    /// F03：管理员核对后精确清除一条**未闭合**的手动/临时运行动作意图。
    /// 正在本进程执行（Issued）的意图不得被清除——那会把"动作仍在飞行"当成"已核对"。
    /// 证据必填由调用端（受管理员认证的端点）校验并写入审计。
    /// </summary>
    public ManualActionIntent ResolveManualIntent(string runId, string operatorName, string reason, string evidence, string identityEvidence)
    {
        if (string.IsNullOrWhiteSpace(operatorName)) throw new ApiValidationException("Authenticated administrator identity is required.");
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(identityEvidence))
            throw new ApiValidationException("Reason, on-site evidence, and device identity evidence are required (the resolution is retained as an auditable record).");

        ApplyDurableResolutions();
        // A04：启动时已按耐久证据闭合的意图——重复核对幂等返回既有结果（不再重复要求或报错）。
        if (_resolvedFromEvidence.ContainsKey(runId) && manualIntents.TryGetResolution(runId) is { } applied)
        {
            logger.LogInformation(
                "Manual action intent {RunId} was already reconciled at {At:O} by '{Operator}' (applied at startup); returning the recorded evidence (idempotent).",
                runId, applied.ResolvedAt, applied.OperatorName);
            return new ManualActionIntent(applied.RunId, applied.Action, applied.DeviceIds, applied.RobotIds, applied.CameraIds,
                applied.ResolvedAt, applied.OriginalProcessId, applied.OriginalState, applied.OriginalClosedReason,
                applied.ResolvedAt, applied.RelatedRunId);
        }
        var matchesFailureLock = _stateTransitionFailures.TryGetValue(runId, out var failure);
        var target = manualIntents.Load().FirstOrDefault(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));
        if (target is null && matchesFailureLock)
        {
            // N03：磁盘条目可能不存在/不可写，但故障锁本身记录了原意图——允许按故障锁信息核对。
            target = new ManualActionIntent(failure!.RunId, failure.Action, [], [], [], failure.At,
                DeviceActionSafetyStore.CurrentProcessId, ManualActionIntentState.Unknown,
                $"safety-state transition failed: {failure.Reason}", failure.At);
        }
        if (target is null)
        {
            // A04：重复核对的幂等语义——耐久证据已存在且台账已闭合时，返回既有核对结果，
            // 而不是报"不存在"（重试/超时重发的调用方据此确认恢复已完成）。
            if (manualIntents.TryGetResolution(runId) is { } prior)
            {
                _stateTransitionFailures.TryRemove(runId, out _);
                logger.LogInformation(
                    "Manual action intent {RunId} was already reconciled at {At:O} by '{Operator}'; returning the recorded evidence (idempotent).",
                    runId, prior.ResolvedAt, prior.OperatorName);
                return new ManualActionIntent(prior.RunId, prior.Action, prior.DeviceIds, prior.RobotIds, prior.CameraIds,
                    prior.ResolvedAt, prior.OriginalProcessId, prior.OriginalState, prior.OriginalClosedReason,
                    prior.ResolvedAt, prior.RelatedRunId);
            }
            throw new ApiNotFoundException($"Manual action intent '{runId}' does not exist.");
        }
        if (!IsUnresolved(target) && !matchesFailureLock)
            throw new ApiConflictException(
                $"Manual action intent '{runId}' ({target.Action}) is still active in this process; wait for it to finish or stop it before reconciling.");

        // C05：核对前必须验证**没有已知在途动作**——机器人仍在执行/停止未确认、或设备命令门
        // 仍被占用时，绝不能把"现场人员写了一段说明"当作动作已终止的证据。
        var pendingRobots = dependencies.VerifyNoPendingRobotOperations(target.RobotIds);
        if (!pendingRobots.Ok)
            throw new ApiConflictException($"Cannot reconcile while robot operations remain active: {pendingRobots.Summary}");
        var busyDevices = target.DeviceIds.Where(dependencies.IsDeviceCommandInFlight).ToArray();
        if (busyDevices.Length > 0)
            throw new ApiConflictException(
                $"Cannot reconcile while device commands are still in flight: {string.Join(", ", busyDevices)}.");
        // R02（2026-10-11 复审）：相机动作同样必须确认不在途——手动触发/取图触发/同步组命令
        // 仍持有派发许可、或采集停止未确认时，核对绝不能凭一纸说明放行（反例：驱动仍卡在
        // 软件触发调用内，核对却 200 且 Unknown 消失）。
        var busyCameras = target.CameraIds.Where(dependencies.IsCameraActionUnresolved).ToArray();
        if (busyCameras.Length > 0)
            throw new ApiConflictException(
                $"Cannot reconcile while camera actions are still in flight or unconfirmed: {string.Join(", ", busyCameras)}.");

        // Fix3：顺序不可颠倒——先把核对证据（含意图快照）耐久落盘，成功后才解除意图。
        // 存证失败 ⇒ 抛出且意图保留（绝不出现"无证据放行"）。
        manualIntents.SaveResolution(new ManualActionResolution(
            target.RunId, target.Action, target.DeviceIds, target.RobotIds, target.CameraIds,
            target.ProcessId, target.State, target.ClosedReason,
            operatorName, reason.Trim(), evidence.Trim(), identityEvidence.Trim(), DateTimeOffset.UtcNow,
            target.RelatedRunId));
        // A04：证据已提交 ≠ 闭合完成——台账闭合持久化失败时**不能返回"完成恢复"**，
        // 也不解除故障锁（旧实现仅记警告仍清锁并报成功：进程内显示已恢复且放行，
        // 重启后同一记录又要求核对，恢复既不一致也不幂等）。重试核对或重启
        // （启动时一致识别耐久证据）即可完成闭合，不会死锁。
        try { manualIntents.Remove(runId); }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Clearing intent {RunId} from the ledger failed; the durable resolution evidence is committed but the closure is not applied yet.",
                runId);
            throw new ApiUnavailableException(
                $"The reconciliation evidence for intent '{runId}' was committed, but the intent ledger could not be updated ({ex.Message}). " +
                "The action stays blocked — retry the resolution once storage is healthy (a host restart also applies the durable evidence).");
        }
        // N03/A01：只解除**绑定该意图**的故障锁——核对无关意图绝不能放行原故障。
        _stateTransitionFailures.TryRemove(runId, out _);
        logger.LogWarning(
            "Manual action intent {RunId} ({Action}) was reconciled and cleared by administrator '{Operator}'; evidence retained in {Directory}.",
            runId, target.Action, operatorName, manualIntents.ResolutionDirectory);
        return target;
    }

    private DeviceActionHandle RecordIntent(string action, IReadOnlyList<string> deviceIds, IReadOnlyList<string> robotIds, IReadOnlyList<string> cameraIds, string? relatedRunId = null)
    {
        var runId = "manual-" + Guid.NewGuid().ToString("N");
        try
        {
            manualIntents.Add(new ManualActionIntent(
                runId, action, deviceIds, robotIds, cameraIds,
                DateTimeOffset.UtcNow, DeviceActionSafetyStore.CurrentProcessId,
                RelatedRunId: relatedRunId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Manual action intent for '{Action}' could not be persisted; refusing to execute the action.", action);
            throw new ApiUnavailableException(
                $"Refusing to execute device action '{action}': its safety intent could not be persisted ({ex.Message}). Resolve the storage problem first.");
        }
        return new DeviceActionHandle(manualIntents, logger, runId, action, EngageStateTransitionFailureLock);
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
    private readonly Action<string, string, string>? _onStateFailure;
    private int _settled;
    private int _disposed;

    internal DeviceActionHandle(ManualActionIntentStore store, ILogger logger, string runId, string action, Action<string, string, string>? onStateFailure = null)
    {
        _store = store;
        _logger = logger;
        _runId = runId;
        _action = action;
        _onStateFailure = onStateFailure;
    }

    public string RunId => _runId;

    /// <summary>动作已确认成功且必要证据（追溯/审计）已持久化。</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _settled, 1) != 0) return;
        try { _store.Remove(_runId); }
        catch (Exception ex)
        {
            // 清除失败 ⇒ 意图台账不可信：置同进程故障锁（重启前不得再下发动作）。
            _logger.LogError(ex, "Manual action intent for '{Action}' ({RunId}) could not be cleared.", _action, _runId);
            _onStateFailure?.Invoke(_runId, _action, $"the intent could not be cleared: {ex.Message}");
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
            _logger.LogError(ex, "Manual action intent for '{Action}' ({RunId}) could not be marked as unknown.", _action, _runId);
            _onStateFailure?.Invoke(_runId, _action, $"the intent could not be marked as unknown: {ex.Message}");
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
            _logger.LogError(ex, "Manual action intent for '{Action}' ({RunId}) could not be marked as unknown on dispose.", _action, _runId);
            _onStateFailure?.Invoke(_runId, _action, $"the intent could not be marked as unknown on dispose: {ex.Message}");
        }
    }
}

/// <summary>
/// R01/C01/C07：设备动作的**单一分类与完成度契约**。
///
/// 分类：生产运行时与临时/调试/作业运行共用同一份副作用节点判定——此前两处各写一份
/// （生产循环内部 + 授权层），修改一处不能覆盖全系统。
///
/// 完成度（C01）：流程/节点返回成功只代表"已受理"，不等于物理动作完成。
/// 生产循环早已有 RequiresManualReconciliation，但 AdHoc/Debug/调试会话/已发布作业
/// 不复用它，导致"未确认的异步机器人命令"被当成闭合、意图被清除。
/// </summary>
public static class DeviceSideEffectClassifier
{
    /// <summary>
    /// 兼容回退：未给出显式动作声明的目录沿用的内置分类（2026-10-11 复审整改后仅作为
    /// "未声明目录"的回退；新插件必须在目录/SDK 中声明 Actions，宿主不再按节点字符串猜测）。
    /// </summary>
    private static readonly HashSet<string> SideEffectNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "device.writeTag",
        "device.writeVisionResult",
        "robot.executeTarget"
    };

    /// <summary>解析显式动作声明；目录未注册或未声明时返回 null（调用方走兼容回退）。</summary>
    private static IReadOnlyList<NodeActionDeclaration>? TryResolveDeclaredActions(string nodeType, VisionNodeRegistry? nodes)
        => nodes is not null && nodes.IsRegistered(nodeType) ? nodes.Require(nodeType).Catalog.Actions : null;

    /// <summary>该工作流是否包含会驱动物理设备的节点（声明优先+按参数求值，未声明目录回退到内置分类）。</summary>
    public static bool HasDeviceSideEffects(WorkflowDefinition workflow, VisionNodeRegistry? nodes = null)
        => workflow.Nodes.Any(node => IsDeviceSideEffectNode(node, nodes));

    /// <summary>
    /// R02：节点级分类——动作声明的参数条件（如取图节点仅在 triggerBeforeGrab=true 时触发相机）
    /// 按节点**有效参数值**求值，与 Engine 调试审批共用同一求值器。
    /// </summary>
    public static bool IsDeviceSideEffectNode(NodeDefinition node, VisionNodeRegistry? nodes = null)
    {
        if (nodes is not null && nodes.IsRegistered(node.Type))
        {
            var catalog = nodes.Require(node.Type).Catalog;
            // 显式声明：空列表 = 明确无副作用；非空 = 按参数求值的动作。未声明 ⇒ 兼容回退。
            if (catalog.DeclaresActions) return NodeActionContract.NodeHasAction(node, catalog);
        }
        return SideEffectNodeTypes.Contains(node.Type);
    }

    /// <summary>
    /// 类型级分类（保守口径：无法看到节点参数时，声明了任何动作即视为有副作用）。
    /// 生产判定请使用节点级重载，避免把"仅在某些参数下才成立"的动作误判为恒有副作用。
    /// </summary>
    public static bool IsDeviceSideEffectNode(string nodeType, VisionNodeRegistry? nodes = null)
    {
        if (TryResolveDeclaredActions(nodeType, nodes) is { } declared) return declared.Count > 0;
        return SideEffectNodeTypes.Contains(nodeType);
    }

    /// <summary>该节点类型是否为机器人指令类动作（声明的 RobotCommand 或回退的 robot.executeTarget）。</summary>
    private static bool IsRobotCommandType(string nodeType, VisionNodeRegistry? nodes)
    {
        if (TryResolveDeclaredActions(nodeType, nodes) is { } declared)
            return declared.Any(x => x.Kind == NodeActionKind.RobotCommand);
        return nodeType.Equals("robot.executeTarget", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 完成证据契约：声明为 Synchronous 的机器人指令以"调用返回"为完成（不需要握手证据）；
    /// 声明为 ProtocolConfirmed 或未声明的沿用协议证据判定。
    /// </summary>
    private static bool DeclaresSynchronousCompletion(string nodeType, VisionNodeRegistry? nodes)
        => TryResolveDeclaredActions(nodeType, nodes) is { } declared &&
           declared.Any(x => x.Kind == NodeActionKind.RobotCommand && x.Completion == NodeActionCompletion.Synchronous);

    /// <summary>
    /// C01：结果中是否存在"已受理但未确认完成"的设备动作——存在即不得解除动作意图。
    /// 判定依据是节点报告的协议快照（inPosition + state=InPosition），而不是流程 Success。
    /// 扩展契约：机器人指令类动作按**目录声明**识别（声明 RobotCommand 的插件节点同样纳入完成度判定）；
    /// 未声明目录回退到内置分类。
    /// </summary>
    public static bool RequiresManualReconciliation(WorkflowDefinition workflow, WorkflowRunResult result, VisionNodeRegistry? registry = null)
    {
        var nodes = workflow.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var runReports = result.NodeReports.Where(report => report.Phase == NodeExecutionPhase.Run).ToArray();
        var robotReports = runReports.Where(report => IsRobotCommandType(report.NodeType, registry)).ToArray();
        // 空/不完整报告集无法证明异步命令被跳过（测试替身与中断的 runner 常见）——保守要求核对。
        if (robotReports.Length == 0 && runReports.Length == 0 && workflow.Nodes.Any(node => IsRobotCommandType(node.Type, registry)))
            return true;
        foreach (var report in robotReports.Where(report => report.Success))
        {
            // 完成证据契约：声明 Synchronous 的机器人指令以"调用返回"为完成，不需要协议证据。
            if (DeclaresSynchronousCompletion(report.NodeType, registry)) continue;
            // N01：未知映射（模块展开前缀 `module-xxx::`、插件节点）**不能静默跳过**——那会让完成
            // 判定对展开后的执行计划失效。无法解析到节点配置时保守要求核对。
            if (!nodes.TryGetValue(report.NodeId, out var node))
                return true;
            var action = node.GetString("action", "Handshake");
            if (action.Equals("SendTarget", StringComparison.OrdinalIgnoreCase) || !node.GetBool("waitForInPosition", true))
                return true;

            var inPosition = TryGetOutput(report, "inPosition", out var ip) ? ip as bool? : null;
            var state = TryGetOutput(report, "state", out var st) ? st as string : null;

            // 路径 a：到位确认（Move / 等到位握手）。
            if (inPosition is true && string.Equals(state, "InPosition", StringComparison.OrdinalIgnoreCase))
                continue;

            // 路径 b（N02）：默认 autoAck=true 的握手在 Ack 后协议复位为 Ready（inPosition 清零），
            // 但 Ack 本身证明该命令已完成——正常成功不得被误判为"未确认"。
            if (HandshakeAcknowledged(report) && !RobotReportsBusy(report))
                continue;

            return true;
        }
        return false;
    }

    /// <summary>N02：节点报告的手势快照是否已确认（Ack）——Ack 后协议复位为 Ready，不能用 Ready 反推完成。</summary>
    private static bool HandshakeAcknowledged(NodeRunReport report)
        => TryGetHandshake(report) is { Ack: true, Error: false };

    private static bool RobotReportsBusy(NodeRunReport report)
        => TryGetHandshake(report) is { Busy: true };

    private static RobotHandshakeSignals? TryGetHandshake(NodeRunReport report)
        => report.Summary is { } summary && summary.TryGetValue("handshake", out var value) && value is RobotHandshakeSignals signals
            ? signals
            : null;

    private static bool TryGetOutput(NodeRunReport report, string name, out object? value)
    {
        value = null;
        if (report.Outputs is null) return false;
        var pair = report.Outputs.FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pair.Key is null) return false;
        value = pair.Value.Value;
        return true;
    }
}
