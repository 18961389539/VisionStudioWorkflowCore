using System.Collections.Concurrent;

namespace VisionStudio.Engine;

/// <summary>
/// Owns instantiation, concurrency enforcement and disposal for one hosted node type.
/// PerNode executors are cached per (workflow plan scope, node id) so two workflows that reuse a node id never
/// share mutable plugin state; entries are released when the workflow's plan leaves the plan cache
/// (<see cref="VisionNodeRegistry.ReleaseScope(string)"/>) or when the whole registration is disposed
/// (plugin rollback / host shutdown). A factory failure is not cached: the faulted entry is evicted so the
/// next execution can retry.
/// </summary>
public sealed class VisionNodeRegistration : IDisposable
{
    /// <summary>Separates the workflow scope from the node id inside one cached instance key.</summary>
    private const char ScopeSeparator = '\u001f';

    private readonly Func<IVisionNodeExecutor> _factory;
    private readonly SemaphoreSlim? _serializedGate;
    private readonly IVisionNodeExecutor? _singleton;
    private readonly bool _ownsSingleton;
    private readonly ConcurrentDictionary<string, Lazy<IVisionNodeExecutor>> _perNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perNodeGates = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    internal VisionNodeRegistration(
        NodeCatalogItem catalog,
        Func<IVisionNodeExecutor> factory,
        string source,
        VisionExecutorLifetime lifetime,
        VisionExecutorConcurrency concurrency,
        IVisionNodeExecutor? singleton,
        bool ownsSingleton,
        VisionPluginToolIdentity? toolIdentity = null)
    {
        Catalog = catalog;
        Source = source;
        Lifetime = lifetime;
        Concurrency = catalog.Capabilities?.SupportsParallel == false
            ? VisionExecutorConcurrency.Serialized
            : concurrency;
        ToolIdentity = toolIdentity;
        _factory = factory;
        _singleton = singleton;
        _ownsSingleton = ownsSingleton;
        // PerNode serialization is scoped to each cacheable node instance (workflow scope + node id), not the whole tool type.
        _serializedGate = Lifetime != VisionExecutorLifetime.PerNode && Concurrency == VisionExecutorConcurrency.Serialized
            ? new SemaphoreSlim(1, 1)
            : null;
    }

    public NodeCatalogItem Catalog { get; }
    public string Source { get; }
    public VisionExecutorLifetime Lifetime { get; }
    public VisionExecutorConcurrency Concurrency { get; }
    public VisionPluginToolIdentity? ToolIdentity { get; }

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var instanceKey = InstanceKey(context.WorkflowScope, node.Id);
        SemaphoreSlim? gate = _serializedGate;
        if (Lifetime == VisionExecutorLifetime.PerNode && Concurrency == VisionExecutorConcurrency.Serialized)
            gate = _perNodeGates.GetOrAdd(instanceKey, static _ => new SemaphoreSlim(1, 1));
        if (gate is not null)
            await gate.WaitAsync(cancellationToken);

        IVisionNodeExecutor? transient = null;
        try
        {
            IVisionNodeExecutor executor;
            if (Lifetime == VisionExecutorLifetime.Singleton)
            {
                executor = _singleton ?? throw new InvalidOperationException($"Singleton executor '{Catalog.Type}' was not initialized.");
            }
            else if (Lifetime == VisionExecutorLifetime.PerNode)
            {
                executor = ResolvePerNodeExecutor(instanceKey);
            }
            else
            {
                transient = CreateValidatedExecutor();
                executor = transient;
            }

            return await executor.ExecuteAsync(context, node, cancellationToken);
        }
        finally
        {
            try
            {
                if (transient is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (transient is IDisposable disposable)
                    disposable.Dispose();
            }
            finally
            {
                gate?.Release();
            }
        }
    }

    private IVisionNodeExecutor ResolvePerNodeExecutor(string instanceKey)
    {
        var lazy = _perNode.GetOrAdd(
            instanceKey,
            _ => new Lazy<IVisionNodeExecutor>(CreateValidatedExecutor, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Lazy caches the factory exception forever; evict the faulted entry so a later execution retries
            // instead of permanently poisoning this node instance until the registration is disposed.
            _perNode.TryRemove(new KeyValuePair<string, Lazy<IVisionNodeExecutor>>(instanceKey, lazy));
            throw;
        }
    }

    private static string InstanceKey(string? workflowScope, string nodeId)
        => string.IsNullOrEmpty(workflowScope)
            ? nodeId
            : string.Concat(workflowScope, ScopeSeparator.ToString(), nodeId);

    /// <summary>
    /// Disposes and forgets every PerNode executor (and serialization gate) cached for one workflow scope.
    /// The caller must guarantee no execution of that scope is in flight - the plan cache only releases a
    /// scope once its plan entry is unleased.
    /// </summary>
    internal int ReleaseScope(string workflowScope)
    {
        if (string.IsNullOrEmpty(workflowScope)) return 0;
        var prefix = workflowScope + ScopeSeparator;
        var released = 0;
        foreach (var pair in _perNode.ToArray())
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_perNode.TryRemove(pair.Key, out var lazy)) continue;
            if (lazy.IsValueCreated) DisposeOwnedExecutor(lazy.Value);
            released++;
        }
        foreach (var pair in _perNodeGates.ToArray())
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (_perNodeGates.TryRemove(pair.Key, out var gate)) gate.Dispose();
        }
        return released;
    }

    private IVisionNodeExecutor CreateValidatedExecutor()
    {
        var executor = _factory() ?? throw new InvalidOperationException($"Factory for '{Catalog.Type}' returned null.");
        if (!executor.Type.Equals(Catalog.Type, StringComparison.OrdinalIgnoreCase))
        {
            DisposeOwnedExecutor(executor);
            throw new InvalidOperationException($"Executor type '{executor.Type}' does not match catalog type '{Catalog.Type}'.");
        }
        return executor;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsSingleton)
            DisposeOwnedExecutor(_singleton);
        foreach (var lazy in _perNode.Values)
        {
            if (lazy.IsValueCreated) DisposeOwnedExecutor(lazy.Value);
        }
        _perNode.Clear();
        foreach (var gate in _perNodeGates.Values) gate.Dispose();
        _perNodeGates.Clear();
        _serializedGate?.Dispose();
    }

    private static void DisposeOwnedExecutor(IVisionNodeExecutor? executor)
    {
        if (executor is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (executor is IDisposable disposable)
            disposable.Dispose();
    }
}

/// <summary>
/// Host runtime registry. Plugins never reference this type: they return factories/lifetime metadata through
/// VisionStudio.Abstractions and the host owns instantiation, concurrency enforcement and disposal.
/// </summary>
public sealed class VisionNodeRegistry : IDisposable
{
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    private readonly ConcurrentDictionary<string, VisionNodeRegistration> _nodes =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<NodeCatalogItem> Catalog => _nodes.Values
        .Select(x => x.Catalog)
        .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<VisionNodeRegistration> Registrations => _nodes.Values
        .OrderBy(x => x.Catalog.Type, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Compatibility path for built-ins/tests already owned by the host DI container.</summary>
    public void Register(NodeCatalogItem catalog, IVisionNodeExecutor executor, string source)
    {
        ValidateType(catalog, executor);
        var concurrency = catalog.Capabilities?.SupportsParallel == false
            ? VisionExecutorConcurrency.Serialized
            : VisionExecutorConcurrency.ThreadSafe;
        Add(catalog, static () => throw new InvalidOperationException("Host-owned singleton factory should not be invoked."),
            source, VisionExecutorLifetime.Singleton, concurrency, executor, ownsSingleton: false, toolIdentity: null);
    }

    public void Register(NodeCatalogItem catalog, VisionPluginNode pluginNode, string source)
    {
        if (!catalog.Type.Equals(pluginNode.Catalog.Type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Host catalog type '{catalog.Type}' does not match plugin node type '{pluginNode.Catalog.Type}'.");
        RegisterFactory(catalog, pluginNode.Factory, source, pluginNode.Lifetime, pluginNode.Concurrency, pluginNode.ToolIdentity);
    }

    public void RegisterFactory(
        NodeCatalogItem catalog,
        Func<IVisionNodeExecutor> factory,
        string source,
        VisionExecutorLifetime lifetime,
        VisionExecutorConcurrency concurrency,
        VisionPluginToolIdentity? toolIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        IVisionNodeExecutor? probe = null;
        try
        {
            probe = factory() ?? throw new InvalidOperationException($"Factory for '{catalog.Type}' returned null.");
            ValidateType(catalog, probe);
            if (lifetime == VisionExecutorLifetime.Singleton)
            {
                Add(catalog, factory, source, lifetime, concurrency, probe, ownsSingleton: true, toolIdentity);
                probe = null;
            }
            else
            {
                // Transient and PerNode retain no probe. PerNode creates its first owned instance lazily for a real node id.
                Add(catalog, factory, source, lifetime, concurrency, singleton: null, ownsSingleton: false, toolIdentity);
            }
        }
        finally
        {
            DisposeExecutorProbe(probe);
        }
    }

    private static void DisposeExecutorProbe(IVisionNodeExecutor? executor)
    {
        if (executor is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (executor is IDisposable disposable)
            disposable.Dispose();
    }

    private void Add(
        NodeCatalogItem catalog,
        Func<IVisionNodeExecutor> factory,
        string source,
        VisionExecutorLifetime lifetime,
        VisionExecutorConcurrency concurrency,
        IVisionNodeExecutor? singleton,
        bool ownsSingleton,
        VisionPluginToolIdentity? toolIdentity)
    {
        var registration = new VisionNodeRegistration(catalog, factory, source, lifetime, concurrency, singleton, ownsSingleton, toolIdentity);
        if (!_nodes.TryAdd(catalog.Type, registration))
        {
            registration.Dispose();
            throw new InvalidOperationException($"Node type '{catalog.Type}' is already registered.");
        }
        Interlocked.Increment(ref _revision);
    }

    private static void ValidateType(NodeCatalogItem catalog, IVisionNodeExecutor executor)
    {
        if (!catalog.Type.Equals(executor.Type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Catalog type '{catalog.Type}' does not match executor type '{executor.Type}'.");
    }

    public bool IsRegistered(string type) => _nodes.ContainsKey(type);

    public bool IsSourceRegistered(string source) => _nodes.Values.Any(x => string.Equals(x.Source, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Removes every node registration contributed by one plugin source. Used only to roll back a partially failed
    /// plugin activation; replacing an already active plugin still requires a host restart in V0.53.
    /// </summary>
    public int UnregisterSource(string source)
    {
        var removed = 0;
        foreach (var pair in _nodes.ToArray())
        {
            if (!string.Equals(pair.Value.Source, source, StringComparison.OrdinalIgnoreCase)) continue;
            if (_nodes.TryRemove(pair.Key, out var registration))
            {
                Interlocked.Increment(ref _revision);
                registration.Dispose();
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Releases every PerNode executor cached for one workflow plan scope across all registrations.
    /// Called when a workflow's compiled plan leaves the plan cache: the plan is not leased at that point,
    /// so no execution of the scope can be in flight and disposing its instances is safe.
    /// Returns the number of released instances.
    /// </summary>
    public int ReleaseScope(string workflowScope)
    {
        if (string.IsNullOrEmpty(workflowScope)) return 0;
        var released = 0;
        foreach (var registration in _nodes.Values)
            released += registration.ReleaseScope(workflowScope);
        return released;
    }

    public VisionNodeRegistration Require(string type) =>
        _nodes.TryGetValue(type, out var value)
            ? value
            : throw new InvalidOperationException($"Node type '{type}' is not registered. Check built-ins/plugins.");

    public void Dispose()
    {
        foreach (var registration in _nodes.Values)
            registration.Dispose();
        _nodes.Clear();
    }
}
