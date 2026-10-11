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
    /// True when the node must not be executed implicitly by a debug warmup (external side effects
    /// such as PLC writes, robot commands or an actively triggering camera).
    /// 扩展契约：显式动作声明（Actions）优先，且按**节点参数**求值（声明条件只在满足时计）；
    /// 未声明目录回退到旧能力标记。
    /// </summary>
    public bool RequiresSideEffectApproval(NodeDefinition node)
    {
        if (!nodes.IsRegistered(node.Type)) return false;
        var catalog = nodes.Require(node.Type).Catalog;
        return catalog.DeclaresActions
            ? NodeActionContract.NodeHasAction(node, catalog)
            : catalog.Capabilities?.SupportsRunNode == false;
    }

    /// <summary>
    /// 类型级审批判定（保守口径：看不到节点参数时，声明了任何动作即要求审批）。
    /// 调试执行路径请使用节点级重载，避免"仅部分参数才成立的动作"被误判为恒有副作用。
    /// </summary>
    public bool RequiresSideEffectApproval(string nodeType)
    {
        if (!nodes.IsRegistered(nodeType)) return false;
        var catalog = nodes.Require(nodeType).Catalog;
        return catalog.DeclaresActions
            ? catalog.Actions!.Count > 0
            : catalog.Capabilities?.SupportsRunNode == false;
    }

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
