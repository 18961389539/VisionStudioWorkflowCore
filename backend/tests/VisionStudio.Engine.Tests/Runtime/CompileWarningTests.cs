using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// flow.parallel 退化为顺序执行时必须给出编译提示（2026-11-10）。
/// 画布写着并行、产物却是 Sequence——不改变结果但影响节拍，不能静默。
/// </summary>
public sealed class CompileWarningTests
{
    [Fact]
    public void ParallelWithNonFusibleBranch_CompilesWithAWarningNamingTheBlocker()
    {
        var workflow = new WorkflowDefinition("parallel-non-fusible", "Parallel non-fusible",
        [
            Node("p", "flow.parallel"), Node("n1", "value.number"), Node("rng1", "value.range"), Node("r1", "flow.result"),
            Node("n2", "value.number"), Node("rng2", "value.range"), Node("r2", "flow.result"), Node("join", "flow.join")
        ],
        [
            C("p1", "p", "branch1", "n1"), C("n1-rng1", "n1", "next", "rng1"),
            C("rng1-r1", "rng1", "next", "r1"), C("r1-j", "r1", "next", "join", "branch1"),
            C("p2", "p", "branch2", "n2"), C("n2-rng2", "n2", "next", "rng2"),
            C("rng2-r2", "rng2", "next", "r2"), C("r2-j", "r2", "next", "join", "branch2"),
            D("d1", "n1", "value", "rng1", "value"), D("d2", "rng1", "pass", "r1", "pass"),
            D("d3", "n2", "value", "rng2", "value"), D("d4", "rng2", "pass", "r2", "pass")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        var warning = Assert.Single(compiled.Warnings);
        Assert.Equal("flow.parallel.sequential", warning.Code);
        Assert.Equal("p", warning.NodeId);
        Assert.Contains("runs both branches sequentially", warning.Message);
        Assert.Contains("flow.result", warning.Message);
    }

    [Fact]
    public void ParallelWithFusibleBranches_HasNoWarning()
    {
        var workflow = new WorkflowDefinition("parallel-fused", "Parallel fused",
        [
            Node("p", "flow.parallel"), Node("n1", "value.number"), Node("n2", "value.number"), Node("join", "flow.join")
        ],
        [
            C("p1", "p", "branch1", "n1"), C("n1-j", "n1", "next", "join", "branch1"),
            C("p2", "p", "branch2", "n2"), C("n2-j", "n2", "next", "join", "branch2")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Empty(compiled.Warnings);
        Assert.Contains(compiled.PipelineSegments.Values, nodes => nodes.Contains("n1") && nodes.Contains("n2"));
    }

    [Fact]
    public void ParallelInsideLoopBody_WarnsThatFusionIsDisabledInLoops()
    {
        var workflow = new WorkflowDefinition("parallel-in-loop", "Parallel in loop",
        [
            Node("start", "value.number"), Node("loop", "flow.repeat", """{"count":1}"""),
            Node("p", "flow.parallel"), Node("n1", "value.number"), Node("n2", "value.number"),
            Node("join", "flow.join"), Node("loopEnd", "flow.loopEnd"), Node("after", "value.number")
        ],
        [
            C("start-loop", "start", "next", "loop", "exec"), C("loop-p", "loop", "body", "p", "exec"),
            C("p1", "p", "branch1", "n1"), C("n1-j", "n1", "next", "join", "branch1"),
            C("p2", "p", "branch2", "n2"), C("n2-j", "n2", "next", "join", "branch2"),
            C("join-end", "join", "next", "loopEnd", "exec"), C("end-loop", "loopEnd", "repeat", "loop", "repeat"),
            C("loop-after", "loop", "done", "after", "exec")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        var warning = Assert.Single(compiled.Warnings);
        Assert.Equal("flow.parallel.sequential", warning.Code);
        Assert.Equal("p", warning.NodeId);
        Assert.Contains("inside a loop body", warning.Message);
    }

    private static VisionWorkflowCompiler CreateCompiler()
    {
        var registry = new VisionNodeRegistry();
        foreach (var catalog in BuiltInNodeCatalog.Items)
            registry.Register(catalog, new NoopExecutor(catalog.Type), "test-catalog");
        return new VisionWorkflowCompiler(registry);
    }

    private static NodeDefinition Node(string id, string type, string? parametersJson = null)
        => new(id, type, id, null, parametersJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson));

    private static EdgeDefinition C(string id, string source, string sourcePort, string target, string targetPort = "exec")
        => new(id, source, sourcePort, target, targetPort, "control");

    private static EdgeDefinition D(string id, string source, string sourcePort, string target, string targetPort)
        => new(id, source, sourcePort, target, targetPort);

    private sealed class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new NotSupportedException("Compilation-only executor.");
    }
}
