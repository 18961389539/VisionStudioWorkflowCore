using System.Collections.Concurrent;
using OpenCvSharp;

namespace VisionStudio.Engine.Camera;

public enum CameraFrameMode
{
    Latest,
    Next
}

/// <summary>
/// Process-level camera owner. Camera SDK lifecycle + continuous acquisition live here, not in Workflow Core.
/// Workflows and Web preview are independent consumers of CameraFrameHub leases.
/// </summary>
public sealed class CameraManager : IAsyncDisposable
{
    private sealed record Runtime(ICameraDevice Device, CameraFrameHub Hub, CameraAcquisitionWorker Worker)
    {
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);
        public int DeferredDisposeStarted;
    }

    private readonly ConcurrentDictionary<string, Runtime> _runtimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registrationSync = new();
    private readonly TimeSpan? _stopDrainGrace;
    private int _disposed;

    public CameraManager(TimeSpan? stopDrainGrace = null) => _stopDrainGrace = stopDrainGrace;

    public IReadOnlyList<CameraDescriptor> List() => _runtimes.Values
        .Select(ToDescriptor)
        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void Register(ICameraDevice device, int ringCapacity = 4)
    {
        lock (_registrationSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var hub = new CameraFrameHub(ringCapacity);
            var runtime = new Runtime(device, hub, new CameraAcquisitionWorker(device, hub, _stopDrainGrace));
            if (_runtimes.TryAdd(device.Id, runtime)) return;
            hub.Dispose();
            runtime.Worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException($"Camera '{device.Id}' is already registered.");
        }
    }

    public FileCameraDevice RegisterFile(string id, string name, string logicalSource, string physicalSource)
    {
        var camera = new FileCameraDevice(id, name, logicalSource, physicalSource);
        Register(camera);
        return camera;
    }

    private Runtime RequireRuntime(string id) =>
        Volatile.Read(ref _disposed) != 0
            ? throw new ObjectDisposedException(nameof(CameraManager))
            : _runtimes.TryGetValue(id, out var runtime)
            ? runtime
            : throw new KeyNotFoundException($"Camera '{id}' is not registered.");

    public ICameraDevice Require(string id) => RequireRuntime(id).Device;

    /// <summary>
    /// R02：该相机当前是否处于采集/收尾/隔离状态——租约接管的空闲判定依据
    /// （Stopped/Faulted 之外的任何状态都视为忙碌）。
    /// </summary>
    public bool IsAcquisitionActive(string id)
    {
        var worker = RequireRuntime(id).Worker;
        return worker.IsRunning ||
               worker.Snapshot().AcquisitionState is CameraAcquisitionState.Starting
                   or CameraAcquisitionState.Running
                   or CameraAcquisitionState.WaitingTrigger
                   or CameraAcquisitionState.Reconnecting
                   or CameraAcquisitionState.Stopping
                   or CameraAcquisitionState.StopUnconfirmed;
    }
    public CameraDescriptor Get(string id) => ToDescriptor(RequireRuntime(id));

    public CameraTransportTelemetry GetTransportTelemetry(string id)
    {
        var device = Require(id);
        if (device is ICameraTelemetryProvider provider && provider.GetTelemetry().Transport is { } transport)
            return transport;
        return new CameraTransportTelemetry(device.Id, device.Driver, false, "unavailable", DateTimeOffset.UtcNow,
            Error: "Camera adapter does not expose vendor transport telemetry.");
    }

    public async Task OpenAsync(string id, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        await runtime.LifecycleGate.WaitAsync(cancellationToken);
        try { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); await runtime.Device.OpenAsync(cancellationToken); }
        finally { runtime.LifecycleGate.Release(); }
    }

    public async Task CloseAsync(string id, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        await runtime.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await runtime.Worker.StopAsync(cancellationToken);
            if (runtime.Worker.IsStopUnconfirmed)
                throw new InvalidOperationException($"Camera '{id}' acquisition has an unconfirmed in-flight grab. The SDK session was retained; retry close after the grab returns.");
            try { await runtime.Device.StopAsync(cancellationToken); } catch { }
            await runtime.Device.CloseAsync(cancellationToken);
            runtime.Hub.Clear();
        }
        finally { runtime.LifecycleGate.Release(); }
    }

    public async Task StartAsync(string id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var runtime = RequireRuntime(id);
        await runtime.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!runtime.Worker.IsRunning && !runtime.Worker.IsStopUnconfirmed) runtime.Hub.Clear();
            await runtime.Worker.StartAsync(cancellationToken);
        }
        finally { runtime.LifecycleGate.Release(); }
    }

    public async Task StopAsync(string id, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        await runtime.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await runtime.Worker.StopAsync(cancellationToken);
            if (runtime.Worker.IsStopUnconfirmed)
                throw new InvalidOperationException($"Camera '{id}' acquisition has an unconfirmed in-flight grab. The SDK session was retained; retry stop after the grab returns.");
            await runtime.Device.StopAsync(cancellationToken);
        }
        finally { runtime.LifecycleGate.Release(); }
    }

    public async Task ApplySettingsAsync(string id, CameraSettings settings, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        await runtime.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var previousMode = runtime.Device.Settings.TriggerMode;
            var normalized = settings.Normalize();
            await runtime.Device.ApplySettingsAsync(normalized, cancellationToken);
            // A worker parked waiting for a trigger must be woken when switching back to free-run.
            if (previousMode is not CameraTriggerMode.Continuous && normalized.TriggerMode == CameraTriggerMode.Continuous)
                runtime.Worker.Wake();
        }
        finally { runtime.LifecycleGate.Release(); }
    }

    public CameraCommissioningCapabilities GetCommissioningCapabilities(string id)
        => Require(id) is ICameraFeatureProvider featureProvider
            ? featureProvider.CommissioningCapabilities
            : new CameraCommissioningCapabilities();

    public string? GetCommissioningProfileHash(string id)
        => (Require(id) as ICameraFeatureProvider)?.CommissioningProfileHash;

    public Task<IReadOnlyList<CameraFeatureDescriptor>> ListFeaturesAsync(string id, CancellationToken cancellationToken = default)
        => RequireFeatureProvider(id).ListFeaturesAsync(cancellationToken);

    public Task<CameraCommissioningProfile> ReadCommissioningProfileAsync(string id, CancellationToken cancellationToken = default)
        => RequireFeatureProvider(id).ReadCommissioningProfileAsync(cancellationToken);

    public Task<CameraCommissioningApplyResult> ApplyCommissioningProfileAsync(string id, CameraCommissioningProfile profile, CancellationToken cancellationToken = default)
        => RequireFeatureProvider(id).ApplyCommissioningProfileAsync(profile, cancellationToken);

    public Task SetFeatureAsync(string id, string key, string value, CancellationToken cancellationToken = default)
        => RequireFeatureProvider(id).SetFeatureAsync(key, value, cancellationToken);

    private ICameraFeatureProvider RequireFeatureProvider(string id)
        => Require(id) is ICameraFeatureProvider featureProvider
            ? featureProvider
            : throw new InvalidOperationException($"Camera '{id}' does not expose a commissioning feature provider.");

    public void Trigger(string id)
    {
        var runtime = RequireRuntime(id);
        if (!runtime.Worker.IsRunning)
            throw new InvalidOperationException($"Camera '{id}' acquisition is stopped. Start it before triggering.");
        runtime.Worker.Trigger();
    }


    public long GetLatestSequence(string id) => RequireRuntime(id).Hub.LatestSequence;

    public CameraFrameTimingSnapshot? GetLatestFrameTiming(string id)
    {
        var runtime = RequireRuntime(id);
        using var frame = runtime.Hub.TryAcquireLatest();
        return frame is null ? null : new CameraFrameTimingSnapshot(frame.CameraId, frame.Sequence, frame.Timestamp, frame.DeviceTimestampNs, frame.TriggerId);
    }

    public async ValueTask<CameraFrameTimingSnapshot> WaitForFrameTimingAsync(string id, long afterSequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        using var frame = await runtime.Hub.WaitForNextAsync(afterSequence, timeout, cancellationToken);
        return new CameraFrameTimingSnapshot(frame.CameraId, frame.Sequence, frame.Timestamp, frame.DeviceTimestampNs, frame.TriggerId);
    }

    /// <summary>Wait for and return an owned lease for the next frame after the supplied sequence.</summary>
    public ValueTask<CameraFrameLease> WaitForFrameLeaseAsync(string id, long afterSequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        return runtime.Hub.WaitForNextAsync(afterSequence, timeout, cancellationToken);
    }

    public async Task<CameraTimeSynchronizationStatus> GetTimeSynchronizationStatusAsync(string id, CancellationToken cancellationToken = default)
    {
        var device = Require(id);
        if (device is not ICameraSynchronizationProvider provider)
            return new CameraTimeSynchronizationStatus(id, device.Driver, false, false, CameraPtpClockState.Unsupported, CapturedAt: DateTimeOffset.UtcNow);
        return await provider.GetTimeSynchronizationStatusAsync(cancellationToken);
    }
    public async ValueTask<CameraFrameLease> AcquireAsync(
        string id,
        CameraFrameMode mode,
        TimeSpan timeout,
        bool autoStart,
        bool triggerBeforeGrab = false,
        CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        await runtime.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!runtime.Worker.IsRunning)
            {
                if (!autoStart) throw new InvalidOperationException($"Camera '{id}' acquisition is stopped and Auto Start is disabled.");
                runtime.Hub.Clear();
                await runtime.Worker.StartAsync(cancellationToken);
            }
        }
        finally { runtime.LifecycleGate.Release(); }

        if (triggerBeforeGrab)
        {
            var beforeTrigger = runtime.Hub.LatestSequence;
            runtime.Worker.Trigger();
            return await runtime.Hub.WaitForNextAsync(beforeTrigger, timeout, cancellationToken);
        }

        if (mode == CameraFrameMode.Latest)
            return await runtime.Hub.WaitForLatestAsync(timeout, cancellationToken);

        var current = runtime.Hub.LatestSequence;
        return await runtime.Hub.WaitForNextAsync(current, timeout, cancellationToken);
    }

    /// <summary>
    /// Preview consumes the same continuous acquisition stream as algorithms. It never calls device.GrabAsync itself.
    /// </summary>
    public async Task<CameraPreview> CapturePreviewAsync(
        string id,
        int maxWidth = 960,
        int jpegQuality = 80,
        CancellationToken cancellationToken = default)
    {
        using var frame = await AcquireAsync(id, CameraFrameMode.Latest, TimeSpan.FromSeconds(2), autoStart: true, triggerBeforeGrab: false, cancellationToken: cancellationToken);
        var source = frame.Image;
        using var display = new Mat();
        if (source.Width > maxWidth)
        {
            var height = Math.Max(1, (int)Math.Round(source.Height * (maxWidth / (double)source.Width)));
            Cv2.Resize(source, display, new Size(maxWidth, height), 0, 0, InterpolationFlags.Area);
        }
        else
        {
            source.CopyTo(display);
        }
        Cv2.ImEncode(".jpg", display, out var jpeg, [new ImageEncodingParam(ImwriteFlags.JpegQuality, Math.Clamp(jpegQuality, 30, 100))]);
        return new CameraPreview(jpeg, display.Width, display.Height, frame.Sequence, frame.Timestamp);
    }

    public async ValueTask DisposeAsync()
    {
        Runtime[] runtimes;
        lock (_registrationSync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            runtimes = _runtimes.Values.ToArray();
        }
        foreach (var runtime in runtimes)
        {
            await runtime.LifecycleGate.WaitAsync();
            try
            {
                try { await runtime.Worker.DisposeAsync(); } catch { }
                if (runtime.Worker.IsStopUnconfirmed)
                {
                    if (Interlocked.Exchange(ref runtime.DeferredDisposeStarted, 1) == 0)
                        _ = DisposeAfterStopConfirmationAsync(runtime);
                    continue;
                }
                try { await runtime.Device.DisposeAsync(); } catch { }
                runtime.Hub.Dispose();
                _runtimes.TryRemove(runtime.Device.Id, out _);
            }
            finally { runtime.LifecycleGate.Release(); }
        }
    }

    private async Task DisposeAfterStopConfirmationAsync(Runtime runtime)
    {
        try
        {
            await runtime.Worker.WaitForStopConfirmationAsync().ConfigureAwait(false);
            await runtime.LifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
            await runtime.Worker.DisposeAsync().ConfigureAwait(false);
            try { await runtime.Device.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await runtime.Device.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await runtime.Device.DisposeAsync().ConfigureAwait(false); } catch { }
            runtime.Hub.Dispose();
            _runtimes.TryRemove(runtime.Device.Id, out _);
            }
            finally { runtime.LifecycleGate.Release(); }
        }
        catch { /* Process shutdown remains best-effort; never release the device before the grab exits. */ }
    }

    private static CameraDescriptor ToDescriptor(Runtime runtime) => new(
        runtime.Device.Id,
        runtime.Device.Name,
        runtime.Device.Driver,
        runtime.Device.State,
        runtime.Device.FramesCaptured,
        runtime.Device.Source,
        runtime.Device.LastError,
        runtime.Device.Settings,
        runtime.Device.Capabilities,
        runtime.Worker.Snapshot());
}
