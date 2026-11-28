using System.Text.Json;
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

    /// <summary>
    /// 连通域个数。阈值生成的区域把全部 run 放在同一个区域对象里，<c>count</c> 因此恒为 1；
    /// 要回答"检出几个目标"必须再连一次通。点面积远小于再执行一次 region.connectedComponents。
    /// </summary>
    public static int CountBlobs(VisionRegion region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var native = JLVisionRegions.ToNative(region);
        using var union = native.Union1();
        using var connected = union.Connection();
        return connected.CountObj();
    }

    /// <summary>整幅画布区域（每行一个 run）。反选时作为补集的底。</summary>
    public static VisionRegion FullCanvas(int width, int height)
    {
        var runs = new VisionRegionRun[height];
        for (var y = 0; y < height; y++) runs[y] = new(y, 0, width - 1);
        return new VisionRegion(width, height, [new VisionRegionComponent(runs)]);
    }

    /// <summary>二值掩膜 → 区域（ROI 求交用）。掩膜尺寸必须与目标区域一致。</summary>
    public static VisionRegion MaskRegion(Mat mask, CancellationToken cancellationToken)
    {
        using var image = JLVisionImages.FromMat(mask);
        using var native = image.Threshold(1d, 255d);
        return JLVisionRegions.FromNative(native, mask.Width, mask.Height, cancellationToken);
    }

    /// <summary>区域集合运算（union/intersection/difference/xor）；两端必须同尺寸。</summary>
    public static VisionRegion SetOperation(VisionRegion a, VisionRegion b, string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidOperationException("Region dimensions must match.");
        using var nativeA = JLVisionRegions.ToNative(a);
        using var nativeB = JLVisionRegions.ToNative(b);
        using var unionA = nativeA.Union1();
        using var unionB = nativeB.Union1();
        using var result = operation switch
        {
            "union" => unionA.Union2(unionB),
            "intersection" => unionA.Intersection(unionB),
            "difference" => unionA.Difference(unionB),
            "xor" => unionA.SymmDifference(unionB),
            _ => throw new InvalidOperationException("Unsupported region boolean operation.")
        };
        return JLVisionRegions.FromNative(result, a.Width, a.Height, cancellationToken);
    }

    public static void ValidateCompatible(VisionRegion a, VisionRegion b, string operation)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidOperationException($"{operation} canvas mismatch: A={a.Width}x{a.Height}, B={b.Width}x{b.Height}.");
        if (a.CoordinateSystem != b.CoordinateSystem || a.SchemaVersion != b.SchemaVersion)
            throw new InvalidOperationException($"{operation} coordinate contract mismatch: A={a.CoordinateSystem}/v{a.SchemaVersion}, B={b.CoordinateSystem}/v{b.SchemaVersion}.");
    }

    public static string SourceMappings(VisionRegion result, VisionRegion a, VisionRegion? b, string operation, CancellationToken token)
    {
        var aMap = MapContributors(result, a, token);
        var bMap = b is null ? null : MapContributors(result, b, token);
        var removedB = new HashSet<int>();
        if (b is not null && operation == "difference")
        {
            var removed = SetOperation(a, result, "difference", token);
            foreach (var index in MapContributors(removed, b, token).SelectMany(set => set)) removedB.Add(index);
        }
        var rows = new List<object>();
        for (var i = 0; i < result.Components.Count; i++)
        {
            var aObjects = aMap[i].OrderBy(x => x).ToArray();
            var bObjects = bMap?[i].OrderBy(x => x).ToArray() ?? [];
            var ownership = operation switch
            {
                "difference" => "a-only",
                "xor" => aObjects.Length > 0 && bObjects.Length == 0 ? "a-only" : bObjects.Length > 0 && aObjects.Length == 0 ? "b-only" : "invalid-overlap",
                "intersection" => "intersection",
                _ => aObjects.Length > 0 && bObjects.Length > 0 ? "a-and-b" : aObjects.Length > 0 ? "a-only" : "b-only"
            };
            rows.Add(new { outputIndex = i, aObjects, bObjects, ownership });
        }
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, operation, coordinateSystem = result.CoordinateSystem,
            sourceRegionIdA = a.SourceRegionId, sourceRegionIdB = b?.SourceRegionId,
            subtractedBObjects = operation == "difference" ? removedB.OrderBy(x => x).ToArray() : Array.Empty<int>(),
            outputs = rows
        });
    }

    private static List<HashSet<int>> MapContributors(VisionRegion result, VisionRegion source, CancellationToken token)
    {
        var rows = new Dictionary<int, List<(int ObjectIndex, VisionRegionRun Run)>>();
        for (var objectIndex = 0; objectIndex < source.Components.Count; objectIndex++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var run in source.Components[objectIndex].Runs)
            {
                if (!rows.TryGetValue(run.Row, out var list)) rows[run.Row] = list = [];
                list.Add((objectIndex, run));
            }
        }
        foreach (var list in rows.Values) list.Sort((x, y) => x.Run.StartColumn.CompareTo(y.Run.StartColumn));
        var maps = new List<HashSet<int>>();
        foreach (var component in result.Components)
        {
            token.ThrowIfCancellationRequested();
            var contributors = new HashSet<int>();
            foreach (var outputRun in component.Runs)
            {
                if (!rows.TryGetValue(outputRun.Row, out var candidates)) continue;
                foreach (var candidate in candidates)
                {
                    if (candidate.Run.EndColumn < outputRun.StartColumn) continue;
                    if (candidate.Run.StartColumn > outputRun.EndColumn) break;
                    contributors.Add(candidate.ObjectIndex);
                }
            }
            maps.Add(contributors);
        }
        return maps;
    }

    public static List<int[]> BridgedPairs(string mappingsJson)
    {
        using var document = JsonDocument.Parse(mappingsJson);
        var pairs = new HashSet<(int A, int B)>();
        if (document.RootElement.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputs.EnumerateArray())
            {
                if (!output.TryGetProperty("aObjects", out var objects) || objects.ValueKind != JsonValueKind.Array) continue;
                var indices = objects.EnumerateArray().Select(v => v.GetInt32()).OrderBy(v => v).ToArray();
                for (var i = 0; i < indices.Length; i++)
                for (var j = i + 1; j < indices.Length; j++) pairs.Add((indices[i], indices[j]));
            }
        }
        return pairs.Select(pair => new[] { pair.A, pair.B }).OrderBy(pair => pair[0]).ThenBy(pair => pair[1]).ToList();
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

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
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
        if (context.TryGetValue("region", out var regionValue))
        {
            var limitingRegion = regionValue.Require<VisionRegion>("region");
            if (limitingRegion.Width != source.Width || limitingRegion.Height != source.Height)
                throw new InvalidOperationException($"region.fromRange region size mismatch: source={source.Width}x{source.Height}, region={limitingRegion.Width}x{limitingRegion.Height}.");
            if (limitingRegion.CoordinateSystem != VisionRegion.ImagePixelCenterContract)
                throw new InvalidOperationException($"region.fromRange region coordinate contract '{limitingRegion.CoordinateSystem}' does not match image-pixel-center-v1.");
        }
        var inner = mode == "gray"
            ? RegionFromThresholdCore.Execute(context, node, Type, cancellationToken)
            : RegionFromColorRangeCore.Execute(context, node, Type, cancellationToken);
        VisionImage? maskImage = inner.Outputs.TryGetValue("image", out var imageValue)
            ? imageValue.Require<VisionImage>("image")
            : null;
        var innerSummary = inner.Summary;
        try
        {
            var region = inner.Outputs["region"].Require<VisionRegion>("region");
            var preInvertArea = JLVisionRegions.PixelArea(region);
            var adjusted = ApplyRoiAndInvert(region, domain, node, source.Width, source.Height, cancellationToken);
            if (!ReferenceEquals(adjusted, region))
            {
                // 集合运算改了区域，count/area/found/掩膜都必须按新区域重算；旧掩膜交回释放。
                var replacement = RegionNodeHelpers.Result(adjusted, cancellationToken);
                maskImage?.Dispose();
                maskImage = replacement.Outputs["image"].Require<VisionImage>("image");
                inner = replacement;
            }

            if (!inner.Outputs.TryGetValue("found", out var foundVal))
                throw new InvalidOperationException("region.fromRange inner result missing found.");
            var found = foundVal.Require<bool>("found");
            if (!found && emptyPolicy == "fail")
                throw new InvalidOperationException("region.fromRange produced an empty region and emptyPolicy=fail.");
            var blobCount = RegionNodeHelpers.CountBlobs(inner.Outputs["region"].Require<VisionRegion>("region"), cancellationToken);
            var finalArea = JLVisionRegions.PixelArea(inner.Outputs["region"].Require<VisionRegion>("region"));
            var graySource = context.TryGetValue("minGray", out _) || context.TryGetValue("maxGray", out _) ? "ports" : "parameters";
            var colorMin = new[] { node.GetDouble("min1", 0), node.GetDouble("min2", 0), node.GetDouble("min3", .5) };
            var colorMax = new[] { node.GetDouble("max1", 360), node.GetDouble("max2", 1), node.GetDouble("max3", 1) };
            var hsvWrap = mode == "color" && node.GetString("space", "hsv").Equals("hsv", StringComparison.OrdinalIgnoreCase) && colorMin[0] > colorMax[0];
            var diag = CoordinateValidation.Diagnostics(Type, ("mode", mode), ("emptyPolicy", emptyPolicy), ("found", found),
                ("width", source.Width), ("height", source.Height), ("hasDomain", domain is not null),
                ("pixelType", innerSummary.GetValueOrDefault("pixelType")), ("grayLowerLimit", innerSummary.GetValueOrDefault("grayLowerLimit")), ("grayUpperLimit", innerSummary.GetValueOrDefault("grayUpperLimit")),
                ("effectiveGrayMin", mode == "gray" ? innerSummary.GetValueOrDefault("minGray") : null),
                ("effectiveGrayMax", mode == "gray" ? innerSummary.GetValueOrDefault("maxGray") : null),
                ("grayRangeSource", mode == "gray" ? graySource : null),
                ("colorSpace", mode == "color" ? node.GetString("space", "hsv") : null),
                ("effectiveColorMin", mode == "color" ? colorMin : null), ("effectiveColorMax", mode == "color" ? colorMax : null),
                ("hsvHueWrap", hsvWrap), ("invert", node.GetBool("invert", false)),
                ("preInvertHitPixels", preInvertArea), ("postInvertHitPixels", finalArea), ("hasRoi", node.GetRoi() is not null), ("blobCount", blobCount));
            var outputs = new Dictionary<string, VisionValue>(inner.Outputs) { ["blobCount"] = VisionValue.Integer(blobCount), ["diagnostics"] = VisionValue.String(diag) };
            var summary = new Dictionary<string, object?>(innerSummary)
            {
                ["mode"] = mode, ["emptyPolicy"] = emptyPolicy, ["blobCount"] = blobCount,
                ["effectiveGrayMin"] = mode == "gray" ? innerSummary.GetValueOrDefault("minGray") : null,
                ["effectiveGrayMax"] = mode == "gray" ? innerSummary.GetValueOrDefault("maxGray") : null,
                ["grayRangeSource"] = mode == "gray" ? graySource : null,
                ["effectiveColorMin"] = mode == "color" ? colorMin : null, ["effectiveColorMax"] = mode == "color" ? colorMax : null,
                ["hsvHueWrap"] = hsvWrap, ["preInvertHitPixels"] = preInvertArea, ["postInvertHitPixels"] = finalArea,
                ["invert"] = node.GetBool("invert", false), ["hasRoi"] = node.GetRoi() is not null, ["diagnostics"] = diag
            };
            return ValueTask.FromResult(inner with { Outputs = outputs, Summary = summary });
        }
        catch
        {
            maskImage?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// ROI 与反选：ROI 先把结果限制在选区内；反选取补集，补集底依次是 ROI、region/domain、整幅画布，
    /// 这样"ROI 内不在区间内"不会把选区外的像素也算进来。未启用时原样返回同一个区域。
    /// </summary>
    private static VisionRegion ApplyRoiAndInvert(
        VisionRegion region, VisionRegion? domain, NodeDefinition node, int width, int height, CancellationToken cancellationToken)
    {
        var invert = node.GetBool("invert", false);
        var roi = node.GetRoi();
        if (roi is null && !invert) return region;

        VisionRegion? roiRegion = null;
        if (roi is not null)
        {
            using var mask = roi.BuildMask(new Size(width, height), cancellationToken);
            roiRegion = RegionNodeHelpers.MaskRegion(mask, cancellationToken);
            if (JLVisionRegions.PixelArea(roiRegion) <= 0)
                throw new InvalidOperationException($"ROI {roi.Type} covers no pixels in the {width}×{height} image; the range selection would be empty.");
        }

        var result = roiRegion is null ? region : RegionNodeHelpers.SetOperation(region, roiRegion, "intersection", cancellationToken);
        if (!invert) return result;
        var outside = roiRegion
            ?? domain
            ?? RegionNodeHelpers.FullCanvas(width, height);
        return RegionNodeHelpers.SetOperation(outside, result, "difference", cancellationToken);
    }
}

/// <summary>灰度区间分割实现：region.fromRange(mode=gray) 与历史别名 region.fromThreshold 共用。</summary>
internal static class RegionFromThresholdCore
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string nodeType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(nodeType + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var (source, domain) = RegionNodeHelpers.Source(context);
        if (source.Width < 1 || source.Height < 1) throw new InvalidOperationException($"{nodeType} requires a non-empty image.");
        var minimum = RangeBound(context, node, "minGray", 128); var maximum = RangeBound(context, node, "maxGray", 255);
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

    /// <summary>
    /// 灰度边界取值：接线端口优先，否则用参数。端口来自上游统计（例如良品均值 ±3σ），
    /// 让阈值随批次漂移自动跟随，而不用每次改参数。
    /// </summary>
    private static double RangeBound(NodeExecutionContext context, NodeDefinition node, string port, double fallback)
        => context.TryGetValue(port, out _) ? context.RequireNumber(port) : node.GetDouble(port, fallback);
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
        var connectivity = node.GetString("connectivity", "8").Trim().ToLowerInvariant();
        if (connectivity is not ("4" or "8")) throw new InvalidOperationException("region.connectedComponents connectivity must be 4 or 8.");
        VisionRegion connectedRegion;
        if (connectivity == "4")
        {
            using var mask = JLVisionRegions.Mask(region);
            var (components, _) = RegionFromMaskNode.BuildComponents(mask, cancellationToken, PixelConnectivity.Connectivity4);
            connectedRegion = new VisionRegion(region.Width, region.Height, components, region.CoordinateSystem, region.SchemaVersion, region.SourceRegionId);
        }
        else
        {
            using var native = JLVisionRegions.ToNative(region);
            using var union = native.Union1();
            using var connected = union.Connection();
            connectedRegion = JLVisionRegions.FromNative(connected, region.Width, region.Height, cancellationToken)
                .WithMetadata(region.CoordinateSystem, region.SchemaVersion, region.SourceRegionId);
        }
        var result = RegionNodeHelpers.Result(connectedRegion, cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.Complete(result, cancellationToken, () =>
        {
            var outCount = result.Summary.TryGetValue("count", out var cs) ? Convert.ToInt32(cs) : 0;
            if (outCount == 0 && emptyPolicy == "fail")
                throw new InvalidOperationException("region.connectedComponents produced no components and emptyPolicy=fail.");
            var mappings = RegionNodeHelpers.SourceMappings(connectedRegion, region, null, "connectedComponents", cancellationToken);
            var diag = CoordinateValidation.Diagnostics(Type, ("inputComponentCount", region.Components.Count), ("outputCount", outCount),
                ("width", region.Width), ("height", region.Height), ("emptyPolicy", emptyPolicy),
                ("connectivity", int.Parse(connectivity)), ("coordinateSystem", region.CoordinateSystem));
            var outputs = new Dictionary<string, VisionValue>(result.Outputs)
            {
                ["diagnostics"] = VisionValue.String(diag), ["sourceMappings"] = VisionValue.String(mappings),
                ["connectivity"] = VisionValue.Integer(int.Parse(connectivity))
            };
            var summary = new Dictionary<string, object?>(result.Summary)
            {
                ["inputComponentCount"] = region.Components.Count, ["diagnostics"] = diag, ["emptyPolicy"] = emptyPolicy,
                ["connectivity"] = int.Parse(connectivity), ["sourceMappings"] = mappings
            };
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
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum < 0 || minimum > maximum)
            throw new InvalidOperationException($"region.filter feature '{feature}' has an invalid closed interval [{minimum}, {maximum}].");
        if (feature is "circularity" or "rectangularity" && maximum > 1)
            throw new InvalidOperationException($"region.filter feature '{feature}' uses the closed unit interval 0..1.");
        if (feature == "anisometry" && minimum < 1)
            throw new InvalidOperationException("region.filter feature 'anisometry' uses the closed interval 1..infinity (major/minor axis ratio).");
        var measurements = RegionMeasurements.Read(region, cancellationToken);
        using var native = JLVisionRegions.ToNative(region);
        var anisometry = new Dictionary<int, double>();
        if (feature == "anisometry")
        {
            for (var i = 0; i < measurements.Count; i++)
            {
                using var item = native.SelectObj(i + 1);
                var ra = item.EllipticAxis(out double rb, out _);
                anisometry[i] = ra > 1e-12 && rb > 1e-12 ? Math.Max(ra, rb) / Math.Min(ra, rb) : 0;
            }
        }
        double FeatureValue(RegionObjectProperties p) => feature switch
        {
            "area" => p.Area, "width" => p.Rectangle.Width, "height" => p.Rectangle.Height,
            "circularity" => p.Circularity, "rectangularity" => p.Rectangularity,
            "anisometry" => anisometry.TryGetValue(p.Index, out var value) ? value : 0,
            _ => throw new InvalidOperationException("Unsupported region feature.")
        };
        var details = measurements.Select(p =>
        {
            var value = FeatureValue(p);
            var reason = value < minimum ? "below-min" : value > maximum ? "above-max" : "accepted";
            return new { sourceIndex = p.Index, sourceRegionId = p.SourceRegionId, feature, value, min = minimum, max = maximum, accepted = reason == "accepted", reason };
        }).ToArray();
        using var selected = native.SelectShape(feature, "and", minimum, maximum);
        var baseResult = RegionNodeHelpers.Result(JLVisionRegions.FromNative(selected, region.Width, region.Height, cancellationToken), cancellationToken);
        var detailsJson = JsonSerializer.Serialize(details);
        var definition = feature switch
        {
            "area" => "pixel count", "width" => "bounding-box width in pixels", "height" => "bounding-box height in pixels",
            "circularity" => "native circularity, normalized 0..1", "rectangularity" => "native rectangularity, normalized 0..1",
            _ => "equivalent-ellipse major/minor axis ratio, >=1"
        };
        var unit = feature is "circularity" or "rectangularity" or "anisometry" ? "ratio" : "px";
        var enriched = RegionNodeHelpers.EnrichExtras(baseResult, Type,
            ("feature", feature), ("featureDefinition", definition), ("unit", unit),
            ("min", minimum), ("max", maximum), ("inputComponentCount", region.Components.Count),
            ("filterDetails", details), ("acceptedCount", details.Count(item => item.accepted)));
        var outputs = new Dictionary<string, VisionValue>(enriched.Outputs) { ["filterDetails"] = VisionValue.String(detailsJson) };
        return ValueTask.FromResult(enriched with { Outputs = outputs });
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
        RegionNodeHelpers.ValidateCompatible(a, b, "region.boolean");
        if (a.Components is null || b.Components is null) throw new InvalidOperationException("region.boolean Components must be non-null.");
        var op = node.GetString("operation", "union").Trim().ToLowerInvariant();
        if (op is not ("union" or "intersection" or "difference" or "xor")) throw new InvalidOperationException("Unsupported region boolean operation.");
        var outputRegion = RegionNodeHelpers.SetOperation(a, b, op, cancellationToken)
            .WithMetadata(a.CoordinateSystem, a.SchemaVersion, a.SourceRegionId);
        var mappings = RegionNodeHelpers.SourceMappings(outputRegion, a, b, op, cancellationToken);
        var baseResult = RegionNodeHelpers.Result(outputRegion, cancellationToken);
        var enriched = RegionNodeHelpers.EnrichExtras(baseResult, Type, ("operation", op), ("width", a.Width), ("height", a.Height),
            ("aObjects", a.Components.Count), ("bObjects", b.Components.Count), ("aArea", JLVisionRegions.PixelArea(a)),
            ("bArea", JLVisionRegions.PixelArea(b)), ("unionArea", JLVisionRegions.PixelArea(RegionNodeHelpers.SetOperation(a, b, "union", cancellationToken))),
            ("coordinateSystemA", a.CoordinateSystem), ("coordinateSystemB", b.CoordinateSystem),
            ("schemaVersionA", a.SchemaVersion), ("schemaVersionB", b.SchemaVersion));
        var outputs = new Dictionary<string, VisionValue>(enriched.Outputs) { ["sourceMappings"] = VisionValue.String(mappings) };
        return ValueTask.FromResult(enriched with { Outputs = outputs, Summary = new Dictionary<string, object?>(enriched.Summary) { ["sourceMappings"] = mappings } });
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
        var maxHoleArea = node.GetInt("maxHoleArea", 0);
        if (maxHoleArea is < 0 or > 100000000) throw new InvalidOperationException("region.fillHoles maxHoleArea must be 0 (unlimited) or 1..100000000.");
        var (filledRegion, holes) = RegionHoleProcessing.Fill(region, maxHoleArea, cancellationToken);
        var result = RegionNodeHelpers.Result(filledRegion, cancellationToken);
        return ValueTask.FromResult(RegionNodeHelpers.Complete(result, cancellationToken, () =>
        {
            var outCount = result.Summary.TryGetValue("count", out var cs) ? Convert.ToInt32(cs) : 0;
            if (outCount == 0 && emptyPolicy == "fail")
                throw new InvalidOperationException("region.fillHoles produced an empty region and emptyPolicy=fail.");
            var inputArea = JLVisionRegions.PixelArea(region);
            var outputArea = JLVisionRegions.PixelArea(filledRegion);
            var holesJson = JsonSerializer.Serialize(holes);
            var diag = CoordinateValidation.Diagnostics(Type, ("inputComponentCount", region.Components.Count), ("outputCount", outCount),
                ("width", region.Width), ("height", region.Height), ("emptyPolicy", emptyPolicy),
                ("maxHoleArea", maxHoleArea), ("inputArea", inputArea), ("outputArea", outputArea),
                ("filledPixels", outputArea - inputArea), ("holes", holes));
            var outputs = new Dictionary<string, VisionValue>(result.Outputs)
            {
                ["diagnostics"] = VisionValue.String(diag), ["holesJson"] = VisionValue.String(holesJson),
                ["filledPixels"] = VisionValue.Integer(outputArea - inputArea)
            };
            var summary = new Dictionary<string, object?>(result.Summary)
            {
                ["inputComponentCount"] = region.Components.Count, ["diagnostics"] = diag, ["emptyPolicy"] = emptyPolicy,
                ["maxHoleArea"] = maxHoleArea, ["holes"] = holes, ["filledPixels"] = outputArea - inputArea
            };
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
        var requested = context.Require<VisionRegion>("region");
        if (source.Width != requested.Width || source.Height != requested.Height)
            throw new InvalidOperationException($"image.reduceDomain region size mismatch: image={source.Width}x{source.Height}, region={requested.Width}x{requested.Height}.");
        if (existing is not null && (source.Width != existing.Width || source.Height != existing.Height))
            throw new InvalidOperationException($"image.reduceDomain domain size mismatch: image={source.Width}x{source.Height}, domain={existing.Width}x{existing.Height}.");
        var requestedArea = JLVisionRegions.PixelArea(requested);
        var existingArea = existing is null ? (double?)null : JLVisionRegions.PixelArea(existing);
        var warnings = new List<object>();
        if (existing is not null && existingArea == 0)
            warnings.Add(new { code = "empty-input-domain", width = existing.Width, height = existing.Height, area = 0 });
        var region = requested;
        if (existing is not null)
        {
            using var a = JLVisionRegions.ToNative(existing); using var b = JLVisionRegions.ToNative(requested);
            using var ua = a.Union1(); using var ub = b.Union1(); using var intersection = ua.Intersection(ub);
            region = JLVisionRegions.FromNative(intersection, source.Width, source.Height, cancellationToken);
            if (requestedArea > 0 && existingArea > 0 && JLVisionRegions.PixelArea(region) == 0)
                warnings.Add(new { code = "disjoint-request-and-domain", requestedArea, existingArea, intersectionArea = 0, width = source.Width, height = source.Height });
        }
        cancellationToken.ThrowIfCancellationRequested();
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "fail"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("fail" or "passthrough"))
            throw new InvalidOperationException("image.reduceDomain emptyPolicy must be fail or passthrough.");
        var area = JLVisionRegions.PixelArea(region);
        var found = area > 0;
        if (!found)
            warnings.Add(new { code = "empty-effective-domain", requestedArea, existingArea, intersectionArea = area, width = source.Width, height = source.Height });
        if (!found && emptyPolicy == "fail")
            throw new InvalidOperationException("image.reduceDomain received an empty region and emptyPolicy=fail.");
        var warningsJson = JsonSerializer.Serialize(warnings);
        var diag = CoordinateValidation.Diagnostics(Type, ("found", found), ("area", area),
            ("requestedArea", requestedArea), ("existingArea", existingArea), ("effectiveArea", area),
            ("width", source.Width), ("height", source.Height), ("emptyPolicy", emptyPolicy),
            ("regionObjects", region.Components.Count), ("intersectedPriorDomain", existing is not null),
            ("warningCount", warnings.Count), ("warnings", warnings));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["domainImage"] = VisionValue.DomainImage(new VisionDomainImage(source.CloneOwned(), region)),
            ["region"] = VisionValue.Region(region), ["requestedRegion"] = VisionValue.Region(requested),
            ["area"] = VisionValue.Double(area), ["found"] = VisionValue.Boolean(found),
            ["requestedArea"] = VisionValue.Double(requestedArea), ["effectiveArea"] = VisionValue.Double(area),
            ["diagnostics"] = VisionValue.String(diag), ["warningsJson"] = VisionValue.String(warningsJson),
            ["warningCount"] = VisionValue.Integer(warnings.Count)
        };
        if (existing is not null)
        {
            outputs["existingRegion"] = VisionValue.Region(existing);
            outputs["existingArea"] = VisionValue.Double(existingArea ?? 0);
        }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
        {
            ["found"] = found, ["area"] = area, ["requestedArea"] = requestedArea, ["existingArea"] = existingArea,
            ["effectiveArea"] = area, ["width"] = source.Width, ["height"] = source.Height,
            ["emptyPolicy"] = emptyPolicy, ["warningCount"] = warnings.Count, ["warnings"] = warnings, ["diagnostics"] = diag
        }));
    }
}

internal static class RegionHoleProcessing
{
    public static (VisionRegion Region, List<object> Holes) Fill(VisionRegion source, int maxHoleArea, CancellationToken token)
    {
        using var mask = JLVisionRegions.Mask(source);
        using var background = new Mat();
        Cv2.BitwiseNot(mask, background);
        using var labels = new Mat();
        var labelCount = Cv2.ConnectedComponents(background, labels, PixelConnectivity.Connectivity8, MatType.CV_32S);
        var borderLabels = new HashSet<int>();
        void MarkBorder(int y, int x) { var label = labels.At<int>(y, x); if (label > 0) borderLabels.Add(label); }
        for (var x = 0; x < source.Width; x++) { MarkBorder(0, x); MarkBorder(source.Height - 1, x); }
        for (var y = 0; y < source.Height; y++) { MarkBorder(y, 0); MarkBorder(y, source.Width - 1); }
        var areas = new long[labelCount]; var sumX = new long[labelCount]; var sumY = new long[labelCount];
        for (var y = 0; y < source.Height; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            {
                var label = labels.At<int>(y, x);
                if (label <= 0) continue;
                areas[label]++; sumX[label] += x; sumY[label] += y;
            }
        }
        var holes = new List<object>(); long filledPixels = 0;
        for (var label = 1; label < labelCount; label++)
        {
            token.ThrowIfCancellationRequested();
            if (areas[label] == 0) continue;
            var open = borderLabels.Contains(label);
            var allowed = !open && (maxHoleArea == 0 || areas[label] <= maxHoleArea);
            var reason = open ? "open-to-canvas" : allowed ? "filled" : "above-max-hole-area";
            if (allowed)
            {
                using var holeMask = new Mat();
                Cv2.Compare(labels, label, holeMask, CmpTypes.EQ);
                mask.SetTo(Scalar.All(255), holeMask);
                filledPixels += areas[label];
            }
            holes.Add(new { holeIndex = holes.Count, label, area = areas[label], x = sumX[label] / (double)areas[label], y = sumY[label] / (double)areas[label], open, filled = allowed, reason });
        }
        if (filledPixels == 0 && source.Components.Count == 0) return (source.WithMetadata(), holes);
        var (components, _) = RegionFromMaskNode.BuildComponents(mask, token);
        return (new VisionRegion(source.Width, source.Height, components, source.CoordinateSystem, source.SchemaVersion, source.SourceRegionId), holes);
    }
}
