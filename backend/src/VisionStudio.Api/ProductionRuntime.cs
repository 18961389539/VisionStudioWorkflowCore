using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public enum ProductionRuntimeState
{
    Stopped,
    Starting,
    Running,
    Recovering,
    Stopping,
    Faulted
}

public sealed record ProductionRuntimeConfig(
    string? JobId = null,
    bool AutoStart = false,
    int CycleDelayMs = 25,
    int MaxCycleMs = 15000,
    int MaxConsecutiveFailures = 3,
    bool AutoRecover = true,
    int RecoveryDelayMs = 1000,
    int StopTimeoutMs = 5000,
    bool PtpDriftGuardEnabled = true,
    int PtpGuardWindowRuns = 20,
    int PtpGuardMinimumEvidenceRuns = 5,
    double PtpGuardMinimumReadyRate = 0.99,
    int PtpGuardCheckEveryCycles = 10,
    bool PtpGuardFaultOnMasterClockChange = true,
    bool SynchronizationHealthGuardEnabled = true,
    int SynchronizationGuardWindowRuns = 20,
    int SynchronizationGuardMinimumEvidenceRuns = 5,
    double SynchronizationGuardMaximumFailureRate = 0.05,
    double SynchronizationGuardMaximumFrameTimeoutRate = 0.05,
    int SynchronizationGuardMaxConsecutiveFailures = 2,
    int SynchronizationGuardMaxConsecutiveSkewViolations = 3,
    double SynchronizationGuardMaximumNativeFrameLossRate = 0.005,
    long SynchronizationGuardMaximumBufferUnderruns = 0,
    long SynchronizationGuardMaximumResynchronizations = 0,
    double SynchronizationGuardMaximumSequenceGapRate = 0.01,
    int SynchronizationGuardCheckEveryCycles = 5,
    int SynchronizationGuardUnhealthyChecksToFault = 2,
    int SynchronizationGuardHealthyChecksToRecover = 3,
    int SynchronizationGuardRecoveryTimeoutMs = 30000,
    /// <summary>同一故障事件内允许的自动恢复总次数：耗尽后锁定 Faulted，需人工核对与恢复。</summary>
    int MaxRecoveryAttempts = 3);

public sealed record ProductionRuntimeStatus(
    ProductionRuntimeState State,
    string? JobId,
    int? LockedJobVersion,
    string? LockedWorkflowHash,
    string? LockedDependencyManifestHash,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastCycleAt,
    string? CurrentRunId,
    long CycleCount,
    long OkCount,
    long NgCount,
    long ErrorCount,
    int ConsecutiveFailures,
    long WatchdogTrips,
    double LastDurationMs,
    string? LastDisposition,
    string? LastError,
    bool ProductionLocked,
    ProductionPtpGuardSnapshot? PtpGuard,
    ProductionSynchronizationGuardSnapshot? SynchronizationGuard,
    int PendingTraceCount = 0,
    int TraceQueueCapacity = 8,
    /// <summary>当前故障事件内已消耗的自动恢复次数（成功后清零）。</summary>
    int RecoveryAttempts = 0,
    /// <summary>锁定快照的工作流是否引用设备（PLC）/机器人：含设备副作用的失败不允许自动重跑。</summary>
    bool HasDeviceSideEffects = false);

public sealed record ProductionStartRequest(string? JobId = null);
public sealed record DeviceActionResolutionRequest(
    string RunId,
    string ManifestHash,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    string Reason,
    string Evidence,
    string DeviceIdentityEvidence);
public sealed record ProductionDeviceActionPendingState(
    string? RunId,
    string? ManifestHash,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> RobotIds,
    IReadOnlyDictionary<string, string> DeviceFingerprints,
    DeviceActionSafetyPhase Phase,
    DateTimeOffset? SetAt,
    string? Reason,
    bool Unreadable);

public sealed class ProductionRuntimeConfigStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProductionRuntimeConfigStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "production");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "runtime.json");
    }

    public async Task<ProductionRuntimeConfig> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new ProductionRuntimeConfig();
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var config = JsonSerializer.Deserialize<ProductionRuntimeConfig>(document.RootElement.GetRawText(), _json) ?? new ProductionRuntimeConfig();
            var defaults = new ProductionRuntimeConfig();
            if (!HasProperty(document.RootElement, "ptpDriftGuardEnabled"))
            {
                config = config with
                {
                    PtpDriftGuardEnabled = defaults.PtpDriftGuardEnabled,
                    PtpGuardWindowRuns = defaults.PtpGuardWindowRuns,
                    PtpGuardMinimumEvidenceRuns = defaults.PtpGuardMinimumEvidenceRuns,
                    PtpGuardMinimumReadyRate = defaults.PtpGuardMinimumReadyRate,
                    PtpGuardCheckEveryCycles = defaults.PtpGuardCheckEveryCycles,
                    PtpGuardFaultOnMasterClockChange = defaults.PtpGuardFaultOnMasterClockChange
                };
            }
            if (!HasProperty(document.RootElement, "synchronizationHealthGuardEnabled"))
            {
                config = config with
                {
                    SynchronizationHealthGuardEnabled = defaults.SynchronizationHealthGuardEnabled,
                    SynchronizationGuardWindowRuns = defaults.SynchronizationGuardWindowRuns,
                    SynchronizationGuardMinimumEvidenceRuns = defaults.SynchronizationGuardMinimumEvidenceRuns,
                    SynchronizationGuardMaximumFailureRate = defaults.SynchronizationGuardMaximumFailureRate,
                    SynchronizationGuardMaximumFrameTimeoutRate = defaults.SynchronizationGuardMaximumFrameTimeoutRate,
                    SynchronizationGuardMaxConsecutiveFailures = defaults.SynchronizationGuardMaxConsecutiveFailures,
                    SynchronizationGuardMaxConsecutiveSkewViolations = defaults.SynchronizationGuardMaxConsecutiveSkewViolations,
                    SynchronizationGuardMaximumNativeFrameLossRate = defaults.SynchronizationGuardMaximumNativeFrameLossRate,
                    SynchronizationGuardMaximumBufferUnderruns = defaults.SynchronizationGuardMaximumBufferUnderruns,
                    SynchronizationGuardMaximumResynchronizations = defaults.SynchronizationGuardMaximumResynchronizations,
                    SynchronizationGuardMaximumSequenceGapRate = defaults.SynchronizationGuardMaximumSequenceGapRate,
                    SynchronizationGuardCheckEveryCycles = defaults.SynchronizationGuardCheckEveryCycles,
                    SynchronizationGuardUnhealthyChecksToFault = defaults.SynchronizationGuardUnhealthyChecksToFault,
                    SynchronizationGuardHealthyChecksToRecover = defaults.SynchronizationGuardHealthyChecksToRecover,
                    SynchronizationGuardRecoveryTimeoutMs = defaults.SynchronizationGuardRecoveryTimeoutMs
                };
            }
            else if (!HasProperty(document.RootElement, "synchronizationGuardMaximumNativeFrameLossRate"))
            {
                // V0.44 already has the synchronization guard but predates vendor-native transport thresholds.
                // Apply V0.45 defaults explicitly instead of depending on constructor missing-parameter behavior.
                config = config with
                {
                    SynchronizationGuardMaximumNativeFrameLossRate = defaults.SynchronizationGuardMaximumNativeFrameLossRate,
                    SynchronizationGuardMaximumBufferUnderruns = defaults.SynchronizationGuardMaximumBufferUnderruns,
                    SynchronizationGuardMaximumResynchronizations = defaults.SynchronizationGuardMaximumResynchronizations
                };
            }
            return config;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProductionRuntimeConfig> SaveAsync(ProductionRuntimeConfig config, CancellationToken ct)
    {
        Validate(config);
        await _gate.WaitAsync(ct);
        try
        {
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, config, _json, ct);
            File.Move(temp, _path, true);
            return config;
        }
        finally { _gate.Release(); }
    }

    private static bool HasProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void Validate(ProductionRuntimeConfig config)
    {
        if (config.CycleDelayMs < 0 || config.CycleDelayMs > 60000) throw new ApiValidationException("CycleDelayMs must be 0..60000.");
        if (config.MaxCycleMs < 100 || config.MaxCycleMs > 600000) throw new ApiValidationException("MaxCycleMs must be 100..600000.");
        if (config.MaxConsecutiveFailures < 1 || config.MaxConsecutiveFailures > 1000) throw new ApiValidationException("MaxConsecutiveFailures must be 1..1000.");
        if (config.RecoveryDelayMs < 0 || config.RecoveryDelayMs > 600000) throw new ApiValidationException("RecoveryDelayMs must be 0..600000.");
        if (config.StopTimeoutMs < 100 || config.StopTimeoutMs > 60000) throw new ApiValidationException("StopTimeoutMs must be 100..60000.");
        if (config.PtpGuardWindowRuns < 5 || config.PtpGuardWindowRuns > 500) throw new ApiValidationException("PtpGuardWindowRuns must be 5..500.");
        if (config.PtpGuardMinimumEvidenceRuns < 1 || config.PtpGuardMinimumEvidenceRuns > config.PtpGuardWindowRuns) throw new ApiValidationException("PtpGuardMinimumEvidenceRuns must be 1..PtpGuardWindowRuns.");
        if (config.PtpGuardMinimumReadyRate < 0.5 || config.PtpGuardMinimumReadyRate > 1.0) throw new ApiValidationException("PtpGuardMinimumReadyRate must be 0.5..1.0.");
        if (config.PtpGuardCheckEveryCycles < 1 || config.PtpGuardCheckEveryCycles > 10000) throw new ApiValidationException("PtpGuardCheckEveryCycles must be 1..10000.");
        if (config.SynchronizationGuardWindowRuns < 5 || config.SynchronizationGuardWindowRuns > 500) throw new ApiValidationException("SynchronizationGuardWindowRuns must be 5..500.");
        if (config.SynchronizationGuardMinimumEvidenceRuns < 1 || config.SynchronizationGuardMinimumEvidenceRuns > config.SynchronizationGuardWindowRuns) throw new ApiValidationException("SynchronizationGuardMinimumEvidenceRuns must be 1..SynchronizationGuardWindowRuns.");
        if (config.SynchronizationGuardMaximumFailureRate < 0 || config.SynchronizationGuardMaximumFailureRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumFailureRate must be 0..0.5.");
        if (config.SynchronizationGuardMaximumFrameTimeoutRate < 0 || config.SynchronizationGuardMaximumFrameTimeoutRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumFrameTimeoutRate must be 0..0.5.");
        if (config.SynchronizationGuardMaxConsecutiveFailures < 1 || config.SynchronizationGuardMaxConsecutiveFailures > 100) throw new ApiValidationException("SynchronizationGuardMaxConsecutiveFailures must be 1..100.");
        if (config.SynchronizationGuardMaxConsecutiveSkewViolations < 1 || config.SynchronizationGuardMaxConsecutiveSkewViolations > 100) throw new ApiValidationException("SynchronizationGuardMaxConsecutiveSkewViolations must be 1..100.");
        if (config.SynchronizationGuardMaximumNativeFrameLossRate < 0 || config.SynchronizationGuardMaximumNativeFrameLossRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumNativeFrameLossRate must be 0..0.5.");
        if (config.SynchronizationGuardMaximumBufferUnderruns < 0 || config.SynchronizationGuardMaximumBufferUnderruns > 1_000_000) throw new ApiValidationException("SynchronizationGuardMaximumBufferUnderruns must be 0..1000000.");
        if (config.SynchronizationGuardMaximumResynchronizations < 0 || config.SynchronizationGuardMaximumResynchronizations > 1_000_000) throw new ApiValidationException("SynchronizationGuardMaximumResynchronizations must be 0..1000000.");
        if (config.SynchronizationGuardMaximumSequenceGapRate < 0 || config.SynchronizationGuardMaximumSequenceGapRate > 0.5) throw new ApiValidationException("SynchronizationGuardMaximumSequenceGapRate must be 0..0.5.");
        if (config.SynchronizationGuardCheckEveryCycles < 1 || config.SynchronizationGuardCheckEveryCycles > 10000) throw new ApiValidationException("SynchronizationGuardCheckEveryCycles must be 1..10000.");
        if (config.SynchronizationGuardUnhealthyChecksToFault < 1 || config.SynchronizationGuardUnhealthyChecksToFault > 100) throw new ApiValidationException("SynchronizationGuardUnhealthyChecksToFault must be 1..100.");
        if (config.SynchronizationGuardHealthyChecksToRecover < 1 || config.SynchronizationGuardHealthyChecksToRecover > 100) throw new ApiValidationException("SynchronizationGuardHealthyChecksToRecover must be 1..100.");
        if (config.SynchronizationGuardRecoveryTimeoutMs < 1000 || config.SynchronizationGuardRecoveryTimeoutMs > 600000) throw new ApiValidationException("SynchronizationGuardRecoveryTimeoutMs must be 1000..600000.");
    }
}

public sealed class ProductionRuntimeService
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
    private ProductionTraceWriter? _traceWriter;
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
        DeviceActionAuthorizationService? authorization = null)
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
                    _unknownActionUnverifiable || persisted?.Unreadable == true);
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
    /// 统一设备安全闸门：若存在未核对的动作意图，则对**动作时锁定的**设备/机器人清单执行快照检查
    /// 作为诊断信息，但连接/新鲜读数不能证明上一动作完成。因此始终保持标记并要求管理员提交现场证据。
    /// Start / Recover / 自动启动共用此闸门，杜绝任何绕过路径。
    /// </summary>
    private Task EnsureNoUnknownDeviceActionsAsync()
    {
        bool unknown;
        string? unknownHash;
        string[] deviceIds;
        string[] robotIds;
        bool unverifiable;
        IReadOnlyDictionary<string, string> fingerprints;
        string? runId;
        DeviceActionSafetyPhase phase;
        lock (_stateSync)
        {
            unknown = _deviceActionStateUnknown;
            unknownHash = _unknownActionManifestHash;
            deviceIds = _unknownActionDeviceIds;
            robotIds = _unknownActionRobotIds;
            unverifiable = _unknownActionUnverifiable;
            fingerprints = _unknownActionDeviceFingerprints;
            runId = _unknownActionRunId;
            phase = _unknownActionPhase;
        }

        // R01：持久化状态是跨组件事实源——除生产自己的内存标记外，还必须覆盖"上一个进程崩溃
        // 遗留"与其它入口（手动写入/机器人指令/相机触发/临时运行）登记的未闭合意图。
        if (_safetyStore?.Load() is { } persisted && DeviceActionSafetyStore.IsUnresolved(persisted))
        {
            unknown = true;
            unknownHash = persisted.ManifestHash;
            deviceIds = persisted.DeviceIds.ToArray();
            robotIds = persisted.RobotIds.ToArray();
            unverifiable = persisted.Unreadable;
            fingerprints = persisted.DeviceFingerprints ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            runId = persisted.RunId;
            phase = persisted.Phase;
        }
        if (!unknown) return Task.CompletedTask;

        if (unverifiable)
        {
            _logger.LogError(
                "Production start blocked: the persisted device-action safety state is unreadable (manifest {ManifestHash}); manual resolution is required before starting.",
                unknownHash);
            throw new ApiConflictException(
                "The persisted device-action safety state is unreadable, so the previous cycle's device actions cannot be verified. " +
                "Verify the physical device state and resolve the safety state file manually before starting.");
        }

        // F02：核对必须针对故障时锁定的清单——否则切换到无设备的新流程时，空清单必然通过核对，
        // 原故障设备从未被检查（旧实现传入的是本次启动所选流程的 manifest，存在此绕过路径）。
        // Q02：连同登记时的设备指纹一起核对（检测设备替换），并检查机器人是否有未返回的停止调用。
        var verification = _dependencies.VerifyLockedDevices(deviceIds, robotIds, fingerprints);
        _logger.LogWarning(
            "Production start blocked: run {RunId} has unresolved {Phase} device action intent (manifest {ManifestHash}); live snapshot verification={Verification}. Automatic recovery cannot prove command completion.",
            runId, phase, unknownHash, verification.Summary);
        throw new ApiConflictException(
            $"Device actions from run '{runId ?? "unknown"}' remain unconfirmed (manifest {unknownHash}). " +
            $"Current live-state checks: {verification.Summary}. Connection and fresh samples do not prove that the previous command completed. " +
            "Stop Production Runtime and use the administrator device-action reconciliation endpoint with recorded physical evidence.");
    }

    /// <summary>
    /// F01：置位"设备动作不确定"标记并立即持久化（含故障时锁定的设备/机器人清单与设备身份指纹）。
    /// Q01：持久化失败必须**阻止继续批准动作**——调用方在副作用失败路径上已停止循环；
    /// 若此写入失败，剩余防线是"周期开始前登记的意图"（RecordDeviceActionIntent），
    /// 它仍在磁盘上，重启后同样触发核对。
    /// </summary>
    private void SetDeviceActionUnknownState(RuntimeDependencyManifest manifest, string reason, string? runId = null)
    {
        string[] deviceIds;
        string[] robotIds;
        Dictionary<string, string> fingerprints;
        lock (_stateSync)
        {
            _deviceActionStateUnknown = true;
            _unknownActionManifestHash = manifest.ManifestHash;
            _unknownActionRunId = runId;
            _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
            _unknownActionDeviceIds = deviceIds = manifest.Devices.Select(x => x.Id).ToArray();
            _unknownActionRobotIds = robotIds = manifest.Robots.Select(x => x.Id).ToArray();
            _unknownActionDeviceFingerprints = fingerprints = CaptureDeviceFingerprints(manifest);
            _unknownActionUnverifiable = false;
        }
        try
        {
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.Save(new DeviceActionSafetyState(manifest.ManifestHash, deviceIds, robotIds, DateTimeOffset.UtcNow, reason,
                Phase: DeviceActionSafetyPhase.Unknown, RunId: runId, DeviceFingerprints: fingerprints,
                ProcessId: DeviceActionSafetyStore.CurrentProcessId));
            lock (_stateSync) _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist the device-action safety state (manifest {ManifestHash}); the in-memory marker stays in force, " +
                "and the pre-cycle intent record remains on disk so a restart still requires verification.",
                manifest.ManifestHash);
        }
    }

    /// <summary>
    /// Q01：副作用周期**开始前**耐久登记意图（RunId + 锁定设备清单 + 身份指纹 + IntentRecorded）。
    /// 进程被杀/断电/追溯队列故障都不会跳过这一步——启动加载见 IntentRecorded 即要求核对。
    /// 返回 false 表示安全状态无法持久化：调用方必须放弃执行设备动作（宁可不跑，不可无记录地跑）。
    /// </summary>
    private bool RecordDeviceActionIntent(RuntimeDependencyManifest manifest, string runId)
    {
        string[] deviceIds;
        string[] robotIds;
        Dictionary<string, string> fingerprints;
        lock (_stateSync)
        {
            _deviceActionStateUnknown = true;
            _unknownActionManifestHash = manifest.ManifestHash;
            _unknownActionRunId = runId;
            _unknownActionPhase = DeviceActionSafetyPhase.IntentRecorded;
            _unknownActionDeviceIds = deviceIds = manifest.Devices.Select(x => x.Id).ToArray();
            _unknownActionRobotIds = robotIds = manifest.Robots.Select(x => x.Id).ToArray();
            _unknownActionDeviceFingerprints = fingerprints = CaptureDeviceFingerprints(manifest);
            _unknownActionUnverifiable = false;
        }
        try
        {
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.Save(new DeviceActionSafetyState(manifest.ManifestHash, deviceIds, robotIds, DateTimeOffset.UtcNow,
                $"side-effect cycle {runId} started; the intent was registered before any device action executed",
                Phase: DeviceActionSafetyPhase.IntentRecorded, RunId: runId, DeviceFingerprints: fingerprints,
                ProcessId: DeviceActionSafetyStore.CurrentProcessId));
            lock (_stateSync) { _unknownActionRunId = runId; _unknownActionPhase = DeviceActionSafetyPhase.IntentRecorded; }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist the device-action intent before starting cycle {RunId} (manifest {ManifestHash}); refusing to execute device side effects.",
                runId, manifest.ManifestHash);
            return false;
        }
    }

    /// <summary>Q02：捕获登记时刻的设备身份（driver|protocol|endpoint）。设备不可解析时跳过该项。</summary>
    private Dictionary<string, string> CaptureDeviceFingerprints(RuntimeDependencyManifest manifest)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in manifest.Devices)
        {
            if (_dependencies.TryGetDeviceFingerprint(device.Id) is { } fingerprint)
                result[device.Id] = fingerprint;
        }
        return result;
    }

    /// <summary>F01：核对通过（或人工解决）后清除未知动作标记（内存 + 持久化）。</summary>
    private void ClearDeviceActionUnknownState()
    {
        if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured; refusing to clear the in-memory gate.");
        _safetyStore.Clear();
        lock (_stateSync)
        {
            _deviceActionStateUnknown = false;
            _unknownActionManifestHash = null;
            _unknownActionRunId = null;
            _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
            _unknownActionDeviceIds = [];
            _unknownActionRobotIds = [];
            _unknownActionDeviceFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _unknownActionUnverifiable = false;
        }
    }

    public async Task ResolveUnknownDeviceActionsAsync(DeviceActionResolutionRequest request, string operatorName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(operatorName)) throw new ApiValidationException("Authenticated administrator identity is required.");
        if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.Evidence) || string.IsNullOrWhiteSpace(request.DeviceIdentityEvidence))
            throw new ApiValidationException("Reason, physical action evidence, and verified device identity evidence are required.");

        await _gate.WaitAsync(ct);
        try
        {
            if (Status.State != ProductionRuntimeState.Stopped || _loopTask is { IsCompleted: false })
                throw new ApiConflictException("Stop Production Runtime and wait for its execution loop to finish before reconciling device actions.");
            _traceWriter?.ThrowIfFaulted();
            if (_traceWriter is { PendingCount: > 0 })
                throw new ApiConflictException("Wait for all accepted production trace records to persist before reconciling device actions.");

            string? runId;
            string? manifestHash;
            string[] deviceIds;
            string[] robotIds;
            bool unverifiable;
            lock (_stateSync)
            {
                if (!_deviceActionStateUnknown) throw new ApiConflictException("There is no unresolved device-action state to reconcile.");
                runId = _unknownActionRunId;
                manifestHash = _unknownActionManifestHash;
                deviceIds = _unknownActionDeviceIds;
                robotIds = _unknownActionRobotIds;
                unverifiable = _unknownActionUnverifiable;
            }
            if (unverifiable || string.IsNullOrWhiteSpace(manifestHash))
                throw new ApiConflictException("The persisted safety file is unreadable; this endpoint cannot verify its original manifest and device list. Preserve the file and have an administrator perform a separately audited, offline recovery procedure.");
            // Legacy safety records predate RunId. Permit explicit reconciliation only when the request uses
            // the fixed legacy sentinel and still matches the exact persisted manifest and asset lists.
            var expectedRunId = string.IsNullOrWhiteSpace(runId) ? "legacy" : runId;
            if (!string.Equals(request.RunId, expectedRunId, StringComparison.Ordinal) || !string.Equals(request.ManifestHash, manifestHash, StringComparison.Ordinal) ||
                !deviceIds.Order(StringComparer.Ordinal).SequenceEqual((request.DeviceIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
                !robotIds.Order(StringComparer.Ordinal).SequenceEqual((request.RobotIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new ApiConflictException("Resolution must identify the exact original run, manifest, devices, and robots recorded in the safety state.");

            var pendingRobots = _dependencies.VerifyNoPendingRobotOperations(robotIds);
            if (!pendingRobots.Ok)
                throw new ApiConflictException($"Cannot reconcile while robot operations remain active: {pendingRobots.Summary}");

            var resolution = new DeviceActionSafetyResolution(expectedRunId, manifestHash, deviceIds, robotIds, operatorName,
                request.Reason.Trim(), request.Evidence.Trim(), request.DeviceIdentityEvidence.Trim(), DateTimeOffset.UtcNow);
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.SaveResolution(resolution);
            ClearDeviceActionUnknownState();
            _logger.LogWarning("Administrator {Operator} manually reconciled device action run {RunId} with physical evidence: {Reason}", operatorName, expectedRunId, request.Reason);
        }
        finally { _gate.Release(); }
    }

    private static bool IsDeviceSideEffectNode(NodeDefinition node)
    {
        if (node.Type is "device.writeTag" or "device.writeVisionResult" or "robot.executeTarget") return true;
        return false;
    }

    private static bool RequiresManualActionReconciliation(WorkflowDefinition workflow, WorkflowRunResult result)
    {
        var nodes = workflow.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var runReports = result.NodeReports.Where(report => report.Phase == NodeExecutionPhase.Run).ToArray();
        var robotReports = runReports.Where(report => report.NodeType.Equals("robot.executeTarget", StringComparison.OrdinalIgnoreCase)).ToArray();
        // An empty/incomplete report set (common in test doubles and interrupted runners) cannot
        // prove that an async command was skipped. Real skipped branches still have run reports
        // for their control-flow nodes, while the skipped robot node itself has no report.
        if (robotReports.Length == 0 && runReports.Length == 0 && workflow.Nodes.Any(node => node.Type == "robot.executeTarget"))
            return true;
        foreach (var report in robotReports.Where(report => report.Success))
        {
            if (!nodes.TryGetValue(report.NodeId, out var node)) continue;
            var action = node.GetString("action", "Handshake");
            if (action.Equals("SendTarget", StringComparison.OrdinalIgnoreCase) || !node.GetBool("waitForInPosition", true))
                return true;

            // A successful runner result only proves that the node returned. Require the robot
            // node's own protocol snapshot to confirm completion before clearing the durable intent.
            if (!TryGetOutput(report, "inPosition", out var inPosition) || inPosition is not true ||
                !TryGetOutput(report, "state", out var state) ||
                !string.Equals(state as string, "InPosition", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryGetOutput(NodeRunReport report, string name, out object? value)
    {
        value = null;
        if (report.Outputs is null) return false;
        var pair = report.Outputs.FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pair.Key is null) return false;
        value = pair.Value.Value;
        return true;
    }

    public async Task<ProductionRuntimeStatus> RecoverAsync(CancellationToken ct)
    {
        if (Status.State != ProductionRuntimeState.Faulted) return Status;
        if (_loopTask is { IsCompleted: false })
            throw new InvalidOperationException("Cannot recover while the previous execution loop is still alive. Stop/terminate the blocked operation first.");

        // R01：手动/临时运行的未闭合意图同样阻断恢复启动。
        _authorization?.EnsureProductionStartAllowed();
        // Q02：先跑统一安全闸门——持久化的动作清单用于诊断，普通恢复绝不清除它；
        // 只有管理员提交实际核对证据后，后续 Start/Recover 才能继续。
        await EnsureNoUnknownDeviceActionsAsync();

        // 副作用故障的恢复闸门：在清理现场前核对锁定清单中设备/机器人的当前状态。
        // 当前连接快照可用于诊断，但没有设备协议确认上一动作的执行结果；未知动作必须由管理员
        // 提交实际核对证据解除，不能因普通 Recover 看到 Connected 就自动放行。
        PublishedJobSnapshot? locked;
        lock (_stateSync) locked = _lockedSnapshot;
        if (locked is not null && (locked.DependencyManifest.Devices.Count > 0 || locked.DependencyManifest.Robots.Count > 0))
        {
            var verification = _dependencies.VerifyLockedDevices(locked.DependencyManifest);
            if (!verification.Ok)
            {
                _logger.LogError("Production recovery blocked by device state verification: {Issues}", verification.Summary);
                throw new ApiConflictException(
                    $"Device state verification failed before recovery: {verification.Summary}. " +
                    "Resolve the device state and retry recovery (the verification failure is recorded).");
            }
            _logger.LogInformation(
                "Device state verification passed before production recovery: {Devices} device(s), {Robots} robot(s).",
                locked.DependencyManifest.Devices.Count, locked.DependencyManifest.Robots.Count);
        }

        await StopAsync(ct); // cleans completed loop/CTS without loading a new version yet.
        await _alarms.RecoverAsync("PROD-RUN-FAILURE", "ProductionRuntime", ct);
        await _alarms.RecoverAsync("PROD-WATCHDOG", "ProductionRuntime", ct);
        await _alarms.RecoverAsync("PROD-STOP-TIMEOUT", "ProductionRuntime", ct);
        await RecoverPtpGuardAlarmsAsync(ct);
        await RecoverSynchronizationGuardAlarmsAsync(ct);
        return await StartAsync(null, ct);
    }

    /// <summary>
    /// Blocks dependency mutations (delete, settings/profile changes) while any actor - production,
    /// debug session, run or manual operation - still holds the asset lease.
    /// </summary>
    public void EnsureDependencyMutationAllowed(string kind, string id)
        => _leases.EnsureNotHeld(kind, id, $"Cannot modify {kind} '{id}'");

    /// <summary>
    /// Acquires the run-scoped lease covering every hardware asset the workflow references. The caller
    /// owns the handle for the whole execution and must dispose it (using/finally) so assets are
    /// released on completion, cancellation or failure.
    /// </summary>
    public async Task<DeviceLease> AcquireRunLeaseAsync(string runId, WorkflowDefinition workflow, string action, CancellationToken ct)
    {
        var references = await ExtractReferencesOrThrowAsync(workflow, ct);
        return _leases.AcquireOrThrow(
            DeviceLeaseOwner.Run(runId),
            DeviceLeaseResource.FromReferences(references),
            action);
    }

    /// <summary>
    /// Blocks ad-hoc/debug/workflow runs that would touch assets already leased by another actor
    /// (production, a paused debug session, a concurrent run or a manual operation). Workflows
    /// referencing unrelated assets remain runnable. Fails closed when hardware references cannot
    /// be resolved instead of letting an unarbitrated run through.
    /// </summary>
    public async Task EnsureWorkflowRunAllowedAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        var references = await ExtractReferencesOrThrowAsync(workflow, ct);
        var conflicts = _leases.DescribeConflicts(DeviceLeaseResource.FromReferences(references));
        if (conflicts.Count > 0)
            throw new ApiConflictException(
                $"Cannot run a workflow while hardware is leased: {DeviceLeaseRegistry.DescribeConflicts(conflicts)}.");
    }

    private async Task<WorkflowRuntimeReferences> ExtractReferencesOrThrowAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        try
        {
            return await _dependencies.ExtractReferencesAsync(workflow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ApiConflictException(
                $"Cannot arbitrate the workflow run because its hardware references could not be resolved: {ex.Message}", ex);
        }
    }

    /// <summary>Hardware assets owned by one locked production snapshot (from its dependency manifest).</summary>
    private static IReadOnlyList<DeviceLeaseResource> LeaseResources(PublishedJobSnapshot snapshot)
        => snapshot.DependencyManifest.Cameras.Select(x => new DeviceLeaseResource(DeviceLeaseResourceKinds.Camera, x.Id))
            .Concat(snapshot.DependencyManifest.Devices.Select(x => new DeviceLeaseResource(DeviceLeaseResourceKinds.Device, x.Id)))
            .Concat(snapshot.DependencyManifest.Robots.Select(x => new DeviceLeaseResource(DeviceLeaseResourceKinds.Robot, x.Id)))
            .ToArray();

    private bool TryGetActiveLock(out PublishedJobSnapshot locked)
    {
        PublishedJobSnapshot? snapshot;
        ProductionRuntimeState state;
        lock (_stateSync)
        {
            snapshot = _lockedSnapshot;
            state = _state;
        }
        locked = snapshot!;
        return snapshot is not null &&
               state is ProductionRuntimeState.Starting or ProductionRuntimeState.Running or ProductionRuntimeState.Recovering or ProductionRuntimeState.Stopping or ProductionRuntimeState.Faulted;
    }

    public void EnsureMediaMutationAllowed(string mediaUri)
    {
        if (!TryGetActiveLock(out var locked)) return;

        static bool Overlaps(string left, string right)
        {
            var a = left.TrimEnd('/');
            var b = right.TrimEnd('/');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                   a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) ||
                   b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
        }

        var referenced = locked.DependencyManifest.Cameras.Any(x =>
            string.Equals(x.Driver, "file", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.Source) &&
            Overlaps(x.Source!, mediaUri));
        if (referenced)
            throw new ApiConflictException($"Production is locked to dependency manifest {locked.DependencyManifest.ManifestHash}. Stop Production Runtime before changing referenced media source '{mediaUri}'.");
    }

    public async Task AutoStartAsync(CancellationToken ct)
    {
        var config = await _configStore.GetAsync(ct);
        lock (_stateSync) _config = config;
        if (!config.AutoStart || string.IsNullOrWhiteSpace(config.JobId)) return;
        try { await StartAsync(config.JobId, ct); }
        catch (Exception ex)
        {
            lock (_stateSync) _lastError = ex.Message;
            await _alarms.RaiseAsync("PROD-START", AlarmSeverity.Critical, "ProductionRuntime", ex.Message, null, CancellationToken.None);
            _logger.LogError(ex, "Production Runtime auto start failed.");
        }
    }

    private async Task LoopGuardedAsync(PublishedJobSnapshot locked, CancellationToken ct)
    {
        try
        {
            await using var writer = new ProductionTraceWriter(_traces, _runs, _traceOptions, _logger);
            lock (_stateSync) _traceWriter = writer;
            await LoopCoreAsync(locked, writer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_stateSync)
            {
                _lastError = ex.Message;
                _state = ProductionRuntimeState.Faulted;
            }
            await _alarms.RaiseAsync("PROD-RUNTIME", AlarmSeverity.Critical, "ProductionRuntime", ex.Message, _currentRunId, CancellationToken.None);
            _logger.LogError(ex, "Production runtime loop faulted unexpectedly.");
        }
        finally { lock (_stateSync) _traceWriter = null; }
    }

    private async Task LoopCoreAsync(PublishedJobSnapshot locked, ProductionTraceWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            writer.ThrowIfFaulted();
            WorkflowRunResult result;
            ProductionRuntimeConfig config;
            lock (_stateSync) config = _config;

            // 周期开始即分配 RunId 并耐久写入起始记录：执行中/排队中进程被杀或断电时，重启后
            // 能列出"已接受未完成"的周期，而不是整条记录缺失（内存队列只承载大图编码，关键
            // 元数据与恢复标记不再仅驻留内存）。起始记录不可写时不盲跑设备，本周期按失败处理。
            var runId = RunTraceRecorder.NewRunId();
            var traceContext = new RunTraceContext("ProductionRuntime", locked.JobId, locked.Version, locked.WorkflowHash,
                DependencyManifestHash: locked.DependencyManifest.ManifestHash);
            string? startRecordError = null;
            try
            {
                using var beginTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _traces.BeginAsync(runId, DateTimeOffset.UtcNow, locked.Workflow, traceContext, beginTimeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                startRecordError = $"Production cycle start record could not be persisted: {ex.Message}";
            }

            if (startRecordError is not null)
            {
                result = new WorkflowRunResult(runId, false, 0, false, 0, 0, [], [], null, startRecordError, "Error")
                {
                    ErrorCode = VisionRunErrorCodes.RuntimeError
                };
            }
            else
            {
                // Q01：副作用流程在**执行任何设备动作之前**耐久登记意图（RunId + 锁定清单 + 指纹）。
                // 进程被杀/断电/追溯队列故障都不会跳过这一步；意图未清除时启动会被闸门阻断，
                // 因此"设备已执行而软件无结果"不可能被当成可开始的新周期。
                bool sideEffectsAtStart;
                lock (_stateSync) sideEffectsAtStart = _hasDeviceSideEffects;
                var intentRecorded = true;
                if (sideEffectsAtStart) intentRecorded = RecordDeviceActionIntent(locked.DependencyManifest, runId);

                if (!intentRecorded)
                {
                    // 安全状态无法持久化 ⇒ 绝不执行设备动作（宁可不跑，不可无记录地跑）。
                    result = new WorkflowRunResult(runId, false, 0, false, 0, 0, [], [], null,
                        "Refused to execute device side effects: the device-action intent could not be persisted. " +
                        "Resolve the storage problem before resuming production.", "Error")
                    {
                        ErrorCode = VisionRunErrorCodes.RuntimeError
                    };
                }
                else
                {
                    try
                    {
                        result = await _runner.RunAsync(
                            locked.Workflow,
                            new VisionRunOptions(DebugRunMode.Full, TimeoutMs: config.MaxCycleMs, Artifacts: writer.ArtifactOptions),
                            runId: runId,
                            cancellationToken: ct);
                    }
                    catch (Exception ex)
                    {
                        result = new WorkflowRunResult(runId, false, 0, false, 0, 0, [], [], null, ex.Message, "Error")
                        {
                            ErrorCode = ct.IsCancellationRequested ? VisionRunErrorCodes.Cancelled : VisionRunErrorCodes.RuntimeError
                        };
                    }
                }
            }

            // Q01：安全判定与置位必须发生在追溯证据落地之前——追溯 writer 故障（队列满/磁盘问题）
            // 绝不能阻断"设备动作不确定"的标记；此前的顺序让 EnqueueAsync 先抛错时置位被整体跳过，
            // 设备动作从此无人核对。
            bool sideEffects;
            lock (_stateSync) sideEffects = _hasDeviceSideEffects;
            var needsManualReconciliation = result.Success && RequiresManualActionReconciliation(locked.Workflow, result);
            var clearIntentAfterTrace = sideEffects && result.Success && !needsManualReconciliation;
            if (needsManualReconciliation)
            {
                const string message = "The workflow accepted an asynchronous robot command without confirming completion; administrator reconciliation is required before another cycle.";
                result = result with { Success = false, Error = message, ErrorCode = VisionRunErrorCodes.RuntimeError, QualityDisposition = "ERROR" };
            }
            if (sideEffects && !clearIntentAfterTrace)
            {
                SetDeviceActionUnknownState(locked.DependencyManifest,
                    needsManualReconciliation
                        ? "robot command was accepted without confirmed completion; actual motion state requires administrator reconciliation"
                        : ct.IsCancellationRequested
                            ? "cycle was cancelled while executing a side-effect workflow; device actions may have been partially applied"
                            : "side-effect workflow failed; device actions from the failed cycle are indeterminate",
                    result.RunId);
            }

            // 生产使用等待式写入：Task 完成只在 TraceabilityStore 的持久化步骤成功后返回。
            // 未确认动作、动作失败或 trace 写入失败时意图一直保留；只有同步动作成功且 trace 已提交，
            // 才删除意图记录。取消/进程终止也不会走到清除分支。
            var tracePersisted = false;
            try
            {
                await writer.EnqueueAndPersistAsync(result, locked.Workflow, traceContext);
                tracePersisted = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Production trace evidence for run {RunId} could not be durably committed; the device-action intent remains in force.", result.RunId);
                if (sideEffects && clearIntentAfterTrace)
                    result = result with { Success = false, Error = $"Trace evidence was not committed: {ex.Message}", ErrorCode = VisionRunErrorCodes.RuntimeError, QualityDisposition = "ERROR" };
            }
            if (clearIntentAfterTrace && tracePersisted)
            {
                try { ClearDeviceActionUnknownState(); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Run {RunId} trace was committed, but clearing its safety intent failed; the runtime remains gated.", result.RunId);
                    result = result with { Success = false, Error = $"Device-action safety state could not be cleared: {ex.Message}", ErrorCode = VisionRunErrorCodes.RuntimeError, QualityDisposition = "ERROR" };
                }
            }
            if (ct.IsCancellationRequested)
            {
                // F01：取消可能发生在设备动作执行中途——安全置位已在上方统一处理（与追溯队列无关），
                // 此处只需停止循环。
                break;
            }

            var disposition = result.Success ? (result.QualityDisposition ?? "OK") : "ERROR";
            lock (_stateSync)
            {
                _currentRunId = result.RunId;
                _lastCycleAt = DateTimeOffset.UtcNow;
                _lastDurationMs = result.TotalDurationMs;
                _lastDisposition = disposition;
                _lastError = result.Error;
                _cycleCount++;
            }

            if (result.Success)
            {
                lock (_stateSync)
                {
                    _consecutiveFailures = 0;
                    _recoveryAttempts = 0; // 故障事件结束：恢复预算随成功运行重置
                    if (string.Equals(disposition, "NG", StringComparison.OrdinalIgnoreCase)) _ngCount++;
                    else _okCount++;
                }
                await _alarms.RecoverAsync("PROD-RUN-FAILURE", "ProductionRuntime", CancellationToken.None);
                await _alarms.RecoverAsync("PROD-WATCHDOG", "ProductionRuntime", CancellationToken.None);
            }
            else
            {
                var watchdog = result.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true;
                int failures;
                bool hasDeviceSideEffects;
                lock (_stateSync)
                {
                    _errorCount++;
                    _consecutiveFailures++;
                    failures = _consecutiveFailures;
                    hasDeviceSideEffects = _hasDeviceSideEffects;
                    if (watchdog) _watchdogTrips++;
                }

                if (watchdog)
                    await _alarms.RaiseAsync("PROD-WATCHDOG", AlarmSeverity.Error, "ProductionRuntime", result.Error ?? "Production cycle watchdog timeout.", result.RunId, CancellationToken.None);
                await _alarms.RaiseAsync("PROD-RUN-FAILURE", AlarmSeverity.Error, "ProductionRuntime", result.Error ?? "Production cycle failed.", result.RunId, CancellationToken.None);

                // 含设备副作用（PLC 写入/机器人动作）的流程：失败一次即锁定。上一周期的真实设备
                // 动作结果在核对前不可确定，任何重跑都会再次触发物理动作——阈值只适用于可以安全
                // 重试的纯计算错误，绝不能用来给副作用流程"再多试两次"。锁定 Faulted，由人工核对
                // 设备状态后经 /api/production/recover 恢复（该入口已有启动校验与租约路径）。
                if (hasDeviceSideEffects)
                {
                    var reason = $"Faulted after {failures} consecutive failure(s); the workflow has device side effects - " +
                                 "device actions from the failed cycle are indeterminate, so automatic replay is disabled. " +
                                 "Verify actual device state before manual recovery.";
                    lock (_stateSync)
                    {
                        _state = ProductionRuntimeState.Faulted;
                        _lastError = reason;
                    }
                    // F01/Q01：未知动作标记已在上方（追溯落地之前）统一置位并持久化——此处不再重复写入，
                    // 保证"副作用失败"与"置位"之间不存在任何可被队列故障插入的间隙。
                    await _alarms.RaiseAsync("PROD-FAULTED", AlarmSeverity.Critical, "ProductionRuntime", reason, result.RunId, CancellationToken.None);
                    return;
                }

                if (failures >= config.MaxConsecutiveFailures)
                {
                    if (!config.AutoRecover)
                    {
                        var reason = $"Faulted after {failures} consecutive failures.";
                        lock (_stateSync)
                        {
                            _state = ProductionRuntimeState.Faulted;
                            _lastError = reason;
                        }
                        await _alarms.RaiseAsync("PROD-FAULTED", AlarmSeverity.Critical, "ProductionRuntime", reason, result.RunId, CancellationToken.None);
                        return;
                    }

                    // 纯计算流程允许自动恢复，但同一故障事件有恢复预算：耗尽后锁定 Faulted 等待人工
                    // 介入，杜绝"恢复-立即再失败"的无限循环（成功运行会重置预算）。
                    int attempts;
                    lock (_stateSync)
                    {
                        _recoveryAttempts++;
                        attempts = _recoveryAttempts;
                    }
                    if (attempts > config.MaxRecoveryAttempts)
                    {
                        var exhausted = $"Automatic recovery budget exhausted after {attempts - 1} attempts (MaxRecoveryAttempts={config.MaxRecoveryAttempts}); locked Faulted for manual recovery.";
                        lock (_stateSync)
                        {
                            _state = ProductionRuntimeState.Faulted;
                            _lastError = exhausted;
                        }
                        await _alarms.RaiseAsync("PROD-RECOVERY-BUDGET", AlarmSeverity.Critical, "ProductionRuntime", exhausted, result.RunId, CancellationToken.None);
                        return;
                    }

                    lock (_stateSync) _state = ProductionRuntimeState.Recovering;
                    if (config.RecoveryDelayMs > 0) await Task.Delay(config.RecoveryDelayMs, ct);
                    lock (_stateSync)
                    {
                        _consecutiveFailures = 0;
                        _state = ProductionRuntimeState.Running;
                    }
                }
            }

            long cycleCount;
            lock (_stateSync) cycleCount = _cycleCount;
            if (config.PtpDriftGuardEnabled && cycleCount % config.PtpGuardCheckEveryCycles == 0)
            {
                var guard = await _ptpGuard.EvaluateAsync(locked.Workflow, config, ct);
                lock (_stateSync) _ptpGuardStatus = guard;
                if (!guard.Healthy)
                {
                    var error = $"Production PTP drift guard faulted runtime: {guard.Summary}";
                    lock (_stateSync)
                    {
                        _state = ProductionRuntimeState.Faulted;
                        _lastError = error;
                    }
                    await RaisePtpGuardIssuesAsync(guard, result.RunId, CancellationToken.None);
                    await _alarms.RaiseAsync("PROD-FAULTED", AlarmSeverity.Critical, "ProductionRuntime", error, result.RunId, CancellationToken.None);
                    return;
                }
                await RecoverPtpGuardAlarmsAsync(CancellationToken.None);
            }

            if (config.SynchronizationHealthGuardEnabled && cycleCount % config.SynchronizationGuardCheckEveryCycles == 0)
            {
                var syncGuard = await _synchronizationGuard.EvaluateAsync(locked.Workflow, config, ct);
                lock (_stateSync) _synchronizationGuardStatus = syncGuard;
                if (syncGuard.Healthy)
                {
                    int healthyChecks;
                    lock (_stateSync)
                    {
                        _synchronizationGuardUnhealthyChecks = 0;
                        _synchronizationGuardHealthyChecks++;
                        healthyChecks = _synchronizationGuardHealthyChecks;
                    }
                    if (healthyChecks >= config.SynchronizationGuardHealthyChecksToRecover)
                        await RecoverSynchronizationGuardAlarmsAsync(CancellationToken.None);
                }
                else
                {
                    int unhealthyChecks;
                    lock (_stateSync)
                    {
                        _synchronizationGuardHealthyChecks = 0;
                        _synchronizationGuardUnhealthyChecks++;
                        unhealthyChecks = _synchronizationGuardUnhealthyChecks;
                    }
                    var faultNow = unhealthyChecks >= config.SynchronizationGuardUnhealthyChecksToFault;
                    await RaiseSynchronizationGuardIssuesAsync(syncGuard, result.RunId, faultNow ? AlarmSeverity.Critical : AlarmSeverity.Warning, CancellationToken.None);
                    if (faultNow)
                    {
                        if (config.AutoRecover && syncGuard.LiveRecoverableOnly && await TryAutoRecoverSynchronizationGuardAsync(locked, config, result.RunId, ct))
                            continue;

                        var error = $"Production synchronization health guard faulted runtime after {unhealthyChecks} unhealthy checks: {syncGuard.Summary}";
                        lock (_stateSync)
                        {
                            _state = ProductionRuntimeState.Faulted;
                            _lastError = error;
                        }
                        await _alarms.RaiseAsync("PROD-FAULTED", AlarmSeverity.Critical, "ProductionRuntime", error, result.RunId, CancellationToken.None);
                        return;
                    }
                }
            }

            if (config.CycleDelayMs > 0) await Task.Delay(config.CycleDelayMs, ct);
        }
    }

    private async Task<bool> TryAutoRecoverSynchronizationGuardAsync(PublishedJobSnapshot locked, ProductionRuntimeConfig config, string? runId, CancellationToken ct)
    {
        lock (_stateSync)
        {
            _state = ProductionRuntimeState.Recovering;
            _lastError = "Synchronization transport is unhealthy; waiting for stable camera reconnect/recovery.";
        }
        await _alarms.RaiseAsync("PROD-SYNC-RECOVERING", AlarmSeverity.Warning, "ProductionSynchronizationGuard",
            "Synchronization transport fault is live-recoverable. Production is paused until the configured healthy-check hysteresis is satisfied.", runId, CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(config.SynchronizationGuardRecoveryTimeoutMs);
        var healthyChecks = 0;
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var delay = Math.Clamp(config.RecoveryDelayMs <= 0 ? 500 : config.RecoveryDelayMs, 250, 5000);
            await Task.Delay(delay, ct);
            var snapshot = await _synchronizationGuard.EvaluateAsync(locked.Workflow, config, ct);
            lock (_stateSync) _synchronizationGuardStatus = snapshot;

            if (snapshot.Healthy)
            {
                healthyChecks++;
                lock (_stateSync) _synchronizationGuardHealthyChecks = healthyChecks;
                if (healthyChecks >= config.SynchronizationGuardHealthyChecksToRecover)
                {
                    lock (_stateSync)
                    {
                        _synchronizationGuardUnhealthyChecks = 0;
                        _state = ProductionRuntimeState.Running;
                        _lastError = null;
                    }
                    await RecoverSynchronizationGuardAlarmsAsync(CancellationToken.None);
                    return true;
                }
            }
            else
            {
                healthyChecks = 0;
                lock (_stateSync) _synchronizationGuardHealthyChecks = 0;
                await RaiseSynchronizationGuardIssuesAsync(snapshot, runId, AlarmSeverity.Warning, CancellationToken.None);
                if (!snapshot.LiveRecoverableOnly) break;
            }
        }

        return false;
    }

    private async Task RaiseSynchronizationGuardIssuesAsync(ProductionSynchronizationGuardSnapshot guard, string? runId, AlarmSeverity severity, CancellationToken ct)
    {
        foreach (var issue in guard.Issues.GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
            await _alarms.RaiseAsync(issue.Code, severity, "ProductionSynchronizationGuard", issue.Message, runId, ct);
    }

    private async Task RecoverSynchronizationGuardAlarmsAsync(CancellationToken ct)
    {
        foreach (var code in new[]
        {
            "PROD-SYNC-CAMERA-NOT-READY",
            "PROD-SYNC-ACTION",
            "PROD-SYNC-ACTION-ACK",
            "PROD-SYNC-FRAME-TIMEOUT",
            "PROD-SYNC-SKEW",
            "PROD-SYNC-FRAME-LOSS",
            "PROD-SYNC-TRANSPORT",
            "PROD-SYNC-RECOVERING"
        })
            await _alarms.RecoverAsync(code, "ProductionSynchronizationGuard", ct);
    }

    private async Task RaisePtpGuardIssuesAsync(ProductionPtpGuardSnapshot guard, string? runId, CancellationToken ct)
    {
        foreach (var issue in guard.Issues.GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
            await _alarms.RaiseAsync(issue.Code, AlarmSeverity.Critical, "ProductionPtpGuard", issue.Message, runId, ct);
    }

    private async Task RecoverPtpGuardAlarmsAsync(CancellationToken ct)
    {
        await _alarms.RecoverAsync("PROD-PTP-NOT-READY", "ProductionPtpGuard", ct);
        await _alarms.RecoverAsync("PROD-PTP-DRIFT", "ProductionPtpGuard", ct);
        await _alarms.RecoverAsync("PROD-PTP-MASTER-CLOCK", "ProductionPtpGuard", ct);
    }
}
