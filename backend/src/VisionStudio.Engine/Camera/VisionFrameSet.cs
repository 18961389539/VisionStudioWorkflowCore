using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// Workflow-owned synchronized image set. The set owns every contained VisionImage and disposes them together.
/// </summary>
public sealed class VisionFrameSet : IVisionFrameSet, IDisposable
{
    private Dictionary<string, IVisionImage>? _images;

    public VisionFrameSet(
        string groupId,
        string timestampBasis,
        double triggerSkewUs,
        bool withinTolerance,
        IReadOnlyDictionary<string, IVisionImage> images,
        IReadOnlyDictionary<string, VisionFrameSetFrameInfo> frames)
    {
        GroupId = groupId;
        TimestampBasis = timestampBasis;
        TriggerSkewUs = triggerSkewUs;
        WithinTolerance = withinTolerance;
        _images = new Dictionary<string, IVisionImage>(images, StringComparer.OrdinalIgnoreCase);
        Frames = new Dictionary<string, VisionFrameSetFrameInfo>(frames, StringComparer.OrdinalIgnoreCase);
    }

    public string GroupId { get; }
    public string TimestampBasis { get; }
    public double TriggerSkewUs { get; }
    public bool WithinTolerance { get; }
    public IReadOnlyDictionary<string, IVisionImage> Images => _images ?? throw new ObjectDisposedException(nameof(VisionFrameSet));
    public IReadOnlyDictionary<string, VisionFrameSetFrameInfo> Frames { get; }

    public IVisionImage GetImage(string cameraId)
        => Images.TryGetValue(cameraId, out var image)
            ? image
            : throw new KeyNotFoundException($"FrameSet '{GroupId}' does not contain camera '{cameraId}'. Available: {string.Join(", ", Images.Keys)}");

    public void Dispose()
    {
        var images = Interlocked.Exchange(ref _images, null);
        if (images is null) return;
        foreach (var disposable in images.Values.OfType<IDisposable>()) disposable.Dispose();
    }
}

public sealed record SynchronizedFrameSetCaptureRequest(
    string GroupId,
    bool Scheduled = false,
    int? LeadTimeMs = null,
    int FrameTimeoutMs = 3000);

/// <summary>
/// Engine-facing boundary for synchronized capture. The API/runtime host owns group persistence and vendor Action Command orchestration.
/// </summary>
public interface ISynchronizedFrameSetService
{
    Task<VisionFrameSet> CaptureAsync(SynchronizedFrameSetCaptureRequest request, CancellationToken cancellationToken = default);
}
