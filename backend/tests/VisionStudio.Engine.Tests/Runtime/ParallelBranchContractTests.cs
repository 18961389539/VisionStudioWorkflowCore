using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Parallel branch lifecycle contract: a failing branch must stop its siblings before the failure
/// becomes observable, a Join must publish which branches completed/failed/were cancelled/never ran,
/// and branches must not read each other's values. Timeout and merge policies are covered here too.
/// </summary>
public sealed class ParallelBranchContractTests
{
    [Fact]
    public async Task FailingBranch_CancelsSiblingBeforeItsNextNode()
    {
        ProbeExecutor.Reset();
        BarrierExecutor.Reset();
        var workflow = new WorkflowDefinition(
            "par-sibling-cancel",
            "Parallel Sibling Cancel",
            [
                Node("p", "flow.parallel"),
                Node("r1", BarrierExecutor.NodeType),
                Node("f1", FailExecutor.NodeType),
                Node("r2", BarrierExecutor.NodeType),
                Node("s2", SlowExecutor.NodeType, Slow(2000)),
                Node("probe", ProbeExecutor.NodeType),
                Node("j", "flow.join")
            ],
            [
                Control("c1", "p", "branch1", "r1"),
                Control("c2", "p", "branch2", "r2"),
                Control("c3", "r1", "next", "f1"),
                Control("c4", "r2", "next", "s2"),
                Control("c5", "s2", "next", "probe"),
                Control("c6", "f1", "next", "j", "branch1"),
                Control("c7", "probe", "next", "j", "branch2")
            ]);

        var result = await RunAsync(workflow);

        Assert.False(result.Success);
        // 关键：兄弟分支没有"再干一步"。两条分支都先过 rendezvous，确保 b2 已在 2s 等待中，
        // b1 这时才失败；b2 的等待必须立刻被取消，probe 永不执行。
        Assert.Equal(0, ProbeExecutor.Invocations);
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "probe");
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "j");

        var sibling = Report(result, "s2");
        Assert.False(sibling.Success);
        Assert.Equal(true, sibling.Summary[NodeSummaryKeys.SchedulerCancelled]);
        Assert.Equal(ParallelCancellationReasons.SiblingFailed, sibling.Summary[NodeSummaryKeys.CancellationReason]);
        // 根因仍然归给真正抛异常的那条分支，不被"取消"计数覆盖。
        Assert.Equal("f1", result.NodeReports.Single(x => x.NodeId == "f1" && !x.Success).NodeId);
    }

    [Fact]
    public async Task BranchTimeout_CancelsSlowBranch_AndNamesItsLabel()
    {
        var workflow = new WorkflowDefinition(
            "par-timeout",
            "Parallel Timeout",
            [
                Node("p", "flow.parallel", Params(("branchLabels", "fast,slow"), ("branchTimeoutMs", 150))),
                Node("quick", ProbeExecutor.NodeType),
                Node("slow", SlowExecutor.NodeType, Slow(2000)),
                Node("j", "flow.join")
            ],
            [
                Control("c1", "p", "branch1", "quick"),
                Control("c2", "p", "branch2", "slow"),
                Control("c3", "quick", "next", "j", "branch1"),
                Control("c4", "slow", "next", "j", "branch2")
            ]);

        var result = await RunAsync(workflow);

        Assert.False(result.Success);
        // 超时是调度层判定：必须落成 node_timeout，而不是"节点被取消"的普通失败。
        Assert.Equal(VisionRunErrorCodes.NodeTimeout, result.ErrorCode);
        Assert.Contains("branch[slow]", result.Error, StringComparison.Ordinal);
        // 快分支不受牵连：它在超时前已经完成，报告里仍是成功。
        Assert.True(Report(result, "quick").Success);
        Assert.DoesNotContain(result.NodeReports, report => report.NodeId == "j");
    }

    [Fact]
    public async Task JoinPublishesBranchLedger_WithExecutionIds()
    {
        var result = await RunAsync(TwoResultWorkflow("par-ledger", pass1: true, pass2: false));
        Assert.True(result.Success, result.Error);

        var join = Report(result, "j");
        Assert.Equal(new[] { "branch1", "branch2" },
            (IReadOnlyList<string>)join.Summary[NodeSummaryKeys.BranchCompleted]!);
        Assert.Empty((IReadOnlyList<string>)join.Summary[NodeSummaryKeys.BranchFailed]!);
        Assert.Empty((IReadOnlyList<string>)join.Summary[NodeSummaryKeys.BranchCancelled]!);
        Assert.Empty((IReadOnlyList<string>)join.Summary[NodeSummaryKeys.BranchMissing]!);
        Assert.Equal("anyNg", join.Summary[NodeSummaryKeys.MergePolicy]);
        Assert.Equal(false, join.Summary[NodeSummaryKeys.MergeSatisfied]);
        Assert.Equal(false, join.Summary[NodeSummaryKeys.JoinDuplicate]);
        Assert.Equal("NG", join.Summary[NodeSummaryKeys.ParallelDisposition]);

        var branches = (IReadOnlyDictionary<string, string>)join.Summary[NodeSummaryKeys.BranchDispositions]!;
        Assert.Equal("OK", branches["branch1"]);
        Assert.Equal("NG", branches["branch2"]);
    }

    [Theory]
    [InlineData("anyNg", 0, "NG")]
    [InlineData("allPass", 0, "NG")]
    [InlineData("anyPass", 0, "OK")]
    [InlineData("quorum", 1, "OK")]
    [InlineData("quorum", 2, "NG")]
    public async Task MergePolicy_ChangesTheAggregate(string policy, int quorum, string expected)
    {
        var result = await RunAsync(TwoResultWorkflow($"par-policy-{policy}-{quorum}", pass1: true, pass2: false,
            mergePolicy: policy, quorum: quorum));

        Assert.True(result.Success, result.Error);
        var join = Report(result, "j");
        Assert.Equal(expected, join.Summary[NodeSummaryKeys.ParallelDisposition]);
        Assert.Equal(expected == "OK", join.Summary[NodeSummaryKeys.MergeSatisfied]);
        Assert.Equal(policy, join.Summary[NodeSummaryKeys.MergePolicy]);
        Assert.Equal(expected, result.QualityDisposition);
    }

    [Fact]
    public void CrossBranchDataEdge_IsRejectedAtCompileTime()
    {
        var workflow = new WorkflowDefinition(
            "par-cross-branch",
            "Parallel Cross Branch",
            [
                Node("p", "flow.parallel"),
                Node("g1", "test.gate", Gate(true)),
                Node("g2", "test.gate", Gate(true)),
                Node("j", "flow.join")
            ],
            [
                Control("c1", "p", "branch1", "g1"),
                Control("c2", "p", "branch2", "g2"),
                Control("c3", "g1", "next", "j", "branch1"),
                Control("c4", "g2", "next", "j", "branch2"),
                // branch1 的 pass 直接喂给 branch2：并发执行下这是未定义顺序的读取。
                Data("d1", "g1", "pass", "g2", "flag")
            ]);

        var provider = BuildProvider();
        var compiler = provider.GetRequiredService<VisionWorkflowCompiler>();
        var error = Assert.Throws<InvalidOperationException>(() => compiler.Compile(workflow));
        Assert.Contains("never share a data context", error.Message, StringComparison.Ordinal);
        Assert.Contains("g1.pass", error.Message, StringComparison.Ordinal);
        Assert.Contains("g2.flag", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedJoinToken_ReturnsCachedAggregation()
    {
        using var data = new VisionWorkflowData();
        data.ConfigureParallelDispositions(
        [
            new StructuredControlRegion("p", "Parallel", "j", 0,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["branch1"] = ["r1"],
                    ["branch2"] = ["r2"]
                })
        ]);

        data.MarkParallelBranches("p");
        data.RecordDisposition("r1", "OK");
        data.RecordDisposition("r2", "OK");

        var first = data.ApplyParallelJoin("j");
        Assert.NotNull(first);
        Assert.False(first.Duplicate);
        Assert.Equal("OK", first.Aggregate);
        Assert.Equal("OK", data.QualityDisposition);

        var replay = data.ApplyParallelJoin("j");
        Assert.NotNull(replay);
        Assert.True(replay.Duplicate);
        Assert.Equal(first.Aggregate, replay.Aggregate);
    }

    [Fact]
    public void BranchRun_ResetsStateBetweenIterations()
    {
        using var data = new VisionWorkflowData();
        data.ConfigureParallelDispositions(
        [
            new StructuredControlRegion("p", "Parallel", "j", 0,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["branch1"] = ["r1"],
                    ["branch2"] = ["r2"]
                })
        ]);

        data.MarkParallelBranches("p");
        data.MarkBranchRunning("p", "branch1");
        data.FailBranch("p", "branch1", 12, "boom");
        Assert.Equal(ParallelBranchStatus.Failed, data.ParallelDispositions.GetBranchExecution("p", "branch1")!.Status);

        // 第二轮重新开始：上一轮的失败不得延续，否则第二次会被误判为失败。
        data.MarkParallelBranches("p");
        var reset = data.ParallelDispositions.GetBranchExecution("p", "branch1")!;
        Assert.Equal(ParallelBranchStatus.Pending, reset.Status);
        Assert.Null(reset.Error);
    }

    private static WorkflowDefinition TwoResultWorkflow(string id, bool pass1, bool pass2,
        string? mergePolicy = null, int quorum = 0)
        => new(
            id,
            "Parallel Ledger",
            [
                Node("p", "flow.parallel"),
                Node("g1", "test.gate", Gate(pass1)),
                Node("g2", "test.gate", Gate(pass2)),
                Node("r1", "flow.result"),
                Node("r2", "flow.result"),
                Node("j", "flow.join", JoinParams(mergePolicy, quorum))
            ],
            [
                Control("c1", "p", "branch1", "g1"),
                Control("c2", "p", "branch2", "g2"),
                Control("c3", "g1", "next", "r1"),
                Control("c4", "g2", "next", "r2"),
                Data("d1", "g1", "pass", "r1", "pass"),
                Data("d2", "g2", "pass", "r2", "pass"),
                Control("c5", "r1", "next", "j", "branch1"),
                Control("c6", "r2", "next", "j", "branch2")
            ]);

    private static async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow)
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        return await runner.RunAsync(workflow);
    }

    private static NodeRunReport Report(WorkflowRunResult result, string nodeId)
        => Assert.Single(result.NodeReports, x => x.NodeId == nodeId);

    private static NodeDefinition Node(string id, string type, Dictionary<string, JsonElement>? parameters = null)
        => new(id, type, id, null, parameters);

    private static EdgeDefinition Control(string id, string source, string sourcePort, string target, string targetPort = "exec")
        => new(id, source, sourcePort, target, targetPort, "control");

    private static EdgeDefinition Data(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort);

    private static Dictionary<string, JsonElement> Params(params (string Key, object Value)[] values)
        => values.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));

    private static Dictionary<string, JsonElement>? JoinParams(string? mergePolicy, int quorum)
    {
        var values = new List<(string, object)>();
        if (mergePolicy is not null) values.Add(("mergePolicy", mergePolicy));
        if (quorum != 0) values.Add(("quorum", quorum));
        return values.Count == 0 ? null : Params([.. values]);
    }

    private static Dictionary<string, JsonElement> Gate(bool pass) => Params(("pass", pass));

    private static Dictionary<string, JsonElement> Slow(int delayMs) => Params(("delayMs", delayMs));

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();

        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("flow.parallel"), new ParallelMarkerNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.join"), new JoinNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.result"), new ResultDispositionNode(), "builtin");
        registry.Register(GateExecutor.Catalog, new GateExecutor(), "test");
        registry.Register(SlowExecutor.Catalog, new SlowExecutor(), "test");
        registry.Register(FailExecutor.Catalog, new FailExecutor(), "test");
        registry.Register(ProbeExecutor.Catalog, new ProbeExecutor(), "test");
        registry.Register(BarrierExecutor.Catalog, new BarrierExecutor(), "test");
        services.AddSingleton(registry);
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddSingleton<WorkflowPlanCache>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        return services.BuildServiceProvider();
    }

    private sealed class GateExecutor : IVisionNodeExecutor
    {
        public string Type => "test.gate";
        public static NodeCatalogItem Catalog { get; } = new(
            "test.gate", "Gate", "Test",
            [new PortDescriptor("exec", VisionDataType.Control), new PortDescriptor("flag", VisionDataType.Boolean, Required: false)],
            [new PortDescriptor("pass", VisionDataType.Boolean), new PortDescriptor("next", VisionDataType.Control)],
            [new ParameterDescriptor("pass", "Pass", "boolean", true)]);

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var pass = node.GetBool("pass", true);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(pass) },
                new Dictionary<string, object?> { ["pass"] = pass }));
        }
    }

    private sealed class SlowExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.slow";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Slow", "Test",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("next", VisionDataType.Control)],
            [new ParameterDescriptor("delayMs", "Delay (ms)", "number", 100)],
            PluginId: "test",
            Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.Pipeline));

        public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            await Task.Delay(node.GetInt("delayMs", 100), cancellationToken);
            return new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>());
        }
    }

    private sealed class FailExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.fail";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Fail", "Test",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("next", VisionDataType.Control)],
            [], PluginId: "test",
            Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.Pipeline));

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new InvalidOperationException("branch1 died");
    }

    /// <summary>让两条分支都进入各自的第一个节点后再放行，消除"谁先跑到"的调度抖动。</summary>
    private sealed class BarrierExecutor : IVisionNodeExecutor
    {
        private static TaskCompletionSource _both = NewGate();
        private static int _entered;
        public const string NodeType = "test.barrier";
        public string Type => NodeType;

        public static void Reset()
        {
            Volatile.Write(ref _entered, 0);
            _both = NewGate();
        }

        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Barrier", "Test",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("next", VisionDataType.Control)],
            [], PluginId: "test",
            Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.Pipeline));

        public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var gate = _both;
            if (Interlocked.Increment(ref _entered) >= 2) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>());
        }
    }

    /// <summary>记录被真正执行过多少次：用来证明被取消的分支没有再"写设备"。</summary>
    private sealed class ProbeExecutor : IVisionNodeExecutor
    {
        private static int _invocations;
        public const string NodeType = "test.probe";
        public string Type => NodeType;
        public static int Invocations => Volatile.Read(ref _invocations);
        public static void Reset() => Volatile.Write(ref _invocations, 0);

        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Probe", "Test",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("next", VisionDataType.Control)],
            [], PluginId: "test",
            Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.Pipeline));

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
        }
    }
}
