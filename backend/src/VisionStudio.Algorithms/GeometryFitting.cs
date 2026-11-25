namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Deterministic total-least-squares line and geometric circle fitting with robust
/// (Tukey/MAD) outlier rejection. Pure managed math: no native calls, so results are
/// identical on every platform and unit-testable without OpenCV/JLVision binaries.
/// Mirrors HALCON fit_line_contour_xld / fit_circle_contour_xld semantics conceptually:
/// residuals, RMS and inlier counts are always reported, never hidden.
/// </summary>
public static class GeometryFitting
{
    public sealed record LineFit(VisionLine2D Line, double RmsError, double MaxError, int InlierCount, int PointCount, double AngleDeg);
    public sealed record CircleFit(VisionCircle Circle, double RmsError, double MaxError, int InlierCount, int PointCount);

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    public static LineFit FitLine(IReadOnlyList<VisionPoint2D> points)
    {
        if (points.Count < 2) throw new InvalidOperationException("A line fit requires at least two points.");
        if (points.All(p => p == points[0])) throw new InvalidOperationException("A line cannot be fit when all points coincide.");
        var pts = points.ToArray();
        // Deterministic pair-RANSAC seed: total least squares alone breaks down under a
        // high-leverage outlier (it rotates the principal axis toward the outlier), so
        // first find the line supported by the most points within 2 px, then refine.
        var seed = pts;
        if (pts.Length >= 3)
        {
            var candidates = pts.Length <= 120 ? pts : Enumerable.Range(0, 120).Select(i => pts[i * pts.Length / 120]).ToArray();
            var bestInliers = Array.Empty<VisionPoint2D>();
            var bestScore = double.MaxValue;
            for (var i = 0; i < candidates.Length; i++)
            for (var j = i + 1; j < candidates.Length; j++)
            {
                var pairDx = candidates[j].X - candidates[i].X; var pairDy = candidates[j].Y - candidates[i].Y;
                var length = Math.Sqrt(pairDx * pairDx + pairDy * pairDy);
                if (length < 1e-9) continue;
                pairDx /= length; pairDy /= length;
                var residuals = pts.Select(p => Math.Abs(-(p.X - candidates[i].X) * pairDy + (p.Y - candidates[i].Y) * pairDx)).ToArray();
                var pairInliers = pts.Where((_, k) => residuals[k] <= 2.0).ToArray();
                var score = residuals.Sum(r => Math.Min(r * r, 4.0));
                if (pairInliers.Length > bestInliers.Length || pairInliers.Length == bestInliers.Length && score < bestScore)
                {
                    bestInliers = pairInliers; bestScore = score;
                }
            }
            if (bestInliers.Length >= 2) seed = bestInliers;
        }
        var (cx, cy, dx, dy) = FitLineTls(seed);
        var allResiduals = pts.Select(p => Math.Abs(-(p.X - cx) * dy + (p.Y - cy) * dx)).ToArray();
        var robustScale = 1.4826 * Median(allResiduals);
        var inlierLimit = Math.Max(3 * robustScale, 0.75);
        var inliers = pts.Where((_, i) => allResiduals[i] <= inlierLimit).ToArray();
        if (inliers.Length < 2) inliers = pts.Select((p, i) => (p, r: allResiduals[i])).OrderBy(v => v.r).Take(2).Select(v => v.p).ToArray();
        if (inliers.Length >= 2 && inliers.Length < pts.Length) (cx, cy, dx, dy) = FitLineTls(inliers);
        var inlierResiduals = inliers.Select(p => Math.Abs(-(p.X - cx) * dy + (p.Y - cy) * dx)).ToArray();
        var projections = inliers.Select(p => (p.X - cx) * dx + (p.Y - cy) * dy).ToArray();
        var start = new VisionPoint2D(cx + dx * projections.Min(), cy + dy * projections.Min());
        var end = new VisionPoint2D(cx + dx * projections.Max(), cy + dy * projections.Max());
        var line = start.X < end.X || start.X == end.X && start.Y <= end.Y ? new VisionLine2D(start, end) : new VisionLine2D(end, start);
        return new(line, Math.Sqrt(inlierResiduals.Average(r => r * r)), inlierResiduals.Max(), inliers.Length, pts.Length, line.AngleDeg);
    }

    private static (double Cx, double Cy, double Dx, double Dy) FitLineTls(IReadOnlyList<VisionPoint2D> points)
    {
        var pts = points.ToArray();
        var weights = pts.Select(_ => 1d).ToArray();
        double cx = 0, cy = 0, dx = 1, dy = 0;
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var wSum = weights.Sum();
            cx = pts.Zip(weights, (p, w) => p.X * w).Sum() / wSum;
            cy = pts.Zip(weights, (p, w) => p.Y * w).Sum() / wSum;
            double sxx = 0, syy = 0, sxy = 0;
            for (var i = 0; i < pts.Length; i++)
            {
                var ex = pts[i].X - cx; var ey = pts[i].Y - cy;
                sxx += weights[i] * ex * ex; syy += weights[i] * ey * ey; sxy += weights[i] * ex * ey;
            }
            var lambda1 = (sxx + syy) / 2 + Math.Sqrt(Math.Pow((sxx - syy) / 2, 2) + sxy * sxy);
            if (lambda1 <= 1e-18) throw new InvalidOperationException("A line cannot be fit when all points coincide.");
            (dx, dy) = Math.Abs(sxy) < 1e-18 && sxx >= syy ? (1d, 0d) : Math.Abs(sxy) < 1e-18 ? (0d, 1d) : Normalize(lambda1 - syy, sxy);
            var residuals = pts.Select(p => Math.Abs(-(p.X - cx) * dy + (p.Y - cy) * dx)).ToArray();
            var scale = 1.4826 * Median(residuals);
            if (scale < 1e-9) break;
            var c = 4.685 * scale;
            var changed = false;
            for (var i = 0; i < weights.Length; i++)
            {
                var u = residuals[i] / c;
                var w = u >= 1 ? 0 : Math.Pow(1 - u * u, 2);
                if (Math.Abs(w - weights[i]) > 1e-9) changed = true;
                weights[i] = w;
            }
            if (weights.Sum() < 2) break;
            if (!changed) break;
        }
        return (cx, cy, dx, dy);
    }

    private static (double, double) Normalize(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length < 1e-18 ? (1, 0) : (x / length, y / length);
    }

    public static CircleFit FitCircle(IReadOnlyList<VisionPoint2D> points)
    {
        if (points.Count < 3) throw new InvalidOperationException("A circle fit requires at least three points.");
        var active = points.ToArray();
        VisionCircle circle = FitCircleOnce(active);
        for (var round = 0; round < 8; round++)
        {
            var residuals = active.Select(p => Math.Abs(Math.Sqrt(Math.Pow(p.X - circle.X, 2) + Math.Pow(p.Y - circle.Y, 2)) - circle.Radius)).ToArray();
            var scale = 1.4826 * Median(residuals);
            var limit = Math.Max(3 * scale, 1.0);
            var kept = active.Where((_, i) => residuals[i] <= limit).ToArray();
            if (kept.Length == active.Length || kept.Length < 3) break;
            active = kept;
            circle = FitCircleOnce(active);
        }
        var finalResiduals = active.Select(p => Math.Abs(Math.Sqrt(Math.Pow(p.X - circle.X, 2) + Math.Pow(p.Y - circle.Y, 2)) - circle.Radius)).ToArray();
        return new(circle, Math.Sqrt(finalResiduals.Average(r => r * r)), finalResiduals.Max(), active.Length, points.Count);
    }

    private static VisionCircle FitCircleOnce(IReadOnlyList<VisionPoint2D> points)
    {
        // Kasa algebraic fit as the deterministic starting point.
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var p in points)
        {
            var z = p.X * p.X + p.Y * p.Y;
            sx += p.X; sy += p.Y; sxx += p.X * p.X; syy += p.Y * p.Y; sxy += p.X * p.Y; sxz += p.X * z; syz += p.Y * z; sz += z;
        }
        var n = points.Count;
        // Solve [sxx sxy sx; sxy syy sy; sx sy n] * [2cx, 2cy, c] = [sxz, syz, sz]
        var a = new double[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        var b = new[] { sxz, syz, sz };
        var solution = Solve3(a, b) ?? throw new InvalidOperationException("Circle fit is degenerate (points are collinear or coincident).");
        var cx = solution[0] / 2; var cy = solution[1] / 2;
        var radius = Math.Sqrt(Math.Max(1e-18, solution[2] + cx * cx + cy * cy));
        // Geometric refinement: Gauss-Newton on radial residuals with small damping.
        for (var iteration = 0; iteration < 25; iteration++)
        {
            double[,] jtj = new double[3, 3]; var jtr = new double[3];
            foreach (var p in points)
            {
                var dx = p.X - cx; var dy = p.Y - cy;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < 1e-12) continue;
                var residual = distance - radius;
                var j = new[] { -dx / distance, -dy / distance, -1d };
                for (var r = 0; r < 3; r++) { jtr[r] += j[r] * residual; for (var c = 0; c < 3; c++) jtj[r, c] += j[r] * j[c]; }
            }
            jtj[0, 0] += 1e-12; jtj[1, 1] += 1e-12; jtj[2, 2] += 1e-12;
            var step = Solve3(jtj, jtr.Select(v => -v).ToArray());
            if (step is null) break;
            cx += step[0]; cy += step[1]; radius += step[2];
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(radius) || radius <= 0)
                throw new InvalidOperationException("Circle fit diverged to a degenerate circle.");
            if (Math.Abs(step[0]) + Math.Abs(step[1]) + Math.Abs(step[2]) < 1e-12) break;
        }
        if (!double.IsFinite(radius) || radius <= 0) throw new InvalidOperationException("Circle fit is degenerate.");
        return new(cx, cy, radius);
    }

    private static double[]? Solve3(double[,] a, double[] b)
    {
        var m = new double[3, 4];
        for (var r = 0; r < 3; r++) { for (var c = 0; c < 3; c++) m[r, c] = a[r, c]; m[r, 3] = b[r]; }
        for (var col = 0; col < 3; col++)
        {
            var pivot = Enumerable.Range(col, 3 - col).MaxBy(r => Math.Abs(m[r, col]));
            if (Math.Abs(m[pivot, col]) < 1e-18) return null;
            for (var c = 0; c < 4; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (var r = 0; r < 3; r++)
            {
                if (r == col) continue;
                var factor = m[r, col] / m[col, col];
                for (var c = col; c < 4; c++) m[r, c] -= factor * m[col, c];
            }
        }
        return [m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2]];
    }
}
