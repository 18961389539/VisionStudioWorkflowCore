using VisionStudio.Engine.Algorithms;
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

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var point = context.Require<VisionPoint2D>("point"); var line = context.Require<VisionLine2D>("line");
        if (new[] { point.X, point.Y, line.Start.X, line.Start.Y, line.End.X, line.End.Y }.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("Point and line coordinates must be finite.");
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y;
        var length = MeasurementMath.Hypot(dx, dy);
        if (!double.IsFinite(length) || length < 1e-9) throw new InvalidOperationException("距离计算要求有限、非零长度的直线。");
        var ux = dx / length; var uy = dy / length;
        var rawT = ((point.X - line.Start.X) * ux + (point.Y - line.Start.Y) * uy) / length;
        var segment = node.GetBool("segment", false);
        var t = segment ? Math.Clamp(rawT, 0, 1) : rawT;
        var closest = new VisionPoint2D(line.Start.X + t * dx, line.Start.Y + t * dy);
        var projected = new VisionPoint2D(line.Start.X + rawT * dx, line.Start.Y + rawT * dy);
        var distance = MeasurementMath.Hypot(point.X - closest.X, point.Y - closest.Y);
        var within = rawT >= -1e-9 && rawT <= 1 + 1e-9;
        var signed = (point.X - line.Start.X) * uy - (point.Y - line.Start.Y) * ux;
        if (new[] { distance, closest.X, closest.Y, projected.X, projected.Y, rawT, signed }.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("点线距离计算超出有限数值范围，请检查坐标单位和幅值。");
        var clamped = segment && t != rawT;
        var diag = CoordinateValidation.Diagnostics(Type, ("distance", distance), ("distanceTo", segment ? "segment" : "line"), ("signedDistance", signed),
            ("signedDistanceTo", "infiniteLine"), ("rawProjectionT", rawT), ("projectionT", t), ("projectionClamped", clamped), ("withinSegment", within));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> {
            ["distance"] = VisionValue.Double(distance), ["closestPoint"] = VisionValue.Point(closest), ["projectedPoint"] = VisionValue.Point(projected),
            ["signedDistance"] = VisionValue.Double(signed), ["lineDistance"] = VisionValue.Double(Math.Abs(signed)), ["projectionT"] = VisionValue.Double(t),
            ["rawProjectionT"] = VisionValue.Double(rawT), ["projectionClamped"] = VisionValue.Boolean(clamped), ["withinSegment"] = VisionValue.Boolean(within),
            ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["distance"] = distance, ["segment"] = segment, ["signedDistanceMeaning"] = "到无限直线的有向垂距", ["diagnostics"] = diag },
            [new VisionOverlay("point-line-distance", VisionOverlayType.Line, X: point.X, Y: point.Y, X2: closest.X, Y2: closest.Y, Stroke: "#722ed1", StrokeWidth: 2)]));
    }
}

public sealed class CircleSpacingNode : IVisionNodeExecutor
{
    public string Type => "measure.circleSpacing";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var a = context.Require<VisionCircle>("circleA"); var b = context.Require<VisionCircle>("circleB");
        if (new[] { a.X, a.Y, b.X, b.Y }.Any(v => !double.IsFinite(v))) throw new InvalidOperationException("Circle centers must be finite.");
        if (!double.IsFinite(a.Radius) || !double.IsFinite(b.Radius) || a.Radius < 0 || b.Radius < 0) throw new InvalidOperationException("Circle radii must be finite and non-negative.");
        var centers = MeasurementMath.Hypot(a.X - b.X, a.Y - b.Y); var gap = centers - a.Radius - b.Radius;
        if (!double.IsFinite(centers) || !double.IsFinite(gap)) throw new InvalidOperationException("measure.circleSpacing produced non-finite output.");
        var radiusDifference = Math.Abs(a.Radius - b.Radius);
        var contained = centers <= radiusDifference + 1e-9;
        var internalTangent = radiusDifference > 0 && Math.Abs(centers - radiusDifference) < 1e-9;
        var relation = Math.Abs(gap) < 1e-9 || internalTangent ? "tangent" : gap > 0 ? "separate" : contained ? "contained" : "overlapping";
        var diag = CoordinateValidation.Diagnostics(Type, ("centerDistance", centers), ("gap", gap), ("relation", relation),
            ("contained", contained), ("internalTangent", internalTangent), ("radiusA", a.Radius), ("radiusB", b.Radius));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["centerDistance"] = VisionValue.Double(centers), ["gap"] = VisionValue.Double(gap), ["measurement"] = VisionValue.Measurement(new VisionMeasurement(gap, "px", "signedCircleGap")), ["relation"] = VisionValue.String(relation), ["overlapping"] = VisionValue.Boolean(gap < 0), ["contained"] = VisionValue.Boolean(contained), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["centerDistance"] = centers, ["gap"] = gap, ["overlapping"] = gap < 0, ["contained"] = contained, ["relation"] = relation, ["diagnostics"] = diag }, [new VisionOverlay("circle-centers", VisionOverlayType.Line, X: a.X, Y: a.Y, X2: b.X, Y2: b.Y, Stroke: "#722ed1", StrokeWidth: 1.5)]));
    }
}

internal static class MultiPointFit
{
    public static VisionPoint2D[] Read(NodeExecutionContext context, CancellationToken cancellationToken, int minimumCount = 2)
    {
        var inputs = new SortedDictionary<int, VisionPoint2D>();
        foreach (var (name, value) in context.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!name.StartsWith("point", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(name.AsSpan(5), out var index) || index is < 1 or > 12
                || !name.Equals($"point{index}", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fit point inputs must be named point1 through point12.");
            var point = value.Require<VisionPoint2D>(name);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new InvalidOperationException("Fit points must have finite coordinates.");
            if (!inputs.TryAdd(index, point)) throw new InvalidOperationException($"Fit input point{index} was supplied more than once.");
        }
        var points = inputs.Values.ToArray();
        if (points.Length < minimumCount)
            throw new InvalidOperationException($"Connect at least {minimumCount} point inputs (point1 through point12).");
        return points;
    }

    // Production path: JLVision FitLineContourXld / FitCircleContourXld, then MAD residual
    // metrics (inlier count / RMS / max) in managed code. GeometryFitting remains available
    // for pure-managed unit tests; nodes must not invent a second fitting algorithm.
    public static GeometryFitting.LineFit LineFit(VisionPoint2D[] p, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Gross-outlier prefilter (pair consensus) before JLVision so a single leverage point
        // cannot warp FitLineContourXld; the geometric fit itself stays JLVision.
        var seed = LineInlierSeed(p, cancellationToken);
        var line = JLVisionPointFitting.Line(seed);
        cancellationToken.ThrowIfCancellationRequested();
        var dx = line.End.X - line.Start.X;
        var dy = line.End.Y - line.Start.Y;
        var length = double.Hypot(dx, dy);
        if (!double.IsFinite(length) || length < 1e-12) throw new InvalidOperationException("JLVision line fit is degenerate.");
        dx /= length; dy /= length;
        var residuals = Residuals(p, pt => LineResidual(pt, line.Start, dx, dy), cancellationToken);
        var (limit, rms, maxErr, count) = MadInliers(residuals, 2, cancellationToken);
        if (count < p.Length && count >= 2)
        {
            var keep = Inliers(p, residuals, limit, cancellationToken);
            line = JLVisionPointFitting.Line(keep);
            cancellationToken.ThrowIfCancellationRequested();
            dx = line.End.X - line.Start.X; dy = line.End.Y - line.Start.Y;
            length = double.Hypot(dx, dy);
            if (!double.IsFinite(length) || length < 1e-12) throw new InvalidOperationException("JLVision line fit is degenerate after inlier refine.");
            dx /= length; dy /= length;
            var refined = Residuals(keep, pt => LineResidual(pt, line.Start, dx, dy), cancellationToken);
            rms = Rms(refined, cancellationToken);
            maxErr = refined.Max();
            count = keep.Length;
        }
        return new(line, rms, maxErr, count, p.Length, line.AngleDeg);
    }

    private static VisionPoint2D[] LineInlierSeed(VisionPoint2D[] pts, CancellationToken cancellationToken)
    {
        if (pts.Length < 3) return pts;
        var candidates = pts.Length <= 120 ? pts : Enumerable.Range(0, 120).Select(i => pts[i * pts.Length / 120]).ToArray();
        var bestCount = 0;
        var bestScore = double.MaxValue;
        var bestOrigin = pts[0]; var bestX = 0d; var bestY = 0d;
        for (var i = 0; i < candidates.Length; i++)
        for (var j = i + 1; j < candidates.Length; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pairDx = candidates[j].X - candidates[i].X; var pairDy = candidates[j].Y - candidates[i].Y;
            var len = double.Hypot(pairDx, pairDy);
            if (!double.IsFinite(len) || len < 1e-9) continue;
            pairDx /= len; pairDy /= len;
            var count = 0; var score = 0d;
            for (var k = 0; k < pts.Length; k++)
            {
                if ((k & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var residual = LineResidual(pts[k], candidates[i], pairDx, pairDy);
                if (residual <= 2) count++;
                var capped = Math.Min(residual, 2);
                score += capped * capped;
            }
            if (count > bestCount || count == bestCount && score < bestScore)
            {
                bestCount = count; bestScore = score;
                bestOrigin = candidates[i]; bestX = pairDx; bestY = pairDy;
            }
        }
        if (bestCount < 2) return pts;
        var inliers = new List<VisionPoint2D>(bestCount);
        for (var i = 0; i < pts.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (LineResidual(pts[i], bestOrigin, bestX, bestY) <= 2) inliers.Add(pts[i]);
        }
        return inliers.ToArray();
    }

    public static GeometryFitting.CircleFit CircleFit(VisionPoint2D[] p, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (p.Length < 3) throw new InvalidOperationException("A circle fit requires at least three points.");
        var circle = JLVisionPointFitting.Circle(p);
        cancellationToken.ThrowIfCancellationRequested();
        var residuals = Residuals(p, pt => Math.Abs(double.Hypot(pt.X - circle.X, pt.Y - circle.Y) - circle.Radius), cancellationToken);
        var (limit, rms, maxErr, count) = MadInliers(residuals, 3, cancellationToken);
        if (count < p.Length && count >= 3)
        {
            var keep = Inliers(p, residuals, limit, cancellationToken);
            circle = JLVisionPointFitting.Circle(keep);
            cancellationToken.ThrowIfCancellationRequested();
            var refined = Residuals(keep, pt => Math.Abs(double.Hypot(pt.X - circle.X, pt.Y - circle.Y) - circle.Radius), cancellationToken);
            rms = Rms(refined, cancellationToken);
            maxErr = refined.Max();
            count = keep.Length;
        }
        return new(circle, rms, maxErr, count, p.Length);
    }

    private static (double Limit, double Rms, double MaxError, int Count) MadInliers(double[] residuals, int minimumCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sorted = residuals.OrderBy(r => r).ToArray();
        var med = SortedMedian(sorted);
        var deviations = new double[residuals.Length];
        for (var i = 0; i < residuals.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            deviations[i] = Math.Abs(residuals[i] - med);
        }
        Array.Sort(deviations);
        cancellationToken.ThrowIfCancellationRequested();
        var mad = 1.4826 * SortedMedian(deviations);
        var limit = Math.Min(double.MaxValue, Math.Max(3 * mad, 0.75));
        // Keep the reported threshold and the actual support set consistent.
        // Circle refinement needs three points; line refinement needs two.
        limit = Math.Max(limit, sorted[minimumCount - 1]);
        var inlierResiduals = sorted.TakeWhile(r => r <= limit).ToArray();
        return (limit, Rms(inlierResiduals, cancellationToken), inlierResiduals[^1], inlierResiduals.Length);
    }

    private static double SortedMedian(double[] sorted)
        => sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : sorted[sorted.Length / 2 - 1] + (sorted[sorted.Length / 2] - sorted[sorted.Length / 2 - 1]) / 2;

    private static double LineResidual(VisionPoint2D point, VisionPoint2D origin, double ux, double uy)
        => Math.Abs(Math.FusedMultiplyAdd(point.Y - origin.Y, ux, -(point.X - origin.X) * uy));

    private static double[] Residuals(VisionPoint2D[] points, Func<VisionPoint2D, double> measure, CancellationToken cancellationToken)
    {
        var residuals = new double[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var residual = measure(points[i]);
            if (!double.IsFinite(residual) || residual < 0)
                throw new InvalidOperationException("Fit residuals exceed the finite numeric range; adjust coordinates or units.");
            residuals[i] = residual;
        }
        return residuals;
    }

    private static VisionPoint2D[] Inliers(VisionPoint2D[] points, double[] residuals, double limit, CancellationToken cancellationToken)
    {
        var selected = new List<VisionPoint2D>();
        for (var i = 0; i < points.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (residuals[i] <= limit) selected.Add(points[i]);
        }
        return selected.ToArray();
    }

    private static double Rms(double[] residuals, CancellationToken cancellationToken)
    {
        var maximum = residuals.Max();
        if (maximum == 0) return 0;
        var squares = 0d;
        for (var i = 0; i < residuals.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var scaled = residuals[i] / maximum;
            squares += scaled * scaled;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return maximum * Math.Sqrt(squares / residuals.Length);
    }

    public static VisionLine2D Line(VisionPoint2D[] p, CancellationToken cancellationToken = default) => LineFit(p, cancellationToken).Line;
    public static VisionCircle Circle(VisionPoint2D[] p, CancellationToken cancellationToken = default) => CircleFit(p, cancellationToken).Circle;

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
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var maxRms = node.GetDouble("maxRmsError", 0); if (!double.IsFinite(maxRms) || maxRms < 0) throw new InvalidOperationException("measure.fitLine maxRmsError must be finite and >= 0 (0 disables the gate).");
        var points = MultiPointFit.Read(context, cancellationToken);
        var fit = MultiPointFit.LineFit(points, cancellationToken); var line = fit.Line;
        if (maxRms > 0 && fit.RmsError > maxRms) throw new InvalidOperationException($"measure.fitLine RMS error {fit.RmsError:0.####} exceeds maxRmsError {maxRms:0.####}.");
        if (!double.IsFinite(fit.RmsError) || !double.IsFinite(fit.MaxError)) throw new InvalidOperationException("measure.fitLine produced non-finite residuals.");
        var outliers = points.Length - fit.InlierCount; var diag = CoordinateValidation.Diagnostics(Type, ("pointCount", points.Length), ("inlierCount", fit.InlierCount), ("outlierCount", outliers), ("rmsError", fit.RmsError), ("maxError", fit.MaxError), ("length", line.Length), ("angleDeg", line.AngleDeg));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["line"] = VisionValue.Line(line), ["length"] = VisionValue.Double(line.Length), ["angle"] = VisionValue.Double(line.AngleDeg), ["rmsError"] = VisionValue.Double(fit.RmsError), ["maxError"] = VisionValue.Double(fit.MaxError), ["inlierCount"] = VisionValue.Integer(fit.InlierCount), ["pointCount"] = VisionValue.Integer(points.Length), ["outlierCount"] = VisionValue.Integer(outliers), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["pointCount"] = points.Length, ["length"] = line.Length, ["angleDeg"] = line.AngleDeg, ["rmsError"] = Math.Round(fit.RmsError, 4), ["maxError"] = Math.Round(fit.MaxError, 4), ["inlierCount"] = fit.InlierCount, ["diagnostics"] = diag }));
    }
}

public sealed class FitCircleNode : IVisionNodeExecutor
{
    public string Type => "measure.fitCircle";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var maxRms = node.GetDouble("maxRmsError", 0); if (!double.IsFinite(maxRms) || maxRms < 0) throw new InvalidOperationException("measure.fitCircle maxRmsError must be finite and >= 0 (0 disables the gate).");
        var points = MultiPointFit.Read(context, cancellationToken, minimumCount: 3);
        var fit = MultiPointFit.CircleFit(points, cancellationToken); var circle = fit.Circle;
        if (maxRms > 0 && fit.RmsError > maxRms) throw new InvalidOperationException($"measure.fitCircle RMS error {fit.RmsError:0.####} exceeds maxRmsError {maxRms:0.####}.");
        if (!double.IsFinite(fit.RmsError) || !double.IsFinite(fit.MaxError) || !double.IsFinite(circle.Radius) || circle.Radius <= 0) throw new InvalidOperationException("measure.fitCircle produced a non-finite or non-positive circle.");
        var outliers = points.Length - fit.InlierCount; var diag = CoordinateValidation.Diagnostics(Type, ("pointCount", points.Length), ("inlierCount", fit.InlierCount), ("outlierCount", outliers), ("rmsError", fit.RmsError), ("maxError", fit.MaxError), ("radius", circle.Radius), ("centerX", circle.X), ("centerY", circle.Y));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["circle"] = VisionValue.Circle(circle), ["center"] = VisionValue.Point(new(circle.X,circle.Y)), ["radius"] = VisionValue.Double(circle.Radius), ["rmsError"] = VisionValue.Double(fit.RmsError), ["maxError"] = VisionValue.Double(fit.MaxError), ["inlierCount"] = VisionValue.Integer(fit.InlierCount), ["pointCount"] = VisionValue.Integer(points.Length), ["outlierCount"] = VisionValue.Integer(outliers), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["pointCount"] = points.Length, ["x"] = circle.X, ["y"] = circle.Y, ["radius"] = circle.Radius, ["rmsError"] = Math.Round(fit.RmsError, 4), ["maxError"] = Math.Round(fit.MaxError, 4), ["inlierCount"] = fit.InlierCount, ["diagnostics"] = diag }));
    }
}
