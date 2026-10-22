using System.Collections.Concurrent;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Tests;

public sealed class FaultInjectionTests
{
    [Fact]
    public async Task RobotHandshake_RetriesAfterInjectedTransientMoveFailure()
    {
        var observer = new RecordingObserver();
        await using var manager = new RobotManager([observer]);
        var adapter = new FailFirstMoveRobotAdapter();
        manager.Register(adapter);

        var target = new VisionRobotTarget2D(545, 255, 31, "RobotBase", "mm", "ABB", "FaultInjection");
        var result = await manager.ExecuteHandshakeAsync(
            adapter.Id,
            target,
            new RobotCommandPolicy(TimeoutMs: 3000, MaxRetries: 1, RetryDelayMs: 5, AutoConnect: true, WaitForComplete: true, AutoAck: true));

        Assert.Equal(2, result.Attempts);
        Assert.True(result.Completed);
        Assert.True(result.Acknowledged);
        Assert.Equal(1, adapter.InjectedFailures);

        var stages = observer.Events.Select(x => x.Stage).ToArray();
        Assert.Contains("Error", stages);
        Assert.Contains("Retry", stages);
        Assert.Equal(2, stages.Count(x => x == "Attempt"));
        Assert.Contains("Done", stages);
    }

    [Fact]
    public async Task DeviceManager_TransientBatchFailuresMarkBadThenRecoverGood()
    {
        var driver = new FlakyBatchDeviceDriver("flaky-device", failReadCycles: 4);
        await using var manager = new DeviceManager();
        manager.Register(driver);
        manager.ApplySettings(driver.Id, new DeviceRuntimeSettings(PollIntervalMs: 40, ReconnectDelayMs: 100, HeartbeatIntervalMs: 200));
        await manager.ConnectAsync(driver.Id);

        await WaitUntilAsync(() => manager.Get(driver.Id).Stats.ReadErrors >= 1, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
        {
            var snapshot = manager.Get(driver.Id);
            return snapshot.Values.TryGetValue("value", out var value) && value.Quality == DeviceTagQuality.Bad;
        }, TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() =>
        {
            var snapshot = manager.Get(driver.Id);
            return snapshot.Values.TryGetValue("value", out var value)
                   && value.Quality == DeviceTagQuality.Good
                   && value.AsInteger() > 0;
        }, TimeSpan.FromSeconds(3));

        var final = manager.Get(driver.Id);
        Assert.True(final.Stats.ReadErrors >= 4);
        Assert.Equal(DeviceConnectionState.Connected, final.ConnectionState);
        Assert.Equal(DeviceTagQuality.Good, final.Values["value"].Quality);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(15);
        }
        Assert.True(predicate(), $"Condition was not reached within {timeout}.");
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

    private sealed class FailFirstMoveRobotAdapter : IRobot2DAdapter
    {
        private readonly VirtualAbbRobotAdapter _inner = new();
        private int _moveCalls;
        public int InjectedFailures { get; private set; }
        public string Id => _inner.Id;
        public string Name => _inner.Name;
        public string Vendor => _inner.Vendor;
        public string Model => _inner.Model;
        public string Driver => "fault-injection(" + _inner.Driver + ")";
        public string BaseFrame => _inner.BaseFrame;
        public string Unit => _inner.Unit;
        public RobotCapabilities Capabilities => _inner.Capabilities;
        public RobotDescriptor Snapshot() => _inner.Snapshot();
        public Task ConnectAsync(CancellationToken cancellationToken = default) => _inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => _inner.DisconnectAsync(cancellationToken);
        public Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default) => _inner.ApplySettingsAsync(settings, cancellationToken);
        public Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default) => _inner.SendTargetAsync(target, cancellationToken);

        public Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _moveCalls) == 1)
            {
                InjectedFailures++;
                throw new IOException("Injected transient robot transport failure before Execute.");
            }
            return _inner.MoveToAsync(target, cancellationToken);
        }

        public Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default) => _inner.AcknowledgeAsync(commandId, cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);
        public Task ResetFaultAsync(CancellationToken cancellationToken = default) => _inner.ResetFaultAsync(cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class FlakyBatchDeviceDriver : IDeviceDriver, IDeviceBatchDriver
    {
        private readonly int _failReadCycles;
        private int _readCycles;
        private long _value;
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;

        public FlakyBatchDeviceDriver(string id, int failReadCycles)
        {
            Id = id;
            _failReadCycles = failReadCycles;
            Tags =
            [
                new DeviceTagDefinition("value", "Value", "sim:0", DeviceTagDataType.Integer),
                new DeviceTagDefinition("deviceHeartbeat", "Device Heartbeat", "sim:1", DeviceTagDataType.Boolean),
                new DeviceTagDefinition("hostHeartbeat", "Host Heartbeat", "sim:2", DeviceTagDataType.Boolean, Writable: true)
            ];
        }

        public string Id { get; }
        public string Name => Id;
        public string Vendor => "Test";
        public string Model => "FlakyBatch";
        public string Driver => nameof(FlakyBatchDeviceDriver);
        public string Protocol => "fault-injection";
        public string Endpoint => "memory://fault";
        public DeviceConnectionState ConnectionState => _state;
        public string? Error => null;
        public DeviceDriverCapabilities Capabilities { get; } = new(SupportsBatchRead: true, SupportsBatchWrite: true);
        public IReadOnlyList<DeviceTagDefinition> Tags { get; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _state = DeviceConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _state = DeviceConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public async Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
        {
            var result = await ReadManyAsync([tagId], cancellationToken);
            return result[0];
        }

        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DeviceTagSample>> ReadManyAsync(IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cycle = Interlocked.Increment(ref _readCycles);
            if (cycle <= _failReadCycles)
                throw new IOException($"Injected batch read failure #{cycle}.");

            var now = DateTimeOffset.UtcNow;
            var numeric = Interlocked.Increment(ref _value);
            IReadOnlyList<DeviceTagSample> samples = tagIds.Select(id => id switch
            {
                "value" => new DeviceTagSample(Id, id, DeviceTagDataType.Integer, numeric, DeviceTagQuality.Good, now),
                "deviceHeartbeat" => new DeviceTagSample(Id, id, DeviceTagDataType.Boolean, numeric % 2 == 0, DeviceTagQuality.Good, now),
                "hostHeartbeat" => new DeviceTagSample(Id, id, DeviceTagDataType.Boolean, false, DeviceTagQuality.Good, now),
                _ => throw new DeviceTagNotFoundException(id)
            }).ToArray();
            return Task.FromResult(samples);
        }

        public Task WriteManyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
