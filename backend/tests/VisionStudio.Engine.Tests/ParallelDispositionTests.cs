using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Parallel branch disposition semantics: flow.result inside a Parallel branch is scoped to its
/// branch and merged at Join with any-NG semantics, so no execution order (branch order, step
/// count or timing) can mask an NG with a later OK. Sequential results outside Parallel regions
/// keep the existing direct-write behavior.
/// </summary>
public sealed class ParallelDispositionTests
{
    [Fact]
    public async Task ParallelBranches_NgBranchWins_WhenItRunsFirst()
    {
        var result = await RunAsync(TwoBranchWorkflow("par-ng-first", pass1: false, pass2: true));

        Assert.True(result.Success, result.Error);
        Assert.Equal("NG", result.QualityDisposition);

        var join = Report(result, "j");
        Assert.Equal("NG", join.Summary["parallelDisposition"]);
        var branches = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(join.Summary["branchDispositions"]);
        Assert.Equal("NG", branches["branch1"]);
        Assert.Equal("OK", branches["branch2"]);

        Assert.Equal("p.branch1", Report(result, "r1").Summary["dispositionScope"]);
        Assert.Equal("p.branch2", Report(result, "r2").Summary["dispositionScope"]);
    }

    [Fact]
    public async Task ParallelBranches_NgBranchWins_WhenItRunsLast()
    {
        var result = await RunAsync(TwoBranchWorkflow("par-ng-last", pass1: true, pass2: false));

        Assert.True(result.Success, result.Error);
        Assert.Equal("NG", result.QualityDisposition);
        var branches = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(Report(result, "j").Summary["branchDispositions"]);
        Assert.Equal("OK", branches["branch1"]);
        Assert.Equal("NG", branches["branch2"]);
    }

    [Fact]
    public async Task ParallelBranches_AllOk_ProducesOk()
    {
        var result = await RunAsync(TwoBranchWorkflow("par-all-ok", pass1: true, pass2: true));

        Assert.True(result.Success, result.Error);
        Assert.Equal("OK", result.QualityDisposition);
        Assert.Equal("OK", Report(result, "j").Summary["parallelDisposition"]);
    }

    [Fact]
    public async Task ParallelBranches_NgWins_EvenWhenTheOkBranchIsLonger()
    {
        // branch1 = gate -> gate -> result (OK, 3 steps); branch2 = gate -> result (NG, 2 steps).
        // Under the previous last-writer behavior the longer branch's OK result executed last and
        // masked branch2's NG. The Join aggregation must keep NG.
        var workflow = new WorkflowDefinition(
            "par-uneven",
            "Parallel Uneven",
            [
                Node("p", "flow.parallel"),
                Node("g1", "test.gate", GateParams(pass: true)),
                Node("g2", "test.gate", GateParams(pass: true)),
                Node("g3", "test.gate", GateParams(pass: false)),
                Node("r1", "flow.result"),
                Node("r2", "flow.result"),
                Node("j", "flow.join")
            ],
            [
                Control("c1", "p", "branch1", "g1"),
                Control("c2", "p", "branch2", "g3"),
                Control("c3", "g1", "next", "g2"),
                Control("c4", "g2", "next", "r1"),
                Control("c5", "g3", "next", "r2"),
                Data("d1", "g2", "pass", "r1", "pass"),
                Data("d2", "g3", "pass", "r2", "pass"),
                Control("c6", "r1", "next", "j", "branch1"),
                Control("c7", "r2", "next", "j", "branch2")
            ]);

        var result = await RunAsync(workflow);

        Assert.True(result.Success, result.Error);
        Assert.Equal("NG", result.QualityDisposition);
        var branches = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(Report(result, "j").Summary["branchDispositions"]);
        Assert.Equal("OK", branches["branch1"]);
        Assert.Equal("NG", branches["branch2"]);
    }

    [Fact]
    public async Task ParallelBranches_WithoutResults_LeaveDispositionUntouched()
    {
        var workflow = new WorkflowDefinition(
            "par-no-results",
            "Parallel Without Results",
            [
                Node("p", "flow.parallel"),
                Node("g1", "test.gate", GateParams(pass: true)),
                Node("g2", "test.gate", GateParams(pass: true)),
                Node("j", "flow.join")
            ],
            [
                Control("c1", "p", "branch1", "g1"),
                Control("c2", "p", "branch2", "g2"),
                Control("c3", "g1", "next", "j", "branch1"),
                Control("c4", "g2", "next", "j", "branch2")
            ]);

        var result = await RunAsync(workflow);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.QualityDisposition);
        Assert.False(Report(result, "j").Summary.ContainsKey("parallelDisposition"));
    }

    [Fact]
    public async Task SequentialResult_OutsideParallel_StillSetsRunDisposition()
    {
        var workflow = new WorkflowDefinition(
            "sequential-result",
            "Sequential Result",
            [
                Node("g1", "test.gate", GateParams(pass: false)),
                Node("r1", "flow.result")
            ],
            [
                Control("c1", "g1", "next", "r1"),
                Data("d1", "g1", "pass", "r1", "pass")
            ]);

        var result = await RunAsync(workflow);

        Assert.True(result.Success, result.Error);
        Assert.Equal("NG", result.QualityDisposition);
        Assert.False(Report(result, "r1").Summary.ContainsKey("dispositionScope"));
    }

    [Fact]
    public async Task ParallelBranches_ExecuteConcurrently()
    {
        // 两条分支各阻塞 200ms：顺序执行时区间不可能重叠、总时长约 400ms。
        var workflow = new WorkflowDefinition(
            "par-concurrent",
            "Parallel Concurrent",
            [
                Node("p", "flow.parallel"),
                Node("s1", "test.slow", SlowParams(200)),
                Node("s2", "test.slow", SlowParams(200)),
                Node("j", "flow.join")
            ],
            [
                Control("c1", "p", "branch1", "s1"),
                Control("c2", "p", "branch2", "s2"),
                Control("c3", "s1", "next", "j", "branch1"),
                Control("c4", "s2", "next", "j", "branch2")
            ]);

        var result = await RunAsync(workflow);

        Assert.True(result.Success, result.Error);
        var first = Report(result, "s1");
        var second = Report(result, "s2");
        var overlapMs = Math.Min(first.EndOffsetMs, second.EndOffsetMs) - Math.Max(first.StartOffsetMs, second.StartOffsetMs);
        Assert.True(
            overlapMs > 0,
            $"parallel branches did not overlap: s1=[{first.StartOffsetMs:F1},{first.EndOffsetMs:F1}] s2=[{second.StartOffsetMs:F1},{second.EndOffsetMs:F1}]");
        Assert.True(
            result.TotalDurationMs < 380,
            $"total duration {result.TotalDurationMs:F1}ms suggests the branches ran sequentially");
    }

    private static async Task<WorkflowRunResult> RunAsync(WorkflowDefinition workflow)
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        return await runner.RunAsync(workflow);
    }

    private static WorkflowDefinition TwoBranchWorkflow(string id, bool pass1, bool pass2)
        => new(
            id,
            "Parallel Disposition",
            [
                Node("p", "flow.parallel"),
                Node("g1", "test.gate", GateParams(pass1)),
                Node("g2", "test.gate", GateParams(pass2)),
                Node("r1", "flow.result"),
                Node("r2", "flow.result"),
                Node("j", "flow.join")
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

    private static NodeRunReport Report(WorkflowRunResult result, string nodeId)
        => Assert.Single(result.NodeReports.Where(x => x.NodeId == nodeId));

    private static NodeDefinition Node(string id, string type, Dictionary<string, JsonElement>? parameters = null)
        => new(id, type, id, null, parameters);

    private static EdgeDefinition Control(string id, string source, string sourcePort, string target, string targetPort = "exec")
        => new(id, source, sourcePort, target, targetPort, "control");

    private static EdgeDefinition Data(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort);

    private static Dictionary<string, JsonElement> GateParams(bool pass) => new()
    {
        ["pass"] = JsonSerializer.SerializeToElement(pass)
    };

    private static Dictionary<string, JsonElement> SlowParams(int delayMs) => new()
    {
        ["delayMs"] = JsonSerializer.SerializeToElement(delayMs)
    };

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();

        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("flow.parallel"), new ParallelMarkerNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.result"), new ResultDispositionNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require("flow.join"), new JoinNode(), "builtin");
        registry.Register(GateExecutor.Catalog, new GateExecutor(), "test");
        registry.Register(SlowExecutor.Catalog, new SlowExecutor(), "test");
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
        public const string NodeType = "test.gate";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Gate", "Test",
            [new PortDescriptor("exec", VisionDataType.Control)],
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

    /// <summary>并发回归用：按 delayMs 阻塞，供时间线区间重叠断言。</summary>
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
}
