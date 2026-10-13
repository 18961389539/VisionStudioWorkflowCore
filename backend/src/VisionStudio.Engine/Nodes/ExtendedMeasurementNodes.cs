using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class PointLineDistanceNode : IVisionNodeExecutor
{
    public string Type => "measure.pointLineDistance";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var point = context.Require<VisionPoint2D>("point"); var line = context.Require<VisionLine2D>("line");
        if (new[] { point.X, point.Y, line.Start.X, line.Start.Y, line.End.X, line.End.Y }.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("Point and line coordinates must be finite.");
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y; var length2 = dx * dx + dy * dy;
        if (length2 < 1e-18) throw new InvalidOperationException("Distance is undefined for a zero-length line.");
        var t = ((point.X - line.Start.X) * dx + (point.Y - line.Start.Y) * dy) / length2;
        if (node.GetBool("segment", false)) t = Math.Clamp(t, 0, 1);
        var closest = new VisionPoint2D(line.Start.X + t * dx, line.Start.Y + t * dy);
        var distance = MeasurementMath.Hypot(point.X - closest.X, point.Y - closest.Y);
        var connector = new VisionLine2D(point, closest);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["distance"] = VisionValue.Double(distance), ["closestPoint"] = VisionValue.Point(closest) }, new Dictionary<string, object?> { ["distance"] = distance, ["segment"] = node.GetBool("segment", false) }, [new VisionOverlay("point-line-distance", VisionOverlayType.Line, X: point.X, Y: point.Y, X2: closest.X, Y2: closest.Y, Stroke: "#722ed1", StrokeWidth: 2)]));
    }
}

public sealed class CircleSpacingNode : IVisionNodeExecutor
{
    public string Type => "measure.circleSpacing";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = context.Require<VisionCircle>("circleA"); var b = context.Require<VisionCircle>("circleB");
        if (new[] { a.X, a.Y, b.X, b.Y }.Any(v => !double.IsFinite(v))) throw new InvalidOperationException("Circle centers must be finite.");
        if (!double.IsFinite(a.Radius) || !double.IsFinite(b.Radius) || a.Radius < 0 || b.Radius < 0) throw new InvalidOperationException("Circle radii must be finite and non-negative.");
        var centers = MeasurementMath.Hypot(a.X - b.X, a.Y - b.Y); var gap = centers - a.Radius - b.Radius;
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["centerDistance"] = VisionValue.Double(centers), ["gap"] = VisionValue.Double(gap), ["measurement"] = VisionValue.Measurement(new VisionMeasurement(gap, "px", "signedCircleGap")) }, new Dictionary<string, object?> { ["centerDistance"] = centers, ["gap"] = gap, ["overlapping"] = gap < 0 }, [new VisionOverlay("circle-centers", VisionOverlayType.Line, X: a.X, Y: a.Y, X2: b.X, Y2: b.Y, Stroke: "#722ed1", StrokeWidth: 1.5)]));
    }
}

internal static class MultiPointFit
{
    public static VisionPoint2D[] Read(NodeExecutionContext context)
    {
        var points = context.Inputs.Where(x => x.Key.StartsWith("point", StringComparison.OrdinalIgnoreCase) && int.TryParse(x.Key.AsSpan(5), out _))
            .Select(x => (Index: int.Parse(x.Key.AsSpan(5)), Point: x.Value.Require<VisionPoint2D>(x.Key)))
            .OrderBy(x => x.Index).Select(x => x.Point).ToArray();
        if (points.Length < 2) throw new InvalidOperationException("Connect at least two point inputs (point1, point2, ...).");
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) throw new InvalidOperationException("Fit points must have finite coordinates.");
        return points;
    }

    // Orthogonal least-squares fit: principal eigenvector of the centered 2x2 covariance matrix.
    public static VisionLine2D Line(VisionPoint2D[] p)
    {
        if (p.Length < 2) throw new InvalidOperationException("A line fit requires at least two points.");
        var mx = p.Average(x => x.X); var my = p.Average(x => x.Y);
        var xx = p.Sum(x => (x.X - mx) * (x.X - mx)); var yy = p.Sum(x => (x.Y - my) * (x.Y - my)); var xy = p.Sum(x => (x.X - mx) * (x.Y - my));
        if (xx + yy < 1e-18) throw new InvalidOperationException("A line cannot be fit when all points coincide.");
        var angle = 0.5 * Math.Atan2(2 * xy, xx - yy); var ux = Math.Cos(angle); var uy = Math.Sin(angle);
        var projections = p.Select(x => (x.X - mx) * ux + (x.Y - my) * uy).ToArray();
        var min = projections.Min(); var max = projections.Max();
        return new VisionLine2D(new(mx + min * ux, my + min * uy), new(mx + max * ux, my + max * uy));
    }

    // Algebraic least-squares circle fit: solve [x y 1] [A B C]' = -(x²+y²), center=(-A/2,-B/2).
    public static VisionCircle Circle(VisionPoint2D[] p)
    {
        if (p.Length < 3) throw new InvalidOperationException("A circle fit requires at least three points.");
        var mx = p.Average(x => x.X); var my = p.Average(x => x.Y);
        var xx = p.Sum(v => (v.X-mx)*(v.X-mx)); var yy = p.Sum(v => (v.Y-my)*(v.Y-my)); var xy = p.Sum(v => (v.X-mx)*(v.Y-my));
        var bx = p.Sum(v => (v.X-mx)*((v.X-mx)*(v.X-mx)+(v.Y-my)*(v.Y-my))); var by = p.Sum(v => (v.Y-my)*((v.X-mx)*(v.X-mx)+(v.Y-my)*(v.Y-my)));
        var det = xx*yy-xy*xy;
        if (Math.Abs(det) < 1e-12 * Math.Max(1, xx*yy)) throw new InvalidOperationException("Circle fit is singular; points may be collinear or too close together.");
        var cx = mx + (bx*yy-by*xy)/(2*det); var cy = my + (by*xx-bx*xy)/(2*det);
        var radius = p.Average(v => MeasurementMath.Hypot(v.X-cx,v.Y-cy));
        if (!double.IsFinite(radius) || radius <= 0) throw new InvalidOperationException("Circle fit produced an invalid radius.");
        return new VisionCircle(cx, cy, radius);
    }
}

internal static class MeasurementMath
{
    public static double Hypot(double x, double y)
    {
        x = Math.Abs(x); y = Math.Abs(y);
        var max = Math.Max(x, y);
        if (double.IsInfinity(max)) return double.PositiveInfinity;
        if (max == 0) return 0;
        var min = Math.Min(x, y) / max;
        return max * Math.Sqrt(1 + min * min);
    }
}

public sealed class FitLineNode : IVisionNodeExecutor
{
    public string Type => "measure.fitLine";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var points = MultiPointFit.Read(context); var line = MultiPointFit.Line(points);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["line"] = VisionValue.Line(line), ["length"] = VisionValue.Double(line.Length), ["angle"] = VisionValue.Double(line.AngleDeg) }, new Dictionary<string, object?> { ["pointCount"] = points.Length, ["length"] = line.Length, ["angleDeg"] = line.AngleDeg }));
    }
}

public sealed class FitCircleNode : IVisionNodeExecutor
{
    public string Type => "measure.fitCircle";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var points = MultiPointFit.Read(context); var circle = MultiPointFit.Circle(points);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["circle"] = VisionValue.Circle(circle), ["center"] = VisionValue.Point(new(circle.X,circle.Y)), ["radius"] = VisionValue.Double(circle.Radius) }, new Dictionary<string, object?> { ["pointCount"] = points.Length, ["x"] = circle.X, ["y"] = circle.Y, ["radius"] = circle.Radius }));
    }
}
