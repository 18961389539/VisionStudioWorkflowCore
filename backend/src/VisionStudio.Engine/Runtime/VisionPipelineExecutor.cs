using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Lightweight in-process executor for contiguous hot-path compute nodes. Workflow Core
/// schedules one pipeline segment while this class executes its member nodes, preserving
/// node-level reports/debug semantics through VisionNodeRuntime. Nodes that belong to
/// different branches of the same parallel region run concurrently (branch order kept).
///
/// Branch contract: each branch owns a linked cancellation source. When one branch fails, its
/// siblings are cancelled before they can start another node, so the Join is a real barrier
/// instead of a wait for work that will never be used. A branch that stops because a sibling
/// failed is reported as cancelled, not failed: the root cause stays attributed to the branch
/// that actually threw.
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

        var branches = ParallelBranchGroups(data, nodeIds);
        if (branches is null)
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

        await ExecuteBranchesAsync(data, branches, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteBranchesAsync(
        VisionWorkflowData data,
        IReadOnlyList<BranchWork> branches,
        CancellationToken cancellationToken)
    {
        var regionId = branches[0].RegionId;
        var timeoutMs = data.ParallelBranchTimeoutMs(regionId);
        var sources = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        var timers = new List<Timer>();
        Exception? firstFailure = null;
        var regionStarted = Stopwatch.GetTimestamp();

        try
        {
            foreach (var branch in branches)
            {
                var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sources[branch.BranchName] = source;
                // 节点运行时要能"立刻"停掉兄弟分支，而不必等到异常冒泡到本执行器。
                data.RegisterBranchCanceller(regionId, branch.BranchName, source.Cancel);
            }

            var tasks = branches.Select(branch => Task.Run(async () =>
            {
                var source = sources[branch.BranchName];
                var token = source.Token;
                data.MarkBranchRunning(regionId, branch.BranchName);
                var started = Stopwatch.GetTimestamp();
                Timer? timer = null;
                try
                {
                    if (timeoutMs > 0)
                    {
                        // 超时原因必须在取消前写入：节点侧要靠它区分"兄弟失败"与"本分支超时"。
                        timer = new Timer(_ =>
                        {
                            try
                            {
                                data.RequestBranchCancellation(regionId, branch.BranchName, ParallelCancellationReasons.Timeout);
                                source.Cancel();
                                // 超时的分支同样让区域无法产出有效结果：兄弟分支没有继续跑完的意义。
                                foreach (var sibling in branches)
                                {
                                    if (ReferenceEquals(sibling, branch)) continue;
                                    data.RequestBranchCancellation(regionId, sibling.BranchName, ParallelCancellationReasons.SiblingTimedOut);
                                    try { sources[sibling.BranchName].Cancel(); }
                                    catch (ObjectDisposedException) { /* 兄弟已结束 */ }
                                }
                            }
                            catch (ObjectDisposedException) { /* 运行已结束，竞态可忽略 */ }
                        }, null, timeoutMs, Timeout.Infinite);
                        lock (timers) timers.Add(timer);
                    }

                    foreach (var nodeId in branch.NodeIds)
                    {
                        token.ThrowIfCancellationRequested();
                        var outcome = await nodeRuntime.ExecuteAsync(data, nodeId, token).ConfigureAwait(false);
                        if (data.Halted || outcome == VisionNodeRuntimeOutcome.HaltedBefore) break;
                    }
                    data.CompleteBranch(regionId, branch.BranchName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 兄弟失败或本分支超时：记录为 cancelled，不抢占根因归因。
                    var reason = ResolveCancellationReason(data, regionId, branch, timeoutMs);
                    data.CancelBranch(regionId, branch.BranchName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, reason);
                }
                catch (Exception ex)
                {
                    data.FailBranch(regionId, branch.BranchName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    foreach (var sibling in branches)
                    {
                        if (ReferenceEquals(sibling, branch)) continue;
                        data.RequestBranchCancellation(regionId, sibling.BranchName, ParallelCancellationReasons.SiblingFailed);
                        try { sources[sibling.BranchName].Cancel(); }
                        catch (ObjectDisposedException) { /* 兄弟已结束 */ }
                    }
                }
                finally
                {
                    timer?.Dispose();
                    data.UnregisterBranchCanceller(regionId, branch.BranchName);
                }
            }, CancellationToken.None));

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            foreach (var timer in timers) timer.Dispose();
            foreach (var source in sources.Values) source.Dispose();
        }

        // 失败优先：异常链路必须原样上行（NodeTimeoutException / IntentionalWorkflowFailureException
        // 的类型决定运行错误码，不能被包装掉）。
        if (firstFailure is not null) ExceptionDispatchInfo.Capture(firstFailure).Throw();

        ThrowIfBranchTimedOut(data, regionId, timeoutMs, Stopwatch.GetElapsedTime(regionStarted));
    }

    private static string ResolveCancellationReason(VisionWorkflowData data, string regionId, BranchWork branch, int timeoutMs)
    {
        if (branch.NodeIds.Count > 0 && data.CancellationReasonForNode(branch.NodeIds[0]) is { } recorded) return recorded;
        return timeoutMs > 0 ? ParallelCancellationReasons.Timeout : ParallelCancellationReasons.RunCancelled;
    }

    /// <summary>
    /// 超时是调度层的失败，没有任何节点会抛异常；必须在这里补一条运行级故障，
    /// 否则工作流会把"分支被掐断"当成正常完成继续往下走。
    /// </summary>
    private static void ThrowIfBranchTimedOut(VisionWorkflowData data, string regionId, int timeoutMs, TimeSpan elapsed)
    {
        if (timeoutMs <= 0) return;
        var timedOut = data.ParallelDispositions.BranchExecutions(regionId)
            .Where(branch => branch.Status == ParallelBranchStatus.Cancelled &&
                             branch.CancellationReason == ParallelCancellationReasons.Timeout)
            .Select(branch => branch.Label)
            .ToArray();
        if (timedOut.Length == 0) return;

        // 命名沿用 NodeTimeoutException 的 "node type" 位：报告里直接能看出是哪个分支超时。
        var nodeType = $"{regionId}:branch[{string.Join('/', timedOut)}]";
        var failure = new NodeTimeoutException(nodeType, timeoutMs, elapsed);
        data.RecordFault(regionId, failure);
        ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>段内节点全部属于同一并行区域的 ≥2 个不同分支时返回分支分组；否则返回 null（保持顺序执行）。</summary>
    private static List<BranchWork>? ParallelBranchGroups(VisionWorkflowData data, IReadOnlyList<string> nodeIds)
    {
        var groups = new Dictionary<string, BranchWork>(StringComparer.OrdinalIgnoreCase);
        string? regionId = null;
        foreach (var nodeId in nodeIds)
        {
            if (!data.ParallelDispositions.TryGetBranchScope(nodeId, out var nodeRegionId, out var branchName))
                return null;
            // 一个流水线段只承载一个并行区域；跨区域融合会让取消语义无法界定。
            if (regionId is null) regionId = nodeRegionId;
            else if (!regionId.Equals(nodeRegionId, StringComparison.OrdinalIgnoreCase)) return null;

            var key = $"{nodeRegionId}::{branchName}";
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new BranchWork(nodeRegionId, branchName, []);
            group.NodeIds.Add(nodeId);
        }
        return groups.Count > 1 ? [.. groups.Values] : null;
    }

    private sealed record BranchWork(string RegionId, string BranchName, List<string> NodeIds);
}
