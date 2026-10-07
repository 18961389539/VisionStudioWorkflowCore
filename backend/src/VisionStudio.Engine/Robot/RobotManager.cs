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
    public Task ResetFaultAsync(string id, CancellationToken ct = default) => Require(id).ResetFaultAsync(ct);
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
        try { return await adapter.SendTargetAsync(target, ct); }
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
            var receipt = await adapter.MoveToAsync(target, ct);
            if (waitForInPosition)
                await WaitForInPositionAsync(id, receipt.CommandId, timeout, ct);
            return receipt;
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
            return await ExecuteHandshakeCoreAsync(id, adapter, target, policy, ct);
        }
        finally { gate.Release(); }
    }

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

                try { await adapter.StopAsync(ct); } catch { /* preserve original command error */ }
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

        throw new InvalidOperationException($"Robot '{id}' handshake failed after {policy.MaxRetries + 1} attempt(s). Trace={traceId}. {lastError?.Message}", lastError);
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
