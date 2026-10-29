using System.Text.Json;
using JLVisionLib;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

internal static class ToleranceGeometry
{
    public const int MaxPoints = 20000;
    public static void Finite(params double[] values)
    {
        if (values.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100_000_000))
            throw new InvalidOperationException("Geometry coordinates and lengths must be finite within +/-100000000 px.");
    }
    public static VisionCircle Circle(NodeExecutionContext context, string port)
    {
        var circle = context.Require<VisionCircle>(port); Finite(circle.X, circle.Y, circle.Radius);
        if (circle.Radius <= 0) throw new InvalidOperationException("Circle radius must be positive.");
        return circle;
    }
    public static (VisionLine2D Line, double X, double Y, double Length) Line(NodeExecutionContext context, string port)
        => Line(context.Require<VisionLine2D>(port));
    public static (VisionLine2D Line, double X, double Y, double Length) Line(VisionLine2D line)
    {
        Finite(line.Start.X, line.Start.Y, line.End.X, line.End.Y);
        var length = MeasurementMath.Hypot(line.End.X - line.Start.X, line.End.Y - line.Start.Y);
        if (length < 1e-9) throw new InvalidOperationException("A nonzero line segment is required.");
        return (line, (line.End.X - line.Start.X) / length, (line.End.Y - line.Start.Y) / length, length);
    }
    public static VisionPoint2D[] Points(IReadOnlyList<VisionPoint2D> points, bool closed = true)
    {
        if (points.Count > MaxPoints) throw new InvalidOperationException("Contour point budget exceeded (20000).");
        var result = new List<VisionPoint2D>();
        foreach (var point in points)
        {
            Finite(point.X, point.Y);
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        }
        if (closed && result.Count > 1 && result[^1] == result[0]) result.RemoveAt(result.Count - 1);
        return result.ToArray();
    }
    public static VisionPoint2D[] Select(NodeExecutionContext context, NodeDefinition node, string port = "contours", string parameter = "index", bool closed = true)
    {
        var list = context.Require<IReadOnlyList<VisionContour>>(port); var index = node.GetInt(parameter, 0);
        if (index < 0 || index > 1999 || list.Count > 2000) throw new InvalidOperationException("Contour index must be 0..1999 and list budget is 2000 contours.");
        if (index >= list.Count) return [];
        var item = list[index];
        if (item?.Points is null) throw new InvalidOperationException("Contour and its points must be non-null.");
        return Points(item.Points, closed);
    }
    public static JlXLDCont Curve(IReadOnlyList<VisionPoint2D> points, bool closed)
    {
        var data = closed && points.Count > 1 && points[^1] != points[0] ? points.Append(points[0]).ToArray() : points.ToArray();
        using var rows = new JlTuple(data.Select(p => p.Y).ToArray()); using var cols = new JlTuple(data.Select(p => p.X).ToArray());
        var curve = new JlXLDCont();
        try { curve.GenContourPolygonXld(rows, cols); return curve; }
        catch { curve.Dispose(); throw; }
    }
    public static NodeExecutorResult Missing(int count, string reason, Dictionary<string, VisionValue>? extra = null, string? nodeType = null)
    {
        var outputs = extra ?? new(); outputs["found"] = VisionValue.Boolean(false); outputs["sampleCount"] = VisionValue.Integer(count);
        var diag = CoordinateValidation.Diagnostics(nodeType ?? "measure.tolerance", ("found", false), ("sampleCount", count), ("reason", reason));
        outputs["diagnostics"] = VisionValue.String(diag);
        return new(outputs, new Dictionary<string, object?> { ["found"] = false, ["sampleCount"] = count, ["reason"] = reason, ["unit"] = "px", ["diagnostics"] = diag });
    }
    public static VisionOverlay Segment(string id, VisionLine2D line, string? label = null)
        => new(id, VisionOverlayType.Line, X: line.Start.X, Y: line.Start.Y, X2: line.End.X, Y2: line.End.Y, Label: label);
}

public sealed class ContourFromPointsNode : IVisionNodeExecutor
{
    public string Type => "contour.fromPoints";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var closed = node.GetBool("closed", true);
        VisionPoint2D[] raw;
        if (context.TryGetValue("pointList", out var wired))
        {
            // 断链修复：轮廓/检测点集可以直连，不再强制走 JSON 文本
            var list = wired.Require<IReadOnlyList<VisionPoint2D>>("pointList");
            if (list.Count > ToleranceGeometry.MaxPoints) throw new InvalidOperationException("Contour requires at most 20000 points.");
            raw = list.ToArray();
        }
        else
        {
            var json = context.TryGetValue("pointsJson", out var input) ? input.Require<string>("pointsJson") : node.GetString("pointsJson", "[[100,100],[200,100],[200,200],[100,200]]");
            if (json.Length > 2_097_152) throw new InvalidOperationException("Contour JSON exceeds 2 MiB of text.");
            List<double[]> data;
            try { data = JsonSerializer.Deserialize<List<double[]>>(json) ?? throw new JsonException("null points"); }
            catch (JsonException ex) { throw new InvalidOperationException("Contour points JSON must be an array of [x,y] pairs.", ex); }
            if (data.Count > ToleranceGeometry.MaxPoints || data.Any(p => p is null || p.Length != 2)) throw new InvalidOperationException("Contour requires at most 20000 [x,y] pairs.");
            raw = data.Select(p => new VisionPoint2D(p[0], p[1])).ToArray();
        }
        var points = ToleranceGeometry.Points(raw, closed);
        if (points.Distinct().Count() < 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", new() { ["contours"] = VisionValue.Contours([]), ["count"] = VisionValue.Integer(0) }, nodeType: Type));
        using var curve = ToleranceGeometry.Curve(points, closed);
        using var length = curve.LengthXld();
        var area = closed && points.Length >= 3 ? Math.Abs(curve.AreaCenterXld(out double _, out double _, out string _)) : 0;
        curve.SmallestRectangle1Xld(out double top, out double left, out double bottom, out double right);
        curve.SmallestCircleXld(out double row, out double col, out double radius);
        var perimeter = length.D;
        var contour = new VisionContour(0, points, -1, false, area, perimeter, perimeter > 0 ? Math.Min(1, 4 * Math.PI * area / (perimeter * perimeter)) : 0,
            new(left, top, right - left, bottom - top), new(col, row, radius)) { Closed = closed };
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", points.Length), ("area", area),
            ("perimeter", perimeter), ("closed", closed), ("boundingWidth", right - left), ("boundingHeight", bottom - top));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([contour]), ["found"] = VisionValue.Boolean(true),
            ["count"] = VisionValue.Integer(1), ["sampleCount"] = VisionValue.Integer(points.Length),
            ["diagnostics"] = VisionValue.String(diag)
        },
            new Dictionary<string, object?> { ["found"] = true, ["sampleCount"] = points.Length, ["area"] = area, ["perimeter"] = perimeter, ["closed"] = closed, ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag },
            [new("point-contour", VisionOverlayType.Contour, Points: (closed ? points.Append(points[0]) : points).Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray())]));
    }
}

public sealed class WallThicknessNode : IVisionNodeExecutor
{
    public string Type => "measure.wallThickness";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var inner = ToleranceGeometry.Circle(context, "inner"); var outer = ToleranceGeometry.Circle(context, "outer");
        if (outer.Radius <= inner.Radius)
            throw new InvalidOperationException("measure.wallThickness requires outer.radius > inner.radius.");
        var count = node.GetInt("sampleCount", 360); var start = node.GetDouble("startAngleDeg", 0);
        if (count is < 8 or > 4096 || !double.IsFinite(start) || start is < -3600 or > 3600) throw new InvalidOperationException("Wall samples must be 8..4096 and start angle -3600..3600 degrees.");
        var dx = outer.X - inner.X; var dy = outer.Y - inner.Y; var distance = MeasurementMath.Hypot(dx, dy);
        if (distance + inner.Radius > outer.Radius)
        {
            var missDiag = CoordinateValidation.Diagnostics(Type, ("found", false), ("reason", "inner-disk-not-contained"),
                ("contained", false), ("centerDistance", distance), ("innerRadius", inner.Radius), ("outerRadius", outer.Radius));
            return ValueTask.FromResult(ToleranceGeometry.Missing(0, "inner-disk-not-contained", new()
            {
                ["contained"] = VisionValue.Boolean(false), ["centerDistance"] = VisionValue.Double(distance),
                ["samplesJson"] = VisionValue.String("[]"), ["diagnostics"] = VisionValue.String(missDiag)
            }, nodeType: Type));
        }
        var values = new double[count]; var samples = new List<object>(); var overlays = new List<VisionOverlay>();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var angle = start + 360d * i / count; var radians = angle * Math.PI / 180; var ux = Math.Cos(radians); var uy = Math.Sin(radians);
            var projection = dx * ux + dy * uy;
            var t = projection + Math.Sqrt(Math.Max(0, outer.Radius * outer.Radius - distance * distance + projection * projection));
            values[i] = Math.Max(0, t - inner.Radius);
            var a = new VisionPoint2D(inner.X + inner.Radius * ux, inner.Y + inner.Radius * uy);
            var b = new VisionPoint2D(inner.X + t * ux, inner.Y + t * uy);
            samples.Add(new { AngleDeg = angle, Inner = a, Outer = b, Thickness = values[i] });
            if (i % (int)Math.Ceiling(count / 200d) == 0) overlays.Add(ToleranceGeometry.Segment($"wall-{i}", new(a, b)));
        }
        var min = Math.Max(0, outer.Radius - inner.Radius - distance); var max = outer.Radius - inner.Radius + distance;
        var mean = values.Average(); var deviation = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        var nominal = node.GetDouble("nominalThickness", 0);
        var tolerance = node.GetDouble("tolerance", 0);
        if (!double.IsFinite(nominal) || nominal < 0) throw new InvalidOperationException("nominalThickness must be finite and >= 0 (0 disables the gate).");
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new InvalidOperationException("tolerance must be finite and >= 0.");
        if (nominal > 0 && tolerance <= 0) throw new InvalidOperationException("tolerance must be > 0 when nominalThickness is set.");
        var within = nominal <= 0 || Math.Abs(mean - nominal) <= tolerance;
        var variation = max - min;
        var cv = Math.Abs(mean) > 1e-12 ? deviation / Math.Abs(mean) : 0;
        var uniformity = mean > 1e-12 ? Math.Max(0, 1 - variation / mean) : 0;
        var diag = CoordinateValidation.Diagnostics(Type,
            ("found", true), ("contained", true), ("sampleCount", count), ("centerDistance", distance),
            ("minThickness", min), ("maxThickness", max), ("meanThickness", mean), ("stdDev", deviation),
            ("variation", variation), ("cv", cv), ("uniformity", uniformity),
            ("nominalThickness", nominal), ("tolerance", tolerance), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["contained"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(count), ["centerDistance"] = VisionValue.Double(distance),
            ["minThickness"] = VisionValue.Double(min), ["maxThickness"] = VisionValue.Double(max), ["meanThickness"] = VisionValue.Double(mean), ["variation"] = VisionValue.Double(variation),
            ["stdDev"] = VisionValue.Double(deviation), ["cv"] = VisionValue.Double(cv), ["uniformity"] = VisionValue.Double(uniformity),
            ["withinTolerance"] = VisionValue.Boolean(within),
            ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)), ["diagnostics"] = VisionValue.String(diag)
        }, new Dictionary<string, object?> { ["found"] = true, ["contained"] = true, ["minThickness"] = min, ["maxThickness"] = max, ["meanThickness"] = mean, ["sampleCount"] = count, ["unit"] = "px", ["withinTolerance"] = within, ["diagnostics"] = diag }, overlays));
    }
}

public sealed class ConcentricityNode : IVisionNodeExecutor
{
    public string Type => "measure.concentricity";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var a = ToleranceGeometry.Circle(context, "circleA"); var b = ToleranceGeometry.Circle(context, "circleB");
        var offset = new VisionPoint2D(b.X - a.X, b.Y - a.Y); var distance = MeasurementMath.Hypot(offset.X, offset.Y);
        if (!double.IsFinite(distance) || !double.IsFinite(offset.X) || !double.IsFinite(offset.Y)) throw new InvalidOperationException("measure.concentricity produced non-finite output.");
        var limit = node.GetDouble("maxCenterDistance", 0); if (!double.IsFinite(limit) || limit < 0) throw new InvalidOperationException("measure.concentricity maxCenterDistance must be finite and >= 0 (0 disables the tolerance).");
        var within = limit <= 0 || distance <= limit; var angle = Math.Atan2(offset.Y, offset.X) * 180 / Math.PI;
        var diag = CoordinateValidation.Diagnostics(Type, ("centerDistance", distance), ("diametralOffset", 2 * distance), ("offsetAngleDeg", angle), ("maxCenterDistance", limit), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["centerDistance"] = VisionValue.Double(distance), ["diametralOffset"] = VisionValue.Double(2 * distance), ["centerOffset"] = VisionValue.Point(offset), ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["centerDistance"] = distance, ["diametralOffset"] = 2 * distance, ["unit"] = "px", ["withinTolerance"] = within, ["diagnostics"] = diag },
            [ToleranceGeometry.Segment("circle-centre-offset", new(new(a.X, a.Y), new(b.X, b.Y)))]));
    }
}

public sealed class RoundnessNode : IVisionNodeExecutor
{
    public string Type => "measure.roundness";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var coverageLimit = node.GetDouble("minCoverageDeg", 270);
        if (!double.IsFinite(coverageLimit) || coverageLimit is < 0 or > 360) throw new InvalidOperationException("Minimum angular coverage must be 0..360 degrees.");
        var maxDeviation = node.GetDouble("maxDeviation", 0);
        if (!double.IsFinite(maxDeviation) || maxDeviation < 0)
            throw new InvalidOperationException("measure.roundness maxDeviation must be finite and >= 0 (0 disables the tolerance).");
        var points = ToleranceGeometry.Select(context, node);
        if (points.Distinct().Count() < 6) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));
        var mx = points.Average(p => p.X); var my = points.Average(p => p.Y);
        var xx = points.Sum(p => (p.X - mx) * (p.X - mx)); var yy = points.Sum(p => (p.Y - my) * (p.Y - my)); var xy = points.Sum(p => (p.X - mx) * (p.Y - my));
        if (xx * yy - xy * xy <= (xx + yy) * (xx + yy) * 1e-12) throw new InvalidOperationException("Circle samples are collinear or ill-conditioned.");
        var circle = JLVisionPointFitting.Circle(points); ToleranceGeometry.Finite(circle.X, circle.Y, circle.Radius);
        var angles = points.Select(p => Math.Atan2(p.Y - circle.Y, p.X - circle.X) * 180 / Math.PI).Order().ToArray();
        var gap = angles[0] + 360 - angles[^1]; for (var i = 1; i < angles.Length; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
        var coverage = 360 - gap;
        if (coverage + 1e-8 < coverageLimit)
            return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-angular-coverage", new() { ["coverageDeg"] = VisionValue.Double(coverage) }, nodeType: Type));
        var radii = points.Select(p => MeasurementMath.Hypot(p.X - circle.X, p.Y - circle.Y)).ToArray();
        var minimum = radii.Min(); var maximum = radii.Max(); var rms = Math.Sqrt(radii.Average(v => (v - circle.Radius) * (v - circle.Radius)));
        cancellationToken.ThrowIfCancellationRequested();
        var deviation = maximum - minimum;
        var within = maxDeviation <= 0 || deviation <= maxDeviation;
        var outOfRoundPct = circle.Radius > 1e-12 ? 100 * deviation / circle.Radius : 0;
        var relativeRms = circle.Radius > 1e-12 ? rms / circle.Radius : 0;
        if (circle.Radius < 1e-9)
            throw new InvalidOperationException("measure.roundness fitted circle radius is degenerate.");
        var peakOut = maximum - circle.Radius;
        var peakIn = circle.Radius - minimum;
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", points.Length), ("coverageDeg", coverage),
            ("radius", circle.Radius), ("minRadius", minimum), ("maxRadius", maximum), ("deviation", deviation), ("rms", rms),
            ("outOfRoundPct", outOfRoundPct), ("relativeRms", relativeRms),
            ("peakOutward", peakOut), ("peakInward", peakIn),
            ("maxDeviation", maxDeviation), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(points.Length), ["coverageDeg"] = VisionValue.Double(coverage),
            ["circle"] = VisionValue.Circle(circle), ["center"] = VisionValue.Point(new(circle.X, circle.Y)), ["radius"] = VisionValue.Double(circle.Radius), ["minRadius"] = VisionValue.Double(minimum), ["maxRadius"] = VisionValue.Double(maximum), ["deviation"] = VisionValue.Double(deviation), ["rms"] = VisionValue.Double(rms), ["outOfRoundPct"] = VisionValue.Double(outOfRoundPct), ["relativeRms"] = VisionValue.Double(relativeRms), ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["found"] = true, ["deviation"] = deviation, ["rms"] = rms, ["outOfRoundPct"] = outOfRoundPct, ["relativeRms"] = relativeRms,
                ["peakOutward"] = peakOut, ["peakInward"] = peakIn,
                ["radius"] = circle.Radius, ["sampleCount"] = points.Length, ["coverageDeg"] = coverage,
                ["withinTolerance"] = within, ["maxDeviation"] = maxDeviation, ["unit"] = "px",
                ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag },
            [new("roundness-fit", VisionOverlayType.Circle, X: circle.X, Y: circle.Y, Radius: circle.Radius, Label: $"radial range={deviation:0.###}")]));
    }
}

public sealed class StraightnessNode : IVisionNodeExecutor
{
    public string Type => "measure.straightness";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var maxDeviation = node.GetDouble("maxDeviation", 0);
        if (!double.IsFinite(maxDeviation) || maxDeviation < 0)
            throw new InvalidOperationException("measure.straightness maxDeviation must be finite and >= 0 (0 disables the tolerance).");
        var points = ToleranceGeometry.Select(context, node);
        if (points.Distinct().Count() < 3) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));
        var line = JLVisionPointFitting.Line(points); var fit = ToleranceGeometry.Line(line);
        var errors = points.Select(p => (p.X - line.Start.X) * -fit.Y + (p.Y - line.Start.Y) * fit.X).ToArray();
        var deviation = errors.Max() - errors.Min(); var rms = Math.Sqrt(errors.Average(v => v * v)); var maxAbs = errors.Max(Math.Abs);
        var length = MeasurementMath.Hypot(line.End.X - line.Start.X, line.End.Y - line.Start.Y);
        if (length < 1e-12) throw new InvalidOperationException("measure.straightness fitted line is degenerate.");
        var angleDeg = Math.Atan2(line.End.Y - line.Start.Y, line.End.X - line.Start.X) * 180 / Math.PI;
        var within = maxDeviation <= 0 || deviation <= maxDeviation;
        var relativeDeviation = length > 1e-12 ? deviation / length : 0;
        var peakPositive = errors.Max();
        var peakNegative = errors.Min();
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("sampleCount", points.Length), ("deviation", deviation), ("rms", rms), ("maxAbsError", maxAbs),
            ("relativeDeviation", relativeDeviation), ("peakPositive", peakPositive), ("peakNegative", peakNegative),
            ("length", length), ("angleDeg", angleDeg), ("maxDeviation", maxDeviation), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(points.Length), ["line"] = VisionValue.Line(line),
            ["deviation"] = VisionValue.Double(deviation), ["rms"] = VisionValue.Double(rms), ["maxAbsError"] = VisionValue.Double(maxAbs),
            ["relativeDeviation"] = VisionValue.Double(relativeDeviation),
            ["length"] = VisionValue.Double(length), ["angleDeg"] = VisionValue.Double(angleDeg),
            ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diag)
        },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["deviation"] = deviation, ["rms"] = rms, ["maxAbsError"] = maxAbs,
                ["relativeDeviation"] = relativeDeviation, ["peakPositive"] = peakPositive, ["peakNegative"] = peakNegative,
                ["length"] = length, ["angleDeg"] = angleDeg, ["sampleCount"] = points.Length,
                ["withinTolerance"] = within, ["maxDeviation"] = maxDeviation, ["unit"] = "px",
                ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag
            }, [ToleranceGeometry.Segment("straightness-fit", line)]));
    }
}

internal static class LineOrientationQuality
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string type, bool perpendicular, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var a = ToleranceGeometry.Line(context, "a"); var b = ToleranceGeometry.Line(context, "b");
        var dot = Math.Clamp(Math.Abs(a.X * b.X + a.Y * b.Y), 0, 1); var cross = Math.Clamp(Math.Abs(a.X * b.Y - a.Y * b.X), 0, 1);
        var angle = Math.Atan2(cross, dot) * 180 / Math.PI; var error = perpendicular ? 90 - angle : angle; var deviation = b.Length * (perpendicular ? dot : cross);
        if (!double.IsFinite(angle) || !double.IsFinite(error) || !double.IsFinite(deviation)) throw new InvalidOperationException($"{type} produced non-finite output.");
        var limit = node.GetDouble("maxAngleErrorDeg", 0); if (!double.IsFinite(limit) || limit < 0 || limit > 90) throw new InvalidOperationException($"{type} maxAngleErrorDeg must be in [0, 90] (0 disables the tolerance).");
        var within = limit <= 0 || Math.Abs(error) <= limit;
        var diag = CoordinateValidation.Diagnostics(type, ("measuredAngleDeg", angle), ("angleErrorDeg", error), ("deviation", deviation), ("measuredLength", b.Length), ("maxAngleErrorDeg", limit), ("withinTolerance", within), ("perpendicular", perpendicular));
        return new(new Dictionary<string, VisionValue> { ["angleError"] = VisionValue.Double(error), ["deviation"] = VisionValue.Double(deviation), ["measuredLength"] = VisionValue.Double(b.Length), ["measuredAngleDeg"] = VisionValue.Double(angle), ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["angleError"] = error, ["deviation"] = deviation, ["measuredLength"] = b.Length, ["unit"] = "px", ["angleUnit"] = "deg", ["withinTolerance"] = within, ["diagnostics"] = diag }, [ToleranceGeometry.Segment("datum-a", a.Line, "A"), ToleranceGeometry.Segment("measured-b", b.Line, "B")]);
    }
}
public sealed class ParallelismNode : IVisionNodeExecutor
{
    public string Type => "measure.parallelism";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        // parallel / perpendicular 共用同一套角度误差计算：差别只在参考角是 0° 还是 90°。
        var mode = (node.GetString("mode", "parallel") ?? "parallel").Trim().ToLowerInvariant();
        if (mode is not ("parallel" or "perpendicular"))
            throw new InvalidOperationException("measure.parallelism mode must be parallel or perpendicular.");
        var limit = node.GetDouble("maxAngleErrorDeg", 0);
        if (!double.IsFinite(limit) || limit < 0 || limit > 90)
            throw new InvalidOperationException("measure.parallelism maxAngleErrorDeg must be finite in [0, 90] (0 disables).");
        _ = context.Require<VisionLine2D>("a");
        _ = context.Require<VisionLine2D>("b");
        var result = LineOrientationQuality.Execute(context, node, Type, mode == "perpendicular", cancellationToken);
        if (!result.Outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("measure.parallelism diagnostics missing.");
        return ValueTask.FromResult(result);
    }
}

public sealed class ContourDeviationNode : IVisionNodeExecutor
{
    public string Type => "measure.contourDeviation";
    private sealed record DistanceSample(string Direction, VisionPoint2D Point, double Distance);
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var closed = node.GetBool("closed", true); var symmetric = node.GetBool("symmetric", true); var step = node.GetDouble("sampleStep", 2); var budget = node.GetInt("maxSamples", 2000);
        var maxDeviation = node.GetDouble("maxDeviation", 0);
        if (!double.IsFinite(step) || step is < .01 or > 10000 || budget is < 2 or > 10000) throw new InvalidOperationException("Deviation step must be 0.01..10000 px and sample budget 2..10000 per direction.");
        if (!double.IsFinite(maxDeviation) || maxDeviation < 0) throw new InvalidOperationException("measure.contourDeviation maxDeviation must be finite and >= 0 (0 disables the tolerance).");
        var points = ToleranceGeometry.Select(context, node, closed: closed); var reference = ToleranceGeometry.Select(context, node, "referenceContours", "referenceIndex", closed);
        if (points.Distinct().Count() < 2 || reference.Distinct().Count() < 2)
            return ValueTask.FromResult(ToleranceGeometry.Missing(0, "insufficient-points",
                new() { ["samplesJson"] = VisionValue.String("[]"), ["withinTolerance"] = VisionValue.Boolean(false) }, nodeType: Type));
        var samples = new List<DistanceSample>(); var forward = Distances(points, reference, "current-to-reference");
        var reverse = symmetric ? Distances(reference, points, "reference-to-current") : [];
        var all = forward.Concat(reverse).ToArray(); var maximum = all.Max(); var mean = all.Average(); var rms = Math.Sqrt(all.Average(v => v * v));
        var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(true), ["sampleCount"] = VisionValue.Integer(all.Length), ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)),
            ["deviation"] = VisionValue.Double(maximum), ["mean"] = VisionValue.Double(mean), ["rms"] = VisionValue.Double(rms), ["forwardMax"] = VisionValue.Double(forward.Max()) };
        if (symmetric) outputs["reverseMax"] = VisionValue.Double(reverse.Max());
        var within = maxDeviation <= 0 || maximum <= maxDeviation;
        outputs["withinTolerance"] = VisionValue.Boolean(within);
        var diag = CoordinateValidation.Diagnostics(Type, ("found", true), ("deviation", maximum), ("mean", mean), ("maxDeviation", maxDeviation), ("withinTolerance", within), ("rms", rms),
            ("sampleCount", all.Length), ("symmetric", symmetric), ("sampleStep", step));
        outputs["diagnostics"] = VisionValue.String(diag);
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?> { ["found"] = true, ["deviation"] = maximum, ["mean"] = mean, ["rms"] = rms, ["sampleCount"] = all.Length, ["symmetric"] = symmetric, ["sampleStep"] = step, ["maxDeviation"] = maxDeviation, ["withinTolerance"] = within, ["unit"] = "px", ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag },
            samples.OrderByDescending(s => s.Distance).Take(200).Select((s, i) => new VisionOverlay($"deviation-{i}", VisionOverlayType.Point, X: s.Point.X, Y: s.Point.Y, Label: $"{s.Distance:0.###}")).ToArray()));

        double[] Distances(VisionPoint2D[] source, VisionPoint2D[] target, string direction)
        {
            var query = Resample(source, closed, step, budget, cancellationToken);
            using var queryCurve = ToleranceGeometry.Curve(query, false); using var targetCurve = ToleranceGeometry.Curve(target, closed);
            using var distances = queryCurve.DistanceContoursXld(targetCurve, "point_to_segment"); using var attribute = distances.GetContourAttribXld("distance");
            if (attribute.Length != query.Length) throw new InvalidOperationException("JLVision contour distance sample count mismatch.");
            var values = new double[query.Length];
            for (var i = 0; i < values.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested(); values[i] = attribute[i].D;
                if (!double.IsFinite(values[i]) || values[i] < 0) throw new InvalidOperationException("JLVision produced invalid contour distances.");
                samples.Add(new(direction, query[i], values[i]));
            }
            return values;
        }
    }
    private static VisionPoint2D[] Resample(VisionPoint2D[] points, bool closed, double step, int budget, CancellationToken token)
    {
        var path = closed ? points.Append(points[0]).ToArray() : points;
        var cumulative = new double[path.Length];
        for (var i = 1; i < path.Length; i++) cumulative[i] = cumulative[i - 1] + MeasurementMath.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
        var length = cumulative[^1]; var intervals = Math.Max(1, Math.Ceiling(length / step)); var count = Math.Max(2, intervals + (closed ? 0 : 1));
        if (count > budget) throw new InvalidOperationException($"Contour deviation sample budget exceeded ({budget}); increase sampleStep or budget.");
        var result = new VisionPoint2D[(int)count]; var segment = 1;
        for (var i = 0; i < result.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var distance = length * i / (closed ? result.Length : result.Length - 1);
            while (segment < path.Length - 1 && cumulative[segment] < distance) segment++;
            var fraction = (distance - cumulative[segment - 1]) / (cumulative[segment] - cumulative[segment - 1]);
            result[i] = new(path[segment - 1].X + fraction * (path[segment].X - path[segment - 1].X), path[segment - 1].Y + fraction * (path[segment].Y - path[segment - 1].Y));
        }
        return result;
    }
}
