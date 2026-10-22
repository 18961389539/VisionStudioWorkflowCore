namespace VisionStudio.Engine;

/// <summary>Remove historical advertised-only ports and distinguish conditional values from guaranteed results.</summary>
internal static class NodeOutputContracts
{
    private static readonly IReadOnlyDictionary<string, string[]> Unimplemented = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["coordinate.transformCompose"] = ["valid"],
        ["coordinate.distance"] = ["frame", "unit", "coincident"],
        ["coordinate.frameTree"] = ["edgeCount", "valid"],
        ["coordinate.transformPoint3D"] = ["magnitude", "xyMagnitude", "azimuthDeg", "elevationDeg"],
        ["geometry.angle"] = ["degrees", "radians", "quadrant"],
        ["measure.concentricity"] = ["deltaX", "deltaY", "pass"],
        ["collection.detections.count"] = ["finiteCount", "invalidCount", "maxScore"],
        ["collection.detections.filter"] = ["keptRatio", "minKeptScore", "maxKeptScore"],
        ["region.toDetections"] = ["emitted", "truncated", "totalArea"],
        ["flow.loopEnd"] = ["iteration", "stopped", "reason"],
        ["flow.parallel"] = ["branchCount", "joined"],
    };

    private static readonly HashSet<string> OverlayProducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "inference.obb", "inference.segmentation", "inference.instanceSegmentation", "inference.pose",
        "jlvision.contourOp", "jlvision.contourFeature", "jlvision.geometry", "jlvision.homMat",
        "jlvision.matrixOp", "jlvision.measure1d", "jlvision.imageOp", "jlvision.regionFeature",
        "jlvision.regionOp", "jlvision.threshold"
    };

    internal static NodeCatalogItem Apply(NodeCatalogItem item)
    {
        Unimplemented.TryGetValue(item.Type, out var unsupported);
        var emitsOverlay = OverlayProducers.Contains(item.Type);
        return item with { Outputs = item.Outputs
            .Where(port => unsupported?.Contains(port.Name) != true
                && port.Name != "preview"
                && (port.Name != "overlay" || emitsOverlay))
            .Select(port => (item.Type == "logic.boolean" && port.Name == "b")
                || (item.Type == "image.colorConvert" && port.Name is "channel2" or "channel3")
                    ? port with { Required = false } : port).ToArray() };
    }

    /// <summary>Transfer a newly owned resource to the result only after construction and cancellation checks succeed.</summary>
    internal static NodeExecutorResult CreateOwnedResult(IDisposable owner, Func<NodeExecutorResult> create, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = create();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            try { owner.Dispose(); } catch { /* preserve the original construction error or cancellation */ }
            throw;
        }
    }

    internal static NodeExecutorResult Telemetry(NodeDefinition node, NodeCatalogItem catalog, NodeExecutorResult result, double elapsedMs)
    {
        if (catalog.PluginId != "builtin") return result;
        var outputs = result.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var port in catalog.Outputs.Where(port => !outputs.ContainsKey(port.Name)))
        {
            switch (port.Name)
            {
                case "warningCount": outputs[port.Name] = VisionValue.Integer(Count("warningCount", 0)); break;
                case "attemptCount": outputs[port.Name] = VisionValue.Integer(Count("attemptCount", Count("attempts", 1))); break;
                case "elapsedMs": outputs[port.Name] = VisionValue.Double(elapsedMs); break;
                case "diagnostics":
                    outputs[port.Name] = VisionValue.String(result.Summary.TryGetValue("diagnostics", out var diagnostic) && diagnostic is string text
                        ? text : System.Text.Json.JsonSerializer.Serialize(new { node = node.Type, valid = true, elapsedMs }));
                    break;
            }
        }
        return result with { Outputs = outputs };

        long Count(string key, long fallback)
        {
            if (!result.Summary.TryGetValue(key, out var value) || value is null) return fallback;
            if (value is System.Text.Json.JsonElement json && json.TryGetInt64(out var integer)) return Math.Max(0, integer);
            return long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var count) ? Math.Max(0, count) : fallback;
        }
    }
}
