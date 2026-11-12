using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class ProductionRuntimeService
{
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
                // R03：起始登记是关键恢复锚点——强持久提交（断电后"已接受未完成"依然可见）。
                await _traces.BeginAsync(runId, DateTimeOffset.UtcNow, locked.Workflow, traceContext, beginTimeout.Token, strongDurability: true);
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
                        // A02：生产循环的真实派发携带生产租约令牌——令牌被换代/撤销后，
                        // 旧周期的后续设备命令在派发边界被拒绝。
                        using var commandScope = DeviceCommandOwnership.Enter(_lease);
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
                // R05：提交等待必须有界（含图像编码/文件 I/O/数据库提交）。超限进入 Faulted 并保留意图，
                // 在途提交未收尾前禁止新周期——不允许"超时返回继续生产"。
                using var traceBudget = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1_000, _traceOptions.CommitBudgetMs)));
                using var traceWait = CancellationTokenSource.CreateLinkedTokenSource(ct, traceBudget.Token);
                await writer.EnqueueAndPersistAsync(result, locked.Workflow, traceContext, traceWait.Token);
                tracePersisted = true;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // 提交预算耗尽：后台提交可能仍未收尾（writer 记入在途闸门，Drain 也会再有界等待一次）。
                _drainingTraceWriter = writer;
                var message = $"Trace evidence for run '{result.RunId}' was not committed within the {_traceOptions.CommitBudgetMs} ms budget; " +
                              "the loop stops in Faulted until the pending record drains. The device-action intent remains in force.";
                _logger.LogError(ex, "Production trace commit budget exhausted for run {RunId}.", result.RunId);
                lock (_stateSync)
                {
                    _state = ProductionRuntimeState.Faulted;
                    _lastError = message;
                }
                await _alarms.RaiseAsync("PROD-TRACE-TIMEOUT", AlarmSeverity.Critical, "ProductionRuntime", message, result.RunId, CancellationToken.None);
                return;
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
