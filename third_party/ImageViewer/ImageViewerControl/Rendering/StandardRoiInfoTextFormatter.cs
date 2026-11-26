using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Rendering
{
    internal static class StandardRoiInfoTextFormatter
    {
        public static string BuildRotatedRectText(RotatedRect rect, RoiRenderContext context)
        {
            var measurement = context.Measure(rect);
            return BuildMultiline(
                rect.Label,
                $"W:{measurement.FormatLength(rect.Width)} H:{measurement.FormatLength(rect.Height)} Area:{measurement.FormatArea(rect.Width * rect.Height)} Angle:{rect.Angle:F1}°");
        }

        public static string BuildBlobAnalysisText(BlobAnalysisRoi roi)
        {
            return BuildMultiline(
                roi.Label,
                $"Blobs:{roi.DetectedBlobs.Count} Total:{roi.DetectedBlobs.Sum(blob => blob.Area)}px Angle:{roi.Angle:F1}°",
                roi.DetectedBlobs.Count > 0 ? $"Max:{roi.DetectedBlobs.Max(blob => blob.Area)}px MinArea:{roi.MinArea}px" : $"MinArea:{roi.MinArea}px");
        }

        public static string BuildFittedEllipseText(FittedEllipseRoi ellipse, RoiRenderContext context)
        {
            if (!ellipse.HasFitEvidence)
            {
                return BuildEllipseText(ellipse, context);
            }

            var measurement = context.Measure(ellipse);
            return BuildMultiline(ellipse.Label, $"Fit RX:{measurement.FormatLength(ellipse.RadiusX)} RY:{measurement.FormatLength(ellipse.RadiusY)} Pts:{ellipse.SourcePointCount}");
        }

        public static string BuildRingText(RingRoi ring, RoiRenderContext context)
        {
            var measurement = context.Measure(ring);
            return BuildMultiline(ring.Label, $"Inner:{measurement.FormatLength(ring.InnerRadius)} Outer:{measurement.FormatLength(ring.OuterRadius)}");
        }

        public static string BuildEllipseText(EllipseRoi ellipse, RoiRenderContext context)
        {
            var measurement = context.Measure(ellipse);
            return BuildMultiline(
                ellipse.Label,
                $"RX:{measurement.FormatLength(ellipse.RadiusX)} RY:{measurement.FormatLength(ellipse.RadiusY)} Area:{measurement.FormatArea(Math.PI * ellipse.RadiusX * ellipse.RadiusY)} Angle:{ellipse.Angle:F1}°");
        }

        public static string BuildCircleText(CircleRoi circle, RoiRenderContext context)
        {
            var measurement = context.Measure(circle);
            return BuildMultiline(circle.Label, $"R:{measurement.FormatPrimary()} Area:{measurement.FormatArea(Math.PI * circle.Radius * circle.Radius)}");
        }

        public static string BuildPolygonText(PolygonRoi polygon, (double Area, double Perimeter, Point Centroid) metrics, RoiRenderContext context)
        {
            var measurement = context.Measure(polygon);
            return BuildMultiline(polygon.Label, $"Area:{measurement.FormatArea(metrics.Area)} Peri:{measurement.FormatLength(metrics.Perimeter)}", $"Vertices:{polygon.Points.Count}");
        }

        public static string BuildPolylineText(PolylineRoi polyline, RoiRenderContext context)
        {
            IReadOnlyList<Point> points = polyline.Points.ToWpfPointArray();
            IReadOnlyList<double> segments = GeometryUtils.GetPolylineSegmentLengths(points);
            if (segments.Count == 0)
            {
                return polyline.Label ?? string.Empty;
            }

            var measurement = context.Measure(polyline);
            return BuildMultiline(
                polyline.Label,
                $"Length:{measurement.FormatLength(GeometryUtils.PolylineLength(points))}",
                $"Segments:{segments.Count} Min:{measurement.FormatLength(segments.Min())} Max:{measurement.FormatLength(segments.Max())}");
        }

        public static string BuildPointAnnotationText(PointAnnotationRoi annotation)
        {
            return annotation.Label ?? string.Empty;
        }

        public static string BuildPointCoordinateText(PointCoordinateMeasureRoi point)
        {
            string coordinates = $"X:{point.Position.X:F1} Y:{point.Position.Y:F1}";
            if (!point.IsEdgeSnapped)
            {
                return BuildInline(point.Label, coordinates);
            }

            return BuildMultiline(point.Label, coordinates, $"Edge:{point.EdgeConfidence * 100:F0}%");
        }

        public static string BuildTextAnnotationText(TextAnnotationRoi annotation)
        {
            return annotation.Label ?? string.Empty;
        }

        public static string BuildArrowAnnotationText(ArrowAnnotationRoi arrow)
        {
            return arrow.Label ?? string.Empty;
        }

        public static string BuildLineMeasureText(LineMeasureRoi line, RoiRenderContext context)
        {
            return BuildInline(line.Label, $"长度：{context.Measure(line).FormatPrimary()}");
        }

        public static string BuildAngleMeasureText(AngleMeasureRoi angle, double angleValue)
        {
            return BuildInline(angle.Label, $"{angleValue:F1}°");
        }

        public static string BuildArcMeasureText(ArcMeasureRoi arc, RoiRenderContext context)
        {
            var measurement = context.Measure(arc);
            return BuildMultiline(
                arc.Label,
                $"R:{measurement.FormatPrimary()} Arc:{measurement.FormatLength(arc.ArcLength)} Angle:{arc.CentralAngle:F1}°");
        }

        public static string BuildPointToLineDistanceText(PointToLineDistanceRoi roi, RoiRenderContext context)
        {
            return BuildMultiline(roi.Label, $"Distance: {context.Measure(roi).FormatLength(roi.Distance)}");
        }

        public static string BuildPointToCircleDistanceText(PointToCircleDistanceRoi roi, RoiRenderContext context)
        {
            var measurement = context.Measure(roi);
            return BuildMultiline(
                roi.Label,
                $"To Circle: {measurement.FormatLength(roi.DistanceToCircle)}",
                $"To Center: {measurement.FormatLength(roi.DistanceToCenter)}");
        }

        public static string BuildParallelismText(ParallelismMeasureRoi roi, RoiRenderContext context)
        {
            return BuildMultiline(
                roi.Label,
                $"Angle Diff: {roi.AngleDifference:F2}°",
                $"Avg Dist: {context.Measure(roi).FormatLength(roi.AverageDistance)}");
        }

        public static string BuildPerpendicularityText(PerpendicularityMeasureRoi roi)
        {
            return BuildMultiline(
                roi.Label,
                $"Angle: {roi.AngleBetweenLines:F1}°",
                $"Error: {roi.PerpendicularityError:F2}°");
        }

        public static string BuildConcentricityText(ConcentricityMeasureRoi roi, RoiRenderContext context)
        {
            var measurement = context.Measure(roi);
            return BuildMultiline(
                roi.Label,
                $"公差带直径：{measurement.FormatPrimary()}",
                $"Center Dist: {measurement.FormatLength(roi.CenterDistance)}",
                $"R1: {measurement.FormatLength(roi.Radius1)} R2: {measurement.FormatLength(roi.Radius2)}");
        }

        public static string BuildCenterDistanceText(CenterDistanceMeasureRoi roi, RoiRenderContext context)
        {
            return BuildInline(roi.Label, $"中心距离：{context.Measure(roi).FormatPrimary()}");
        }

        public static string BuildThreePointCircleText(ThreePointCircleMeasureRoi roi, RoiRenderContext context)
        {
            return roi.IsValid
                ? BuildMultiline(roi.Label, $"R:{context.Measure(roi).FormatPrimary()} 3 Points")
                : BuildInline(roi.Label, "Invalid 3-point circle");
        }

        private static string BuildInline(string? label, string content)
        {
            return string.IsNullOrWhiteSpace(label) ? content : $"{label}: {content}";
        }

        private static string BuildMultiline(string? label, params string[] lines)
        {
            string content = string.Join(Environment.NewLine, lines);
            return string.IsNullOrWhiteSpace(label) ? content : $"{label}{Environment.NewLine}{content}";
        }
    }
}
