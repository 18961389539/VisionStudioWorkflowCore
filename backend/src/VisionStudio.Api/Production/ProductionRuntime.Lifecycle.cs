using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class ProductionRuntimeService
{
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
        var writer = new ProductionTraceWriter(_traces, _runs, _traceOptions, _logger);
        try
        {
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
        finally
        {
            lock (_stateSync)
            {
                // R05：循环退出时若有在途提交未收尾（预算超时/存储挂起），保留引用以禁止新周期直到排空。
                if (writer.PendingCount > 0) _drainingTraceWriter = writer;
                _traceWriter = null;
            }
            // R05/F02（2026-10-12 复核）：writer 释放必须在本异常边界内——追溯 worker 失败时
            // DisposeAsync 会重抛持久化异常；此前它在 try 之外（await using）逃出本方法，把 loop
            // Task 变成 faulted，Stop 等待时吞不掉、状态永久卡在 Stopping。失败证据在此记录并吸收；
            // 未闭合的设备动作意图不受影响（仍会阻断后续启动）。
            try { await writer.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Production trace writer drain surfaced a persistence failure during shutdown; the device-action intent gate remains in force.");
            }
        }
    }

}
