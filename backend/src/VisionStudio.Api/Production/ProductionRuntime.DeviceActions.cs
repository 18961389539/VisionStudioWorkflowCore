using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

internal sealed partial class ProductionStationRuntime
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

    private Task EnsureNoUnknownDeviceActionsAsync()
        => _safetyCoordinator.EnsureNoUnknownDeviceActionsAsync();

    private void SetDeviceActionUnknownState(RuntimeDependencyManifest manifest, string reason, string? runId = null)
        => _safetyCoordinator.SetUnknownState(manifest, reason, runId);

    private bool RecordDeviceActionIntent(RuntimeDependencyManifest manifest, string runId)
        => _safetyCoordinator.RecordIntent(manifest, runId);

    private void ClearDeviceActionUnknownState()
        => _safetyCoordinator.Clear();

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

            _safetyCoordinator.Resolve(request, operatorName);
        }
        finally { _gate.Release(); }
    }

    // C07：副作用分类与完成度判定统一在 DeviceSideEffectClassifier（生产循环不再另写一份）。
    // 扩展契约：携带目录注册表按**节点级参数**求值（声明条件只在满足时才算动作）。
    private bool IsDeviceSideEffectNode(NodeDefinition node) => DeviceSideEffectClassifier.IsDeviceSideEffectNode(node, _nodes);

    private bool RequiresManualActionReconciliation(WorkflowDefinition workflow, WorkflowRunResult result)
        => DeviceSideEffectClassifier.RequiresManualReconciliation(workflow, result, _nodes);
}
