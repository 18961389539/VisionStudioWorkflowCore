using ImageViewer.Models;

namespace ImageViewer.Core.Measurements;

/// <summary>Result of a two-dimensional geometric construction or measurement.</summary>
public sealed record GeometryMeasurementResult
{
    private GeometryMeasurementResult(bool isValid, double value, PointD? firstWitness, PointD? secondWitness, string? error)
    {
        IsValid = isValid;
        Value = value;
        FirstWitness = firstWitness;
        SecondWitness = secondWitness;
        Error = error;
    }

    /// <summary>Gets a value indicating whether the operation produced a meaningful result.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the measured scalar, or zero for an invalid result.</summary>
    public double Value { get; }

    /// <summary>Gets the first constructed point or closest-point witness.</summary>
    public PointD? FirstWitness { get; }

    /// <summary>Gets the second constructed point or closest-point witness.</summary>
    public PointD? SecondWitness { get; }

    /// <summary>Gets a human-readable reason when the result is invalid.</summary>
    public string? Error { get; }

    /// <summary>Creates a valid result from a scalar and optional witness points.</summary>
    public static GeometryMeasurementResult Valid(double value, PointD? firstWitness = null, PointD? secondWitness = null) =>
        double.IsFinite(value) && (!firstWitness.HasValue || IsFinite(firstWitness.Value)) && (!secondWitness.HasValue || IsFinite(secondWitness.Value))
            ? new GeometryMeasurementResult(true, value, firstWitness, secondWitness, null)
            : Invalid("The result contains non-finite geometry.");

    /// <summary>Creates an explicit invalid result.</summary>
    public static GeometryMeasurementResult Invalid(string error) =>
        new(false, 0, null, null, string.IsNullOrWhiteSpace(error) ? "The geometry is invalid." : error);

    private static bool IsFinite(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}

/// <summary>Result of fitting a circle to a two-dimensional point distribution.</summary>
public sealed record DistributionCircleFit
{
    /// <summary>Creates a circle fit result.</summary>
    public DistributionCircleFit(bool isValid, PointD center, double radius, double rootMeanSquareResidual, string? error)
    {
        IsValid = isValid;
        Center = center;
        Radius = radius;
        RootMeanSquareResidual = rootMeanSquareResidual;
        Error = error;
    }

    /// <summary>Gets whether the fit is valid.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the fitted center.</summary>
    public PointD Center { get; }

    /// <summary>Gets the fitted radius.</summary>
    public double Radius { get; }

    /// <summary>Gets the root-mean-square radial residual.</summary>
    public double RootMeanSquareResidual { get; }

    /// <summary>Gets the reason for an invalid fit.</summary>
    public string? Error { get; }
}

/// <summary>Framework-neutral exact and least-squares two-dimensional measurement operations.</summary>
public static class GeometryMeasurementTools
{
    private const double Epsilon = 1e-12;

    /// <summary>Returns the midpoint of two finite points.</summary>
    public static GeometryMeasurementResult Midpoint(PointD first, PointD second)
    {
        if (!Finite(first) || !Finite(second)) return GeometryMeasurementResult.Invalid("Both points must be finite.");
        PointD middle = new((first.X / 2) + (second.X / 2), (first.Y / 2) + (second.Y / 2));
        return GeometryMeasurementResult.Valid(0, middle);
    }

    /// <summary>Finds the unique intersection of two non-parallel infinite lines.</summary>
    public static GeometryMeasurementResult IntersectLines(PointD a, PointD b, PointD c, PointD d)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) return GeometryMeasurementResult.Invalid("Line points must be finite.");
        VectorD r = b - a, s = d - c;
        double denominator = Cross(r, s);
        if (Degenerate(r) || Degenerate(s)) return GeometryMeasurementResult.Invalid("A line is degenerate.");
        if (NearZero(denominator, r.Length * s.Length)) return GeometryMeasurementResult.Invalid("Lines are parallel or coincident.");
        double t = Cross(c - a, s) / denominator;
        PointD intersection = a + Scale(r, t);
        return Finite(intersection) ? GeometryMeasurementResult.Valid(0, intersection) : GeometryMeasurementResult.Invalid("The intersection is not finite.");
    }

    /// <summary>Projects a finite point onto an infinite line.</summary>
    public static GeometryMeasurementResult ProjectPointToLine(PointD point, PointD lineStart, PointD lineEnd)
    {
        if (!Finite(point) || !Finite(lineStart) || !Finite(lineEnd)) return GeometryMeasurementResult.Invalid("All points must be finite.");
        VectorD direction = lineEnd - lineStart;
        double lengthSquared = Dot(direction, direction);
        if (!double.IsFinite(lengthSquared) || lengthSquared <= Epsilon * Epsilon) return GeometryMeasurementResult.Invalid("The line is degenerate.");
        PointD projection = lineStart + Scale(direction, Dot(point - lineStart, direction) / lengthSquared);
        return Finite(projection) ? GeometryMeasurementResult.Valid(point.DistanceTo(projection), projection, point) : GeometryMeasurementResult.Invalid("The projection is not finite.");
    }

    /// <summary>Projects a finite point onto a finite segment, clamping to its endpoints.</summary>
    public static GeometryMeasurementResult ProjectPointToSegment(PointD point, PointD segmentStart, PointD segmentEnd)
    {
        if (!Finite(point) || !Finite(segmentStart) || !Finite(segmentEnd)) return GeometryMeasurementResult.Invalid("All points must be finite.");
        VectorD direction = segmentEnd - segmentStart;
        double lengthSquared = Dot(direction, direction);
        if (!double.IsFinite(lengthSquared) || lengthSquared <= Epsilon * Epsilon) return GeometryMeasurementResult.Invalid("The segment is degenerate.");
        double t = Math.Clamp(Dot(point - segmentStart, direction) / lengthSquared, 0, 1);
        PointD projection = segmentStart + Scale(direction, t);
        return Finite(projection) ? GeometryMeasurementResult.Valid(point.DistanceTo(projection), projection, point) : GeometryMeasurementResult.Invalid("The projection is not finite.");
    }

    /// <summary>Constructs the internal angle bisector ray from a vertex and two points on its rays.</summary>
    public static GeometryMeasurementResult AngleBisector(PointD vertex, PointD firstRayPoint, PointD secondRayPoint)
    {
        if (!Finite(vertex) || !Finite(firstRayPoint) || !Finite(secondRayPoint)) return GeometryMeasurementResult.Invalid("All points must be finite.");
        VectorD u = firstRayPoint - vertex, v = secondRayPoint - vertex;
        double lu = u.Length, lv = v.Length;
        if (!double.IsFinite(lu) || !double.IsFinite(lv) || lu <= Epsilon || lv <= Epsilon) return GeometryMeasurementResult.Invalid("An angle ray is degenerate.");
        u = Scale(u, 1 / lu); v = Scale(v, 1 / lv);
        VectorD bisector = new(u.X + v.X, u.Y + v.Y);
        double length = bisector.Length;
        if (!double.IsFinite(length) || length <= Epsilon) return GeometryMeasurementResult.Invalid("Opposite rays have no unique internal angle bisector.");
        PointD rayPoint = Translate(vertex, Scale(bisector, 1 / length));
        double angle = Math.Acos(Math.Clamp(Dot(u, v), -1, 1));
        return GeometryMeasurementResult.Valid(angle, vertex, rayPoint);
    }

    /// <summary>Constructs a segment on the midline between two parallel finite segments.</summary>
    public static GeometryMeasurementResult ParallelSegmentCenterline(PointD a, PointD b, PointD c, PointD d)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) return GeometryMeasurementResult.Invalid("Segment points must be finite.");
        VectorD u = b - a, v = d - c;
        double lu = u.Length, lv = v.Length;
        if (!double.IsFinite(lu) || !double.IsFinite(lv) || lu <= Epsilon || lv <= Epsilon) return GeometryMeasurementResult.Invalid("A segment is degenerate.");
        if (Math.Abs(Cross(u, v)) > Epsilon * lu * lv) return GeometryMeasurementResult.Invalid("Segments are not parallel.");
        if (Dot(u, v) < 0) (c, d) = (d, c);
        PointD first = new(a.X / 2 + c.X / 2, a.Y / 2 + c.Y / 2);
        PointD second = new(b.X / 2 + d.X / 2, b.Y / 2 + d.Y / 2);
        return GeometryMeasurementResult.Valid(first.DistanceTo(second), first, second);
    }

    /// <summary>Finds the shortest distance between two non-degenerate finite segments and returns closest points.</summary>
    public static GeometryMeasurementResult SegmentSegmentDistance(PointD a, PointD b, PointD c, PointD d)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) return GeometryMeasurementResult.Invalid("Segment points must be finite.");
        if (Degenerate(b - a) || Degenerate(d - c)) return GeometryMeasurementResult.Invalid("A segment is degenerate.");
        var intersection = SegmentIntersection(a, b, c, d);
        if (intersection.HasValue) return GeometryMeasurementResult.Valid(0, intersection.Value, intersection.Value);
        PointD cToA = ClosestPointOnSegment(c, a, b);
        PointD dToA = ClosestPointOnSegment(d, a, b);
        PointD aToD = ClosestPointOnSegment(a, c, d);
        PointD bToD = ClosestPointOnSegment(b, c, d);
        (PointD First, PointD Second)[] candidates =
        [
            (a, aToD), (b, bToD), (cToA, c), (dToA, d)
        ];
        var best = candidates.MinBy(pair => pair.First.DistanceTo(pair.Second));
        return GeometryMeasurementResult.Valid(best.First.DistanceTo(best.Second), best.First, best.Second);
    }

    /// <summary>Finds the shortest distance between a finite segment and a circle boundary.</summary>
    public static GeometryMeasurementResult SegmentCircleBoundaryDistance(PointD a, PointD b, PointD center, double radius)
    {
        if (!Finite(a) || !Finite(b) || !Finite(center) || !double.IsFinite(radius) || radius <= 0) return GeometryMeasurementResult.Invalid("The segment and positive-radius circle must be finite.");
        if (Degenerate(b - a)) return GeometryMeasurementResult.Invalid("The segment is degenerate.");
        List<PointD> intersections = SegmentCircleIntersections(a, b, center, radius);
        if (intersections.Count > 0)
        {
            PointD hit = intersections[0];
            return GeometryMeasurementResult.Valid(0, hit, hit);
        }
        List<(PointD Segment, PointD Circle)> candidates = [];
        AddPointCircleCandidate(a, center, radius, candidates);
        AddPointCircleCandidate(b, center, radius, candidates);
        PointD segmentProjection = ClosestPointOnSegment(center, a, b);
        AddPointCircleCandidate(segmentProjection, center, radius, candidates);
        double segmentDistance = center.DistanceTo(segmentProjection);
        if (segmentDistance > Epsilon)
        {
            VectorD fromCenter = segmentProjection - center;
            PointD circleProjection = center + Scale(fromCenter, radius / segmentDistance);
            candidates.Add((segmentProjection, circleProjection));
        }
        else
        {
            // A segment through the center has many equally close boundary points; use a stable perpendicular.
            VectorD direction = b - a;
            VectorD normal = new(-direction.Y, direction.X);
            PointD circlePoint = center + Scale(normal, radius / normal.Length);
            candidates.Add((segmentProjection, circlePoint));
        }
        var best = candidates.MinBy(x => x.Segment.DistanceTo(x.Circle));
        return GeometryMeasurementResult.Valid(best.Segment.DistanceTo(best.Circle), best.Segment, best.Circle);
    }

    /// <summary>Finds the shortest distance between two circle boundaries, including intersecting boundaries.</summary>
    public static GeometryMeasurementResult CircleCircleBoundaryDistance(PointD firstCenter, double firstRadius, PointD secondCenter, double secondRadius)
    {
        if (!Finite(firstCenter) || !Finite(secondCenter) || !double.IsFinite(firstRadius) || !double.IsFinite(secondRadius) || firstRadius <= 0 || secondRadius <= 0)
            return GeometryMeasurementResult.Invalid("Circle centers and positive radii must be finite.");
        VectorD delta = secondCenter - firstCenter;
        double distance = delta.Length;
        if (!double.IsFinite(distance)) return GeometryMeasurementResult.Invalid("Circle separation is not finite.");
        VectorD axis = distance <= Epsilon ? new VectorD(1, 0) : Scale(delta, 1 / distance);
        if (distance <= firstRadius + secondRadius && distance + Math.Min(firstRadius, secondRadius) >= Math.Max(firstRadius, secondRadius))
        {
            double along = (distance * distance + firstRadius * firstRadius - secondRadius * secondRadius) / (2 * Math.Max(distance, Epsilon));
            double h2 = Math.Max(0, firstRadius * firstRadius - along * along);
            VectorD perpendicular = new(-axis.Y, axis.X);
             PointD intersection = Translate(Translate(firstCenter, Scale(axis, along)), Scale(perpendicular, Math.Sqrt(h2)));
            return GeometryMeasurementResult.Valid(0, intersection, intersection);
        }
        PointD first, second;
        if (distance >= firstRadius + secondRadius)
        {
            first = firstCenter + Scale(axis, firstRadius);
            second = Translate(secondCenter, Scale(axis, -secondRadius));
        }
        else if (firstRadius >= secondRadius)
        {
            first = firstCenter + Scale(axis, firstRadius);
            second = secondCenter + Scale(axis, secondRadius);
        }
        else
        {
            first = Translate(firstCenter, Scale(axis, -firstRadius));
            second = Translate(secondCenter, Scale(axis, -secondRadius));
        }
        return GeometryMeasurementResult.Valid(first.DistanceTo(second), first, second);
    }

    /// <summary>Finds the shortest distance between two polylines, skipping repeated adjacent vertices.</summary>
    public static GeometryMeasurementResult PolylinePolylineDistance(IReadOnlyList<PointD> first, IReadOnlyList<PointD> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Count < 2 || second.Count < 2) return GeometryMeasurementResult.Invalid("Each polyline needs at least two vertices.");
        if (first.Any(p => !Finite(p)) || second.Any(p => !Finite(p))) return GeometryMeasurementResult.Invalid("Polyline vertices must be finite.");
        GeometryMeasurementResult? best = null;
        for (int i = 1; i < first.Count; i++)
        {
            if (Degenerate(first[i] - first[i - 1])) continue;
            for (int j = 1; j < second.Count; j++)
            {
                if (Degenerate(second[j] - second[j - 1])) continue;
                GeometryMeasurementResult candidate = SegmentSegmentDistance(first[i - 1], first[i], second[j - 1], second[j]);
                if (candidate.IsValid && (best is null || candidate.Value < best.Value)) best = candidate;
            }
        }
        return best ?? GeometryMeasurementResult.Invalid("Each polyline must contain a non-degenerate segment.");
    }

    /// <summary>Measures the signed center displacement projected onto a specified direction.</summary>
    public static GeometryMeasurementResult ProjectedCenterDistance(PointD firstCenter, PointD secondCenter, VectorD direction)
    {
        if (!Finite(firstCenter) || !Finite(secondCenter) || !double.IsFinite(direction.X) || !double.IsFinite(direction.Y)) return GeometryMeasurementResult.Invalid("Centers and direction must be finite.");
        double length = direction.Length;
        if (!double.IsFinite(length) || length <= Epsilon) return GeometryMeasurementResult.Invalid("The projection direction is degenerate.");
        VectorD unit = Scale(direction, 1 / length);
        double projected = Dot(secondCenter - firstCenter, unit);
        return double.IsFinite(projected) ? GeometryMeasurementResult.Valid(projected, firstCenter, secondCenter) : GeometryMeasurementResult.Invalid("Projected distance is not finite.");
    }

    /// <summary>Measures actual-to-nominal position deviation; the equivalent diametrical position is twice the deviation.</summary>
    public static GeometryMeasurementResult PositionDeviation(PointD actual, PointD nominal)
    {
        if (!Finite(actual) || !Finite(nominal)) return GeometryMeasurementResult.Invalid("Both positions must be finite.");
        double distance = actual.DistanceTo(nominal);
        return double.IsFinite(distance) ? GeometryMeasurementResult.Valid(distance, actual, nominal) : GeometryMeasurementResult.Invalid("Position deviation is not finite.");
    }

    /// <summary>Returns twice the positional deviation, the diametrical position value.</summary>
    public static GeometryMeasurementResult DiametricalPosition(PointD actual, PointD nominal)
    {
        GeometryMeasurementResult deviation = PositionDeviation(actual, nominal);
        return deviation.IsValid ? GeometryMeasurementResult.Valid(2 * deviation.Value, deviation.FirstWitness, deviation.SecondWitness) : deviation;
    }

    /// <summary>Returns distances between adjacent array points in input order.</summary>
    public static IReadOnlyList<GeometryMeasurementResult> AdjacentPitch(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) return Array.Empty<GeometryMeasurementResult>();
        var values = new GeometryMeasurementResult[points.Count - 1];
        for (int i = 1; i < points.Count; i++)
            values[i - 1] = !Finite(points[i - 1]) || !Finite(points[i])
                ? GeometryMeasurementResult.Invalid("Array points must be finite.")
                : GeometryMeasurementResult.Valid(points[i - 1].DistanceTo(points[i]), points[i - 1], points[i]);
        return Array.AsReadOnly(values);
    }

    /// <summary>Fits a circle by algebraic least squares and reports radial root-mean-square residual.</summary>
    public static DistributionCircleFit FitDistributionCircle(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3 || points.Any(p => !Finite(p))) return new(false, default, 0, 0, "At least three finite points are required.");
        // Solve x²+y² + A*x + B*y + C = 0 using centered normal equations.
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        double xx = 0, yy = 0, xy = 0, xq = 0, yq = 0;
        foreach (PointD p in points)
        {
            double x = p.X - mx, y = p.Y - my, q = x * x + y * y;
            xx += x * x; yy += y * y; xy += x * y; xq += x * q; yq += y * q;
        }
        double determinant = xx * yy - xy * xy;
        if (!double.IsFinite(determinant) || determinant <= Epsilon * xx * yy)
            return new(false, default, 0, 0, "Points are collinear or insufficiently spread to define a circle.");
        double a = (xy * yq - yy * xq) / determinant;
        double b = (xy * xq - xx * yq) / determinant;
        double centerX = mx - a / 2, centerY = my - b / 2;
        // Since coordinates were centered, C is the negative mean squared radius adjustment.
        double c = points.Average(p => { double x = p.X - mx, y = p.Y - my; return -(x * x + y * y + a * x + b * y); });
        double radiusSquared = (a * a + b * b) / 4 - c;
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) || !double.IsFinite(radiusSquared) || radiusSquared <= 0)
            return new(false, default, 0, 0, "Fitted circle is degenerate.");
        PointD center = new(centerX, centerY);
        double radius = Math.Sqrt(radiusSquared);
        double rms = Math.Sqrt(points.Average(p => Math.Pow(p.DistanceTo(center) - radius, 2)));
        return double.IsFinite(rms) ? new(true, center, radius, rms, null) : new(false, default, 0, 0, "Circle fit is not finite.");
    }

    private static List<PointD> SegmentCircleIntersections(PointD a, PointD b, PointD center, double radius)
    {
        VectorD d = b - a, f = a - center;
        double aa = Dot(d, d), bb = 2 * Dot(f, d), cc = Dot(f, f) - radius * radius;
        double discriminant = bb * bb - 4 * aa * cc;
        List<PointD> points = [];
        if (discriminant < -Epsilon) return points;
        double root = Math.Sqrt(Math.Max(0, discriminant));
        foreach (double t in new[] { (-bb - root) / (2 * aa), (-bb + root) / (2 * aa) })
            if (t >= 0 && t <= 1 && !points.Any(p => p.DistanceTo(a + Scale(d, t)) <= Epsilon)) points.Add(a + Scale(d, t));
        return points;
    }

    private static void AddPointCircleCandidate(PointD point, PointD center, double radius, List<(PointD Segment, PointD Circle)> candidates)
    {
        VectorD radial = point - center;
        double distance = radial.Length;
        PointD circle = distance <= Epsilon ? new PointD(center.X + radius, center.Y) : center + Scale(radial, radius / distance);
        candidates.Add((point, circle));
    }

    private static PointD ClosestPointOnSegment(PointD point, PointD a, PointD b)
    {
        VectorD direction = b - a;
        double t = Math.Clamp(Dot(point - a, direction) / Dot(direction, direction), 0, 1);
        return a + Scale(direction, t);
    }

    private static PointD? SegmentIntersection(PointD a, PointD b, PointD c, PointD d)
    {
        VectorD r = b - a, s = d - c;
        double denominator = Cross(r, s);
        if (NearZero(denominator, r.Length * s.Length))
        {
            if (!NearZero(Cross(c - a, r), r.Length * (c - a).Length)) return null;
            foreach (PointD p in new[] { a, b, c, d })
                if (OnSegment(p, a, b) && OnSegment(p, c, d)) return p;
            return null;
        }
        double t = Cross(c - a, s) / denominator, u = Cross(c - a, r) / denominator;
        return t >= -Epsilon && t <= 1 + Epsilon && u >= -Epsilon && u <= 1 + Epsilon ? a + Scale(r, Math.Clamp(t, 0, 1)) : null;
    }

    private static bool OnSegment(PointD p, PointD a, PointD b) =>
        p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon &&
        p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;

    private static bool Finite(PointD p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static bool Degenerate(VectorD v) => !double.IsFinite(v.X) || !double.IsFinite(v.Y) || v.Length <= Epsilon;
    private static bool NearZero(double value, double scale) => Math.Abs(value) <= Epsilon * scale;
    private static double Dot(VectorD a, VectorD b) => a.X * b.X + a.Y * b.Y;
    private static double Cross(VectorD a, VectorD b) => a.X * b.Y - a.Y * b.X;
    private static VectorD Scale(VectorD v, double scale) => new(v.X * scale, v.Y * scale);
    private static PointD Translate(PointD point, VectorD vector) => new(point.X + vector.X, point.Y + vector.Y);
}
