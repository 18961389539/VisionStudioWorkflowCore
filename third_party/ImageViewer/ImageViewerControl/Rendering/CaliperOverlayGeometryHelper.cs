using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Rendering
{
    internal static class CaliperOverlayGeometryHelper
    {
        public static LineSegmentOverlay[] BuildCircularCaliperRegionSegments(CircularCaliperMeasureRoi caliper)
        {
            return BuildCircularCaliperRegionSegments(caliper.Center.ToWpfPoint(), caliper.Radius, caliper.CaliperSearchRange, caliper.CaliperCount);
        }

        public static LineSegmentOverlay[] BuildCircularCaliperRegionSegments(Point center, double radius, int caliperSearchRange, int caliperCount)
        {
            return CreateCircleApproximationSegments(center, Math.Max(1, radius - caliperSearchRange), caliperCount)
                .Concat(CreateCircleApproximationSegments(center, radius + caliperSearchRange, caliperCount))
                .ToArray();
        }

        public static LineSegmentOverlay[] BuildCircularCaliperBars(CircularCaliperMeasureRoi caliper)
        {
            return BuildCircularCaliperBars(caliper.Center.ToWpfPoint(), caliper.Radius, caliper.CaliperSearchRange, caliper.CaliperCount);
        }

        public static LineSegmentOverlay[] BuildCircularCaliperBars(Point center, double radius, int caliperSearchRange, int caliperCount)
        {
            int count = Math.Clamp(caliperCount, 6, 180);
            var segments = new LineSegmentOverlay[count];
            for (int i = 0; i < count; i++)
            {
                double angleRadians = i * Math.PI * 2 / count;
                Vector radial = new(Math.Cos(angleRadians), Math.Sin(angleRadians));
                segments[i] = new LineSegmentOverlay(
                    (center + radial * Math.Max(0, radius - caliperSearchRange)).ToPointD(),
                    (center + radial * (radius + caliperSearchRange)).ToPointD());
            }

            return segments;
        }

        public static LineSegmentOverlay[] BuildArcCaliperRegionSegments(ArcCaliperMeasureRoi caliper)
        {
            return BuildArcCaliperRegionSegments(caliper.Center.ToWpfPoint(), caliper.Radius, caliper.CaliperSearchRange, caliper.CaliperCount, caliper.StartAngle, caliper.SweepAngle);
        }

        public static LineSegmentOverlay[] BuildArcCaliperRegionSegments(Point center, double radius, int caliperSearchRange, int caliperCount, double startAngle, double sweepAngle)
        {
            double innerRadius = Math.Max(1, radius - caliperSearchRange);
            double outerRadius = radius + caliperSearchRange;
            List<LineSegmentOverlay> segments =
            [
                .. CreateArcApproximationSegments(center, innerRadius, startAngle, sweepAngle, caliperCount),
                .. CreateArcApproximationSegments(center, outerRadius, startAngle, sweepAngle, caliperCount)
            ];

            double startRadians = startAngle * Math.PI / 180.0;
            double endRadians = (startAngle + sweepAngle) * Math.PI / 180.0;
            Vector startRadial = new(Math.Cos(startRadians), Math.Sin(startRadians));
            Vector endRadial = new(Math.Cos(endRadians), Math.Sin(endRadians));
            segments.Add(new LineSegmentOverlay((center + startRadial * innerRadius).ToPointD(), (center + startRadial * outerRadius).ToPointD()));
            segments.Add(new LineSegmentOverlay((center + endRadial * innerRadius).ToPointD(), (center + endRadial * outerRadius).ToPointD()));
            return [.. segments];
        }

        public static LineSegmentOverlay[] BuildArcCaliperBars(ArcCaliperMeasureRoi caliper)
        {
            return BuildArcCaliperBars(caliper.Center.ToWpfPoint(), caliper.Radius, caliper.CaliperSearchRange, caliper.CaliperCount, caliper.StartAngle, caliper.SweepAngle);
        }

        public static LineSegmentOverlay[] BuildArcCaliperBars(Point center, double radius, int caliperSearchRange, int caliperCount, double startAngle, double sweepAngle)
        {
            int count = Math.Clamp(caliperCount, 4, 180);
            double innerRadius = Math.Max(0, radius - caliperSearchRange);
            double outerRadius = radius + caliperSearchRange;
            var segments = new LineSegmentOverlay[count];
            for (int i = 0; i < count; i++)
            {
                double angleDegrees = startAngle + (count == 1 ? 0 : sweepAngle * i / (count - 1));
                double angleRadians = angleDegrees * Math.PI / 180.0;
                Vector radial = new(Math.Cos(angleRadians), Math.Sin(angleRadians));
                segments[i] = new LineSegmentOverlay((center + radial * innerRadius).ToPointD(), (center + radial * outerRadius).ToPointD());
            }

            return segments;
        }

        public static LineSegmentOverlay[] BuildLineCaliperRegionSegments(LineCaliperMeasureRoi line)
        {
            return BuildLineCaliperRegionSegments(line.P1.ToWpfPoint(), line.P2.ToWpfPoint(), line.CaliperSearchRange);
        }

        public static LineSegmentOverlay[] BuildLineCaliperRegionSegments(Point p1, Point p2, int caliperSearchRange)
        {
            Vector direction = p2 - p1;
            if (direction.LengthSquared < 1e-6)
            {
                direction = new Vector(1, 0);
            }

            direction.Normalize();
            Vector measurementDirection = new(-direction.Y, direction.X);
            Point topLeft = p1 - measurementDirection * caliperSearchRange;
            Point topRight = p2 - measurementDirection * caliperSearchRange;
            Point bottomRight = p2 + measurementDirection * caliperSearchRange;
            Point bottomLeft = p1 + measurementDirection * caliperSearchRange;
            return
            [
                new LineSegmentOverlay(topLeft.ToPointD(), topRight.ToPointD()),
                new LineSegmentOverlay(topRight.ToPointD(), bottomRight.ToPointD()),
                new LineSegmentOverlay(bottomRight.ToPointD(), bottomLeft.ToPointD()),
                new LineSegmentOverlay(bottomLeft.ToPointD(), topLeft.ToPointD())
            ];
        }

        public static LineSegmentOverlay[] BuildLineCaliperBars(LineCaliperMeasureRoi line)
        {
            return BuildLineCaliperBars(line.P1.ToWpfPoint(), line.P2.ToWpfPoint(), line.CaliperSearchRange, line.CaliperCount);
        }

        public static LineSegmentOverlay[] BuildLineCaliperBars(Point p1, Point p2, int caliperSearchRange, int caliperCount)
        {
            Vector direction = p2 - p1;
            if (direction.LengthSquared < 1e-6)
            {
                direction = new Vector(1, 0);
            }

            direction.Normalize();
            Vector measurementDirection = new(-direction.Y, direction.X);
            int resolvedCaliperCount = Math.Clamp(caliperCount, 6, 180);
            var segments = new LineSegmentOverlay[resolvedCaliperCount];
            for (int i = 0; i < resolvedCaliperCount; i++)
            {
                double lerp = resolvedCaliperCount == 1 ? 0.5 : (double)i / (resolvedCaliperCount - 1);
                Point sampleCenter = new(
                    p1.X + (p2.X - p1.X) * lerp,
                    p1.Y + (p2.Y - p1.Y) * lerp);
                segments[i] = new LineSegmentOverlay(
                    (sampleCenter - measurementDirection * caliperSearchRange).ToPointD(),
                    (sampleCenter + measurementDirection * caliperSearchRange).ToPointD());
            }

            return segments;
        }

        public static LineSegmentOverlay[] BuildLinearMarkers(IReadOnlyList<Point> points, Vector markerDirection, double markerHalfLength)
        {
            var markers = new LineSegmentOverlay[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                markers[i] = new LineSegmentOverlay((points[i] - markerDirection * markerHalfLength).ToPointD(), (points[i] + markerDirection * markerHalfLength).ToPointD());
            }

            return markers;
        }

        public static LineSegmentOverlay[] BuildCircularMarkers(IReadOnlyList<Point> points, Point center, double markerHalfLength)
        {
            var markers = new LineSegmentOverlay[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                Vector radial = points[i] - center;
                Vector tangent = radial.LengthSquared < 1e-6 ? new Vector(1, 0) : new Vector(-radial.Y, radial.X);
                tangent.Normalize();
                markers[i] = new LineSegmentOverlay((points[i] - tangent * markerHalfLength).ToPointD(), (points[i] + tangent * markerHalfLength).ToPointD());
            }

            return markers;
        }

        public static CaliperScoreOverlay[] BuildLinearScoreOverlays(IReadOnlyList<Point> invalidPoints, IReadOnlyList<Point> acceptedPoints, IReadOnlyList<double> acceptedScores, IReadOnlyList<Point> rejectedPoints, Vector labelDirection)
        {
            Vector labelOffset = NormalizeLabelOffset(labelDirection);
            List<CaliperScoreOverlay> overlays = new(invalidPoints.Count + acceptedPoints.Count + rejectedPoints.Count);

            foreach (Point point in invalidPoints)
            {
                overlays.Add(new CaliperScoreOverlay((point + labelOffset).ToPointD(), "N/A", CaliperOverlayStatus.Invalid));
            }

            for (int i = 0; i < Math.Min(acceptedPoints.Count, acceptedScores.Count); i++)
            {
                overlays.Add(new CaliperScoreOverlay((acceptedPoints[i] + labelOffset).ToPointD(), FormatScoreText(acceptedScores[i]), CaliperOverlayStatus.Valid));
            }

            foreach (Point point in rejectedPoints)
            {
                overlays.Add(new CaliperScoreOverlay((point + labelOffset).ToPointD(), "rej", CaliperOverlayStatus.Rejected));
            }

            return [.. overlays];
        }

        public static CaliperScoreOverlay[] BuildCircularScoreOverlays(IReadOnlyList<Point> invalidPoints, IReadOnlyList<Point> acceptedPoints, IReadOnlyList<double> acceptedScores, IReadOnlyList<Point> rejectedPoints, Point center)
        {
            List<CaliperScoreOverlay> overlays = new(invalidPoints.Count + acceptedPoints.Count + rejectedPoints.Count);

            foreach (Point point in invalidPoints)
            {
                overlays.Add(new CaliperScoreOverlay(GetCircularLabelPosition(point, center).ToPointD(), "N/A", CaliperOverlayStatus.Invalid));
            }

            for (int i = 0; i < Math.Min(acceptedPoints.Count, acceptedScores.Count); i++)
            {
                overlays.Add(new CaliperScoreOverlay(GetCircularLabelPosition(acceptedPoints[i], center).ToPointD(), FormatScoreText(acceptedScores[i]), CaliperOverlayStatus.Valid));
            }

            foreach (Point point in rejectedPoints)
            {
                overlays.Add(new CaliperScoreOverlay(GetCircularLabelPosition(point, center).ToPointD(), "rej", CaliperOverlayStatus.Rejected));
            }

            return [.. overlays];
        }

        public static CaliperScoreOverlay[] BuildDualEdgeScoreOverlays(IReadOnlyList<Point> invalidCenters, IReadOnlyList<Point> edge1Points, IReadOnlyList<Point> edge2Points, IReadOnlyList<double> edge1Scores, IReadOnlyList<double> edge2Scores, IReadOnlyList<Point> rejectedEdge1Points, IReadOnlyList<Point> rejectedEdge2Points, Vector labelDirection)
        {
            Vector labelOffset = NormalizeLabelOffset(labelDirection);
            List<CaliperScoreOverlay> overlays = new(invalidCenters.Count + edge1Points.Count + rejectedEdge1Points.Count);

            foreach (Point center in invalidCenters)
            {
                overlays.Add(new CaliperScoreOverlay((center + labelOffset).ToPointD(), "N/A", CaliperOverlayStatus.Invalid));
            }

            int validCount = new[] { edge1Points.Count, edge2Points.Count, edge1Scores.Count, edge2Scores.Count }.Min();
            for (int i = 0; i < validCount; i++)
            {
                Point midpoint = new((edge1Points[i].X + edge2Points[i].X) / 2, (edge1Points[i].Y + edge2Points[i].Y) / 2);
                overlays.Add(new CaliperScoreOverlay((midpoint + labelOffset).ToPointD(), FormatScoreText((edge1Scores[i] + edge2Scores[i]) / 2), CaliperOverlayStatus.Valid));
            }

            int rejectedCount = Math.Min(rejectedEdge1Points.Count, rejectedEdge2Points.Count);
            for (int i = 0; i < rejectedCount; i++)
            {
                Point midpoint = new((rejectedEdge1Points[i].X + rejectedEdge2Points[i].X) / 2, (rejectedEdge1Points[i].Y + rejectedEdge2Points[i].Y) / 2);
                overlays.Add(new CaliperScoreOverlay((midpoint + labelOffset).ToPointD(), "rej", CaliperOverlayStatus.Rejected));
            }

            return [.. overlays];
        }

        public static LineSegmentOverlay[] BuildDualEdgeCaliperRegionSegments(CaliperMeasureRoi line)
        {
            Vector measurementDirection = line.GetCaliperMeasurementDirection().ToWpfVector();
            Vector caliperDirection = new(-measurementDirection.Y, measurementDirection.X);
            Point center = line.CaliperCenter.ToWpfPoint();
            double halfSearchRange = line.CaliperSearchRange;
            double regionHalfLength = line.GetResolvedCaliperRegionLength() / 2;
            Point topLeft = center - measurementDirection * halfSearchRange - caliperDirection * regionHalfLength;
            Point topRight = center - measurementDirection * halfSearchRange + caliperDirection * regionHalfLength;
            Point bottomRight = center + measurementDirection * halfSearchRange + caliperDirection * regionHalfLength;
            Point bottomLeft = center + measurementDirection * halfSearchRange - caliperDirection * regionHalfLength;
            return
            [
                new LineSegmentOverlay(topLeft.ToPointD(), topRight.ToPointD()),
                new LineSegmentOverlay(topRight.ToPointD(), bottomRight.ToPointD()),
                new LineSegmentOverlay(bottomRight.ToPointD(), bottomLeft.ToPointD()),
                new LineSegmentOverlay(bottomLeft.ToPointD(), topLeft.ToPointD())
            ];
        }

        public static LineSegmentOverlay[] BuildDualEdgeCaliperBars(CaliperMeasureRoi line)
        {
            Vector measurementDirection = line.GetCaliperMeasurementDirection().ToWpfVector();
            Vector caliperDirection = new(-measurementDirection.Y, measurementDirection.X);
            Point center = line.CaliperCenter.ToWpfPoint();
            double halfSearchRange = line.CaliperSearchRange;
            double regionHalfLength = line.GetResolvedCaliperRegionLength() / 2;
            int caliperCount = Math.Clamp(line.CaliperCount, 3, 31);
            var segments = new LineSegmentOverlay[caliperCount];
            for (int i = 0; i < caliperCount; i++)
            {
                double lerp = caliperCount == 1 ? 0.5 : (double)i / (caliperCount - 1);
                double tangentOffset = -regionHalfLength + regionHalfLength * 2 * lerp;
                Point caliperCenter = center + caliperDirection * tangentOffset;
                segments[i] = new LineSegmentOverlay(
                    (caliperCenter - measurementDirection * halfSearchRange).ToPointD(),
                    (caliperCenter + measurementDirection * halfSearchRange).ToPointD());
            }

            return segments;
        }

        private static Vector NormalizeLabelOffset(Vector labelDirection)
        {
            Vector labelOffset = labelDirection;
            if (labelOffset.LengthSquared < 1e-6)
            {
                labelOffset = new Vector(0, -1);
            }

            labelOffset.Normalize();
            return labelOffset * 6;
        }

        private static string FormatScoreText(double score)
        {
            double normalizedScore = Math.Clamp(score / 255.0 * 100.0, 0, 100);
            return $"{normalizedScore:F0}%";
        }

        private static Point GetCircularLabelPosition(Point point, Point center)
        {
            Vector radial = point - center;
            if (radial.LengthSquared < 1e-6)
            {
                radial = new Vector(0, -1);
            }

            radial.Normalize();
            return point + radial * 8;
        }

        private static IEnumerable<LineSegmentOverlay> CreateCircleApproximationSegments(Point center, double radius, int segmentCount)
        {
            if (radius <= 0)
            {
                yield break;
            }

            int steps = Math.Max(24, segmentCount * 2);
            Point previous = default;
            Point first = default;
            for (int i = 0; i <= steps; i++)
            {
                double angle = i * Math.PI * 2 / steps;
                Point current = new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
                if (i == 0)
                {
                    first = current;
                }
                else
                {
                    yield return new LineSegmentOverlay(previous.ToPointD(), current.ToPointD());
                }

                previous = current;
            }

            yield return new LineSegmentOverlay(previous.ToPointD(), first.ToPointD());
        }

        private static IEnumerable<LineSegmentOverlay> CreateArcApproximationSegments(Point center, double radius, double startAngle, double sweepAngle, int segmentCount)
        {
            if (radius <= 0)
            {
                yield break;
            }

            int steps = Math.Max(8, Math.Abs(segmentCount));
            Point? previous = null;
            for (int i = 0; i <= steps; i++)
            {
                double angle = (startAngle + sweepAngle * i / steps) * Math.PI / 180.0;
                Point current = new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
                if (previous != null)
                {
                    yield return new LineSegmentOverlay(previous.Value.ToPointD(), current.ToPointD());
                }

                previous = current;
            }
        }
    }
}
