using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

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

    public static NodeExecutorResult EnrichExtras(NodeExecutorResult result, string nodeType, params (string Key, object? Value)[] extra)
    {
        var fields = new List<(string Key, object? Value)>();
        if (result.Summary.TryGetValue("count", out var c)) fields.Add(("componentCount", c));
        if (result.Summary.TryGetValue("area", out var a)) fields.Add(("area", a));
        if (result.Summary.TryGetValue("found", out var f)) fields.Add(("found", f));
        fields.AddRange(extra);
        var diag = CoordinateValidation.Diagnostics(nodeType, fields.ToArray());
        var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
        var summary = new Dictionary<string, object?>(result.Summary) { ["diagnostics"] = diag };
        foreach (var (k, v) in extra) summary[k] = v;
        return result with { Outputs = outputs, Summary = summary };
    }

    public static NodeExecutorResult Result(VisionRegion region, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var overlays = new List<VisionOverlay>();
        // Bounded bounding boxes label objects; the exact, hole-preserving shape is shown by the mask image.
        foreach (var component in region.Components.Take(200))
        {
            token.ThrowIfCancellationRequested();
            var x = component.Runs.Min(r => r.StartColumn); var y = component.Runs[0].Row;
            overlays.Add(new($"region-{overlays.Count}", VisionOverlayType.Rectangle, Label: $"{overlays.Count}", X: x, Y: y,
                Width: component.Runs.Max(r => r.EndColumn) - x + 1, Height: component.Runs[^1].Row - y + 1));
        }
        var area = JLVisionRegions.PixelArea(region);
        if (!double.IsFinite(area) || area < 0) throw new InvalidOperationException("Region area must be finite and non-negative.");
        var totalPixels = (double)Math.Max(1, region.Width) * Math.Max(1, region.Height);
        var largest = 0d;
        foreach (var component in region.Components)
        {
            token.ThrowIfCancellationRequested();
            var componentArea = 0d;
            for (var i = 0; i < component.Runs.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var run = component.Runs[i];
                componentArea += (double)run.EndColumn - run.StartColumn + 1;
            }
            largest = Math.Max(largest, componentArea);
        }
        var diag = CoordinateValidation.Diagnostics("region", ("componentCount", region.Components.Count), ("area", area), ("areaRatio", area / totalPixels), ("largestComponentArea", largest), ("width", region.Width), ("height", region.Height), ("empty", region.Components.Count == 0));
        token.ThrowIfCancellationRequested();
        var mask = JLVisionRegions.Mask(region);
        return NodeOutputContracts.CreateOwnedResult(mask, () => new(new Dictionary<string, VisionValue>
        {
            ["region"] = VisionValue.Region(region), ["image"] = VisionValue.Image(VisionImage.Own(mask)),
            ["count"] = VisionValue.Integer(region.Components.Count), ["area"] = VisionValue.Double(area),
            ["found"] = VisionValue.Boolean(region.Components.Count > 0), ["diagnostics"] = VisionValue.String(diag)
        }, new Dictionary<string, object?> { ["count"] = region.Components.Count, ["area"] = area, ["found"] = region.Components.Count > 0, ["diagnostics"] = diag }, overlays), token);
    }

    /// <summary>Finish a result whose image is a newly owned region mask; release that mask on failure.</summary>
    public static NodeExecutorResult Complete(NodeExecutorResult result, CancellationToken token, Func<NodeExecutorResult> finish)
        => NodeOutputContracts.CreateOwnedResult(result.Outputs["image"].Require<VisionImage>("image"), finish, token);
}

public sealed class RegionFromRangeNode : IVisionNodeExecutor
{
    public string Type => "region.fromRange";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var mode = CoordinateValidation.RequireNonEmpty(node.GetString("mode", "gray"), "mode").ToLowerInvariant();
        if (mode is not ("gray" or "color"))
            throw new InvalidOperationException($"Unsupported region range mode '{mode}'.");
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "empty"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail"))
            throw new InvalidOperationException("region.fromRange emptyPolicy must be empty or fail.");
        var (source, domain) = RegionNodeHelpers.Source(context);
        if (source.Mat is null) throw new InvalidOperationException("region.fromRange image mat must be non-null.");
        if (source.Width < 1 || source.Height < 1)
            throw new InvalidOperationException("region.fromRange requires a non-empty image.");
        if (source.Width > 16384 || source.Height > 16384)
            throw new InvalidOperationException("region.fromRange image must be at most 16384 per axis.");
        var inner = mode == "gray"
            ? RegionFromThresholdCore.Execute(context, node, Type, cancellationToken)
            : await new RegionFromColorRangeNode().ExecuteAsync(context, node, cancellationToken).ConfigureAwait(false);
        return RegionNodeHelpers.Complete(inner, cancellationToken, () =>
        {
            if (!inner.Outputs.TryGetValue("found", out var foundVal))
                throw new InvalidOperationException("region.fromRange inner result missing found.");
            var found = foundVal.Require<bool>("found");
            if (!found && emptyPolicy == "fail")
                throw new InvalidOperationException("region.fromRange produced an empty region and emptyPolicy=fail.");
            var diag = CoordinateValidation.Diagnostics(Type, ("mode", mode), ("emptyPolicy", emptyPolicy), ("found", found),
                ("width", source.Width), ("height", source.Height), ("hasDomain", domain is not null),
                ("pixelType", inner.Summary.GetValueOrDefault("pixelType")), ("grayLowerLimit", inner.Summary.GetValueOrDefault("grayLowerLimit")), ("grayUpperLimit", inner.Summary.GetValueOrDefault("grayUpperLimit")));
            var outputs = new Dictionary<string, VisionValue>(inner.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
            var summary = new Dictionary<string, object?>(inner.Summary) { ["mode"] = mode, ["emptyPolicy"] = emptyPolicy, ["diagnostics"] = diag };
            return inner with { Outputs = outputs, Summary = summary };
        });
    }
}

/// <summary>灰度区间分割实现：只由 region.fromRange(mode=gray) 调用，不再单独暴露为节点。</summary>
internal static class RegionFromThresholdCore
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string nodeType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(nodeType + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var (source, domain) = RegionNodeHelpers.Source(context);
        if (source.Width < 1 || source.Height < 1) throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        var minimum = node.GetDouble("minGray", 128); var maximum = node.GetDouble("maxGray", 255);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new InvalidOperationException($"{nodeType} minGray/maxGray must be finite with minGray <= maxGray.");
        var depth = source.Mat.Depth();
        var (pixelType, lowerLimit, upperLimit) = depth == MatType.CV_8U ? ("byte", 0d, 255d)
            : depth == MatType.CV_16U ? ("uint2", 0d, 65535d)
            : depth == MatType.CV_16S ? ("int2", -32768d, 32767d)
            : depth == MatType.CV_32S ? ("int4", (double)int.MinValue, (double)int.MaxValue)
            : depth == MatType.CV_32F ? ("real", -(double)float.MaxValue, (double)float.MaxValue)
            : throw new InvalidOperationException("灰度范围分割支持 byte、uint2、int2、int4 和 real 图像；请先转换像素类型。");
        if (minimum < lowerLimit || maximum > upperLimit)
            throw new InvalidOperationException($"输入像素类型为 {pixelType}，灰度上下限必须在 {lowerLimit}..{upperLimit} 内；不会隐式缩放为 8 位。");
        if (depth == MatType.CV_32F && !AdvancedImageValidation.FiniteFloatImage(source.Mat)) throw new InvalidOperationException("实数灰度图包含 NaN 或无穷值。");
        using var native = JLVisionImages.FromMat(source.Mat);
        using var channels = native.CountChannels();
        using var gray = channels.I == 1 ? native.CopyImage() : native.Rgb1ToGray();
        using var effectiveDomain = DefectNodeHelpers.Domain(gray, source.Width, source.Height, domain, context);
        using var limited = gray.ReduceDomain(effectiveDomain);
        using var threshold = limited.Threshold(minimum, maximum);
        var region = JLVisionRegions.FromNative(threshold, source.Width, source.Height, cancellationToken);
        var result = RegionNodeHelpers.Result(region, cancellationToken);
        var area = result.Outputs.TryGetValue("area", out var a) ? a.Value : null;
        var count = result.Outputs.TryGetValue("count", out var c) ? c.Value : null;
        var diag = CoordinateValidation.Diagnostics(nodeType, ("minGray", minimum), ("maxGray", maximum),
            ("width", source.Width), ("height", source.Height), ("channels", channels.I),
            ("pixelType", pixelType), ("grayLowerLimit", lowerLimit), ("grayUpperLimit", upperLimit),
            ("componentCount", count), ("area", area), ("count", count));
        var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
        var summary = new Dictionary<string, object?>(result.Summary) { ["minGray"] = minimum, ["maxGray"] = maximum, ["pixelType"] = pixelType, ["grayLowerLimit"] = lowerLimit, ["grayUpperLimit"] = upperLimit, ["diagnostics"] = diag };
        return result with { Outputs = outputs, Summary = summary };
    }
}

public sealed class RegionConnectedComponentsNode : IVisionNodeExecutor
{
    public string Type => "region.connectedComponents";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1)
            throw new InvalidOperationException("region.connectedComponents requires positive canvas dimensions.");
        if (region.Components is null)
            throw new InvalidOperationException("region.connectedComponents region.Components must be non-null.");
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "empty"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail")) throw new InvalidOperationException("region.connectedComponents emptyPolicy must be empty or fail.");
        if (region.Width > 16384 || region.Height > 16384)
            throw new InvalidOperationException("region.connectedComponents canvas must be at most 16384 per axis.");
        if (region.Components.Count > 100_000) throw new InvalidOperationException("region.connectedComponents refuses more than 100000 input objects.");
        using var native = JLVisionRegions.ToNative(region);
        using var union = native.Union1();
        using var connected = union.Connection();
        var result = RegionNodeHelpers.Result(JLVisionRegions.FromNative(connected, region.Width, region.Height, cancellationToken), cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.Complete(result, cancellationToken, () =>
        {
            var outCount = result.Summary.TryGetValue("count", out var cs) ? Convert.ToInt32(cs) : 0;
            if (outCount == 0 && emptyPolicy == "fail")
                throw new InvalidOperationException("region.connectedComponents produced no components and emptyPolicy=fail.");
            var diag = CoordinateValidation.Diagnostics(Type, ("inputComponentCount", region.Components.Count), ("outputCount", outCount),
                ("width", region.Width), ("height", region.Height), ("emptyPolicy", emptyPolicy));
            var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
            var summary = new Dictionary<string, object?>(result.Summary) { ["inputComponentCount"] = region.Components.Count, ["diagnostics"] = diag, ["emptyPolicy"] = emptyPolicy };
            return result with { Outputs = outputs, Summary = summary };
        }));
    }
}

public sealed class RegionFilterNode : IVisionNodeExecutor
{
    public string Type => "region.filter";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1) throw new InvalidOperationException("region.filter requires positive canvas dimensions.");
        if (region.Components is null) throw new InvalidOperationException("region.filter region.Components must be non-null.");
        var feature = node.GetString("feature", "area").Trim().ToLowerInvariant();
        if (feature is not ("area" or "circularity" or "width" or "height" or "rectangularity" or "anisometry")) throw new InvalidOperationException("Unsupported region feature.");
        var minimum = node.GetDouble("min", 0); var maximum = node.GetDouble("max", 99999999);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum < 0 || minimum > maximum) throw new InvalidOperationException("Invalid region filter range.");
        using var native = JLVisionRegions.ToNative(region);
        using var selected = native.SelectShape(feature, "and", minimum, maximum);
        var baseResult = RegionNodeHelpers.Result(JLVisionRegions.FromNative(selected, region.Width, region.Height, cancellationToken), cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.EnrichExtras(baseResult, Type, ("feature", feature), ("min", minimum), ("max", maximum), ("inputComponentCount", region.Components.Count)));
    }
}

public sealed class RegionBooleanNode : IVisionNodeExecutor
{
    public string Type => "region.boolean";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var a = context.Require<VisionRegion>("a"); var b = context.Require<VisionRegion>("b");
        if (a.Width < 1 || a.Height < 1 || b.Width < 1 || b.Height < 1)
            throw new InvalidOperationException("region.boolean requires positive canvas dimensions.");
        if (a.Width != b.Width || a.Height != b.Height) throw new InvalidOperationException("Region dimensions must match.");
        if (a.Components is null || b.Components is null) throw new InvalidOperationException("region.boolean Components must be non-null.");
        using var nativeA = JLVisionRegions.ToNative(a); using var nativeB = JLVisionRegions.ToNative(b);
        using var ua = nativeA.Union1(); using var ub = nativeB.Union1();
        var op = node.GetString("operation", "union").Trim().ToLowerInvariant();
        using var result = op switch
        {
            "union" => ua.Union2(ub), "intersection" => ua.Intersection(ub),
            "difference" => ua.Difference(ub), "xor" => ua.SymmDifference(ub),
            _ => throw new InvalidOperationException("Unsupported region boolean operation.")
        };
        var baseResult = RegionNodeHelpers.Result(JLVisionRegions.FromNative(result, a.Width, a.Height, cancellationToken), cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.EnrichExtras(baseResult, Type, ("operation", op), ("width", a.Width), ("height", a.Height)));
    }
}

public sealed class RegionFillHolesNode : IVisionNodeExecutor
{
    public string Type => "region.fillHoles";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var region = context.Require<VisionRegion>("region");
        if (region.Width < 1 || region.Height < 1)
            throw new InvalidOperationException("region.fillHoles requires positive canvas dimensions.");
        if (region.Components is null)
            throw new InvalidOperationException("region.fillHoles region.Components must be non-null.");
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "empty"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail")) throw new InvalidOperationException("region.fillHoles emptyPolicy must be empty or fail.");
        if (region.Width > 16384 || region.Height > 16384)
            throw new InvalidOperationException("region.fillHoles canvas must be at most 16384 per axis.");
        if (region.Components.Count > 100_000) throw new InvalidOperationException("region.fillHoles refuses more than 100000 input objects.");
        using var native = JLVisionRegions.ToNative(region); using var filled = native.FillUp();
        var result = RegionNodeHelpers.Result(JLVisionRegions.FromNative(filled, region.Width, region.Height, cancellationToken), cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.Complete(result, cancellationToken, () =>
        {
            var outCount = result.Summary.TryGetValue("count", out var cs) ? Convert.ToInt32(cs) : 0;
            if (outCount == 0 && emptyPolicy == "fail")
                throw new InvalidOperationException("region.fillHoles produced an empty region and emptyPolicy=fail.");
            var diag = CoordinateValidation.Diagnostics(Type, ("inputComponentCount", region.Components.Count), ("outputCount", outCount),
                ("width", region.Width), ("height", region.Height), ("emptyPolicy", emptyPolicy));
            var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
            var summary = new Dictionary<string, object?>(result.Summary) { ["inputComponentCount"] = region.Components.Count, ["diagnostics"] = diag, ["emptyPolicy"] = emptyPolicy };
            return result with { Outputs = outputs, Summary = summary };
        }));
    }
}

public sealed class ReduceDomainNode : IVisionNodeExecutor
{
    public string Type => "image.reduceDomain";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
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
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "fail"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("fail" or "passthrough"))
            throw new InvalidOperationException("image.reduceDomain emptyPolicy must be fail or passthrough.");
        var area = JLVisionRegions.PixelArea(region);
        var found = area > 0;
        if (!found && emptyPolicy == "fail")
            throw new InvalidOperationException("image.reduceDomain received an empty region and emptyPolicy=fail.");
        var diag = CoordinateValidation.Diagnostics(Type, ("found", found), ("area", area),
            ("width", source.Width), ("height", source.Height), ("emptyPolicy", emptyPolicy),
            ("regionObjects", region.Components.Count), ("intersectedPriorDomain", existing is not null));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["domainImage"] = VisionValue.DomainImage(new VisionDomainImage(source.CloneOwned(), region)),
            ["region"] = VisionValue.Region(region), ["area"] = VisionValue.Double(area),
            ["found"] = VisionValue.Boolean(found), ["diagnostics"] = VisionValue.String(diag)
        }, new Dictionary<string, object?> { ["found"] = found, ["area"] = area, ["width"] = source.Width, ["height"] = source.Height, ["emptyPolicy"] = emptyPolicy, ["diagnostics"] = diag }));
    }
}
