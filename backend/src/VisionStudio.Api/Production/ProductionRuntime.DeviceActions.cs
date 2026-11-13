using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class ProductionRuntimeService
{
    private void EnsureNoDrainingTraceWriter()
    {
        var draining = _drainingTraceWriter;
        if (draining is null) return;
        if (draining.PendingCount > 0)
            throw new ApiConflictException(
                $"{draining.PendingCount} production trace record(s) from the previous loop are still draining to storage; " +
                "wait for the persistence backend to recover, or restart the host (the device-action intent gate survives a restart).");
        _drainingTraceWriter = null;
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
        string[] cameraIds;
        Dictionary<string, string> fingerprints;
        lock (_stateSync)
        {
            _deviceActionStateUnknown = true;
            _unknownActionManifestHash = manifest.ManifestHash;
            _unknownActionRunId = runId;
            _unknownActionPhase = DeviceActionSafetyPhase.Unknown;
            _unknownActionDeviceIds = deviceIds = manifest.Devices.Select(x => x.Id).ToArray();
            _unknownActionRobotIds = robotIds = manifest.Robots.Select(x => x.Id).ToArray();
            // R02（2026-10-11 复审）：相机动作纳入同一耐久安全状态——没有相机清单就无法核对相机动作。
            _unknownActionCameraIds = cameraIds = manifest.Cameras.Select(x => x.Id).ToArray();
            _unknownActionDeviceFingerprints = fingerprints = CaptureDeviceFingerprints(manifest);
            _unknownActionUnverifiable = false;
        }
        try
        {
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.Save(new DeviceActionSafetyState(manifest.ManifestHash, deviceIds, robotIds, DateTimeOffset.UtcNow, reason,
                Phase: DeviceActionSafetyPhase.Unknown, RunId: runId, DeviceFingerprints: fingerprints,
                ProcessId: DeviceActionSafetyStore.CurrentProcessId, CameraIds: cameraIds));
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
        string[] cameraIds;
        Dictionary<string, string> fingerprints;
        lock (_stateSync)
        {
            _deviceActionStateUnknown = true;
            _unknownActionManifestHash = manifest.ManifestHash;
            _unknownActionRunId = runId;
            _unknownActionPhase = DeviceActionSafetyPhase.IntentRecorded;
            _unknownActionDeviceIds = deviceIds = manifest.Devices.Select(x => x.Id).ToArray();
            _unknownActionRobotIds = robotIds = manifest.Robots.Select(x => x.Id).ToArray();
            // R02（2026-10-11 复审）：相机动作纳入同一耐久安全状态——没有相机清单就无法核对相机动作。
            _unknownActionCameraIds = cameraIds = manifest.Cameras.Select(x => x.Id).ToArray();
            _unknownActionDeviceFingerprints = fingerprints = CaptureDeviceFingerprints(manifest);
            _unknownActionUnverifiable = false;
        }
        try
        {
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.Save(new DeviceActionSafetyState(manifest.ManifestHash, deviceIds, robotIds, DateTimeOffset.UtcNow,
                $"side-effect cycle {runId} started; the intent was registered before any device action executed",
                Phase: DeviceActionSafetyPhase.IntentRecorded, RunId: runId, DeviceFingerprints: fingerprints,
                ProcessId: DeviceActionSafetyStore.CurrentProcessId, CameraIds: cameraIds));
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
            _unknownActionCameraIds = [];
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
            string[] cameraIds;
            bool unverifiable;
            lock (_stateSync)
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
            // Legacy safety records predate RunId. Permit explicit reconciliation only when the request uses
            // the fixed legacy sentinel and still matches the exact persisted manifest and asset lists.
            var expectedRunId = string.IsNullOrWhiteSpace(runId) ? "legacy" : runId;
            if (!string.Equals(request.RunId, expectedRunId, StringComparison.Ordinal) || !string.Equals(request.ManifestHash, manifestHash, StringComparison.Ordinal) ||
                !deviceIds.Order(StringComparer.Ordinal).SequenceEqual((request.DeviceIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
                !robotIds.Order(StringComparer.Ordinal).SequenceEqual((request.RobotIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
                !cameraIds.Order(StringComparer.Ordinal).SequenceEqual((request.CameraIds ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new ApiConflictException("Resolution must identify the exact original run, manifest, devices, robots, and cameras recorded in the safety state.");

            var pendingRobots = _dependencies.VerifyNoPendingRobotOperations(robotIds);
            if (!pendingRobots.Ok)
                throw new ApiConflictException($"Cannot reconcile while robot operations remain active: {pendingRobots.Summary}");
            // R02（2026-10-11 复审）：相机在途同样阻断生产核对——触发/采集/停止未确认不得凭说明放行。
            var busyCameras = cameraIds.Where(_dependencies.IsCameraActionUnresolved).ToArray();
            if (busyCameras.Length > 0)
                throw new ApiConflictException(
                    $"Cannot reconcile while camera actions are still in flight or unconfirmed: {string.Join(", ", busyCameras)}.");

            var resolution = new DeviceActionSafetyResolution(expectedRunId, manifestHash, deviceIds, robotIds, operatorName,
                request.Reason.Trim(), request.Evidence.Trim(), request.DeviceIdentityEvidence.Trim(), DateTimeOffset.UtcNow,
                CameraIds: cameraIds);
            if (_safetyStore is null) throw new InvalidOperationException("Device-action safety persistence is not configured.");
            _safetyStore.SaveResolution(resolution);
            ClearDeviceActionUnknownState();
            _logger.LogWarning("Administrator {Operator} manually reconciled device action run {RunId} with physical evidence: {Reason}", operatorName, expectedRunId, request.Reason);
        }
        finally { _gate.Release(); }
    }

    // C07：副作用分类与完成度判定统一在 DeviceSideEffectClassifier（生产循环不再另写一份）。
    // 扩展契约：携带目录注册表按**节点级参数**求值（声明条件只在满足时才算动作）。
    private bool IsDeviceSideEffectNode(NodeDefinition node) => DeviceSideEffectClassifier.IsDeviceSideEffectNode(node, _nodes);

    private bool RequiresManualActionReconciliation(WorkflowDefinition workflow, WorkflowRunResult result)
        => DeviceSideEffectClassifier.RequiresManualReconciliation(workflow, result, _nodes);

}
