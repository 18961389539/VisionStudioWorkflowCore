using Microsoft.Extensions.DependencyInjection;
using WorkflowCore.Interface;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// PerNode plugin instances are cached per (workflow plan identity, node id): two workflows that both use
/// node id "n1" must not share a cached instance, the same workflow content reuses its instance, and the
/// instances are released when the workflow's plan leaves the plan cache.
/// </summary>
public sealed class PluginScopeIsolationTests
{
    [Fact]
    public async Task PerNodeInstances_AreIsolatedPerWorkflow_AndReleasedWithTheEvictedPlan()
    {
        ScopedExecutor.Reset();
        await using var provider = BuildProvider(planCacheCapacity: 2);
        var runner = provider.GetRequiredService<WorkflowCoreVisionRunner>();

        var first = await runner.RunAsync(Workflow("wf-a"), new VisionRunOptions(DebugRunMode.Full));
        Assert.True(first.Success, first.Error);
        Assert.Equal(2, ScopedExecutor.Created); // registration probe + wf-a instance

        // Same workflow content reuses the cached instance across runs.
        await runner.RunAsync(Workflow("wf-a"), new VisionRunOptions(DebugRunMode.Full));
        Assert.Equal(2, ScopedExecutor.Created);

        // Another workflow that reuses node id "n1" gets its own instance instead of sharing wf-a state.
        await runner.RunAsync(Workflow("wf-b"), new VisionRunOptions(DebugRunMode.Full));
        Assert.Equal(3, ScopedExecutor.Created);

        // Capacity 2: the third workflow evicts the least recently used plan (wf-a) and releases only its instances.
        await runner.RunAsync(Workflow("wf-c"), new VisionRunOptions(DebugRunMode.Full));
        Assert.Equal(4, ScopedExecutor.Created);
        Assert.Equal(2, ScopedExecutor.Disposed); // probe + wf-a instance

        // wf-b is still cached: re-running it must not create a new instance.
        await runner.RunAsync(Workflow("wf-b"), new VisionRunOptions(DebugRunMode.Full));
        Assert.Equal(4, ScopedExecutor.Created);
        Assert.Equal(2, ScopedExecutor.Disposed);
    }

    private static WorkflowDefinition Workflow(string id) => new(
        id,
        "Scope Isolation",
        [new NodeDefinition("n1", ScopedExecutor.NodeType, "n1", null, null)],
        []);

    private static ServiceProvider BuildProvider(int planCacheCapacity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();

        var registry = new VisionNodeRegistry();
        registry.RegisterFactory(
            ScopedExecutor.Catalog,
            () => new ScopedExecutor(),
            "test",
            VisionExecutorLifetime.PerNode,
            VisionExecutorConcurrency.ThreadSafe);

        services.AddSingleton(registry);
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddSingleton(sp => new WorkflowPlanCache(
            sp.GetRequiredService<VisionWorkflowCompiler>(),
            sp.GetRequiredService<IDefinitionLoader>(),
            sp.GetRequiredService<IWorkflowRegistry>(),
            registry,
            planCacheCapacity));
        services.AddSingleton<VisionNodeDispatcher>();
        services.AddSingleton<VisionNodeRuntime>();
        services.AddSingleton<VisionPipelineExecutor>();
        services.AddTransient<VisionNodeStep>();
        services.AddTransient<VisionPipelineStep>();
        services.AddTransient<WorkflowCoreVisionRunner>();
        return services.BuildServiceProvider();
    }

    private sealed class ScopedExecutor : IVisionNodeExecutor, IDisposable
    {
        public const string NodeType = "test.scope-isolation";
        private static int _created;
        private static int _disposed;

        public static NodeCatalogItem Catalog { get; } = new(
            NodeType, "Scope Isolation", "Test", [], [], [], Capabilities: new VisionToolCapabilities());

        public static int Created => Volatile.Read(ref _created);
        public static int Disposed => Volatile.Read(ref _disposed);

        public static void Reset()
        {
            Volatile.Write(ref _created, 0);
            Volatile.Write(ref _disposed, 0);
        }

        public string Type => NodeType;

        // Every constructed instance increments the creation counter.
        private readonly int _creationMarker = Interlocked.Increment(ref _created);

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>(),
                new Dictionary<string, object?> { ["instance"] = _creationMarker }));

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
}