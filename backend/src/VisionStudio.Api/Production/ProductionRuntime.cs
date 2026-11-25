using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

internal sealed partial class ProductionStationRuntime
{
    private readonly string _stationId;
    private readonly string _alarmSource;
    private readonly string _traceSource;
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
    private readonly ILogger _logger;
    private readonly ProductionTraceOptions _traceOptions;
    private readonly DeviceActionSafetyStore? _safetyStore;
    private readonly ProductionSafetyLedgerRegistry? _safetyLedgers;
    /// <summary>R01：统一动作授权——生产启动必须同时检查其它入口（手动/临时运行）登记的意图。</summary>
    private readonly DeviceActionAuthorizationService? _authorization;
    private readonly WorkflowModuleExpander? _moduleExpander;
    /// <summary>扩展安全契约：显式动作声明的目录解析入口（可选；缺省走内置兼容分类）。</summary>
    private readonly VisionNodeRegistry? _nodes;
    private readonly RuntimeWorkloadGate _workloads;
    private readonly SemaphoreSlim? _executionSlotGate;
    private readonly Action<ProductionStationResultSnapshot>? _onCycleCompleted;
    private readonly ProductionTriggerQueue? _triggerQueue;
    private SemaphoreSlim _triggerSignal = new(0);
    private IDisposable? _workloadLease;
    private ProductionTraceWriter? _traceWriter;
    /// <summary>
    /// R05（2026-10-12 复审）：上一次循环退出时仍有在途追溯提交（存储挂起被预算截断）的 writer。
    /// 非 null 且 PendingCount &gt; 0 时禁止启动新周期——"后台旧任务未收尾时禁止新周期"，直到排空。
    /// </summary>
    private ProductionTraceWriter? _drainingTraceWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly string _statusEpoch = Guid.NewGuid().ToString("N");
    private long _statusVersion;
    private CancellationTokenSource? _runtimeCts;
    private Task? _loopTask;
    private TaskCompletionSource<ProductionRuntimeStatus>? _stopCompletion;
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

    public ProductionStationRuntime(
        string stationId,
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
        ILogger logger,
        Microsoft.Extensions.Options.IOptions<ProductionTraceOptions>? traceOptions = null,
        DeviceActionSafetyStore? safetyStore = null,
        WorkflowModuleExpander? moduleExpander = null,
        DeviceActionAuthorizationService? authorization = null,
        VisionNodeRegistry? nodes = null,
        RuntimeWorkloadGate? workloads = null,
        SemaphoreSlim? executionSlotGate = null,
        Action<ProductionStationResultSnapshot>? onCycleCompleted = null,
        ProductionSafetyLedgerRegistry? safetyLedgers = null,
        ProductionTriggerQueue? triggerQueue = null)
    {
        _stationId = ProductionRuntimeStationIds.Normalize(stationId);
        var isDefault = ProductionRuntimeStationIds.IsDefault(_stationId);
        _alarmSource = isDefault ? "ProductionRuntime" : $"ProductionRuntime:{_stationId}";
        _traceSource = isDefault ? "ProductionRuntime" : $"ProductionRuntime:{_stationId}";
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
        _safetyLedgers = safetyLedgers;
        _authorization = authorization;
        _moduleExpander = moduleExpander;
        _nodes = nodes;
        _workloads = workloads ?? new RuntimeWorkloadGate();
        _executionSlotGate = executionSlotGate;
        _onCycleCompleted = onCycleCompleted;
        _triggerQueue = triggerQueue;
        if (safetyStore is not null) safetyLedgers?.Register(_stationId, safetyStore);

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
                    _hasDeviceSideEffects,
                    _statusEpoch,
                    ++_statusVersion);
            }
        }
    }

    public string StationId => _stationId;

    public ProductionStationRuntimeStatus StationStatus => ProductionStationRuntimeStatus.From(_stationId, Status);

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

    public async Task<ProductionRuntimeConfig> UpdateConfigAsync(ProductionRuntimeConfig config, CancellationToken ct, string? expectedRevision = null)
    {
        // 检查与写入必须在同一生命周期锁（_gate）内：否则并发 StartAsync 可以在检查通过之后、
        // 保存完成之前拿到锁并完成启动，随后这里把新配置写进正在运行的 _config；
        // 运行循环每周期读 _config（MaxCycleMs / 恢复策略 / guard 开关），运行中即被改动。
        await _gate.WaitAsync(ct);
        try
        {
            if (Status.ProductionLocked) throw new InvalidOperationException("Stop Production Runtime before changing production configuration.");
            var saved = await _configStore.SaveAsync(config, ct, expectedRevision);
            lock (_stateSync) _config = saved;
            return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProductionRuntimeStatus> StartAsync(string? jobId, CancellationToken ct)
    {
        IDisposable? startupLease = null;
        var starting = false;
        await _gate.WaitAsync(ct);
        try
        {
            var current = Status.State;
            if (current is ProductionRuntimeState.Starting or ProductionRuntimeState.Running or ProductionRuntimeState.Recovering or ProductionRuntimeState.Stopping)
                return Status;
            if (_stopCompletion is not null || _loopTask is { IsCompleted: false })
                throw new InvalidOperationException("Previous Production Runtime work is still active or stopping. Stop must complete before starting another loop.");

            if (_workloadLease is null) startupLease = _workloads.AcquireProduction();
            starting = true;
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
            await _alarms.RecoverAsync("PROD-DEPENDENCY-DRIFT", _alarmSource, ct);

            // R01：手动/临时运行登记的动作意图（含本进程正在进行的动作）同样阻断生产启动。
            var deviceIds = snapshot.DependencyManifest.Devices.Select(x => x.Id).ToArray();
            var robotIds = snapshot.DependencyManifest.Robots.Select(x => x.Id).ToArray();
            var cameraIds = snapshot.DependencyManifest.Cameras.Select(x => x.Id).ToArray();
            var workflowHasSideEffects = snapshot.Workflow.Nodes.Any(IsDeviceSideEffectNode);
            if (_authorization is not null)
                _authorization.EnsureProductionStartAllowed(deviceIds, robotIds, cameraIds, allowEmptyTarget: !workflowHasSideEffects);
            else
                _safetyLedgers?.EnsureNoConflictingUnresolvedState(
                    "production start", deviceIds, robotIds, cameraIds, allowEmptyTarget: !workflowHasSideEffects);
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

            await _alarms.RecoverAsync("PROD-START", _alarmSource, ct);
            await _alarms.RecoverAsync("PROD-STOP-TIMEOUT", _alarmSource, ct);
            _runtimeCts?.Dispose();
            _runtimeCts = new CancellationTokenSource();
            lock (_stateSync)
            {
                // Lease acquisition is atomic with the locked snapshot: a conflicting holder (debug
                // session, run or manual operation) blocks the start instead of silently sharing hardware.
                var leaseOwner = ProductionRuntimeStationIds.IsDefault(_stationId)
                    ? DeviceLeaseOwner.Production(snapshot.JobId)
                    : new DeviceLeaseOwner("Production", $"{_stationId}:{snapshot.JobId}", $"Production Runtime ({_stationId})");
                var lease = _leases.AcquireOrThrow(
                    leaseOwner,
                    LeaseResources(snapshot),
                    $"Cannot start Production Runtime ({_stationId}) for job '{snapshot.JobId}'");
                _lease?.Dispose();
                _lease = lease;
                _workloadLease ??= startupLease;
                startupLease = null;
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

            var token = _runtimeCts.Token;
            _triggerSignal = new SemaphoreSlim(0);
            _loopTask = Task.Run(() => LoopGuardedAsync(snapshot, token));
            return Status;
        }
        catch (Exception ex) when (starting)
        {
            lock (_stateSync)
            {
                _state = ProductionRuntimeState.Faulted;
                _lastError = ex.Message;
            }
            if (ex.Message.Contains("dependency", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("manifest", StringComparison.OrdinalIgnoreCase))
                await _alarms.RaiseAsync("PROD-DEPENDENCY-DRIFT", AlarmSeverity.Critical, _alarmSource, ex.Message, null, CancellationToken.None);
            await _alarms.RaiseAsync("PROD-START", AlarmSeverity.Critical, _alarmSource, ex.Message, null, CancellationToken.None);
            throw;
        }
        finally
        {
            startupLease?.Dispose();
            _gate.Release();
        }
    }

    public ProductionTriggerEnqueueResult? TriggerOnce(string? eventId = null)
    {
        var state = Status.State;
        if (state != ProductionRuntimeState.Running)
            throw new ApiConflictException($"Cannot trigger station '{_stationId}' because its state is {state} (must be Running).");
        var accepted = _triggerQueue?.Enqueue(eventId);
        if (accepted?.IsNew != false) _triggerSignal.Release();
        return accepted;
    }

    public IReadOnlyList<ProductionTriggerEvent> ListTriggers()
        => _triggerQueue?.List() ?? [];

    public async Task DeleteConfigAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Status.State != ProductionRuntimeState.Stopped || _loopTask is { IsCompleted: false })
                throw new ApiConflictException($"Stop station '{_stationId}' before removing it.");
            lock (_stateSync)
            {
                if (_deviceActionStateUnknown)
                    throw new ApiConflictException($"Station '{_stationId}' has unresolved device actions; resolve them before removing the station.");
            }
            await _configStore.DeleteAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
