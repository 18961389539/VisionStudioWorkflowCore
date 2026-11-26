using System.Collections.ObjectModel;
using ImageViewer.Models;

namespace ImageViewer.Core.Measurements;

/// <summary>Provides framework-neutral measurements for ordered two-dimensional contours.</summary>
public static class ShapeMeasurementTools
{
    private const double Epsilon = 1e-10;

    /// <summary>
    /// Measures a filled polygon and optional interior hole polygons. Contour vertices are ordered
    /// around each boundary; the closing edge is implicit and a repeated final copy of the first
    /// vertex is accepted. Hole area is subtracted from area and centroid, while all boundary
    /// lengths contribute to perimeter.
    /// </summary>
    /// <param name="outerContour">Ordered vertices around the exterior boundary.</param>
    /// <param name="holes">Optional ordered polygons representing holes inside the exterior.</param>
    /// <returns>Geometric measurements derived from the supplied polygon boundaries.</returns>
    /// <exception cref="ArgumentNullException">A required collection is null.</exception>
    /// <exception cref="ArgumentException">A contour is invalid or a hole lies outside the outer polygon.</exception>
    public static ContourShapeMetrics MeasureContour(
        IReadOnlyList<PointD> outerContour,
        IReadOnlyList<IReadOnlyList<PointD>>? holes = null)
    {
        ArgumentNullException.ThrowIfNull(outerContour);
        List<PointD> outer = NormalizeContour(outerContour, nameof(outerContour));
        if (HasSelfIntersection(outer))
        {
            throw new ArgumentException("The outer contour must not self-intersect.", nameof(outerContour));
        }

        PolygonProperties outerProperties = GetPolygonProperties(outer);
        if (outerProperties.Area <= Epsilon)
        {
            throw new ArgumentException("The outer contour must enclose a non-zero area.", nameof(outerContour));
        }

        var holeContours = new List<List<PointD>>();
        double holesArea = 0;
        double holesPerimeter = 0;
        double weightedX = outerProperties.Centroid.X * outerProperties.Area;
        double weightedY = outerProperties.Centroid.Y * outerProperties.Area;
        if (holes is not null)
        {
            for (int i = 0; i < holes.Count; i++)
            {
                IReadOnlyList<PointD> source = holes[i]
                    ?? throw new ArgumentException("A hole contour cannot be null.", nameof(holes));
                List<PointD> hole = NormalizeContour(source, nameof(holes));
                if (HasSelfIntersection(hole))
                {
                    throw new ArgumentException("A hole contour must not self-intersect.", nameof(holes));
                }

                PolygonProperties properties = GetPolygonProperties(hole);
                if (properties.Area <= Epsilon)
                {
                    throw new ArgumentException("Each hole must enclose a non-zero area.", nameof(holes));
                }

                if (hole.Any(point => !PointInPolygon(point, outer)) || BoundariesIntersect(hole, outer))
                {
                    throw new ArgumentException("Every hole must lie inside the outer contour.", nameof(holes));
                }

                foreach (List<PointD> previousHole in holeContours)
                {
                    if (BoundariesIntersect(hole, previousHole) ||
                        PointInPolygon(hole[0], previousHole) || PointInPolygon(previousHole[0], hole))
                    {
                        throw new ArgumentException("Hole contours must be disjoint and cannot contain one another.", nameof(holes));
                    }
                }

                holeContours.Add(hole);
                holesArea += properties.Area;
                holesPerimeter += properties.Perimeter;
                weightedX -= properties.Centroid.X * properties.Area;
                weightedY -= properties.Centroid.Y * properties.Area;
            }
        }

        if (holesArea >= outerProperties.Area - Epsilon)
        {
            throw new ArgumentException("Hole area must be smaller than outer contour area.", nameof(holes));
        }

        double netArea = outerProperties.Area - holesArea;
        PointD centroid = new(weightedX / netArea, weightedY / netArea);
        List<PointD> hull = CalculateConvexHull(outer);
        double minimumFeret = MinimumFeretDiameter(hull);
        double maximumFeret = MaximumFeretDiameter(hull);
        double perimeter = outerProperties.Perimeter + holesPerimeter;
        double circularity = perimeter <= Epsilon ? 0 : Math.Clamp(4 * Math.PI * netArea / (perimeter * perimeter), 0, 1);

        return new ContourShapeMetrics(
            perimeter,
            netArea,
            centroid,
            new ReadOnlyCollection<PointD>(hull),
            minimumFeret,
            maximumFeret,
            minimumFeret <= Epsilon ? 0 : maximumFeret / minimumFeret,
            circularity,
            holesArea / outerProperties.Area);
    }

    /// <summary>
    /// Fits the minimum-area enclosing rectangle by evaluating the rectangle aligned with every
    /// convex-hull edge. The returned width is the longer side, and angle is its direction in radians
    /// from the positive X axis, normalized to [-π/2, π/2).
    /// </summary>
    /// <param name="contour">Ordered contour points; convexity is not required.</param>
    /// <returns>The minimum-area oriented rectangle enclosing all points.</returns>
    public static OrientedRectangleFit FitMinimumAreaRectangle(IReadOnlyList<PointD> contour)
    {
        ArgumentNullException.ThrowIfNull(contour);
        List<PointD> points = NormalizeContour(contour, nameof(contour), requireThree: false);
        List<PointD> hull = CalculateConvexHull(points);
        if (hull.Count < 2)
        {
            throw new ArgumentException("At least two distinct finite points are required.", nameof(contour));
        }

        if (hull.Count == 2)
        {
            PointD first = hull[0];
            PointD second = hull[1];
            VectorD axis = second - first;
            double length = axis.Length;
            double angle = NormalizeRectangleAngle(Math.Atan2(axis.Y, axis.X));
            var degenerateCorners = Array.AsReadOnly(new[] { first, second, second, first });
            return new OrientedRectangleFit(Midpoint(first, second), length, 0, angle, degenerateCorners, 0);
        }

        double bestArea = double.PositiveInfinity;
        double bestMinX = 0;
        double bestMaxX = 0;
        double bestMinY = 0;
        double bestMaxY = 0;
        double bestAngle = 0;
        foreach ((double ux, double uy) in HullEdgeDirections(hull))
        {
            double vx = -uy;
            double vy = ux;
            Project(hull, ux, uy, out double minX, out double maxX);
            Project(hull, vx, vy, out double minY, out double maxY);
            double area = (maxX - minX) * (maxY - minY);
            if (area < bestArea)
            {
                bestArea = area;
                bestMinX = minX;
                bestMaxX = maxX;
                bestMinY = minY;
                bestMaxY = maxY;
                bestAngle = Math.Atan2(uy, ux);
            }
        }

        double sideX = bestMaxX - bestMinX;
        double sideY = bestMaxY - bestMinY;
        double width;
        double height;
        double angleRadians;
        double centerX;
        double centerY;
        if (sideX >= sideY)
        {
            width = sideX;
            height = sideY;
            angleRadians = bestAngle;
            centerX = ((bestMinX + bestMaxX) / 2) * Math.Cos(bestAngle) - ((bestMinY + bestMaxY) / 2) * Math.Sin(bestAngle);
            centerY = ((bestMinX + bestMaxX) / 2) * Math.Sin(bestAngle) + ((bestMinY + bestMaxY) / 2) * Math.Cos(bestAngle);
        }
        else
        {
            width = sideY;
            height = sideX;
            angleRadians = bestAngle + (Math.PI / 2);
            centerX = ((bestMinX + bestMaxX) / 2) * Math.Cos(bestAngle) - ((bestMinY + bestMaxY) / 2) * Math.Sin(bestAngle);
            centerY = ((bestMinX + bestMaxX) / 2) * Math.Sin(bestAngle) + ((bestMinY + bestMaxY) / 2) * Math.Cos(bestAngle);
        }

        angleRadians = NormalizeRectangleAngle(angleRadians);
        PointD center = new(centerX, centerY);
        PointD[] corners = BuildRectangleCorners(center, width, height, angleRadians);
        return new OrientedRectangleFit(center, width, height, angleRadians, Array.AsReadOnly(corners), bestArea);
    }

    /// <summary>
    /// Fits a capsule (two parallel straight sides joined by semicircular ends) to a closed contour.
    /// The principal covariance axis supplies orientation; projected extents supply center and width;
    /// residuals are distances from contour points to the fitted capsule boundary. Fits with substantial
    /// normalized residuals, non-finite points, or insufficient elongation are rejected.
    /// </summary>
    /// <param name="contour">Ordered vertices around one closed capsule-like contour.</param>
    /// <param name="fit">The fit when successful; otherwise null.</param>
    /// <param name="failureReason">A human-readable reason when the contour is rejected.</param>
    /// <returns>True when the contour is capsule-like within the documented residual limits.</returns>
    public static bool TryFitSlot(
        IReadOnlyList<PointD> contour,
        out SlotShapeFit? fit,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(contour);
        fit = null;
        failureReason = null;
        List<PointD> points;
        try
        {
            points = NormalizeContour(contour, nameof(contour));
        }
        catch (ArgumentException exception)
        {
            failureReason = exception.Message;
            return false;
        }

        List<PointD> hull = CalculateConvexHull(points);
        if (hull.Count < 5)
        {
            failureReason = "A slot fit requires at least five distinct boundary points.";
            return false;
        }

        double meanX = points.Average(point => point.X);
        double meanY = points.Average(point => point.Y);
        double xx = 0;
        double xy = 0;
        double yy = 0;
        foreach (PointD point in points)
        {
            double dx = point.X - meanX;
            double dy = point.Y - meanY;
            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }

        double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
        double ux = Math.Cos(angle);
        double uy = Math.Sin(angle);
        double vx = -uy;
        double vy = ux;
        Project(points, ux, uy, out double minU, out double maxU);
        Project(points, vx, vy, out double minV, out double maxV);
        double length = maxU - minU;
        double width = maxV - minV;
        if (width <= Epsilon || length <= width * 1.35)
        {
            failureReason = "The contour is too short or too wide to be a slot.";
            return false;
        }

        double radius = width / 2;
        double centerU = (minU + maxU) / 2;
        double centerV = (minV + maxV) / 2;
        double halfCenterline = (length - width) / 2;
        double startU = centerU - halfCenterline;
        double endU = centerU + halfCenterline;
        var distances = new double[points.Count];
        double squaredResidual = 0;
        double maximumResidual = 0;
        for (int i = 0; i < points.Count; i++)
        {
            PointD point = points[i];
            double u = (point.X * ux) + (point.Y * uy);
            double v = (point.X * vx) + (point.Y * vy);
            double nearestU = Math.Clamp(u, startU, endU);
            double distance = Math.Abs(Math.Sqrt(Math.Pow(u - nearestU, 2) + Math.Pow(v - centerV, 2)) - radius);
            distances[i] = distance;
            squaredResidual += distance * distance;
            maximumResidual = Math.Max(maximumResidual, distance);
        }

        double rmsResidual = Math.Sqrt(squaredResidual / distances.Length);
        double normalizedRms = rmsResidual / width;
        double normalizedMaximum = maximumResidual / width;
        if (normalizedRms > 0.075 || normalizedMaximum > 0.22)
        {
            failureReason = $"The contour is not capsule-like (normalized RMS residual {normalizedRms:F3}, maximum {normalizedMaximum:F3}).";
            return false;
        }

        PointD center = new((centerU * ux) + (centerV * vx), (centerU * uy) + (centerV * vy));
        PointD axisStart = new((startU * ux) + (centerV * vx), (startU * uy) + (centerV * vy));
        PointD axisEnd = new((endU * ux) + (centerV * vx), (endU * uy) + (centerV * vy));
        double quality = Math.Clamp(1 - (normalizedRms / 0.075), 0, 1);
        fit = new SlotShapeFit(
            center,
            length,
            width,
            radius,
            angle,
            axisStart,
            axisEnd,
            rmsResidual,
            maximumResidual,
            normalizedRms,
            quality);
        return true;
    }

    private static List<PointD> NormalizeContour(IReadOnlyList<PointD> contour, string parameterName, bool requireThree = true)
    {
        var points = new List<PointD>(contour.Count);
        foreach (PointD point in contour)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ArgumentException("Contour coordinates must be finite.", parameterName);
            }

            if (points.Count == 0 || points[^1].DistanceTo(point) > Epsilon)
            {
                points.Add(point);
            }
        }

        if (points.Count > 1 && points[0].DistanceTo(points[^1]) <= Epsilon)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < (requireThree ? 3 : 2))
        {
            throw new ArgumentException(requireThree
                ? "A contour must contain at least three distinct points."
                : "At least two distinct points are required.", parameterName);
        }

        return points;
    }

    private static PolygonProperties GetPolygonProperties(IReadOnlyList<PointD> polygon)
    {
        PointD origin = polygon[0];
        double twiceSignedArea = 0;
        double centroidX = 0;
        double centroidY = 0;
        double perimeter = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            PointD current = polygon[i];
            PointD next = polygon[(i + 1) % polygon.Count];
            double currentX = current.X - origin.X;
            double currentY = current.Y - origin.Y;
            double nextX = next.X - origin.X;
            double nextY = next.Y - origin.Y;
            double cross = (currentX * nextY) - (nextX * currentY);
            twiceSignedArea += cross;
            centroidX += (currentX + nextX) * cross;
            centroidY += (currentY + nextY) * cross;
            perimeter += current.DistanceTo(next);
        }

        double signedArea = twiceSignedArea / 2;
        if (Math.Abs(signedArea) <= Epsilon)
        {
            return new PolygonProperties(0, perimeter, origin);
        }

        PointD centroid = new(
            origin.X + (centroidX / (6 * signedArea)),
            origin.Y + (centroidY / (6 * signedArea)));
        return new PolygonProperties(Math.Abs(signedArea), perimeter, centroid);
    }

    private static List<PointD> CalculateConvexHull(IReadOnlyList<PointD> points)
    {
        PointD[] sorted = points.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        if (sorted.Length <= 2)
        {
            return sorted.ToList();
        }

        var lower = new List<PointD>();
        foreach (PointD point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= Epsilon)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<PointD>();
        for (int i = sorted.Length - 1; i >= 0; i--)
        {
            PointD point = sorted[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= Epsilon)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(PointD origin, PointD first, PointD second)
    {
        return ((first.X - origin.X) * (second.Y - origin.Y)) - ((first.Y - origin.Y) * (second.X - origin.X));
    }

    private static double MinimumFeretDiameter(IReadOnlyList<PointD> hull)
    {
        if (hull.Count < 3)
        {
            return 0;
        }

        double minimum = double.PositiveInfinity;
        foreach ((double ux, double uy) in HullEdgeDirections(hull))
        {
            double nx = -uy;
            double ny = ux;
            Project(hull, nx, ny, out double min, out double max);
            minimum = Math.Min(minimum, max - min);
        }

        return double.IsFinite(minimum) ? minimum : 0;
    }

    private static double MaximumFeretDiameter(IReadOnlyList<PointD> hull)
    {
        double maximumSquared = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            for (int j = i + 1; j < hull.Count; j++)
            {
                double dx = hull[i].X - hull[j].X;
                double dy = hull[i].Y - hull[j].Y;
                maximumSquared = Math.Max(maximumSquared, (dx * dx) + (dy * dy));
            }
        }

        return Math.Sqrt(maximumSquared);
    }

    private static IEnumerable<(double X, double Y)> HullEdgeDirections(IReadOnlyList<PointD> hull)
    {
        for (int i = 0; i < hull.Count; i++)
        {
            PointD first = hull[i];
            PointD second = hull[(i + 1) % hull.Count];
            VectorD edge = second - first;
            double length = edge.Length;
            if (length > Epsilon)
            {
                yield return (edge.X / length, edge.Y / length);
            }
        }
    }

    private static void Project(IReadOnlyList<PointD> points, double ux, double uy, out double minimum, out double maximum)
    {
        minimum = double.PositiveInfinity;
        maximum = double.NegativeInfinity;
        foreach (PointD point in points)
        {
            double projection = (point.X * ux) + (point.Y * uy);
            minimum = Math.Min(minimum, projection);
            maximum = Math.Max(maximum, projection);
        }
    }

    private static PointD[] BuildRectangleCorners(PointD center, double width, double height, double angle)
    {
        double ux = Math.Cos(angle);
        double uy = Math.Sin(angle);
        double vx = -uy;
        double vy = ux;
        double halfWidth = width / 2;
        double halfHeight = height / 2;
        return
        [
            new PointD(center.X - (halfWidth * ux) - (halfHeight * vx), center.Y - (halfWidth * uy) - (halfHeight * vy)),
            new PointD(center.X + (halfWidth * ux) - (halfHeight * vx), center.Y + (halfWidth * uy) - (halfHeight * vy)),
            new PointD(center.X + (halfWidth * ux) + (halfHeight * vx), center.Y + (halfWidth * uy) + (halfHeight * vy)),
            new PointD(center.X - (halfWidth * ux) + (halfHeight * vx), center.Y - (halfWidth * uy) + (halfHeight * vy))
        ];
    }

    private static bool PointInPolygon(PointD point, IReadOnlyList<PointD> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            PointD a = polygon[i];
            PointD b = polygon[j];
            if (DistanceToSegment(point, a, b) <= Epsilon)
            {
                return true;
            }

            bool crosses = (a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X);
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool HasSelfIntersection(IReadOnlyList<PointD> polygon)
    {
        for (int first = 0; first < polygon.Count; first++)
        {
            PointD firstStart = polygon[first];
            PointD firstEnd = polygon[(first + 1) % polygon.Count];
            for (int second = first + 1; second < polygon.Count; second++)
            {
                if (second == first || second == first + 1 || first == 0 && second == polygon.Count - 1)
                {
                    continue;
                }

                PointD secondStart = polygon[second];
                PointD secondEnd = polygon[(second + 1) % polygon.Count];
                if (SegmentsIntersect(firstStart, firstEnd, secondStart, secondEnd))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool BoundariesIntersect(IReadOnlyList<PointD> first, IReadOnlyList<PointD> second)
    {
        for (int i = 0; i < first.Count; i++)
        {
            PointD firstStart = first[i];
            PointD firstEnd = first[(i + 1) % first.Count];
            for (int j = 0; j < second.Count; j++)
            {
                if (SegmentsIntersect(firstStart, firstEnd, second[j], second[(j + 1) % second.Count]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SegmentsIntersect(PointD a, PointD b, PointD c, PointD d)
    {
        double abC = Cross(a, b, c);
        double abD = Cross(a, b, d);
        double cdA = Cross(c, d, a);
        double cdB = Cross(c, d, b);
        double tolerance = Epsilon * Math.Max(1, Math.Max((b - a).Length, (d - c).Length));

        if (((abC > tolerance && abD < -tolerance) || (abC < -tolerance && abD > tolerance)) &&
            ((cdA > tolerance && cdB < -tolerance) || (cdA < -tolerance && cdB > tolerance)))
        {
            return true;
        }

        return Math.Abs(abC) <= tolerance && DistanceToSegment(c, a, b) <= Epsilon ||
               Math.Abs(abD) <= tolerance && DistanceToSegment(d, a, b) <= Epsilon ||
               Math.Abs(cdA) <= tolerance && DistanceToSegment(a, c, d) <= Epsilon ||
               Math.Abs(cdB) <= tolerance && DistanceToSegment(b, c, d) <= Epsilon;
    }

    private static double DistanceToSegment(PointD point, PointD start, PointD end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= Epsilon)
        {
            return point.DistanceTo(start);
        }

        double t = Math.Clamp((((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0, 1);
        return point.DistanceTo(new PointD(start.X + (t * dx), start.Y + (t * dy)));
    }

    private static double NormalizeRectangleAngle(double angle)
    {
        while (angle < -(Math.PI / 2))
        {
            angle += Math.PI;
        }

        while (angle >= Math.PI / 2)
        {
            angle -= Math.PI;
        }

        return angle;
    }

    private static PointD Midpoint(PointD first, PointD second) => new((first.X + second.X) / 2, (first.Y + second.Y) / 2);

    private sealed record PolygonProperties(double Area, double Perimeter, PointD Centroid);
}

/// <summary>Measurements calculated from an outer contour and its explicit hole contours.</summary>
public sealed record ContourShapeMetrics(
    double Perimeter,
    double Area,
    PointD Centroid,
    IReadOnlyList<PointD> ConvexHull,
    double MinimumFeretDiameter,
    double MaximumFeretDiameter,
    double AspectRatio,
    double Circularity,
    double HoleAreaFraction);

/// <summary>An enclosing oriented rectangle with corners in counter-clockwise order.</summary>
public sealed record OrientedRectangleFit(
    PointD Center,
    double Width,
    double Height,
    double AngleRadians,
    IReadOnlyList<PointD> Corners,
    double Area);

/// <summary>Geometric capsule fit for a slot-like closed contour.</summary>
public sealed record SlotShapeFit(
    PointD Center,
    double Length,
    double Width,
    double EndRadius,
    double AngleRadians,
    PointD AxisStart,
    PointD AxisEnd,
    double RmsResidual,
    double MaximumResidual,
    double NormalizedRmsResidual,
    double Quality);
