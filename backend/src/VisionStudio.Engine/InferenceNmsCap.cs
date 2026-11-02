namespace VisionStudio.Engine;

/// <summary>
/// 带"截断证据"的 NMS 收敛器：最多保留 <c>maxCount</c> 个存活候选，并报告是否还有没发出的存活候选。
///
/// 推理节点的 <c>maxCount</c> 是 NMS 期间的上限，过去一旦凑满就 break，下游只看到 <c>count</c>，
/// 无法区分"图上就这么多目标"和"被上限截断"。这里在凑满后**只多看一个候选**：
/// 若下一个候选也存活，说明确实被截断（truncated=true）；若剩余候选全被抑制，则没有丢失任何结果。
/// 因此不需要为统计跑完整 NMS，最坏只多一次 IoU 计算。
/// </summary>
internal static class InferenceNmsCap
{
    /// <param name="orderedCandidates">已按分数降序排列的候选。</param>
    /// <param name="maxCount">最多保留的存活候选数（≥1）。</param>
    /// <param name="kept">输出列表，调用方负责创建；方法只追加。</param>
    /// <param name="isSuppressed">抑制判定：给定已保留集合与候选，返回 true 表示应丢弃。</param>
    internal static bool Fill<T>(
        IEnumerable<T> orderedCandidates,
        int maxCount,
        List<T> kept,
        Func<IReadOnlyList<T>, T, bool> isSuppressed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentNullException.ThrowIfNull(isSuppressed);
        if (maxCount < 1) throw new InvalidOperationException("Inference maxCount must be at least 1.");

        var truncated = false;
        foreach (var candidate in orderedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isSuppressed(kept, candidate)) continue;
            if (kept.Count >= maxCount)
            {
                // 已凑满上限又遇到一个存活候选：列表被截断。只需这一个证据，不必继续扫描。
                truncated = true;
                break;
            }
            kept.Add(candidate);
        }
        return truncated;
    }
}
