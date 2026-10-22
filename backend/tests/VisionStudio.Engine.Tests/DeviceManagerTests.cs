using VisionStudio.Engine.Device;

namespace VisionStudio.Engine.Tests;

public sealed class DeviceManagerTests
{
    [Fact]
    public async Task Polling_WritesHostHeartbeatAndMaintainsGoodQuality()
    {
        var driver = new TestDeviceDriver("heartbeat-device", failFirstRead: false);
        await using var manager = new DeviceManager();
        manager.Register(driver);
        manager.ApplySettings(driver.Id, new DeviceRuntimeSettings(PollIntervalMs: 20, ReconnectDelayMs: 100, HeartbeatIntervalMs: 100));
        await manager.ConnectAsync(driver.Id);

        await WaitUntilAsync(() => driver.HostHeartbeatWrites >= 2, TimeSpan.FromSeconds(2));
        var snapshot = manager.Get(driver.Id);

        Assert.Equal(DeviceConnectionState.Connected, snapshot.ConnectionState);
        Assert.True(snapshot.Stats.PollCycles > 0);
        Assert.True(driver.HostHeartbeatWrites >= 2);
        Assert.True(snapshot.Values.TryGetValue("deviceHeartbeat", out var sample));
        Assert.Equal(DeviceTagQuality.Good, sample!.Quality);
    }

    [Fact]
    public async Task Polling_ReconnectsAfterDriverTransitionsToDisconnected()
    {
        var driver = new TestDeviceDriver("reconnect-device", failFirstRead: true);
        await using var manager = new DeviceManager();
        manager.Register(driver);
        manager.ApplySettings(driver.Id, new DeviceRuntimeSettings(PollIntervalMs: 20, ReconnectDelayMs: 100, HeartbeatIntervalMs: 100));
        await manager.ConnectAsync(driver.Id);

        await WaitUntilAsync(() => manager.Get(driver.Id).Stats.ReconnectCount >= 1, TimeSpan.FromSeconds(3));
        var snapshot = manager.Get(driver.Id);

        Assert.True(driver.ConnectCount >= 2);
        Assert.True(snapshot.Stats.ReconnectCount >= 1);
        Assert.Equal(DeviceConnectionState.Connected, snapshot.ConnectionState);
    }

    [Fact]
    public async Task WriteSequence_SerializesConcurrentSequencesOnSameDevice()
    {
        var driver = new RecordingDeviceDriver("sequence-device", writeDelayMs: 25);
        await using var manager = new DeviceManager();
        manager.Register(driver);
        await manager.ConnectAsync(driver.Id);

        var first = manager.WriteSequenceAsync(driver.Id, [("a", (object?)1), ("b", (object?)1), ("c", (object?)1)]);
        var second = manager.WriteSequenceAsync(driver.Id, [("a", (object?)2), ("b", (object?)2), ("c", (object?)2)]);
        await Task.WhenAll(first, second);

        var order = driver.Writes;
        Assert.Equal(6, order.Count);
        var firstRun = order.Take(3).Select(x => x.Value).ToArray();
        var secondRun = order.Skip(3).Select(x => x.Value).ToArray();
        Assert.All(firstRun, value => Assert.Equal(firstRun[0], value));
        Assert.All(secondRun, value => Assert.Equal(secondRun[0], value));
        Assert.NotEqual(firstRun[0], secondRun[0]);
    }

    [Fact]
    public async Task WriteSequence_DoesNotLetSingleWriteInterleaveIntoTransaction()
    {
        var driver = new RecordingDeviceDriver("sequence-single-device", writeDelayMs: 25);
        await using var manager = new DeviceManager();
        manager.Register(driver);
        await manager.ConnectAsync(driver.Id);

        var sequence = manager.WriteSequenceAsync(driver.Id, [("a", (object?)1), ("b", (object?)1)]);
        var single = manager.WriteTagAsync(driver.Id, "c", 2);
        await Task.WhenAll(sequence, single);

        var order = driver.Writes;
        Assert.Equal(3, order.Count);
        var singleIndex = order.FindIndex(x => x.TagId == "c");
        Assert.True(singleIndex is 0 or 2, $"Single write interleaved into the sequence: {string.Join(",", order.Select(x => x.TagId))}");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.True(predicate(), $"Condition was not reached within {timeout}.");
    }

    private sealed class TestDeviceDriver : IDeviceDriver
    {
        private readonly bool _failFirstRead;
        private bool _failed;
        private bool _heartbeat;
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
        public int ConnectCount { get; private set; }
        public int HostHeartbeatWrites { get; private set; }

        public TestDeviceDriver(string id, bool failFirstRead)
        {
            Id = id;
            _failFirstRead = failFirstRead;
            Tags =
            [
                new DeviceTagDefinition("deviceHeartbeat", "Device Heartbeat", "sim:0", DeviceTagDataType.Boolean),
                new DeviceTagDefinition("hostHeartbeat", "Host Heartbeat", "sim:1", DeviceTagDataType.Boolean, Writable: true)
            ];
        }

        public string Id { get; }
        public string Name => Id;
        public string Vendor => "Test";
        public string Model => "Fake";
        public string Driver => "TestDeviceDriver";
        public string Protocol => "test";
        public string Endpoint => "memory://test";
        public DeviceConnectionState ConnectionState => _state;
        public string? Error => null;
        public DeviceDriverCapabilities Capabilities { get; } = new();
        public IReadOnlyList<DeviceTagDefinition> Tags { get; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            _state = DeviceConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _state = DeviceConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
        {
            if (_failFirstRead && !_failed)
            {
                _failed = true;
                _state = DeviceConnectionState.Disconnected;
                throw new IOException("Simulated link loss");
            }
            if (_state != DeviceConnectionState.Connected) throw new DeviceDisconnectedException("Disconnected");
            if (tagId.Equals("deviceHeartbeat", StringComparison.OrdinalIgnoreCase)) _heartbeat = !_heartbeat;
            var value = tagId.Equals("deviceHeartbeat", StringComparison.OrdinalIgnoreCase) ? _heartbeat : false;
            return Task.FromResult(new DeviceTagSample(Id, tagId, DeviceTagDataType.Boolean, value, DeviceTagQuality.Good, DateTimeOffset.UtcNow));
        }

        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default)
        {
            if (_state != DeviceConnectionState.Connected) throw new DeviceDisconnectedException("Disconnected");
            if (tagId.Equals("hostHeartbeat", StringComparison.OrdinalIgnoreCase)) HostHeartbeatWrites++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDeviceDriver : IDeviceDriver
    {
        private readonly int _writeDelayMs;
        private readonly object _sync = new();
        private readonly List<(string TagId, object? Value)> _writes = new();
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;

        public RecordingDeviceDriver(string id, int writeDelayMs)
        {
            Id = id;
            _writeDelayMs = writeDelayMs;
            Tags =
            [
                new DeviceTagDefinition("a", "A", "sim:a", DeviceTagDataType.Integer, Writable: true),
                new DeviceTagDefinition("b", "B", "sim:b", DeviceTagDataType.Integer, Writable: true),
                new DeviceTagDefinition("c", "C", "sim:c", DeviceTagDataType.Integer, Writable: true)
            ];
        }

        public string Id { get; }
        public string Name => Id;
        public string Vendor => "Test";
        public string Model => "Recording";
        public string Driver => "RecordingDeviceDriver";
        public string Protocol => "test";
        public string Endpoint => "memory://recording";
        public DeviceConnectionState ConnectionState => _state;
        public string? Error => null;
        public DeviceDriverCapabilities Capabilities { get; } = new();
        public IReadOnlyList<DeviceTagDefinition> Tags { get; }

        public List<(string TagId, object? Value)> Writes
        {
            get { lock (_sync) return _writes.ToList(); }
        }

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

        public Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
        {
            if (_state != DeviceConnectionState.Connected) throw new DeviceDisconnectedException("Disconnected");
            var definition = Tags.First(x => x.Id.Equals(tagId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new DeviceTagSample(Id, tagId, definition.DataType, 0L, DeviceTagQuality.Good, DateTimeOffset.UtcNow));
        }

        public async Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default)
        {
            if (_state != DeviceConnectionState.Connected) throw new DeviceDisconnectedException("Disconnected");
            lock (_sync) _writes.Add((tagId, value));
            if (_writeDelayMs > 0) await Task.Delay(_writeDelayMs, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
