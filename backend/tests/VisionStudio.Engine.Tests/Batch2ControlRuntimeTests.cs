using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class Batch2ControlRuntimeTests
{
    [Theory]
    [InlineData("A", "inner-false", "a-tail")]
    [InlineData("B", "b-tail", "b-tail")]
    [InlineData("unmatched", "fallback-tail", "fallback-tail")]
    public async Task Switch_ExecutesOnlySelectedRoute_AndNestedBooleanIfRoutesIndependently(string selected, string expected, string alsoExpected)
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(SwitchWorkflow(selected, false));

        Assert.True(result.Success, result.Error);
        var executed = result.NodeReports.Select(x => x.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(expected, executed);
        Assert.Contains(alsoExpected, executed);
        Assert.Contains("merge", executed);
        Assert.Contains("after", executed);
        Assert.DoesNotContain(selected == "A" ? "b-tail" : "a-tail", executed);
        Assert.DoesNotContain("fallback-tail", executed.Where(x => selected != "unmatched"));
        if (selected == "A")
        {
            Assert.Contains("inner-false", executed);
            Assert.DoesNotContain("inner-true", executed);
        }
        Assert.Equal(selected == "A" ? "case1" : selected == "B" ? "case2" : "default",
            Assert.Single(result.ControlFlowDecisions!, x => x.NodeId == "switch").SelectedBranch);
    }

    [Fact]
    public async Task BooleanIf_RoutesFalseAndNestedIfDecisionDoesNotOverwriteOuterDecision()
    {
        await using var provider = Provider();
        var result = await provider.GetRequiredService<WorkflowCoreVisionRunner>().RunAsync(BooleanIfWorkflow(false));

        Assert.True(result.Success, result.Error);
        var executed = result.NodeReports.Select(x => x.NodeId).ToArray();
        Assert.Contains("false-node", executed);
        Assert.DoesNotContain("true-node", executed);
        Assert.Contains("after", executed);
        var decision = Assert.Single(result.ControlFlowDecisions!, x => x.NodeId == "if");
        Assert.Equal("false", decision.SelectedBranch);
        Assert.Equal("flow.ifBoolean", decision.NodeType);
    }

    [Fact]
    public void Switch_RejectsCasesAndMergePortsThatDoNotMatchExactly()
    {
        var workflow = SwitchWorkflow("A", false);
        var nodes = workflow.Nodes.Select(node => node.Id == "switch"
            ? node with { Parameters = Params("{\"casesJson\":\"[\\\"A\\\",\\\"B\\\",\\\"C\\\"]\"}") }
            : node).ToArray();
        var compiler = new VisionWorkflowCompiler(Registry());
        var error = Assert.Throws<InvalidOperationException>(() => compiler.Compile(workflow with { Nodes = nodes }));
        Assert.Contains("case3", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowDefinition BooleanIfWorkflow(bool condition) => new(
        "boolean-if-runtime", "Boolean If Runtime",
        [Bool("condition", condition), Node("if", "flow.ifBoolean"), Node("true-node", "test.mark"), Node("false-node", "test.mark"), Node("merge", "flow.join"), Node("after", "test.mark")],
        [C("start", "condition", "next", "if", "exec"), D("condition-value", "condition", "value", "if", "condition"),
         C("true", "if", "true", "true-node", "exec"), C("false", "if", "false", "false-node", "exec"),
         C("to-join-true", "true-node", "next", "merge", "branch1"), C("to-join-false", "false-node", "next", "merge", "branch2"), C("after-merge", "merge", "next", "after", "exec")]);

    private static WorkflowDefinition SwitchWorkflow(string value, bool innerCondition)
    {
        var cases = "[\"A\",\"B\"]";
        var nodes = new[]
        {
            Node("source", "test.string"), Node("switch", "flow.switch", "{\"casesJson\":" + JsonSerializer.Serialize(cases) + "}"),
            Bool("inner-condition", innerCondition), Node("inner-if", "flow.ifBoolean"), Node("inner-true", "test.mark"), Node("inner-false", "test.mark"), Node("inner-join", "flow.join"),
            Node("a-tail", "test.mark"), Node("b-tail", "test.mark"), Node("fallback-tail", "test.mark"), Node("merge", "flow.merge"), Node("after", "test.mark")
        };
        var edges = new List<EdgeDefinition>
        {
            C("source-switch", "source", "next", "switch", "exec"), D("source-value", "source", "value", "switch", "value"),
            C("case-a", "switch", "case1", "inner-condition", "exec"), C("case-b", "switch", "case2", "b-tail", "exec"), C("default", "switch", "default", "fallback-tail", "exec"),
            C("condition-if", "inner-condition", "next", "inner-if", "exec"), D("condition-value", "inner-condition", "value", "inner-if", "condition"),
            C("inner-true", "inner-if", "true", "inner-true", "exec"), C("inner-false", "inner-if", "false", "inner-false", "exec"),
            C("inner-join-true", "inner-true", "next", "inner-join", "branch1"), C("inner-join-false", "inner-false", "next", "inner-join", "branch2"),
            C("a-join", "inner-join", "next", "a-tail", "exec"),
            C("a-merge", "a-tail", "next", "merge", "case1"), C("b-merge", "b-tail", "next", "merge", "case2"), C("default-merge", "fallback-tail", "next", "merge", "default"),
            C("merge-after", "merge", "next", "after", "exec")
        };
        var configuredNodes = nodes.Select(node => node.Id == "source" ? node with { Parameters = Params(JsonSerializer.Serialize(new { value })) } : node).ToArray();
        return new WorkflowDefinition("switch-runtime-" + value, "Switch Runtime", configuredNodes, edges);
    }

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddWorkflow(); services.AddWorkflowDSL();
        services.AddSingleton(Registry()); services.AddSingleton<VisionNodeDispatcher>(); services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>(); services.AddSingleton<VisionWorkflowCompiler>(); services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>(); services.AddSingleton<WorkflowPlanCache>(); services.AddTransient<WorkflowCoreVisionRunner>();
        return services.BuildServiceProvider();
    }

    private static VisionNodeRegistry Registry()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.ifBoolean"), new BooleanIfConditionNode(), "batch2-test");
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.switch"), new SwitchNode(), "batch2-test");
        registry.Register(Batch2UtilityCatalog.Items.Single(x => x.Type == "flow.merge"), new FlowMergeNode(), "batch2-test");
        registry.Register(BuiltInNodeCatalog.Require("flow.join"), new JoinNode(), "built-in-test");
        registry.Register(SourceCatalog("test.string", VisionDataType.String), new SourceNode("test.string"), "test");
        registry.Register(SourceCatalog("test.boolean", VisionDataType.Boolean), new SourceNode("test.boolean"), "test");
        registry.Register(MarkCatalog, new MarkNode(), "test");
        return registry;
    }

    private static NodeCatalogItem SourceCatalog(string type, VisionDataType output) => new(type, type, "Test", [new("exec", VisionDataType.Control)], [new("value", output), new("next", VisionDataType.Control)], [new("value", "Value", output == VisionDataType.Boolean ? "boolean" : "text", output == VisionDataType.Boolean ? false : "")], Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));
    private static NodeCatalogItem MarkCatalog { get; } = new("test.mark", "Mark", "Test", [new("exec", VisionDataType.Control)], [new("next", VisionDataType.Control)], [], Capabilities: new VisionToolCapabilities(ExecutionMode: VisionExecutionMode.WorkflowCore));
    private sealed class SourceNode(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var value = Type == "test.boolean" ? VisionValue.Boolean(node.GetBool("value", false)) : VisionValue.String(node.GetString("value", ""));
            return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["value"] = value }, new Dictionary<string, object?> { ["value"] = value.Value }));
        }
    }
    private sealed class MarkNode : IVisionNodeExecutor
    {
        public string Type => "test.mark";
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?> { ["node"] = node.Id }));
    }

    private static NodeDefinition Node(string id, string type, string? parameters = null) => new(id, type, id, null, parameters is null ? null : Params(parameters));
    private static NodeDefinition Bool(string id, bool value) => Node(id, "test.boolean", JsonSerializer.Serialize(new { value }));
    private static Dictionary<string, JsonElement> Params(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static EdgeDefinition C(string id, string source, string sourcePort, string target, string targetPort) => new(id, source, sourcePort, target, targetPort, "control");
    private static EdgeDefinition D(string id, string source, string sourcePort, string target, string targetPort) => new(id, source, sourcePort, target, targetPort);
}
