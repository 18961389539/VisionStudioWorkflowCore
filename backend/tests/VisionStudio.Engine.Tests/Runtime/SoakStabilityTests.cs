using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

[Trait("Category", "Soak")]
public sealed class SoakStabilityTests
{
    [Fact]
    public void CoordinateRoundTrip_RemainsFiniteAcrossManyTransforms()
    {
        var iterations = ReadIterations("VISION_SOAK_MATH_ITERATIONS", 10000, 100, 1000000);
        var forward = VisionTransform2DMath.Rigid("Workpiece", "RobotBase", "mm", 517.2, -81.4, 17.3);
        var inverse = VisionTransform2DMath.Inverse(forward);
        var p = new VisionCoordinatePoint2D(22.4, 13.7, "Workpiece", "mm");

        for (var i = 0; i < iterations; i++)
        {
            var robot = VisionTransform2DMath.Apply(forward, p);
            p = VisionTransform2DMath.Apply(inverse, robot);
            Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y));
        }
        Assert.InRange(Math.Abs(p.X - 22.4), 0, 1e-7);
        Assert.InRange(Math.Abs(p.Y - 13.7), 0, 1e-7);
    }

    [Fact]
    public async Task RobotHandshake_RepeatedCyclesAlwaysReturnToReady()
    {
        var iterations = ReadIterations("VISION_SOAK_ROBOT_ITERATIONS", 25, 1, 500);
        await using var manager = new RobotManager();
        var adapter = new VirtualAbbRobotAdapter();
        manager.Register(adapter);
        await manager.ConnectAsync(adapter.Id);
        await manager.ApplySettingsAsync(adapter.Id, new RobotRuntimeSettings(1200, 540, 0.05, 0.05));

        for (var i = 0; i < iterations; i++)
        {
            var x = 520 + (i % 5) * 2.0;
            var y = 240 + (i % 3) * 2.0;
            var r = 28 + (i % 7) * 0.5;
            var result = await manager.ExecuteHandshakeAsync(
                adapter.Id,
                new VisionRobotTarget2D(x, y, r, "RobotBase", "mm", "ABB", "Soak"),
                new RobotCommandPolicy(TimeoutMs: 2500, MaxRetries: 0, RetryDelayMs: 0, AutoConnect: true, WaitForComplete: true, AutoAck: true));
            Assert.True(result.Completed);
            Assert.True(result.Acknowledged);
            Assert.Equal(RobotHandshakeState.Ready, manager.Get(adapter.Id).HandshakeState);
        }
    }

    [Fact]
    public async Task WorkflowCore_RepeatedExecutionsRemainSuccessful()
    {
        var iterations = ReadIterations("VISION_SOAK_WORKFLOW_ITERATIONS", 100, 1, 5000);
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = new WorkflowDefinition(
            "soak-workflow",
            "Soak Workflow",
            [new NodeDefinition("source", SoakSource.NodeType, "Source", null, null), new NodeDefinition("sink", SoakSink.NodeType, "Sink", null, null)],
            [new EdgeDefinition("e1", "source", "value", "sink", "value")]);

        for (var i = 0; i < iterations; i++)
        {
            var result = await runner.RunAsync(workflow);
            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.NodeReports.Count);
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        var registry = new VisionNodeRegistry();
        registry.Register(SoakSource.Catalog, new SoakSource(), "soak");
        registry.Register(SoakSink.Catalog, new SoakSink(), "soak");
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

    private static int ReadIterations(string name, int fallback, int min, int max)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? Math.Clamp(value, min, max) : fallback;

    private sealed class SoakSource : IVisionNodeExecutor
    {
        public const string NodeType = "soak.source";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(NodeType, "Source", "Soak", [], [new PortDescriptor("value", VisionDataType.Double)], []);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(42) }, new Dictionary<string, object?>()));
    }

    private sealed class SoakSink : IVisionNodeExecutor
    {
        public const string NodeType = "soak.sink";
        public string Type => NodeType;
        public static NodeCatalogItem Catalog { get; } = new(NodeType, "Sink", "Soak", [new PortDescriptor("value", VisionDataType.Double)], [], []);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var value = context.RequireNumber("value");
            return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?> { ["value"] = value }));
        }
    }
}
