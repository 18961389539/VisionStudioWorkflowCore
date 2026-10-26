using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Runtime;

public sealed class VisionNodeDispatcher(VisionNodeRegistry registry)
{
    public IReadOnlyDictionary<string, VisionValue> ResolveInputs(VisionWorkflowData data, NodeDefinition node)
        => ResolveInputs(node, data.IncomingDataEdges(node.Id), data.OutputsByNode, registry);

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeDefinition node,
        IReadOnlyDictionary<string, VisionValue> inputs,
        string? workflowScope,
        CancellationToken cancellationToken)
        => ExecuteAsync(node, inputs, workflowScope, cancellationToken, runId: null);

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeDefinition node,
        IReadOnlyDictionary<string, VisionValue> inputs,
        string? workflowScope,
        CancellationToken cancellationToken,
        string? runId)
    {
        var registration = registry.Require(node.Type);
        ValidateInputs(node, registration.Catalog, inputs);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await registration.ExecuteAsync(new NodeExecutionContext(inputs, workflowScope, runId), node, cancellationToken);
        try
        {
            result = NodeOutputContracts.Telemetry(node, registration.Catalog, result, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            result = AttachImageCoordinateFrames(node, registration.Catalog, inputs, result);
            ValidateOutputs(node, registration.Catalog, result.Outputs);
            return result;
        }
        catch
        {
            // A rejected contract must release returned resources before they enter the run's ownership table.
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var value in result.Outputs.Values)
                if (value.Value is IDisposable disposable && released.Add(disposable)
                    && !inputs.Values.Any(input => ReferenceEquals(input.Value, disposable)))
                    try { disposable.Dispose(); } catch { /* preserve the contract error */ }
            throw;
        }
    }

    private static NodeExecutorResult AttachImageCoordinateFrames(
        NodeDefinition node,
        NodeCatalogItem catalog,
        IReadOnlyDictionary<string, VisionValue> inputs,
        NodeExecutorResult result)
    {
        // 帧语义来自目录声明（内置与插件同一路径）：宿主不再按节点类型字符串猜测。
        var rule = NodeFrameSemantics.Resolve(node, catalog);
        var imageInputPort = rule?.InputPort ?? "image";
        var inputFrame = TryGetImageFrame(inputs, imageInputPort);
        var outputFrame = NodeFrameSemantics.ImageFrameKey(
            node.Id,
            rule,
            port => TryGetImageFrame(inputs, port),
            port => inputs.TryGetValue(port, out var value) && value.Value is VisionTransform2D transform
                ? transform.TargetFrame
                : null) ?? inputFrame ?? $"image:{node.Id}:source";

        Dictionary<string, VisionValue>? rewrittenOutputs = null;
        foreach (var port in catalog.Outputs.Where(port => port.DataType == VisionDataType.Image))
        {
            if (!result.Outputs.TryGetValue(port.Name, out var value) || value.Value is not VisionImage image)
                continue;
            var inputImage = inputs.TryGetValue(imageInputPort, out var inputValue) ? inputValue.Value : null;
            if (!ReferenceEquals(image, inputImage))
                image.SetCoordinateFrame(outputFrame);
        }

        if (rule?.Transform is { } transformPolicy
            && transformPolicy != NodeTransformFramePolicy.None
            && result.Outputs.TryGetValue("transform", out var transformValue)
            && transformValue.Value is VisionTransform2D transform)
        {
            var framedTransform = transformPolicy switch
            {
                NodeTransformFramePolicy.Spatial => transform with
                {
                    SourceFrame = inputFrame ?? "CurrentImage",
                    TargetFrame = outputFrame
                },
                NodeTransformFramePolicy.RoiReference => transform with
                {
                    SourceFrame = $"roi:{node.Id}:reference",
                    TargetFrame = inputFrame ?? "CurrentImage"
                },
                NodeTransformFramePolicy.ReferenceInput => transform with
                {
                    SourceFrame = inputFrame ?? "CurrentImage",
                    TargetFrame = TryGetImageFrame(inputs, rule.ReferencePort ?? "reference") ?? "ReferenceImage"
                },
                _ => null
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
            var expected = targetCatalog.Inputs.FirstOrDefault(p => p.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Input port '{edge.TargetPort}' does not exist on node type '{node.Type}'.");

            if (!outputsByNode.TryGetValue(edge.SourceNodeId, out var sourceOutputs))
            {
                // 回边（loop condition）在首轮尚未执行：可选输入按"未接线"处理，
                // 必填输入仍然报错，避免把真正的接线错误吞掉。
                if (!expected.IsRequired(node, targetCatalog)) continue;
                throw new InvalidOperationException($"Source node '{edge.SourceNodeId}' has not executed.");
            }
            if (!sourceOutputs.TryGetValue(edge.SourcePort, out var value))
            {
                if (!expected.IsRequired(node, targetCatalog)) continue;
                throw new InvalidOperationException($"Output '{edge.SourcePort}' does not exist on '{edge.SourceNodeId}'.");
            }

            if (!AreCompatible(value.Type, expected.DataType))
                throw new InvalidOperationException($"Runtime type mismatch on {edge.SourceNodeId}.{edge.SourcePort} -> {node.Id}.{edge.TargetPort}: {value.Type} -> {expected.DataType}.");

            if (!result.TryAdd(expected.Name, value))
                throw new InvalidOperationException($"Input '{node.Id}.{expected.Name}' has multiple data sources. One source per data input is required.");
        }

        ValidateInputs(node, targetCatalog, result);
        return result;
    }

    private static void ValidateInputs(NodeDefinition node, NodeCatalogItem catalog, IReadOnlyDictionary<string, VisionValue> inputs)
    {
        foreach (var required in catalog.Inputs.Where(p => p.DataType != VisionDataType.Control && p.IsRequired(node, catalog)))
        {
            if (!inputs.ContainsKey(required.Name))
                throw new InvalidOperationException($"Required input '{node.Id}.{required.Name}' ({required.DataType}) is not connected.");
        }
        foreach (var input in inputs)
        {
            var port = catalog.Inputs.FirstOrDefault(p => p.Name.Equals(input.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Input port '{input.Key}' does not exist on node type '{node.Type}'.");
            if (!AreCompatible(input.Value.Type, port.DataType))
                throw new InvalidOperationException($"Runtime type mismatch on {node.Id}.{input.Key}: {input.Value.Type} -> {port.DataType}.");
        }
    }

    private static void ValidateOutputs(NodeDefinition node, NodeCatalogItem catalog, IReadOnlyDictionary<string, VisionValue> outputs)
    {
        foreach (var required in catalog.Outputs.Where(port => port.DataType != VisionDataType.Control && port.IsRequired(node, catalog)))
            if (!outputs.ContainsKey(required.Name))
                throw new InvalidOperationException($"Executor '{node.Type}' did not return required output '{required.Name}' on node '{node.Id}'.");
        foreach (var output in outputs)
        {
            var port = catalog.Outputs.FirstOrDefault(p => p.Name.Equals(output.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Executor '{node.Type}' returned undeclared output '{output.Key}'.");
            if (!AreCompatible(output.Value.Type, port.DataType))
                throw new InvalidOperationException($"Executor '{node.Type}' returned {output.Value.Type} on '{output.Key}', catalog declares {port.DataType}.");
        }
    }

    public static bool AreCompatible(VisionDataType source, VisionDataType target)
    {
        if (source == VisionDataType.Control || target == VisionDataType.Control)
            return source == target;
        return source == target || source == VisionDataType.Any || target == VisionDataType.Any ||
            (source == VisionDataType.Integer && target == VisionDataType.Double);
    }
}
