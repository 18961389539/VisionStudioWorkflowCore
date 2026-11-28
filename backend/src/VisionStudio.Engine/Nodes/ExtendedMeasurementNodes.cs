using VisionStudio.Engine.Algorithms;
using VisionStudio.Abstractions;

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
        var clamped = Math.Abs(t - rawT) > 1e-9;
        var endpointDistance = Math.Min(MeasurementMath.Hypot(point.X - line.Start.X, point.Y - line.Start.Y),
            MeasurementMath.Hypot(point.X - line.End.X, point.Y - line.End.Y));
        var maxDistance = node.GetDouble("maxDistance", 0);
        if (!double.IsFinite(maxDistance) || maxDistance < 0)
            throw new InvalidOperationException("measure.pointLineDistance maxDistance must be finite and >= 0 (0 disables the gate).");
        var requireWithinSegment = node.GetBool("requireWithinSegment", false);
        var gateEnabled = maxDistance > 0 || requireWithinSegment;
        var segmentPassed = !requireWithinSegment || within;
        var distancePassed = maxDistance <= 0 || distance <= maxDistance + 1e-9;
        var withinTolerance = gateEnabled && segmentPassed && distancePassed;
        var reason = !segmentPassed ? "outside-segment" : !distancePassed ? "distance-exceeded" : gateEnabled ? "ok" : "not-evaluated";
        var evaluationState = !gateEnabled ? "not-evaluated" : withinTolerance ? "pass" : "fail";
        var referenceKind = segment ? "segment" : "infinite-line";
        var diag = CoordinateValidation.Diagnostics(Type,
            ("distance", distance), ("referenceKind", referenceKind), ("distanceTo", segment ? "segment" : "line"), ("signedDistance", signed),
            ("signedDistanceTo", "infiniteLine"), ("endpointDistance", endpointDistance), ("rawProjectionT", rawT), ("projectionT", t),
            ("projectionClamped", clamped), ("withinSegment", within),
            ("maxDistance", maxDistance), ("requireWithinSegment", requireWithinSegment),
            ("withinTolerance", withinTolerance), ("reason", reason), ("evaluationState", evaluationState));
        var outputs = new Dictionary<string, VisionValue> {
            ["distance"] = VisionValue.Double(distance), ["closestPoint"] = VisionValue.Point(closest), ["projectedPoint"] = VisionValue.Point(projected),
            // signedDistance/lineDistance 始终是无限直线垂距；segment 只在投影落在线段内时才有定义。
            ["signedDistance"] = VisionValue.Double(signed), ["lineDistance"] = VisionValue.Double(Math.Abs(signed)),
            ["referenceKind"] = VisionValue.String(referenceKind), ["endpointDistance"] = VisionValue.Double(endpointDistance),
            ["projectionT"] = VisionValue.Double(t),
            ["rawProjectionT"] = VisionValue.Double(rawT), ["projectionClamped"] = VisionValue.Boolean(clamped), ["withinSegment"] = VisionValue.Boolean(within),
            ["withinTolerance"] = VisionValue.Boolean(withinTolerance), ["reason"] = VisionValue.String(reason),
            ["evaluationState"] = VisionValue.String(evaluationState),
            ["diagnostics"] = VisionValue.String(diag) };
        if (segment && within) outputs["signedSegmentDistance"] = VisionValue.Double(signed);
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?> {
            ["distance"] = distance, ["segment"] = segment, ["referenceKind"] = referenceKind,
            ["signedDistanceMeaning"] = "到无限直线的有向垂距", ["endpointDistance"] = endpointDistance,
            ["withinTolerance"] = withinTolerance, ["reason"] = reason, ["evaluationState"] = evaluationState,
            ["diagnostics"] = diag },
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
        var centers = MeasurementMath.Hypot(a.X - b.X, a.Y - b.Y);
        if (!double.IsFinite(centers)) throw new InvalidOperationException("measure.circleSpacing produced non-finite output.");
        // 相切判据必须是可解释的长度公差：1e-9 px 远小于拟合/标定不确定度，
        // 会让本应相切的结果因数值噪声跳到 overlapping/separate。
        var tolerance = node.GetDouble("relationTolerancePx", 1e-6);
        if (!double.IsFinite(tolerance) || tolerance is < 0 or > 1)
            throw new InvalidOperationException("measure.circleSpacing relationTolerancePx must be finite within 0..1 px.");
        var radiusDifference = Math.Abs(a.Radius - b.Radius);
        var externalGap = Math.Abs(centers - (a.Radius + b.Radius));
        var internalGap = Math.Abs(centers - radiusDifference);
        var internalTangent = radiusDifference > 0 && internalGap <= tolerance;
        var boundariesIntersect = externalGap <= tolerance || internalTangent;
        var contained = !boundariesIntersect && centers < radiusDifference;
        var disksIntersect = centers <= a.Radius + b.Radius;
        // A signed negative gap is not a physical clearance for contained disks.
        var gapValid = !contained && !boundariesIntersect;
        var gap = gapValid ? centers - a.Radius - b.Radius : double.NaN;
        var relation = boundariesIntersect ? "tangent" : contained ? "contained" : disksIntersect ? "overlapping" : "separate";
        var diag = CoordinateValidation.Diagnostics(Type, ("centerDistance", centers), ("gap", gap), ("gapValid", gapValid), ("relation", relation),
            ("contained", contained), ("disksIntersect", disksIntersect), ("boundariesIntersect", boundariesIntersect),
            ("internalTangent", internalTangent), ("relationTolerancePx", tolerance),
            ("externalGap", externalGap), ("internalGap", internalGap), ("radiusA", a.Radius), ("radiusB", b.Radius));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> {
            ["centerDistance"] = VisionValue.Double(centers), ["gap"] = VisionValue.Double(gap),
            ["gapValid"] = VisionValue.Boolean(gapValid),
            ["measurement"] = VisionValue.Measurement(new VisionMeasurement(gap, "px", "minimumCircumferenceGap")),
            ["relation"] = VisionValue.String(relation), ["overlapping"] = VisionValue.Boolean(disksIntersect && !boundariesIntersect),
            ["disksIntersect"] = VisionValue.Boolean(disksIntersect), ["boundariesIntersect"] = VisionValue.Boolean(boundariesIntersect),
            ["contained"] = VisionValue.Boolean(contained), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["centerDistance"] = centers, ["gap"] = gap, ["gapValid"] = gapValid,
                ["overlapping"] = disksIntersect && !boundariesIntersect, ["disksIntersect"] = disksIntersect,
                ["boundariesIntersect"] = boundariesIntersect, ["contained"] = contained, ["relation"] = relation, ["diagnostics"] = diag },
            [new VisionOverlay("circle-centers", VisionOverlayType.Line, X: a.X, Y: a.Y, X2: b.X, Y2: b.Y, Stroke: "#722ed1", StrokeWidth: 1.5)]));
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
        // 最小二乘会被离群点整体拉偏，一旦圆本身偏了，MAD 就再也分不出内外点。
        // 先用确定性 3 点采样挑“支撑最多”的种子圆，再做 MAD 精修。
        var circle = p.Length > 3 ? RobustCircleSeed(p, cancellationToken) : JLVisionPointFitting.Circle(p);
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

    /// <summary>支撑集固定的 2px 容差：只用于种子评估，不参与最终质量判定。</summary>
    private const double SeedTolerancePx = 2;

    private static VisionCircle RobustCircleSeed(VisionPoint2D[] p, CancellationToken cancellationToken)
    {
        var best = JLVisionPointFitting.Circle(p);
        var bestCount = CountWithin(best, p, cancellationToken);
        if (bestCount == p.Length) return best;
        var bestScore = double.MaxValue;
        uint state = 0x9E3779B9u;
        int Next(int bound) { state = unchecked(state * 1664525u + 1013904223u); return (int)(state % (uint)bound); }
        // 点数越多，单次采样成本越高；大轮廓降低采样次数，保持节拍稳定。
        var attempts = p.Length > 200 ? 80 : 400;
        for (var attempt = 0; attempt < attempts && bestCount < p.Length; attempt++)
        {
            if ((attempt & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var i = Next(p.Length); var j = Next(p.Length); var k = Next(p.Length);
            if (i == j || j == k || i == k) continue;
            VisionCircle candidate;
            try { candidate = JLVisionPointFitting.Circle([p[i], p[j], p[k]]); }
            catch { continue; }
            if (!double.IsFinite(candidate.Radius) || candidate.Radius <= 0 || !double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y)) continue;
            var count = CountWithin(candidate, p, cancellationToken);
            var score = 0d;
            foreach (var point in p)
            {
                var residual = Math.Abs(double.Hypot(point.X - candidate.X, point.Y - candidate.Y) - candidate.Radius);
                score += Math.Min(residual, SeedTolerancePx) * Math.Min(residual, SeedTolerancePx);
            }
            if (count > bestCount || count == bestCount && score < bestScore)
            {
                bestCount = count; bestScore = score; best = candidate;
            }
        }
        return best;
    }

    private static int CountWithin(VisionCircle circle, VisionPoint2D[] points, CancellationToken cancellationToken)
    {
        var count = 0;
        for (var index = 0; index < points.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (Math.Abs(double.Hypot(points[index].X - circle.X, points[index].Y - circle.Y) - circle.Radius) <= SeedTolerancePx) count++;
        }
        return count;
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

    /// <summary>全点 RMS：不剔除离群点，用来暴露“少数点撑起高置信结果”。</summary>
    public static double AllPointRms(VisionPoint2D[] points, VisionCircle circle, CancellationToken cancellationToken = default)
        => Rms(Residuals(points, pt => Math.Abs(double.Hypot(pt.X - circle.X, pt.Y - circle.Y) - circle.Radius), cancellationToken), cancellationToken);

    public static double AllPointRms(VisionPoint2D[] points, VisionLine2D line, CancellationToken cancellationToken = default)
    {
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y;
        var length = double.Hypot(dx, dy);
        if (!double.IsFinite(length) || length < 1e-12) throw new InvalidOperationException("JLVision line fit is degenerate.");
        return Rms(Residuals(points, pt => LineResidual(pt, line.Start, dx / length, dy / length), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 质量门：内点 RMS + 支撑数量 + 支撑比例三者同时约束。门限全关时返回 not-evaluated，避免“未设置即合格”。
    /// </summary>
    public static (bool Found, string Reason, bool GatesEnabled) EvaluateGates(
        double rms, int inlierCount, int pointCount, double maxRms, int minInlierCount, double minInlierRatio)
    {
        var enabled = maxRms > 0 || minInlierCount > 0 || minInlierRatio > 0;
        if (!enabled) return (true, "ok", false);
        if (inlierCount < minInlierCount) return (false, "insufficient-inliers", true);
        var ratio = pointCount == 0 ? 0 : (double)inlierCount / pointCount;
        if (minInlierRatio > 0 && ratio < minInlierRatio - 1e-12) return (false, "insufficient-inlier-ratio", true);
        if (maxRms > 0 && rms > maxRms) return (false, "rms-exceeded", true);
        return (true, "ok", true);
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
        var minInlierCount = node.GetInt("minInlierCount", 0); if (minInlierCount < 0) throw new InvalidOperationException("measure.fitLine minInlierCount must be >= 0 (0 disables the gate).");
        var minInlierRatio = node.GetDouble("minInlierRatio", 0); if (!double.IsFinite(minInlierRatio) || minInlierRatio is < 0 or > 1) throw new InvalidOperationException("measure.fitLine minInlierRatio must be within 0..1 (0 disables the gate).");
        var points = MultiPointFit.Read(context, cancellationToken);
        var fit = MultiPointFit.LineFit(points, cancellationToken); var line = fit.Line;
        if (!double.IsFinite(fit.RmsError) || !double.IsFinite(fit.MaxError)) throw new InvalidOperationException("measure.fitLine produced non-finite residuals.");
        var allPointRms = MultiPointFit.AllPointRms(points, line, cancellationToken);
        var (found, reason, gatesEnabled) = MultiPointFit.EvaluateGates(fit.RmsError, fit.InlierCount, points.Length, maxRms, minInlierCount, minInlierRatio);
        var outliers = points.Length - fit.InlierCount;
        var inlierRatio = points.Length == 0 ? 0 : (double)fit.InlierCount / points.Length;
        // 方向极性显式化：angle 由 Start→End 决定，反向输入会差 180°，而平行度等下游只关心无向直线。
        var angleMod180 = VisionTransform2D.NormalizeAngleDeg(line.AngleDeg) % 180;
        if (angleMod180 < 0) angleMod180 += 180;
        // 极性按输入点序（首点→末点）在拟合方向上的投影判定：反向输入翻转符号，
        // 而角度本身可能被拟合库归一化，不能直接拿端点方向当极性。
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y;
        var length = double.Hypot(dx, dy);
        var axial = length < 1e-12 ? 0 : ((points[^1].X - points[0].X) * dx + (points[^1].Y - points[0].Y) * dy) / length;
        var directionSign = axial >= 0 ? 1 : -1;
        var evaluationState = !gatesEnabled ? "not-evaluated" : found ? "pass" : "fail";
        var diag = CoordinateValidation.Diagnostics(Type,
            ("pointCount", points.Length), ("inlierCount", fit.InlierCount), ("outlierCount", outliers), ("inlierRatio", inlierRatio),
            ("rmsError", fit.RmsError), ("maxError", fit.MaxError), ("allPointRmsError", allPointRms),
            ("maxRmsError", maxRms), ("minInlierCount", minInlierCount), ("minInlierRatio", minInlierRatio),
            ("gatesEnabled", gatesEnabled), ("found", found), ("reason", reason), ("evaluationState", evaluationState),
            ("length", line.Length), ("angleDeg", line.AngleDeg), ("angleMod180", angleMod180), ("directionSign", directionSign),
            ("polarityKnown", true));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["line"] = VisionValue.Line(line), ["length"] = VisionValue.Double(line.Length), ["angle"] = VisionValue.Double(line.AngleDeg),
            ["angleMod180"] = VisionValue.Double(angleMod180), ["directionSign"] = VisionValue.Integer(directionSign), ["polarityKnown"] = VisionValue.Boolean(true),
            ["rmsError"] = VisionValue.Double(fit.RmsError), ["maxError"] = VisionValue.Double(fit.MaxError), ["allPointRmsError"] = VisionValue.Double(allPointRms),
            ["inlierCount"] = VisionValue.Integer(fit.InlierCount), ["pointCount"] = VisionValue.Integer(points.Length), ["outlierCount"] = VisionValue.Integer(outliers),
            ["inlierRatio"] = VisionValue.Double(inlierRatio), ["found"] = VisionValue.Boolean(found), ["reason"] = VisionValue.String(reason),
            ["evaluationState"] = VisionValue.String(evaluationState), ["diagnostics"] = VisionValue.String(diag)
        }, new Dictionary<string, object?>
        {
            ["pointCount"] = points.Length, ["length"] = line.Length, ["angleDeg"] = line.AngleDeg,
            ["angleMod180"] = Math.Round(angleMod180, 6), ["directionSign"] = directionSign,
            ["rmsError"] = Math.Round(fit.RmsError, 4), ["maxError"] = Math.Round(fit.MaxError, 4), ["allPointRmsError"] = Math.Round(allPointRms, 4),
            ["inlierCount"] = fit.InlierCount, ["inlierRatio"] = Math.Round(inlierRatio, 6),
            ["found"] = found, ["reason"] = reason, ["evaluationState"] = evaluationState, ["diagnostics"] = diag
        }));
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
        var minInlierCount = node.GetInt("minInlierCount", 0); if (minInlierCount < 0) throw new InvalidOperationException("measure.fitCircle minInlierCount must be >= 0 (0 disables the gate).");
        var minInlierRatio = node.GetDouble("minInlierRatio", 0); if (!double.IsFinite(minInlierRatio) || minInlierRatio is < 0 or > 1) throw new InvalidOperationException("measure.fitCircle minInlierRatio must be within 0..1 (0 disables the gate).");
        var points = MultiPointFit.Read(context, cancellationToken, minimumCount: 3);
        var fit = MultiPointFit.CircleFit(points, cancellationToken); var circle = fit.Circle;
        if (!double.IsFinite(fit.RmsError) || !double.IsFinite(fit.MaxError) || !double.IsFinite(circle.Radius) || circle.Radius <= 0) throw new InvalidOperationException("measure.fitCircle produced a non-finite or non-positive circle.");
        var allPointRms = MultiPointFit.AllPointRms(points, circle, cancellationToken);
        var (found, reason, gatesEnabled) = MultiPointFit.EvaluateGates(fit.RmsError, fit.InlierCount, points.Length, maxRms, minInlierCount, minInlierRatio);
        var outliers = points.Length - fit.InlierCount;
        var inlierRatio = points.Length == 0 ? 0 : (double)fit.InlierCount / points.Length;
        var evaluationState = !gatesEnabled ? "not-evaluated" : found ? "pass" : "fail";
        // 质量不合格不再抛异常：圆与残差保留，供复检区分“拟合失败”和“拟合出来但不合格”。
        var diag = CoordinateValidation.Diagnostics(Type,
            ("pointCount", points.Length), ("inlierCount", fit.InlierCount), ("outlierCount", outliers), ("inlierRatio", inlierRatio),
            ("rmsError", fit.RmsError), ("maxError", fit.MaxError), ("allPointRmsError", allPointRms),
            ("maxRmsError", maxRms), ("minInlierCount", minInlierCount), ("minInlierRatio", minInlierRatio),
            ("gatesEnabled", gatesEnabled), ("found", found), ("reason", reason), ("evaluationState", evaluationState),
            ("radius", circle.Radius), ("centerX", circle.X), ("centerY", circle.Y));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["circle"] = VisionValue.Circle(circle), ["center"] = VisionValue.Point(new(circle.X, circle.Y)), ["radius"] = VisionValue.Double(circle.Radius),
            ["rmsError"] = VisionValue.Double(fit.RmsError), ["maxError"] = VisionValue.Double(fit.MaxError), ["allPointRmsError"] = VisionValue.Double(allPointRms),
            ["inlierCount"] = VisionValue.Integer(fit.InlierCount), ["pointCount"] = VisionValue.Integer(points.Length), ["outlierCount"] = VisionValue.Integer(outliers),
            ["inlierRatio"] = VisionValue.Double(inlierRatio), ["found"] = VisionValue.Boolean(found), ["reason"] = VisionValue.String(reason),
            ["evaluationState"] = VisionValue.String(evaluationState), ["diagnostics"] = VisionValue.String(diag)
        }, new Dictionary<string, object?>
        {
            ["pointCount"] = points.Length, ["x"] = circle.X, ["y"] = circle.Y, ["radius"] = circle.Radius,
            ["rmsError"] = Math.Round(fit.RmsError, 4), ["maxError"] = Math.Round(fit.MaxError, 4), ["allPointRmsError"] = Math.Round(allPointRms, 4),
            ["inlierCount"] = fit.InlierCount, ["inlierRatio"] = Math.Round(inlierRatio, 6),
            ["found"] = found, ["reason"] = reason, ["evaluationState"] = evaluationState, ["diagnostics"] = diag
        }));
    }
}
