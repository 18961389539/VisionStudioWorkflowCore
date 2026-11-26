using System.Windows;
using System.Windows.Media;
using ImageViewer.Controls;
using ImageViewer.Utils;

namespace ImageViewer.Rendering
{
    internal static class StandardRoiLayoutHelper
    {
        public static Point GetTopInfoAnchor(Point center, double verticalRadius, RoiRenderContext context)
        {
            return new Point(center.X, center.Y - verticalRadius - context.InfoTextOffset / context.Scale);
        }

        public static void DrawRotatedBoxHandles(RoiRenderContext context, Point center, double halfWidth, double halfHeight, double angle)
        {
            double handleSize = context.HandleSize / context.Scale;
            var handlePositions = RoiRenderContext.CreateBoxHandlePositions(center, halfWidth, halfHeight);
            var rotateTransform = new RotateTransform(angle, center.X, center.Y);

            foreach (var kvp in handlePositions)
            {
                context.DrawHandle(rotateTransform.Transform(kvp.Value), kvp.Key, handleSize, true);
            }

            context.DrawRotationHandle(handlePositions[ResizeHandle.TopCenter], rotateTransform, handleSize);
        }

        public static void DrawCircleHandles(RoiRenderContext context, Point center, double radius, Brush? stroke = null)
        {
            double handleSize = context.HandleSize / context.Scale;
            foreach (var kvp in RoiRenderContext.CreateBoxHandlePositions(center, radius, radius))
            {
                context.DrawHandle(kvp.Value, kvp.Key, handleSize, true, stroke);
            }
        }

        public static Point GetAnnotationInfoAnchor(Point position, RoiRenderContext context)
        {
            return new Point(position.X + 6 / context.Scale, position.Y - 6 / context.Scale);
        }

        public static Point GetMidpointInfoAnchor(Point p1, Point p2)
        {
            return new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
        }

        public static Point GetPolygonInfoAnchor((double Area, double Perimeter, Point Centroid) metrics)
        {
            return metrics.Centroid;
        }

        public static Point GetPolylineInfoAnchor(System.Collections.Generic.IReadOnlyList<Point> points)
        {
            return GeometryUtils.GetCentroid(points);
        }
    }
}
