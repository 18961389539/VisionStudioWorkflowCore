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
    private sealed record Runtime(ICameraDevice Device, CameraFrameHub Hub, CameraAcquisitionWorker Worker);

    private readonly ConcurrentDictionary<string, Runtime> _runtimes =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CameraDescriptor> List() => _runtimes.Values
        .Select(ToDescriptor)
        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void Register(ICameraDevice device, int ringCapacity = 4)
    {
        var hub = new CameraFrameHub(ringCapacity);
        var runtime = new Runtime(device, hub, new CameraAcquisitionWorker(device, hub));
        if (_runtimes.TryAdd(device.Id, runtime)) return;
        hub.Dispose();
        runtime.Worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        throw new InvalidOperationException($"Camera '{device.Id}' is already registered.");
    }

    public FileCameraDevice RegisterFile(string id, string name, string logicalSource, string physicalSource)
    {
        var camera = new FileCameraDevice(id, name, logicalSource, physicalSource);
        Register(camera);
        return camera;
    }

    private Runtime RequireRuntime(string id) =>
        _runtimes.TryGetValue(id, out var runtime)
            ? runtime
            : throw new KeyNotFoundException($"Camera '{id}' is not registered.");

    public ICameraDevice Require(string id) => RequireRuntime(id).Device;
    public CameraDescriptor Get(string id) => ToDescriptor(RequireRuntime(id));

    public CameraTransportTelemetry GetTransportTelemetry(string id)
    {
        var device = Require(id);
        if (device is ICameraTelemetryProvider provider && provider.GetTelemetry().Transport is { } transport)
            return transport;
        return new CameraTransportTelemetry(device.Id, device.Driver, false, "unavailable", DateTimeOffset.UtcNow,
            Error: "Camera adapter does not expose vendor transport telemetry.");
    }

    public Task OpenAsync(string id, CancellationToken cancellationToken = default) => Require(id).OpenAsync(cancellationToken);

    public async Task CloseAsync(string id, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        await runtime.Worker.StopAsync(cancellationToken);
        try { await runtime.Device.StopAsync(cancellationToken); } catch { }
        await runtime.Device.CloseAsync(cancellationToken);
        runtime.Hub.Clear();
    }

    public async Task StartAsync(string id, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        if (!runtime.Worker.IsRunning) runtime.Hub.Clear();
        await runtime.Worker.StartAsync(cancellationToken);
    }

    public async Task StopAsync(string id, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        await runtime.Worker.StopAsync(cancellationToken);
        await runtime.Device.StopAsync(cancellationToken);
    }

    public async Task ApplySettingsAsync(string id, CameraSettings settings, CancellationToken cancellationToken = default)
    {
        var runtime = RequireRuntime(id);
        var previousMode = runtime.Device.Settings.TriggerMode;
        var normalized = settings.Normalize();
        await runtime.Device.ApplySettingsAsync(normalized, cancellationToken);
        // A worker parked waiting for a trigger must be woken when switching back to free-run.
        if (previousMode is not CameraTriggerMode.Continuous && normalized.TriggerMode == CameraTriggerMode.Continuous)
            runtime.Worker.Wake();
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
        if (!runtime.Worker.IsRunning)
        {
            if (!autoStart) throw new InvalidOperationException($"Camera '{id}' acquisition is stopped and Auto Start is disabled.");
            runtime.Hub.Clear();
            await runtime.Worker.StartAsync(cancellationToken);
        }

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
        foreach (var runtime in _runtimes.Values)
        {
            try { await runtime.Worker.DisposeAsync(); } catch { }
            try { await runtime.Device.DisposeAsync(); } catch { }
            runtime.Hub.Dispose();
        }
        _runtimes.Clear();
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
