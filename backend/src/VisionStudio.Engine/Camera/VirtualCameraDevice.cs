using OpenCvSharp;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// Deterministic software camera used to validate acquisition, trigger, settings and workflow paths without hardware.
/// </summary>
public sealed class VirtualCameraDevice : ICameraDevice, IHardwareProvenanceProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;
    private CameraState _state = CameraState.Closed;
    private string? _error;
    private CameraSettings _settings = new(TargetFps: 12, TriggerMode: CameraTriggerMode.Continuous);

    public VirtualCameraDevice(string id = "virtual-1", string name = "Virtual Camera 1")
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public string Driver => "virtual";
    public string? Source => "Generated frame";
    public CameraState State => _state;
    public long FramesCaptured => Interlocked.Read(ref _sequence);
    public string? LastError => _error;
    public CameraSettings Settings => _settings;
    public CameraCapabilities Capabilities { get; } = new(HostSimulatedExternalTrigger: true, MaxFps: 120);

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state is CameraState.Open or CameraState.Streaming) return;
            _error = null;
            _state = CameraState.Open;
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state = CameraState.Closed;
            _error = null;
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { _state = CameraState.Streaming; }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state == CameraState.Streaming) _state = CameraState.Open;
        }
        finally { _gate.Release(); }
    }

    public async Task ApplySettingsAsync(CameraSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { _settings = settings.Normalize(); }
        finally { _gate.Release(); }
    }

    public Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default)
    {
        // Test drivers do not have a vendor trigger command; CameraAcquisitionWorker's signal gates the next GrabAsync.
        return Task.CompletedTask;
    }

    public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state == CameraState.Closed)
                throw new InvalidOperationException($"Camera '{Id}' is closed.");
            if (_state == CameraState.Faulted)
                throw new InvalidOperationException($"Camera '{Id}' is faulted: {_error}");

            var sequence = Interlocked.Increment(ref _sequence);
            const int width = 1280;
            const int height = 720;
            var exposureFactor = Math.Clamp(_settings.ExposureUs / 5000d, 0.25, 3.0);
            var gainFactor = 1.0 + _settings.GainDb / 24d;
            byte L(double value) => (byte)Math.Clamp(value * exposureFactor * gainFactor, 0, 255);

            var image = new Mat(height, width, MatType.CV_8UC1, Scalar.All(L(24)));
            var centerX = 360 + (int)((sequence * 13) % 520);
            var centerY = 360 + (int)(Math.Sin(sequence / 7d) * 120);
            Cv2.Circle(image, new Point(centerX, centerY), 92, Scalar.All(L(224)), -1, LineTypes.AntiAlias);
            Cv2.Rectangle(image, new Rect(95, 110, 190, 105), Scalar.All(L(92)), -1);
            Cv2.Line(image, new Point(75, 610), new Point(1160, 545), Scalar.All(L(132)), 8, LineTypes.AntiAlias);
            Cv2.PutText(image, $"Virtual #{sequence}", new Point(28, 48), HersheyFonts.HersheySimplex, 1.0, Scalar.All(L(180)), 2, LineTypes.AntiAlias);

            return new VisionFrame(Id, sequence, DateTimeOffset.UtcNow, image, "Mono8");
        }
        finally { _gate.Release(); }
    }

    public HardwareProvenanceData GetHardwareProvenance() => new(
        Manufacturer: "VisionStudio",
        ProductName: Name,
        Model: "VirtualCamera",
        SerialNumber: Id,
        HardwareRevision: "sim-v1",
        FirmwareVersion: "simulated",
        SoftwareVersion: typeof(VirtualCameraDevice).Assembly.GetName().Version?.ToString(),
        Attributes: new Dictionary<string, string> { ["simulation"] = "true" });

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
