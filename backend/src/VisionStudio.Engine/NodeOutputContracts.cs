namespace VisionStudio.Engine;

/// <summary>
/// Output-contract helpers that do not rewrite the catalog.
///
/// The catalog used to be post-processed here by three rules: an <c>Unimplemented</c> list that stripped ports no
/// executor ever emitted (coordinate.distance's frame/unit/coincident, geometry.angle's degrees/radians/quadrant,
/// flow.parallel's branchCount/joined, …), an <c>OverlayProducers</c> whitelist that decided which nodes may publish an
/// <c>overlay</c> image port, and a per-node patch that weakened required flags. All three made the source catalog
/// disagree with /api/catalog and all three went stale (flow.loopEnd's ports were already gone; 118 nodes declared an
/// overlay port they never produced; 38 declared a <c>preview</c> port nothing emits). Every dead declaration has been
/// removed from its own catalog, region.toDetections really emits emitted/truncated/totalArea, image.colorConvert
/// declares channel2/channel3 optional itself, and only the 16 overlay-image producers declare <c>overlay</c>.
/// The catalog is now published exactly as written: a declared output must be produced, and a produced output must be
/// declared. <c>NodeOutputContractTests</c> enforces that.
/// </summary>
internal static class NodeOutputContracts
{
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
