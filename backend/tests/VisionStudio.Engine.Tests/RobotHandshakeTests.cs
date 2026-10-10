using System.Collections.Concurrent;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Tests;

public sealed class RobotHandshakeTests
{
    [Fact]
    public async Task VirtualAbb_CompletesStandardHandshakeAndEmitsTraceStages()
    {
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        manager.Register(new VirtualAbbRobotAdapter());
        var target = new VisionRobotTarget2D(530, 248, 31, "RobotBase", "mm", "ABB", "Test");

        var result = await manager.ExecuteHandshakeAsync(
            "virtual-abb-1",
            target,
            new RobotCommandPolicy(TimeoutMs: 3000, MaxRetries: 0, RetryDelayMs: 0, AutoConnect: true, WaitForComplete: true, AutoAck: true));

        Assert.True(result.Completed);
        Assert.True(result.Acknowledged);
        Assert.Equal(RobotHandshakeState.Ready, result.Robot.HandshakeState);
        Assert.InRange(Math.Abs(result.Robot.CurrentPose.X - target.X), 0, 0.001);
        Assert.InRange(Math.Abs(result.Robot.CurrentPose.Y - target.Y), 0, 0.001);
        var stages = observer.Events.Select(x => x.Stage).ToArray();
        foreach (var expected in new[] { "Connected", "Attempt", "TargetReady", "Execute", "Busy", "Complete", "Ack", "Done" })
            Assert.Contains(expected, stages);
    }

    [Fact]
    public async Task ConcurrentHandshakes_OnSameRobot_QueueInsteadOfClobberingCommandIds()
    {
        await using var manager = new RobotManager();
        manager.Register(new VirtualAbbRobotAdapter());
        var policy = new RobotCommandPolicy(TimeoutMs: 3000, MaxRetries: 0, RetryDelayMs: 0, AutoConnect: true, WaitForComplete: true, AutoAck: true);

        var first = manager.ExecuteHandshakeAsync("virtual-abb-1", new VisionRobotTarget2D(530, 248, 31, "RobotBase", "mm", "ABB", "Test"), policy);
        var second = manager.ExecuteHandshakeAsync("virtual-abb-1", new VisionRobotTarget2D(600, 300, 15, "RobotBase", "mm", "ABB", "Test"), policy);

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Completed));
        Assert.All(results, result => Assert.True(result.Acknowledged));
        Assert.True(results[0].Receipt.CommandId < results[1].Receipt.CommandId,
            $"Handshakes were not serialized: command ids {results[0].Receipt.CommandId} and {results[1].Receipt.CommandId}.");
    }

    [Fact]
    public async Task FinalFailure_IssuesBoundedStop_AndReportsStopConfirmation()
    {
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        var adapter = new StubbornRobotAdapter();
        manager.Register(adapter);
        var policy = new RobotCommandPolicy(TimeoutMs: 200, MaxRetries: 0, RetryDelayMs: 0, AutoConnect: true, WaitForComplete: true, AutoAck: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteHandshakeAsync(adapter.Id, new VisionRobotTarget2D(1, 2, 0, "RobotBase", "mm", "Test", "stubborn"), policy));

        Assert.True(adapter.StopCalls >= 1, "final failure must issue a stop");
        Assert.Contains("STOP COULD NOT BE CONFIRMED", ex.Message);
        Assert.Contains(observer.Events, x => x.Stage == "FinalStop");
    }

    [Fact]
    public async Task CallerCancellation_StillIssuesStop_WithIndependentCleanupToken()
    {
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        var adapter = new StubbornRobotAdapter();
        manager.Register(adapter);
        var policy = new RobotCommandPolicy(TimeoutMs: 5000, MaxRetries: 0, RetryDelayMs: 0, AutoConnect: true, WaitForComplete: true, AutoAck: true);
        using var cts = new CancellationTokenSource(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.ExecuteHandshakeAsync(adapter.Id, new VisionRobotTarget2D(3, 4, 0, "RobotBase", "mm", "Test", "stubborn"), policy, cts.Token));

        // 适配器的 StopAsync 在"已取消的 token"下会抛 OCE：StopCalls 记录了调用，说明
        // 取消路径使用的是独立的清理 token（不沿用调用方已取消的 token）。
        Assert.True(adapter.StopCalls >= 1, "cancellation must still issue a best-effort stop with an independent token");
        Assert.Contains(observer.Events, x => x.Stage == "CancelStop");
    }

    [Fact]
    public async Task RetryStopUnconfirmed_DoesNotResendTarget()
    {
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        var adapter = new StubbornRobotAdapter { FailFirstSend = true };
        manager.Register(adapter);
        var policy = new RobotCommandPolicy(TimeoutMs: 500, MaxRetries: 1, RetryDelayMs: 1, AutoConnect: true, WaitForComplete: true, AutoAck: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteHandshakeAsync(adapter.Id, new VisionRobotTarget2D(5, 6, 0, "RobotBase", "mm", "Test", "stubborn"), policy));

        Assert.Equal(1, adapter.TargetSendCalls); // 停止未确认：第二次尝试没有重发目标
        Assert.True(adapter.StopCalls >= 1);
        Assert.Contains("NOT resent", ex.Message);
    }

    [Fact]
    public async Task UnresponsiveStop_IsBounded_DoesNotHangExecutionLoop()
    {
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        var adapter = new HangingStopRobotAdapter();
        manager.Register(adapter);
        var policy = new RobotCommandPolicy(TimeoutMs: 200, MaxRetries: 0, RetryDelayMs: 0, AutoConnect: true, WaitForComplete: true, AutoAck: true);

        // 适配器的 StopAsync 永不返回（忽略 token）。停止清理必须有界：整体调用必须在远小于
        // "永久"的时间内完成——用 10s 兜底，若清理无界则会命中兜底而失败。
        var handshake = Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteHandshakeAsync(adapter.Id, new VisionRobotTarget2D(7, 8, 0, "RobotBase", "mm", "Test", "hang"), policy));
        var timeoutGuard = Task.Delay(TimeSpan.FromSeconds(10));
        var winner = await Task.WhenAny(handshake, timeoutGuard);

        Assert.True(winner == handshake, "robot stop cleanup hung: an unresponsive adapter must not block the execution loop");
        await handshake; // 传播并确认异常类型

        Assert.Equal(1, adapter.StopCalls);
        Assert.Contains(observer.Events, x => x.Stage == "FinalStop");
        Assert.Contains(observer.Events, x => x.Stage == "FinalStop" && x.Error == "stop_timeout");
    }

    [Fact]
    public async Task UnconfirmedStop_IsolatesRobot_UntilResetFault()
    {
        // F04 回归：停止未确认后，该机器人必须被隔离——后续 Send/Move/Handshake 在触达设备前
        // 全部被拒绝（设备状态不确定时不得叠加新动作）；人工 ResetFault（核对设备状态后）解除隔离。
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        var adapter = new StubbornRobotAdapter();
        manager.Register(adapter);
        var policy = new RobotCommandPolicy(TimeoutMs: 200, MaxRetries: 0, RetryDelayMs: 0, AutoConnect: true, WaitForComplete: true, AutoAck: true);
        var target = new VisionRobotTarget2D(5, 6, 0, "RobotBase", "mm", "Test", "stubborn");

        // 触发一次停止未确认：失败流程的 FinalStop 无法确认（适配器 Busy 恒真）。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteHandshakeAsync(adapter.Id, target, policy));

        // 隔离生效：所有运动入口被拒，且不得触达设备。
        var sendsBefore = adapter.TargetSendCalls;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.SendTargetAsync(adapter.Id, target, autoConnect: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.MoveAsync(adapter.Id, target, autoConnect: true, waitForInPosition: false, timeout: TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteHandshakeAsync(adapter.Id, target, policy));
        Assert.Equal(sendsBefore, adapter.TargetSendCalls);

        // 已核对设备状态并复位 → 隔离解除，命令可以再次下发。
        await manager.ResetFaultAsync(adapter.Id);
        var receipt = await manager.SendTargetAsync(adapter.Id, target, autoConnect: true);
        Assert.Equal(adapter.Id, receipt.RobotId);
        Assert.True(adapter.TargetSendCalls > sendsBefore);
    }

    [Fact]
    public async Task MoveTimeout_IssuesBoundedStopCleanup()
    {
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        var adapter = new MoveTimeoutRobotAdapter();
        manager.Register(adapter);

        await Assert.ThrowsAsync<RobotCommandTimeoutException>(() =>
            manager.MoveAsync(adapter.Id, new VisionRobotTarget2D(9, 9, 0, "RobotBase", "mm", "Test", "mt"), autoConnect: true, waitForInPosition: true, timeout: TimeSpan.FromMilliseconds(80)));

        // 移动超时后必须发出停止（统一安全终止协议），不能只释放命令锁。
        Assert.True(adapter.StopCalls >= 1, "a timed-out move must issue a bounded stop");
        Assert.Contains(observer.Events, x => x.Stage == "MoveStop");
    }

    private sealed class RecordingObserver : IRobotCommandObserver
    {
        public ConcurrentQueue<RobotCommandTraceEvent> Events { get; } = new();
        public ValueTask OnEventAsync(RobotCommandTraceEvent traceEvent, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(traceEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// 复现"设备忽略调用方 token、持续 Busy、停止无法确认"的真实故障形态：
    /// 用于验证最终失败/取消路径的停止清理与"停止未确认禁止重发"语义。
    /// </summary>
    private sealed class StubbornRobotAdapter : IRobot2DAdapter
    {
        private readonly object _gate = new();
        private RobotConnectionState _connection = RobotConnectionState.Disconnected;
        private long _commandId;

        public string Id => "stubborn-1";
        public string Name => "Stubborn";
        public string Vendor => "Test";
        public string Model => "T";
        public string Driver => "test";
        public string BaseFrame => "RobotBase";
        public string Unit => "mm";
        public RobotCapabilities Capabilities { get; } = new(MaxLinearSpeedMmPerSec: 100, MaxAngularSpeedDegPerSec: 90);

        public int StopCalls { get; private set; }
        public int TargetSendCalls { get; private set; }
        public bool FailFirstSend { get; init; }

        public RobotDescriptor Snapshot()
        {
            lock (_gate)
            {
                return new RobotDescriptor(
                    Id, Name, Vendor, Model, Driver, BaseFrame, Unit,
                    _connection,
                    _connection == RobotConnectionState.Connected ? RobotHandshakeState.Executing : RobotHandshakeState.Disconnected,
                    RobotHandshakeSignals.Empty with { Busy = true, CommandId = _commandId, UpdatedAt = DateTimeOffset.UtcNow },
                    new VisionCoordinatePose2D(0, 0, 0, BaseFrame, Unit),
                    null,
                    _commandId,
                    Busy: true,
                    InPosition: false,
                    Error: null,
                    new RobotRuntimeSettings(),
                    Capabilities,
                    DateTimeOffset.UtcNow);
            }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) _connection = RobotConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) _connection = RobotConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
        {
            TargetSendCalls++;
            if (FailFirstSend && TargetSendCalls == 1)
                throw new InvalidOperationException("Simulated send failure.");
            lock (_gate) _commandId++;
            return Task.FromResult(new RobotCommandReceipt(_commandId, Id, target, RobotHandshakeState.Ready, DateTimeOffset.UtcNow));
        }

        public Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
            => Task.FromResult(new RobotCommandReceipt(_commandId, Id, target, RobotHandshakeState.Executing, DateTimeOffset.UtcNow));

        public Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            // 刻意模拟"拒绝在已取消的调用方 token 下执行"：独立清理 token 才能到达这里。
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            StopCalls++;
            // 持续的 Busy：停止无法确认（返回后快照仍 Busy）。
            return Task.CompletedTask;
        }

        public Task ResetFaultAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>StopAsync 永不返回且忽略 token：验证停止清理有界、不阻塞执行循环。</summary>
    private sealed class HangingStopRobotAdapter : IRobot2DAdapter
    {
        private readonly object _gate = new();
        private RobotConnectionState _connection = RobotConnectionState.Disconnected;
        private long _commandId;

        public string Id => "hang-1";
        public string Name => "Hanging";
        public string Vendor => "Test";
        public string Model => "T";
        public string Driver => "test";
        public string BaseFrame => "RobotBase";
        public string Unit => "mm";
        public RobotCapabilities Capabilities { get; } = new(MaxLinearSpeedMmPerSec: 100, MaxAngularSpeedDegPerSec: 90);

        public int StopCalls { get; private set; }

        public RobotDescriptor Snapshot()
        {
            lock (_gate)
            {
                return new RobotDescriptor(
                    Id, Name, Vendor, Model, Driver, BaseFrame, Unit, _connection,
                    _connection == RobotConnectionState.Connected ? RobotHandshakeState.Ready : RobotHandshakeState.Disconnected,
                    RobotHandshakeSignals.Empty with { CommandId = _commandId, UpdatedAt = DateTimeOffset.UtcNow },
                    new VisionCoordinatePose2D(0, 0, 0, BaseFrame, Unit), null, _commandId,
                    Busy: true, InPosition: false, Error: null, new RobotRuntimeSettings(), Capabilities, DateTimeOffset.UtcNow);
            }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) { lock (_gate) _connection = RobotConnectionState.Connected; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { lock (_gate) _connection = RobotConnectionState.Disconnected; return Task.CompletedTask; }
        public Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
        { lock (_gate) _commandId++; return Task.FromResult(new RobotCommandReceipt(_commandId, Id, target, RobotHandshakeState.Ready, DateTimeOffset.UtcNow)); }
        public Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default) => SendTargetAsync(target, cancellationToken);
        public Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            // 永不完成：模拟卡死的厂商调用（忽略 token）。
            return new TaskCompletionSource().Task;
        }

        public Task ResetFaultAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>MoveToAsync 立即返回但机器人永不 InPosition：验证移动超时后有界停止清理。</summary>
    private sealed class MoveTimeoutRobotAdapter : IRobot2DAdapter
    {
        private readonly object _gate = new();
        private RobotConnectionState _connection = RobotConnectionState.Disconnected;
        private long _commandId;

        public string Id => "movetimeout-1";
        public string Name => "Move Timeout";
        public string Vendor => "Test";
        public string Model => "T";
        public string Driver => "test";
        public string BaseFrame => "RobotBase";
        public string Unit => "mm";
        public RobotCapabilities Capabilities { get; } = new(MaxLinearSpeedMmPerSec: 100, MaxAngularSpeedDegPerSec: 90);

        public int StopCalls { get; private set; }

        public RobotDescriptor Snapshot()
        {
            lock (_gate)
            {
                return new RobotDescriptor(
                    Id, Name, Vendor, Model, Driver, BaseFrame, Unit, _connection,
                    _connection == RobotConnectionState.Connected ? RobotHandshakeState.Executing : RobotHandshakeState.Disconnected,
                    RobotHandshakeSignals.Empty with { CommandId = _commandId, Busy = true, UpdatedAt = DateTimeOffset.UtcNow },
                    new VisionCoordinatePose2D(0, 0, 0, BaseFrame, Unit), null, _commandId,
                    Busy: true, InPosition: false, Error: null, new RobotRuntimeSettings(), Capabilities, DateTimeOffset.UtcNow);
            }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) { lock (_gate) _connection = RobotConnectionState.Connected; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { lock (_gate) _connection = RobotConnectionState.Disconnected; return Task.CompletedTask; }
        public Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
        { lock (_gate) _commandId++; return Task.FromResult(new RobotCommandReceipt(_commandId, Id, target, RobotHandshakeState.Executing, DateTimeOffset.UtcNow)); }
        public Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default) => SendTargetAsync(target, cancellationToken);
        public Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) { StopCalls++; return Task.CompletedTask; }

        public Task ResetFaultAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
