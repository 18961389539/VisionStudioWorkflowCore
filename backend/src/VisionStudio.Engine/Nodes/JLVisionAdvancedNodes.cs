using System.Text.Json;
using VisionStudio.Engine.Runtime;
using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

internal static class AdvancedJl
{
    internal sealed class OwnedOutputs : Dictionary<string, VisionValue>, IDisposable
    {
        internal bool Transferred { get; set; }
        public void Dispose()
        {
            if (Transferred) return;
            foreach (var owner in Values.Select(v => v.Value).OfType<IDisposable>().Distinct()) owner.Dispose();
        }
    }
    internal static double Number(NodeDefinition node, string name, double fallback, double min = -1e9, double max = 1e9)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value) || value < min || value > max)
            throw new InvalidOperationException($"{name} 必须在 {min}..{max} 范围内且为有限数值。");
        return value;
    }
    internal static int Integer(NodeDefinition node, string name, int fallback, int min = 0, int max = 100000)
    {
        var value = Number(node, name, fallback, min, max);
        if (value != Math.Truncate(value)) throw new InvalidOperationException($"{name} 必须为整数。");
        return (int)value;
    }
    internal static int OddInteger(NodeDefinition node, string name, int fallback, int min, int max)
    {
        var value = Integer(node, name, fallback, min, max);
        if (value % 2 == 0) throw new InvalidOperationException($"{name} 必须为 {min}..{max} 内的奇数，不会自动更改窗口尺寸。");
        return value;
    }
    internal static string Choice(NodeDefinition node, string name, string fallback, params string[] choices)
    {
        var value = node.GetString(name, fallback).Trim();
        if (!choices.Contains(value, StringComparer.Ordinal)) throw new InvalidOperationException($"{name}: {string.Join(" / ", choices)}。");
        return value;
    }
    internal static VisionValue Image(JlImage image) => VisionValue.Image(VisionImage.Own(JLVisionImages.ToMat(image)));
    internal static JlImage Source(NodeExecutionContext context, bool color = false)
    {
        var source = context.RequireImage("image");
        var image = JLVisionImages.FromMat(source.Mat);
        try
        {
            using var channels = image.CountChannels();
            if (color && channels.I != 3) throw new InvalidOperationException("此操作需要三通道彩色图像。");
            if (!color && channels.I == 3) { var gray = image.Rgb1ToGray(); image.Dispose(); return gray; }
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    internal static string Json(object value) => JsonSerializer.Serialize(value);
    internal static NodeExecutorResult Result(NodeDefinition node, string operation, Dictionary<string, VisionValue> outputs)
    {
        outputs["diagnostics"] = VisionValue.String(Json(new { operation, backend = "jlvision" }));
        var result = new NodeExecutorResult(outputs, new Dictionary<string, object?> { ["algorithmBackend"] = "jlvision", ["operator"] = operation });
        if (outputs is OwnedOutputs owned) owned.Transferred = true;
        return result;
    }
    internal static VisionPoint2D[] Points(JlTuple rows, JlTuple columns)
    {
        if (rows.Length != columns.Length || rows.Length > 2000000) throw new InvalidOperationException("原生点坐标数量无效或超出预算。");
        var points = Enumerable.Range(0, rows.Length).Select(i => new VisionPoint2D(columns[i].D, rows[i].D)).ToArray();
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) throw new InvalidOperationException("原生算法返回非有限坐标。");
        return points;
    }
    internal static double[] TupleNumbers(JlTuple tuple) => Enumerable.Range(0, tuple.Length).Select(i => tuple[i].D).ToArray();
    internal static JlTuple Tuple(string json, string name, bool mixed = false)
    {
        JsonElement[] values;
        try { values = JsonSerializer.Deserialize<JsonElement[]>(json) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException($"{name} 必须为 JSON 数组。", ex); }
        if (values.Length == 0 || values.Length > 10000) throw new InvalidOperationException($"{name} 不能为空，最多 10000 项。");
        return new JlTuple(values.Select(v => v.ValueKind == JsonValueKind.String && mixed ? (object)v.GetString()! :
            v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? (object)n :
            throw new InvalidOperationException($"{name} 含有无效元素。")).ToArray());
    }
}

public sealed class JLVisionFeatureExtractNode : IVisionNodeExecutor
{
    public string Type => "jlvision.featureExtract";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "linesGauss", "linesGauss", "linesFacet", "linesColor", "edgesSubPix", "edgesColorSubPix", "pointsHarris", "pointsFoerstner", "detectEdgeSegments");
        using var image = AdvancedJl.Source(context, operation is "linesColor" or "edgesColorSubPix");
        var sigma = operation is "linesFacet" or "detectEdgeSegments" ? 1 : AdvancedJl.Number(node, "sigma", 1, .1, 100);
        var usesThresholds = operation is not ("pointsHarris" or "pointsFoerstner");
        var low = operation == "detectEdgeSegments" ? AdvancedJl.Integer(node, "low", 5, 0, 1000000000)
            : usesThresholds ? AdvancedJl.Number(node, "low", 5, 0, 1e9) : 0;
        var high = usesThresholds && operation != "detectEdgeSegments" ? AdvancedJl.Number(node, "high", 10, low, 1e9) : low;
        JlXLDCont? curves = null;
        var points = new List<VisionPoint2D>();
        object? details = null;
        try
        {
            using var nativeSigma = new JlTuple(sigma); using var nativeLow = new JlTuple(low); using var nativeHigh = new JlTuple(high);
            var lightDark = node.GetString("polarity", "light");
            switch (operation)
            {
                case "linesGauss":
                    curves = image.LinesGauss(nativeSigma, nativeLow, nativeHigh, lightDark,
                        node.GetBool("extractWidth", true) ? "true" : "false", node.GetString("lineModel", "bar-shaped"), node.GetBool("completeJunctions", true) ? "true" : "false"); break;
                case "linesFacet": curves = image.LinesFacet(AdvancedJl.OddInteger(node, "maskSize", 5, 3, 39), nativeLow, nativeHigh, lightDark); break;
                case "linesColor": curves = image.LinesColor(nativeSigma, nativeLow, nativeHigh, node.GetBool("extractWidth", true) ? "true" : "false", node.GetBool("completeJunctions", true) ? "true" : "false"); break;
                case "edgesSubPix": curves = image.EdgesSubPix(node.GetString("filter", "canny"), sigma, nativeLow, nativeHigh); break;
                case "edgesColorSubPix": curves = image.EdgesColorSubPix(node.GetString("filter", "canny"), sigma, low, high); break;
                case "pointsHarris":
                    image.PointsHarris(sigma, AdvancedJl.Number(node, "sigmaSmooth", 2, .1, 100), AdvancedJl.Number(node, "alpha", .04, .001, .25),
                        AdvancedJl.Number(node, "threshold", 100, 0), out var rows, out var columns);
                    using (rows) using (columns) points.AddRange(AdvancedJl.Points(rows, columns)); break;
                case "pointsFoerstner":
                    image.PointsFoerstner(sigma, AdvancedJl.Number(node, "sigmaSmooth", 2, .1, 100), AdvancedJl.Number(node, "sigmaPoints", 2, .1, 100),
                        AdvancedJl.Number(node, "threshold", 100, 0), AdvancedJl.Number(node, "shapeThreshold", .3, 0, 1), "gauss", "true",
                        out var rj, out var cj, out var rrj, out var rcj, out var ccj, out var ra, out var ca, out var rra, out var rca, out var cca);
                    using (rj) using (cj) using (rrj) using (rcj) using (ccj) using (ra) using (ca) using (rra) using (rca) using (cca)
                    {
                        points.AddRange(AdvancedJl.Points(rj, cj)); points.AddRange(AdvancedJl.Points(ra, ca));
                        details = new { junctionCount = rj.Length, areaCount = ra.Length, covarianceJunctions = new[] { AdvancedJl.TupleNumbers(rrj), AdvancedJl.TupleNumbers(rcj), AdvancedJl.TupleNumbers(ccj) }, covarianceAreas = new[] { AdvancedJl.TupleNumbers(rra), AdvancedJl.TupleNumbers(rca), AdvancedJl.TupleNumbers(cca) } };
                    }
                    break;
                case "detectEdgeSegments":
                    image.DetectEdgeSegments(AdvancedJl.OddInteger(node, "sobelSize", 3, 3, 31), (int)low, AdvancedJl.Integer(node, "maxDistance", 2, 0, 1000), AdvancedJl.Integer(node, "minLength", 10, 1),
                        out var br, out var bc, out var er, out var ec);
                    using (br) using (bc) using (er) using (ec)
                    {
                        var begin = AdvancedJl.Points(br, bc); var end = AdvancedJl.Points(er, ec);
                        if (begin.Length != end.Length) throw new InvalidOperationException("线段端点数量不一致。");
                        var segments = begin.Select((p, i) => JlContourCodec.Segment(i, p, end[i])).ToArray();
                        curves = JlContourCodec.ToNative(segments);
                    }
                    break;
            }
            var contours = curves is null ? [] : JlContourCodec.FromNative(curves, cancellationToken);
            var count = curves is null ? points.Count : contours.Count;
            var outputs = new Dictionary<string, VisionValue> { ["contours"] = VisionValue.Contours(contours), ["points"] = VisionValue.String(AdvancedJl.Json(points)),
                ["details"] = VisionValue.String(AdvancedJl.Json(details ?? new { })), ["count"] = VisionValue.Integer(count), ["found"] = VisionValue.Boolean(count > 0) };
            var selected = curves is null ? AdvancedJl.Integer(node, "selectedIndex", 0) : 0;
            if (selected < points.Count) outputs["point"] = VisionValue.Point(points[selected]);
            var detections = curves is null
                ? points.Select((p, i) => new VisionDetection(i, p.X, p.Y, 0, 0, 0, 0, 0, new(p.X, p.Y, 0), new(p.X, p.Y, 0, 0))).ToArray()
                : contours.Select((c, i) => new VisionDetection(i, c.BoundingBox.X, c.BoundingBox.Y, c.BoundingBox.Width, c.BoundingBox.Height, c.Area, c.Perimeter, c.Circularity, c.MinEnclosingCircle, c.BoundingBox)).ToArray();
            outputs["detections"] = VisionValue.Detections(detections);
            return ValueTask.FromResult(AdvancedJl.Result(node, operation, outputs));
        }
        finally { curves?.Dispose(); }
    }
}

public sealed class JLVisionSegmentationNode : IVisionNodeExecutor
{
    public string Type => "jlvision.segmentation";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "watersheds", "watersheds", "watershedsMarker", "watershedsThreshold", "mser");
        var source = context.RequireImage("image"); using var image = AdvancedJl.Source(context);
        JlRegion? region = null; JlRegion? secondary = null;
        try
        {
            switch (operation)
            {
                case "watersheds": region = image.Watersheds(out secondary); break;
                case "watershedsThreshold": region = image.WatershedsThreshold(AdvancedJl.Integer(node, "threshold", 10, 0, 65535)); break;
                case "watershedsMarker":
                    var markers = context.Require<VisionRegion>("markers");
                    if (markers.Width != source.Width || markers.Height != source.Height || markers.Components.Count == 0) throw new InvalidOperationException("标记区域必须非空并与输入图像尺寸一致。");
                    using (var native = JLVisionRegions.ToNative(markers)) region = image.WatershedsMarker(native);
                    break;
                case "mser":
                    var min = AdvancedJl.Integer(node, "minArea", 10, 1); var max = AdvancedJl.Integer(node, "maxArea", 10000, min, 100000000);
                    using (var names = new JlTuple()) using (var values = new JlTuple())
                        region = image.SegmentImageMser(out secondary, node.GetString("polarity", "both"), min, max, AdvancedJl.Integer(node, "delta", 5, 1, 255), names, values);
                    break;
            }
            var output = JLVisionRegions.FromNative(region!, source.Width, source.Height, cancellationToken);
            var auxiliary = secondary is null ? null : JLVisionRegions.FromNative(secondary, source.Width, source.Height, cancellationToken);
            var count = output.Components.Count + (operation == "mser" ? auxiliary?.Components.Count ?? 0 : 0);
            var outputs = new Dictionary<string, VisionValue> { ["region"] = VisionValue.Region(output), ["count"] = VisionValue.Integer(count), ["found"] = VisionValue.Boolean(count > 0),
                ["primaryCount"] = VisionValue.Integer(output.Components.Count), ["secondaryCount"] = VisionValue.Integer(auxiliary?.Components.Count ?? 0) };
            if (auxiliary is not null) outputs["secondaryRegion"] = VisionValue.Region(auxiliary);
            if (operation == "mser")
            {
                outputs["darkRegion"] = VisionValue.Region(output);
                outputs["lightRegion"] = VisionValue.Region(auxiliary ?? new VisionRegion(source.Width, source.Height, []));
                outputs["darkCount"] = VisionValue.Integer(output.Components.Count);
                outputs["lightCount"] = VisionValue.Integer(auxiliary?.Components.Count ?? 0);
            }
            return ValueTask.FromResult(AdvancedJl.Result(node, operation, outputs));
        }
        finally { region?.Dispose(); secondary?.Dispose(); }
    }
}

public sealed class JLVisionRegionAnalysisNode : IVisionNodeExecutor
{
    public string Type => "jlvision.regionAnalysis";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "distanceTransform", "distanceTransform", "closestPointTransform", "spatialRelation", "selectRegionSpatial", "findNeighbors", "splitSkeletonLines");
        var source = context.Require<VisionRegion>("region"); using var region = JLVisionRegions.ToNative(source);
        using var outputs = new AdvancedJl.OwnedOutputs(); object details = new { };
        var contours = (IReadOnlyList<VisionContour>)Array.Empty<VisionContour>(); var count = 0;
        if (operation is "distanceTransform" or "closestPointTransform")
        {
            var metric = node.GetString("metric", "euclidean"); var foreground = node.GetString("foreground", "true");
            if (operation == "distanceTransform")
            {
                using var distance = region.DistanceTransform(metric, foreground, source.Width, source.Height); outputs["image"] = AdvancedJl.Image(distance);
            }
            else
            {
                using var distance = region.ClosestPointTransform(out var closest, metric, foreground, "absolute", source.Width, source.Height);
                using (closest) using (var rows = closest.VectorFieldToReal(out var columns)) using (columns)
                {
                    outputs["image"] = AdvancedJl.Image(distance); outputs["closestRows"] = AdvancedJl.Image(rows); outputs["closestColumns"] = AdvancedJl.Image(columns);
                }
            }
            count = source.Components.Count;
        }
        else if (operation == "splitSkeletonLines")
        {
            region.SplitSkeletonLines(AdvancedJl.Integer(node, "maxDistance", 2, 0), out var br, out var bc, out var er, out var ec);
            using (br) using (bc) using (er) using (ec)
            {
                var begin = AdvancedJl.Points(br, bc); var end = AdvancedJl.Points(er, ec);
                if (begin.Length != end.Length) throw new InvalidOperationException("骨架线段端点数量不一致。");
                contours = begin.Select((p, i) => JlContourCodec.Segment(i, p, end[i])).ToArray();
                count = contours.Count;
            }
        }
        else
        {
            var other = context.Require<VisionRegion>("region2");
            if (other.Width != source.Width || other.Height != source.Height) throw new InvalidOperationException("两个区域必须处于相同尺寸的坐标空间。");
            using var second = JLVisionRegions.ToNative(other);
            JlTuple? indices = null; JlTuple? indices2 = null; JlTuple? relation1 = null; JlTuple? relation2 = null;
            try
            {
                switch (operation)
                {
                    case "findNeighbors": indices = region.FindNeighbors(second, AdvancedJl.Integer(node, "maxDistance", 2, 0), out indices2); break;
                    case "selectRegionSpatial": indices = region.SelectRegionSpatial(second, node.GetString("direction", "left"), out indices2); break;
                    case "spatialRelation": indices = region.SpatialRelation(second, AdvancedJl.Integer(node, "percent", 50, 0, 100), out indices2, out relation1, out relation2); break;
                }
                count = indices!.Length;
                details = new { indexA = AdvancedJl.TupleNumbers(indices).Select(v => v - 1), indexB = AdvancedJl.TupleNumbers(indices2!).Select(v => v - 1),
                    relationA = relation1 is null ? [] : Enumerable.Range(0, relation1.Length).Select(i => relation1[i].ToString()).ToArray(),
                    relationB = relation2 is null ? [] : Enumerable.Range(0, relation2.Length).Select(i => relation2[i].ToString()).ToArray() };
            }
            finally { indices?.Dispose(); indices2?.Dispose(); relation1?.Dispose(); relation2?.Dispose(); }
        }
        outputs["contours"] = VisionValue.Contours(contours); outputs["relations"] = VisionValue.String(AdvancedJl.Json(details));
        outputs["count"] = VisionValue.Integer(count); outputs["found"] = VisionValue.Boolean(count > 0);
        return ValueTask.FromResult(AdvancedJl.Result(node, operation, outputs));
    }
}

public sealed class JLVisionFitRectangleNode : IVisionNodeExecutor
{
    public string Type => "measure.fitRectangle";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = context.Require<IReadOnlyList<VisionContour>>("contours");
        var outputs = new Dictionary<string, VisionValue>();
        var rectangles = new List<VisionRectangle2D>();
        if (input.Count > 0)
        {
            using var contours = JlContourCodec.ToNative(input);
            contours.FitRectangle2ContourXld(node.GetString("algorithm", "tukey"), AdvancedJl.Integer(node, "maxPoints", -1, -1, 1000000),
                AdvancedJl.Number(node, "maxClosureDistance", 0, 0), AdvancedJl.Integer(node, "clipEndPoints", 0), AdvancedJl.Integer(node, "iterations", 5, 1, 1000),
                AdvancedJl.Number(node, "clippingFactor", 2, .01, 100), out JlTuple rows, out var columns, out var angles, out var lengths1, out var lengths2, out var orders);
            using (rows) using (columns) using (angles) using (lengths1) using (lengths2) using (orders)
                for (var i = 0; i < rows.Length; i++) rectangles.Add(new(columns[i].D, rows[i].D, 2 * lengths1[i].D, 2 * lengths2[i].D, -angles[i].D * 180 / Math.PI));
        }
        var index = AdvancedJl.Integer(node, "index", 0);
        outputs["found"] = VisionValue.Boolean(index < rectangles.Count); outputs["count"] = VisionValue.Integer(rectangles.Count); outputs["rectangles"] = VisionValue.String(AdvancedJl.Json(rectangles));
        if (index < rectangles.Count)
        {
            var rectangle = rectangles[index]; outputs["rectangle"] = VisionValue.Rectangle(rectangle); outputs["center"] = VisionValue.Point(new(rectangle.X, rectangle.Y)); outputs["pose"] = VisionValue.Pose(new(rectangle.X, rectangle.Y, rectangle.AngleDeg));
        }
        return ValueTask.FromResult(AdvancedJl.Result(node, "fitRectangle2ContourXld", outputs));
    }
}

public sealed class JLVisionIntersectionsNode : IVisionNodeExecutor
{
    public string Type => "geometry.intersections";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = AdvancedJl.Choice(node, "operator", "lineCircle", "lineCircle", "circles", "segments");
        JlTuple? rows = null; JlTuple? columns = null; JlTuple? overlapping = null;
        try
        {
            using var start = new JlTuple(0d); using var extent = new JlTuple(2 * Math.PI); using var order = new JlTuple("positive");
            if (operation == "circles")
            {
                var a = context.Require<VisionCircle>("circle"); var b = context.Require<VisionCircle>("circle2");
                Validate(a); Validate(b);
                using var ar = new JlTuple(a.Y); using var ac = new JlTuple(a.X); using var radiusA = new JlTuple(a.Radius);
                using var br = new JlTuple(b.Y); using var bc = new JlTuple(b.X); using var radiusB = new JlTuple(b.Radius);
                JlOperatorSet.IntersectionCircles(ar, ac, radiusA, start, extent, order, br, bc, radiusB, start, extent, order, out rows, out columns, out overlapping);
            }
            else
            {
                var a = context.Require<VisionLine2D>("line"); Validate(a);
                using var r1 = new JlTuple(a.Start.Y); using var c1 = new JlTuple(a.Start.X); using var r2 = new JlTuple(a.End.Y); using var c2 = new JlTuple(a.End.X);
                if (operation == "lineCircle")
                {
                    var b = context.Require<VisionCircle>("circle"); Validate(b);
                    using var br = new JlTuple(b.Y); using var bc = new JlTuple(b.X); using var radius = new JlTuple(b.Radius);
                    JlOperatorSet.IntersectionLineCircle(r1, c1, r2, c2, br, bc, radius, start, extent, order, out rows, out columns);
                }
                else
                {
                    var b = context.Require<VisionLine2D>("line2"); Validate(b);
                    using var r3 = new JlTuple(b.Start.Y); using var c3 = new JlTuple(b.Start.X); using var r4 = new JlTuple(b.End.Y); using var c4 = new JlTuple(b.End.X);
                    JlOperatorSet.IntersectionSegments(r1, c1, r2, c2, r3, c3, r4, c4, out rows, out columns, out overlapping);
                }
            }
            var points = AdvancedJl.Points(rows!, columns!);
            var outputs = new Dictionary<string, VisionValue> { ["points"] = VisionValue.String(AdvancedJl.Json(points)), ["count"] = VisionValue.Integer(points.Length),
                ["found"] = VisionValue.Boolean(points.Length > 0), ["overlapping"] = VisionValue.Boolean(overlapping is { Length: > 0 } && overlapping[0].D != 0) };
            var selected = AdvancedJl.Integer(node, "index", 0);
            if (selected < points.Length) outputs["point"] = VisionValue.Point(points[selected]);
            return ValueTask.FromResult(AdvancedJl.Result(node, operation, outputs));
        }
        finally { rows?.Dispose(); columns?.Dispose(); overlapping?.Dispose(); }
    }
    private static void Validate(VisionCircle circle)
    {
        if (!double.IsFinite(circle.X) || !double.IsFinite(circle.Y) || !double.IsFinite(circle.Radius) || circle.Radius <= 0) throw new InvalidOperationException("圆心必须有限，半径必须为正。");
    }
    private static void Validate(VisionLine2D line)
    {
        if (!double.IsFinite(line.Start.X) || !double.IsFinite(line.Start.Y) || !double.IsFinite(line.End.X) || !double.IsFinite(line.End.Y) || line.Length < 1e-9) throw new InvalidOperationException("线段需要两个不同的有限坐标点。");
    }
}
