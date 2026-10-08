using VisionStudio.Engine;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// RunStore 驱逐策略：条数与总字节预算双上限，都从最旧的 run 开始驱逐。
/// </summary>
public sealed class RunStoreTests
{
    private static WorkflowRunResult Result(string runId, int nodeImageBytes, int nodeImageCount = 1)
        => new(runId, true, 1.0, false, 0, 0, [], [], null)
        {
            NodeImages = Enumerable.Range(0, nodeImageCount)
                .Select(_ => new RunNodeImage($"node-{Guid.NewGuid():N}", "image", new byte[nodeImageBytes], 1, 1))
                .ToList()
        };

    [Fact]
    public void Put_EvictsOldestRuns_WhenCountExceeded()
    {
        var store = new RunStore(maxRetainedRuns: 8, maxTotalBytes: 1024L * 1024 * 1024);
        for (var i = 0; i < 20; i++) store.Put(Result($"run-{i}", 1024));

        Assert.False(store.TryGet("run-0", out _));
        Assert.False(store.TryGet("run-11", out _));
        Assert.True(store.TryGet("run-19", out _));
    }

    [Fact]
    public void Put_EvictsOldestRuns_WhenByteBudgetExceeded_EvenUnderCountCap()
    {
        // 每条 1MB × 6 条 = 6MB > 4MB 预算：条数远未到 8，字节预算也要触发驱逐
        var store = new RunStore(maxRetainedRuns: 8, maxTotalBytes: 4L * 1024 * 1024);
        for (var i = 0; i < 6; i++) store.Put(Result($"big-{i}", 1024 * 1024));

        Assert.False(store.TryGet("big-0", out _));
        Assert.False(store.TryGet("big-1", out _));
        Assert.True(store.TryGet("big-5", out _));
    }

    [Fact]
    public void Put_ReplacingSameRunId_AccountsForOldArtifactBytes()
    {
        var store = new RunStore(maxRetainedRuns: 8, maxTotalBytes: 3L * 1024 * 1024);
        store.Put(Result("run-a", 1024 * 1024));
        // 同 runId 重放 1MB → 2MB：旧账应被扣掉，而不是累计成 2MB 导致提前驱逐
        store.Put(Result("run-a", 1024 * 1024));
        store.Put(Result("run-b", 1024 * 1024));

        Assert.True(store.TryGet("run-a", out _));
        Assert.True(store.TryGet("run-b", out _));
    }

    [Fact]
    public void Put_ReplacingSameRunId_DoesNotAccumulateOrderEntries()
    {
        var store = new RunStore(maxRetainedRuns: 8, maxTotalBytes: 1024L * 1024 * 1024);
        for (var i = 0; i < 50; i++) store.Put(Result("run-a", 1024));

        // 反复覆盖同一 runId：缓存 1 条，顺序队列必须同样只有 1 项（修复前只在驱逐时清队列，会堆积 50 项）
        Assert.Equal(1, OrderEntryCount(store));
        Assert.True(store.TryGet("run-a", out _));
    }

    [Fact]
    public void Put_ReplacingSameRunId_MovesEntryToMostRecent()
    {
        var store = new RunStore(maxRetainedRuns: 2, maxTotalBytes: 1024L * 1024 * 1024);
        store.Put(Result("run-a", 1024));
        store.Put(Result("run-b", 1024));
        store.Put(Result("run-a", 1024)); // 覆盖：run-a 应变为最新，而不是保留原来的最旧位置
        store.Put(Result("run-c", 1024)); // 超限：驱逐最旧的 run-b

        Assert.False(store.TryGet("run-b", out _));
        Assert.True(store.TryGet("run-a", out _));
        Assert.True(store.TryGet("run-c", out _));
        Assert.Equal(2, OrderEntryCount(store));
    }

    [Fact]
    public void Put_OversizedArtifact_EvictsItselfAndLeavesNoOrderEntry()
    {
        var store = new RunStore(maxRetainedRuns: 8, maxTotalBytes: 1024);
        store.Put(Result("huge", 1024 * 1024));

        // 单条工件自身超预算：整条被驱逐，顺序队列不得残留该条目
        Assert.False(store.TryGet("huge", out _));
        Assert.Equal(0, OrderEntryCount(store));
    }

    /// <summary>顺序队列长度（内部实现细节）：应恒等于缓存条数，覆盖/驱逐后不得有残留过期项。</summary>
    private static int OrderEntryCount(RunStore store)
    {
        var field = typeof(RunStore).GetField("_order", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return ((LinkedList<string>)field!.GetValue(store)!).Count;
    }
}
