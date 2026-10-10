using OpenCvSharp;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Tests;

public sealed class CameraAcquisitionTests
{
    [Fact]
    public async Task ManagerCloseAndDispose_KeepDeviceSessionWhileGrabIsUnconfirmed()
    {
        var device = new DrainBlockingCamera();
        var manager = new CameraManager(TimeSpan.FromMilliseconds(100));
        manager.Register(device);
        await manager.StartAsync(device.Id);
        await WaitUntilAsync(() => device.ActiveGrabs == 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CloseAsync(device.Id));
        Assert.Equal(0, device.StopCalls);
        Assert.Equal(0, device.CloseCalls);
        Assert.Equal(0, device.DisposeCalls);
        Assert.Equal(CameraState.Streaming, device.State);
        Assert.Equal(0, manager.GetLatestSequence(device.Id));

        await manager.DisposeAsync();
        await manager.DisposeAsync();
        Assert.Equal(0, device.StopCalls);
        Assert.Equal(0, device.CloseCalls);
        Assert.Equal(0, device.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(device.Id));
        device.Release();
        await WaitUntilAsync(() => device.DisposeCalls == 1);
        Assert.Equal(CameraState.Closed, device.State);
    }

    [Fact]
    public async Task ManagerClose_HoldsDeviceLifecycleUntilSdkCloseReturns()
    {
        var device = new LifecycleInterleavingCamera();
        await using var manager = new CameraManager();
        manager.Register(device);
        await manager.StartAsync(device.Id);

        var close = manager.CloseAsync(device.Id);
        await device.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var startsBefore = device.StartCalls;
        var start = manager.StartAsync(device.Id);
        await Task.Delay(50);
        Assert.Equal(startsBefore, device.StartCalls);

        device.ReleaseClose();
        await close;
        await start;
        Assert.Equal(startsBefore + 1, device.StartCalls);
        Assert.Equal(CameraState.Streaming, device.State);
    }

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
    public async Task OlderStopFinalization_CannotOverwriteNewAcquisitionGeneration()
    {
        var device = new DrainBlockingCamera();
        using var hub = new CameraFrameHub(2);
        await using var worker = new CameraAcquisitionWorker(device, hub);
        var secondStopReachedFinalize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = typeof(CameraAcquisitionWorker).GetProperty("BeforeStopFinalizeAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(hook);
        hook!.SetValue(worker, (Func<long, Task>)(async stopInvocation =>
        {
            if (stopInvocation == 2)
            {
                secondStopReachedFinalize.TrySetResult();
                await releaseSecondStop.Task;
            }
        }));

        await worker.StartAsync();
        await WaitUntilAsync(() => device.ActiveGrabs == 1);
        var firstStop = worker.StopAsync();
        await WaitUntilAsync(() => worker.Snapshot().AcquisitionState == CameraAcquisitionState.Stopping);
        var olderStop = worker.StopAsync();
        device.Release();

        await secondStopReachedFinalize.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await firstStop; // invocation 1 clears the old generation while invocation 2 is held back
        await worker.StartAsync();
        await WaitUntilAsync(() => worker.IsRunning && worker.Snapshot().AcquisitionState == CameraAcquisitionState.Running);

        releaseSecondStop.TrySetResult();
        await olderStop;
        Assert.True(worker.IsRunning);
        Assert.Equal(CameraAcquisitionState.Running, worker.Snapshot().AcquisitionState);

        await worker.StopAsync();
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task UnresponsiveDevice_StopIsBoundedByIndependentDrainGrace()
    {
        // F08 回归：设备在取消后仍不返回时，Stop 必须在独立的收尾期限内完成（不依赖调用方 token，
        // 也不无限等待），并把超时留在运行诊断中。
        // Q03 更新：超时不再报告 Stopped 并放行新 Start——进入"停止未确认"隔离：
        // 句柄保留、新 Start 被显式拒绝，直到旧调用真正返回（下一次 Stop 重试确认）。
        var device = new DrainBlockingCamera(); // 不 Release：GrabAsync 永不返回
        using var hub = new CameraFrameHub(2);
        var worker = new CameraAcquisitionWorker(device, hub, stopDrainGrace: TimeSpan.FromMilliseconds(150));
        try
        {
            await worker.StartAsync();
            // Running is published before GrabAsync starts; synchronize on the actual device call
            // so this bounded-drain assertion cannot race the worker scheduler.
            await WaitUntilAsync(() => device.ActiveGrabs == 1);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await worker.StopAsync();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"stop must be bounded by the drain grace, took {sw.Elapsed}");
            // Q03：必须报告"停止未确认"（而不是 Stopped），且诊断中说明原因。
            Assert.Equal(CameraAcquisitionState.StopUnconfirmed, worker.Snapshot().AcquisitionState);
            Assert.Contains("not confirmed", worker.Snapshot().RuntimeError ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Q03：隔离期间 Start 必须被显式拒绝（不能静默返回、也不能并发第二次 Grab）。
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync());
            Assert.Contains("stop could not be confirmed", rejected.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, device.ActiveGrabs);

            // Q03：旧调用返回后，重试 Stop 解除隔离并完成清理（仍不得出现第二条采集循环）。
            device.Release();
            await WaitUntilAsync(() => worker.IsRunning == false);
            await worker.StopAsync();
            Assert.Equal(CameraAcquisitionState.Stopped, worker.Snapshot().AcquisitionState);
            // 迟到调用已退出：在途计数归零（其返回的帧因代次不匹配被丢弃，不会进入帧环）。
            Assert.Equal(0, device.ActiveGrabs);
            Assert.Equal(0, hub.LatestSequence);

            // Q03：隔离解除后 Start 恢复可用（新的代次）。
            await worker.StartAsync();
            await WaitUntilAsync(() => worker.IsRunning);
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
        private int _activeGrabs;
        private int _stopCalls;
        private int _closeCalls;
        private int _disposeCalls;

        public string Id => "drain-camera";
        public string Name => "Drain Camera";
        public string Driver => "fake";
        public string? Source => null;
        public CameraState State { get; private set; } = CameraState.Closed;
        public long FramesCaptured => 0;
        public string? LastError => null;
        public CameraSettings Settings { get; private set; } = new();
        public CameraCapabilities Capabilities { get; } = new(HostSimulatedExternalTrigger: false);

        /// <summary>Q03：当前在途的 Grab 调用数——隔离断言"绝不能有第二条采集循环"的依据。</summary>
        public int ActiveGrabs => Volatile.Read(ref _activeGrabs);
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public int CloseCalls => Volatile.Read(ref _closeCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void Release() => _release.TrySetResult();

        public Task OpenAsync(CancellationToken cancellationToken = default) { State = CameraState.Open; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref _closeCalls); State = CameraState.Closed; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken cancellationToken = default) { State = CameraState.Streaming; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref _stopCalls); if (State != CameraState.Closed) State = CameraState.Open; return Task.CompletedTask; }
        public Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
        public Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // 忽略取消：设备在调用方取消后仍需时间收尾；显式 Release 后返回一帧（loop 随即可观察到取消并退出）。
            Interlocked.Increment(ref _activeGrabs);
            try
            {
                await _release.Task;
                await Task.Delay(5);
                return new VisionFrame(Id, 1, DateTimeOffset.UtcNow, new Mat(2, 2, MatType.CV_8UC1, Scalar.All(1)), "Mono8");
            }
            finally { Interlocked.Decrement(ref _activeGrabs); }
        }

        public ValueTask DisposeAsync() { Interlocked.Increment(ref _disposeCalls); State = CameraState.Closed; return ValueTask.CompletedTask; }
    }

    private sealed class LifecycleInterleavingCamera : ICameraDevice
    {
        private readonly TaskCompletionSource _releaseClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startCalls;
        public string Id => "lifecycle-camera";
        public string Name => "Lifecycle Camera";
        public string Driver => "test";
        public string? Source => null;
        public CameraState State { get; private set; } = CameraState.Closed;
        public long FramesCaptured => 0;
        public string? LastError => null;
        public CameraSettings Settings { get; private set; } = new();
        public CameraCapabilities Capabilities { get; } = new();
        public int StartCalls => Volatile.Read(ref _startCalls);
        public TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseClose() => _releaseClose.TrySetResult();
        public Task OpenAsync(CancellationToken cancellationToken = default) { State = CameraState.Open; return Task.CompletedTask; }
        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseEntered.TrySetResult();
            await _releaseClose.Task.WaitAsync(cancellationToken);
            State = CameraState.Closed;
        }
        public Task StartAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref _startCalls); State = CameraState.Streaming; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { if (State != CameraState.Closed) State = CameraState.Open; return Task.CompletedTask; }
        public Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
        public Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
        public ValueTask DisposeAsync() { State = CameraState.Closed; return ValueTask.CompletedTask; }
    }
}
