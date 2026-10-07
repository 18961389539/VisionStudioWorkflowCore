using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;

namespace VisionStudio.Engine;

public sealed record RunArtifactOptions(
    bool DeferEncoding = false,
    int PreviewSampleEvery = 1,
    int ReplaySampleEvery = 1,
    long MaxRawBytes = 32L * 1024 * 1024);

public static class RunArtifactSampling
{
    public static bool Include(string runId, string disposition, int every, bool replay = false)
    {
        if (!disposition.Equals("OK", StringComparison.OrdinalIgnoreCase) || every <= 1) return true;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((replay ? "replay:" : "") + runId));
        return BitConverter.ToUInt32(bytes, 0) % (uint)every == 0;
    }
}

/// <summary>Owned copies of sampled images. The background trace consumer must dispose them after encoding.</summary>
public sealed class RunImageArtifacts : IDisposable
{
    private Mat? _preview;
    private Mat? _replay;
    private readonly string? _sourceNodeId;
    public string? Warning { get; }
    public long RawBytes { get; }

    private RunImageArtifacts(Mat? preview, Mat? replay, string? sourceNodeId, long bytes, string? warning)
        => (_preview, _replay, _sourceNodeId, RawBytes, Warning) = (preview, replay, sourceNodeId, bytes, warning);

    public static RunImageArtifacts Capture(Mat? preview, Mat? replay, string? sourceNodeId, long maxBytes)
    {
        var same = ReferenceEquals(preview, replay);
        var bytes = checked((preview is null ? 0 : preview.Total() * preview.ElemSize())
            + (replay is null || same ? 0 : replay.Total() * replay.ElemSize()));
        if (bytes > maxBytes)
            return new(null, null, sourceNodeId, 0, $"Sampled image artifacts omitted: {bytes} raw bytes exceed the per-run limit {maxBytes}.");
        Mat? previewCopy = null;
        Mat? replayCopy = null;
        try
        {
            previewCopy = preview?.Clone();
            replayCopy = same ? previewCopy : replay?.Clone();
            return new(previewCopy, replayCopy, sourceNodeId, bytes, null);
        }
        catch
        {
            if (!ReferenceEquals(previewCopy, replayCopy)) replayCopy?.Dispose();
            previewCopy?.Dispose();
            throw;
        }
    }

    public WorkflowRunResult Encode(WorkflowRunResult result)
    {
        byte[]? jpeg = null;
        ReplayInputArtifact? replayInput = null;
        if (_preview is not null) Cv2.ImEncode(".jpg", _preview, out jpeg);
        if (_replay is not null)
        {
            Cv2.ImEncode(".png", _replay, out var png);
            replayInput = new(png, _sourceNodeId!, _replay.Cols, _replay.Rows);
        }
        return result with
        {
            PreviewJpeg = jpeg,
            PreviewAvailable = jpeg is not null,
            PreviewWidth = _preview?.Cols ?? 0,
            PreviewHeight = _preview?.Rows ?? 0,
            ReplayInput = replayInput,
            DeferredArtifacts = null
        };
    }

    public void Dispose()
    {
        var preview = Interlocked.Exchange(ref _preview, null);
        var replay = Interlocked.Exchange(ref _replay, null);
        if (!ReferenceEquals(preview, replay)) replay?.Dispose();
        preview?.Dispose();
    }
}
