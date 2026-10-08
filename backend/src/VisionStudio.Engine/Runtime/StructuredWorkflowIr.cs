namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Recursive, engine-neutral-enough structured control-flow IR produced from the visual graph
/// before Workflow Core DSL emission. Split/join pairing and branch ownership are resolved here,
/// so the DSL emitter never has to rediscover graph structure.
/// </summary>
internal abstract record StructuredIrItem
{
    public abstract IReadOnlyList<string> NodeIds { get; }
}

internal sealed record StructuredNodeIr(NodeDefinition Node) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds => [Node.Id];
}

internal sealed record StructuredIfIr(
    NodeDefinition Node,
    StructuredSequenceIr TrueBranch,
    StructuredSequenceIr FalseBranch,
    string JoinNodeId,
    int Depth) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds =>
        [Node.Id, .. TrueBranch.NodeIds, .. FalseBranch.NodeIds];
}

internal sealed record StructuredParallelIr(
    NodeDefinition Node,
    StructuredSequenceIr Branch1,
    StructuredSequenceIr Branch2,
    string JoinNodeId,
    int Depth) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds =>
        [Node.Id, .. Branch1.NodeIds, .. Branch2.NodeIds];
}

internal sealed record StructuredSequenceIr(IReadOnlyList<StructuredIrItem> Items)
{
    public IReadOnlyList<string> NodeIds => Items.SelectMany(x => x.NodeIds).ToArray();
}

public sealed record StructuredControlRegion(
    string ControlNodeId,
    string Kind,
    string JoinNodeId,
    int Depth,
    IReadOnlyDictionary<string, IReadOnlyList<string>> BranchNodeIds);

internal sealed record StructuredWorkflowIr(
    StructuredSequenceIr Root,
    IReadOnlyList<string> VisualOrder,
    IReadOnlyList<StructuredControlRegion> Regions);

/// <summary>
/// Parses explicit Control edges into a strict structured tree. Each If/Parallel owns one distinct
/// Join before its parent's Join, which makes nesting unambiguous and rejects irreducible graphs.
/// </summary>
internal sealed class StructuredWorkflowIrBuilder
{
    private readonly StructuredControlGraph _graph;
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>已配对的 If/Parallel Join：其块被处理后由外层序列直接消费，不再视为“意外 Join”。</summary>
    private readonly HashSet<string> _pairedJoins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _visualOrder = [];
    private readonly List<StructuredControlRegion> _regions = [];

    public StructuredWorkflowIrBuilder(WorkflowDefinition workflow, IReadOnlyList<EdgeDefinition> controlEdges)
        => _graph = new StructuredControlGraph(workflow, controlEdges);

    public StructuredWorkflowIr Build()
    {
        var starts = _graph.Workflow.Nodes.Where(n => _graph.Incoming(n.Id).Count == 0).ToArray();
        if (starts.Length != 1)
            throw new InvalidOperationException($"Structured workflow requires exactly one control-flow start node; found {starts.Length}.");

        var root = BuildSequence(starts[0].Id, stopJoinId: null, ownerId: "root", depth: 0);
        if (_claimed.Count != _graph.Workflow.Nodes.Count)
        {
            var missing = _graph.Workflow.Nodes.Where(n => !_claimed.Contains(n.Id)).Select(n => n.Id);
            throw new InvalidOperationException($"Every executable node must belong to the control-flow graph. Unreachable: {string.Join(", ", missing)}");
        }

        return new StructuredWorkflowIr(root, _visualOrder.ToArray(), _regions.ToArray());
    }

    private StructuredSequenceIr BuildSequence(string startId, string? stopJoinId, string ownerId, int depth)
    {
        var items = new List<StructuredIrItem>();
        var currentId = startId;

        while (true)
        {
            if (stopJoinId is not null && currentId.Equals(stopJoinId, StringComparison.OrdinalIgnoreCase))
                return new StructuredSequenceIr(items);

            var node = _graph.Node(currentId);
            // 分支内部的合法 Join 只有本层边界（stopJoinId，上一行已处理）与刚处理完的嵌套块
            // 自己的配对 Join（_pairedJoins，下一轮循环会把它作为汇合点正常消费）。
            // 其余任何 Join 都属于越界/共享结构，必须拒绝。
            if (node.Type.Equals("flow.join", StringComparison.OrdinalIgnoreCase)
                && stopJoinId is not null
                && !_pairedJoins.Contains(node.Id))
                throw new InvalidOperationException(
                    $"Structured branch '{ownerId}' reached unexpected Join '{node.Id}' before its paired Join '{stopJoinId}'.");

            Claim(node.Id, ownerId);
            _visualOrder.Add(node.Id);

            if (node.Type.Equals("flow.if", StringComparison.OrdinalIgnoreCase))
            {
                var outgoing = _graph.Outgoing(node.Id);
                var trueEdge = SinglePort(outgoing, "true", node.Id);
                var falseEdge = SinglePort(outgoing, "false", node.Id);
                var joinId = _graph.FindNearestCommonJoin(
                    [trueEdge.TargetNodeId, falseEdge.TargetNodeId],
                    excludedBoundaryJoinId: stopJoinId,
                    ownerId: node.Id);

                EnsureNonEmptyBranch(node.Id, "true", trueEdge.TargetNodeId, joinId);
                EnsureNonEmptyBranch(node.Id, "false", falseEdge.TargetNodeId, joinId);

                var trueBranch = BuildSequence(trueEdge.TargetNodeId, joinId, $"{node.Id}.true", depth + 1);
                var falseBranch = BuildSequence(falseEdge.TargetNodeId, joinId, $"{node.Id}.false", depth + 1);
                ValidateNoCrossBranchData(node.Id, trueBranch.NodeIds, falseBranch.NodeIds);

                items.Add(new StructuredIfIr(node, trueBranch, falseBranch, joinId, depth));
                _regions.Add(new StructuredControlRegion(
                    node.Id,
                    "If",
                    joinId,
                    depth,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["true"] = trueBranch.NodeIds,
                        ["false"] = falseBranch.NodeIds
                    }));
                // 自己的 Join 已配对：下一轮循环直接消费它（嵌套块位于外层分支内时，
                // 内层 Join 是合法路径点，而不是“意外 Join”）
                _pairedJoins.Add(joinId);
                currentId = joinId;
                continue;
            }

            if (node.Type.Equals("flow.parallel", StringComparison.OrdinalIgnoreCase))
            {
                var outgoing = _graph.Outgoing(node.Id);
                var branch1Edge = SinglePort(outgoing, "branch1", node.Id);
                var branch2Edge = SinglePort(outgoing, "branch2", node.Id);
                var joinId = _graph.FindNearestCommonJoin(
                    [branch1Edge.TargetNodeId, branch2Edge.TargetNodeId],
                    excludedBoundaryJoinId: stopJoinId,
                    ownerId: node.Id);

                EnsureNonEmptyBranch(node.Id, "branch1", branch1Edge.TargetNodeId, joinId);
                EnsureNonEmptyBranch(node.Id, "branch2", branch2Edge.TargetNodeId, joinId);

                var branch1 = BuildSequence(branch1Edge.TargetNodeId, joinId, $"{node.Id}.branch1", depth + 1);
                var branch2 = BuildSequence(branch2Edge.TargetNodeId, joinId, $"{node.Id}.branch2", depth + 1);
                ValidateNoCrossBranchData(node.Id, branch1.NodeIds, branch2.NodeIds);

                items.Add(new StructuredParallelIr(node, branch1, branch2, joinId, depth));
                _regions.Add(new StructuredControlRegion(
                    node.Id,
                    "Parallel",
                    joinId,
                    depth,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["branch1"] = branch1.NodeIds,
                        ["branch2"] = branch2.NodeIds
                    }));
                // 自己的 Join 已配对：下一轮循环直接消费它（嵌套块位于外层分支内时，
                // 内层 Join 是合法路径点，而不是“意外 Join”）
                _pairedJoins.Add(joinId);
                currentId = joinId;
                continue;
            }

            items.Add(new StructuredNodeIr(node));
            var next = _graph.Outgoing(node.Id);
            if (next.Count > 1)
                throw new InvalidOperationException($"Node '{node.Id}' has {next.Count} control outputs. Use If or Parallel for branching.");
            if (next.Count == 0)
            {
                if (stopJoinId is not null)
                    throw new InvalidOperationException($"Structured branch '{ownerId}' terminated at '{node.Id}' before Join '{stopJoinId}'.");
                return new StructuredSequenceIr(items);
            }

            currentId = next[0].TargetNodeId;
        }
    }

    private void Claim(string nodeId, string ownerId)
    {
        if (!_claimed.Add(nodeId))
            throw new InvalidOperationException(
                $"Node '{nodeId}' is shared by multiple structured regions or participates in a control-flow cycle (owner '{ownerId}').");
    }

    private void ValidateNoCrossBranchData(
        string ownerId,
        IReadOnlyList<string> branch1,
        IReadOnlyList<string> branch2)
    {
        var left = branch1.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var right = branch2.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cross = _graph.Workflow.Edges.FirstOrDefault(e => !WorkflowGraph.IsControlEdge(e) &&
            ((left.Contains(e.SourceNodeId) && right.Contains(e.TargetNodeId)) ||
             (right.Contains(e.SourceNodeId) && left.Contains(e.TargetNodeId))));
        if (cross is not null)
            throw new InvalidOperationException(
                $"Branches under '{ownerId}' must be data-independent until their paired Join. Cross-branch data edge: '{cross.Id}'.");
    }

    private static EdgeDefinition SinglePort(IReadOnlyList<EdgeDefinition> edges, string port, string nodeId)
    {
        var matches = edges.Where(x => x.SourcePort.Equals(port, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Node '{nodeId}' requires exactly one '{port}' control edge; found {matches.Length}.");
        return matches[0];
    }

    private static void EnsureNonEmptyBranch(string nodeId, string branchName, string targetId, string joinId)
    {
        if (targetId.Equals(joinId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Structured control '{nodeId}' requires non-empty '{branchName}' before Join '{joinId}'.");
    }
}

internal sealed class StructuredControlGraph
{
    private readonly IReadOnlyDictionary<string, NodeDefinition> _nodes;
    private readonly IReadOnlyDictionary<string, List<EdgeDefinition>> _outgoing;
    private readonly IReadOnlyDictionary<string, List<EdgeDefinition>> _incoming;

    public WorkflowDefinition Workflow { get; }

    public StructuredControlGraph(WorkflowDefinition workflow, IReadOnlyList<EdgeDefinition> controlEdges)
    {
        Workflow = workflow;
        _nodes = workflow.Nodes.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        _outgoing = workflow.Nodes.ToDictionary(x => x.Id, _ => new List<EdgeDefinition>(), StringComparer.OrdinalIgnoreCase);
        _incoming = workflow.Nodes.ToDictionary(x => x.Id, _ => new List<EdgeDefinition>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in controlEdges)
        {
            _outgoing[edge.SourceNodeId].Add(edge);
            _incoming[edge.TargetNodeId].Add(edge);
        }
    }

    public NodeDefinition Node(string id) => _nodes.TryGetValue(id, out var node)
        ? node
        : throw new InvalidOperationException($"Missing node '{id}'.");

    public IReadOnlyList<EdgeDefinition> Outgoing(string id) => _outgoing[id];
    public IReadOnlyList<EdgeDefinition> Incoming(string id) => _incoming[id];

    public string FindNearestCommonJoin(
        IReadOnlyList<string> branchStarts,
        string? excludedBoundaryJoinId,
        string ownerId)
    {
        if (branchStarts.Count < 2)
            throw new InvalidOperationException($"Structured control '{ownerId}' requires at least two branches.");

        var reachability = branchStarts
            .Select(start => JoinDistances(start, excludedBoundaryJoinId))
            .ToArray();

        var common = reachability[0].Keys
            .Where(id => reachability.Skip(1).All(x => x.ContainsKey(id)))
            .Select(id => new
            {
                Id = id,
                Max = reachability.Max(x => x[id]),
                Sum = reachability.Sum(x => x[id])
            })
            .OrderBy(x => x.Max)
            .ThenBy(x => x.Sum)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (common.Length == 0)
        {
            var boundary = excludedBoundaryJoinId is null ? string.Empty : $" before parent Join '{excludedBoundaryJoinId}'";
            throw new InvalidOperationException($"Structured control '{ownerId}' has no distinct common Join{boundary}.");
        }

        if (common.Length > 1 && common[0].Max == common[1].Max && common[0].Sum == common[1].Sum)
            throw new InvalidOperationException(
                $"Structured control '{ownerId}' has ambiguous paired Joins '{common[0].Id}' and '{common[1].Id}'.");

        return common[0].Id;
    }

    private Dictionary<string, int> JoinDistances(string startId, string? excludedBoundaryJoinId)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var distance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [startId] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var d = distance[id];
            if (excludedBoundaryJoinId is not null && id.Equals(excludedBoundaryJoinId, StringComparison.OrdinalIgnoreCase))
                continue;

            var node = Node(id);
            if (node.Type.Equals("flow.join", StringComparison.OrdinalIgnoreCase))
                result.TryAdd(id, d);

            foreach (var edge in Outgoing(id))
            {
                if (excludedBoundaryJoinId is not null && edge.TargetNodeId.Equals(excludedBoundaryJoinId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (distance.TryAdd(edge.TargetNodeId, d + 1))
                    queue.Enqueue(edge.TargetNodeId);
            }
        }

        return result;
    }
}
