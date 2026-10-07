using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class RecursiveStructuredWorkflowTests
{
    [Fact]
    public void Compiler_CompilesNestedIfWithDistinctControlRegionsAndKeyedConditions()
    {
        var compiler = CreateBuiltInCompiler();
        var workflow = LoadSample("nested-structured-workflow.json");

        var compiled = compiler.Compile(workflow);

        Assert.Equal(2, compiled.ControlRegions.Count);
        var outer = Assert.Single(compiled.ControlRegions.Where(x => x.ControlNodeId == "outer-if"));
        var inner = Assert.Single(compiled.ControlRegions.Where(x => x.ControlNodeId == "inner-if"));
        Assert.Equal(0, outer.Depth);
        Assert.Equal("outer-join", outer.JoinNodeId);
        Assert.Equal(1, inner.Depth);
        Assert.Equal("inner-join", inner.JoinNodeId);
        Assert.Contains("data.GetBranchCondition(\"outer-if\")", compiled.DslJson, StringComparison.Ordinal);
        Assert.Contains("data.GetBranchCondition(\"inner-if\")", compiled.DslJson, StringComparison.Ordinal);
        Assert.DoesNotContain("data.BranchCondition == false", compiled.DslJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_PreservesVisionPipelineFusionInsideNestedBranches()
    {
        var compiler = CreateBuiltInCompiler();
        var compiled = compiler.Compile(LoadSample("nested-structured-workflow.json"));

        Assert.Contains(compiled.PipelineSegments.Values, x => x.Contains("inner-true"));
        Assert.Contains(compiled.PipelineSegments.Values, x => x.Contains("inner-false"));
        Assert.Contains(compiled.PipelineSegments.Values, x => x.Contains("outer-false"));
        Assert.Equal(12, compiled.OrderedNodeIds.Count);
    }

    [Fact]
    public void Compiler_RejectsNestedSplitThatAttemptsToReuseParentJoin()
    {
        var compiler = CreateControlCompiler();
        var workflow = new WorkflowDefinition(
            "invalid-shared-join",
            "Invalid Shared Join",
            [
                Node("outer", "flow.if"),
                Node("inner", "flow.if"),
                Node("inner-t", "test.step"),
                Node("inner-f", "test.step"),
                Node("outer-f", "test.step"),
                Node("join", "flow.join")
            ],
            [
                C("e1", "outer", "true", "inner", "exec"),
                C("e2", "outer", "false", "outer-f", "exec"),
                C("e3", "inner", "true", "inner-t", "exec"),
                C("e4", "inner", "false", "inner-f", "exec"),
                C("e5", "inner-t", "next", "join", "branch1"),
                C("e6", "inner-f", "next", "join", "branch2"),
                C("e7", "outer-f", "next", "join", "branch2")
            ]);

        var ex = Assert.Throws<InvalidOperationException>(() => compiler.Compile(workflow));
        Assert.Contains("no distinct common Join", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compiler_CompilesParallelNestedInsideIf()
    {
        var compiler = CreateControlCompiler();
        var workflow = new WorkflowDefinition(
            "if-parallel",
            "If Parallel",
            [
                Node("outer", "flow.if"),
                Node("parallel", "flow.parallel"),
                Node("p1", "test.step"),
                Node("p2", "test.step"),
                Node("pjoin", "flow.join"),
                Node("true-tail", "test.step"),
                Node("false-tail", "test.step"),
                Node("outer-join", "flow.join")
            ],
            [
                C("e1", "outer", "true", "parallel", "exec"),
                C("e2", "outer", "false", "false-tail", "exec"),
                C("e3", "parallel", "branch1", "p1", "exec"),
                C("e4", "parallel", "branch2", "p2", "exec"),
                C("e5", "p1", "next", "pjoin", "branch1"),
                C("e6", "p2", "next", "pjoin", "branch2"),
                C("e7", "pjoin", "next", "true-tail", "exec"),
                C("e8", "true-tail", "next", "outer-join", "branch1"),
                C("e9", "false-tail", "next", "outer-join", "branch2")
            ]);

        var compiled = compiler.Compile(workflow);

        Assert.Contains(compiled.ControlRegions, x => x.ControlNodeId == "outer" && x.Depth == 0);
        Assert.Contains(compiled.ControlRegions, x => x.ControlNodeId == "parallel" && x.Depth == 1);
        Assert.Contains("parallel.__parallel", compiled.DslJson, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowData_KeepsNestedBranchDecisionsIndependent()
    {
        using var data = new VisionWorkflowData();
        data.SetBranchCondition("outer-if", true);
        data.SetBranchCondition("inner-if", false);
        data.MarkParallelBranches("parallel-1");

        Assert.True(data.GetBranchCondition("outer-if"));
        Assert.False(data.GetBranchCondition("inner-if"));
        var decisions = data.SnapshotControlFlowDecisions();
        Assert.Contains(decisions, x => x.NodeId == "outer-if" && x.SelectedBranch == "true");
        Assert.Contains(decisions, x => x.NodeId == "inner-if" && x.SelectedBranch == "false");
        Assert.Contains(decisions, x => x.NodeId == "parallel-1" && x.ActiveBranches.SequenceEqual(["branch1", "branch2"]));
    }

    private static WorkflowDefinition LoadSample(string name)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var path = Path.Combine(AppContext.BaseDirectory, "samples", name);
        return JsonSerializer.Deserialize<WorkflowDefinition>(File.ReadAllText(path), options)
            ?? throw new InvalidOperationException($"Unable to load {name}.");
    }

    private static VisionWorkflowCompiler CreateBuiltInCompiler()
    {
        var registry = new VisionNodeRegistry();
        foreach (var catalog in BuiltInNodeCatalog.Items)
            registry.Register(catalog, new NoopExecutor(catalog.Type), "test-catalog");
        return new VisionWorkflowCompiler(registry);
    }

    private static VisionWorkflowCompiler CreateControlCompiler()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(new NodeCatalogItem(
            "flow.if", "If", "Flow",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("true", VisionDataType.Control), new PortDescriptor("false", VisionDataType.Control)], []),
            new NoopExecutor("flow.if"), "test");
        registry.Register(new NodeCatalogItem(
            "flow.parallel", "Parallel", "Flow",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("branch1", VisionDataType.Control), new PortDescriptor("branch2", VisionDataType.Control)], []),
            new NoopExecutor("flow.parallel"), "test");
        registry.Register(new NodeCatalogItem(
            "flow.join", "Join", "Flow",
            [new PortDescriptor("branch1", VisionDataType.Control), new PortDescriptor("branch2", VisionDataType.Control)],
            [new PortDescriptor("next", VisionDataType.Control)], []),
            new NoopExecutor("flow.join"), "test");
        registry.Register(new NodeCatalogItem(
            "test.step", "Step", "Test",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("next", VisionDataType.Control)], []),
            new NoopExecutor("test.step"), "test");
        return new VisionWorkflowCompiler(registry);
    }

    private static NodeDefinition Node(string id, string type) => new(id, type, id, null, null);
    private static EdgeDefinition C(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort, "control");

    private sealed class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new NotSupportedException("Compilation-only executor.");
    }
}
