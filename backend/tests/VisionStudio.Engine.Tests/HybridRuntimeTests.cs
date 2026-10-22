using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class HybridRuntimeTests
{
    [Fact]
    public void Compiler_FusesContiguousPipelineNodesAcrossCoarseBoundaries()
    {
        var registry = BuildRegistry();
        var compiler = new VisionWorkflowCompiler(registry);
        var compiled = compiler.Compile(CreateWorkflow());

        Assert.Equal(2, compiled.PipelineSegments.Count);
        Assert.Contains(compiled.PipelineSegments.Values, x => x.SequenceEqual(["p1", "p2"]));
        Assert.Contains(compiled.PipelineSegments.Values, x => x.SequenceEqual(["p3"]));

        using var doc = JsonDocument.Parse(compiled.DslJson);
        var steps = doc.RootElement.GetProperty("Steps").EnumerateArray().ToArray();
        Assert.Equal(3, steps.Length);
        Assert.Contains("VisionPipelineStep", steps[0].GetProperty("StepType").GetString());
        Assert.Contains("VisionNodeStep", steps[1].GetProperty("StepType").GetString());
        Assert.Contains("VisionPipelineStep", steps[2].GetProperty("StepType").GetString());
        Assert.Equal("c1", steps[0].GetProperty("NextStepId").GetString());
        Assert.Equal(steps[2].GetProperty("Id").GetString(), steps[1].GetProperty("NextStepId").GetString());
    }

    [Fact]
    public async Task Runner_PreservesNodeReportsAndTypedDataInsideFusedSegments()
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(CreateWorkflow());

        Assert.True(result.Success, result.Error);
        Assert.Equal(4, result.NodeReports.Count);
        var final = Assert.Single(result.NodeReports.Where(x => x.NodeId == "p3"));
        Assert.Equal(25d, Assert.IsType<double>(final.Summary["value"]));
    }

    [Fact]
    public async Task RunNode_CanHaltInsideOneFusedPipelineSegment()
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(CreateWorkflow(), new VisionRunOptions(DebugRunMode.RunNode, "p2"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("p2", result.HaltNodeId);
        Assert.Equal(2, result.NodeReports.Count);
        Assert.Equal(NodeExecutionPhase.Warmup, result.NodeReports.Single(x => x.NodeId == "p1").Phase);
        Assert.Equal(NodeExecutionPhase.Run, result.NodeReports.Single(x => x.NodeId == "p2").Phase);
        Assert.DoesNotContain(result.NodeReports, x => x.NodeId == "c1" || x.NodeId == "p3");
    }

    [Fact]
    public async Task Breakpoint_CanStopBeforeNodeInsideFusedPipelineSegment()
    {
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var result = await runner.RunAsync(CreateWorkflow(), new VisionRunOptions(DebugRunMode.Breakpoints, Breakpoints: ["p2"]));

        Assert.True(result.Success, result.Error);
        Assert.Equal("Breakpoint", result.DebugState);
        Assert.Equal("p2", result.HaltNodeId);
        Assert.Single(result.NodeReports);
        Assert.Equal("p1", result.NodeReports[0].NodeId);
    }

    private static WorkflowDefinition CreateWorkflow() => new(
        "hybrid-runtime",
        "Hybrid Runtime",
        [
            new NodeDefinition("p1", SourceExecutor.NodeType, null, null, null),
            new NodeDefinition("p2", AddOneExecutor.NodeType, null, null, null),
            new NodeDefinition("c1", CoarseMultiplyExecutor.NodeType, null, null, null),
            new NodeDefinition("p3", AddFiveExecutor.NodeType, null, null, null)
        ],
        [
            new EdgeDefinition("c12", "p1", "next", "p2", "exec", "control"),
            new EdgeDefinition("c23", "p2", "next", "c1", "exec", "control"),
            new EdgeDefinition("c34", "c1", "next", "p3", "exec", "control"),
            new EdgeDefinition("d12", "p1", "value", "p2", "value"),
            new EdgeDefinition("d23", "p2", "value", "c1", "value"),
            new EdgeDefinition("d34", "c1", "value", "p3", "value")
        ]);

    private static VisionNodeRegistry BuildRegistry()
    {
        var registry = new VisionNodeRegistry();
        registry.Register(SourceExecutor.Catalog, new SourceExecutor(), "test");
        registry.Register(AddOneExecutor.Catalog, new AddOneExecutor(), "test");
        registry.Register(CoarseMultiplyExecutor.Catalog, new CoarseMultiplyExecutor(), "test");
        registry.Register(AddFiveExecutor.Catalog, new AddFiveExecutor(), "test");
        return registry;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        services.AddSingleton(BuildRegistry());
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

    private static NodeCatalogItem Catalog(string type, bool source, VisionExecutionMode mode) => new(
        type,
        type,
        "Test",
        source
            ? []
            : [new PortDescriptor("exec", VisionDataType.Control), new PortDescriptor("value", VisionDataType.Double)],
        [new PortDescriptor("value", VisionDataType.Double), new PortDescriptor("next", VisionDataType.Control)],
        [],
        PluginId: "test",
        Capabilities: new VisionToolCapabilities(ExecutionMode: mode));

    private sealed class SourceExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.pipeline.source";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = HybridRuntimeTests.Catalog(NodeType, true, VisionExecutionMode.Pipeline);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(Result(1));
    }

    private sealed class AddOneExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.pipeline.add1";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = HybridRuntimeTests.Catalog(NodeType, false, VisionExecutionMode.Pipeline);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(Result(context.RequireNumber("value") + 1));
    }

    private sealed class CoarseMultiplyExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.coarse.multiply";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = HybridRuntimeTests.Catalog(NodeType, false, VisionExecutionMode.WorkflowCore);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(Result(context.RequireNumber("value") * 10));
    }

    private sealed class AddFiveExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.pipeline.add5";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = HybridRuntimeTests.Catalog(NodeType, false, VisionExecutionMode.Pipeline);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(Result(context.RequireNumber("value") + 5));
    }

    private static NodeExecutorResult Result(double value) => new(
        new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(value) },
        new Dictionary<string, object?> { ["value"] = value });
}
