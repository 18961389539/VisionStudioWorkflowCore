using System.Text.Json;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class WorkflowPropertyTests
{
    [Fact]
    public void DeterministicGeneratedLinearGraphs_CompileForOneHundredSeeds()
    {
        var compiler = CreateCompiler();
        for (var seed = 0; seed < 100; seed++)
        {
            var workflow = GenerateLinear(seed);
            var first = compiler.Compile(workflow);
            var second = compiler.Compile(workflow);
            Assert.Equal(first.WorkflowCoreId, second.WorkflowCoreId);
            Assert.Equal(workflow.Nodes.Count, first.OrderedNodeIds.Count);
            Assert.Equal(workflow.Nodes.Select(x => x.Id), first.OrderedNodeIds);
        }
    }

    [Fact]
    public void GeneratedGraphs_WithRandomTypeMutation_AreRejectedBeforeRuntime()
    {
        var compiler = CreateCompiler();
        for (var seed = 0; seed < 50; seed++)
        {
            var workflow = GenerateLinear(seed);
            var last = workflow.Nodes[^1];
            var sink = new NodeDefinition("bool-sink", BoolSink.NodeType, "Bool Sink", null, null);
            var nodes = workflow.Nodes.Concat([sink]).ToArray();
            var edges = workflow.Edges.Concat([
                new EdgeDefinition($"bad-{seed}", last.Id, "value", sink.Id, "value")
            ]).ToArray();
            var invalid = workflow with { Id = workflow.Id + "-bad", Nodes = nodes, Edges = edges };
            var ex = Assert.Throws<InvalidOperationException>(() => compiler.Compile(invalid));
            Assert.Contains("type mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GeneratedGraphs_WithCycleOrSecondSource_AreRejected()
    {
        var compiler = CreateCompiler();
        for (var seed = 0; seed < 25; seed++)
        {
            var workflow = GenerateLinear(seed);
            if (workflow.Nodes.Count < 3) continue;
            var last = workflow.Nodes[^1];
            var second = workflow.Nodes[1];
            var edges = workflow.Edges.Concat([
                new EdgeDefinition($"cycle-{seed}", last.Id, "value", second.Id, "value")
            ]).ToArray();
            var invalid = workflow with { Id = workflow.Id + "-cycle", Edges = edges };
            Assert.Throws<InvalidOperationException>(() => compiler.Compile(invalid));
        }
    }

    private static VisionWorkflowCompiler CreateCompiler()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(NumberSource.Catalog, new NumberSource(), "property-test");
        registry.Register(NumberTransform.Catalog, new NumberTransform(), "property-test");
        registry.Register(BoolSink.Catalog, new BoolSink(), "property-test");
        return new VisionWorkflowCompiler(registry);
    }

    private static WorkflowDefinition GenerateLinear(int seed)
    {
        var rng = new Random(seed * 7919 + 17);
        var transforms = rng.Next(1, 12);
        var nodes = new List<NodeDefinition>
        {
            new("source", NumberSource.NodeType, "Source", null, new Dictionary<string, JsonElement>
            {
                ["value"] = JsonSerializer.SerializeToElement(rng.NextDouble() * 100)
            })
        };
        var edges = new List<EdgeDefinition>();
        var previous = "source";
        for (var i = 0; i < transforms; i++)
        {
            var id = $"n{i:D2}";
            nodes.Add(new NodeDefinition(id, NumberTransform.NodeType, id, null, new Dictionary<string, JsonElement>
            {
                ["gain"] = JsonSerializer.SerializeToElement(0.5 + rng.NextDouble() * 2),
                ["offset"] = JsonSerializer.SerializeToElement(rng.NextDouble() * 5 - 2.5)
            }));
            edges.Add(new EdgeDefinition($"e{i:D2}", previous, "value", id, "value"));
            previous = id;
        }
        return new WorkflowDefinition($"generated-{seed:D3}", $"Generated {seed}", nodes, edges);
    }

    private sealed class NumberSource : IVisionNodeExecutor
    {
        public const string NodeType = "property.numberSource";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Number Source", "Test", [], [new PortDescriptor("value", VisionDataType.Double)],
            [new ParameterDescriptor("value", "Value", "number", 0)]);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(node.GetDouble("value", 0)) },
                new Dictionary<string, object?>()));
    }

    private sealed class NumberTransform : IVisionNodeExecutor
    {
        public const string NodeType = "property.numberTransform";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Number Transform", "Test",
            [new PortDescriptor("value", VisionDataType.Double)],
            [new PortDescriptor("value", VisionDataType.Double)],
            [new ParameterDescriptor("gain", "Gain", "number", 1), new ParameterDescriptor("offset", "Offset", "number", 0)]);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var value = context.RequireNumber("value") * node.GetDouble("gain", 1) + node.GetDouble("offset", 0);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(value) },
                new Dictionary<string, object?>()));
        }
    }

    private sealed class BoolSink : IVisionNodeExecutor
    {
        public const string NodeType = "property.boolSink";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Bool Sink", "Test", [new PortDescriptor("value", VisionDataType.Boolean)], [], []);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
