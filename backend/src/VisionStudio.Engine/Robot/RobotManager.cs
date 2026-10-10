using System.Collections.Concurrent;

namespace VisionStudio.Engine.Robot;

/// <summary>
/// Process-level owner of robot adapters. Workflow nodes request robot state/commands through this service;
/// vendor connection objects never live inside Workflow Core instances.
/// V0.19 also owns the protocol-neutral industrial handshake orchestration and retry/timeout policy.
/// </summary>
public sealed class RobotManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IRobot2DAdapter> _robots = new(StringComparer.OrdinalIgnoreCase);
    // Command-level mutual exclusion per robot: a full handshake (or a move) is one operation, so a
    // concurrent production/debug/manual command queues behind it instead of clobbering its command id.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _commandGates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// F04：停止未确认的机器人隔离标记。停止无法确认（超时/异常/仍 Busy）后置位；所有运动入口
    /// （Send/Move/Handshake）在进入命令临界区时被拒绝，直到停止被确认或人工重置故障——
    /// 防止迟到的旧停止任务作用于新批准的动作。
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _stopUnconfirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<IRobotCommandObserver> _observers;

    public RobotManager(IEnumerable<IRobotCommandObserver>? observers = null)
        => _observers = observers?.ToArray() ?? [];

    private SemaphoreSlim CommandGate(string id) => _commandGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));

    public void Register(IRobot2DAdapter adapter)
    {
        if (!_robots.TryAdd(adapter.Id, adapter))
            throw new InvalidOperationException($"Robot '{adapter.Id}' is already registered.");
    }

    public IReadOnlyList<RobotDescriptor> List() => _robots.Values
        .Select(x => x.Snapshot())
        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IRobot2DAdapter Require(string id) => _robots.TryGetValue(id, out var robot)
        ? robot
        : throw new KeyNotFoundException($"Robot '{id}' is not registered.");

    public RobotDescriptor Get(string id) => Require(id).Snapshot();
    public Task ConnectAsync(string id, CancellationToken ct = default) => Require(id).ConnectAsync(ct);
    public Task DisconnectAsync(string id, CancellationToken ct = default) => Require(id).DisconnectAsync(ct);
    public Task StopAsync(string id, CancellationToken ct = default) => Require(id).StopAsync(ct);
    /// <summary>
    /// 重置适配器故障。同时解除 F04 的"停止未确认"隔离——人工复位表示已核对设备实际状态。
    /// </summary>
    public async Task ResetFaultAsync(string id, CancellationToken ct = default)
    {
        await Require(id).ResetFaultAsync(ct);
        _stopUnconfirmed.TryRemove(id, out _);
    }
    public Task ApplySettingsAsync(string id, RobotRuntimeSettings settings, CancellationToken ct = default) => Require(id).ApplySettingsAsync(settings, ct);
    public Task AcknowledgeAsync(string id, long commandId, CancellationToken ct = default) => Require(id).AcknowledgeAsync(commandId, ct);

    public async Task<RobotCommandReceipt> SendTargetAsync(
        string id,
        VisionRobotTarget2D target,
        bool autoConnect,
        CancellationToken ct = default)
    {
        var adapter = Require(id);
        await EnsureConnectedAsync(adapter, autoConnect, ct);
        var gate = CommandGate(id);
        await gate.WaitAsync(ct);
        try
        {
            EnsureNoUnconfirmedStop(id);
            return await adapter.SendTargetAsync(target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 统一安全终止协议：发送被取消时机器人可能已收到目标，必须用独立宽限期停止并确认，
            // 不允许仅释放命令锁就返回——否则后续命令会覆盖未确认的动作。
            await TryStopAsync(Guid.NewGuid().ToString("N"), id, adapter, 1, "SendStop");
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<RobotCommandReceipt> MoveAsync(
        string id,
        VisionRobotTarget2D target,
        bool autoConnect,
        bool waitForInPosition,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var adapter = Require(id);
        await EnsureConnectedAsync(adapter, autoConnect, ct);
        var gate = CommandGate(id);
        await gate.WaitAsync(ct);
        try
        {
            EnsureNoUnconfirmedStop(id);
            var receipt = await adapter.MoveToAsync(target, ct);
            if (waitForInPosition)
                await WaitForInPositionAsync(id, receipt.CommandId, timeout, ct);
            return receipt;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            // 统一安全终止协议：移动超时/取消/确认失败时，机器人可能仍在运动。用独立宽限期停止并确认；
            // 停止未确认不抛新异常（保留原始错误），但已发出停止——调用方据诊断判断是否重发。
            await TryStopAsync(Guid.NewGuid().ToString("N"), id, adapter, 1, "MoveStop");
            throw;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Standard production handshake:
    /// TargetReady -> Execute -> Busy -> Complete/Error -> Ack.
    /// Retries stop the current attempt before resending the target.
    /// The whole handshake (including retries and auto-ack) is one mutually exclusive robot command:
    /// concurrent senders on the same robot queue behind it instead of overwriting the command id.
    /// Stop/ResetFault/Ack deliberately bypass this gate so control paths stay available mid-command.
    /// </summary>
    public async Task<RobotCommandExecutionResult> ExecuteHandshakeAsync(
        string id,
        VisionRobotTarget2D target,
        RobotCommandPolicy policy,
        CancellationToken ct = default)
    {
        policy = policy.Normalize();
        var adapter = Require(id);
        var gate = CommandGate(id);
        await gate.WaitAsync(ct);
        try
        {
            EnsureNoUnconfirmedStop(id);
            return await ExecuteHandshakeCoreAsync(id, adapter, target, policy, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>停止清理的独立有界宽限期：调用方取消/超时后仍须尽力停下设备（绝不沿用已取消的 token）。</summary>
    private static readonly TimeSpan StopCleanupGrace = TimeSpan.FromSeconds(3);

    private async Task<RobotCommandExecutionResult> ExecuteHandshakeCoreAsync(
        string id,
        IRobot2DAdapter adapter,
        VisionRobotTarget2D target,
        RobotCommandPolicy policy,
        CancellationToken ct)
    {
        var traceId = Guid.NewGuid().ToString("N");
        RobotCommandReceipt? lastReceipt = null;
        Exception? lastError = null;

        await EnsureConnectedAsync(adapter, policy.AutoConnect, ct);
        await EmitAsync(traceId, id, 0, 0, "Connected", "Robot connection ready.", adapter.Snapshot().Handshake, target, null, ct);

        try
        {
            for (var attempt = 1; attempt <= policy.MaxRetries + 1; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await EmitAsync(traceId, id, 0, attempt, "Attempt", $"Handshake attempt {attempt} started.", adapter.Snapshot().Handshake, target, null, ct);

                    lastReceipt = await adapter.SendTargetAsync(target, ct);
                    var accepted = adapter.Snapshot();
                    await EmitAsync(traceId, id, lastReceipt.CommandId, attempt, "TargetReady", "Target payload accepted and TargetReady asserted.", accepted.Handshake, target, null, ct);

                    lastReceipt = await adapter.MoveToAsync(target, ct);
                    var executing = adapter.Snapshot();
                    await EmitAsync(traceId, id, lastReceipt.CommandId, attempt, "Execute", "Execute requested.", executing.Handshake, target, null, ct);

                    RobotDescriptor final;
                    if (policy.WaitForComplete)
                        final = await WaitForCompleteAsync(id, lastReceipt.CommandId, TimeSpan.FromMilliseconds(policy.TimeoutMs), traceId, attempt, ct);
                    else
                        final = adapter.Snapshot();

                    if (final.Handshake.Error)
                        throw new InvalidOperationException(final.Error ?? final.Handshake.ErrorCode ?? "Robot handshake reported Error.");

                    var acknowledged = false;
                    if (policy.AutoAck && final.Handshake.Complete)
                    {
                        await adapter.AcknowledgeAsync(lastReceipt.CommandId, ct);
                        final = adapter.Snapshot();
                        acknowledged = true;
                        await EmitAsync(traceId, id, lastReceipt.CommandId, attempt, "Ack", "Completion acknowledged; handshake cycle closed.", final.Handshake, target, null, ct);
                    }

                    await EmitAsync(traceId, id, lastReceipt.CommandId, attempt, "Done", "Robot command handshake completed.", final.Handshake, target, null, ct);
                    return new RobotCommandExecutionResult(traceId, attempt, lastReceipt, final, final.InPosition || final.Handshake.Complete || acknowledged, acknowledged);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    var snapshot = adapter.Snapshot();
                    await EmitAsync(traceId, id, lastReceipt?.CommandId ?? snapshot.LastCommandId, attempt, "Error", ex.Message, snapshot.Handshake, target, ex.Message, ct);

                    if (attempt > policy.MaxRetries)
                        break;

                    // 停止未确认时禁止重发目标：设备可能仍在执行上一个目标，再次下发会叠加真实动作。
                    if (!await TryStopAsync(traceId, id, adapter, attempt, "RetryStop"))
                    {
                        lastError = new InvalidOperationException(
                            $"Robot '{id}' could not confirm stop after attempt {attempt}; the target was NOT resent to avoid overlapping device motion.", lastError);
                        break;
                    }
                    await EmitAsync(traceId, id, lastReceipt?.CommandId ?? snapshot.LastCommandId, attempt, "Retry", $"Retry scheduled after {policy.RetryDelayMs} ms.", adapter.Snapshot().Handshake, target, null, ct);
                    if (policy.RetryDelayMs > 0)
                        await Task.Delay(policy.RetryDelayMs, ct);

                    var afterStop = adapter.Snapshot();
                    if (afterStop.ConnectionState == RobotConnectionState.Faulted || afterStop.Handshake.Error)
                    {
                        try { await adapter.ResetFaultAsync(ct); } catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方取消：软件等待结束不等于设备已停止——独立安全通道尽力下发停止并记录确认结果，
            // 未确认时事件明确标注状态不确定（复现窗口：真实的设备动作可能仍在进行中）。
            var confirmed = await TryStopAsync(traceId, id, adapter, policy.MaxRetries + 1, "CancelStop");
            await EmitAsync(traceId, id, adapter.Snapshot().LastCommandId, policy.MaxRetries + 1, "Cancelled",
                confirmed
                    ? "Command cancelled; stop issued and confirmed."
                    : "Command cancelled; STOP COULD NOT BE CONFIRMED - robot state is indeterminate.",
                adapter.Snapshot().Handshake, target, confirmed ? null : "stop_unconfirmed", CancellationToken.None);
            throw;
        }

        // 最终失败：统一执行停止清理（独立宽限期，不依赖调用方 token），诊断中明确停止是否确认。
        var finalStopConfirmed = await TryStopAsync(traceId, id, adapter, policy.MaxRetries + 1, "FinalStop");
        var message = finalStopConfirmed
            ? $"Robot '{id}' handshake failed after {policy.MaxRetries + 1} attempt(s); stop was issued and confirmed. Trace={traceId}. {lastError?.Message}"
            : $"Robot '{id}' handshake failed after {policy.MaxRetries + 1} attempt(s); STOP COULD NOT BE CONFIRMED - robot state is indeterminate, verify actual device state before resending. Trace={traceId}. {lastError?.Message}";
        throw new InvalidOperationException(message, lastError);
    }

    /// <summary>
    /// 独立且有界的停止清理：使用专用宽限期 token（绝不沿用调用方已取消/超时的 token），
    /// 并以设备回报（不再 Busy）确认停止结果。未能确认停止时返回 false——调用方必须禁止重发
    /// 并在诊断中标注"状态不确定"，不得把取消软件等待等同于设备已停止。
    ///
    /// 关键：绝不无界地 await 厂商调用。忽略 token / 卡死的 StopAsync 会让执行循环、机器人命令锁
    /// 与生产停止全部无法收尾；因此这里用 Task.WhenAny 与宽限期竞争——超时即判定"停止未确认"，
    /// 不再等待厂商调用返回（孤儿任务被观察以免未处理异常），由调用方按"状态不确定"处理。
    /// </summary>
    private async Task<bool> TryStopAsync(string traceId, string id, IRobot2DAdapter adapter, int attempt, string stage)
    {
        using var cleanup = new CancellationTokenSource(StopCleanupGrace);
        try
        {
            var stopTask = adapter.StopAsync(cleanup.Token);
            var completed = await Task.WhenAny(stopTask, Task.Delay(StopCleanupGrace)).ConfigureAwait(false);
            if (completed != stopTask)
            {
                // 厂商调用在宽限期内没有返回：不能无限等待，按"停止未确认"处理。
                // 观察孤儿任务，避免其后续异常成为未处理任务异常。
                _ = stopTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                MarkStopUnconfirmed(id, "stop did not return within the cleanup grace period");
                await EmitAsync(traceId, id, adapter.Snapshot().LastCommandId, attempt, stage,
                    $"Stop was issued but did not return within {StopCleanupGrace.TotalSeconds:0}s; the robot call is unresponsive and stop is UNCONFIRMED.",
                    adapter.Snapshot().Handshake, null, "stop_timeout", CancellationToken.None);
                return false;
            }
            await stopTask; // 传播真实异常（若厂商调用抛错）
            var snapshot = adapter.Snapshot();
            var stopped = !snapshot.Busy;
            if (stopped) ClearStopUnconfirmed(id);
            else MarkStopUnconfirmed(id, "robot still reports Busy after stop");
            await EmitAsync(traceId, id, snapshot.LastCommandId, attempt, stage,
                stopped ? "Stop issued and confirmed." : "Stop issued, but the robot still reports Busy.",
                snapshot.Handshake, null, stopped ? null : "stop_unconfirmed", CancellationToken.None);
            return stopped;
        }
        catch (Exception ex)
        {
            MarkStopUnconfirmed(id, $"stop attempt failed: {ex.Message}");
            await EmitAsync(traceId, id, adapter.Snapshot().LastCommandId, attempt, stage,
                $"Stop attempt failed: {ex.Message}", adapter.Snapshot().Handshake, null, ex.Message, CancellationToken.None);
            return false;
        }
    }

    // F04：停止未确认的机器人隔离——置位/清除/检查。
    private void MarkStopUnconfirmed(string id, string reason) => _stopUnconfirmed[id] = reason;
    private void ClearStopUnconfirmed(string id) => _stopUnconfirmed.TryRemove(id, out _);

    /// <summary>
    /// F04：停止未确认的机器人拒绝一切新运动命令，直到停止被确认或人工重置故障——
    /// 设备状态不确定时绝不允许叠加新的真实动作。
    /// </summary>
    private void EnsureNoUnconfirmedStop(string id)
    {
        if (_stopUnconfirmed.TryGetValue(id, out var reason))
            throw new InvalidOperationException(
                $"Robot '{id}' has an unconfirmed stop ({reason}); motion commands are blocked until the stop is confirmed or the fault is explicitly reset after verifying the device state.");
    }

    public async Task<RobotDescriptor> WaitForInPositionAsync(
        string id,
        long commandId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = Get(id);
            ValidateCommandState(snapshot, commandId);
            if (snapshot.InPosition && snapshot.HandshakeState == RobotHandshakeState.InPosition)
                return snapshot;
            await Task.Delay(20, ct);
        }
        throw new RobotCommandTimeoutException($"Robot '{id}' did not reach InPosition for command #{commandId} within {timeout.TotalMilliseconds:0} ms.");
    }

    private async Task<RobotDescriptor> WaitForCompleteAsync(
        string id,
        long commandId,
        TimeSpan timeout,
        string traceId,
        int attempt,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var busyLogged = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = Get(id);
            ValidateCommandState(snapshot, commandId);

            if (snapshot.Handshake.Error || snapshot.ConnectionState == RobotConnectionState.Faulted)
                throw new InvalidOperationException($"Robot '{id}' reported Error for command #{commandId}: {snapshot.Error ?? snapshot.Handshake.ErrorCode ?? "unknown"}.");

            if (!busyLogged && snapshot.Handshake.Busy)
            {
                busyLogged = true;
                await EmitAsync(traceId, id, commandId, attempt, "Busy", "Robot asserted Busy.", snapshot.Handshake, snapshot.ActiveTarget, null, ct);
            }

            if (snapshot.Handshake.Complete || snapshot.InPosition)
            {
                await EmitAsync(traceId, id, commandId, attempt, "Complete", "Robot asserted Complete/InPosition.", snapshot.Handshake, snapshot.ActiveTarget, null, ct);
                return snapshot;
            }
            await Task.Delay(20, ct);
        }
        throw new RobotCommandTimeoutException($"Robot '{id}' handshake timed out for command #{commandId} after {timeout.TotalMilliseconds:0} ms.");
    }

    private static void ValidateCommandState(RobotDescriptor snapshot, long commandId)
    {
        if (snapshot.LastCommandId != commandId)
            throw new InvalidOperationException($"Robot '{snapshot.Id}' command changed while waiting for #{commandId}; current is #{snapshot.LastCommandId}.");
        if (snapshot.ConnectionState == RobotConnectionState.Faulted || snapshot.HandshakeState == RobotHandshakeState.Faulted)
            throw new InvalidOperationException($"Robot '{snapshot.Id}' faulted: {snapshot.Error ?? "unknown fault"}.");
    }

    private static async Task EnsureConnectedAsync(IRobot2DAdapter adapter, bool autoConnect, CancellationToken ct)
    {
        if (adapter.Snapshot().ConnectionState == RobotConnectionState.Connected) return;
        if (!autoConnect) throw new InvalidOperationException($"Robot '{adapter.Id}' is disconnected and Auto Connect is disabled.");
        await adapter.ConnectAsync(ct);
    }

    private async ValueTask EmitAsync(
        string traceId,
        string robotId,
        long commandId,
        int attempt,
        string stage,
        string message,
        RobotHandshakeSignals? handshake,
        VisionRobotTarget2D? target,
        string? error,
        CancellationToken ct)
    {
        if (_observers.Count == 0) return;
        var evt = new RobotCommandTraceEvent(traceId, robotId, commandId, attempt, stage, DateTimeOffset.UtcNow, message, handshake, target, error);
        foreach (var observer in _observers)
        {
            try { await observer.OnEventAsync(evt, ct); }
            catch { /* diagnostics must never break motion orchestration */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var robot in _robots.Values)
            await robot.DisposeAsync();
        _robots.Clear();
        foreach (var gate in _commandGates.Values)
            gate.Dispose();
        _commandGates.Clear();
    }
}
