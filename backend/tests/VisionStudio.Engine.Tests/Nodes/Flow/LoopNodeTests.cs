using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 通用循环（flow.while / flow.repeat / flow.break）行为测试。
/// 覆盖：固定次数、条件循环、迭代预算兜底、提前退出、端口暴露的 index，以及编译期拒绝的非法接法。
/// </summary>
public sealed class LoopNodeTests
{
    [Fact]
    public async Task RepeatRunsBodyExactCountAndPublishesIndex()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(RepeatWorkflow(count: 3));
        Assert.True(result.Success, result.Error);

        var body = result.NodeReports.Where(r => r.NodeId == "body").ToArray();
        Assert.Equal(3, body.Length);
        Assert.Equal(new[] { 0d, 1d, 2d }, body.Select(r => Convert.ToDouble(r.Summary["index"])));
        Assert.Contains(result.NodeReports, r => r.NodeId == "after");
        Assert.Equal(3, result.NodeReports.Count(r => r.NodeId == "loopEnd"));
    }

    [Fact]
    public async Task RepeatCountCanComeFromInputPort()
    {
        await using var provider = Provider();
        // 参数写 99，count 端口接常量 2，端口值应覆盖参数。
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(RepeatWorkflow(count: 99, countFromPort: true));
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.NodeReports.Count(r => r.NodeId == "body"));
    }

    [Fact]
    public async Task WhileLoopRepeatsUntilConditionTurnsFalse()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WhileWorkflow(stopAfter: 2, maxIterations: 10));
        Assert.True(result.Success, result.Error);
        // do-while：第 1、2 轮条件为真继续，第 3 轮判定为假退出，因此循环体执行 3 次。
        Assert.Equal(3, result.NodeReports.Count(r => r.NodeId == "body"));
        Assert.Contains(result.NodeReports, r => r.NodeId == "after");
    }

    [Fact]
    public async Task WhileLoopIsStoppedByIterationBudget()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WhileWorkflow(stopAfter: 999, maxIterations: 4));
        Assert.True(result.Success, result.Error);
        // 条件恒真时必须由预算兜底退出，而不是挂死。
        Assert.Equal(4, result.NodeReports.Count(r => r.NodeId == "body"));
        Assert.Contains(result.NodeReports, r => r.NodeId == "after");
    }

    [Theory]
    [InlineData(2, 10, "conditionFalse", 3)]
    [InlineData(999, 4, "iterationLimit", 4)]
    [InlineData(0, 1, "conditionFalse", 1)]
    public async Task WhilePublishesExitReasonAndCompletedIterations(int stopAfter, int maxIterations, string reason, int iterations)
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(
            WithExitReader(WhileWorkflow(stopAfter, maxIterations)));
        Assert.True(result.Success, result.Error);
        var after = Assert.Single(result.NodeReports, r => r.NodeId == "after");
        Assert.Equal(reason, after.Summary["reason"]);
        Assert.Equal((long)iterations, after.Summary["iterations"]);
    }

    [Theory]
    [InlineData("flow.while")]
    [InlineData("flow.repeat")]
    public async Task BreakPublishesExitReason(string loopType)
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WithExitReader(BreakWorkflow(loopType)));
        Assert.True(result.Success, result.Error);
        var after = Assert.Single(result.NodeReports, r => r.NodeId == "after");
        Assert.Equal("break", after.Summary["reason"]);
        Assert.Equal(1L, after.Summary["iterations"]);
    }

    [Fact]
    public async Task RepeatPublishesCompletedExitReason()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WithExitReader(RepeatWorkflow(3)));
        Assert.True(result.Success, result.Error);
        var after = Assert.Single(result.NodeReports, r => r.NodeId == "after");
        Assert.Equal("completed", after.Summary["reason"]);
        Assert.Equal(3L, after.Summary["iterations"]);
    }

    [Fact]
    public async Task WhileWithFreshConditionAcceptsSourceExecutedEveryIteration()
    {
        await using var provider = Provider();
        var workflow = WithFreshCondition(WhileWorkflow(stopAfter: 2, maxIterations: 10));
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WithExitReader(workflow));
        Assert.True(result.Success, result.Error);
        Assert.Equal("conditionFalse", Assert.Single(result.NodeReports, r => r.NodeId == "after").Summary["reason"]);
    }

    [Fact]
    public async Task NestedWhileResetsExitAndFreshnessForEachOuterIteration()
    {
        await using var provider = Provider();
        var workflow = new WorkflowDefinition("nested-loops", "Nested Loops",
            [Node("start", "test.mark"), Node("loop", "flow.repeat", "{\"count\":2}"),
             Node("inner", "flow.while", "{\"maxIterations\":3,\"requireFreshCondition\":true}"),
             Node("source", "test.counter", "{\"stopAfter\":0}"), Node("innerEnd", "flow.loopEnd"),
             Node("innerAfter", "test.loopExit"), Node("outerEnd", "flow.loopEnd"), Node("after", "test.mark")],
            [C("s-o", "start", "next", "loop", "exec"), C("o-i", "loop", "body", "inner", "exec"),
             C("i-s", "inner", "body", "source", "exec"), D("cond", "source", "pass", "inner", "condition"),
             C("s-ie", "source", "next", "innerEnd", "exec"), C("ie-i", "innerEnd", "repeat", "inner", "repeat"),
             C("i-a", "inner", "done", "innerAfter", "exec"),
             D("inner-reason", "inner", "exitReason", "innerAfter", "reason"),
             D("inner-iterations", "inner", "iterations", "innerAfter", "iterations"),
             C("ia-oe", "innerAfter", "next", "outerEnd", "exec"), C("oe-o", "outerEnd", "repeat", "loop", "repeat"),
             C("o-a", "loop", "done", "after", "exec")]);

        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WithExitReader(workflow));

        Assert.True(result.Success, result.Error);
        var innerExits = result.NodeReports.Where(report => report.NodeId == "innerAfter").ToArray();
        Assert.Equal(2, innerExits.Length);
        Assert.All(innerExits, report =>
        {
            Assert.Equal("conditionFalse", report.Summary["reason"]);
            Assert.Equal(1L, report.Summary["iterations"]);
        });
        var outerExit = Assert.Single(result.NodeReports, report => report.NodeId == "after");
        Assert.Equal("completed", outerExit.Summary["reason"]);
        Assert.Equal(2L, outerExit.Summary["iterations"]);
    }

    [Fact]
    public async Task FreshConditionAcceptsSourceInParallelBranchAfterJoin()
    {
        await using var provider = Provider();
        var workflow = new WorkflowDefinition("parallel-condition", "Parallel Condition",
            [Node("start", "test.mark"), Node("loop", "flow.while", "{\"maxIterations\":5,\"requireFreshCondition\":true}"),
             Node("parallel", "flow.parallel"), Node("source", "test.counter", "{\"stopAfter\":2}"),
             Node("other", "test.mark"), Node("join", "flow.join"), Node("loopEnd", "flow.loopEnd"),
             Node("after", "test.mark")],
            [C("s-l", "start", "next", "loop", "exec"), C("l-p", "loop", "body", "parallel", "exec"),
             C("p-s", "parallel", "branch1", "source", "exec"), C("p-o", "parallel", "branch2", "other", "exec"),
             C("s-j", "source", "next", "join", "branch1"), C("o-j", "other", "next", "join", "branch2"),
             D("cond", "source", "pass", "loop", "condition"), C("j-e", "join", "next", "loopEnd", "exec"),
             C("e-l", "loopEnd", "repeat", "loop", "repeat"), C("l-a", "loop", "done", "after", "exec")]);

        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WithExitReader(workflow));

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.NodeReports.Count(report => report.NodeId == "source"));
        var after = Assert.Single(result.NodeReports, report => report.NodeId == "after");
        Assert.Equal("conditionFalse", after.Summary["reason"]);
        Assert.Equal(3L, after.Summary["iterations"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WhileFreshConditionRejectsSkippedSourceWithoutChangingLegacyBehavior(bool requireFresh, bool success)
    {
        await using var provider = Provider();
        var workflow = SkippedConditionWorkflow();
        if (requireFresh) workflow = WithFreshCondition(workflow);
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(workflow);
        Assert.Equal(success, result.Success);
        if (!success)
        {
            Assert.Contains("did not execute in this iteration", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(VisionRunErrorCodes.NodeFailure, result.ErrorCode);
            Assert.Contains(result.NodeReports, report => report.NodeId == "loop" && !report.Success);
        }
        else Assert.Equal(2, result.NodeReports.Count(r => r.NodeId == "gate"));
    }

    [Fact]
    public async Task FreshConditionRejectsSourceOutsideLoopOnFirstIteration()
    {
        await using var provider = Provider();
        var workflow = WhileWorkflow(stopAfter: 999, maxIterations: 2);
        var nodes = workflow.Nodes.Select(node => node.Id switch
        {
            "start" => Node("start", "test.counter", "{\"stopAfter\":999}"),
            "body" => Node("body", "test.mark"),
            _ => node
        }).ToArray();
        var edges = workflow.Edges.Select(edge => edge.Id == "cond" ? edge with { SourceNodeId = "start" } : edge).ToArray();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(
            WithFreshCondition(workflow with { Nodes = nodes, Edges = edges }));
        Assert.False(result.Success);
        Assert.Contains("did not execute in this iteration", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.NodeReports.Count(report => report.NodeId == "body"));
    }

    [Fact]
    public async Task FreshConditionDoesNotBlockBreakBeforeConditionSource()
    {
        await using var provider = Provider();
        var workflow = BreakWorkflow("flow.while");
        var edges = workflow.Edges.Select(edge => edge.Id == "cond" ? edge with { SourceNodeId = "condition" } : edge).ToArray();
        var nodes = workflow.Nodes.Select(node => node.Id == "afterBreak" ? Node("condition", "test.counter") : node).ToArray();
        edges = edges.Select(edge => edge.SourceNodeId == "afterBreak" ? edge with { SourceNodeId = "condition" }
            : edge.TargetNodeId == "afterBreak" ? edge with { TargetNodeId = "condition" } : edge).ToArray();
        workflow = WithFreshCondition(workflow with { Nodes = nodes, Edges = edges });
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(WithExitReader(workflow));
        Assert.True(result.Success, result.Error);
        Assert.Equal("break", Assert.Single(result.NodeReports, r => r.NodeId == "after").Summary["reason"]);
    }

    [Fact]
    public async Task BreakSkipsRemainingBodyAndExitsLoop()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(BreakWorkflow("flow.while"));
        Assert.True(result.Success, result.Error);

        Assert.Equal(1, result.NodeReports.Count(r => r.NodeId == "body"));
        Assert.Equal(1, result.NodeReports.Count(r => r.NodeId == "break"));
        // break 之后的本轮步骤被守卫跳过，循环也不再进入下一轮。
        Assert.DoesNotContain(result.NodeReports, r => r.NodeId == "afterBreak");
        Assert.Contains(result.NodeReports, r => r.NodeId == "after");
    }

    [Fact]
    public async Task BreakWorksInsideRepeatLoop()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(BreakWorkflow("flow.repeat"));
        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.NodeReports.Count(r => r.NodeId == "body"));
        Assert.DoesNotContain(result.NodeReports, r => r.NodeId == "afterBreak");
        Assert.Contains(result.NodeReports, r => r.NodeId == "after");
    }

    [Fact]
    public void WhileRequiresConnectedCondition()
    {
        var workflow = WhileWorkflow(2, 10) with
        {
            Edges = WhileWorkflow(2, 10).Edges.Where(e => e.Id != "cond").ToArray()
        };
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(Registry()).Compile(workflow));
        Assert.Contains("requires its 'condition' input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BreakOutsideLoopIsRejected()
    {
        var workflow = new WorkflowDefinition("break-orphan", "Break Orphan",
            [Node("start", "test.mark"), Node("break", "flow.break"), Node("after", "test.mark")],
            [C("s-b", "start", "next", "break", "exec"), C("b-a", "break", "next", "after", "exec")]);
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(Registry()).Compile(workflow));
        Assert.Contains("must be placed inside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BreakInsideForEachIsRejected()
    {
        // forEach 没有提前退出语义：break 会让"本轮剩余"被跳过，却仍继续下一轮，容易误读，直接拒绝。
        var workflow = new WorkflowDefinition("break-foreach", "Break In ForEach",
            [Node("source", "test.detections"), Node("loop", "flow.forEach"), Node("break", "flow.break"), Node("end", "flow.loopEnd"), Node("after", "test.mark")],
            [C("s-l", "source", "next", "loop", "exec"), D("d", "source", "detections", "loop", "detections"),
             C("l-b", "loop", "body", "break", "exec"), C("b-e", "break", "next", "end", "exec"),
             C("e-l", "end", "repeat", "loop", "repeat"), C("l-a", "loop", "done", "after", "exec")]);
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(Registry()).Compile(workflow));
        Assert.Contains("forEach has no break", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ===== 测试工作流 =====

    private static WorkflowDefinition RepeatWorkflow(int count, bool countFromPort = false)
    {
        var edges = new List<EdgeDefinition>
        {
            C("s-l", "start", "next", "loop", "exec"),
            C("l-b", "loop", "body", "body", "exec"),
            D("idx", "loop", "index", "body", "index"),
            C("b-e", "body", "next", "loopEnd", "exec"),
            C("e-l", "loopEnd", "repeat", "loop", "repeat"),
            C("l-a", "loop", "done", "after", "exec")
        };
        if (countFromPort) edges.Add(D("count", "start", "value", "loop", "count"));
        return new WorkflowDefinition("repeat", "Repeat",
            [Node("start", "test.mark"), Node("loop", "flow.repeat", $"{{\"count\":{count}}}"),
             Node("body", "test.echoIndex"), Node("loopEnd", "flow.loopEnd"), Node("after", "test.mark")],
            edges.ToArray());
    }

    private static WorkflowDefinition WhileWorkflow(int stopAfter, int maxIterations) => new("while", "While",
        [Node("start", "test.mark"), Node("loop", "flow.while", $"{{\"maxIterations\":{maxIterations}}}"),
         Node("body", "test.counter", $"{{\"stopAfter\":{stopAfter}}}"), Node("loopEnd", "flow.loopEnd"), Node("after", "test.mark")],
        [C("s-l", "start", "next", "loop", "exec"),
         C("l-b", "loop", "body", "body", "exec"),
         D("cond", "body", "pass", "loop", "condition"),
         C("b-e", "body", "next", "loopEnd", "exec"),
         C("e-l", "loopEnd", "repeat", "loop", "repeat"),
         C("l-a", "loop", "done", "after", "exec")]);

    private static WorkflowDefinition BreakWorkflow(string loopType)
    {
        var loopParameters = loopType == "flow.repeat" ? Params("{\"count\":5}") : Params("{\"maxIterations\":10}");
        var edges = new List<EdgeDefinition>
        {
            C("s-l", "start", "next", "loop", "exec"),
            C("l-b", "loop", "body", "body", "exec"),
            C("b-brk", "body", "next", "break", "exec"),
            C("brk-a", "break", "next", "afterBreak", "exec"),
            C("a-e", "afterBreak", "next", "loopEnd", "exec"),
            C("e-l", "loopEnd", "repeat", "loop", "repeat"),
            C("l-a", "loop", "done", "after", "exec")
        };
        if (loopType == "flow.while") edges.Add(D("cond", "body", "pass", "loop", "condition"));
        return new WorkflowDefinition("break", "Break",
            [Node("start", "test.mark"), new("loop", loopType, "loop", null, loopParameters),
             Node("body", "test.counter", "{\"stopAfter\":999}"), Node("break", "flow.break", "{\"reason\":\"test\"}"),
             Node("afterBreak", "test.mark"), Node("loopEnd", "flow.loopEnd"), Node("after", "test.mark")],
            edges.ToArray());
    }

    private static WorkflowDefinition WithFreshCondition(WorkflowDefinition workflow) => workflow with
    {
        Nodes = workflow.Nodes.Select(node => node.Id == "loop"
            ? node with { Parameters = new Dictionary<string, JsonElement>(node.Parameters ?? [])
                { ["requireFreshCondition"] = JsonSerializer.SerializeToElement(true) } } : node).ToArray()
    };

    private static WorkflowDefinition WithExitReader(WorkflowDefinition workflow) => workflow with
    {
        Nodes = workflow.Nodes.Select(node => node.Id == "after" ? Node("after", "test.loopExit") : node).ToArray(),
        Edges = [.. workflow.Edges, D("exit-reason", "loop", "exitReason", "after", "reason"),
            D("exit-iterations", "loop", "iterations", "after", "iterations")]
    };

    private static WorkflowDefinition SkippedConditionWorkflow() => new("while-skipped", "While Skipped Condition",
        [Node("start", "test.mark"), Node("loop", "flow.while", "{\"maxIterations\":2}"),
         Node("gate", "test.counter", "{\"stopAfter\":1}"), Node("if", "flow.ifBoolean"),
         Node("source", "test.counter", "{\"stopAfter\":999}"), Node("skipped", "test.mark"),
         Node("join", "flow.join"), Node("loopEnd", "flow.loopEnd"), Node("after", "test.mark")],
        [C("s-l", "start", "next", "loop", "exec"), C("l-g", "loop", "body", "gate", "exec"),
         C("g-i", "gate", "next", "if", "exec"), D("gate-cond", "gate", "pass", "if", "condition"),
         C("i-t", "if", "true", "source", "exec"), C("i-f", "if", "false", "skipped", "exec"),
         C("t-j", "source", "next", "join", "branch1"), C("f-j", "skipped", "next", "join", "branch2"),
         D("cond", "source", "pass", "loop", "condition"), C("j-e", "join", "next", "loopEnd", "exec"),
         C("e-l", "loopEnd", "repeat", "loop", "repeat"), C("l-a", "loop", "done", "after", "exec")]);

    // ===== 测试基础设施 =====

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        services.AddSingleton(Registry());
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddTransient<VisionLoopStep>();
        services.AddSingleton<WorkflowPlanCache>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        services.AddSingleton<WorkflowDebugSessionService>();
        return services.BuildServiceProvider();
    }

    private static VisionNodeRegistry Registry()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(Catalog("flow.while"), new WhileLoopNode(), "builtin");
        registry.Register(Catalog("flow.repeat"), new RepeatLoopNode(), "builtin");
        registry.Register(Catalog("flow.break"), new BreakLoopNode(), "builtin");
        registry.Register(Catalog("flow.forEach"), new DetectionForEachNode(), "builtin");
        registry.Register(Catalog("flow.loopEnd"), new LoopEndNode(), "builtin");
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.ifBoolean"), new BooleanIfConditionNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.join"), new JoinNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.parallel"), new ParallelMarkerNode(), "builtin");
        registry.Register(CounterCatalog, new CounterNode(), "test");
        registry.Register(EchoIndexCatalog, new EchoIndexNode(), "test");
        registry.Register(SourceCatalog, new DetectionSource(), "test");
        registry.Register(MarkCatalog, new MarkNode(), "test");
        registry.Register(LoopExitCatalog, new LoopExitNode(), "test");
        return registry;
    }

    private static NodeCatalogItem Catalog(string type) => Batch4IterationCatalog.Items.Single(item => item.Type == type);

    private static NodeCatalogItem CounterCatalog { get; } = new("test.counter", "Counter", "Test",
        [new("exec", VisionDataType.Control)],
        [new("pass", VisionDataType.Boolean), new("count", VisionDataType.Integer), new("next", VisionDataType.Control)],
        [new("stopAfter", "Stop After", "number", 2, 0, 1000, 1)],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private static NodeCatalogItem EchoIndexCatalog { get; } = new("test.echoIndex", "Echo Index", "Test",
        [new("exec", VisionDataType.Control), new("index", VisionDataType.Integer)],
        [new("next", VisionDataType.Control)], [],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private static NodeCatalogItem SourceCatalog { get; } = new("test.detections", "Detection Source", "Test",
        [new("exec", VisionDataType.Control)],
        [new("detections", VisionDataType.DetectionList), new("next", VisionDataType.Control)],
        [new("count", "Count", "number", 1)],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private static NodeCatalogItem MarkCatalog { get; } = new("test.mark", "Mark", "Test",
        [new("exec", VisionDataType.Control)],
        [new("next", VisionDataType.Control), new("value", VisionDataType.Integer)], [],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private static NodeCatalogItem LoopExitCatalog { get; } = new("test.loopExit", "Loop Exit", "Test",
        [new("exec", VisionDataType.Control), new("reason", VisionDataType.String), new("iterations", VisionDataType.Integer)],
        [new("next", VisionDataType.Control)], [],
        Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));

    private sealed class LoopExitNode : IVisionNodeExecutor
    {
        public string Type => "test.loopExit";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(),
                new Dictionary<string, object?>
                {
                    ["reason"] = context.Require<string>("reason"),
                    ["iterations"] = context.Require<long>("iterations")
                }));
    }

    private sealed class CounterNode : IVisionNodeExecutor
    {
        private int _count;
        public string Type => "test.counter";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var stopAfter = node.GetInt("stopAfter", 2);
            var count = Interlocked.Increment(ref _count);
            var pass = count <= stopAfter;
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["pass"] = VisionValue.Boolean(pass),
                    ["count"] = VisionValue.Integer(count)
                },
                new Dictionary<string, object?> { ["count"] = count, ["pass"] = pass }));
        }
    }

    private sealed class EchoIndexNode : IVisionNodeExecutor
    {
        public string Type => "test.echoIndex";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var index = context.Require<long>("index");
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>(),
                new Dictionary<string, object?> { ["index"] = index }));
        }
    }

    private sealed class DetectionSource : IVisionNodeExecutor
    {
        public string Type => "test.detections";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var count = node.GetInt("count", 1);
            IReadOnlyList<VisionDetection> list = Enumerable.Range(0, count)
                .Select(i => new VisionDetection(i, i + 1, 0, 1, 1, 10, 4, 1, new VisionCircle(i + 1, 0, 1), new VisionRectangle2D(i + 1, 0, 1, 1), 0.9, 1))
                .ToArray();
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(list) },
                new Dictionary<string, object?> { ["count"] = count }));
        }
    }

    private sealed class MarkNode : IVisionNodeExecutor
    {
        public string Type => "test.mark";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["value"] = VisionValue.Integer(2) },
                new Dictionary<string, object?> { ["value"] = 2 }));
    }

    private static NodeDefinition Node(string id, string type, string? json = null)
        => new(id, type, id, null, json is null ? null : Params(json));
    private static Dictionary<string, JsonElement> Params(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static EdgeDefinition C(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort, "control");
    private static EdgeDefinition D(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort);
}
