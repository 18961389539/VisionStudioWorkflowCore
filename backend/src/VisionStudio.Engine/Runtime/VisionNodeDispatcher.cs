using VisionStudio.Engine.Camera;

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
        result = AttachImageCoordinateFrames(node, registration.Catalog, inputs, result);
        ValidateOutputs(node, registration.Catalog, result.Outputs);
        return result;
    }

    private static NodeExecutorResult AttachImageCoordinateFrames(
        NodeDefinition node,
        NodeCatalogItem catalog,
        IReadOnlyDictionary<string, VisionValue> inputs,
        NodeExecutorResult result)
    {
        var type = node.Type;
        var imageInputPort = type.Equals("roi.transform", StringComparison.OrdinalIgnoreCase) ? "debugImage" : "image";
        var inputFrame = TryGetImageFrame(inputs, imageInputPort);
        var warpTargetFrame = type.Equals("image.warpAffine", StringComparison.OrdinalIgnoreCase)
            && inputs.TryGetValue("transform", out var warpTransformValue)
            ? warpTransformValue.Require<VisionTransform2D>("transform").TargetFrame
            : null;
        var outputFrame = type.Equals("image.crop", StringComparison.OrdinalIgnoreCase)
            ? $"image:{node.Id}:crop"
            : type.Equals("image.warpAffine", StringComparison.OrdinalIgnoreCase)
                ? warpTargetFrame ?? $"image:{node.Id}:spatial"
            : type.Equals("image.resize", StringComparison.OrdinalIgnoreCase)
                || type.Equals("image.rotate", StringComparison.OrdinalIgnoreCase)
                || type.Equals("image.perspective", StringComparison.OrdinalIgnoreCase)
                ? $"image:{node.Id}:spatial"
                : type.Equals("roi.transform", StringComparison.OrdinalIgnoreCase)
                    ? inputFrame ?? "CurrentImage"
                    : inputFrame ?? $"image:{node.Id}:source";

        Dictionary<string, VisionValue>? rewrittenOutputs = null;
        foreach (var port in catalog.Outputs.Where(port => port.DataType == VisionDataType.Image))
        {
            if (!result.Outputs.TryGetValue(port.Name, out var value) || value.Value is not VisionImage image)
                continue;
            var inputImage = inputs.TryGetValue(imageInputPort, out var inputValue) ? inputValue.Value : null;
            if (!ReferenceEquals(image, inputImage))
                image.SetCoordinateFrame(outputFrame);
        }

        if (result.Outputs.TryGetValue("transform", out var transformValue)
            && transformValue.Value is VisionTransform2D transform)
        {
            VisionTransform2D? framedTransform = null;
            if (type.Equals("image.crop", StringComparison.OrdinalIgnoreCase))
                framedTransform = transform with { SourceFrame = inputFrame ?? "CurrentImage", TargetFrame = outputFrame };
            else if (type.Equals("roi.transform", StringComparison.OrdinalIgnoreCase))
                framedTransform = transform with { SourceFrame = $"roi:{node.Id}:reference", TargetFrame = inputFrame ?? "CurrentImage" };
            else if (type.Equals("image.register", StringComparison.OrdinalIgnoreCase))
                framedTransform = transform with
                {
                    SourceFrame = inputFrame ?? "CurrentImage",
                    TargetFrame = TryGetImageFrame(inputs, "reference") ?? "ReferenceImage"
                };

            if (framedTransform is not null)
            {
                rewrittenOutputs = result.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                rewrittenOutputs["transform"] = VisionValue.Transform(framedTransform);
            }
        }

        return rewrittenOutputs is null ? result : result with { Outputs = rewrittenOutputs };
    }

    private static string? TryGetImageFrame(IReadOnlyDictionary<string, VisionValue> inputs, string port)
    {
        if (!inputs.TryGetValue(port, out var value) || value.Type != VisionDataType.Image)
            return null;
        return value.Value is VisionImage image ? image.CoordinateFrame : "CurrentImage";
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
