using System;
using System.Collections.Generic;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    public static class RoiGeometryService
    {
        public static bool IsRingDrawable(double outerRadius, double minimumDrawableSize)
        {
            return outerRadius > minimumDrawableSize;
        }

        public static double GetAdjustedRingInnerRadius(double outerRadius, double innerRadius, double minimumDrawableSize)
        {
            return Math.Min(innerRadius, Math.Max(0, outerRadius - minimumDrawableSize));
        }

        public static double ClampRingInnerRadius(double candidateInnerRadius, double outerRadius, double minimumDrawableSize)
        {
            return Math.Min(candidateInnerRadius, Math.Max(0, outerRadius - minimumDrawableSize));
        }

        public static bool ShouldClosePolygon(Point currentPos, Point startPos, double hitTestTolerance, double scale)
        {
            return GeometryUtils.Distance(currentPos, startPos) < (hitTestTolerance * 2) / scale;
        }

        public static bool ShouldAppendFreehandPolylinePoint(IReadOnlyList<Point> points, Point currentPos, double minimumDistance = 1)
        {
            return points.Count == 0 || GeometryUtils.Distance(points[points.Count - 1], currentPos) >= minimumDistance;
        }

        public static Rect GetBounds(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);

            return roi switch
            {
                CaliperMeasureRoi caliper => GeometryUtils.GetBoundingBox(BuildCaliperCorners(caliper)),
                RotatedRect rect => GeometryUtils.GetBoundingBox(new[]
                {
                    GeometryUtils.RotatePoint(new Point(rect.Center.X - rect.Width / 2, rect.Center.Y - rect.Height / 2), rect.Center.ToWpfPoint(), rect.Angle),
                    GeometryUtils.RotatePoint(new Point(rect.Center.X + rect.Width / 2, rect.Center.Y - rect.Height / 2), rect.Center.ToWpfPoint(), rect.Angle),
                    GeometryUtils.RotatePoint(new Point(rect.Center.X + rect.Width / 2, rect.Center.Y + rect.Height / 2), rect.Center.ToWpfPoint(), rect.Angle),
                    GeometryUtils.RotatePoint(new Point(rect.Center.X - rect.Width / 2, rect.Center.Y + rect.Height / 2), rect.Center.ToWpfPoint(), rect.Angle)
                }),
                EllipseRoi ellipse => GeometryUtils.GetBoundingBox(new[]
                {
                    GeometryUtils.RotatePoint(new Point(ellipse.Center.X - ellipse.RadiusX, ellipse.Center.Y - ellipse.RadiusY), ellipse.Center.ToWpfPoint(), ellipse.Angle),
                    GeometryUtils.RotatePoint(new Point(ellipse.Center.X + ellipse.RadiusX, ellipse.Center.Y - ellipse.RadiusY), ellipse.Center.ToWpfPoint(), ellipse.Angle),
                    GeometryUtils.RotatePoint(new Point(ellipse.Center.X + ellipse.RadiusX, ellipse.Center.Y + ellipse.RadiusY), ellipse.Center.ToWpfPoint(), ellipse.Angle),
                    GeometryUtils.RotatePoint(new Point(ellipse.Center.X - ellipse.RadiusX, ellipse.Center.Y + ellipse.RadiusY), ellipse.Center.ToWpfPoint(), ellipse.Angle)
                }),
                CircleRoi circle => new Rect(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius, circle.Radius * 2, circle.Radius * 2),
                PolygonRoi polygon when polygon.Points.Count > 0 => GeometryUtils.GetBoundingBox(polygon.Points.ToWpfPoints()),
                PolylineRoi polyline when polyline.Points.Count > 0 => GeometryUtils.GetBoundingBox(polyline.Points.ToWpfPoints()),
                PointAnnotationRoi point => new Rect(point.Position.X, point.Position.Y, 1, 1),
                PointCoordinateMeasureRoi pointCoordinate => new Rect(pointCoordinate.Position.X, pointCoordinate.Position.Y, 1, 1),
                TextAnnotationRoi text => new Rect(text.Position.X, text.Position.Y, 1, 1),
                LineMeasureRoi line => GeometryUtils.GetBoundingBox(new[] { line.P1.ToWpfPoint(), line.P2.ToWpfPoint() }),
                AngleMeasureRoi angle => GeometryUtils.GetBoundingBox(new[] { angle.P1.ToWpfPoint(), angle.Vertex.ToWpfPoint(), angle.P2.ToWpfPoint() }),
                ArcMeasureRoi arc => GeometryUtils.GetBoundingBox(new[] { arc.StartPoint.ToWpfPoint(), arc.EndPoint.ToWpfPoint(), arc.ArcPoint.ToWpfPoint() }),
                CenterDistanceMeasureRoi centerDistance => GeometryUtils.GetBoundingBox(new[] { centerDistance.Center1.ToWpfPoint(), centerDistance.Center2.ToWpfPoint() }),
                ThreePointCircleMeasureRoi threePointCircle when threePointCircle.IsValid => new Rect(threePointCircle.Center.X - threePointCircle.Radius, threePointCircle.Center.Y - threePointCircle.Radius, threePointCircle.Radius * 2, threePointCircle.Radius * 2),
                ThreePointCircleMeasureRoi threePointCircle => GeometryUtils.GetBoundingBox(new[] { threePointCircle.P1.ToWpfPoint(), threePointCircle.P2.ToWpfPoint(), threePointCircle.P3.ToWpfPoint() }),
                _ => Rect.Empty
            };
        }

        public static IEnumerable<Point> GetRepresentativePoints(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);

            return roi switch
            {
                CaliperMeasureRoi caliper => BuildCaliperCorners(caliper),
                RotatedRect rect => new[] { rect.Center.ToWpfPoint() },
                EllipseRoi ellipse => new[] { ellipse.Center.ToWpfPoint() },
                CircleRoi circle => new[] { circle.Center.ToWpfPoint() },
                PolygonRoi polygon => polygon.Points.ToWpfPoints(),
                PolylineRoi polyline => polyline.Points.ToWpfPoints(),
                PointAnnotationRoi point => new[] { point.Position.ToWpfPoint() },
                PointCoordinateMeasureRoi pointCoordinate => new[] { pointCoordinate.Position.ToWpfPoint() },
                TextAnnotationRoi text => new[] { text.Position.ToWpfPoint() },
                LineMeasureRoi line => new[] { line.P1.ToWpfPoint(), line.P2.ToWpfPoint() },
                AngleMeasureRoi angle => new[] { angle.P1.ToWpfPoint(), angle.Vertex.ToWpfPoint(), angle.P2.ToWpfPoint() },
                ArcMeasureRoi arc => new[] { arc.StartPoint.ToWpfPoint(), arc.EndPoint.ToWpfPoint(), arc.ArcPoint.ToWpfPoint() },
                CenterDistanceMeasureRoi centerDistance => new[] { centerDistance.Center1.ToWpfPoint(), centerDistance.Center2.ToWpfPoint() },
                ThreePointCircleMeasureRoi threePointCircle => new[] { threePointCircle.P1.ToWpfPoint(), threePointCircle.P2.ToWpfPoint(), threePointCircle.P3.ToWpfPoint() },
                _ => Array.Empty<Point>()
            };
        }

        private static Point[] BuildCaliperCorners(CaliperMeasureRoi caliper)
        {
            caliper.EnsureCaliperRegion();
            Vector measurementDirection = caliper.GetCaliperMeasurementDirection().ToWpfVector();
            Vector caliperDirection = new(-measurementDirection.Y, measurementDirection.X);
            Point center = caliper.CaliperCenter.ToWpfPoint();
            double halfSearchRange = caliper.CaliperSearchRange;
            double regionHalfLength = caliper.GetResolvedCaliperRegionLength() / 2;
            return
            [
                center - measurementDirection * halfSearchRange - caliperDirection * regionHalfLength,
                center - measurementDirection * halfSearchRange + caliperDirection * regionHalfLength,
                center + measurementDirection * halfSearchRange + caliperDirection * regionHalfLength,
                center + measurementDirection * halfSearchRange - caliperDirection * regionHalfLength
            ];
        }
    }
}
