using OpenCvSharp;

namespace VisionStudio.Engine.Camera;

public enum CameraState
{
    Closed,
    Open,
    Streaming,
    Faulted
}

public enum CameraTriggerMode
{
    Continuous,
    Software,
    External
}

public enum CameraOutputPixelFormat
{
    Auto,
    Mono8,
    Bgr8
}

public enum CameraAcquisitionState
{
    Stopped,
    Starting,
    /// <summary>F08：停止收尾进行中（等待旧采集循环退出、控制句柄尚未清理）。</summary>
    Stopping,
    Running,
    WaitingTrigger,
    Reconnecting,
    Faulted
}

public sealed record CameraSettings(
    double ExposureUs = 5000,
    double GainDb = 0,
    double TargetFps = 10,
    CameraTriggerMode TriggerMode = CameraTriggerMode.Continuous,
    CameraOutputPixelFormat OutputPixelFormat = CameraOutputPixelFormat.Auto,
    string ExternalTriggerSource = "Line1")
{
    public CameraSettings Normalize() => this with
    {
        ExposureUs = Math.Clamp(ExposureUs, 10, 10_000_000),
        GainDb = Math.Clamp(GainDb, 0, 48),
        TargetFps = Math.Clamp(TargetFps, 0.2, 240),
        ExternalTriggerSource = string.IsNullOrWhiteSpace(ExternalTriggerSource) ? "Line1" : ExternalTriggerSource.Trim()
    };
}

public sealed record CameraCapabilities(
    bool Exposure = true,
    bool Gain = true,
    bool FrameRate = true,
    bool SoftwareTrigger = true,
    bool ExternalTrigger = true,
    bool HostSimulatedExternalTrigger = false,
    double MinExposureUs = 10,
    double MaxExposureUs = 10_000_000,
    double MinGainDb = 0,
    double MaxGainDb = 48,
    double MaxFps = 240,
    IReadOnlyList<CameraOutputPixelFormat>? OutputPixelFormats = null);

public sealed record CameraAcquisitionStats(
    CameraAcquisitionState AcquisitionState,
    long FramesPublished,
    long RingOverwrites,
    long AcquisitionErrors,
    long ReconnectCount,
    long LastSequence,
    double ActualFps,
    DateTimeOffset? LastFrameAt,
    string? RuntimeError,
    long FrameTimeouts = 0,
    long DriverDroppedFrames = 0,
    string? NativePixelFormat = null,
    CameraTransportTelemetry? Transport = null);

public sealed record CameraDescriptor(
    string Id,
    string Name,
    string Driver,
    CameraState State,
    long FramesCaptured,
    string? Source,
    string? Error,
    CameraSettings Settings,
    CameraCapabilities Capabilities,
    CameraAcquisitionStats Acquisition);

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


public sealed record CameraTransportTelemetry(
    string CameraId,
    string Driver,
    bool Native,
    string Source,
    DateTimeOffset CapturedAt,
    long? ReceivedBytes = null,
    long? ReceivedFrames = null,
    long? LostFrames = null,
    long? FailedFrames = null,
    long? BufferUnderruns = null,
    long? ReceivedPackets = null,
    long? LostPackets = null,
    long? FailedPackets = null,
    long? ResendRequests = null,
    long? ResentPackets = null,
    long? Resynchronizations = null,
    double? ThroughputMbps = null,
    string? Error = null)
{
    public bool HasNativeCounters => Native && new long?[]
    {
        ReceivedBytes, ReceivedFrames, LostFrames, FailedFrames, BufferUnderruns, ReceivedPackets,
        LostPackets, FailedPackets, ResendRequests, ResentPackets, Resynchronizations
    }.Any(x => x is not null);
}

public sealed record CameraDeviceTelemetry(
    long DriverDroppedFrames = 0,
    string? NativePixelFormat = null,
    long? NativeFrameId = null,
    CameraTransportTelemetry? Transport = null);

/// <summary>Optional adapter telemetry surfaced through CameraAcquisitionStats.</summary>
public interface ICameraTelemetryProvider
{
    CameraDeviceTelemetry GetTelemetry();
}

public sealed record CameraDiscoveredDevice(
    string Driver,
    string Vendor,
    string Model,
    string SerialNumber,
    string? UserDefinedName,
    string? IpAddress,
    string? Transport,
    string DeviceKey,
    string? FirmwareVersion = null);

public sealed record CameraAdapterRegistration(
    string Id,
    string? Name,
    string? SerialNumber = null,
    string? UserDefinedName = null,
    string? DeviceKey = null,
    CameraSettings? Settings = null);

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

public sealed record CameraPreview(byte[] Jpeg, int Width, int Height, long Sequence, DateTimeOffset Timestamp);
public sealed record CameraSourceProvenance(
    string LogicalSource,
    string ContentSha256,
    int ItemCount,
    long TotalBytes);

public interface ICameraSourceProvenanceProvider
{
    CameraSourceProvenance GetSourceProvenance();
}


public enum CameraFeatureValueKind
{
    Boolean,
    Integer,
    Float,
    Enum,
    String
}

/// <summary>Normalized GenICam/vendor feature metadata used by the commissioning UI.</summary>
public sealed record CameraFeatureDescriptor(
    string Key,
    string DisplayName,
    string Category,
    CameraFeatureValueKind ValueKind,
    string? Value,
    bool Readable,
    bool Writable,
    bool ProfileEligible,
    string? Unit = null,
    double? Min = null,
    double? Max = null,
    double? Increment = null,
    IReadOnlyList<string>? Options = null,
    string? Description = null);

public sealed record CameraCommissioningCapabilities(
    bool FeatureBrowser = false,
    bool GigENetworkTuning = false,
    bool TriggerDelay = false,
    bool LineDebouncer = false,
    bool StrobeOutput = false,
    bool Ptp = false,
    bool ActionCommand = false,
    IReadOnlyList<string>? InputLines = null,
    IReadOnlyList<string>? OutputLines = null,
    IReadOnlyList<string>? OutputSources = null);

/// <summary>
/// Portable, vendor-neutral commissioning profile. Null values mean "leave unchanged / unsupported".
/// AdvancedFeatures is restricted by each adapter to ProfileEligible features.
/// </summary>
public sealed record CameraCommissioningProfile(
    int SchemaVersion = 1,
    CameraSettings? Acquisition = null,
    int? PacketSizeBytes = null,
    long? InterPacketDelayTicks = null,
    double? TriggerDelayUs = null,
    string? TriggerInputLine = null,
    double? LineDebouncerUs = null,
    string? OutputLine = null,
    string? OutputSource = null,
    bool? OutputInverted = null,
    bool? StrobeEnabled = null,
    double? StrobeDelayUs = null,
    double? StrobeDurationUs = null,
    double? StrobePreDelayUs = null,
    bool? PtpEnabled = null,
    int? ActionDeviceKey = null,
    int? ActionGroupKey = null,
    int? ActionGroupMask = null,
    int? ActionSelector = null,
    IReadOnlyDictionary<string, string>? AdvancedFeatures = null);

public sealed record CameraCommissioningApplyResult(
    CameraCommissioningProfile Applied,
    string ProfileHash,
    IReadOnlyList<string> AppliedFeatures,
    IReadOnlyList<string> SkippedFeatures);

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



public enum CameraPtpClockState
{
    Unsupported,
    Disabled,
    Initializing,
    Listening,
    Master,
    Slave,
    Locked,
    Faulted,
    Unknown
}

public sealed record CameraTimeSynchronizationStatus(
    string CameraId,
    string Driver,
    bool PtpSupported,
    bool PtpEnabled,
    CameraPtpClockState State,
    long? OffsetFromMasterNs = null,
    long? DeviceTimestampNs = null,
    string? MasterClockId = null,
    long? DeviceTickFrequencyHz = null,
    string? Error = null,
    DateTimeOffset? CapturedAt = null);

public sealed record CameraActionCommandCapabilities(
    bool Immediate = false,
    bool Scheduled = false,
    bool Acknowledgements = false,
    bool DirectedBroadcast = true,
    bool ScheduledRequiresTickFrequency = false);

public sealed record CameraActionCommandRequest(
    int DeviceKey,
    int GroupKey,
    int GroupMask,
    string BroadcastAddress = "255.255.255.255",
    long? ScheduledDeviceTimeNs = null,
    int TimeoutMs = 1000,
    long? DeviceTickFrequencyHz = null);

public sealed record CameraActionCommandResult(
    string Driver,
    bool Scheduled,
    long? ScheduledDeviceTimeNs,
    DateTimeOffset IssuedAt,
    int AcknowledgedDevices,
    IReadOnlyList<string> Messages);

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

public sealed record CameraFrameTimingSnapshot(
    string CameraId,
    long Sequence,
    DateTimeOffset HostTimestamp,
    long? DeviceTimestampNs,
    long? TriggerId);
