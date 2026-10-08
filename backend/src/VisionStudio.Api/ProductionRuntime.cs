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
    int SynchronizationGuardRecoveryTimeoutMs = 30000);

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
    int TraceQueueCapacity = 8);

public sealed record ProductionStartRequest(string? JobId = null);

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
        var dir = Path.Combine(env.ContentRootPath, "data", "production");
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
        Microsoft.Extensions.Options.IOptions<ProductionTraceOptions>? traceOptions = null)
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
                    _traceOptions.QueueCapacity);
            }
        }
    }

    public Task<ProductionRuntimeConfig> GetConfigAsync(CancellationToken ct) => _configStore.GetAsync(ct);

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
            await _dependencies.ValidateOrThrowAsync(snapshot.DependencyManifest, snapshot.Workflow, ct);
            await _alarms.RecoverAsync("PROD-DEPENDENCY-DRIFT", "ProductionRuntime", ct);

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

    public async Task<ProductionRuntimeStatus> RecoverAsync(CancellationToken ct)
    {
        if (Status.State != ProductionRuntimeState.Faulted) return Status;
        if (_loopTask is { IsCompleted: false })
            throw new InvalidOperationException("Cannot recover while the previous execution loop is still alive. Stop/terminate the blocked operation first.");

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
            try
            {
                result = await _runner.RunAsync(
                    locked.Workflow,
                    new VisionRunOptions(DebugRunMode.Full, TimeoutMs: config.MaxCycleMs, Artifacts: writer.ArtifactOptions),
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                result = new WorkflowRunResult(Guid.NewGuid().ToString("N"), false, 0, false, 0, 0, [], [], null, ex.Message, "Error")
                {
                    ErrorCode = ct.IsCancellationRequested ? VisionRunErrorCodes.Cancelled : VisionRunErrorCodes.RuntimeError
                };
            }

            // Preserve completed/current cycle evidence even if stop was requested during execution.
            await writer.EnqueueAsync(result, locked.Workflow,
                new RunTraceContext("ProductionRuntime", locked.JobId, locked.Version, locked.WorkflowHash,
                    DependencyManifestHash: locked.DependencyManifest.ManifestHash));
            if (ct.IsCancellationRequested) break;

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
                lock (_stateSync)
                {
                    _errorCount++;
                    _consecutiveFailures++;
                    failures = _consecutiveFailures;
                    if (watchdog) _watchdogTrips++;
                }

                if (watchdog)
                    await _alarms.RaiseAsync("PROD-WATCHDOG", AlarmSeverity.Error, "ProductionRuntime", result.Error ?? "Production cycle watchdog timeout.", result.RunId, CancellationToken.None);
                await _alarms.RaiseAsync("PROD-RUN-FAILURE", AlarmSeverity.Error, "ProductionRuntime", result.Error ?? "Production cycle failed.", result.RunId, CancellationToken.None);

                if (failures >= config.MaxConsecutiveFailures)
                {
                    if (!config.AutoRecover)
                    {
                        lock (_stateSync) _state = ProductionRuntimeState.Faulted;
                        await _alarms.RaiseAsync("PROD-FAULTED", AlarmSeverity.Critical, "ProductionRuntime", $"Faulted after {failures} consecutive failures.", result.RunId, CancellationToken.None);
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
