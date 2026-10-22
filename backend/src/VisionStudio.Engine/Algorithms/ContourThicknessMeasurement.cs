using JLVisionLib;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Algorithms;

internal static class ContourThicknessMeasurement
{
    private const double Epsilon = 1e-7;
    private const long MaximumWork = 50_000_000;
    internal readonly record struct Query(VisionPoint2D Point, VisionPoint2D Normal, double Arc, double Weight);
    internal sealed record Sample(string Boundary, int Index, double ArcLength, double Weight,
        VisionPoint2D Inner, VisionPoint2D Outer, double Thickness);
    internal sealed record Result(string Reason, bool Contained, IReadOnlyList<Sample> Samples,
        int RequestedSamples, int InvalidSamples, double Minimum, VisionLine2D? MinimumLine);

    public static Result Measure(VisionPoint2D[] inner, VisionPoint2D[] outer, string mode, double step,
        int budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((long)inner.Length * outer.Length > MaximumWork)
            return Missing("geometry-work-budget-exceeded");
        using var innerCurve = ToleranceGeometry.Curve(inner, true);
        using var outerCurve = ToleranceGeometry.Curve(outer, true);
        using var innerSelf = innerCurve.TestSelfIntersectionXld("true");
        using var outerSelf = outerCurve.TestSelfIntersectionXld("true");
        if (innerSelf[0].I != 0 || outerSelf[0].I != 0) return Missing("self-intersecting-contour");
        var innerArea = SignedArea(inner);
        var outerArea = SignedArea(outer);
        if (Math.Abs(innerArea) <= Epsilon || Math.Abs(outerArea) <= Epsilon) return Missing("degenerate-contour");
        cancellationToken.ThrowIfCancellationRequested();
        using var intersectionMode = new JlTuple("all");
        JlOperatorSet.IntersectionContoursXld(innerCurve, outerCurve, intersectionMode, out var rows, out var columns, out var overlapping);
        using (rows) using (columns) using (overlapping)
            if (rows.Length > 0 || overlapping.ToIArr().Any(value => value != 0)) return Missing("contours-intersect-or-touch");
        var minimum = innerCurve.DistanceCcMinPoints(outerCurve, "point_to_segment", out double r1, out double c1, out double r2, out double c2);
        if (!double.IsFinite(minimum)) throw new InvalidOperationException("Contour thickness produced a non-finite minimum distance.");
        if (minimum <= Epsilon) return Missing("contours-intersect-or-touch");
        if (outerCurve.TestXldPoint(inner[0].Y, inner[0].X) == 0) return Missing("inner-contour-not-contained");

        var innerCount = mode == "innernormal" ? RequiredSamples(inner, step, budget) : 0;
        var outerCount = mode == "shortest" ? RequiredSamples(outer, step, budget) : 0;
        if (innerCount > budget || outerCount > budget)
            return new("sample-budget-exceeded", true, [], innerCount + outerCount, 0, 0, null);
        var work = (long)innerCount * outer.Length + (long)outerCount * inner.Length;
        if (mode == "shortest") work += (long)(innerCount + outerCount) * (inner.Length + outer.Length);
        if (mode == "innernormal") work += (long)innerCount * inner.Length;
        if (work > MaximumWork) return new("geometry-work-budget-exceeded", true, [], innerCount + outerCount, 0, 0, null);
        var innerQuery = mode == "innernormal" ? SampleBoundary(inner, step, innerArea, cancellationToken) : [];
        var samples = new List<Sample>(innerCount + outerCount);
        var invalid = 0;
        var visibilityBudget = MaximumWork - work;
        if (mode == "shortest")
        {
            // Sample from the outer/material boundary towards the hole. The exact
            // global minimum still uses every segment of both original contours.
            AddShortest(SampleBoundary(outer, step, outerArea, cancellationToken), inner, innerCurve, "outer");
            if (visibilityBudget < 0) return new("geometry-work-budget-exceeded", true, [], innerCount + outerCount, 0, 0, null);
            if (invalid > 0) return new("shortest-pairing-failed", true, samples, innerCount + outerCount, invalid, 0, null);
        }
        else
        {
            for (var i = 0; i < innerQuery.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = innerQuery[i];
                var outerHit = FirstRayHit(query.Point, query.Normal, outer, cancellationToken);
                var innerHit = FirstRayHit(query.Point, query.Normal, inner, cancellationToken);
                if (!double.IsFinite(outerHit) || outerHit <= Epsilon || innerHit < outerHit - Epsilon)
                { invalid++; continue; }
                var endpoint = new VisionPoint2D(query.Point.X + query.Normal.X * outerHit, query.Point.Y + query.Normal.Y * outerHit);
                samples.Add(new("inner", i, query.Arc, query.Weight, query.Point, endpoint, outerHit));
            }
            if (invalid > 0) return new("normal-pairing-failed", true, samples, innerCount, invalid, 0, null);
            var smallest = samples.MinBy(sample => sample.Thickness)!;
            minimum = smallest.Thickness;
            c1 = smallest.Inner.X; r1 = smallest.Inner.Y; c2 = smallest.Outer.X; r2 = smallest.Outer.Y;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new("ok", true, samples, innerCount + outerCount, invalid, minimum, new(new(c1, r1), new(c2, r2)));

        void AddShortest(Query[] queries, VisionPoint2D[] target, JlXLDCont targetCurve, string boundary)
        {
            using var queryCurve = ToleranceGeometry.Curve(queries.Select(query => query.Point).ToArray(), false);
            using var measured = queryCurve.DistanceContoursXld(targetCurve, "point_to_segment");
            using var distances = measured.GetContourAttribXld("distance");
            if (distances.Length != queries.Length) throw new InvalidOperationException("Contour thickness distance sample count mismatch.");
            for (var i = 0; i < queries.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = queries[i];
                var closest = ClosestPoint(query.Point, target, cancellationToken);
                var distance = distances[i].D;
                if (!double.IsFinite(distance) || distance < 0) throw new InvalidOperationException("Contour thickness distance must be finite and non-negative.");
                // Euclidean proximity alone can cross a concave hole or leave the
                // outer shell. Such a connection is not a reliable wall section.
                if (!StaysInside(query.Point, closest, outer, ref visibilityBudget, cancellationToken))
                { invalid++; if (visibilityBudget < 0) return; continue; }
                if (visibilityBudget < 0) return;
                samples.Add(new(boundary, i, query.Arc, query.Weight,
                    boundary == "inner" ? query.Point : closest, boundary == "inner" ? closest : query.Point, distance));
            }
        }
    }

    private static Result Missing(string reason) => new(reason, false, [], 0, 0, 0, null);

    private static int RequiredSamples(VisionPoint2D[] points, double step, int budget)
    {
        var count = 0d;
        for (var i = 0; i < points.Length; i++)
        {
            var next = points[(i + 1) % points.Length];
            count += Math.Max(1, Math.Ceiling(MeasurementMath.Hypot(next.X - points[i].X, next.Y - points[i].Y) / step));
            if (count > budget) return budget + 1;
        }
        return (int)count;
    }

    private static Query[] SampleBoundary(VisionPoint2D[] points, double step, double area, CancellationToken cancellationToken)
    {
        var queries = new List<Query>();
        var arc = 0d;
        for (var i = 0; i < points.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = points[i]; var next = points[(i + 1) % points.Length];
            var previous = points[(i + points.Length - 1) % points.Length];
            var dx = next.X - point.X; var dy = next.Y - point.Y;
            var length = MeasurementMath.Hypot(dx, dy);
            var previousLength = MeasurementMath.Hypot(point.X - previous.X, point.Y - previous.Y);
            var sign = Math.Sign(area);
            var normal = new VisionPoint2D(sign * dy / length, -sign * dx / length);
            var bisector = new VisionPoint2D(normal.X + sign * (point.Y - previous.Y) / previousLength,
                normal.Y - sign * (point.X - previous.X) / previousLength);
            var magnitude = MeasurementMath.Hypot(bisector.X, bisector.Y);
            var vertexNormal = magnitude > 1e-12 ? new VisionPoint2D(bisector.X / magnitude, bisector.Y / magnitude) : new(0, 0);
            var parts = Math.Max(1, (int)Math.Ceiling(length / step));
            for (var part = 0; part < parts; part++)
            {
                if ((part & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var fraction = part / (double)parts;
                queries.Add(new(new(point.X + dx * fraction, point.Y + dy * fraction), part == 0 ? vertexNormal : normal,
                    arc + length * fraction, length / parts));
            }
            arc += length;
        }
        return queries.ToArray();
    }

    private static double SignedArea(VisionPoint2D[] points)
    {
        var origin = points[0]; var area = 0d;
        for (var i = 1; i + 1 < points.Length; i++)
            area += (points[i].X - origin.X) * (points[i + 1].Y - origin.Y) - (points[i].Y - origin.Y) * (points[i + 1].X - origin.X);
        return area / 2;
    }

    private static VisionPoint2D ClosestPoint(VisionPoint2D point, VisionPoint2D[] target, CancellationToken cancellationToken)
    {
        var closest = target[0]; var best = double.PositiveInfinity;
        for (var i = 0; i < target.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var a = target[i]; var b = target[(i + 1) % target.Length];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var fraction = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            var candidate = new VisionPoint2D(a.X + dx * fraction, a.Y + dy * fraction);
            var distance = MeasurementMath.Hypot(candidate.X - point.X, candidate.Y - point.Y);
            if (distance < best) { best = distance; closest = candidate; }
        }
        return closest;
    }

    private static double FirstRayHit(VisionPoint2D point, VisionPoint2D normal, VisionPoint2D[] target, CancellationToken cancellationToken)
    {
        var first = double.PositiveInfinity;
        for (var i = 0; i < target.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var a = target[i]; var b = target[(i + 1) % target.Length];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var denominator = normal.X * dy - normal.Y * dx;
            if (Math.Abs(denominator) < 1e-12) continue;
            var wx = a.X - point.X; var wy = a.Y - point.Y;
            var distance = (wx * dy - wy * dx) / denominator;
            var fraction = (wx * normal.Y - wy * normal.X) / denominator;
            if (distance > Epsilon && fraction >= -1e-10 && fraction <= 1 + 1e-10) first = Math.Min(first, distance);
        }
        return first;
    }

    private static bool StaysInside(VisionPoint2D start, VisionPoint2D end, VisionPoint2D[] boundary,
        ref long remainingWork, CancellationToken cancellationToken)
    {
        var dx = end.X - start.X; var dy = end.Y - start.Y;
        var cuts = new List<double> { 0, 1 };
        for (var i = 0; i < boundary.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var a = boundary[i]; var b = boundary[(i + 1) % boundary.Length];
            var sx = b.X - a.X; var sy = b.Y - a.Y;
            var denominator = dx * sy - dy * sx;
            if (Math.Abs(denominator) < 1e-12) continue;
            var wx = a.X - start.X; var wy = a.Y - start.Y;
            var t = (wx * sy - wy * sx) / denominator;
            var u = (wx * dy - wy * dx) / denominator;
            if (t > 0 && t < 1 && u >= -1e-10 && u <= 1 + 1e-10) cuts.Add(t);
        }
        cuts.Sort();
        for (var i = 1; i < cuts.Count; i++)
        {
            if (cuts[i] - cuts[i - 1] <= 1e-10) continue;
            remainingWork -= boundary.Length;
            if (remainingWork < 0) return false;
            var fraction = (cuts[i] + cuts[i - 1]) / 2;
            var point = new VisionPoint2D(start.X + dx * fraction, start.Y + dy * fraction);
            var inside = false; var onBoundary = false;
            for (var j = 0; j < boundary.Length; j++)
            {
                if ((j & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var a = boundary[j]; var b = boundary[(j + 1) % boundary.Length];
                var sx = b.X - a.X; var sy = b.Y - a.Y;
                if (Math.Abs((point.X - a.X) * sy - (point.Y - a.Y) * sx) <= Epsilon * MeasurementMath.Hypot(sx, sy)
                    && point.X >= Math.Min(a.X, b.X) - Epsilon && point.X <= Math.Max(a.X, b.X) + Epsilon
                    && point.Y >= Math.Min(a.Y, b.Y) - Epsilon && point.Y <= Math.Max(a.Y, b.Y) + Epsilon)
                { onBoundary = true; break; }
                if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < a.X + (point.Y - a.Y) * sx / sy) inside = !inside;
            }
            // A grazing contact or a pixel staircase along the boundary is not an exit.
            if (!inside && !onBoundary) return false;
        }
        return true;
    }
}
