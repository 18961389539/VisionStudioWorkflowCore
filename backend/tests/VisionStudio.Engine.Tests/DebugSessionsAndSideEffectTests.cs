using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Debug side-effect guard (SupportsRunNode wiring) and stateful debug sessions:
/// start halts at the first breakpoint, continue skips completed nodes without re-running
/// predecessors (cached outputs feed downstream inputs), and run-node executes a single node
/// from cached inputs while the halt is preserved.
/// </summary>
public sealed class DebugSessionsAndSideEffectTests
{
    [Fact]
    public async Task RunNode_ImplicitPrefixWithSideEffectNode_IsRejected_UnlessAllowed()
    {
        EffectExecutor.Reset();
        ProduceExecutor.Reset();
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = EffectThenProduceWorkflow("guard-runnode");

        var blocked = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.RunNode, "compute"));

        Assert.False(blocked.Success);
        Assert.Contains("external side effects", blocked.Error);
        Assert.Contains("AllowSideEffects", blocked.Error);
        Assert.Equal(0, EffectExecutor.Invocations);

        var allowed = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.RunNode, "compute", [], AllowSideEffects: true));

        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(1, EffectExecutor.Invocations);
        Assert.Equal(1, ProduceExecutor.Count("compute"));
    }

    [Fact]
    public async Task RunNode_OnSideEffectTargetItself_IsAlwaysAllowed()
    {
        EffectExecutor.Reset();
        ProduceExecutor.Reset();
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = new WorkflowDefinition(
            "guard-target",
            "Guard Target",
            [new NodeDefinition("effect", EffectExecutor.NodeType, "effect", null, null)],
            []);

        var result = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.RunNode, "effect"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, EffectExecutor.Invocations);
    }

    [Fact]
    public async Task BreakpointsWithSideEffectNode_AreRejected_UnlessAllowed()
    {
        EffectExecutor.Reset();
        ProduceExecutor.Reset();
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = EffectThenProduceWorkflow("guard-breakpoints");

        var blocked = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.Breakpoints, null, ["compute"]));
        Assert.False(blocked.Success);
        Assert.Contains("external side effects", blocked.Error);
        Assert.Equal(0, EffectExecutor.Invocations);

        var allowed = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.Breakpoints, null, ["compute"], AllowSideEffects: true));
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal("Breakpoint", allowed.DebugState);
        Assert.Equal("compute", allowed.HaltNodeId);
    }

    [Fact]
    public async Task RunFromNode_PrefixGuard_IsRejected_UnlessAllowed()
    {
        EffectExecutor.Reset();
        ProduceExecutor.Reset();
        await using var provider = BuildProvider();
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();
        var workflow = EffectThenProduceWorkflow("guard-runfrom");

        var blocked = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.RunFromNode, "compute"));
        Assert.False(blocked.Success);
        Assert.Contains("external side effects", blocked.Error);

        var allowed = await runner.RunAsync(workflow, new VisionRunOptions(DebugRunMode.RunFromNode, "compute", [], AllowSideEffects: true));
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(1, EffectExecutor.Invocations);
    }

    [Fact]
    public async Task Session_Continue_ResumesFromHalt_WithoutReRunningPredecessors()
    {
        ProduceExecutor.Reset();
        await using var provider = BuildProvider();
        var sessions = provider.GetRequiredService<WorkflowDebugSessionService>();
        var workflow = ProduceChain("session-resume", ("a", 1), ("b", 10), ("c", 100));

        var (session, seg1) = await sessions.StartAsync(workflow, new VisionRunOptions(DebugRunMode.Breakpoints, null, ["b", "c"]));

        Assert.Equal("Breakpoint", seg1.DebugState);
        Assert.Equal("b", seg1.HaltNodeId);
        Assert.Single(seg1.NodeReports);
        Assert.Equal(1, ProduceExecutor.Count("a"));

        var seg2 = await sessions.ContinueAsync(session, default);

        Assert.Equal("Breakpoint", seg2.DebugState);
        Assert.Equal("c", seg2.HaltNodeId);
        Assert.Equal(2, seg2.NodeReports.Count);
        Assert.Equal(1, ProduceExecutor.Count("a"));
        Assert.Equal(11, ProduceExecutor.Value("b")); // input came from a's cached output

        var seg3 = await sessions.ContinueAsync(session, default);

        Assert.Equal("Complete", seg3.DebugState);
        Assert.Equal(3, seg3.NodeReports.Count);
        Assert.Equal(111, ProduceExecutor.Value("c")); // input came from b's cached output
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Equal(1, ProduceExecutor.Count(id));

        session.Dispose();
    }

    [Fact]
    public async Task Session_RunNodeWithCachedInputs_ExecutesSingleNode_ThenContinueSkipsIt()
    {
        ProduceExecutor.Reset();
        await using var provider = BuildProvider();
        var sessions = provider.GetRequiredService<WorkflowDebugSessionService>();
        var workflow = ProduceChain("session-run-node", ("a", 1), ("b", 10), ("c", 100));

        var (session, seg1) = await sessions.StartAsync(workflow, new VisionRunOptions(DebugRunMode.Breakpoints, null, ["c"]));
        Assert.Equal("c", seg1.HaltNodeId);
        Assert.Equal(1, ProduceExecutor.Count("a"));
        Assert.Equal(1, ProduceExecutor.Count("b"));

        var nodeRun = await sessions.RunNodeWithCachedInputsAsync(session, "c", default);

        Assert.True(nodeRun.Success, nodeRun.Error);
        Assert.Equal(111, ProduceExecutor.Value("c")); // input came from b's cached output
        Assert.Equal(3, nodeRun.NodeReports.Count); // a, b (segment) + c (node run)
        Assert.Equal("Breakpoint", nodeRun.DebugState); // halt state is preserved
        Assert.Equal("c", nodeRun.HaltNodeId);
        Assert.Equal("Halted", session.Snapshot().State);
        Assert.Equal(1, ProduceExecutor.Count("a"));
        Assert.Equal(1, ProduceExecutor.Count("b"));

        var seg2 = await sessions.ContinueAsync(session, default);

        Assert.Equal("Complete", seg2.DebugState);
        Assert.Equal(1, ProduceExecutor.Count("c")); // already executed via run-node, not re-run

        session.Dispose();
    }

    [Fact]
    public async Task Session_Start_WithSideEffectPrefix_IsRejected_UnlessAllowed()
    {
        EffectExecutor.Reset();
        await using var provider = BuildProvider();
        var sessions = provider.GetRequiredService<WorkflowDebugSessionService>();
        var workflow = EffectThenProduceWorkflow("session-guard");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sessions.StartAsync(workflow, new VisionRunOptions(DebugRunMode.Breakpoints, null, ["compute"])));
        Assert.Contains("external side effects", ex.Message);
        Assert.Equal(0, EffectExecutor.Invocations);

        var (session, result) = await sessions.StartAsync(
            workflow,
            new VisionRunOptions(DebugRunMode.Breakpoints, null, ["compute"], AllowSideEffects: true));
        Assert.Equal("Breakpoint", result.DebugState);
        Assert.Equal("compute", result.HaltNodeId);
        Assert.Equal(1, EffectExecutor.Invocations);
        session.Dispose();
    }

    [Fact]
    public async Task Session_RequiresBreakpointsMode()
    {
        await using var provider = BuildProvider();
        var sessions = provider.GetRequiredService<WorkflowDebugSessionService>();
        var workflow = ProduceChain("session-mode", ("a", 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sessions.StartAsync(workflow, new VisionRunOptions(DebugRunMode.RunNode, "a")));
    }

    private static WorkflowDefinition EffectThenProduceWorkflow(string id)
    {
        var workflow = ProduceChain(id, ("compute", 5));
        var nodes = workflow.Nodes.Prepend(new NodeDefinition("effect", EffectExecutor.NodeType, "effect", null, null)).ToArray();
        var edges = workflow.Edges.Prepend(new EdgeDefinition("ce", "effect", "next", "compute", "exec", "control")).ToArray();
        return workflow with { Nodes = nodes, Edges = edges };
    }

    private static WorkflowDefinition ProduceChain(string id, params (string NodeId, double Increment)[] nodes)
    {
        var definitions = nodes
            .Select(x => new NodeDefinition(x.NodeId, ProduceExecutor.NodeType, x.NodeId, null, new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["increment"] = System.Text.Json.JsonSerializer.SerializeToElement(x.Increment)
            }))
            .ToArray();
        var edges = new List<VisionStudio.Abstractions.EdgeDefinition>();
        for (var i = 0; i < nodes.Length - 1; i++)
            edges.Add(new VisionStudio.Abstractions.EdgeDefinition($"c{i}", nodes[i].NodeId, "next", nodes[i + 1].NodeId, "exec", "control"));
        for (var i = 1; i < nodes.Length; i++)
            edges.Add(new VisionStudio.Abstractions.EdgeDefinition($"d{i}", nodes[i - 1].NodeId, "value", nodes[i].NodeId, "value"));
        return new VisionStudio.Abstractions.WorkflowDefinition(id, "Debug Chain", definitions, edges);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();

        var registry = new VisionNodeRegistry();
        registry.Register(ProduceExecutor.Catalog, new ProduceExecutor(), "test");
        registry.Register(EffectExecutor.Catalog, new EffectExecutor(), "test");
        services.AddSingleton(registry);
        services.AddSingleton<WorkflowPlanCache>();
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        services.AddSingleton<WorkflowDebugSessionService>();
        return services.BuildServiceProvider();
    }

    private sealed class ProduceExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.produce";
        public string Type => NodeType;
        public static readonly ConcurrentQueue<(string Node, double Value)> Produces = new();

        public static void Reset()
        {
            while (Produces.TryDequeue(out _)) { }
        }

        public static int Count(string nodeId) => Produces.Count(x => x.Node == nodeId);

        public static double Value(string nodeId) => Produces.Last(x => x.Node == nodeId).Value;

        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Produce", "Test",
            [new PortDescriptor("exec", VisionDataType.Control), new PortDescriptor("value", VisionDataType.Double, Required: false)],
            [new PortDescriptor("value", VisionDataType.Double), new PortDescriptor("next", VisionDataType.Control)],
            [new ParameterDescriptor("increment", "Increment", "number", 1)]);

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var input = context.TryGetValue("value", out var incoming) && incoming.Value is not null
                ? Convert.ToDouble(incoming.Value)
                : 0d;
            var result = input + node.GetDouble("increment", 0);
            Produces.Enqueue((node.Id, result));
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(result) },
                new Dictionary<string, object?> { ["value"] = result }));
        }
    }

    private sealed class EffectExecutor : IVisionNodeExecutor
    {
        public const string NodeType = "test.effect";
        public string Type => NodeType;
        public static int Invocations;

        public static void Reset() => Invocations = 0;

        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Effect", "Test",
            [new PortDescriptor("exec", VisionDataType.Control)],
            [new PortDescriptor("next", VisionDataType.Control)],
            [],
            Capabilities: new VisionToolCapabilities(SupportsRunNode: false));

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Invocations);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>(),
                new Dictionary<string, object?> { ["executed"] = node.Id }));
        }
    }
}
