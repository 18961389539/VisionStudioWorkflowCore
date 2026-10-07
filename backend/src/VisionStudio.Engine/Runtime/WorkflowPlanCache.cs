using WorkflowCore.Interface;
using WorkflowCore.Services.DefinitionStorage;

namespace VisionStudio.Engine.Runtime;

/// <summary>Bounded LRU of compiled and registered plans. Leases pin definitions for the entire execution.</summary>
public sealed class WorkflowPlanCache(
    VisionWorkflowCompiler compiler,
    IDefinitionLoader loader,
    IWorkflowRegistry registry,
    VisionNodeRegistry nodes,
    int capacity = 128)
{
    private sealed class Entry(CompiledWorkflow plan, long lastUsed)
    {
        public CompiledWorkflow Plan { get; } = plan;
        public long LastUsed = lastUsed;
        public int References;
    }

    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _clock;
    private long _compilations;
    public int Count { get { lock (_sync) return _entries.Count; } }
    public long CompilationCount { get { lock (_sync) return _compilations; } }

    /// <summary>
    /// True when the node catalog declares the type must not be executed implicitly by a debug warmup
    /// (external side effects such as PLC writes or robot commands).
    /// </summary>
    public bool RequiresSideEffectApproval(string nodeType)
        => nodes.IsRegistered(nodeType) && nodes.Require(nodeType).Catalog.Capabilities?.SupportsRunNode == false;

    public async Task<Lease> AcquireAsync(WorkflowDefinition workflow, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (_sync)
            {
                var key = compiler.GetPlanCacheKey(workflow);
                var canCreate = true;
                wait = _released.Task;
                if (!_entries.TryGetValue(key, out var entry))
                {
                    if (_entries.Count >= _capacity)
                    {
                        var oldest = _entries.Where(x => x.Value.References == 0).OrderBy(x => x.Value.LastUsed).FirstOrDefault();
                        if (oldest.Value is null)
                        {
                            canCreate = false;
                        }
                        else
                        {
                            registry.DeregisterWorkflow(oldest.Value.Plan.WorkflowCoreId, oldest.Value.Plan.Version);
                            _entries.Remove(oldest.Key);
                            // PerNode plugin instances are scoped to the plan identity: releasing them together with the
                            // evicted entry keeps stateful plugin caches bounded by the plan cache capacity.
                            nodes.ReleaseScope(oldest.Key);
                        }
                    }
                    if (canCreate)
                    {
                        var revision = nodes.Revision;
                        var plan = compiler.Compile(workflow);
                        if (revision != nodes.Revision || key != compiler.GetPlanCacheKey(workflow)) continue;
                        loader.LoadDefinition(plan.DslJson, Deserializers.Json);
                        entry = new Entry(plan, ++_clock);
                        _entries.Add(key, entry);
                        _compilations++;
                    }
                }
                if (entry is not null)
                {
                    entry.References++;
                    entry.LastUsed = ++_clock;
                    return new Lease(this, entry.Plan, key);
                }
            }
            await wait.WaitAsync(ct);
        }
    }

    private void Release(string key)
    {
        lock (_sync)
        {
            _entries[key].References--;
            var signal = _released;
            _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }
    }

    public sealed class Lease : IDisposable
    {
        private WorkflowPlanCache? _owner;
        private readonly string _key;
        public CompiledWorkflow Plan { get; }
        internal Lease(WorkflowPlanCache owner, CompiledWorkflow plan, string key)
            => (_owner, Plan, _key) = (owner, plan, key);

        /// <summary>
        /// Plan identity (workflow id + content fingerprint + catalog revision). Used as the workflow scope for
        /// per-node plugin instance caches so instances are isolated per workflow and released with this entry.
        /// </summary>
        public string Key => _key;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_key);
    }
}
