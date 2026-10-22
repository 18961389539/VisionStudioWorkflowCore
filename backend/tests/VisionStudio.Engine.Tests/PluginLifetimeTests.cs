using System.Collections.Concurrent;

namespace VisionStudio.Engine.Tests;

public sealed class PluginLifetimeTests
{
    [Fact]
    public async Task SingletonFactory_IsCreatedOnceAndReused()
    {
        CountingExecutor.Reset();
        using var registry = new VisionNodeRegistry();
        registry.RegisterFactory(
            Catalog("test.singleton"),
            () => new CountingExecutor("test.singleton"),
            "test",
            VisionExecutorLifetime.Singleton,
            VisionExecutorConcurrency.ThreadSafe);

        var registration = registry.Require("test.singleton");
        await registration.ExecuteAsync(EmptyContext(), Node("test.singleton"), CancellationToken.None);
        await registration.ExecuteAsync(EmptyContext(), Node("test.singleton"), CancellationToken.None);

        Assert.Equal(1, CountingExecutor.Created);
        Assert.Equal(2, CountingExecutor.Executed);
    }

    [Fact]
    public async Task TransientFactory_CreatesAndDisposesPerExecution()
    {
        CountingExecutor.Reset();
        using var registry = new VisionNodeRegistry();
        registry.RegisterFactory(
            Catalog("test.transient"),
            () => new CountingExecutor("test.transient"),
            "test",
            VisionExecutorLifetime.Transient,
            VisionExecutorConcurrency.ThreadSafe);

        // Registration performs one disposable validation probe; each run gets a fresh executor.
        Assert.Equal(1, CountingExecutor.Created);
        Assert.Equal(1, CountingExecutor.Disposed);

        var registration = registry.Require("test.transient");
        await registration.ExecuteAsync(EmptyContext(), Node("test.transient"), CancellationToken.None);
        await registration.ExecuteAsync(EmptyContext(), Node("test.transient"), CancellationToken.None);

        Assert.Equal(3, CountingExecutor.Created);
        Assert.Equal(2, CountingExecutor.Executed);
        Assert.Equal(3, CountingExecutor.Disposed);
    }

    [Fact]
    public async Task SerializedConcurrency_ProtectsNonReentrantExecutor()
    {
        ConcurrentExecutor.Reset();
        using var registry = new VisionNodeRegistry();
        registry.RegisterFactory(
            Catalog("test.serialized"),
            () => new ConcurrentExecutor("test.serialized"),
            "test",
            VisionExecutorLifetime.Singleton,
            VisionExecutorConcurrency.Serialized);

        var registration = registry.Require("test.serialized");
        await Task.WhenAll(
            registration.ExecuteAsync(EmptyContext(), Node("test.serialized"), CancellationToken.None).AsTask(),
            registration.ExecuteAsync(EmptyContext(), Node("test.serialized"), CancellationToken.None).AsTask(),
            registration.ExecuteAsync(EmptyContext(), Node("test.serialized"), CancellationToken.None).AsTask());

        Assert.Equal(1, ConcurrentExecutor.MaxConcurrent);
    }

    [Fact]
    public void SupportsParallelFalse_OverridesUnsafeThreadSafeRegistrationRequest()
    {
        using var registry = new VisionNodeRegistry();
        var catalog = Catalog("test.no-parallel") with
        {
            Capabilities = new VisionToolCapabilities(SupportsParallel: false)
        };
        registry.RegisterFactory(
            catalog,
            () => new CountingExecutor("test.no-parallel"),
            "test",
            VisionExecutorLifetime.Singleton,
            VisionExecutorConcurrency.ThreadSafe);

        Assert.Equal(VisionExecutorConcurrency.Serialized, registry.Require("test.no-parallel").Concurrency);
    }

    [Fact]
    public async Task PerNodeFactory_ReusesOneExecutorPerConfiguredNodeId()
    {
        CountingExecutor.Reset();
        var registry = new VisionNodeRegistry();
        registry.RegisterFactory(
            Catalog("test.per-node"),
            () => new CountingExecutor("test.per-node"),
            "test",
            VisionExecutorLifetime.PerNode,
            VisionExecutorConcurrency.ThreadSafe);

        // Registration probe is disposed; then n1 and n2 each own exactly one cached executor.
        Assert.Equal(1, CountingExecutor.Created);
        Assert.Equal(1, CountingExecutor.Disposed);
        var registration = registry.Require("test.per-node");
        await registration.ExecuteAsync(EmptyContext(), new NodeDefinition("n1", "test.per-node", null, null, null), CancellationToken.None);
        await registration.ExecuteAsync(EmptyContext(), new NodeDefinition("n1", "test.per-node", null, null, null), CancellationToken.None);
        await registration.ExecuteAsync(EmptyContext(), new NodeDefinition("n2", "test.per-node", null, null, null), CancellationToken.None);

        Assert.Equal(3, CountingExecutor.Created);
        Assert.Equal(3, CountingExecutor.Executed);
        registry.Dispose();
        Assert.Equal(3, CountingExecutor.Disposed);
    }

    [Fact]
    public void SamplePlugin_DeclaresSdk2PerNodeToolIdentityWithoutEngineTypes()
    {
        var plugin = new VisionStudio.Plugin.Sample.SampleMathPlugin();
        var v2 = Assert.IsAssignableFrom<IVisionPluginV2>(plugin);
        var node = Assert.Single(plugin.CreateNodes());

        Assert.True(v2.Compatibility.Supports(VisionPluginSdk.ApiVersion));
        Assert.Equal(VisionExecutorLifetime.PerNode, node.Lifetime);
        Assert.Equal(VisionExecutorConcurrency.ThreadSafe, node.Concurrency);
        Assert.Equal("math.offset", node.Catalog.Type);
        Assert.Equal("2.0.0", node.ToolIdentity?.Version);
        Assert.IsAssignableFrom<IVisionNodeExecutor>(node.Factory());
    }

    [Fact]
    public async Task PerNodeFactory_IsolatesExecutorsPerWorkflowScope()
    {
        CountingExecutor.Reset();
        using var registry = new VisionNodeRegistry();
        registry.RegisterFactory(
            Catalog("test.per-scope"),
            () => new CountingExecutor("test.per-scope"),
            "test",
            VisionExecutorLifetime.PerNode,
            VisionExecutorConcurrency.ThreadSafe);

        Assert.Equal(1, CountingExecutor.Created); // registration probe
        Assert.Equal(1, CountingExecutor.Disposed);
        var registration = registry.Require("test.per-scope");
        var first = Node("test.per-scope");
        var second = Node("test.per-scope");

        await registration.ExecuteAsync(ScopedContext("workflow-a"), first, CancellationToken.None);
        await registration.ExecuteAsync(ScopedContext("workflow-a"), first, CancellationToken.None);
        await registration.ExecuteAsync(ScopedContext("workflow-b"), second, CancellationToken.None);

        // Same scope reuses one instance; two workflows that both use node id n1 never share it.
        Assert.Equal(3, CountingExecutor.Created);
        Assert.Equal(3, CountingExecutor.Executed);
        registry.Dispose();
        Assert.Equal(3, CountingExecutor.Disposed);
    }

    [Fact]
    public async Task PerNodeFactory_DoesNotCacheFactoryFailures()
    {
        CountingExecutor.Reset();
        using var registry = new VisionNodeRegistry();
        var attempts = 0;
        registry.RegisterFactory(
            Catalog("test.flaky"),
            () => Interlocked.Increment(ref attempts) switch
            {
                1 => new CountingExecutor("test.flaky"), // registration probe
                2 => throw new InvalidOperationException("transient executor failure"),
                _ => new CountingExecutor("test.flaky")
            },
            "test",
            VisionExecutorLifetime.PerNode,
            VisionExecutorConcurrency.ThreadSafe);

        var registration = registry.Require("test.flaky");
        var node = Node("test.flaky");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            await registration.ExecuteAsync(EmptyContext(), node, CancellationToken.None));
        Assert.Equal(2, attempts);

        // The faulted cache entry was evicted, so the next execution retries instead of replaying the cached exception.
        await registration.ExecuteAsync(EmptyContext(), node, CancellationToken.None);
        Assert.Equal(3, attempts);
        Assert.Equal(2, CountingExecutor.Created);
        Assert.Equal(1, CountingExecutor.Executed);
    }

    [Fact]
    public async Task ReleaseScope_DisposesOnlyThatScopesInstances()
    {
        CountingExecutor.Reset();
        using var registry = new VisionNodeRegistry();
        registry.RegisterFactory(
            Catalog("test.scope-release"),
            () => new CountingExecutor("test.scope-release"),
            "test",
            VisionExecutorLifetime.PerNode,
            VisionExecutorConcurrency.Serialized);

        var registration = registry.Require("test.scope-release");
        var node = Node("test.scope-release");
        await registration.ExecuteAsync(ScopedContext("scope-a"), node, CancellationToken.None);
        await registration.ExecuteAsync(ScopedContext("scope-b"), node, CancellationToken.None);
        Assert.Equal(3, CountingExecutor.Created); // probe + one instance per scope

        Assert.Equal(1, registry.ReleaseScope("scope-a"));
        Assert.Equal(2, CountingExecutor.Disposed); // probe + scope-a instance
        Assert.Equal(0, registry.ReleaseScope("scope-a")); // idempotent

        // scope-b still owns its instance; a released scope gets a fresh instance if it runs again.
        await registration.ExecuteAsync(ScopedContext("scope-b"), node, CancellationToken.None);
        Assert.Equal(3, CountingExecutor.Created);
        await registration.ExecuteAsync(ScopedContext("scope-a"), node, CancellationToken.None);
        Assert.Equal(4, CountingExecutor.Created);
        Assert.Equal(1, registry.ReleaseScope("scope-b"));
        Assert.Equal(3, CountingExecutor.Disposed);
    }

    private static NodeCatalogItem Catalog(string type) => new(
        type, type, "Test", [], [], [], Capabilities: new VisionToolCapabilities());

    private static NodeDefinition Node(string type) => new("n1", type, null, null, null);
    private static NodeExecutionContext EmptyContext() => new(new Dictionary<string, VisionValue>());
    private static NodeExecutionContext ScopedContext(string workflowScope) => new(new Dictionary<string, VisionValue>(), workflowScope);

    private sealed class CountingExecutor(string type) : IVisionNodeExecutor, IDisposable
    {
        private static int _created;
        private static int _executed;
        private static int _disposed;
        public static int Created => Volatile.Read(ref _created);
        public static int Executed => Volatile.Read(ref _executed);
        public static int Disposed => Volatile.Read(ref _disposed);
        public string Type { get; } = type;

        // Every constructed instance increments the creation counter.
        private readonly int _creationMarker = Interlocked.Increment(ref _created);

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executed);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>(),
                new Dictionary<string, object?>()));
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);

        public static void Reset()
        {
            Volatile.Write(ref _created, 0);
            Volatile.Write(ref _executed, 0);
            Volatile.Write(ref _disposed, 0);
        }
    }

    private sealed class ConcurrentExecutor(string type) : IVisionNodeExecutor
    {
        private static int _current;
        private static int _max;
        public static int MaxConcurrent => Volatile.Read(ref _max);
        public string Type { get; } = type;

        public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _current);
            UpdateMax(current);
            try
            {
                await Task.Delay(20, cancellationToken);
                return new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>());
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private static void UpdateMax(int value)
        {
            while (true)
            {
                var snapshot = Volatile.Read(ref _max);
                if (snapshot >= value) return;
                if (Interlocked.CompareExchange(ref _max, value, snapshot) == snapshot) return;
            }
        }

        public static void Reset()
        {
            Volatile.Write(ref _current, 0);
            Volatile.Write(ref _max, 0);
        }
    }
}
