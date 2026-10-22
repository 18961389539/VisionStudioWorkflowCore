using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;
using VisionStudio.Engine.Provenance;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// Media-library backed offline camera. The host resolves a media:// logical source to a sandboxed physical path.
/// Direct user filesystem paths are deliberately not accepted by this adapter's public contract.
/// Directories recursively cycle supported image files in deterministic relative-path order.
/// </summary>
public sealed class FileCameraDevice : ICameraDevice, ICameraSourceProvenanceProvider, IHardwareProvenanceProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _physicalSource;
    private string[] _files = [];
    private int _index;
    private long _sequence;
    private CameraState _state = CameraState.Closed;
    private string? _error;
    private CameraSettings _settings = new(TargetFps: 5, TriggerMode: CameraTriggerMode.Continuous);

    public FileCameraDevice(string id, string name, string logicalSource, string physicalSource)
    {
        if (string.IsNullOrWhiteSpace(logicalSource) || !logicalSource.StartsWith("media://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("File Camera logical source must use media://.", nameof(logicalSource));
        Id = id;
        Name = name;
        Source = logicalSource;
        _physicalSource = Path.GetFullPath(physicalSource);
    }

    public string Id { get; }
    public string Name { get; }
    public string Driver => "file";
    public string Source { get; }
    public CameraState State => _state;
    public long FramesCaptured => Interlocked.Read(ref _sequence);
    public string? LastError => _error;
    public CameraSettings Settings => _settings;
    public CameraCapabilities Capabilities { get; } = new(Exposure: false, Gain: false, HostSimulatedExternalTrigger: true, MaxFps: 60);

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state is CameraState.Open or CameraState.Streaming) return;
            try
            {
                ReloadFiles();
                _error = null;
                _state = CameraState.Open;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _state = CameraState.Faulted;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _files = [];
            _index = 0;
            _state = CameraState.Closed;
            _error = null;
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A stopped/open File Camera refreshes its media collection before every new acquisition run.
            ReloadFiles();
            _state = CameraState.Streaming;
        }
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

    public Task ExecuteSoftwareTriggerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask<VisionFrame> GrabAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state == CameraState.Closed)
                throw new InvalidOperationException($"Camera '{Id}' is closed.");
            if (_state == CameraState.Faulted)
                throw new InvalidOperationException($"Camera '{Id}' is faulted: {_error}");
            if (_files.Length == 0)
                throw new InvalidOperationException($"Camera '{Id}' has no image files loaded.");

            var fileIndex = Math.Abs(_index++) % _files.Length;
            var file = _files[fileIndex];
            var image = Cv2.ImRead(file, ImreadModes.Grayscale);
            if (image.Empty())
            {
                image.Dispose();
                throw new InvalidOperationException($"OpenCV failed to read media item '{Path.GetFileName(file)}'.");
            }

            var sequence = Interlocked.Increment(ref _sequence);
            return new VisionFrame(Id, sequence, DateTimeOffset.UtcNow, image, "Mono8");
        }
        finally { _gate.Release(); }
    }

    public CameraSourceProvenance GetSourceProvenance()
    {
        var files = ResolveFiles(_physicalSource);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        foreach (var file in files)
        {
            var relative = Directory.Exists(_physicalSource)
                ? Path.GetRelativePath(_physicalSource, file).Replace('\\', '/')
                : Path.GetFileName(file);
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(new byte[] { 0 });
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
                totalBytes += read;
            }
            hash.AppendData(new byte[] { 0xFF });
        }
        return new CameraSourceProvenance(Source, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), files.Length, totalBytes);
    }

    public HardwareProvenanceData GetHardwareProvenance() => new(
        Manufacturer: "VisionStudio",
        ProductName: Name,
        Model: "MediaFileCamera",
        SerialNumber: Id,
        HardwareRevision: "sandbox-v1",
        FirmwareVersion: "n/a",
        SoftwareVersion: typeof(FileCameraDevice).Assembly.GetName().Version?.ToString(),
        Attributes: new Dictionary<string, string> { ["logicalSource"] = Source, ["offlineSource"] = "true" });

    private void ReloadFiles()
    {
        _files = ResolveFiles(_physicalSource);
        if (_files.Length == 0)
            throw new FileNotFoundException($"No supported images found in media source '{Source}'.");
        _index = 0;
    }

    private static string[] ResolveFiles(string path)
    {
        if (File.Exists(path) && Extensions.Contains(Path.GetExtension(path))) return [path];
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => Extensions.Contains(Path.GetExtension(file)))
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
