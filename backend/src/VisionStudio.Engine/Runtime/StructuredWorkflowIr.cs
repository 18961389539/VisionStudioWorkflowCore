using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

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

internal sealed record StructuredSwitchBranch(string Port, string Label, StructuredSequenceIr Sequence);

internal sealed record StructuredSwitchIr(
    NodeDefinition Node,
    IReadOnlyList<StructuredSwitchBranch> Branches,
    string JoinNodeId,
    int Depth) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds => [Node.Id, .. Branches.SelectMany(x => x.Sequence.NodeIds)];
}

internal sealed record StructuredTryIr(
    NodeDefinition Node,
    StructuredSequenceIr Body,
    StructuredSequenceIr Success,
    StructuredSequenceIr Error,
    string JoinNodeId,
    int Depth) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds => [Node.Id, .. Body.NodeIds, .. Success.NodeIds, .. Error.NodeIds];
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

internal sealed record StructuredForEachIr(
    NodeDefinition Node,
    StructuredSequenceIr Body,
    string LoopEndNodeId,
    int Depth) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds => [Node.Id, .. Body.NodeIds];
}

/// <summary>flow.while 的条件来源：循环体（或循环之前）某节点的一个 Boolean 输出端口。</summary>
internal sealed record LoopConditionSource(string NodeId, string Port);

/// <summary>
/// 通用循环（flow.while / flow.repeat）。与 ForEach 的区别是迭代次数由条件或计数决定，
/// 而不是由集合元素决定；两者共用 flow.loopEnd + repeat 回边的配对约定。
/// </summary>
internal sealed record StructuredLoopIr(
    NodeDefinition Node,
    string Kind,
    StructuredSequenceIr Body,
    string LoopEndNodeId,
    int Depth,
    LoopConditionSource? ConditionSource,
    int MaxIterations) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds => [Node.Id, .. Body.NodeIds];
}

/// <summary>
/// flow.break：节点本身之后的本轮剩余步骤被包进 <c>If(!broken)</c> 守卫。
/// Remaining 可以为空（break 正好是循环体最后一步）。
/// </summary>
internal sealed record StructuredBreakIr(
    NodeDefinition Node,
    StructuredSequenceIr Remaining,
    int Depth) : StructuredIrItem
{
    public override IReadOnlyList<string> NodeIds => [Node.Id, .. Remaining.NodeIds];
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
    IReadOnlyList<StructuredControlRegion> Regions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TryBodies);

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
    private readonly HashSet<string> _pairedLoopEnds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _visualOrder = [];
    private readonly List<StructuredControlRegion> _regions = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _tryBodies = new(StringComparer.OrdinalIgnoreCase);

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

        return new StructuredWorkflowIr(root, _visualOrder.ToArray(), _regions.ToArray(),
            new Dictionary<string, IReadOnlyList<string>>(_tryBodies, StringComparer.OrdinalIgnoreCase));
    }

    private StructuredSequenceIr BuildSequence(string startId, string? stopJoinId, string ownerId, int depth, string? stopLoopEndId = null, string? enclosingLoopId = null, bool breakAllowed = false)
    {
        var items = new List<StructuredIrItem>();
        var currentId = startId;

        while (true)
        {
            if (stopJoinId is not null && currentId.Equals(stopJoinId, StringComparison.OrdinalIgnoreCase))
                return new StructuredSequenceIr(items);

            if (stopLoopEndId is not null && currentId.Equals(stopLoopEndId, StringComparison.OrdinalIgnoreCase))
            {
                var loopEnd = _graph.Node(currentId);
                if (!loopEnd.Type.Equals("flow.loopEnd", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Loop body '{ownerId}' expected flow.loopEnd '{stopLoopEndId}'.");
                Claim(loopEnd.Id, ownerId);
                _visualOrder.Add(loopEnd.Id);
                items.Add(new StructuredNodeIr(loopEnd));
                return new StructuredSequenceIr(items);
            }

            var node = _graph.Node(currentId);
            // 分支内部的合法 Join 只有本层边界（stopJoinId，上一行已处理）与刚处理完的嵌套块
            // 自己的配对 Join（_pairedJoins，下一轮循环会把它作为汇合点正常消费）。
            // 其余任何 Join 都属于越界/共享结构，必须拒绝。
            if (IsMergeNode(node)
                && stopJoinId is not null
                && !_pairedJoins.Contains(node.Id))
                throw new InvalidOperationException(
                    $"Structured branch '{ownerId}' reached unexpected Join '{node.Id}' before its paired Join '{stopJoinId}'.");

            Claim(node.Id, ownerId);
            _visualOrder.Add(node.Id);

            if (node.Type.Equals("flow.break", StringComparison.OrdinalIgnoreCase))
            {
                // break 只支持直接写在 while/repeat 的循环体主序列里：它把"本轮剩余步骤"整体包进守卫，
                // 若出现在 If/Switch/Try/Parallel 分支内，分支退出后外层还有步骤会继续执行，语义不成立，
                // 因此明确拒绝而不是给出半正确的行为。forEach 没有提前退出语义，同样拒绝。
                if (enclosingLoopId is null)
                    throw new InvalidOperationException($"flow.break '{node.Id}' must be placed inside a flow.while or flow.repeat body.");
                if (!breakAllowed)
                    throw new InvalidOperationException($"flow.break '{node.Id}' cannot be used inside this loop kind; only flow.while and flow.repeat support early exit (forEach has no break).");
                if (stopLoopEndId is null)
                    throw new InvalidOperationException($"flow.break '{node.Id}' must be wired directly in the loop body sequence; break inside If/Switch/Try/Parallel branches is not supported.");

                var breakOutgoing = _graph.Outgoing(node.Id);
                if (breakOutgoing.Count != 1)
                    throw new InvalidOperationException($"flow.break '{node.Id}' requires exactly one 'next' control edge; found {breakOutgoing.Count}.");
                if (breakOutgoing[0].SourcePort.Equals("exec", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"flow.break '{node.Id}' must use its 'next' control output.");

                var remaining = BuildSequence(breakOutgoing[0].TargetNodeId, stopJoinId, $"{ownerId}.afterBreak", depth + 1,
                    stopLoopEndId, enclosingLoopId, breakAllowed);
                items.Add(new StructuredBreakIr(node, remaining, depth));
                return new StructuredSequenceIr(items);
            }

            if (IsLoopNode(node))
            {
                var kind = LoopKindOf(node.Type);
                var label = LoopLabel(kind);
                var outgoing = _graph.Outgoing(node.Id);
                var expectedPorts = new[] { "body", "done" };
                var unexpected = outgoing.Where(e => !expectedPorts.Contains(e.SourcePort, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (unexpected.Length > 0)
                    throw new InvalidOperationException($"{label} '{node.Id}' has control edges from undeclared ports: {string.Join(", ", unexpected.Select(x => x.SourcePort))}.");
                var bodyEdge = SinglePort(outgoing, "body", node.Id);
                var doneEdge = SinglePort(outgoing, "done", node.Id);
                var pairedEnds = _graph.Workflow.Nodes.Where(candidate => candidate.Type.Equals("flow.loopEnd", StringComparison.OrdinalIgnoreCase) &&
                    _graph.Outgoing(candidate.Id).Count == 1 &&
                    _graph.Outgoing(candidate.Id)[0].SourcePort.Equals("repeat", StringComparison.OrdinalIgnoreCase) &&
                    _graph.Outgoing(candidate.Id)[0].TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (pairedEnds.Length != 1)
                    throw new InvalidOperationException($"{label} '{node.Id}' requires exactly one paired flow.loopEnd repeat edge; found {pairedEnds.Length}.");
                var loopEndId = pairedEnds[0].Id;
                var loopEndIncoming = _graph.Incoming(loopEndId);
                if (loopEndIncoming.Count != 1 || !_graph.Outgoing(loopEndId).Single().TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"flow.loopEnd '{loopEndId}' must have exactly one body predecessor and one repeat edge to {label} '{node.Id}'.");
                if (!_pairedLoopEnds.Add(loopEndId))
                    throw new InvalidOperationException($"flow.loopEnd '{loopEndId}' is paired with more than one loop node.");

                // 循环体内允许 flow.break 的只有 while/repeat（forEach 没有提前退出语义）。
                var body = BuildSequence(bodyEdge.TargetNodeId, stopJoinId: null, ownerId: $"{node.Id}.body", depth: depth + 1,
                    stopLoopEndId: loopEndId, enclosingLoopId: node.Id, breakAllowed: kind != "forEach");
                if (body.Items.Count == 0 || !body.NodeIds.Contains(loopEndId, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{label} '{node.Id}' body does not reach paired flow.loopEnd '{loopEndId}'.");
                var doneStart = _graph.Node(doneEdge.TargetNodeId);
                if (_claimed.Contains(doneStart.Id))
                    throw new InvalidOperationException($"{label} '{node.Id}' done route overlaps an already claimed node '{doneStart.Id}'.");

                LoopConditionSource? conditionSource = null;
                var maxIterations = 0;
                switch (kind)
                {
                    case "forEach":
                        ValidateDynamicLoopOutputs(node, body.NodeIds);
                        break;
                    case "while":
                        conditionSource = ResolveWhileConditionSource(node);
                        maxIterations = node.GetInt("maxIterations", 100);
                        break;
                    default:
                        // count 走端口时编译期拿不到值，用上限作为迭代预算；实际次数由运行时 total 决定。
                        maxIterations = HasConnectedDataInput(node.Id, "count") ? RepeatLoopNode.MaxCountCeiling : node.GetInt("count", 3);
                        break;
                }

                items.Add(kind == "forEach"
                    ? new StructuredForEachIr(node, body, loopEndId, depth)
                    : new StructuredLoopIr(node, kind, body, loopEndId, depth, conditionSource, maxIterations));
                _regions.Add(new StructuredControlRegion(node.Id, LoopRegionKind(kind), loopEndId, depth,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["body"] = body.NodeIds, ["done"] = [] }));
                currentId = doneEdge.TargetNodeId;
                continue;
            }

            if (node.Type.Equals("flow.loopEnd", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"flow.loopEnd '{node.Id}' is not paired with its active flow.forEach / flow.while / flow.repeat body.");

            if (IsIfNode(node))
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

                var trueBranch = BuildSequence(trueEdge.TargetNodeId, joinId, $"{node.Id}.true", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed);
                var falseBranch = BuildSequence(falseEdge.TargetNodeId, joinId, $"{node.Id}.false", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed);
                ValidateNoCrossBranchData(node.Id, trueBranch.NodeIds, falseBranch.NodeIds);

                items.Add(new StructuredIfIr(node, trueBranch, falseBranch, joinId, depth));
                _regions.Add(new StructuredControlRegion(
                    node.Id,
                    node.Type.Equals("flow.ifBoolean", StringComparison.OrdinalIgnoreCase) ? "IfBoolean" : "If",
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

            if (node.Type.Equals("flow.switch", StringComparison.OrdinalIgnoreCase))
            {
                var cases = SwitchNode.ReadCases(node);
                var outgoing = _graph.Outgoing(node.Id);
                var expectedPorts = cases.Select((_, index) => $"case{index + 1}").Append("default").ToArray();
                var unexpected = outgoing.Where(edge => !expectedPorts.Contains(edge.SourcePort, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (unexpected.Length > 0)
                    throw new InvalidOperationException($"Switch '{node.Id}' has control edges from undeclared ports: {string.Join(", ", unexpected.Select(x => x.SourcePort))}.");
                var branchEdges = expectedPorts.Select(port => SinglePort(outgoing, port, node.Id)).ToArray();
                var joinId = _graph.FindNearestCommonJoin(branchEdges.Select(x => x.TargetNodeId).ToArray(), stopJoinId, node.Id);
                var merge = _graph.Node(joinId);
                if (!merge.Type.Equals("flow.merge", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Switch '{node.Id}' requires a flow.merge convergence node; found '{merge.Type}' at '{joinId}'.");
                var mergeEdges = _graph.Incoming(joinId);
                var mergePorts = expectedPorts.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (mergeEdges.Count != expectedPorts.Length || mergeEdges.Any(edge => !mergePorts.Contains(edge.TargetPort)) ||
                    expectedPorts.Any(port => mergeEdges.Count(edge => edge.TargetPort.Equals(port, StringComparison.OrdinalIgnoreCase)) != 1))
                    throw new InvalidOperationException($"Switch '{node.Id}' casesJson must match exactly one incoming flow.merge control port per configured case and default (case1..caseN, default).");
                var branches = new List<StructuredSwitchBranch>(branchEdges.Length);
                for (var index = 0; index < branchEdges.Length; index++)
                {
                    var edge = branchEdges[index];
                    var port = expectedPorts[index];
                    EnsureNonEmptyBranch(node.Id, port, edge.TargetNodeId, joinId);
                    var branch = BuildSequence(edge.TargetNodeId, joinId, $"{node.Id}.{port}", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed);
                    var matchingMergeEdge = mergeEdges.Single(x => x.TargetPort.Equals(port, StringComparison.OrdinalIgnoreCase));
                    if (!branch.NodeIds.Contains(matchingMergeEdge.SourceNodeId, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Switch '{node.Id}' branch '{port}' must converge through the matching flow.merge input '{port}'.");
                    branches.Add(new StructuredSwitchBranch(port, index < cases.Length ? cases[index] : "default", branch));
                }
                for (var i = 0; i < branches.Count; i++)
                for (var j = i + 1; j < branches.Count; j++)
                    ValidateNoCrossBranchData(node.Id, branches[i].Sequence.NodeIds, branches[j].Sequence.NodeIds);

                items.Add(new StructuredSwitchIr(node, branches, joinId, depth));
                _regions.Add(new StructuredControlRegion(node.Id, "Switch", joinId, depth,
                    branches.ToDictionary(x => x.Port, x => x.Sequence.NodeIds, StringComparer.OrdinalIgnoreCase)));
                _pairedJoins.Add(joinId);
                currentId = joinId;
                continue;
            }

            if (node.Type.Equals("flow.try", StringComparison.OrdinalIgnoreCase))
            {
                var outgoing = _graph.Outgoing(node.Id);
                var needsError = node.GetString("failurePolicy", "route") != "rethrow"
                    || outgoing.Any(edge => edge.SourcePort.Equals("error", StringComparison.OrdinalIgnoreCase));
                var expected = needsError ? new[] { "body", "success", "error" } : ["body", "success"];
                var unexpected = outgoing.Where(edge => !expected.Contains(edge.SourcePort, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (unexpected.Length > 0)
                    throw new InvalidOperationException($"Try '{node.Id}' has control edges from undeclared ports: {string.Join(", ", unexpected.Select(x => x.SourcePort))}.");
                var edges = expected.Select(port => SinglePort(outgoing, port, node.Id)).ToArray();
                var joinId = _graph.FindNearestCommonJoin(edges.Select(x => x.TargetNodeId).ToArray(), stopJoinId, node.Id);
                var merge = _graph.Node(joinId);
                if (!merge.Type.Equals("flow.merge", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Try '{node.Id}' requires a flow.merge convergence node; found '{merge.Type}' at '{joinId}'.");
                var incoming = _graph.Incoming(joinId);
                var targetPorts = needsError ? new[] { "case1", "case2", "default" } : ["case1", "case2"];
                if (incoming.Count != targetPorts.Length || targetPorts.Any(port => incoming.Count(edge => edge.TargetPort.Equals(port, StringComparison.OrdinalIgnoreCase)) != 1) ||
                    incoming.Any(edge => !targetPorts.Contains(edge.TargetPort, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Try '{node.Id}' requires exactly one matching merge input per configured branch: case1/body, case2/success, and default/error when routing failures.");

                var body = BuildSequence(edges[0].TargetNodeId, joinId, $"{node.Id}.body", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed);
                var success = BuildSequence(edges[1].TargetNodeId, joinId, $"{node.Id}.success", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed);
                var error = needsError ? BuildSequence(edges[2].TargetNodeId, joinId, $"{node.Id}.error", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed) : new StructuredSequenceIr([]);
                ValidateNoCrossBranchData(node.Id, body.NodeIds, error.NodeIds);
                ValidateNoCrossBranchData(node.Id, success.NodeIds, error.NodeIds);
                var branches = needsError ? new[] { body, success, error } : [body, success];
                for (var index = 0; index < branches.Length; index++)
                {
                    var port = targetPorts[index];
                    var matching = incoming.Single(x => x.TargetPort.Equals(port, StringComparison.OrdinalIgnoreCase));
                    if (!branches[index].NodeIds.Contains(matching.SourceNodeId, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Try '{node.Id}' branch '{expected[index]}' must converge through flow.merge input '{port}'.");
                }
                items.Add(new StructuredTryIr(node, body, success, error, joinId, depth));
                _tryBodies[node.Id] = body.NodeIds;
                _regions.Add(new StructuredControlRegion(node.Id, "Try", joinId, depth,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["body"] = body.NodeIds, ["success"] = success.NodeIds, ["error"] = error.NodeIds
                    }));
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

                var branch1 = BuildSequence(branch1Edge.TargetNodeId, joinId, $"{node.Id}.branch1", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed);
                var branch2 = BuildSequence(branch2Edge.TargetNodeId, joinId, $"{node.Id}.branch2", depth + 1, enclosingLoopId: enclosingLoopId, breakAllowed: breakAllowed);
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

    private void ValidateDynamicLoopOutputs(NodeDefinition loop, IReadOnlyList<string> bodyNodeIds)
    {
        var body = bodyNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dynamicPorts = new HashSet<string>(["index", "center", "circle", "rectangle", "x", "y", "confidence", "classId"], StringComparer.OrdinalIgnoreCase);
        var external = _graph.Workflow.Edges.FirstOrDefault(edge => !WorkflowGraph.IsControlEdge(edge) &&
            edge.SourceNodeId.Equals(loop.Id, StringComparison.OrdinalIgnoreCase) &&
            dynamicPorts.Contains(edge.SourcePort) && !body.Contains(edge.TargetNodeId));
        if (external is not null)
            throw new InvalidOperationException($"ForEach '{loop.Id}' dynamic output '{external.SourcePort}' may only be consumed inside its body (edge '{external.Id}').");
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

    private static bool IsIfNode(NodeDefinition node) =>
        node.Type.Equals("flow.if", StringComparison.OrdinalIgnoreCase) ||
        node.Type.Equals("flow.ifBoolean", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopNode(NodeDefinition node) =>
        node.Type.Equals("flow.forEach", StringComparison.OrdinalIgnoreCase) ||
        node.Type.Equals("flow.while", StringComparison.OrdinalIgnoreCase) ||
        node.Type.Equals("flow.repeat", StringComparison.OrdinalIgnoreCase);

    private static string LoopKindOf(string type) =>
        type.Equals("flow.while", StringComparison.OrdinalIgnoreCase) ? "while"
        : type.Equals("flow.repeat", StringComparison.OrdinalIgnoreCase) ? "repeat"
        : "forEach";

    private static string LoopLabel(string kind) => kind switch
    {
        "while" => "flow.while",
        "repeat" => "flow.repeat",
        _ => "ForEach"
    };

    private static string LoopRegionKind(string kind) => kind switch
    {
        "while" => "While",
        "repeat" => "Repeat",
        _ => "ForEach"
    };

    /// <summary>
    /// flow.while 的条件来源必须是一条数据边接到它的 condition 输入。
    /// 该来源通常在循环体内部（本轮结果），也可以在循环之前（此时条件恒定，靠迭代预算兜底）。
    /// </summary>
    private LoopConditionSource ResolveWhileConditionSource(NodeDefinition node)
    {
        var edge = _graph.Workflow.Edges.SingleOrDefault(candidate => !WorkflowGraph.IsControlEdge(candidate)
            && candidate.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
            && candidate.TargetPort.Equals("condition", StringComparison.OrdinalIgnoreCase));
        if (edge is null)
            throw new InvalidOperationException(
                $"flow.while '{node.Id}' requires its 'condition' input to be connected. Wire a boolean that must be re-checked each iteration " +
                "(for example flow.result.pass, value.range.pass or logic.boolean.result) instead of relying on a constant.");
        return new LoopConditionSource(edge.SourceNodeId, edge.SourcePort);
    }

    private bool HasConnectedDataInput(string nodeId, string port) =>
        _graph.Workflow.Edges.Any(edge => !WorkflowGraph.IsControlEdge(edge)
            && edge.TargetNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
            && edge.TargetPort.Equals(port, StringComparison.OrdinalIgnoreCase));

    private static bool IsMergeNode(NodeDefinition node) =>
        node.Type.Equals("flow.join", StringComparison.OrdinalIgnoreCase) ||
        node.Type.Equals("flow.merge", StringComparison.OrdinalIgnoreCase);

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
            if (node.Type.Equals("flow.join", StringComparison.OrdinalIgnoreCase) ||
                node.Type.Equals("flow.merge", StringComparison.OrdinalIgnoreCase))
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
