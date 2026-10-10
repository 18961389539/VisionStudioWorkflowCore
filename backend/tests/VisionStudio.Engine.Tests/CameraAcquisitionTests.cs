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

    [Fact]
    public async Task StopInProgress_RejectsConcurrentStart_AndKeepsControlHandles()
    {
        // F08 回归：旧 Stop 在等待 loop 收尾期间，并发 Start 必须被拒绝——否则旧 Stop 会
        // 无条件清空新任务的 CTS/loop，导致新采集任务失去控制句柄（再也无法被 Stop）。
        var device = new DrainBlockingCamera();
        using var hub = new CameraFrameHub(2);
        await using var worker = new CameraAcquisitionWorker(device, hub);

        await worker.StartAsync();
        await WaitUntilAsync(() => worker.Snapshot().AcquisitionState == CameraAcquisitionState.Running);

        var stop = worker.StopAsync();
        await WaitUntilAsync(() => worker.Snapshot().AcquisitionState == CameraAcquisitionState.Stopping);

        // 收尾期间的 Start 被拒绝：状态不得回到 Starting/Running。
        await worker.StartAsync();
        Assert.Equal(CameraAcquisitionState.Stopping, worker.Snapshot().AcquisitionState);

        device.Release(); // 旧 loop 得以收尾
        await stop;
        Assert.Equal(CameraAcquisitionState.Stopped, worker.Snapshot().AcquisitionState);

        // 收尾完成后 Start 正常，且新任务可被再次停止（控制句柄完好）。
        await worker.StartAsync();
        await WaitUntilAsync(() => worker.Snapshot().AcquisitionState == CameraAcquisitionState.Running);
        await worker.StopAsync();
        Assert.Equal(CameraAcquisitionState.Stopped, worker.Snapshot().AcquisitionState);
    }

    [Fact]
    public async Task UnresponsiveDevice_StopIsBoundedByIndependentDrainGrace()
    {
        // F08 回归：设备在取消后仍不返回时，Stop 必须在独立的收尾期限内完成（不依赖调用方 token，
        // 也不无限等待），并把超时留在运行诊断中。
        var device = new DrainBlockingCamera(); // 不 Release：GrabAsync 永不返回
        using var hub = new CameraFrameHub(2);
        var worker = new CameraAcquisitionWorker(device, hub, stopDrainGrace: TimeSpan.FromMilliseconds(150));
        try
        {
            await worker.StartAsync();
            await WaitUntilAsync(() => worker.Snapshot().AcquisitionState == CameraAcquisitionState.Running);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await worker.StopAsync();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"stop must be bounded by the drain grace, took {sw.Elapsed}");
            Assert.Equal(CameraAcquisitionState.Stopped, worker.Snapshot().AcquisitionState);
            Assert.Contains("drainage", worker.Snapshot().RuntimeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            device.Release(); // 释放孤儿 loop，避免泄漏到后续测试
            await worker.DisposeAsync();
        }
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

    /// <summary>模拟"取消后仍需收尾时间"的相机：GrabAsync 忽略取消、等待显式 Release 才返回。</summary>
    private sealed class DrainBlockingCamera : ICameraDevice
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "drain-camera";
        public string Name => "Drain Camera";
        public string Driver => "fake";
        public string? Source => null;
        public CameraState State { get; private set; } = CameraState.Closed;
        public long FramesCaptured => 0;
        public string? LastError => null;
        public CameraSettings Settings { get; private set; } = new();
        public CameraCapabilities Capabilities { get; } = new(HostSimulatedExternalTrigger: false);

        public void Release() => _release.TrySetResult();

        public Task OpenAsync(CancellationToken cancellationToken = default) { State = CameraState.Open; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { State = CameraState.Closed; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken cancellationToken = default) { State = CameraState.Streaming; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { if (State != CameraState.Closed) State = CameraState.Open; return Task.CompletedTask; }
        public Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
        public Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // 忽略取消：设备在调用方取消后仍需时间收尾；显式 Release 后返回一帧（loop 随即可观察到取消并退出）。
            await _release.Task;
            await Task.Delay(5);
            return new VisionFrame(Id, 1, DateTimeOffset.UtcNow, new Mat(2, 2, MatType.CV_8UC1, Scalar.All(1)), "Mono8");
        }

        public ValueTask DisposeAsync() { State = CameraState.Closed; return ValueTask.CompletedTask; }
    }
}
