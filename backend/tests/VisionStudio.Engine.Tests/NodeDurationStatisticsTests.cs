using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 每节点耗时统计（2026-10-29）。这组数字唯一的用途是回答"<c>__timeoutMs</c> 该填多少"，
/// 所以断言集中在三件事上：分位数口径、样本不足时不瞎给建议、建议值不越过当前上限。
/// </summary>
public sealed class NodeDurationStatisticsTests
{
    [Fact]
    public void Snapshot_UsesNearestRankPercentiles()
    {
        var statistics = new NodeDurationStatistics();
        for (var i = 1; i <= 100; i++) statistics.Record("camera.grab", i);

        var statistic = Assert.Single(statistics.Snapshot());
        Assert.Equal(100, statistic.Samples);
        Assert.Equal(50d, statistic.P50Ms);
        Assert.Equal(95d, statistic.P95Ms);
        Assert.Equal(100d, statistic.MaxMs);
        // 建议值必须真的能容下 P95，否则"按建议填"会立刻误杀。
        Assert.NotNull(statistic.SuggestedTimeoutMs);
        Assert.True(statistic.SuggestedTimeoutMs >= statistic.P95Ms);
    }

    [Fact]
    public void Suggestion_IsWithheldUntilThereAreEnoughSamples()
    {
        var statistics = new NodeDurationStatistics();
        for (var i = 0; i < NodeDurationStatistics.MinSamplesForSuggestion - 1; i++) statistics.Record("robot.move", 100);

        // 样本太少时"95 分位"就是最大值，据此推建议等于把一次偶发尖峰固化成配置。
        Assert.Null(Assert.Single(statistics.Snapshot()).SuggestedTimeoutMs);

        statistics.Record("robot.move", 100);
        Assert.NotNull(Assert.Single(statistics.Snapshot()).SuggestedTimeoutMs);
    }

    [Fact]
    public void Record_KeepsOnlyTheLatestWindowOfSamples()
    {
        var statistics = new NodeDurationStatistics(capacity: 4);
        foreach (var elapsed in new double[] { 10, 20, 30, 40, 50, 60 })
            statistics.Record("io.read", elapsed);

        var statistic = Assert.Single(statistics.Snapshot());
        Assert.Equal(4, statistic.Samples);
        // 环形覆盖：留下最近的 {30,40,50,60}，最早的 10/20 已被挤掉。
        Assert.Equal(60d, statistic.MaxMs);
        Assert.Equal(40d, statistic.P50Ms);
    }

    [Fact]
    public void Snapshot_FlagsConfigurationThatHasAlreadyBeenHit()
    {
        var statistics = new NodeDurationStatistics();
        for (var i = 0; i < 20; i++) statistics.Record("plc.write", 190, configuredTimeoutMs: 200);
        statistics.Record("plc.write", 200, configuredTimeoutMs: 200, timedOut: true);

        var statistic = Assert.Single(statistics.Snapshot());
        // P95 = 190ms 而配置只有 200ms —— 已经吃掉 95%，下一次抖动就误杀。
        Assert.True(statistic.TimeoutTooTight);
        Assert.True(statistic.HasTimedOut);
        Assert.Equal(1, statistic.TimeoutCount);
        Assert.Equal(200, statistic.ConfiguredTimeoutMs);
    }

    [Fact]
    public void Snapshot_OrdersByP95DescendingSoTheSlowestNodesComeFirst()
    {
        var statistics = new NodeDurationStatistics();
        statistics.Record("fast", 5);
        statistics.Record("slow", 900);
        statistics.Record("medium", 120);

        Assert.Equal(["slow", "medium", "fast"], statistics.Snapshot().Select(x => x.NodeType).ToArray());
    }

    [Fact]
    public void Record_IgnoresBlankTypesAndNonFiniteDurations()
    {
        var statistics = new NodeDurationStatistics();
        statistics.Record("  ", 10);
        statistics.Record("node", double.NaN);
        statistics.Record("node", -5);

        // NaN/负数归零而不是丢弃：样本数仍要增长，否则"一直失败"的节点会永远凑不到建议门槛。
        var statistic = Assert.Single(statistics.Snapshot());
        Assert.Equal(2, statistic.Samples);
        Assert.Equal(0d, statistic.MaxMs);
    }

    [Fact]
    public void Suggestion_NeverExceedsTheConfiguredCeiling()
    {
        try
        {
            // 一个需要 5 分钟的节点：默认上限 600s 内可以给出正常建议。
            var statistics = new NodeDurationStatistics();
            for (var i = 0; i < 20; i++) statistics.Record("bake.cure", 300_000);
            Assert.True(Assert.Single(statistics.Snapshot()).SuggestedTimeoutMs <= NodeTimeoutPolicy.CeilingMs);

            // 上限被调低后，建议值必须跟着钳住：否则"按建议填"会被 Normalize 悄悄改小，
            // 表里写着 600s、实际跑 60s，比不给建议更糟。
            NodeTimeoutPolicy.TryConfigureCeiling(60_000, out _);
            Assert.True(Assert.Single(statistics.Snapshot()).SuggestedTimeoutMs <= 60_000);
        }
        finally
        {
            NodeTimeoutPolicy.ResetCeiling();
        }
    }

    [Fact]
    public void Clear_DropsEveryBucket()
    {
        var statistics = new NodeDurationStatistics();
        statistics.Record("node", 1);
        statistics.Clear();
        Assert.Empty(statistics.Snapshot());
    }

    [Fact]
    public async Task Dispatcher_RecordsEveryExecution()
    {
        var durations = new NodeDurationStatistics();
        var executor = new FixedExecutor();
        var dispatcher = new VisionNodeDispatcher(Registry(executor), durations);

        for (var i = 0; i < 3; i++)
            await dispatcher.ExecuteAsync(Node(executor.Type), new Dictionary<string, VisionValue>(), "wf-a", CancellationToken.None);

        var statistic = Assert.Single(durations.Snapshot());
        Assert.Equal(executor.Type, statistic.NodeType);
        Assert.Equal(3, statistic.Samples);
        Assert.False(statistic.HasTimedOut);
        Assert.Null(statistic.ConfiguredTimeoutMs);
    }

    [Fact]
    public async Task Dispatcher_RecordsTimeoutsEvenThoughTheExecutionThrows()
    {
        // 超时路径最容易漏统计：异常直接冒泡，写在 try 末尾的 Record 永远执行不到。
        // 这里锁住"finally 里记录"这个实现选择——否则最需要被看见的样本恰好全部丢失。
        var durations = new NodeDurationStatistics();
        var executor = new HangingExecutor();
        var dispatcher = new VisionNodeDispatcher(Registry(executor), durations);

        await Assert.ThrowsAsync<NodeTimeoutException>(async () =>
            await dispatcher.ExecuteAsync(Node(executor.Type, timeoutMs: 50),
                new Dictionary<string, VisionValue>(), "wf-a", CancellationToken.None));

        var statistic = Assert.Single(durations.Snapshot());
        Assert.True(statistic.HasTimedOut);
        Assert.Equal(1, statistic.TimeoutCount);
        Assert.Equal(50, statistic.ConfiguredTimeoutMs);
    }

    private static VisionNodeRegistry Registry(IVisionNodeExecutor executor)
    {
        var registry = new VisionNodeRegistry();
        registry.Register(
            new NodeCatalogItem(
                executor.Type, executor.Type, "Test", [], [], [],
                PluginId: "test",
                Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore)),
            executor,
            "test");
        return registry;
    }

    private static NodeDefinition Node(string type, int? timeoutMs = null)
    {
        Dictionary<string, System.Text.Json.JsonElement>? parameters = timeoutMs is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>
            {
                [NodeTimeoutPolicy.ParameterName] = System.Text.Json.JsonSerializer.SerializeToElement(timeoutMs.Value)
            };
        return new NodeDefinition("n1", type, null, null, parameters);
    }

    private sealed class FixedExecutor : IVisionNodeExecutor
    {
        public string Type => "test.durations.fixed";

        public ValueTask<NodeExecutorResult> ExecuteAsync(
            NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }

    private sealed class HangingExecutor : IVisionNodeExecutor
    {
        public string Type => "test.durations.hanging";

        public async ValueTask<NodeExecutorResult> ExecuteAsync(
            NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
