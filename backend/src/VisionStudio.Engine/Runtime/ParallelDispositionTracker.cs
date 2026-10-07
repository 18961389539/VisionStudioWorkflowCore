using System.Collections.Concurrent;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Result of recording one flow.result contribution.
/// Branch-scoped contributions are held in the branch slot until the paired Join merges them;
/// unscoped contributions set the run disposition directly.
/// </summary>
public sealed record DispositionContribution(
    string Disposition,
    bool ScopedToBranch,
    string? RegionId = null,
    string? BranchName = null,
    string? BranchDisposition = null);

/// <summary>Aggregation produced when a Parallel region's Join executes.</summary>
public sealed record ParallelJoinAggregation(
    string ParallelNodeId,
    IReadOnlyDictionary<string, string> BranchDispositions,
    string? Aggregate,
    string? PropagatedToRegionId);

/// <summary>
/// Branch-scoped quality disposition aggregation for Parallel regions.
///
/// Semantics: a flow.result inside a Parallel branch records into that branch's slot with
/// any-NG semantics (NG wins). When the paired Join executes, branch slots are merged with the
/// same any-NG rule; a nested Parallel propagates its aggregate into the parent branch slot,
/// a top-level Parallel join returns the aggregate for the run-level disposition.
/// Unbalanced branches are irrelevant: no execution order (branch order, step count or timing)
/// can mask an NG with a later OK.
/// </summary>
public sealed class ParallelDispositionTracker
{
    private sealed record RegionScope(
        string RegionId,
        string JoinNodeId,
        IReadOnlyList<string> BranchNames,
        string? ParentRegionId,
        string? ParentBranchName);

    public static ParallelDispositionTracker Empty { get; } = new([], []);

    private readonly Dictionary<string, (string RegionId, string BranchName)> _branchScopes;
    private readonly Dictionary<string, RegionScope> _regionsByJoinId;
    private readonly ConcurrentDictionary<string, string> _branchDispositions = new(StringComparer.OrdinalIgnoreCase);

    private ParallelDispositionTracker(
        Dictionary<string, (string RegionId, string BranchName)> branchScopes,
        Dictionary<string, RegionScope> regionsByJoinId)
    {
        _branchScopes = branchScopes;
        _regionsByJoinId = regionsByJoinId;
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

            regionsByJoinId[region.JoinNodeId] = new RegionScope(
                region.ControlNodeId,
                region.JoinNodeId,
                region.BranchNodeIds.Keys.ToArray(),
                parentRegionId,
                parentBranchName);
        }

        return new ParallelDispositionTracker(branchScopes, regionsByJoinId);
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

    /// <summary>Records a branch contribution (any-NG) and returns the combined branch value.</summary>
    public string ContributeBranchDisposition(string regionId, string branchName, string disposition)
        => _branchDispositions.AddOrUpdate(
            SlotKey(regionId, branchName),
            disposition,
            (_, current) => Combine(current, disposition));

    /// <summary>
    /// Merges the slots of the Parallel region closed by <paramref name="joinNodeId"/> with any-NG
    /// semantics. Nested regions propagate their aggregate into the parent branch slot.
    /// Returns null when the join does not close a Parallel region.
    /// </summary>
    public ParallelJoinAggregation? AggregateJoin(string joinNodeId)
    {
        if (!_regionsByJoinId.TryGetValue(joinNodeId, out var region)) return null;

        var branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? aggregate = null;
        foreach (var branchName in region.BranchNames)
        {
            if (!_branchDispositions.TryGetValue(SlotKey(region.RegionId, branchName), out var value)) continue;
            branches[branchName] = value;
            aggregate = aggregate is null ? value : Combine(aggregate, value);
        }

        if (aggregate is not null && region.ParentRegionId is not null && region.ParentBranchName is not null)
            ContributeBranchDisposition(region.ParentRegionId, region.ParentBranchName, aggregate);

        return new ParallelJoinAggregation(region.RegionId, branches, aggregate, region.ParentRegionId);
    }

    /// <summary>Any-NG merge: once a side is NG the result stays NG; otherwise the incoming value wins.</summary>
    public static string Combine(string current, string incoming)
        => current.Equals("NG", StringComparison.OrdinalIgnoreCase) ? "NG" : incoming;

    private static string SlotKey(string regionId, string branchName) => $"{regionId}|{branchName}";
}
