using System.Collections.Concurrent;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record RunArtifact(
    byte[]? PreviewJpeg,
    int Width,
    int Height,
    IReadOnlyList<VisionOverlay> Overlays,
    IReadOnlyList<RunNodeImage> NodeImages);

/// <summary>
/// Small in-memory artifact cache for the MVP viewer. Bounded retention prevents repeated debug runs
/// from keeping JPEG/overlay payloads forever. Production can replace this with a run/image repository.
/// Retention is bounded both by run count and by a total JPEG byte budget: high-resolution runs
/// can no longer grow memory without bound just because each run stays under the count cap.
/// </summary>
public sealed class RunStore
{
    private readonly int _maxRetainedRuns;
    private readonly long _maxTotalBytes;
    private readonly ConcurrentDictionary<string, RunArtifact> _runs = new();
    private readonly ConcurrentQueue<string> _order = new();
    private long _totalBytes;

    public RunStore(int maxRetainedRuns = 32, long maxTotalBytes = 256L * 1024 * 1024)
    {
        _maxRetainedRuns = maxRetainedRuns > 0
            ? maxRetainedRuns
            : throw new ArgumentOutOfRangeException(nameof(maxRetainedRuns));
        _maxTotalBytes = maxTotalBytes > 0
            ? maxTotalBytes
            : throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
    }

    public void Put(WorkflowRunResult result)
    {
        var artifact = new RunArtifact(
            result.PreviewJpeg,
            result.PreviewWidth,
            result.PreviewHeight,
            result.Overlays,
            result.NodeImages ?? []);
        var bytes = EstimateBytes(artifact);

        // 同 runId 重复 Put 时先扣掉旧工件的账，再记新账
        _order.Enqueue(result.RunId);
        if (_runs.TryRemove(result.RunId, out var replaced))
            Interlocked.Add(ref _totalBytes, -EstimateBytes(replaced));
        _runs[result.RunId] = artifact;
        Interlocked.Add(ref _totalBytes, bytes);

        // 超过条数或字节预算都从最旧的开始驱逐；_order 里可能残留已驱逐的过期 id，跳过即可。
        // 若单条工件自身超预算，同样会被驱逐：宁可不缓存这一次，也不让内存被撑爆。
        while (_runs.Count > _maxRetainedRuns || Interlocked.Read(ref _totalBytes) > _maxTotalBytes)
        {
            if (!_order.TryDequeue(out var expired)) break;
            if (_runs.TryRemove(expired, out var removed))
                Interlocked.Add(ref _totalBytes, -EstimateBytes(removed));
        }
    }

    public bool TryGet(string runId, out RunArtifact artifact) => _runs.TryGetValue(runId, out artifact!);

    /// <summary>预算只计 JPEG 字节；overlay 为小型矢量数据，忽略不计。</summary>
    private static long EstimateBytes(RunArtifact artifact)
        => (artifact.PreviewJpeg?.Length ?? 0) + artifact.NodeImages.Sum(x => (long)x.Jpeg.Length);
}
