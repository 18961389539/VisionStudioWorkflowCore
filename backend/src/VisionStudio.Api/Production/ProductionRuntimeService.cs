using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

/// <summary>
/// 多工位并行生产运行时管理器，同时保持默认工位（"default"）的向后兼容门面。
/// 每个工位拥有独立的生命周期状态机、执行循环、配置存储（runtime.{stationId}.json）与
/// 安全状态文件（device-action-safety.{stationId}.json）；硬件互斥由共享的
/// <see cref="DeviceLeaseRegistry"/> 按资产粒度仲裁，互不重叠硬件的工位可同时并行生产。
/// </summary>
public sealed class ProductionRuntimeService
{
    private readonly JobStore _jobs;
    private readonly IVisionWorkflowRunner _runner;
    private readonly TraceabilityStore _traces;
    private readonly RunStore _runs;
    private readonly AlarmStore _alarms;
    private readonly RuntimeDependencyManifestService _dependencies;
    private readonly StorageCapacityService _storageCapacity;
    private readonly ProductionPtpDriftGuardService _ptpGuard;
    private readonly ProductionSynchronizationHealthGuardService _synchronizationGuard;
    private readonly DeviceLeaseRegistry _leases;
    private readonly ILogger<ProductionRuntimeService> _logger;
    private readonly IOptions<ProductionTraceOptions>? _traceOptions;
    private readonly WorkflowModuleExpander? _moduleExpander;
    private readonly DeviceActionAuthorizationService? _authorization;
    private readonly VisionNodeRegistry? _nodes;
    private readonly RuntimeWorkloadGate _workloads;
    private readonly IWebHostEnvironment? _env;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<DeviceActionSafetyStore>? _safetyLogger;
    private readonly ProductionSafetyLedgerRegistry _safetyLedgers;
    private readonly ConcurrentDictionary<string, ProductionStationRuntime> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProductionTriggerQueue> _triggerQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProductionStationResultSnapshot> _latestResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim? _executionSlotGate;
    private readonly ProductionStationRuntime _defaultStation;
    private readonly string _fallbackRoot;

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
        IOptions<ProductionTraceOptions>? traceOptions = null,
        DeviceActionSafetyStore? safetyStore = null,
        WorkflowModuleExpander? moduleExpander = null,
        DeviceActionAuthorizationService? authorization = null,
        VisionNodeRegistry? nodes = null,
        RuntimeWorkloadGate? workloads = null,
        IWebHostEnvironment? env = null,
        IConfiguration? configuration = null,
        ILogger<DeviceActionSafetyStore>? safetyLogger = null,
        ProductionSafetyLedgerRegistry? safetyLedgers = null)
    {
        _jobs = jobs;
        _runner = runner;
        _traces = traces;
        _runs = runs;
        _alarms = alarms;
        _dependencies = dependencies;
        _storageCapacity = storageCapacity;
        _ptpGuard = ptpGuard;
        _synchronizationGuard = synchronizationGuard;
        _leases = leases;
        _logger = logger;
        _traceOptions = traceOptions;
        _moduleExpander = moduleExpander;
        _authorization = authorization;
        _nodes = nodes;
        _workloads = workloads ?? new RuntimeWorkloadGate();
        _env = env;
        _configuration = configuration;
        _safetyLogger = safetyLogger;
        _safetyLedgers = safetyLedgers ?? new ProductionSafetyLedgerRegistry();
        _authorization?.AttachSafetyLedgers(_safetyLedgers);
        var maxConcurrent = configuration?.GetValue<int?>("Production:MaxConcurrentCycles") ?? 0;
        if (maxConcurrent > 0) _executionSlotGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _fallbackRoot = Path.Combine(Path.GetTempPath(), "visionstudio-stations", Guid.NewGuid().ToString("N"));

        _defaultStation = new ProductionStationRuntime(
            ProductionRuntimeStationIds.DefaultStationId,
            jobs,
            runner,
            traces,
            runs,
            configStore,
            alarms,
            dependencies,
            storageCapacity,
            ptpGuard,
            synchronizationGuard,
            leases,
            logger,
            traceOptions,
            safetyStore,
            moduleExpander,
            authorization,
            nodes,
            _workloads,
            _executionSlotGate,
            PublishStationResult,
            _safetyLedgers,
            CreateTriggerQueue(ProductionRuntimeStationIds.DefaultStationId));
        _stations[ProductionRuntimeStationIds.DefaultStationId] = _defaultStation;
        if (safetyStore is not null) _safetyLedgers.Register(ProductionRuntimeStationIds.DefaultStationId, safetyStore);

        if (_env is not null)
        {
            foreach (var stationId in ProductionRuntimeConfigStore.DiscoverStationIds(_env, _configuration))
                GetOrCreateStation(stationId);
        }
    }

    // --- 默认工位兼容门面 ---

    public ProductionRuntimeStatus Status => _defaultStation.Status;

    public ProductionSafetyLedgerRegistry SafetyLedgers => _safetyLedgers;

    public IReadOnlyList<string> TriggerQueuePaths => _triggerQueues.Values.Select(queue => queue.StatePath).ToArray();

    public bool AnyStationProductionLocked => _stations.Values.Any(station => station.Status.ProductionLocked);

    public bool AnyStationFaulted => _stations.Values.Any(station => station.Status.State == ProductionRuntimeState.Faulted);

    public ProductionDeviceActionPendingState? DeviceActionResolutionState => _defaultStation.DeviceActionResolutionState;

    public Task<ProductionRuntimeConfig> GetConfigAsync(CancellationToken ct) => _defaultStation.GetConfigAsync(ct);

    public Task<ProductionRuntimeConfig> UpdateConfigAsync(ProductionRuntimeConfig config, CancellationToken ct, string? expectedRevision = null)
        => _defaultStation.UpdateConfigAsync(config, ct, expectedRevision);

    public Task<ProductionRuntimeStatus> StartAsync(string? jobId, CancellationToken ct)
        => _defaultStation.StartAsync(jobId, ct);

    public Task<ProductionRuntimeStatus> StopAsync(CancellationToken ct)
        => _defaultStation.StopAsync(ct);

    public Task<ProductionRuntimeStatus> RecoverAsync(CancellationToken ct)
        => _defaultStation.RecoverAsync(ct);

    public Task ResolveUnknownDeviceActionsAsync(DeviceActionResolutionRequest request, string operatorName, CancellationToken ct)
        => _defaultStation.ResolveUnknownDeviceActionsAsync(request, operatorName, ct);

    // --- 多工位并行管理 ---

    public IReadOnlyList<ProductionStationRuntimeStatus> ListStatuses()
        => _stations.Values
            .OrderBy(s => ProductionRuntimeStationIds.IsDefault(s.StationId) ? 0 : 1)
            .ThenBy(s => s.StationId, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.StationStatus)
            .ToArray();

    public ProductionStationRuntimeStatus GetStatus(string stationId)
        => GetOrCreateStation(stationId).StationStatus;

    public ProductionDeviceActionPendingState? GetDeviceActionResolutionState(string stationId)
        => GetOrCreateStation(stationId).DeviceActionResolutionState;

    public Task<ProductionRuntimeConfig> GetConfigAsync(string stationId, CancellationToken ct)
        => GetOrCreateStation(stationId).GetConfigAsync(ct);

    public Task<ProductionRuntimeConfig> UpdateConfigAsync(string stationId, ProductionRuntimeConfig config, CancellationToken ct, string? expectedRevision = null)
        => GetOrCreateStation(stationId).UpdateConfigAsync(config, ct, expectedRevision);

    public async Task<ProductionStationRuntimeStatus> StartAsync(string stationId, string? jobId, CancellationToken ct)
    {
        var station = GetOrCreateStation(stationId);
        var status = await station.StartAsync(jobId, ct);
        return ProductionStationRuntimeStatus.From(station.StationId, status);
    }

    public async Task<ProductionStationRuntimeStatus> StopAsync(string stationId, CancellationToken ct)
    {
        var station = GetExistingOrThrow(stationId);
        var status = await station.StopAsync(ct);
        return ProductionStationRuntimeStatus.From(station.StationId, status);
    }

    public async Task<ProductionStationRuntimeStatus> RecoverAsync(string stationId, CancellationToken ct)
    {
        var station = GetExistingOrThrow(stationId);
        var status = await station.RecoverAsync(ct);
        return ProductionStationRuntimeStatus.From(station.StationId, status);
    }

    public Task ResolveUnknownDeviceActionsAsync(string stationId, DeviceActionResolutionRequest request, string operatorName, CancellationToken ct)
        => GetExistingOrThrow(stationId).ResolveUnknownDeviceActionsAsync(request, operatorName, ct);

    public async Task<IReadOnlyList<ProductionStationRuntimeStatus>> StartAllAsync(CancellationToken ct)
    {
        var candidates = _stations.Values.ToArray();
        await Task.WhenAll(candidates.Select(async station =>
        {
            var config = await station.GetConfigAsync(ct);
            if (string.IsNullOrWhiteSpace(config.JobId)) return;
            try
            {
                await station.StartAsync(config.JobId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Station {StationId} failed during StartAll.", station.StationId);
            }
        }));
        return ListStatuses();
    }

    public async Task<IReadOnlyList<ProductionStationRuntimeStatus>> StopAllAsync(CancellationToken ct)
    {
        var stations = _stations.Values.ToArray();
        await Task.WhenAll(stations.Select(s => s.StopAsync(ct)));
        return ListStatuses();
    }

    public async Task RemoveStationAsync(string stationId, CancellationToken ct)
    {
        var normalized = ProductionRuntimeStationIds.Normalize(stationId);
        if (ProductionRuntimeStationIds.IsDefault(normalized))
            throw new ApiValidationException("The default production station cannot be removed.");
        var station = GetExistingOrThrow(normalized);
        await station.DeleteConfigAsync(ct);
        _stations.TryRemove(normalized, out _);
        _latestResults.TryRemove(normalized, out _);
    }

    public ProductionTriggerEnqueueResult? Trigger(string stationId, string? eventId = null)
    {
        var station = GetExistingOrThrow(stationId);
        return station.TriggerOnce(eventId);
    }

    public IReadOnlyList<ProductionTriggerEvent> ListTriggers(string stationId)
        => GetExistingOrThrow(stationId).ListTriggers();

    public ProductionStationResultSnapshot? GetLatestResult(string stationId)
    {
        var normalized = ProductionRuntimeStationIds.Normalize(stationId);
        return _latestResults.TryGetValue(normalized, out var result) ? result : null;
    }

    private void PublishStationResult(ProductionStationResultSnapshot snapshot)
        => _latestResults[snapshot.StationId] = snapshot;

    // --- 跨工位全局联动 ---

    public Task AutoStartAsync(CancellationToken ct)
        => Task.WhenAll(_stations.Values.Select(s => s.AutoStartAsync(ct)));

    public void EnsureDependencyMutationAllowed(string kind, string id)
        => _leases.EnsureNotHeld(kind, id, $"Cannot modify {kind} '{id}'");

    public Task<DeviceLease> AcquireRunLeaseAsync(string runId, WorkflowDefinition workflow, string action, CancellationToken ct)
        => _defaultStation.AcquireRunLeaseAsync(runId, workflow, action, ct);

    public Task EnsureWorkflowRunAllowedAsync(WorkflowDefinition workflow, CancellationToken ct)
        => _defaultStation.EnsureWorkflowRunAllowedAsync(workflow, ct);

    public void EnsureMediaMutationAllowed(string mediaUri)
    {
        foreach (var station in _stations.Values)
            station.EnsureMediaMutationAllowed(mediaUri);
    }

    private ProductionStationRuntime GetExistingOrThrow(string stationId)
    {
        var normalized = ProductionRuntimeStationIds.Normalize(stationId);
        if (_stations.TryGetValue(normalized, out var station))
            return station;
        throw new ApiNotFoundException($"Production station '{normalized}' does not exist.");
    }

    private ProductionStationRuntime GetOrCreateStation(string stationId)
    {
        var normalized = ProductionRuntimeStationIds.Normalize(stationId);
        return _stations.GetOrAdd(normalized, CreateStation);
    }

    private ProductionStationRuntime CreateStation(string stationId)
    {
        ProductionRuntimeConfigStore configStore;
        DeviceActionSafetyStore? safetyStore;
        if (_env is not null)
        {
            configStore = new ProductionRuntimeConfigStore(_env, _configuration, stationId);
            safetyStore = new DeviceActionSafetyStore(_env, _configuration ?? new ConfigurationBuilder().Build(),
                _safetyLogger ?? NullLogger<DeviceActionSafetyStore>.Instance, stationId);
        }
        else
        {
            var stationEnv = new FallbackStationEnvironment(Path.Combine(_fallbackRoot, stationId));
            configStore = new ProductionRuntimeConfigStore(stationEnv, _configuration, stationId);
            safetyStore = new DeviceActionSafetyStore(
                Path.Combine(_fallbackRoot, stationId, "production", $"device-action-safety.{stationId}.json"),
                _safetyLogger ?? NullLogger<DeviceActionSafetyStore>.Instance);
        }

        return new ProductionStationRuntime(
            stationId,
            _jobs,
            _runner,
            _traces,
            _runs,
            configStore,
            _alarms,
            _dependencies,
            _storageCapacity,
            _ptpGuard,
            _synchronizationGuard,
            _leases,
            _logger,
            _traceOptions,
            safetyStore,
            _moduleExpander,
            _authorization,
            _nodes,
            _workloads,
            _executionSlotGate,
            PublishStationResult,
            _safetyLedgers,
            CreateTriggerQueue(stationId));
    }

    private ProductionTriggerQueue CreateTriggerQueue(string stationId)
    {
        var normalized = ProductionRuntimeStationIds.Normalize(stationId);
        return _triggerQueues.GetOrAdd(normalized, id =>
        {
            var root = _env is not null
                ? Path.Combine(VisionStudioDataRoot.Resolve(_env.ContentRootPath, _configuration ?? new ConfigurationBuilder().Build()), "production")
                : Path.Combine(_fallbackRoot, id, "production");
            var fileName = ProductionRuntimeStationIds.IsDefault(id)
                ? "trigger-queue.json"
                : $"trigger-queue.{id}.json";
            return new ProductionTriggerQueue(Path.Combine(root, fileName));
        });
    }

    private sealed class FallbackStationEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VisionStudio";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
