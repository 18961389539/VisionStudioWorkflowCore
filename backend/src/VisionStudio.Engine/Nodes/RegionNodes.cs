using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

internal static class RegionNodeHelpers
{
    public static (VisionImage Image, VisionRegion? Domain) Source(NodeExecutionContext context)
    {
        var plain = context.TryGetValue("image", out var image);
        var scoped = context.TryGetValue("domainImage", out var domainImage);
        if (plain == scoped) throw new InvalidOperationException("Connect exactly one of image or domainImage.");
        if (plain) return (image.Require<VisionImage>("image"), null);
        var domain = domainImage.Require<VisionDomainImage>("domainImage");
        return (domain.Image, domain.Domain);
    }

    public static NodeExecutorResult Result(VisionRegion region, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var overlays = new List<VisionOverlay>();
        // Bounded bounding boxes label objects; the exact, hole-preserving shape is shown by the mask image.
        foreach (var component in region.Components.Take(200))
        {
            var x = component.Runs.Min(r => r.StartColumn); var y = component.Runs[0].Row;
            overlays.Add(new($"region-{overlays.Count}", VisionOverlayType.Rectangle, Label: $"{overlays.Count}", X: x, Y: y,
                Width: component.Runs.Max(r => r.EndColumn) - x + 1, Height: component.Runs[^1].Row - y + 1));
        }
        var area = JLVisionRegions.PixelArea(region);
        var mask = JLVisionRegions.Mask(region);
        return new(new Dictionary<string, VisionValue>
        {
            ["region"] = VisionValue.Region(region), ["image"] = VisionValue.Image(VisionImage.Own(mask)),
            ["count"] = VisionValue.Integer(region.Components.Count), ["area"] = VisionValue.Double(area),
            ["found"] = VisionValue.Boolean(region.Components.Count > 0)
        }, new Dictionary<string, object?> { ["count"] = region.Components.Count, ["area"] = area, ["found"] = region.Components.Count > 0 }, overlays);
    }
}

public sealed class RegionFromRangeNode : IVisionNodeExecutor
{
    public string Type => "region.fromRange";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var mode = node.GetString("mode", "gray").Trim().ToLowerInvariant();
        return mode switch
        {
            "gray" => new RegionFromThresholdNode().ExecuteAsync(context, node, cancellationToken),
            "color" => new RegionFromColorRangeNode().ExecuteAsync(context, node, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported region range mode '{mode}'.")
        };
    }
}

public sealed class RegionFromThresholdNode : IVisionNodeExecutor
{
    public string Type => "region.fromThreshold";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (source, domain) = RegionNodeHelpers.Source(context);
        var minimum = node.GetDouble("minGray", 128); var maximum = node.GetDouble("maxGray", 255);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum) throw new InvalidOperationException("Invalid inclusive gray range.");
        using var native = JLVisionImages.FromMat(source.Mat);
        using var channels = native.CountChannels();
        using var gray = channels.I == 1 ? native.CopyImage() : native.Rgb1ToGray();
        using var effectiveDomain = DefectNodeHelpers.Domain(gray, source.Width, source.Height, domain, context);
        using var limited = gray.ReduceDomain(effectiveDomain);
        using var threshold = limited.Threshold(minimum, maximum);
        var region = JLVisionRegions.FromNative(threshold, source.Width, source.Height, cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.Result(region, cancellationToken));
    }
}

public sealed class RegionConnectedComponentsNode : IVisionNodeExecutor
{
    public string Type => "region.connectedComponents";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var region = context.Require<VisionRegion>("region");
        using var native = JLVisionRegions.ToNative(region);
        using var union = native.Union1();
        using var connected = union.Connection();
        return ValueTask.FromResult(RegionNodeHelpers.Result(JLVisionRegions.FromNative(connected, region.Width, region.Height, cancellationToken), cancellationToken));
    }
}

public sealed class RegionFilterNode : IVisionNodeExecutor
{
    public string Type => "region.filter";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var region = context.Require<VisionRegion>("region");
        var feature = node.GetString("feature", "area");
        if (feature is not ("area" or "circularity" or "width" or "height" or "rectangularity" or "anisometry")) throw new InvalidOperationException("Unsupported region feature.");
        var minimum = node.GetDouble("min", 0); var maximum = node.GetDouble("max", 99999999);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum < 0 || minimum > maximum) throw new InvalidOperationException("Invalid region filter range.");
        using var native = JLVisionRegions.ToNative(region);
        using var selected = native.SelectShape(feature, "and", minimum, maximum);
        return ValueTask.FromResult(RegionNodeHelpers.Result(JLVisionRegions.FromNative(selected, region.Width, region.Height, cancellationToken), cancellationToken));
    }
}

public sealed class RegionBooleanNode : IVisionNodeExecutor
{
    public string Type => "region.boolean";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = context.Require<VisionRegion>("a"); var b = context.Require<VisionRegion>("b");
        if (a.Width != b.Width || a.Height != b.Height) throw new InvalidOperationException("Region dimensions must match.");
        using var nativeA = JLVisionRegions.ToNative(a); using var nativeB = JLVisionRegions.ToNative(b);
        using var ua = nativeA.Union1(); using var ub = nativeB.Union1();
        using var result = node.GetString("operation", "union") switch
        {
            "union" => ua.Union2(ub), "intersection" => ua.Intersection(ub),
            "difference" => ua.Difference(ub), "xor" => ua.SymmDifference(ub),
            _ => throw new InvalidOperationException("Unsupported region boolean operation.")
        };
        return ValueTask.FromResult(RegionNodeHelpers.Result(JLVisionRegions.FromNative(result, a.Width, a.Height, cancellationToken), cancellationToken));
    }
}

public sealed class RegionFillHolesNode : IVisionNodeExecutor
{
    public string Type => "region.fillHoles";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var region = context.Require<VisionRegion>("region");
        using var native = JLVisionRegions.ToNative(region); using var filled = native.FillUp();
        return ValueTask.FromResult(RegionNodeHelpers.Result(JLVisionRegions.FromNative(filled, region.Width, region.Height, cancellationToken), cancellationToken));
    }
}

public sealed class ReduceDomainNode : IVisionNodeExecutor
{
    public string Type => "image.reduceDomain";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (source, existing) = RegionNodeHelpers.Source(context);
        var region = context.Require<VisionRegion>("region");
        if (source.Width != region.Width || source.Height != region.Height) throw new InvalidOperationException("Domain/image dimensions must match.");
        if (existing is not null)
        {
            using var a = JLVisionRegions.ToNative(existing); using var b = JLVisionRegions.ToNative(region);
            using var ua = a.Union1(); using var ub = b.Union1(); using var intersection = ua.Intersection(ub);
            region = JLVisionRegions.FromNative(intersection, source.Width, source.Height, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var area = JLVisionRegions.PixelArea(region);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["domainImage"] = VisionValue.DomainImage(new VisionDomainImage(source.CloneOwned(), region)),
            ["region"] = VisionValue.Region(region), ["area"] = VisionValue.Double(area)
        }, new Dictionary<string, object?> { ["area"] = area, ["width"] = source.Width, ["height"] = source.Height }));
    }
}
