namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Pure-managed edge/chamfer shape matching (HALCON find_shape_model / Cognex PatMax
/// direction — NOT parity). Model contour points may carry gradient normals for polarity.
/// Score = 0.7 * coverage + 0.3 * exp(-meanCoveredDistance / tolerance) * coverage, then
/// optional clutter multiply (1 - clutterScore).
/// Depth vs PatMax/HALCON: continuous parabolic angle+scale refine after discrete search,
/// gradient-direction polarity, unexplained-edge clutter, optional XLD-ish contour snap along
/// normals, and multi-level pyramid coarse-to-fine. Still short of PatMax feature vectors /
/// trained clutter models / full HALCON XLD least-squares deformation.
/// </summary>
public static class ChamferShapeMatching
{
    /// <summary>Origin-relative contour sample. Nx/Ny are template-space unit gradient (brighter side); zero = unoriented.</summary>
    public readonly record struct ModelPoint(double X, double Y, double Nx = 0, double Ny = 0)
    {
        public bool HasNormal => Nx * Nx + Ny * Ny > 1e-12;
    }

    public sealed record Options(
        double AngleMinDeg = -180, double AngleMaxDeg = 180, double AngleStepDeg = 5,
        double ScaleMin = 1, double ScaleMax = 1, double ScaleStep = 0.1,
        double MinScore = 0.6, int MaxTargets = 5, double MinCenterDistance = 20,
        double TolerancePx = 2.5, int MaxModelPoints = 240, int CoarseStride = 4,
        bool IgnorePolarity = true,
        double PolarityCosine = 0.0,
        double MinGradient = 0,
        bool ClutterEnabled = false,
        double MaxClutterScore = 1,
        bool ScoreUsingClutter = false,
        double ClutterTolerancePx = 0,
        int ClutterRoiX = 0, int ClutterRoiY = 0, int ClutterRoiWidth = 0, int ClutterRoiHeight = 0,
        int PyramidLevels = 1,
        bool ContinuousPoseRefine = true,
        bool XldContourSnap = true);

    public sealed record Hit(
        double X, double Y, double AngleDeg, double Scale, double Score,
        double Coverage, double MeanDistancePx, int ModelPoints,
        double PolarityMatchFraction = 1,
        double ClutterScore = 0,
        double CoverageScore = 0,
        double DistanceScore = 0,
        double NativeScore = 0,
        double AngleRefineDeltaDeg = 0,
        double ScaleRefineDelta = 0,
        double XldSnapDeltaPx = 0,
        int PyramidLevelUsed = 1);

    public static ModelPoint[] BuildModel(IReadOnlyList<ModelPoint> points, int maxPoints)
    {
        if (points.Count == 0) return [];
        if (maxPoints <= 0 || points.Count <= maxPoints) return points.ToArray();
        var stride = (double)points.Count / maxPoints;
        var result = new ModelPoint[maxPoints];
        for (var i = 0; i < maxPoints; i++) result[i] = points[(int)(i * stride)];
        return result;
    }

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

    public static (double Score, double Coverage, double MeanDistance, double PolarityMatchFraction, double CoverageScore, double DistanceScore) ScoreAt(
        float[,] dist, IReadOnlyList<ModelPoint> model, double cx, double cy, double angleDeg, double scale, double tolerancePx,
        bool ignorePolarity = true, float[,]? gradX = null, float[,]? gradY = null, double polarityCosine = 0, double minGradient = 0)
    {
        var h = dist.GetLength(0); var w = dist.GetLength(1);
        var rad = angleDeg * Math.PI / 180; var cos = Math.Cos(rad); var sin = Math.Sin(rad);
        var covered = 0; var coveredDist = 0d; var polarityOk = 0; var polarityChecked = 0;
        var usePolarity = !ignorePolarity && gradX is not null && gradY is not null;
        foreach (var p in model)
        {
            var x = cx + (cos * p.X + sin * p.Y) * scale;
            var y = cy + (-sin * p.X + cos * p.Y) * scale;
            var xi = (int)Math.Round(x); var yi = (int)Math.Round(y);
            if (xi < 0 || yi < 0 || xi >= w || yi >= h) continue;
            var d = dist[yi, xi];
            if (d > tolerancePx) continue;

            if (usePolarity && p.HasNormal)
            {
                polarityChecked++;
                // Rotate normal with the same image-space convention as points.
                var nx = (cos * p.Nx + sin * p.Ny);
                var ny = (-sin * p.Nx + cos * p.Ny);
                var gx = gradX![yi, xi]; var gy = gradY![yi, xi];
                var mag = Math.Sqrt(gx * gx + gy * gy);
                if (mag < Math.Max(1e-6, minGradient)) continue;
                var dot = (nx * gx + ny * gy) / mag;
                if (dot < polarityCosine) continue;
                polarityOk++;
            }

            covered++;
            coveredDist += d;
        }
        if (model.Count == 0) return (0, 0, double.PositiveInfinity, 1, 0, 0);
        var coverage = (double)covered / model.Count;
        var mean = covered > 0 ? coveredDist / covered : tolerancePx * 4;
        var coverageScore = coverage;
        var distanceScore = Math.Exp(-mean / Math.Max(0.1, tolerancePx)) * coverage;
        var score = 0.7 * coverageScore + 0.3 * distanceScore;
        var polarityFrac = polarityChecked == 0 ? 1.0 : (double)polarityOk / polarityChecked;
        return (score, coverage, mean, polarityFrac, coverageScore, distanceScore);
    }

    /// <summary>Fraction of scene edge pixels inside ROI that are farther than clutterTolerance from any projected model point.</summary>
    public static double MeasureClutter(
        bool[,] sceneEdges, IReadOnlyList<ModelPoint> model, double cx, double cy, double angleDeg, double scale,
        double clutterTolerancePx, int roiX, int roiY, int roiW, int roiH)
    {
        var h = sceneEdges.GetLength(0); var w = sceneEdges.GetLength(1);
        var x0 = Math.Clamp(roiX, 0, w); var y0 = Math.Clamp(roiY, 0, h);
        var x1 = Math.Clamp(roiX + Math.Max(0, roiW), 0, w); var y1 = Math.Clamp(roiY + Math.Max(0, roiH), 0, h);
        if (x1 <= x0 || y1 <= y0) { x0 = 0; y0 = 0; x1 = w; y1 = h; }

        var rad = angleDeg * Math.PI / 180; var cos = Math.Cos(rad); var sin = Math.Sin(rad);
        var projected = new List<(double X, double Y)>(model.Count);
        foreach (var p in model)
            projected.Add((cx + (cos * p.X + sin * p.Y) * scale, cy + (-sin * p.X + cos * p.Y) * scale));

        var tol2 = clutterTolerancePx * clutterTolerancePx;
        var edgeCount = 0; var unexplained = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            if (!sceneEdges[y, x]) continue;
            edgeCount++;
            var explained = false;
            foreach (var (px, py) in projected)
            {
                var dx = x - px; var dy = y - py;
                if (dx * dx + dy * dy <= tol2) { explained = true; break; }
            }
            if (!explained) unexplained++;
        }
        return edgeCount == 0 ? 0 : (double)unexplained / edgeCount;
    }

    public static IReadOnlyList<Hit> Search(
        float[,] dist, IReadOnlyList<ModelPoint> model, Options o,
        bool[,]? sceneEdges = null, float[,]? gradX = null, float[,]? gradY = null)
    {
        if (model.Count < 8) throw new InvalidOperationException("Chamfer shape model needs at least 8 contour points.");
        var h = dist.GetLength(0); var w = dist.GetLength(1);
        var levels = Math.Clamp(o.PyramidLevels <= 0 ? 1 : o.PyramidLevels, 1, 4);
        var coarse = BuildModel(model, Math.Min(80, o.MaxModelPoints));
        // Pyramid refines seeds only — never coarsens the initial search grid (keeps CoarseStride).
        var stride = Math.Max(1, o.CoarseStride);
        var clutterTol = o.ClutterTolerancePx > 0 ? o.ClutterTolerancePx : o.TolerancePx * 1.5;
        var candidates = new List<Hit>();
        for (var scale = o.ScaleMin; scale <= o.ScaleMax + 1e-9; scale += o.ScaleStep <= 0 ? 1 : o.ScaleStep)
        for (var angle = o.AngleMinDeg; angle <= o.AngleMaxDeg + 1e-9; angle += o.AngleStepDeg <= 0 ? 5 : o.AngleStepDeg)
        {
            for (var y = stride / 2; y < h; y += stride)
            for (var x = stride / 2; x < w; x += stride)
            {
                var scored = ScoreAt(dist, coarse, x, y, angle, scale, o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient);
                if (scored.Score >= o.MinScore * 0.75)
                    candidates.Add(ToHit(x, y, angle, scale, scored, coarse.Length, nativeOnly: true) with { PyramidLevelUsed = levels });
            }
            if (o.ScaleStep <= 0) break;
        }
        for (var level = levels - 1; level >= 1; level--)
        {
            var fineStride = Math.Max(1, o.CoarseStride * Math.Max(1, level));
            var seeds = candidates.OrderByDescending(c => c.Score).Take(24).ToList();
            var next = new List<Hit>();
            foreach (var seed in seeds)
            {
                var best = seed;
                for (var dy = -fineStride; dy <= fineStride; dy += Math.Max(1, fineStride / 2))
                for (var dx = -fineStride; dx <= fineStride; dx += Math.Max(1, fineStride / 2))
                {
                    var scored = ScoreAt(dist, coarse, seed.X + dx, seed.Y + dy, seed.AngleDeg, seed.Scale, o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient);
                    if (scored.Score > best.Score + 1e-9)
                        best = ToHit(seed.X + dx, seed.Y + dy, seed.AngleDeg, seed.Scale, scored, coarse.Length, nativeOnly: true) with { PyramidLevelUsed = level };
                }
                next.Add(best);
            }
            candidates = next;
        }
        var refined = new List<Hit>();
        foreach (var c in candidates.OrderByDescending(c => c.Score).Take(40))
        {
            var best = Refine(dist, model, c, o, gradX, gradY);
            if (o.ContinuousPoseRefine)
                best = RefineContinuousPose(dist, model, best, o, gradX, gradY);
            if (o.XldContourSnap)
                best = SnapContourAlongGradient(dist, model, best, o, gradX, gradY);
            best = ApplyClutter(best, model, o, sceneEdges, clutterTol);
            if (best.Score >= o.MinScore && (!o.ClutterEnabled || best.ClutterScore <= o.MaxClutterScore + 1e-12))
                refined.Add(best with { PyramidLevelUsed = levels });
        }
        var hits = new List<Hit>();
        foreach (var hit in refined.OrderByDescending(r => r.Score))
        {
            if (hits.Any(k => Math.Sqrt((k.X - hit.X) * (k.X - hit.X) + (k.Y - hit.Y) * (k.Y - hit.Y)) < o.MinCenterDistance)) continue;
            var sx = Subpixel(dist, model, hit, 1, o, gradX, gradY);
            var sy = Subpixel(dist, model, hit, 2, o, gradX, gradY);
            hits.Add(hit with { X = sx, Y = sy });
            if (hits.Count >= Math.Max(1, o.MaxTargets)) break;
        }
        return hits;
    }

    /// <summary>Model contour metrics for train diagnostics (point count, oriented fraction, mean radius).</summary>
    public static (int PointCount, int OrientedCount, double MeanRadiusPx, double MeanNormalMagnitude) ModelMetrics(IReadOnlyList<ModelPoint> model)
    {
        if (model.Count == 0) return (0, 0, 0, 0);
        var oriented = 0; var radius = 0d; var mag = 0d;
        foreach (var p in model)
        {
            radius += Math.Sqrt(p.X * p.X + p.Y * p.Y);
            var n = Math.Sqrt(p.Nx * p.Nx + p.Ny * p.Ny);
            mag += n;
            if (p.HasNormal) oriented++;
        }
        return (model.Count, oriented, radius / model.Count, mag / model.Count);
    }

    /// <summary>Parabolic peak refine on angle and uniform scale (continuous, not PatMax / HALCON XLD LS).</summary>
    private static Hit RefineContinuousPose(float[,] dist, IReadOnlyList<ModelPoint> model, Hit seed, Options o, float[,]? gradX, float[,]? gradY)
    {
        var da = Math.Max(0.25, o.AngleStepDeg / 4);
        var ds = Math.Max(0.01, (o.ScaleStep <= 0 ? 0.05 : o.ScaleStep) / 4);
        double Score(double a, double s) => ScoreAt(dist, model, seed.X, seed.Y, a, Math.Max(0.05, s), o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient).Score;
        var a0 = seed.AngleDeg; var s0 = Score(a0, seed.Scale); var am = Score(a0 - da, seed.Scale); var ap = Score(a0 + da, seed.Scale);
        var denomA = am - 2 * s0 + ap;
        var angleOff = Math.Abs(denomA) < 1e-12 ? 0 : Math.Clamp(0.5 * (am - ap) / denomA, -1, 1) * da;
        var sc0 = Score(a0 + angleOff, seed.Scale); var sm = Score(a0 + angleOff, seed.Scale - ds); var sp = Score(a0 + angleOff, seed.Scale + ds);
        var denomS = sm - 2 * sc0 + sp;
        var scaleOff = Math.Abs(denomS) < 1e-12 ? 0 : Math.Clamp(0.5 * (sm - sp) / denomS, -1, 1) * ds;
        var angle = a0 + angleOff; var scale = Math.Max(0.05, seed.Scale + scaleOff);
        var scored = ScoreAt(dist, model, seed.X, seed.Y, angle, scale, o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient);
        if (scored.Score + 1e-9 < seed.Score) return seed;
        return ToHit(seed.X, seed.Y, angle, scale, scored, model.Count, nativeOnly: true) with
        {
            AngleRefineDeltaDeg = angleOff,
            ScaleRefineDelta = scaleOff,
            PyramidLevelUsed = seed.PyramidLevelUsed
        };
    }

    /// <summary>XLD-ish snap: average residual along model normals toward the chamfer zero-set (+/-1 px).</summary>
    private static Hit SnapContourAlongGradient(float[,] dist, IReadOnlyList<ModelPoint> model, Hit seed, Options o, float[,]? gradX, float[,]? gradY)
    {
        var h = dist.GetLength(0); var w = dist.GetLength(1);
        var rad = seed.AngleDeg * Math.PI / 180; var cos = Math.Cos(rad); var sin = Math.Sin(rad);
        var sumDx = 0d; var sumDy = 0d; var used = 0;
        foreach (var p in model)
        {
            var lx = (cos * p.X - sin * p.Y) * seed.Scale;
            var ly = (sin * p.X + cos * p.Y) * seed.Scale;
            var px = seed.X + lx; var py = seed.Y + ly;
            var nx = cos * p.Nx - sin * p.Ny; var ny = sin * p.Nx + cos * p.Ny;
            var nmag = Math.Sqrt(nx * nx + ny * ny);
            if (nmag < 1e-6) { nx = lx; ny = ly; nmag = Math.Sqrt(nx * nx + ny * ny); }
            if (nmag < 1e-6) continue;
            nx /= nmag; ny /= nmag;
            var bestT = 0d; var bestD = SampleDist(dist, px, py, h, w);
            for (var t = -1.0; t <= 1.0 + 1e-9; t += 0.5)
            {
                var d = SampleDist(dist, px + t * nx, py + t * ny, h, w);
                if (d < bestD) { bestD = d; bestT = t; }
            }
            sumDx += bestT * nx; sumDy += bestT * ny; used++;
        }
        if (used < 4) return seed;
        var dx = sumDx / used; var dy = sumDy / used;
        var snapMag = Math.Sqrt(dx * dx + dy * dy);
        if (snapMag < 1e-4) return seed with { XldSnapDeltaPx = 0 };
        var scored = ScoreAt(dist, model, seed.X + dx, seed.Y + dy, seed.AngleDeg, seed.Scale, o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient);
        if (scored.Score + 1e-9 < seed.Score) return seed with { XldSnapDeltaPx = 0 };
        return ToHit(seed.X + dx, seed.Y + dy, seed.AngleDeg, seed.Scale, scored, model.Count, nativeOnly: true) with
        {
            AngleRefineDeltaDeg = seed.AngleRefineDeltaDeg,
            ScaleRefineDelta = seed.ScaleRefineDelta,
            XldSnapDeltaPx = snapMag,
            PyramidLevelUsed = seed.PyramidLevelUsed
        };
    }

    private static float SampleDist(float[,] dist, double x, double y, int h, int w)
    {
        var ix = (int)Math.Round(x); var iy = (int)Math.Round(y);
        if ((uint)ix >= (uint)w || (uint)iy >= (uint)h) return 1e6f;
        return dist[iy, ix];
    }

    private static Hit ApplyClutter(Hit hit, IReadOnlyList<ModelPoint> model, Options o, bool[,]? sceneEdges, double clutterTol)
    {
        if (!o.ClutterEnabled || sceneEdges is null) return hit with { NativeScore = hit.Score };
        var clutter = MeasureClutter(sceneEdges, model, hit.X, hit.Y, hit.AngleDeg, hit.Scale, clutterTol,
            o.ClutterRoiX, o.ClutterRoiY, o.ClutterRoiWidth, o.ClutterRoiHeight);
        var native = hit.Score;
        var final = o.ScoreUsingClutter ? native * (1 - clutter) : native;
        return hit with { Score = final, ClutterScore = clutter, NativeScore = native };
    }

    private static Hit ToHit(double x, double y, double angle, double scale,
        (double Score, double Coverage, double MeanDistance, double PolarityMatchFraction, double CoverageScore, double DistanceScore) s,
        int modelPoints, bool nativeOnly)
        => new(x, y, angle, scale, s.Score, s.Coverage, s.MeanDistance, modelPoints, s.PolarityMatchFraction, 0, s.CoverageScore, s.DistanceScore, s.Score);

    private static Hit Refine(float[,] dist, IReadOnlyList<ModelPoint> model, Hit seed, Options o, float[,]? gradX, float[,]? gradY)
    {
        var best = seed; var stepT = Math.Max(1d, o.CoarseStride / 2d); var stepA = Math.Max(0.5, o.AngleStepDeg / 2); var stepS = Math.Max(0.02, (o.ScaleStep <= 0 ? 0.1 : o.ScaleStep) / 2);
        for (var iter = 0; iter < 60; iter++)
        {
            var improved = false;
            foreach (var (dx, dy, da, ds) in new[] { (stepT, 0d, 0d, 0d), (-stepT, 0d, 0d, 0d), (0d, stepT, 0d, 0d), (0d, -stepT, 0d, 0d), (0d, 0d, stepA, 0d), (0d, 0d, -stepA, 0d), (0d, 0d, 0d, stepS), (0d, 0d, 0d, -stepS) })
            {
                var scored = ScoreAt(dist, model, best.X + dx, best.Y + dy, best.AngleDeg + da, Math.Max(0.05, best.Scale + ds), o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient);
                if (scored.Score > best.Score + 1e-9)
                {
                    best = ToHit(best.X + dx, best.Y + dy, best.AngleDeg + da, Math.Max(0.05, best.Scale + ds), scored, model.Count, nativeOnly: true);
                    improved = true;
                }
            }
            if (!improved) { stepT /= 2; stepA /= 2; stepS /= 2; if (stepT < 0.25) break; }
        }
        return best;
    }

    private static double Subpixel(float[,] dist, IReadOnlyList<ModelPoint> model, Hit hit, int axis, Options o, float[,]? gradX, float[,]? gradY)
    {
        double At(double d) => axis == 1
            ? ScoreAt(dist, model, hit.X + d, hit.Y, hit.AngleDeg, hit.Scale, o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient).Score
            : ScoreAt(dist, model, hit.X, hit.Y + d, hit.AngleDeg, hit.Scale, o.TolerancePx, o.IgnorePolarity, gradX, gradY, o.PolarityCosine, o.MinGradient).Score;
        var s0 = At(0); var sm = At(-1); var sp = At(1); var denom = sm - 2 * s0 + sp;
        var offset = Math.Abs(denom) < 1e-12 ? 0 : Math.Clamp(0.5 * (sm - sp) / denom, -1, 1);
        return (axis == 1 ? hit.X : hit.Y) + offset;
    }
}