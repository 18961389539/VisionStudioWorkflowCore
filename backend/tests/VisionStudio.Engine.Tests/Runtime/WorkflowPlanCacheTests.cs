using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Engine.Runtime;
using WorkflowCore.Interface;

namespace VisionStudio.Engine.Tests;

public sealed class WorkflowPlanCacheTests
{
    [Fact]
    public async Task ReusesPlanAcrossConcurrentRunsAndDesignerEdits()
    {
        using var provider = Provider(4);
        var cache = provider.GetRequiredService<WorkflowPlanCache>();
        var workflow = Workflow(1);
        var moved = workflow with { Name = "Renamed", Nodes = [workflow.Nodes[0] with { Name = "Moved", Position = new(123, 456) }] };
        var leases = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => cache.AcquireAsync(moved))));
        using var original = await cache.AcquireAsync(workflow);
        Assert.All(leases, x => Assert.Same(original.Plan, x.Plan));
        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(1, cache.CompilationCount);
        Assert.Single(provider.GetRequiredService<IWorkflowRegistry>().GetAllDefinitions());
    }

    [Fact]
    public async Task ParameterSweepBoundsCompiledAndRegisteredDefinitions()
    {
        using var provider = Provider(4);
        var cache = provider.GetRequiredService<WorkflowPlanCache>();
        var registry = provider.GetRequiredService<IWorkflowRegistry>();
        string? first = null;
        for (var i = 0; i < 200; i++)
        {
            using var lease = await cache.AcquireAsync(Workflow(i));
            first ??= lease.Plan.WorkflowCoreId;
            Assert.InRange(cache.Count, 1, 4);
            Assert.InRange(registry.GetAllDefinitions().Count(), 1, 4);
        }
        Assert.False(registry.IsRegistered(first!, 1));
        Assert.Equal(200, cache.CompilationCount);
    }

    [Fact]
    public async Task EvictionKeepsRecentlyUsedDefinitions()
    {
        using var provider = Provider(2);
        var cache = provider.GetRequiredService<WorkflowPlanCache>();
        string firstId, secondId;
        using (var first = await cache.AcquireAsync(Workflow(1))) firstId = first.Plan.WorkflowCoreId;
        using (var second = await cache.AcquireAsync(Workflow(2))) secondId = second.Plan.WorkflowCoreId;
        using (var recent = await cache.AcquireAsync(Workflow(1))) { }
        using var third = await cache.AcquireAsync(Workflow(3));
        var registry = provider.GetRequiredService<IWorkflowRegistry>();
        Assert.True(registry.IsRegistered(firstId, 1));
        Assert.False(registry.IsRegistered(secondId, 1));
    }

    [Fact]
    public async Task PinnedDefinitionCannotBeEvictedAndCapacityWaitIsCancellable()
    {
        using var provider = Provider(1);
        var cache = provider.GetRequiredService<WorkflowPlanCache>();
        var registry = provider.GetRequiredService<IWorkflowRegistry>();
        var active = await cache.AcquireAsync(Workflow(1));
        var pending = cache.AcquireAsync(Workflow(2));
        Assert.False(pending.IsCompleted);
        Assert.True(registry.IsRegistered(active.Plan.WorkflowCoreId, 1));
        using var cts = new CancellationTokenSource();
        var cancelled = cache.AcquireAsync(Workflow(3), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        active.Dispose();
        using var next = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(registry.IsRegistered(active.Plan.WorkflowCoreId, 1));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task CatalogChangesInvalidatePlansAndSanitizedIdsDoNotAlias()
    {
        using var provider = Provider(4);
        var cache = provider.GetRequiredService<WorkflowPlanCache>();
        using var a = await cache.AcquireAsync(Workflow(1) with { Id = "a/b" });
        using var b = await cache.AcquireAsync(Workflow(1) with { Id = "a?b" });
        Assert.NotEqual(a.Plan.WorkflowCoreId, b.Plan.WorkflowCoreId);
        var registry = provider.GetRequiredService<VisionNodeRegistry>();
        registry.Register(TestExecutor.Catalog with { Type = "test.extra" }, new TestExecutor("test.extra"), "extra");
        using var updated = await cache.AcquireAsync(Workflow(1) with { Id = "a/b" });
        Assert.NotEqual(a.Plan.WorkflowCoreId, updated.Plan.WorkflowCoreId);
    }

    private static ServiceProvider Provider(int capacity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflow();
        services.AddWorkflowDSL();
        var nodes = new VisionNodeRegistry();
        nodes.Register(TestExecutor.Catalog, new TestExecutor(), "test");
        services.AddSingleton(nodes);
        services.AddSingleton<VisionWorkflowCompiler>();
        services.AddSingleton(sp => new WorkflowPlanCache(sp.GetRequiredService<VisionWorkflowCompiler>(),
            sp.GetRequiredService<IDefinitionLoader>(), sp.GetRequiredService<IWorkflowRegistry>(), nodes, capacity));
        return services.BuildServiceProvider();
    }

    private static WorkflowDefinition Workflow(int parameter) => new("cache", "Cache",
        [new("n", "test.cache", "Node", null, new() { ["value"] = JsonSerializer.SerializeToElement(parameter) })], []);

    private sealed class TestExecutor(string type = "test.cache") : IVisionNodeExecutor
    {
        public string Type => type;
        public static NodeCatalogItem Catalog { get; } = new("test.cache", "Cache", "Test", [], [], []);
        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken ct)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
