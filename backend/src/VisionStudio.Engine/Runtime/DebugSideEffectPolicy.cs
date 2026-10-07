namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Node-debug execution policy for nodes with external side effects (PLC writes, robot commands).
///
/// RunNode / RunFromNode / Breakpoints execute predecessors for real ("warmup"), so a node whose
/// catalog declares SupportsRunNode=false must not be executed implicitly by a debug run unless the
/// caller explicitly approves it through VisionRunOptions.AllowSideEffects. The explicitly selected
/// target node itself is always allowed - executing it is a deliberate operator action.
///
/// Implicit set per mode (debug modes reject Parallel workflows, so execution order is a linear-ish
/// graph; if/else prefixes are checked conservatively - a node is flagged even if its branch may not
/// be taken):
///  - RunNode / RunFromNode: every node ordered before the target;
///  - Breakpoints: every node in the workflow (the halt point is only known at runtime).
/// Read-only acquisitions (image.acquire / camera.syncCapture) are intentionally not flagged: replay
/// and tuning substitute their source, and live debugging starts from a camera grab.
/// </summary>
public static class DebugSideEffectPolicy
{
    public static IReadOnlyList<NodeDefinition> FindImplicitSideEffectNodes(
        WorkflowDefinition workflow,
        IReadOnlyList<string> orderedNodeIds,
        DebugRunMode mode,
        string? targetNodeId,
        Func<string, bool> requiresSideEffectApproval)
    {
        if (mode == DebugRunMode.Full) return [];

        var targetIndex = mode is DebugRunMode.RunNode or DebugRunMode.RunFromNode
            ? IndexOf(orderedNodeIds, targetNodeId)
            : int.MaxValue;

        var risks = new List<NodeDefinition>();
        foreach (var node in workflow.Nodes)
        {
            if (!requiresSideEffectApproval(node.Type)) continue;
            var implicitExecution = mode switch
            {
                DebugRunMode.RunNode or DebugRunMode.RunFromNode =>
                    !string.Equals(node.Id, targetNodeId, StringComparison.OrdinalIgnoreCase) &&
                    IsOrderedBefore(orderedNodeIds, node.Id, targetIndex),
                DebugRunMode.Breakpoints => true,
                _ => false
            };
            if (implicitExecution) risks.Add(node);
        }
        return risks;
    }

    private static bool IsOrderedBefore(IReadOnlyList<string> orderedNodeIds, string nodeId, int targetIndex)
    {
        var index = IndexOf(orderedNodeIds, nodeId);
        return index < 0 || targetIndex < 0 || index < targetIndex;
    }

    private static int IndexOf(IReadOnlyList<string> orderedNodeIds, string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return -1;
        for (var i = 0; i < orderedNodeIds.Count; i++)
            if (orderedNodeIds[i].Equals(nodeId, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
