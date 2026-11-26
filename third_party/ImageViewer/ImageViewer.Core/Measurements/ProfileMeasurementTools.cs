using ImageViewer.Models;

namespace ImageViewer.Core.Measurements;

/// <summary>Framework-neutral profile measurements for ordered two-dimensional edge points.</summary>
/// <remarks>
/// These calculations describe the fitting and sampling methods used here. They do not claim
/// minimum-zone straightness, certified roundness, or compliance with a particular standard.
/// </remarks>
public static class ProfileMeasurementTools
{
    private const double Epsilon = 1e-12;

    /// <summary>Fits an orthogonal least-squares line and returns peak-to-valley perpendicular residual.</summary>
    /// <param name="points">Finite edge or profile samples in order-independent point coordinates.</param>
    /// <returns>The documented least-squares fit and residual range, or a failure with a diagnostic.</returns>
    public static StraightnessResult EvaluateStraightness(IReadOnlyList<PointD>? points)
    {
        if (!TryValidatePoints(points, 3, out PointD[] p, out string error))
            return StraightnessResult.Failure(error);

        double mx = p.Average(q => q.X), my = p.Average(q => q.Y);
        if (!double.IsFinite(mx) || !double.IsFinite(my))
            return StraightnessResult.Failure("The point centroid is not finite.");
        double xx = 0, xy = 0, yy = 0;
        foreach (PointD q in p)
        {
            double x = q.X - mx, y = q.Y - my;
            xx += x * x; xy += x * y; yy += y * y;
        }
        if (!double.IsFinite(xx) || !double.IsFinite(xy) || !double.IsFinite(yy))
            return StraightnessResult.Failure("The least-squares covariance is not finite.");
        if (xx + yy <= Epsilon)
            return StraightnessResult.Failure("The points do not define a line with non-zero extent.");

        // The smallest-eigenvalue direction of the 2D covariance matrix is the line normal.
        double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
        VectorD normal = new(-Math.Sin(angle), Math.Cos(angle));
        if (normal.X < -Epsilon || (Math.Abs(normal.X) <= Epsilon && normal.Y < 0))
            normal = new VectorD(-normal.X, -normal.Y);
        double[] residuals = p.Select(q => (q.X - mx) * normal.X + (q.Y - my) * normal.Y).ToArray();
        if (residuals.Any(value => !double.IsFinite(value)))
            return StraightnessResult.Failure("Perpendicular residuals are not finite.");
        double min = residuals.Min(), max = residuals.Max();
        return StraightnessResult.Succeeded(new PointD(mx, my), normal, residuals, max - min);
    }

    /// <summary>Fits a circle algebraically by least squares and reports radial residual range.</summary>
    /// <remarks>The algebraic fit and reported residual range are descriptive profile results, not certified roundness.</remarks>
    /// <param name="points">Finite edge or profile samples.</param>
    /// <returns>The fitted center, mean radial radius, and radial residuals, or an explicit failure.</returns>
    public static CircularityResult EvaluateCircularity(IReadOnlyList<PointD>? points)
    {
        if (!TryValidatePoints(points, 3, out PointD[] p, out string error))
            return CircularityResult.Failure(error);

        double mx = p.Average(q => q.X), my = p.Average(q => q.Y);
        double suu = 0, suv = 0, svv = 0, suR = 0, svR = 0;
        foreach (PointD q in p)
        {
            double u = q.X - mx, v = q.Y - my, r2 = u * u + v * v;
            suu += u * u; suv += u * v; svv += v * v;
            suR += u * r2; svR += v * r2;
        }
        double determinant = suu * svv - suv * suv;
        if (!double.IsFinite(determinant) || determinant <= Epsilon * Math.Max(1, (suu + svv) * (suu + svv)))
            return CircularityResult.Failure("The points are collinear or too close to collinear to fit a circle.");

        double d = (-0.5 * suR * svv + 0.5 * svR * suv) / determinant;
        double e = (-0.5 * svR * suu + 0.5 * suR * suv) / determinant;
        PointD center = new(mx - d, my - e);
        double radius = p.Average(q => q.DistanceTo(center));
        if (!double.IsFinite(radius) || radius <= Epsilon || !double.IsFinite(center.X) || !double.IsFinite(center.Y))
            return CircularityResult.Failure("The fitted circle is degenerate or non-finite.");
        double[] residuals = p.Select(q => q.DistanceTo(center) - radius).ToArray();
        return CircularityResult.Succeeded(center, radius, residuals, residuals.Max() - residuals.Min());
    }

    /// <summary>Measures each measured point's nearest distance to a reference polyline.</summary>
    /// <param name="measuredPoints">Finite measured contour points.</param>
    /// <param name="referencePolyline">Ordered finite vertices of the reference polyline.</param>
    /// <param name="signedOffsets">When true, signs are positive on the left side of the directed reference segment.</param>
    /// <returns>Nearest-reference deviations in input point order.</returns>
    public static ContourDeviationResult EvaluateContourDeviation(
        IReadOnlyList<PointD>? measuredPoints, IReadOnlyList<PointD>? referencePolyline, bool signedOffsets = true)
    {
        if (!TryValidatePoints(measuredPoints, 1, out PointD[] measured, out string error))
            return ContourDeviationResult.Failure(error);
        if (!TryValidatePolyline(referencePolyline, out PointD[] reference, out error))
            return ContourDeviationResult.Failure(error);

        var deviations = new List<double>(measured.Length);
        foreach (PointD point in measured)
        {
            double bestSquared = double.PositiveInfinity, bestSigned = 0;
            for (int i = 0; i < reference.Length - 1; i++)
            {
                PointD a = reference[i], b = reference[i + 1];
                double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
                if (lengthSquared <= Epsilon) continue;
                double t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
                double rx = point.X - (a.X + t * dx), ry = point.Y - (a.Y + t * dy);
                double squared = rx * rx + ry * ry;
                if (squared < bestSquared)
                {
                    bestSquared = squared;
                    double cross = dx * (point.Y - a.Y) - dy * (point.X - a.X);
                    bestSigned = Math.CopySign(Math.Sqrt(squared), cross);
                }
            }
            if (!double.IsFinite(bestSquared))
                return ContourDeviationResult.Failure("The reference polyline has no segment with non-zero length.");
            deviations.Add(signedOffsets ? bestSigned : Math.Abs(bestSigned));
        }
        return ContourDeviationResult.Succeeded(deviations);
    }

    /// <summary>Samples the distance between two polylines along normals to the source polyline.</summary>
    /// <param name="sourcePolyline">Ordered source vertices that define sampling stations and normals.</param>
    /// <param name="opposingPolyline">Ordered target vertices intersected by each source normal.</param>
    /// <param name="sampleSpacing">Maximum arclength spacing; endpoints are included and spacing is uniform.</param>
    /// <returns>Gap samples with witness points and statistics, or a failure if any station lacks an intersection.</returns>
    /// <remarks>At each station, the nearest intersection in either normal direction is reported.</remarks>
    public static GapProfileResult MeasureNormalGaps(
        IReadOnlyList<PointD>? sourcePolyline, IReadOnlyList<PointD>? opposingPolyline, double sampleSpacing)
    {
        if (!TryValidatePolyline(sourcePolyline, out PointD[] source, out string error))
            return GapProfileResult.Failure(error);
        if (!TryValidatePolyline(opposingPolyline, out PointD[] opposing, out error))
            return GapProfileResult.Failure(error);
        if (!double.IsFinite(sampleSpacing) || sampleSpacing <= 0)
            return GapProfileResult.Failure("Sample spacing must be a finite positive value.");

        double[] lengths = SegmentLengths(source);
        double total = lengths.Sum();
        if (!double.IsFinite(total) || total <= Epsilon)
            return GapProfileResult.Failure("The source polyline has no measurable length.");
        double requestedIntervals = Math.Ceiling(total / sampleSpacing);
        if (!double.IsFinite(requestedIntervals) || requestedIntervals > 1_000_000)
            return GapProfileResult.Failure("The requested spacing would create too many samples.");
        int intervals = Math.Max(1, (int)requestedIntervals);
        var samples = new List<GapSample>(intervals + 1);
        for (int sampleIndex = 0; sampleIndex <= intervals; sampleIndex++)
        {
            double station = total * sampleIndex / intervals;
            Locate(source, lengths, station, out PointD origin, out VectorD tangent);
            VectorD normal = new(-tangent.Y, tangent.X);
            if (!TryNearestNormalIntersection(origin, normal, opposing, out PointD witness))
                return GapProfileResult.Failure($"No opposing-polyline intersection was found at source station {station:R}.");
            samples.Add(new GapSample(station, origin, witness, origin.DistanceTo(witness)));
        }
        return GapProfileResult.Succeeded(samples);
    }

    /// <summary>Computes consecutive point-to-point pitches from ordered points.</summary>
    /// <param name="orderedPoints">At least two finite points in pitch order.</param>
    /// <returns>Consecutive distances with summary statistics, or an explicit failure.</returns>
    public static PitchResult EvaluatePitch(IReadOnlyList<PointD>? orderedPoints)
    {
        if (!TryValidatePoints(orderedPoints, 2, out PointD[] points, out string error))
            return PitchResult.Failure(error);
        double[] pitches = new double[points.Length - 1];
        for (int i = 0; i < pitches.Length; i++)
        {
            pitches[i] = points[i].DistanceTo(points[i + 1]);
            if (!double.IsFinite(pitches[i]) || pitches[i] <= Epsilon)
                return PitchResult.Failure($"Consecutive points {i} and {i + 1} do not define a positive finite pitch.");
        }
        return PitchResult.Succeeded(pitches);
    }

    private static bool TryValidatePoints(IReadOnlyList<PointD>? input, int minimum, out PointD[] points, out string error)
    {
        points = [];
        if (input is null || input.Count < minimum) { error = $"At least {minimum} points are required."; return false; }
        points = input.ToArray();
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
        { error = "All point coordinates must be finite."; points = []; return false; }
        error = string.Empty; return true;
    }

    private static bool TryValidatePolyline(IReadOnlyList<PointD>? input, out PointD[] points, out string error)
    {
        if (!TryValidatePoints(input, 2, out points, out error)) return false;
        if (SegmentLengths(points).All(length => length <= Epsilon))
        { error = "A polyline must contain at least one segment with non-zero length."; points = []; return false; }
        return true;
    }

    private static double[] SegmentLengths(PointD[] points) => Enumerable.Range(0, points.Length - 1)
        .Select(i => points[i].DistanceTo(points[i + 1])).ToArray();

    private static void Locate(PointD[] points, double[] lengths, double station, out PointD point, out VectorD tangent)
    {
        double traversed = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] <= Epsilon) continue;
            if (station <= traversed + lengths[i] || i == lengths.Length - 1)
            {
                double t = Math.Clamp((station - traversed) / lengths[i], 0, 1);
                VectorD delta = points[i + 1] - points[i];
                point = new PointD(points[i].X + delta.X * t, points[i].Y + delta.Y * t);
                tangent = new VectorD(delta.X / lengths[i], delta.Y / lengths[i]);
                return;
            }
            traversed += lengths[i];
        }
        int last = Array.FindLastIndex(lengths, length => length > Epsilon);
        tangent = new VectorD((points[last + 1].X - points[last].X) / lengths[last], (points[last + 1].Y - points[last].Y) / lengths[last]);
        point = points[^1];
    }

    private static bool TryNearestNormalIntersection(PointD origin, VectorD normal, PointD[] polyline, out PointD witness)
    {
        witness = default;
        double bestAbs = double.PositiveInfinity;
        for (int i = 0; i < polyline.Length - 1; i++)
        {
            PointD a = polyline[i], b = polyline[i + 1];
            VectorD edge = b - a;
            double denominator = Cross(normal, edge);
            if (Math.Abs(denominator) <= Epsilon) continue;
            VectorD offset = a - origin;
            double t = Cross(offset, edge) / denominator;
            double u = Cross(offset, normal) / denominator;
            if (u < -Epsilon || u > 1 + Epsilon || Math.Abs(t) >= bestAbs) continue;
            PointD candidate = origin + new VectorD(normal.X * t, normal.Y * t);
            if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y)) continue;
            bestAbs = Math.Abs(t); witness = candidate;
        }
        return double.IsFinite(bestAbs);
    }

    private static double Cross(VectorD a, VectorD b) => a.X * b.Y - a.Y * b.X;

    internal static double[] Statistics(double[] values, out double mean, out double standardDeviation)
    {
        double min = values.Min(), max = values.Max();
        mean = values.Average();
        double average = mean;
        standardDeviation = Math.Sqrt(values.Average(value => (value - average) * (value - average)));
        return [min, max];
    }

    private static double[] Copy(IEnumerable<double> values) => values.ToArray();
}

/// <summary>Orthogonal least-squares line fit and perpendicular residual range.</summary>
public sealed record StraightnessResult(bool Success, string? Error, PointD Centroid, VectorD Normal, IReadOnlyList<double> SignedResiduals, double PeakToValley)
{
    internal static StraightnessResult Failure(string error) => new(false, error, default, default, [], double.NaN);
    internal static StraightnessResult Succeeded(PointD centroid, VectorD normal, double[] residuals, double range) => new(true, null, centroid, normal, Array.AsReadOnly(residuals), range);
}

/// <summary>Algebraic least-squares circle fit with radial residual range.</summary>
/// <param name="Success">Whether fitting produced a meaningful circle.</param>
/// <param name="Error">Diagnostic when the fit failed.</param>
/// <param name="Center">Fitted circle center.</param>
/// <param name="Radius">Mean observed radius about the fitted center.</param>
/// <param name="RadialResiduals">Per-point radius minus fitted mean radius.</param>
/// <param name="PeakToValley">Range of the radial residuals.</param>
public sealed record CircularityResult(bool Success, string? Error, PointD Center, double Radius, IReadOnlyList<double> RadialResiduals, double PeakToValley)
{
    internal static CircularityResult Failure(string error) => new(false, error, default, double.NaN, [], double.NaN);
    internal static CircularityResult Succeeded(PointD center, double radius, double[] residuals, double range) => new(true, null, center, radius, Array.AsReadOnly(residuals), range);
}

/// <summary>Nearest-reference contour offsets, with signed offsets positive to the directed segment's left.</summary>
/// <param name="Success">Whether all input points found a usable reference polyline.</param>
/// <param name="Error">Diagnostic when evaluation failed.</param>
/// <param name="Deviations">Nearest-reference distances in measured point order.</param>
public sealed record ContourDeviationResult(bool Success, string? Error, IReadOnlyList<double> Deviations)
{
    /// <summary>Returns the range of signed offsets, or NaN if evaluation failed.</summary>
    public double PeakToValley => Success ? Deviations.Max() - Deviations.Min() : double.NaN;
    /// <summary>Returns the largest absolute offset, or NaN if evaluation failed.</summary>
    public double MaximumAbsoluteDeviation => Success ? Deviations.Max(Math.Abs) : double.NaN;
    internal static ContourDeviationResult Failure(string error) => new(false, error, []);
    internal static ContourDeviationResult Succeeded(List<double> deviations) => new(true, null, Array.AsReadOnly(deviations.ToArray()));
}

/// <summary>A source station and its normal-intersection witness pair.</summary>
public sealed record GapSample(double Station, PointD SourcePoint, PointD OpposingPoint, double Gap);

/// <summary>Uniformly sampled normal gap profile and population statistics.</summary>
public sealed record GapProfileResult(bool Success, string? Error, IReadOnlyList<GapSample> Samples, double Minimum, double Maximum, double Mean, double StandardDeviation)
{
    internal static GapProfileResult Failure(string error) => new(false, error, [], double.NaN, double.NaN, double.NaN, double.NaN);
    internal static GapProfileResult Succeeded(List<GapSample> samples)
    {
        double[] values = samples.Select(sample => sample.Gap).ToArray();
        double[] range = ProfileMeasurementTools.Statistics(values, out double mean, out double sd);
        return new(true, null, Array.AsReadOnly(samples.ToArray()), range[0], range[1], mean, sd);
    }
}

/// <summary>Statistics of consecutive distances in an ordered point sequence.</summary>
public sealed record PitchResult(bool Success, string? Error, IReadOnlyList<double> Pitches, double Minimum, double Maximum, double Mean, double StandardDeviation)
{
    internal static PitchResult Failure(string error) => new(false, error, [], double.NaN, double.NaN, double.NaN, double.NaN);
    internal static PitchResult Succeeded(double[] pitches)
    {
        double[] range = ProfileMeasurementTools.Statistics(pitches, out double mean, out double sd);
        return new(true, null, Array.AsReadOnly(pitches), range[0], range[1], mean, sd);
    }
}
