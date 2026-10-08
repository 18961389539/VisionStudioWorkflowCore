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
    private readonly ConcurrentDictionary<string, Entry> _runs = new();
    private readonly object _sync = new();
    /// <summary>
    /// 写入顺序的伪 LRU 链表（头部最旧），与缓存条目一一对应：覆盖同一 runId 时先摘除旧节点，
    /// 链表长度恒等于缓存条数——反复覆盖也不会堆积过期项（普通 Queue 只在驱逐时清理，会无限增长）。
    /// </summary>
    private readonly LinkedList<string> _order = new();
    private long _totalBytes;

    private sealed record Entry(RunArtifact Artifact, LinkedListNode<string> OrderNode);

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

        // 替换、记账、驱逐作为一个整体在同一临界区完成：并发 Put 不再互相覆盖字节账
        // （此前计数读取与更新分离），覆盖同一 runId 时旧链表节点同步摘除，
        // 也不会再出现"旧顺序项驱逐新版本"或队列项无限堆积的问题。
        lock (_sync)
        {
            if (_runs.TryGetValue(result.RunId, out var replaced))
            {
                _order.Remove(replaced.OrderNode);
                _totalBytes -= EstimateBytes(replaced.Artifact);
            }
            var node = _order.AddLast(result.RunId);
            _runs[result.RunId] = new Entry(artifact, node);
            _totalBytes += bytes;

            // 超过条数或字节预算都从最旧的开始驱逐；链表与字典在锁内保持一致，队首即最旧条目。
            // 若单条工件自身超预算，同样会被驱逐：宁可不缓存这一次，也不让内存被撑爆。
            while (_runs.Count > _maxRetainedRuns || _totalBytes > _maxTotalBytes)
            {
                if (_order.First is not { } oldest) break;
                _order.RemoveFirst();
                if (_runs.TryRemove(oldest.Value, out var removed))
                    _totalBytes -= EstimateBytes(removed.Artifact);
            }
        }
    }

    public bool TryGet(string runId, out RunArtifact artifact)
    {
        if (_runs.TryGetValue(runId, out var entry))
        {
            artifact = entry.Artifact;
            return true;
        }
        artifact = null!;
        return false;
    }

    /// <summary>预算只计 JPEG 字节；overlay 为小型矢量数据，忽略不计。</summary>
    private static long EstimateBytes(RunArtifact artifact)
        => (artifact.PreviewJpeg?.Length ?? 0) + artifact.NodeImages.Sum(x => (long)x.Jpeg.Length);
}
