using System.Collections.Concurrent;

namespace VisionStudio.Engine.Runtime;

/// <summary>Result of recording one flow.result contribution.</summary>
public sealed record DispositionContribution(
    string Disposition,
    bool ScopedToBranch,
    string? RegionId = null,
    string? BranchName = null,
    string? BranchDisposition = null,
    string? ScopeId = null,
    string? PreviousDisposition = null);

/// <summary>Lifecycle of one Parallel branch within a single region run.</summary>
public enum ParallelBranchStatus
{
    /// <summary>No evidence the branch started (empty branch, or not reached).</summary>
    Pending,
    /// <summary>Join-time verdict: the branch produced no node report and no terminal state.</summary>
    Missing,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// One branch's execution record. <see cref="ExecutionId"/> is unique per region run, so a
/// replayed token can be told apart from a fresh iteration instead of being merged silently.
/// </summary>
public sealed record ParallelBranchExecution(
    string RegionId,
    string BranchName,
    string ExecutionId,
    string Label,
    ParallelBranchStatus Status,
    double ElapsedMs = 0,
    string? Error = null,
    string? CancellationReason = null)
{
    public bool IsTerminal => Status is ParallelBranchStatus.Completed or ParallelBranchStatus.Failed or ParallelBranchStatus.Cancelled;
}

/// <summary>
/// Read-only view of a compiled Parallel region, used to resolve branch membership and to
/// aggregate the region's Join without exposing the tracker's mutable state.
/// </summary>
public sealed record ParallelRegionView(
    string RegionId,
    string JoinNodeId,
    IReadOnlyList<string> BranchNames,
    IReadOnlyDictionary<string, string> BranchLabels,
    IReadOnlyDictionary<string, IReadOnlyList<string>> BranchNodeIds,
    string? ParentRegionId,
    string? ParentBranchName)
{
    public bool ContainsBranchNode(string branchName, string nodeId)
        => BranchNodeIds.TryGetValue(branchName, out var nodes) && nodes.Contains(nodeId, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Aggregation produced when a Parallel region's Join executes.</summary>
public sealed record ParallelJoinAggregation(
    string ParallelNodeId,
    IReadOnlyDictionary<string, string> BranchDispositions,
    string? Aggregate,
    string? PropagatedToRegionId,
    IReadOnlyDictionary<string, ParallelBranchExecution> BranchExecutions,
    string Policy,
    bool PolicySatisfied,
    bool Duplicate = false)
{
    public IReadOnlyList<string> BranchNames(ParallelBranchStatus status)
        => BranchExecutions.Where(x => x.Value.Status == status).Select(x => x.Key)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public int Count(ParallelBranchStatus status) => BranchExecutions.Count(x => x.Value.Status == status);
}

/// <summary>Supported Join merge policies. anyNg is the historical default.</summary>
public static class ParallelMergePolicies
{
    public const string AnyNg = "anyNg";
    public const string AllPass = "allPass";
    public const string AnyPass = "anyPass";
    public const string Quorum = "quorum";

    public static readonly string[] All = [AnyNg, AllPass, AnyPass, Quorum];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AnyNg;
        var match = All.FirstOrDefault(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException($"flow.join mergePolicy must be {string.Join('|', All)}; got '{value}'.");
    }
}

/// <summary>Why a Parallel branch stopped early. Kept stable so monitors can group by it.</summary>
public static class ParallelCancellationReasons
{
    public const string SiblingFailed = "sibling-failed";
    public const string SiblingTimedOut = "sibling-timeout";
    public const string Timeout = "timeout";
    public const string RunCancelled = "run-cancelled";
}

/// <summary>
/// Branch-scoped quality disposition aggregation for Parallel regions, plus the per-branch
/// execution ledger the Join needs to report completed/failed/cancelled/missing sets.
///
/// Disposition semantics: a flow.result inside a Parallel branch records into that branch's slot
/// with any-NG semantics (NG wins). When the paired Join executes, branch slots are merged
/// according to the configured policy; a nested Parallel propagates its aggregate into the parent
/// branch slot, a top-level Parallel join returns the aggregate for the run-level disposition.
/// </summary>
public sealed class ParallelDispositionTracker
{
    private sealed record RegionScope(
        string RegionId,
        string JoinNodeId,
        IReadOnlyList<string> BranchNames,
        Dictionary<string, string> BranchLabels,
        IReadOnlyDictionary<string, IReadOnlyList<string>> BranchNodeIds,
        string? ParentRegionId,
        string? ParentBranchName);

    public static ParallelDispositionTracker Empty { get; } = new([], [], []);

    private readonly Dictionary<string, (string RegionId, string BranchName)> _branchScopes;
    private readonly Dictionary<string, RegionScope> _regionsByJoinId;
    private readonly Dictionary<string, RegionScope> _regionsById;
    private readonly ConcurrentDictionary<string, string> _branchDispositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ParallelBranchExecution> _branchExecutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _cancellationReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _currentRunIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ParallelJoinAggregation> _joinResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Action> _branchCancellers = new(StringComparer.OrdinalIgnoreCase);
    private int _runSequence;

    private ParallelDispositionTracker(
        Dictionary<string, (string RegionId, string BranchName)> branchScopes,
        Dictionary<string, RegionScope> regionsByJoinId,
        Dictionary<string, RegionScope> regionsById)
    {
        _branchScopes = branchScopes;
        _regionsByJoinId = regionsByJoinId;
        _regionsById = regionsById;
    }

    /// <summary>Builds the tracker from the compiled structured control regions (If regions are ignored).</summary>
    public static ParallelDispositionTracker FromRegions(IReadOnlyList<StructuredControlRegion> regions)
    {
        var parallelRegions = regions
            .Where(x => x.Kind.Equals("Parallel", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (parallelRegions.Length == 0) return Empty;

        // Innermost enclosing Parallel region wins: deeper regions overwrite shallower ones.
        var branchScopes = new Dictionary<string, (string RegionId, string BranchName)>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in parallelRegions.OrderBy(x => x.Depth))
        {
            foreach (var (branchName, nodeIds) in region.BranchNodeIds)
                foreach (var nodeId in nodeIds)
                    branchScopes[nodeId] = (region.ControlNodeId, branchName);
        }

        var regionsByJoinId = new Dictionary<string, RegionScope>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in parallelRegions)
        {
            string? parentRegionId = null;
            string? parentBranchName = null;
            foreach (var candidate in parallelRegions
                .Where(x => !ReferenceEquals(x, region))
                .OrderBy(x => x.Depth))
            {
                var containingBranch = candidate.BranchNodeIds
                    .FirstOrDefault(kv => kv.Value.Contains(region.ControlNodeId, StringComparer.OrdinalIgnoreCase));
                if (containingBranch.Key is not null)
                {
                    parentRegionId = candidate.ControlNodeId;
                    parentBranchName = containingBranch.Key;
                }
            }

            var branchNames = region.BranchNodeIds.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var branchNodeIds = region.BranchNodeIds
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            regionsByJoinId[region.JoinNodeId] = new RegionScope(
                region.ControlNodeId,
                region.JoinNodeId,
                branchNames,
                branchNames.ToDictionary(x => x, x => x, StringComparer.OrdinalIgnoreCase),
                branchNodeIds,
                parentRegionId,
                parentBranchName);
        }

        var regionsById = regionsByJoinId.Values
            .GroupBy(x => x.RegionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return new ParallelDispositionTracker(branchScopes, regionsByJoinId, regionsById);
    }

    /// <summary>Returns the innermost Parallel branch scope of a node, if it lives inside one.</summary>
    public bool TryGetBranchScope(string nodeId, out string regionId, out string branchName)
    {
        if (_branchScopes.TryGetValue(nodeId, out var scope))
        {
            regionId = scope.RegionId;
            branchName = scope.BranchName;
            return true;
        }
        regionId = string.Empty;
        branchName = string.Empty;
        return false;
    }

    /// <summary>Resolves the region closed by a Join node; null when the Join does not close a Parallel region.</summary>
    public ParallelRegionView? RegionForJoin(string joinNodeId)
        => _regionsByJoinId.TryGetValue(joinNodeId, out var region)
            ? new ParallelRegionView(region.RegionId, region.JoinNodeId, region.BranchNames, region.BranchLabels,
                region.BranchNodeIds, region.ParentRegionId, region.ParentBranchName)
            : null;

    /// <summary>Region view addressed by the Parallel node id instead of its Join node id.</summary>
    public ParallelRegionView? RegionById(string regionId)
        => _regionsById.TryGetValue(regionId, out var region)
            ? new ParallelRegionView(region.RegionId, region.JoinNodeId, region.BranchNames, region.BranchLabels,
                region.BranchNodeIds, region.ParentRegionId, region.ParentBranchName)
            : null;

    /// <summary>Reads the current branch slot value, if any, so an audit trail can report the previous disposition.</summary>
    public string? GetBranchDisposition(string regionId, string branchName)
        => _branchDispositions.TryGetValue(SlotKey(regionId, branchName), out var value) ? value : null;

    /// <summary>Clears one branch slot so an explicit reset can start a new disposition in that branch.</summary>
    public bool ResetBranch(string regionId, string branchName)
        => _branchDispositions.TryRemove(SlotKey(regionId, branchName), out _);

    /// <summary>Records a branch contribution (any-NG) and returns the combined branch value.</summary>
    public string ContributeBranchDisposition(string regionId, string branchName, string disposition)
        => _branchDispositions.AddOrUpdate(
            SlotKey(regionId, branchName),
            disposition,
            (_, current) => Combine(current, disposition));

    /// <summary>
    /// Starts a new run of a Parallel region: new execution ids, cleared terminal states, and a
    /// cleared Join cache so a loop iteration never reads the previous iteration's aggregation.
    /// </summary>
    public string BeginRegionRun(string regionId)
    {
        if (!_regionsById.TryGetValue(regionId, out var region))
            return $"{regionId}#0";

        var runId = $"{regionId}#{Interlocked.Increment(ref _runSequence)}";
        _currentRunIds[regionId] = runId;
        _joinResults.TryRemove(region.JoinNodeId, out _);
        foreach (var branchName in region.BranchNames)
        {
            var key = SlotKey(regionId, branchName);
            _cancellationReasons.TryRemove(key, out _);
            _branchExecutions[key] = new ParallelBranchExecution(regionId, branchName, $"{runId}/{branchName}",
                region.BranchLabels.GetValueOrDefault(branchName, branchName), ParallelBranchStatus.Pending);
        }
        return runId;
    }

    /// <summary>Applies a branch label/timeout configuration coming from the flow.parallel node parameters.</summary>
    public void ConfigureBranches(string regionId, IReadOnlyList<string> labels)
    {
        if (!_regionsById.TryGetValue(regionId, out var region) || labels.Count != region.BranchNames.Count) return;
        for (var index = 0; index < region.BranchNames.Count; index++)
            region.BranchLabels[region.BranchNames[index]] = labels[index];
    }

    public ParallelBranchExecution? GetBranchExecution(string regionId, string branchName)
        => _branchExecutions.TryGetValue(SlotKey(regionId, branchName), out var execution) ? execution : null;

    public IReadOnlyList<ParallelBranchExecution> BranchExecutions(string regionId)
        => _regionsById.TryGetValue(regionId, out var region)
            ? region.BranchNames.Select(name => GetBranchExecution(regionId, name)).OfType<ParallelBranchExecution>().ToArray()
            : [];

    public void MarkBranchRunning(string regionId, string branchName)
        => Transition(regionId, branchName, current => current.Status == ParallelBranchStatus.Pending
            ? current with { Status = ParallelBranchStatus.Running }
            : current);

    public void CompleteBranch(string regionId, string branchName, double elapsedMs)
        => Transition(regionId, branchName, current => current.IsTerminal
            ? current
            : current with { Status = ParallelBranchStatus.Completed, ElapsedMs = elapsedMs });

    public void FailBranch(string regionId, string branchName, double elapsedMs, string error)
        => Transition(regionId, branchName, current => current.IsTerminal
            ? current
            : current with { Status = ParallelBranchStatus.Failed, ElapsedMs = elapsedMs, Error = error });

    /// <summary>
    /// Declares that a branch is about to be cancelled by the scheduler (sibling failure or
    /// branch timeout). The reason is recorded before the token fires so the node runtime can
    /// tell scheduler cancellation apart from a genuine cancellation request.
    /// </summary>
    public void RequestBranchCancellation(string regionId, string branchName, string reason)
        => _cancellationReasons[SlotKey(regionId, branchName)] = reason;

    /// <summary>
    /// Registers the scheduler's cancel handle for one branch, so a failure inside a node can stop
    /// its siblings *before* the failure becomes observable (report/observer), which is what keeps
    /// a sibling from squeezing in one more node.
    /// </summary>
    public void RegisterBranchCanceller(string regionId, string branchName, Action cancel)
        => _branchCancellers[SlotKey(regionId, branchName)] = cancel;

    public void UnregisterBranchCanceller(string regionId, string branchName)
        => _branchCancellers.TryRemove(SlotKey(regionId, branchName), out _);

    /// <summary>
    /// Cancels every other branch of the region that owns <paramref name="nodeId"/>. Returns the
    /// branch names that received the request so the caller can report them.
    /// </summary>
    public IReadOnlyList<string> CancelSiblings(string nodeId, string reason)
    {
        if (!_branchScopes.TryGetValue(nodeId, out var scope)) return [];
        if (!_regionsById.TryGetValue(scope.RegionId, out var region)) return [];

        var cancelled = new List<string>();
        foreach (var branchName in region.BranchNames)
        {
            if (branchName.Equals(scope.BranchName, StringComparison.OrdinalIgnoreCase)) continue;
            _cancellationReasons[SlotKey(scope.RegionId, branchName)] = reason;
            if (_branchCancellers.TryGetValue(SlotKey(scope.RegionId, branchName), out var cancel))
            {
                try { cancel(); }
                catch (ObjectDisposedException) { /* 兄弟已结束 */ }
                cancelled.Add(branchName);
            }
        }
        return cancelled;
    }

    public string? CancellationReasonOf(string regionId, string branchName)
        => _cancellationReasons.TryGetValue(SlotKey(regionId, branchName), out var reason) ? reason : null;

    /// <summary>Reason a node's cancellation was scheduler-induced, or null for a real run cancellation.</summary>
    public string? CancellationReasonForNode(string nodeId)
        => TryGetBranchScope(nodeId, out var regionId, out var branchName) ? CancellationReasonOf(regionId, branchName) : null;

    public void CancelBranch(string regionId, string branchName, double elapsedMs, string reason)
        => Transition(regionId, branchName, current => current.IsTerminal
            ? current
            : current with { Status = ParallelBranchStatus.Cancelled, ElapsedMs = elapsedMs, CancellationReason = reason });

    /// <summary>Reads the disposition slots of a region so the caller can apply the merge policy.</summary>
    public IReadOnlyDictionary<string, string> BranchDispositions(string regionId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_regionsById.TryGetValue(regionId, out var region)) return result;
        foreach (var branchName in region.BranchNames)
            if (_branchDispositions.TryGetValue(SlotKey(regionId, branchName), out var value))
                result[branchName] = value;
        return result;
    }

    /// <summary>Idempotency gate for Join: the second token for the same Join returns the cached aggregation.</summary>
    public bool TryGetCachedJoin(string joinNodeId, out ParallelJoinAggregation aggregation)
        => _joinResults.TryGetValue(joinNodeId, out aggregation!);

    public void CacheJoin(string joinNodeId, ParallelJoinAggregation aggregation)
        => _joinResults[joinNodeId] = aggregation;

    /// <summary>Policy evaluation shared by the live Join and by the report-only view.</summary>
    public static (string? Aggregate, bool Satisfied) Evaluate(
        ParallelRegionView view, IReadOnlyDictionary<string, string> dispositions, int quorum, string policy)
    {
        var okCount = dispositions.Values.Count(x => x.Equals("OK", StringComparison.OrdinalIgnoreCase));
        var ngCount = dispositions.Values.Count(x => x.Equals("NG", StringComparison.OrdinalIgnoreCase));
        var evaluated = okCount + ngCount;
        var branches = view.BranchNames.Count;

        switch (policy)
        {
            case ParallelMergePolicies.AllPass:
                if (evaluated == 0) return (null, false);
                var allOk = okCount == branches && evaluated == branches;
                return (allOk ? "OK" : "NG", allOk);
            case ParallelMergePolicies.AnyPass:
                if (evaluated == 0) return (null, false);
                return okCount > 0 ? ("OK", true) : ("NG", false);
            case ParallelMergePolicies.Quorum:
                var required = quorum > 0 ? quorum : branches;
                if (evaluated == 0) return (null, false);
                return okCount >= required ? ("OK", true) : ("NG", false);
            default:
                if (evaluated == 0) return (null, false);
                return ngCount > 0 ? ("NG", false) : ("OK", true);
        }
    }

    /// <summary>Any-NG merge: once a side is NG the result stays NG; otherwise the incoming value wins.</summary>
    public static string Combine(string current, string incoming)
        => current.Equals("NG", StringComparison.OrdinalIgnoreCase) ? "NG" : incoming;

    private void Transition(string regionId, string branchName, Func<ParallelBranchExecution, ParallelBranchExecution> update)
    {
        if (!_regionsById.TryGetValue(regionId, out var region)) return;
        var key = SlotKey(regionId, branchName);
        _branchExecutions.AddOrUpdate(
            key,
            _ => update(new ParallelBranchExecution(regionId, branchName,
                $"{_currentRunIds.GetValueOrDefault(regionId, regionId)}/{branchName}",
                region.BranchLabels.GetValueOrDefault(branchName, branchName), ParallelBranchStatus.Pending)),
            (_, current) => update(current));
    }

    private static string SlotKey(string regionId, string branchName) => $"{regionId}|{branchName}";
}
