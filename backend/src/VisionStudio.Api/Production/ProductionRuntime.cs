using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class ProductionRuntimeService
{
    private readonly JobStore _jobs;
    private readonly IVisionWorkflowRunner _runner;
    private readonly TraceabilityStore _traces;
    private readonly RunStore _runs;
    private readonly ProductionRuntimeConfigStore _configStore;
    private readonly AlarmStore _alarms;
    private readonly RuntimeDependencyManifestService _dependencies;
    private readonly StorageCapacityService _storageCapacity;
    private readonly ProductionPtpDriftGuardService _ptpGuard;
    private readonly ProductionSynchronizationHealthGuardService _synchronizationGuard;
    private readonly DeviceLeaseRegistry _leases;
    private readonly ILogger<ProductionRuntimeService> _logger;
    private readonly ProductionTraceOptions _traceOptions;
    private readonly DeviceActionSafetyStore? _safetyStore;
    /// <summary>R01：统一动作授权——生产启动必须同时检查其它入口（手动/临时运行）登记的意图。</summary>
    private readonly DeviceActionAuthorizationService? _authorization;
    private readonly WorkflowModuleExpander? _moduleExpander;
    /// <summary>扩展安全契约：显式动作声明的目录解析入口（可选；缺省走内置兼容分类）。</summary>
    private readonly VisionNodeRegistry? _nodes;
    private ProductionTraceWriter? _traceWriter;
    /// <summary>
    /// R05（2026-10-12 复审）：上一次循环退出时仍有在途追溯提交（存储挂起被预算截断）的 writer。
    /// 非 null 且 PendingCount &gt; 0 时禁止启动新周期——"后台旧任务未收尾时禁止新周期"，直到排空。
    /// </summary>
    private ProductionTraceWriter? _drainingTraceWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateSync = new();
    private CancellationTokenSource? _runtimeCts;
    private Task? _loopTask;
    private ProductionRuntimeConfig _config = new();
    private PublishedJobSnapshot? _lockedSnapshot;
    private DeviceLease? _lease;
    private ProductionRuntimeState _state = ProductionRuntimeState.Stopped;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastCycleAt;
    private string? _currentRunId;
    private long _cycleCount;
    private long _okCount;
    private long _ngCount;
    private long _errorCount;
    private int _consecutiveFailures;
    private long _watchdogTrips;
    private double _lastDurationMs;
    private string? _lastDisposition;
    private string? _lastError;
    private int _recoveryAttempts;
    /// <summary>锁定快照的工作流是否含设备（PLC/机器人）副作用：含副作用时禁止自动恢复重跑。</summary>
    private bool _hasDeviceSideEffects;
    /// <summary>
    /// 副作用流程运行期间保留的动作意图：只有动作完成被协议确认且对应追溯已持久提交，或管理员
    /// 提交可审计的现场核对证据后才清除。该标记独立于 Faulted 状态与循环生命周期——Stop 不会清除它，因此
    /// "Stop → Start" 无法绕过核对，重启后的自动启动同样受其约束。
    /// </summary>
    private bool _deviceActionStateUnknown;
    /// <summary>置位未知标记时锁定的依赖清单哈希，用于重启后仍能核对同一批设备。</summary>
    private string? _unknownActionManifestHash;
    private string? _unknownActionRunId;
    private DeviceActionSafetyPhase _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
    /// <summary>F02：故障时锁定的设备/机器人 ID 清单——核对必须针对它，而不是新发布流程的清单。</summary>
    private string[] _unknownActionDeviceIds = [];
    private string[] _unknownActionRobotIds = [];
    /// <summary>R02（2026-10-11 复审）：动作时锁定的相机 ID 清单——相机动作纳入同一核对契约。</summary>
    private string[] _unknownActionCameraIds = [];
    /// <summary>Q02：登记动作时的设备身份指纹（driver|protocol|endpoint），核对时检测设备替换漂移。</summary>
    private IReadOnlyDictionary<string, string> _unknownActionDeviceFingerprints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>F01：持久化状态不可读（损坏）——无法核对，必须人工处理后才能启动。</summary>
    private bool _unknownActionUnverifiable;
    private ProductionPtpGuardSnapshot _ptpGuardStatus = ProductionPtpGuardSnapshot.Disabled;
    private ProductionSynchronizationGuardSnapshot _synchronizationGuardStatus = ProductionSynchronizationGuardSnapshot.Disabled;
    private int _synchronizationGuardUnhealthyChecks;
    private int _synchronizationGuardHealthyChecks;

    public ProductionRuntimeService(
        JobStore jobs,
        IVisionWorkflowRunner runner,
        TraceabilityStore traces,
        RunStore runs,
        ProductionRuntimeConfigStore configStore,
        AlarmStore alarms,
        RuntimeDependencyManifestService dependencies,
        StorageCapacityService storageCapacity,
        ProductionPtpDriftGuardService ptpGuard,
        ProductionSynchronizationHealthGuardService synchronizationGuard,
        DeviceLeaseRegistry leases,
        ILogger<ProductionRuntimeService> logger,
        Microsoft.Extensions.Options.IOptions<ProductionTraceOptions>? traceOptions = null,
        DeviceActionSafetyStore? safetyStore = null,
        WorkflowModuleExpander? moduleExpander = null,
        DeviceActionAuthorizationService? authorization = null,
        VisionNodeRegistry? nodes = null)
    {
        _jobs = jobs;
        _runner = runner;
        _traces = traces;
        _runs = runs;
        _configStore = configStore;
        _alarms = alarms;
        _dependencies = dependencies;
        _storageCapacity = storageCapacity;
        _ptpGuard = ptpGuard;
        _synchronizationGuard = synchronizationGuard;
        _leases = leases;
        _logger = logger;
        _traceOptions = traceOptions?.Value ?? new ProductionTraceOptions();
        _safetyStore = safetyStore;
        _authorization = authorization;
        _moduleExpander = moduleExpander;
        _nodes = nodes;

        // F01：加载持久化的"设备动作不确定"状态——重启后标记与锁定清单必须复现，
        // 否则自动/人工启动都会跳过设备核对。文件损坏时按"不可核对"处理（保守拒绝启动）。
        if (safetyStore?.Load() is { } persisted)
        {
            _deviceActionStateUnknown = true;
            _unknownActionManifestHash = persisted.ManifestHash;
            _unknownActionRunId = persisted.RunId;
            _unknownActionPhase = persisted.Phase;
            _unknownActionDeviceIds = persisted.DeviceIds.ToArray();
            _unknownActionRobotIds = persisted.RobotIds.ToArray();
            _unknownActionCameraIds = persisted.CameraIds?.ToArray() ?? [];
            _unknownActionDeviceFingerprints = persisted.DeviceFingerprints ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _unknownActionUnverifiable = persisted.Unreadable;
            _logger.LogWarning(
                "Persisted device-action safety state loaded (set {SetAt}, phase {Phase}, unreadable={Unreadable}): {Reason}. Production start requires device verification.",
                persisted.SetAt, persisted.Phase, persisted.Unreadable, persisted.Reason);
        }
    }

    public ProductionRuntimeStatus Status
    {
        get
        {
            lock (_stateSync)
            {
                return new ProductionRuntimeStatus(
                    _state,
                    _lockedSnapshot?.JobId ?? _config.JobId,
                    _lockedSnapshot?.Version,
                    _lockedSnapshot?.WorkflowHash,
                    _lockedSnapshot?.DependencyManifest.ManifestHash,
                    _startedAt,
                    _lastCycleAt,
                    _currentRunId,
                    _cycleCount,
                    _okCount,
                    _ngCount,
                    _errorCount,
                    _consecutiveFailures,
                    _watchdogTrips,
                    _lastDurationMs,
                    _lastDisposition,
                    _lastError,
                    _lockedSnapshot is not null,
                    _ptpGuardStatus,
                    _synchronizationGuardStatus,
                    _traceWriter?.PendingCount ?? 0,
                    _traceOptions.QueueCapacity,
                    _recoveryAttempts,
                    _hasDeviceSideEffects);
            }
        }
    }

    public Task<ProductionRuntimeConfig> GetConfigAsync(CancellationToken ct) => _configStore.GetAsync(ct);

    public ProductionDeviceActionPendingState? DeviceActionResolutionState
    {
        get
        {
            lock (_stateSync)
            {
                if (!_deviceActionStateUnknown) return null;
                var persisted = _safetyStore?.Load();
                return new ProductionDeviceActionPendingState(
                    _unknownActionRunId,
                    _unknownActionManifestHash,
                    _unknownActionDeviceIds,
                    _unknownActionRobotIds,
                    _unknownActionDeviceFingerprints,
                    _unknownActionPhase,
                    persisted?.SetAt,
                    persisted?.Reason,
                    _unknownActionUnverifiable || persisted?.Unreadable == true,
                    _unknownActionCameraIds);
            }
        }
    }

    public async Task<ProductionRuntimeConfig> UpdateConfigAsync(ProductionRuntimeConfig config, CancellationToken ct)
    {
        // 检查与写入必须在同一生命周期锁（_gate）内：否则并发 StartAsync 可以在检查通过之后、
        // 保存完成之前拿到锁并完成启动，随后这里把新配置写进正在运行的 _config；
        // 运行循环每周期读 _config（MaxCycleMs / 恢复策略 / guard 开关），运行中即被改动。
        await _gate.WaitAsync(ct);
        try
        {
            if (Status.ProductionLocked) throw new InvalidOperationException("Stop Production Runtime before changing production configuration.");
            var saved = await _configStore.SaveAsync(config, ct);
            lock (_stateSync) _config = saved;
            return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProductionRuntimeStatus> StartAsync(string? jobId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = Status.State;
            // Stopping 同样拒绝：两段式停止的收尾（等待循环退出）可能持续到 StopTimeoutMs，
            // 期间启动会与旧 Stop 的收尾交错——旧收尾即使做了循环身份核对，也不该给它创造窗口
            if (current is ProductionRuntimeState.Starting or ProductionRuntimeState.Running or ProductionRuntimeState.Recovering or ProductionRuntimeState.Stopping)
                return Status;
            if (_loopTask is { IsCompleted: false })
                throw new InvalidOperationException("A previous Production Runtime loop is still alive. Stop must complete before starting another loop.");

            var config = await _configStore.GetAsync(ct);
            var selectedJob = string.IsNullOrWhiteSpace(jobId) ? config.JobId : jobId;
            if (string.IsNullOrWhiteSpace(selectedJob)) throw new InvalidOperationException("Production JobId is not configured.");

            lock (_stateSync)
            {
                _config = config;
                _state = ProductionRuntimeState.Starting;
            }

            await _storageCapacity.EnsureProductionStartAllowedAsync(ct);
            var snapshot = await _jobs.GetPublishedSnapshotAsync(selectedJob, ct);
            await _dependencies.ValidateOrThrowAsync(snapshot.DependencyManifest, snapshot.VersionSnapshot.Workflow, ct);
            if (_moduleExpander is not null)
            {
                var expanded = await _moduleExpander.ExpandAsync(snapshot.VersionSnapshot.Workflow, ct);
                var expectedModules = (snapshot.DependencyManifest.Modules ?? [])
                    .OrderBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase).ThenBy(module => module.Version).ToArray();
                var actualModules = expanded.Dependencies
                    .Select(module => new WorkflowModuleRuntimeDependency(module.ModuleId, module.Version, module.ModuleHash))
                    .DistinctBy(module => (module.ModuleId, module.Version, module.ModuleHash))
                    .OrderBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase).ThenBy(module => module.Version).ToArray();
                if (!expectedModules.SequenceEqual(actualModules))
                    throw new ApiConflictException("Published workflow module versions or hashes differ from the locked dependency manifest.");
                snapshot = snapshot with { ExpandedWorkflow = expanded.Workflow };
            }
            await _alarms.RecoverAsync("PROD-DEPENDENCY-DRIFT", "ProductionRuntime", ct);

            // R01：手动/临时运行登记的动作意图（含本进程正在进行的动作）同样阻断生产启动。
            _authorization?.EnsureProductionStartAllowed();
            // R05：上一循环若有在途追溯提交未收尾（存储挂起），禁止启动新周期。
            EnsureNoDrainingTraceWriter();
            // 统一安全闸门：只要上一次副作用故障留下的"设备动作未知"标记仍在，任何启动路径
            // （人工 Start、/api/production/recover、重启后的自动启动）都必须先通过设备状态核对。
            // 该标记独立于 Stop、持久化到磁盘（F01），因此 Stop、重启与切换流程都无法绕过；
            // 核对针对**故障时锁定的**清单（F02），而不是本次所选流程的清单。
            await EnsureNoUnknownDeviceActionsAsync();

            var ptpGuard = await _ptpGuard.EvaluateAsync(snapshot.Workflow, config, ct);
            lock (_stateSync) _ptpGuardStatus = ptpGuard;
            if (!ptpGuard.Healthy)
            {
                await RaisePtpGuardIssuesAsync(ptpGuard, null, CancellationToken.None);
                throw new ApiConflictException($"Production PTP drift guard blocked start: {ptpGuard.Summary}");
            }
            await RecoverPtpGuardAlarmsAsync(ct);

            var synchronizationGuard = await _synchronizationGuard.EvaluateAsync(snapshot.Workflow, config, ct);
            lock (_stateSync) _synchronizationGuardStatus = synchronizationGuard;
            if (!synchronizationGuard.Healthy)
            {
                await RaiseSynchronizationGuardIssuesAsync(synchronizationGuard, null, AlarmSeverity.Critical, CancellationToken.None);
                throw new ApiConflictException($"Production synchronization health guard blocked start: {synchronizationGuard.Summary}");
            }
            await RecoverSynchronizationGuardAlarmsAsync(ct);
            _runtimeCts?.Dispose();
            _runtimeCts = new CancellationTokenSource();
            lock (_stateSync)
            {
                // Lease acquisition is atomic with the locked snapshot: a conflicting holder (debug
                // session, run or manual operation) blocks the start instead of silently sharing hardware.
                // The previous lease (if any) is kept until the single release point in StopAsync.
                _lease = _leases.AcquireOrThrow(
                    DeviceLeaseOwner.Production(snapshot.JobId),
                    LeaseResources(snapshot),
                    $"Cannot start Production Runtime for job '{snapshot.JobId}'");
                _lockedSnapshot = snapshot;
                _startedAt = DateTimeOffset.UtcNow;
                _lastCycleAt = null;
                _currentRunId = null;
                _cycleCount = _okCount = _ngCount = _errorCount = _watchdogTrips = 0;
                _consecutiveFailures = 0;
                _recoveryAttempts = 0;
                // 设备（PLC）/机器人引用 = 副作用：含副作用的流程失败后不允许自动重跑（见 LoopCoreAsync）
                _hasDeviceSideEffects = snapshot.Workflow.Nodes.Any(IsDeviceSideEffectNode);
                _synchronizationGuardUnhealthyChecks = 0;
                _synchronizationGuardHealthyChecks = 0;
                _lastDurationMs = 0;
                _lastDisposition = null;
                _lastError = null;
                _state = ProductionRuntimeState.Running;
            }

            await _alarms.RecoverAsync("PROD-START", "ProductionRuntime", ct);
            await _alarms.RecoverAsync("PROD-STOP-TIMEOUT", "ProductionRuntime", ct);
            _loopTask = Task.Run(() => LoopGuardedAsync(snapshot, _runtimeCts.Token));
            return Status;
        }
        catch (Exception ex)
        {
            lock (_stateSync)
            {
                _state = ProductionRuntimeState.Faulted;
                _lastError = ex.Message;
            }
            if (ex.Message.Contains("dependency", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("manifest", StringComparison.OrdinalIgnoreCase))
                await _alarms.RaiseAsync("PROD-DEPENDENCY-DRIFT", AlarmSeverity.Critical, "ProductionRuntime", ex.Message, null, CancellationToken.None);
            await _alarms.RaiseAsync("PROD-START", AlarmSeverity.Critical, "ProductionRuntime", ex.Message, null, CancellationToken.None);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProductionRuntimeStatus> StopAsync(CancellationToken ct)
    {
        Task? loop;
        await _gate.WaitAsync(ct);
        try
        {
            if (Status.State == ProductionRuntimeState.Stopped) return Status;
            lock (_stateSync) _state = ProductionRuntimeState.Stopping;
            _runtimeCts?.Cancel();
            loop = _loopTask;
        }
        finally { _gate.Release(); }

        var stopTimedOut = false;
        if (loop is not null)
        {
            ProductionRuntimeConfig config;
            lock (_stateSync) config = _config;
            try { await loop.WaitAsync(TimeSpan.FromMilliseconds(config.StopTimeoutMs), ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (TimeoutException)
            {
                stopTimedOut = true;
                _logger.LogError("Production runtime loop did not stop within configured timeout; the host will not claim Stopped while work may still be executing.");
            }
            // R05/F02（2026-10-12 复核）：循环已结束但 Task 失败（例如 writer 释放时重抛追溯持久化
            // 错误）——错误证据已在其边界记录；这里吸收后继续完成停止收尾，绝不以异常中断使状态
            // 永久卡在 Stopping。设备所有权的释放仍由下方统一收尾点执行（未结束的循环不会走到这里）。
            catch (Exception ex) when (loop.IsCompleted)
            {
                _logger.LogWarning(ex, "Production runtime loop task ended with an error during stop; completing the resource cleanup and releasing the lease.");
            }
        }

        if (stopTimedOut && loop is { IsCompleted: false })
        {
            const string error = "Production Runtime stop timed out; an execution loop may still be active. Restart is blocked until that loop exits.";
            lock (_stateSync)
            {
                _state = ProductionRuntimeState.Faulted;
                _lastError = error;
            }
            await _alarms.RaiseAsync("PROD-STOP-TIMEOUT", AlarmSeverity.Critical, "ProductionRuntime", error, _currentRunId, CancellationToken.None);
            return Status;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // 循环身份校验：等待“自己捕获的那个循环”退出期间，可能有另一个 Stop 完成了收尾、
            // 随后新的 Start 建立了新循环。本次收尾只能清理自己捕获的那一代（循环引用仍是当前
            // _loopTask 时才执行）；否则会把新循环的状态、CTS 和硬件租约一并清掉——对外显示
            // Stopped / 未锁定，但新执行器仍在运行。
            if (!ReferenceEquals(_loopTask, loop))
                return Status;
            lock (_stateSync)
            {
                _state = ProductionRuntimeState.Stopped;
                _lockedSnapshot = null;
                // Single release point for the production lease: reached by a completed stop and by
                // recovery after a faulted loop. Faulted states with live work keep the lease.
                _lease?.Dispose();
                _lease = null;
                _currentRunId = null;
            }
            _runtimeCts?.Dispose();
            _runtimeCts = null;
            _loopTask = null;
            return Status;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// R05：把"在途追溯提交"作为启动闸门——上一循环退出时若有提交未收尾（存储挂起被预算截断），
    /// 新周期开始前必须先等它排空。永久挂起时宿主重启是恢复路径（设备动作意图持久保留，
    /// 重启后由安全闸门要求核对）——绝不允许与同一个挂起存储并发跑新周期。
    /// </summary>
}
