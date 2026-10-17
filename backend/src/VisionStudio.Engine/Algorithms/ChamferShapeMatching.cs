namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Pure-managed edge/chamfer shape matching (HALCON find_shape_model concept,
/// Cognex PatMax direction — NOT parity). No native code: the model is a set
/// of contour points relative to an origin, the scene is reduced to a chamfer
/// distance transform of its edge map, and a pose scores by
///   score = 0.7 * coverage(d &lt;= tolerance) + 0.3 * exp(-meanCoveredDistance / tolerance)
/// so a partially occluded object (low coverage, good covered edges) can still
/// pass, while scattered clutter (low coverage, poor distances) cannot.
/// Search is coarse-to-fine: a strided coarse pass over angle/scale/translation
/// with a subsampled model, then local hill-climb refinement with the full
/// model and a parabolic sub-pixel translation estimate. Angle/scale remain
/// sampled (discrete), there is no gradient-direction (polarity) term, no
/// least-squares XLD refinement and no deformation model — see audit report.
/// </summary>
public static class ChamferShapeMatching
{
    public readonly record struct ModelPoint(double X, double Y);
    public sealed record Options(
        double AngleMinDeg = -180, double AngleMaxDeg = 180, double AngleStepDeg = 5,
        double ScaleMin = 1, double ScaleMax = 1, double ScaleStep = 0.1,
        double MinScore = 0.6, int MaxTargets = 5, double MinCenterDistance = 20,
        double TolerancePx = 2.5, int MaxModelPoints = 240, int CoarseStride = 4);
    public sealed record Hit(double X, double Y, double AngleDeg, double Scale, double Score,
        double Coverage, double MeanDistancePx, int ModelPoints);

    /// <summary>Subsample contour points (origin-relative) to at most maxPoints, keeping spread.</summary>
    public static ModelPoint[] BuildModel(IReadOnlyList<ModelPoint> points, int maxPoints)
    {
        if (points.Count == 0) return [];
        if (maxPoints <= 0 || points.Count <= maxPoints) return points.ToArray();
        var stride = (double)points.Count / maxPoints;
        var result = new ModelPoint[maxPoints];
        for (var i = 0; i < maxPoints; i++) result[i] = points[(int)(i * stride)];
        return result;
    }

    /// <summary>Two-pass chamfer distance transform (weights 1 / sqrt(2)). edges[y,x]=true is an edge.</summary>
    public static float[,] DistanceTransform(bool[,] edges)
    {
        var h = edges.GetLength(0); var w = edges.GetLength(1);
        const float inf = 1e6f; const float d2 = 1.41421356f;
        var dist = new float[h, w];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) dist[y, x] = edges[y, x] ? 0f : inf;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var v = dist[y, x];
            if (x > 0) v = Math.Min(v, dist[y, x - 1] + 1);
            if (y > 0) v = Math.Min(v, dist[y - 1, x] + 1);
            if (x > 0 && y > 0) v = Math.Min(v, dist[y - 1, x - 1] + d2);
            if (x < w - 1 && y > 0) v = Math.Min(v, dist[y - 1, x + 1] + d2);
            dist[y, x] = v;
        }
        for (var y = h - 1; y >= 0; y--) for (var x = w - 1; x >= 0; x--)
        {
            var v = dist[y, x];
            if (x < w - 1) v = Math.Min(v, dist[y, x + 1] + 1);
            if (y < h - 1) v = Math.Min(v, dist[y + 1, x] + 1);
            if (x < w - 1 && y < h - 1) v = Math.Min(v, dist[y + 1, x + 1] + d2);
            if (x > 0 && y < h - 1) v = Math.Min(v, dist[y + 1, x - 1] + d2);
            dist[y, x] = v;
        }
        return dist;
    }

    /// <summary>Score one pose. Points outside the image count as uncovered.</summary>
    public static (double Score, double Coverage, double MeanDistance) ScoreAt(
        float[,] dist, IReadOnlyList<ModelPoint> model, double cx, double cy, double angleDeg, double scale, double tolerancePx)
    {
        var h = dist.GetLength(0); var w = dist.GetLength(1);
        var rad = angleDeg * Math.PI / 180; var cos = Math.Cos(rad); var sin = Math.Sin(rad);
        var covered = 0; var coveredDist = 0d;
        foreach (var p in model)
        {
            // Image coords (y down): CCW in math sense matches native matcher convention.
            var x = cx + (cos * p.X + sin * p.Y) * scale;
            var y = cy + (-sin * p.X + cos * p.Y) * scale;
            var xi = (int)Math.Round(x); var yi = (int)Math.Round(y);
            if (xi < 0 || yi < 0 || xi >= w || yi >= h) continue;
            var d = dist[yi, xi];
            if (d <= tolerancePx) { covered++; coveredDist += d; }
        }
        if (model.Count == 0) return (0, 0, double.PositiveInfinity);
        var coverage = (double)covered / model.Count;
        var mean = covered > 0 ? coveredDist / covered : tolerancePx * 4;
        var score = 0.7 * coverage + 0.3 * Math.Exp(-mean / Math.Max(0.1, tolerancePx)) * coverage;
        return (score, coverage, mean);
    }

    public static IReadOnlyList<Hit> Search(float[,] dist, IReadOnlyList<ModelPoint> model, Options o)
    {
        if (model.Count < 8) throw new InvalidOperationException("Chamfer shape model needs at least 8 contour points.");
        var h = dist.GetLength(0); var w = dist.GetLength(1);
        var coarse = BuildModel(model, Math.Min(80, o.MaxModelPoints));
        var stride = Math.Max(1, o.CoarseStride);
        var candidates = new List<Hit>();
        for (var scale = o.ScaleMin; scale <= o.ScaleMax + 1e-9; scale += o.ScaleStep <= 0 ? 1 : o.ScaleStep)
        for (var angle = o.AngleMinDeg; angle <= o.AngleMaxDeg + 1e-9; angle += o.AngleStepDeg <= 0 ? 5 : o.AngleStepDeg)
        {
            for (var y = stride / 2; y < h; y += stride)
            for (var x = stride / 2; x < w; x += stride)
            {
                var (s, cov, mean) = ScoreAt(dist, coarse, x, y, angle, scale, o.TolerancePx);
                if (s >= o.MinScore * 0.75) candidates.Add(new(x, y, angle, scale, s, cov, mean, coarse.Length));
            }
            if (o.ScaleStep <= 0) break;
        }
        var refined = new List<Hit>();
        foreach (var c in candidates.OrderByDescending(c => c.Score).Take(40))
        {
            var best = Refine(dist, model, c, o);
            if (best.Score >= o.MinScore) refined.Add(best);
        }
        var hits = new List<Hit>();
        foreach (var hit in refined.OrderByDescending(r => r.Score))
        {
            if (hits.Any(k => Math.Sqrt((k.X-hit.X)*(k.X-hit.X)+(k.Y-hit.Y)*(k.Y-hit.Y)) < o.MinCenterDistance)) continue;
            hits.Add(hit with { X = Subpixel(dist, model, hit, 1), Y = Subpixel(dist, model, hit, 2) });
            if (hits.Count >= Math.Max(1, o.MaxTargets)) break;
        }
        return hits;
    }

    private static Hit Refine(float[,] dist, IReadOnlyList<ModelPoint> model, Hit seed, Options o)
    {
        var best = seed; var stepT = Math.Max(1d, o.CoarseStride / 2d); var stepA = Math.Max(0.5, o.AngleStepDeg / 2); var stepS = Math.Max(0.02, (o.ScaleStep <= 0 ? 0.1 : o.ScaleStep) / 2);
        for (var iter = 0; iter < 60; iter++)
        {
            var improved = false;
            foreach (var (dx, dy, da, ds) in new[] { (stepT,0d,0d,0d), (-stepT,0d,0d,0d), (0d,stepT,0d,0d), (0d,-stepT,0d,0d), (0d,0d,stepA,0d), (0d,0d,-stepA,0d), (0d,0d,0d,stepS), (0d,0d,0d,-stepS) })
            {
                var (s, cov, mean) = ScoreAt(dist, model, best.X + dx, best.Y + dy, best.AngleDeg + da, Math.Max(0.05, best.Scale + ds), o.TolerancePx);
                if (s > best.Score + 1e-9) { best = new(best.X + dx, best.Y + dy, best.AngleDeg + da, Math.Max(0.05, best.Scale + ds), s, cov, mean, model.Count); improved = true; }
            }
            if (!improved) { stepT /= 2; stepA /= 2; stepS /= 2; if (stepT < 0.25) break; }
        }
        return best;
    }

    /// <summary>Parabolic sub-pixel offset on one axis (axis 1=X, 2=Y) from score samples at -1,0,+1 px.</summary>
    private static double Subpixel(float[,] dist, IReadOnlyList<ModelPoint> model, Hit hit, int axis)
    {
        double At(double d) => axis == 1
            ? ScoreAt(dist, model, hit.X + d, hit.Y, hit.AngleDeg, hit.Scale, 2.5).Score
            : ScoreAt(dist, model, hit.X, hit.Y + d, hit.AngleDeg, hit.Scale, 2.5).Score;
        var s0 = At(0); var sm = At(-1); var sp = At(1); var denom = sm - 2 * s0 + sp;
        var offset = Math.Abs(denom) < 1e-12 ? 0 : Math.Clamp(0.5 * (sm - sp) / denom, -1, 1);
        return (axis == 1 ? hit.X : hit.Y) + offset;
    }
}
