using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class WorkflowCoreRuntimeTests
{
    [Fact]
    public async Task WorkflowCoreRunner_ExecutesRegisteredNodesAndPassesTypedData()
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var workflow = new WorkflowDefinition(
            "test-linear",
            "Test Linear",
            [
                new NodeDefinition("source", "test.constant", "Source", null, null),
                new NodeDefinition("add", "test.add", "Add", null, new Dictionary<string, JsonElement>
                {
                    ["increment"] = JsonDocument.Parse("3").RootElement.Clone()
                })
            ],
            [new EdgeDefinition("e1", "source", "value", "add", "value")]);

        var result = await runner.RunAsync(workflow);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.NodeReports.Count);
        var add = Assert.Single(result.NodeReports.Where(x => x.NodeId == "add"));
        Assert.Equal(5d, Assert.IsType<double>(add.Summary["result"]));
        Assert.All(result.NodeReports, report => Assert.True(report.ExecutionSequence > 0));
        Assert.All(result.NodeReports, report => Assert.True(report.EndOffsetMs >= report.StartOffsetMs));
        Assert.Equal(result.NodeReports.Count, result.NodeReports.Select(x => x.ExecutionSequence).Distinct().Count());
    }

    [Fact]
    public void Compiler_RejectsTypeMismatchBeforeWorkflowCoreExecution()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(ConstantExecutor.Catalog, new ConstantExecutor(), "test");
        registry.Register(BooleanSinkExecutor.Catalog, new BooleanSinkExecutor(), "test");
        var compiler = new VisionWorkflowCompiler(registry);
        var workflow = new WorkflowDefinition(
            "bad-types",
            "Bad Types",
            [
                new NodeDefinition("source", "test.constant", null, null, null),
                new NodeDefinition("sink", "test.boolSink", null, null, null)
            ],
            [new EdgeDefinition("e1", "source", "value", "sink", "value")]);

        var ex = Assert.Throws<InvalidOperationException>(() => compiler.Compile(workflow));
        Assert.Contains("type mismatch", ex.Message.ToLowerInvariant());
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();

        var registry = new VisionNodeRegistry();
        registry.Register(ConstantExecutor.Catalog, new ConstantExecutor(), "test");
        registry.Register(AddExecutor.Catalog, new AddExecutor(), "test");
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

    private sealed class ConstantExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.constant";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Constant", "Test", [], [new PortDescriptor("value", VisionDataType.Double)], []);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(2) },
                new Dictionary<string, object?> { ["value"] = 2d }));
    }

    private sealed class AddExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.add";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Add", "Test", [new PortDescriptor("value", VisionDataType.Double)], [new PortDescriptor("value", VisionDataType.Double)],
            [new ParameterDescriptor("increment", "Increment", "number", 0)]);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var result = context.RequireNumber("value") + node.GetDouble("increment", 0);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(result) },
                new Dictionary<string, object?> { ["result"] = result }));
        }
    }

    private sealed class BooleanSinkExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.boolSink";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Boolean Sink", "Test", [new PortDescriptor("value", VisionDataType.Boolean)], [], []);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
