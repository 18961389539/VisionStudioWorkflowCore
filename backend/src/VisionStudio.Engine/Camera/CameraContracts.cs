using OpenCvSharp;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// Frame returned by a vendor/device adapter. Ownership of Image belongs to this object until DetachImage().
/// Vendor adapters should copy/wrap their SDK buffer here and never expose SDK handles to workflow code.
/// </summary>
public sealed class VisionFrame : IDisposable
{
    private Mat? _image;

    public VisionFrame(string cameraId, long sequence, DateTimeOffset timestamp, Mat image, string pixelFormat, long? deviceTimestampNs = null, long? triggerId = null)
    {
        CameraId = cameraId;
        Sequence = sequence;
        Timestamp = timestamp;
        _image = image;
        PixelFormat = pixelFormat;
        DeviceTimestampNs = deviceTimestampNs;
        TriggerId = triggerId;
    }

    public string CameraId { get; }
    public long Sequence { get; }
    public DateTimeOffset Timestamp { get; }
    public string PixelFormat { get; }
    public long? DeviceTimestampNs { get; }
    public long? TriggerId { get; }
    public Mat Image => _image ?? throw new ObjectDisposedException(nameof(VisionFrame));

    public Mat DetachImage()
    {
        var image = _image ?? throw new ObjectDisposedException(nameof(VisionFrame));
        _image = null;
        return image;
    }

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}

public interface ICameraDevice : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    string Driver { get; }
    string? Source { get; }
    CameraState State { get; }
    long FramesCaptured { get; }
    string? LastError { get; }
    CameraSettings Settings { get; }
    CameraCapabilities Capabilities { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default);
    Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default);
    ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Vendor adapters can throw this when a trigger wait expires without treating the camera as disconnected.</summary>
public sealed class CameraFrameTimeoutException(string message) : TimeoutException(message);

/// <summary>Optional adapter telemetry surfaced through CameraAcquisitionStats.</summary>
public interface ICameraTelemetryProvider
{
    CameraDeviceTelemetry GetTelemetry();
}

/// <summary>Runtime-discovered vendor camera adapter. Vendor SDK assemblies remain optional and externally installed.</summary>
public interface ICameraAdapterProvider
{
    string Driver { get; }
    string Vendor { get; }
    bool IsSdkAvailable { get; }
    string? SdkError { get; }
    ValueTask<IReadOnlyList<CameraDiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken = default);
    ICameraDevice Create(CameraAdapterRegistration registration);
}

public interface ICameraSourceProvenanceProvider
{
    CameraSourceProvenance GetSourceProvenance();
}

/// <summary>
/// Optional interface implemented by industrial vendor cameras that expose commissioning features.
/// Implementations own all vendor SDK reflection/GenICam details; the Engine/API only sees normalized contracts.
/// </summary>
public interface ICameraFeatureProvider
{
    CameraCommissioningCapabilities CommissioningCapabilities { get; }
    string? CommissioningProfileHash { get; }
    Task<IReadOnlyList<CameraFeatureDescriptor>> ListFeaturesAsync(CancellationToken cancellationToken = default);
    Task<CameraCommissioningProfile> ReadCommissioningProfileAsync(CancellationToken cancellationToken = default);
    Task<CameraCommissioningApplyResult> ApplyCommissioningProfileAsync(CameraCommissioningProfile profile, CancellationToken cancellationToken = default);
    Task SetFeatureAsync(string key, string value, CancellationToken cancellationToken = default);
}

/// <summary>Optional per-camera PTP/timestamp surface used by multi-camera synchronization diagnostics.</summary>
public interface ICameraSynchronizationProvider
{
    Task<CameraTimeSynchronizationStatus> GetTimeSynchronizationStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional provider-level GigE Vision Action Command broadcaster.</summary>
public interface ICameraActionCommandProvider
{
    string Driver { get; }
    CameraActionCommandCapabilities ActionCommandCapabilities { get; }
    Task<CameraActionCommandResult> IssueActionCommandAsync(CameraActionCommandRequest request, CancellationToken cancellationToken = default);
}
