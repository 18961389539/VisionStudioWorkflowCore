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

    private sealed class RecordingObserver : IRobotCommandObserver
    {
        public ConcurrentQueue<RobotCommandTraceEvent> Events { get; } = new();
        public ValueTask OnEventAsync(RobotCommandTraceEvent traceEvent, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(traceEvent);
            return ValueTask.CompletedTask;
        }
    }
}
