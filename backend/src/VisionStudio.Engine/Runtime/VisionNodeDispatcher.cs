namespace VisionStudio.Engine.Runtime;

public sealed class VisionNodeDispatcher(VisionNodeRegistry registry)
{
    public IReadOnlyDictionary<string, VisionValue> ResolveInputs(VisionWorkflowData data, NodeDefinition node)
        => ResolveInputs(node, data.IncomingDataEdges(node.Id), data.OutputsByNode, registry);

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeDefinition node,
        IReadOnlyDictionary<string, VisionValue> inputs,
        string? workflowScope,
        CancellationToken cancellationToken)
    {
        var registration = registry.Require(node.Type);
        var result = await registration.ExecuteAsync(new NodeExecutionContext(inputs, workflowScope), node, cancellationToken);
        ValidateOutputs(node, registration.Catalog, result.Outputs);
        return result;
    }

    private static Dictionary<string, VisionValue> ResolveInputs(
        NodeDefinition node,
        IReadOnlyList<EdgeDefinition> incomingDataEdges,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VisionValue>> outputsByNode,
        VisionNodeRegistry registry)
    {
        var result = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase);
        var targetCatalog = registry.Require(node.Type).Catalog;

        foreach (var edge in incomingDataEdges)
        {
            if (!outputsByNode.TryGetValue(edge.SourceNodeId, out var sourceOutputs))
                throw new InvalidOperationException($"Source node '{edge.SourceNodeId}' has not executed.");
            if (!sourceOutputs.TryGetValue(edge.SourcePort, out var value))
                throw new InvalidOperationException($"Output '{edge.SourcePort}' does not exist on '{edge.SourceNodeId}'.");

            var expected = targetCatalog.Inputs.FirstOrDefault(p => p.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Input port '{edge.TargetPort}' does not exist on node type '{node.Type}'.");
            if (!AreCompatible(value.Type, expected.DataType))
                throw new InvalidOperationException($"Runtime type mismatch on {edge.SourceNodeId}.{edge.SourcePort} -> {node.Id}.{edge.TargetPort}: {value.Type} -> {expected.DataType}.");

            result[edge.TargetPort] = value;
        }

        foreach (var required in targetCatalog.Inputs.Where(p => p.Required && p.DataType != VisionDataType.Control))
        {
            if (!result.ContainsKey(required.Name))
                throw new InvalidOperationException($"Required input '{node.Id}.{required.Name}' ({required.DataType}) is not connected.");
        }

        return result;
    }

    private static void ValidateOutputs(NodeDefinition node, NodeCatalogItem catalog, IReadOnlyDictionary<string, VisionValue> outputs)
    {
        foreach (var output in outputs)
        {
            var port = catalog.Outputs.FirstOrDefault(p => p.Name.Equals(output.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Executor '{node.Type}' returned undeclared output '{output.Key}'.");
            if (!AreCompatible(output.Value.Type, port.DataType))
                throw new InvalidOperationException($"Executor '{node.Type}' returned {output.Value.Type} on '{output.Key}', catalog declares {port.DataType}.");
        }
    }

    public static bool AreCompatible(VisionDataType source, VisionDataType target) =>
        source == target || source == VisionDataType.Any || target == VisionDataType.Any ||
        (source == VisionDataType.Integer && target == VisionDataType.Double);
}
