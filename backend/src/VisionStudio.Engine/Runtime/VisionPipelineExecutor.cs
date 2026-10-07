namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Lightweight in-process executor for contiguous hot-path compute nodes. Workflow Core
/// schedules one pipeline segment while this class executes its member nodes, preserving
/// node-level reports/debug semantics through VisionNodeRuntime. Nodes that belong to
/// different branches of the same parallel region run concurrently (branch order kept).
/// </summary>
public sealed class VisionPipelineExecutor(VisionNodeRuntime nodeRuntime)
{
    public async ValueTask ExecuteAsync(
        VisionWorkflowData data,
        string segmentId,
        CancellationToken cancellationToken)
    {
        if (!data.PipelineSegments.TryGetValue(segmentId, out var nodeIds))
            throw new InvalidOperationException($"Pipeline segment '{segmentId}' was not found in the compiled execution plan.");

        var parallelGroups = ParallelBranchGroups(data, nodeIds);
        if (parallelGroups is null)
        {
            foreach (var nodeId in nodeIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await nodeRuntime.ExecuteAsync(data, nodeId, cancellationToken);
                if (data.Halted || outcome == VisionNodeRuntimeOutcome.HaltedBefore)
                    break;
            }
            return;
        }

        // 并行区域：不同分支并发推进，分支内保持原顺序；Join 步骤在该段完成之后才执行，天然形成屏障。
        await Task.WhenAll(parallelGroups.Select(group => Task.Run(async () =>
        {
            foreach (var nodeId in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await nodeRuntime.ExecuteAsync(data, nodeId, cancellationToken);
                if (data.Halted || outcome == VisionNodeRuntimeOutcome.HaltedBefore)
                    break;
            }
        }, cancellationToken)));
    }

    /// <summary>段内节点全部属于同一并行区域的 ≥2 个不同分支时返回分支分组；否则返回 null（保持顺序执行）。</summary>
    private static List<List<string>>? ParallelBranchGroups(VisionWorkflowData data, IReadOnlyList<string> nodeIds)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodeId in nodeIds)
        {
            if (!data.ParallelDispositions.TryGetBranchScope(nodeId, out var regionId, out var branchName))
                return null;
            var key = $"{regionId}::{branchName}";
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add(nodeId);
        }
        return groups.Count > 1 ? [.. groups.Values] : null;
    }
}
