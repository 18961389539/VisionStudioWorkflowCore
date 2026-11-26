using System;
using System.Collections.Generic;
using System.Linq;
using ImageViewer.Models;

namespace ImageViewer.Core.Geometry;

/// <summary>
/// Framework-neutral two-dimensional geometry operations.
/// </summary>
public static class Geometry2D
{
    /// <summary>Computes the Euclidean distance between two points.</summary>
    public static double Distance(PointD first, PointD second) => first.DistanceTo(second);

    /// <summary>Computes the shortest distance from a point to a line segment.</summary>
    public static double DistanceToSegment(PointD point, PointD segmentStart, PointD segmentEnd)
    {
        double dx = segmentEnd.X - segmentStart.X;
        double dy = segmentEnd.Y - segmentStart.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            return Distance(point, segmentStart);
        }

        double projection = ((point.X - segmentStart.X) * dx + (point.Y - segmentStart.Y) * dy) / lengthSquared;
        projection = Math.Clamp(projection, 0, 1);
        PointD closestPoint = new(segmentStart.X + projection * dx, segmentStart.Y + projection * dy);
        return Distance(point, closestPoint);
    }

    /// <summary>Computes the total length of a polyline.</summary>
    public static double PolylineLength(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        double length = 0;
        for (int index = 1; index < points.Count; index++)
        {
            length += Distance(points[index - 1], points[index]);
        }

        return length;
    }

    /// <summary>Computes the length of each consecutive polyline segment.</summary>
    public static IReadOnlyList<double> GetPolylineSegmentLengths(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            return Array.Empty<double>();
        }

        var lengths = new double[points.Count - 1];
        for (int index = 1; index < points.Count; index++)
        {
            lengths[index - 1] = Distance(points[index - 1], points[index]);
        }

        return lengths;
    }

    /// <summary>Returns whether a point is within the specified distance of a segment.</summary>
    public static bool IsPointNearSegment(PointD point, PointD segmentStart, PointD segmentEnd, double threshold)
    {
        return DistanceToSegment(point, segmentStart, segmentEnd) < threshold;
    }

    /// <summary>Computes the directed angle around a center in the range [0, 360).</summary>
    public static double Angle(PointD first, PointD center, PointD second)
    {
        double firstAngle = Math.Atan2(first.Y - center.Y, first.X - center.X);
        double secondAngle = Math.Atan2(second.Y - center.Y, second.X - center.X);
        double result = (secondAngle - firstAngle) * 180 / Math.PI;
        return result < 0 ? result + 360 : result;
    }

    /// <summary>Rotates a point around a center by an angle in degrees.</summary>
    public static PointD RotatePoint(PointD point, PointD center, double angleDegrees)
    {
        double angleRadians = angleDegrees * Math.PI / 180;
        double cosine = Math.Cos(angleRadians);
        double sine = Math.Sin(angleRadians);
        double dx = point.X - center.X;
        double dy = point.Y - center.Y;
        return new PointD(center.X + dx * cosine - dy * sine, center.Y + dx * sine + dy * cosine);
    }

    /// <summary>Computes the axis-aligned bounding box, or null for an empty point sequence.</summary>
    public static RectD? GetBoundingBox(IEnumerable<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        using IEnumerator<PointD> enumerator = points.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        PointD first = enumerator.Current;
        double minX = first.X;
        double maxX = first.X;
        double minY = first.Y;
        double maxY = first.Y;
        while (enumerator.MoveNext())
        {
            PointD point = enumerator.Current;
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Computes the arithmetic mean of a point sequence.</summary>
    public static PointD GetCentroid(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return default;
        }

        double sumX = 0;
        double sumY = 0;
        foreach (PointD point in points)
        {
            sumX += point.X;
            sumY += point.Y;
        }

        return new PointD(sumX / points.Count, sumY / points.Count);
    }

    /// <summary>Tests whether a point lies inside a polygon using an even-odd crossing rule.</summary>
    public static bool IsPointInPolygon(PointD point, IReadOnlyList<PointD> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 3)
        {
            return false;
        }

        bool inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            PointD currentPoint = polygon[current];
            PointD previousPoint = polygon[previous];
            if ((currentPoint.Y > point.Y) != (previousPoint.Y > point.Y) &&
                point.X < (previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) /
                (previousPoint.Y - currentPoint.Y) + currentPoint.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Computes the perimeter of a closed polygon.</summary>
    public static double PolygonPerimeter(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            return 0;
        }

        double perimeter = 0;
        for (int index = 0; index < points.Count; index++)
        {
            perimeter += Distance(points[index], points[(index + 1) % points.Count]);
        }

        return perimeter;
    }

    /// <summary>Computes the unsigned area of a polygon using the shoelace formula.</summary>
    public static double PolygonArea(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            PointD first = points[index];
            PointD second = points[(index + 1) % points.Count];
            area += first.X * second.Y - second.X * first.Y;
        }

        return Math.Abs(area) / 2;
    }

    /// <summary>Computes polygon area, perimeter and centroid.</summary>
    public static (double Area, double Perimeter, PointD Centroid) GetPolygonMetrics(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return (0, 0, default);
        }

        double areaAccumulator = 0;
        double centroidXAccumulator = 0;
        double centroidYAccumulator = 0;
        double perimeter = 0;
        double sumX = 0;
        double sumY = 0;
        for (int index = 0; index < points.Count; index++)
        {
            PointD current = points[index];
            PointD next = points[(index + 1) % points.Count];
            sumX += current.X;
            sumY += current.Y;
            if (points.Count >= 2)
            {
                perimeter += Distance(current, next);
            }

            if (points.Count >= 3)
            {
                double cross = current.X * next.Y - next.X * current.Y;
                areaAccumulator += cross;
                centroidXAccumulator += (current.X + next.X) * cross;
                centroidYAccumulator += (current.Y + next.Y) * cross;
            }
        }

        PointD centroid = new(sumX / points.Count, sumY / points.Count);
        if (points.Count >= 3 && Math.Abs(areaAccumulator) > 1e-9)
        {
            centroid = new PointD(
                centroidXAccumulator / (3 * areaAccumulator),
                centroidYAccumulator / (3 * areaAccumulator));
        }

        return (Math.Abs(areaAccumulator) / 2, perimeter, centroid);
    }

    /// <summary>Computes the smaller angle between two rays in the range [0, 180].</summary>
    public static double SmallestAngle(PointD first, PointD vertex, PointD second)
    {
        double firstAngle = Math.Atan2(first.Y - vertex.Y, first.X - vertex.X);
        double secondAngle = Math.Atan2(second.Y - vertex.Y, second.X - vertex.X);
        double difference = Math.Abs(firstAngle - secondAngle) * 180 / Math.PI;
        return difference > 180 ? 360 - difference : difference;
    }
}
