using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class NodePortValidationTests
{
    private static NodeDefinition Node(string parameters = "{}") => new("node", "test.ports", null, null,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters));

    private static NodeCatalogItem Catalog(object? defaultMode = null) => new("test.ports", "Ports", "Test",
        [new("reference", VisionDataType.Double, Required: false) { RequiredWhen = new("mode", ["enabled", "true"]) }],
        [new("result", VisionDataType.Double, Required: false) { RequiredWhen = new("mode", ["enabled", "true"]) }],
        [new("mode", "Mode", defaultMode is bool ? "boolean" : "select", defaultMode ?? "disabled")]);

    [Theory]
    [InlineData("{\"mode\":\"ENABLED\"}")]
    [InlineData("{\"mode\":true}")]
    public async Task DirectExecution_RejectsMissingConditionalInputsBeforeCallingExecutor(string parameters)
    {
        using var registry = new VisionNodeRegistry();
        var executor = new Probe();
        registry.Register(Catalog(), executor, "test");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new VisionNodeDispatcher(registry).ExecuteAsync(Node(parameters), new Dictionary<string, VisionValue>(), null, default));

        Assert.Contains("Required input 'node.reference'", error.Message);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void Compilation_UsesBooleanDefaultsForConditionalInputs()
    {
        using var registry = new VisionNodeRegistry();
        registry.Register(Catalog(true), new Probe(), "test");
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(registry)
            .Compile(new("conditional", "Conditional", [Node()], [])));

        Assert.Contains("Required input 'node.reference'", error.Message);
    }

    [Fact]
    public async Task DirectExecution_RejectsWrongInputTypesBeforeCallingExecutor()
    {
        using var registry = new VisionNodeRegistry();
        var executor = new Probe();
        registry.Register(Catalog(), executor, "test");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new VisionNodeDispatcher(registry).ExecuteAsync(Node(),
                new Dictionary<string, VisionValue> { ["reference"] = VisionValue.String("invalid") }, null, default));

        Assert.Contains("String -> Double", error.Message);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task DirectExecution_EnforcesConditionalOutputsAndAllowsIntegerWidening()
    {
        using var registry = new VisionNodeRegistry();
        var executor = new Probe();
        registry.Register(Catalog(), executor, "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var inputs = new Dictionary<string, VisionValue> { ["reference"] = VisionValue.Integer(7) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.ExecuteAsync(Node("""{"mode":"enabled"}"""), inputs, null, default));
        Assert.Contains("required output 'result'", error.Message);

        executor.Outputs["result"] = VisionValue.Integer(7);
        var result = await dispatcher.ExecuteAsync(Node("""{"mode":"enabled"}"""), inputs, null, default);
        Assert.Equal(7L, result.Outputs["result"].Value);
    }

    [Fact]
    public void ResolveInputs_EnforcesConditionalRequirementsAndRejectsDuplicateSources()
    {
        using var registry = new VisionNodeRegistry();
        registry.Register(Catalog(), new Probe(), "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var node = Node("""{"mode":"ENABLED"}""");
        using var missing = new VisionWorkflowData { Definition = new("missing", "Missing", [node], []) };
        Assert.Contains("Required input", Assert.Throws<InvalidOperationException>(() => dispatcher.ResolveInputs(missing, node)).Message);

        using var duplicate = new VisionWorkflowData { Definition = new("duplicate", "Duplicate", [node],
            [new("a", "source", "value", node.Id, "reference"), new("b", "source", "value", node.Id, "reference")]) };
        duplicate.OutputsByNode["source"] = new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(1) };
        Assert.Contains("multiple data sources", Assert.Throws<InvalidOperationException>(() => dispatcher.ResolveInputs(duplicate, node)).Message);
    }

    [Theory]
    [InlineData(VisionDataType.Control, VisionDataType.Any)]
    [InlineData(VisionDataType.Any, VisionDataType.Control)]
    public void WildcardDataPorts_CannotCarryControl(VisionDataType source, VisionDataType target)
        => Assert.False(VisionNodeDispatcher.AreCompatible(source, target));

    private sealed class Probe : IVisionNodeExecutor
    {
        public string Type => "test.ports";
        public int Calls { get; private set; }
        public Dictionary<string, VisionValue> Outputs { get; } = [];
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new NodeExecutorResult(Outputs, new Dictionary<string, object?>()));
        }
    }
}
