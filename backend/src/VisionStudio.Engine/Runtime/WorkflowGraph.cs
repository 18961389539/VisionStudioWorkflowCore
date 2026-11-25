namespace VisionStudio.Engine.Runtime;

public static class WorkflowGraph
{
    private static readonly HashSet<string> ControlSourcePorts =
        new(["next", "true", "false", "branch1", "branch2"], StringComparer.OrdinalIgnoreCase);

    public static bool IsControlEdge(EdgeDefinition edge)
        => edge.IsControl || ControlSourcePorts.Contains(edge.SourcePort);

    public static bool IsControlEdge(EdgeDefinition edge, NodeCatalogItem sourceCatalog, NodeCatalogItem targetCatalog)
    {
        if (edge.IsControl) return true;
        var sourcePort = sourceCatalog.Outputs.FirstOrDefault(p => p.Name.Equals(edge.SourcePort, StringComparison.OrdinalIgnoreCase));
        var targetPort = targetCatalog.Inputs.FirstOrDefault(p => p.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase));
        return sourcePort?.DataType == VisionDataType.Control || targetPort?.DataType == VisionDataType.Control;
    }

    public static bool IsControlEdge(
        EdgeDefinition edge,
        IReadOnlyDictionary<string, NodeDefinition> nodesById,
        VisionNodeRegistry registry)
    {
        if (edge.IsControl) return true;
        if (nodesById.TryGetValue(edge.SourceNodeId, out var sourceNode)
            && registry.TryGet(sourceNode.Type, out var sourceRegistration)
            && sourceRegistration.Catalog.Outputs.Any(p =>
                p.Name.Equals(edge.SourcePort, StringComparison.OrdinalIgnoreCase)
                && p.DataType == VisionDataType.Control))
            return true;
        if (nodesById.TryGetValue(edge.TargetNodeId, out var targetNode)
            && registry.TryGet(targetNode.Type, out var targetRegistration)
            && targetRegistration.Catalog.Inputs.Any(p =>
                p.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase)
                && p.DataType == VisionDataType.Control))
            return true;
        return ControlSourcePorts.Contains(edge.SourcePort);
    }
}
