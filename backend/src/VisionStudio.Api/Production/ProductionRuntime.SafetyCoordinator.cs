using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

internal sealed record ProductionStationSharedServices(
    JobStore Jobs,
    IVisionWorkflowRunner Runner,
    TraceabilityStore Traces,
    RunStore Runs,
    AlarmStore Alarms,
    RuntimeDependencyManifestService Dependencies,
    StorageCapacityService StorageCapacity,
    ProductionPtpDriftGuardService PtpGuard,
    ProductionSynchronizationHealthGuardService SynchronizationGuard,
    DeviceLeaseRegistry Leases,
    ILogger Logger,
    ProductionTraceOptions TraceOptions,
    WorkflowModuleExpander? ModuleExpander,
    DeviceActionAuthorizationService? Authorization,
    VisionNodeRegistry? Nodes,
    RuntimeWorkloadGate Workloads,
    SemaphoreSlim? ExecutionSlotGate,
    Action<ProductionStationResultSnapshot>? OnCycleCompleted,
    ProductionSafetyLedgerRegistry? SafetyLedgers);

/// <summary>
/// Owns one production station's persisted device-action safety state machine (intent registration,
/// unknown-action gating across restarts, fingerprint checks, and administrator reconciliation).
/// </summary>
internal sealed class ProductionStationSafetyCoordinator
{
    private readonly DeviceActionSafetyStore? _safetyStore;
    private readonly RuntimeDependencyManifestService _dependencies;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    private bool _deviceActionStateUnknown;
    private string? _unknownActionManifestHash;
    private string? _unknownActionRunId;
    private DeviceActionSafetyPhase _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
    private string[] _unknownActionDeviceIds = [];
    private string[] _unknownActionRobotIds = [];
    private string[] _unknownActionCameraIds = [];
    private IReadOnlyDictionary<string, string> _unknownActionDeviceFingerprints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool _unknownActionUnverifiable;

    public ProductionStationSafetyCoordinator(
        DeviceActionSafetyStore? safetyStore,
        RuntimeDependencyManifestService dependencies,
        ILogger logger)
    {
        _safetyStore = safetyStore;
        _dependencies = dependencies;
        _logger = logger;

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

    public bool HasUnresolvedActions
    {
        get { lock (_sync) return _deviceActionStateUnknown; }
    }

    public ProductionDeviceActionPendingState? ResolutionState
    {
        get
        {
            lock (_sync)
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

    public Task EnsureNoUnknownDeviceActionsAsync()
    {
        bool unknown;
        string? unknownHash;
        string[] deviceIds;
        string[] robotIds;
        bool unverifiable;
        IReadOnlyDictionary<string, string> fingerprints;
        string? runId;
        DeviceActionSafetyPhase phase;
        lock (_sync)
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
        // 原故障设备从未被检查；Q02：连同登记时的设备指纹一起核对，并检查机器人是否有未返回的停止调用。
        var verification = _dependencies.VerifyLockedDevices(deviceIds, robotIds, fingerprints);
        _logger.LogWarning(
            "Production start blocked: run {RunId} has unresolved {Phase} device action intent (manifest {ManifestHash}); live snapshot verification={Verification}. Automatic recovery cannot prove command completion.",
            runId, phase, unknownHash, verification.Summary);
        throw new ApiConflictException(
            $"Device actions from run '{runId ?? "unknown"}' remain unconfirmed (manifest {unknownHash}). " +
            $"Current live-state checks: {verification.Summary}. Connection and fresh samples do not prove that the previous command completed. " +
            "Stop Production Runtime and use the administrator device-action reconciliation endpoint with recorded physical evidence.");
    }

    public void SetUnknownState(RuntimeDependencyManifest manifest, string reason, string? runId = null)
    {
        var (deviceIds, robotIds, cameraIds, fingerprints) = RecordInMemory(manifest, runId, DeviceActionSafetyPhase.Unknown);
        try
        {
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.Save(new DeviceActionSafetyState(manifest.ManifestHash, deviceIds, robotIds, DateTimeOffset.UtcNow, reason,
                Phase: DeviceActionSafetyPhase.Unknown, RunId: runId, DeviceFingerprints: fingerprints,
                ProcessId: DeviceActionSafetyStore.CurrentProcessId, CameraIds: cameraIds));
            lock (_sync) _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist the device-action safety state (manifest {ManifestHash}); the in-memory marker stays in force, " +
                "and the pre-cycle intent record remains on disk so a restart still requires verification.",
                manifest.ManifestHash);
        }
    }

    public bool RecordIntent(RuntimeDependencyManifest manifest, string runId)
    {
        var (deviceIds, robotIds, cameraIds, fingerprints) = RecordInMemory(manifest, runId, DeviceActionSafetyPhase.IntentRecorded);
        try
        {
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.Save(new DeviceActionSafetyState(manifest.ManifestHash, deviceIds, robotIds, DateTimeOffset.UtcNow,
                $"side-effect cycle {runId} started; the intent was registered before any device action executed",
                Phase: DeviceActionSafetyPhase.IntentRecorded, RunId: runId, DeviceFingerprints: fingerprints,
                ProcessId: DeviceActionSafetyStore.CurrentProcessId, CameraIds: cameraIds));
            lock (_sync) { _unknownActionRunId = runId; _unknownActionPhase = DeviceActionSafetyPhase.IntentRecorded; }
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

    public void Clear()
    {
        if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured; refusing to clear the in-memory gate.");
        _safetyStore.Clear();
        lock (_sync)
        {
            _deviceActionStateUnknown = false;
            _unknownActionManifestHash = null;
            _unknownActionRunId = null;
            _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
            _unknownActionDeviceIds = [];
            _unknownActionRobotIds = [];
            _unknownActionCameraIds = [];
            _unknownActionDeviceFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _unknownActionUnverifiable = false;
        }
    }

    public void Resolve(DeviceActionResolutionRequest request, string operatorName)
    {
        string? runId;
        string? manifestHash;
        string[] deviceIds;
        string[] robotIds;
        string[] cameraIds;
        bool unverifiable;
        lock (_sync)
        {
            if (!_deviceActionStateUnknown) throw new ApiConflictException("There is no unresolved device-action state to reconcile.");
            runId = _unknownActionRunId;
            manifestHash = _unknownActionManifestHash;
            deviceIds = _unknownActionDeviceIds;
            robotIds = _unknownActionRobotIds;
            cameraIds = _unknownActionCameraIds;
            unverifiable = _unknownActionUnverifiable;
        }
        if (unverifiable || string.IsNullOrWhiteSpace(manifestHash))
            throw new ApiConflictException("The persisted safety file is unreadable; this endpoint cannot verify its original manifest and device list. Preserve the file and have an administrator perform a separately audited, offline recovery procedure.");

        var expectedRunId = string.IsNullOrWhiteSpace(runId) ? "legacy" : runId;
        if (!string.Equals(request.RunId, expectedRunId, StringComparison.Ordinal) || !string.Equals(request.ManifestHash, manifestHash, StringComparison.Ordinal) ||
            !deviceIds.Order(StringComparer.Ordinal).SequenceEqual((request.DeviceIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !robotIds.Order(StringComparer.Ordinal).SequenceEqual((request.RobotIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !cameraIds.Order(StringComparer.Ordinal).SequenceEqual((request.CameraIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ApiConflictException("Resolution must identify the exact original run, manifest, devices, robots, and cameras recorded in the safety state.");

        var pendingRobots = _dependencies.VerifyNoPendingRobotOperations(robotIds);
        if (!pendingRobots.Ok)
            throw new ApiConflictException($"Cannot reconcile while robot operations remain active: {pendingRobots.Summary}");

        var busyCameras = cameraIds.Where(_dependencies.IsCameraActionUnresolved).ToArray();
        if (busyCameras.Length > 0)
            throw new ApiConflictException(
                $"Cannot reconcile while camera actions are still in in-flight or unconfirmed: {string.Join(", ", busyCameras)}.");

        var resolution = new DeviceActionSafetyResolution(expectedRunId, manifestHash, deviceIds, robotIds, operatorName,
            request.Reason.Trim(), request.Evidence.Trim(), request.DeviceIdentityEvidence.Trim(), DateTimeOffset.UtcNow,
            CameraIds: cameraIds);
        if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
        _safetyStore.SaveResolution(resolution);
        Clear();
        _logger.LogWarning("Administrator {Operator} manually reconciled device action run {RunId} with physical evidence: {Reason}", operatorName, expectedRunId, request.Reason);
    }

    private (string[] Devices, string[] Robots, string[] Cameras, Dictionary<string, string> Fingerprints) RecordInMemory(
        RuntimeDependencyManifest manifest,
        string? runId,
        DeviceActionSafetyPhase phase)
    {
        var deviceIds = manifest.Devices.Select(x => x.Id).ToArray();
        var robotIds = manifest.Robots.Select(x => x.Id).ToArray();
        var cameraIds = manifest.Cameras.Select(x => x.Id).ToArray();
        var fingerprints = CaptureDeviceFingerprints(manifest);
        lock (_sync)
        {
            _deviceActionStateUnknown = true;
            _unknownActionManifestHash = manifest.ManifestHash;
            _unknownActionRunId = runId;
            _unknownActionPhase = phase;
            _unknownActionDeviceIds = deviceIds;
            _unknownActionRobotIds = robotIds;
            _unknownActionCameraIds = cameraIds;
            _unknownActionDeviceFingerprints = fingerprints;
            _unknownActionUnverifiable = false;
        }
        return (deviceIds, robotIds, cameraIds, fingerprints);
    }

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
}
