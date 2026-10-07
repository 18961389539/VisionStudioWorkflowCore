using OpenCvSharp;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Tests;

public sealed class CameraAcquisitionTests
{
    [Fact]
    public async Task FrameTimeout_IsCountedWithoutReconnect()
    {
        var device = new FakeCamera(new CameraSettings(TriggerMode: CameraTriggerMode.External), timeoutOnly: true);
        using var hub = new CameraFrameHub(2);
        await using var worker = new CameraAcquisitionWorker(device, hub);

        await worker.StartAsync();
        await WaitUntilAsync(() => worker.Snapshot().FrameTimeouts >= 2);
        var snapshot = worker.Snapshot();
        await worker.StopAsync();

        Assert.True(snapshot.FrameTimeouts >= 2);
        Assert.Equal(0, snapshot.ReconnectCount);
        Assert.Equal(0, snapshot.AcquisitionErrors);
    }

    [Fact]
    public async Task AdapterTelemetry_IsSurfacedSeparatelyFromRingOverwrite()
    {
        var transport = new CameraTransportTelemetry("fake-camera", "fake", true, "fake-native", DateTimeOffset.UtcNow, ReceivedFrames: 100, LostFrames: 2, ThroughputMbps: 88.5);
        var device = new FakeCamera(new CameraSettings(), telemetry: new CameraDeviceTelemetry(7, "BayerRG8", 1234, transport));
        using var hub = new CameraFrameHub(2);
        await using var worker = new CameraAcquisitionWorker(device, hub);

        var snapshot = worker.Snapshot();

        Assert.Equal(7, snapshot.DriverDroppedFrames);
        Assert.Equal("BayerRG8", snapshot.NativePixelFormat);
        Assert.Equal(0, snapshot.RingOverwrites);
        Assert.True(snapshot.Transport?.Native);
        Assert.Equal(2, snapshot.Transport?.LostFrames);
    }

    [Fact]
    public async Task SoftwareTrigger_InvokesAdapterBeforePublishingFrame()
    {
        var device = new FakeCamera(new CameraSettings(TriggerMode: CameraTriggerMode.Software));
        using var hub = new CameraFrameHub(2);
        await using var worker = new CameraAcquisitionWorker(device, hub);

        await worker.StartAsync();
        worker.Trigger();
        await WaitUntilAsync(() => worker.Snapshot().FramesPublished >= 1);
        await worker.StopAsync();

        Assert.True(device.SoftwareTriggerCount >= 1);
        Assert.True(device.GrabCount >= 1);
        Assert.True(device.TriggerObservedBeforeGrab);
    }

    [Fact]
    public async Task ExternalHardwareTrigger_CannotBeSynthesizedByHost()
    {
        var device = new FakeCamera(new CameraSettings(TriggerMode: CameraTriggerMode.External));
        using var hub = new CameraFrameHub(2);
        await using var worker = new CameraAcquisitionWorker(device, hub);

        await worker.StartAsync();
        var ex = Assert.Throws<InvalidOperationException>(() => worker.Trigger());
        await worker.StopAsync();

        Assert.Contains("hardware external trigger", ex.Message.ToLowerInvariant());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not reached by the test deadline.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeCamera : ICameraDevice, ICameraTelemetryProvider
    {
        private readonly bool _timeoutOnly;
        private readonly CameraDeviceTelemetry _telemetry;
        private long _sequence;
        private int _softwareTriggerCount;
        private int _grabCount;
        private volatile bool _triggerSeen;

        public FakeCamera(CameraSettings settings, bool timeoutOnly = false, CameraDeviceTelemetry? telemetry = null)
        {
            Settings = settings;
            _timeoutOnly = timeoutOnly;
            _telemetry = telemetry ?? new CameraDeviceTelemetry();
        }

        public string Id => "fake-camera";
        public string Name => "Fake Camera";
        public string Driver => "fake";
        public string? Source => null;
        public CameraState State { get; private set; } = CameraState.Closed;
        public long FramesCaptured => Interlocked.Read(ref _sequence);
        public string? LastError => null;
        public CameraSettings Settings { get; private set; }
        public CameraCapabilities Capabilities { get; } = new(HostSimulatedExternalTrigger: false);
        public int SoftwareTriggerCount => Volatile.Read(ref _softwareTriggerCount);
        public int GrabCount => Volatile.Read(ref _grabCount);
        public bool TriggerObservedBeforeGrab { get; private set; }

        public Task OpenAsync(CancellationToken cancellationToken = default) { State = CameraState.Open; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { State = CameraState.Closed; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken cancellationToken = default) { State = CameraState.Streaming; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { if (State != CameraState.Closed) State = CameraState.Open; return Task.CompletedTask; }
        public Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
        public Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _softwareTriggerCount);
            _triggerSeen = true;
            return Task.CompletedTask;
        }

        public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _grabCount);
            if (_timeoutOnly)
            {
                await Task.Delay(5, cancellationToken);
                throw new CameraFrameTimeoutException("No hardware trigger arrived.");
            }

            TriggerObservedBeforeGrab = _triggerSeen;
            _triggerSeen = false;
            var sequence = Interlocked.Increment(ref _sequence);
            return new VisionFrame(Id, sequence, DateTimeOffset.UtcNow, new Mat(2, 2, MatType.CV_8UC1, Scalar.All(sequence)), "Mono8");
        }

        public CameraDeviceTelemetry GetTelemetry() => _telemetry;
        public ValueTask DisposeAsync() { State = CameraState.Closed; return ValueTask.CompletedTask; }
    }
}
