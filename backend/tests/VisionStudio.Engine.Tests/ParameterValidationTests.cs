using System.Text.Json;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Compile-time parameter validation: catalog-declared parameters with wrong types, out-of-range
/// numbers or invalid select options must be rejected with node id + parameter name + reason.
/// </summary>
public sealed class ParameterValidationTests
{
    [Theory]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":"3"}""", "expects a number")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":null}""", "received null")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":-1}""", "below the minimum 0")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":1000000}""", "above the maximum 999999")]
    [InlineData("calibration.planar", "targetUnit", """{"targetUnit":"furlong"}""", "not an allowed option")]
    [InlineData("calibration.planar", "targetUnit", """{"targetUnit":7}""", "expects one of [mm, cm, m]")]
    [InlineData("calibration.planar", "targetFrame", """{"targetFrame":42}""", "expects text")]
    [InlineData("image.acquire", "autoStart", """{"autoStart":"true"}""", "expects a boolean")]
    [InlineData("image.acquire", "timeoutMs", """{"timeoutMs":{"value":100}}""", "expects a number")]
    public void InvalidParameter_IsRejectedWithNodeIdAndParameterName(
        string nodeType,
        string parameterName,
        string parametersJson,
        string reasonFragment)
    {
        var compiler = CreateCompiler();

        var error = Assert.Throws<InvalidOperationException>(
            () => compiler.Compile(CreateWorkflow(nodeType, parametersJson)));

        Assert.Contains($"Node 'n1' parameter '{parameterName}'", error.Message);
        Assert.Contains(reasonFragment, error.Message);
    }

    [Theory]
    [InlineData("calibration.planar", """{"targetUnit":"MM","assetVersion":0,"pairsJson":"[]","targetFrame":"Workpiece"}""")]
    [InlineData("calibration.planar", """{"assetVersion":999999}""")]
    [InlineData("calibration.planar", """{"assetVersion":3.5}""")]
    [InlineData("calibration.planar", "{}")]
    [InlineData("calibration.planar", "null")]
    // Undeclared keys must stay ignored: roi is consumed by VisionRoiParameterExtensions, not by the catalog.
    [InlineData("calibration.planar", """{"roi":{"type":"Rectangle","x":1,"y":2,"width":3,"height":4},"customKey":7}""")]
    [InlineData("image.acquire", """{"frameMode":"next","timeoutMs":1500,"autoStart":true}""")]
    [InlineData("image.acquire", """{"timeoutMs":50}""")]
    [InlineData("image.acquire", """{"timeoutMs":10000}""")]
    public void AcceptedParameterValues_Compile(string nodeType, string parametersJson)
    {
        var compiler = CreateCompiler();

        var compiled = compiler.Compile(CreateWorkflow(nodeType, parametersJson));

        Assert.NotEmpty(compiled.DslJson);
        Assert.Contains("n1", compiled.OrderedNodeIds);
    }

    [Theory]
    [InlineData("image.filter", """{"method":"median","sigmaX":"inactive"}""")]
    [InlineData("image.morphology", """{"operation":"gradient","kernelSize":"inactive"}""")]
    [InlineData("image.morphology", """{"operation":"open","kernelWidth":"inactive"}""")]
    [InlineData("text.validate", """{"mode":"contains","pattern":7,"timeoutMs":"inactive"}""")]
    public void InactiveModeParameters_AreNotValidated(string nodeType, string parametersJson)
    {
        var compiler = CreateCompiler();
        var nodeCatalog = BuiltInNodeCatalog.Require(nodeType);
        var nodes = new List<NodeDefinition>();
        var edges = new List<EdgeDefinition>();
        if (nodeCatalog.Inputs.Any(port => port.Name == "image" && port.DataType != VisionDataType.Control))
        {
            nodes.Add(new NodeDefinition("source", "image.synthetic", null, null, null));
            edges.Add(new EdgeDefinition("image", "source", "image", "n1", "image", "data"));
        }
        nodes.Add(new NodeDefinition("n1", nodeType, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson)));

        var compiled = compiler.Compile(new WorkflowDefinition("wf-conditional", "conditional parameters", nodes, edges));

        Assert.NotEmpty(compiled.DslJson);
    }

    private static WorkflowDefinition CreateWorkflow(string nodeType, string parametersJson)
        => new(
            "wf-params",
            "Parameter validation",
            [new NodeDefinition("n1", nodeType, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson))],
            []);

    private static VisionWorkflowCompiler CreateCompiler()
    {
        var registry = new VisionNodeRegistry();
        foreach (var catalog in BuiltInNodeCatalog.Items)
            registry.Register(catalog, new NoopExecutor(catalog.Type), "test-catalog");
        return new VisionWorkflowCompiler(registry);
    }

    private sealed class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new NotSupportedException("Compilation-only executor.");
    }
}
