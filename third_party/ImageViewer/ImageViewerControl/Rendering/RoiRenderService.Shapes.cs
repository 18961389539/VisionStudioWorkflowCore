using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Rendering
{
    public sealed partial class RoiRenderService
    {
        private sealed class RotatedRectRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is RotatedRect;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var rect = (RotatedRect)roi;
                if (!rect.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, rect.StrokeColor);
                context.DrawRectangleOutline(rect.Center.ToWpfPoint(), rect.Width, rect.Height, rect.Angle, brush, (isSelected ? 3 : rect.StrokeThickness) / context.Scale, RoiRenderContext.CreateTranslucentFill(brush));

                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildRotatedRectText(rect, context), StandardRoiLayoutHelper.GetTopInfoAnchor(rect.Center.ToWpfPoint(), rect.Height / 2, context), brush, true);

                if (isSelected)
                {
                    StandardRoiLayoutHelper.DrawRotatedBoxHandles(context, rect.Center.ToWpfPoint(), rect.Width / 2, rect.Height / 2, rect.Angle);
                }
            }
        }

        private sealed class BlobAnalysisRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is BlobAnalysisRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var blobRoi = (BlobAnalysisRoi)roi;
                if (!blobRoi.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, blobRoi.StrokeColor);
                context.DrawRectangleOutline(blobRoi.Center.ToWpfPoint(), blobRoi.Width, blobRoi.Height, blobRoi.Angle, brush, (isSelected ? 3 : blobRoi.StrokeThickness) / context.Scale, RoiRenderContext.CreateTranslucentFill(brush));
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildBlobAnalysisText(blobRoi), StandardRoiLayoutHelper.GetTopInfoAnchor(blobRoi.Center.ToWpfPoint(), blobRoi.Height / 2, context), brush, true);

                if (isSelected)
                {
                    StandardRoiLayoutHelper.DrawRotatedBoxHandles(context, blobRoi.Center.ToWpfPoint(), blobRoi.Width / 2, blobRoi.Height / 2, blobRoi.Angle);
                }

                Brush blobBrush = Brushes.Magenta;
                double blobThickness = 1.0 / context.Scale;
                foreach (var blob in blobRoi.DetectedBlobs)
                {
                    if (blob.Contour is { Length: >= 3 } contour)
                    {
                        // Traced pixel boundary, so a slanted blob reads as its real shape rather
                        // than an axis-aligned box that misleadingly looks like the search region.
                        var points = new Point[contour.Length];
                        for (int i = 0; i < contour.Length; i++)
                        {
                            points[i] = contour[i].ToWpfPoint();
                        }

                        context.DrawPolyline(points, blobBrush, blobThickness, closed: true);
                    }
                    else
                    {
                        context.DrawRectangleOutline(
                            new Point(blob.BoundingBox.X + blob.BoundingBox.Width / 2, blob.BoundingBox.Y + blob.BoundingBox.Height / 2),
                            blob.BoundingBox.Width,
                            blob.BoundingBox.Height,
                            0,
                            blobBrush,
                            blobThickness);
                    }

                    context.DrawHandle(blob.Centroid.ToWpfPoint(), ResizeHandle.None, 2 / context.Scale, false, blobBrush);

                    if (isSelected)
                    {
                        context.DrawInfoText(
                            $"#{blob.Label} A:{blob.Area} V:{blob.Contour?.Length ?? 0}",
                            new Point(blob.BoundingBox.X, blob.BoundingBox.Y),
                            blobBrush);
                    }
                }
            }
        }

        private sealed class FittedEllipseRoiRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is FittedEllipseRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var ellipse = (FittedEllipseRoi)roi;
                if (!ellipse.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, ellipse.StrokeColor);
                context.DrawEllipseOutline(ellipse.Center.ToWpfPoint(), ellipse.RadiusX, ellipse.RadiusY, ellipse.Angle, brush, (isSelected ? 3 : ellipse.StrokeThickness) / context.Scale, RoiRenderContext.CreateTranslucentFill(brush));
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildFittedEllipseText(ellipse, context), StandardRoiLayoutHelper.GetTopInfoAnchor(ellipse.Center.ToWpfPoint(), ellipse.RadiusY, context), brush, true);

                if (isSelected)
                {
                    StandardRoiLayoutHelper.DrawRotatedBoxHandles(context, ellipse.Center.ToWpfPoint(), ellipse.RadiusX, ellipse.RadiusY, ellipse.Angle);
                }
            }
        }

        private sealed class RingRoiRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is RingRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var ring = (RingRoi)roi;
                if (!ring.IsVisible || ring.OuterRadius <= 0) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, ring.StrokeColor);
                double thickness = (isSelected ? 3 : ring.StrokeThickness) / context.Scale;
                Point center = context.ToScreenPoint(ring.Center.ToWpfPoint());
                double outer = context.ToScreenLength(ring.OuterRadius);
                double inner = context.ToScreenLength(ring.InnerRadius);

                var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
                geometry.Figures.Add(new EllipseGeometry(center, outer, outer).GetOutlinedPathGeometry().Figures[0]);
                if (inner > 0)
                {
                    geometry.Figures.Add(new EllipseGeometry(center, inner, inner).GetOutlinedPathGeometry().Figures[0]);
                }

                var path = new Path
                {
                    Data = geometry,
                    Fill = RoiRenderContext.CreateTranslucentFill(brush),
                    Stroke = brush,
                    StrokeThickness = Math.Max(1, context.ToScreenLength(thickness)),
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                context.ScreenOverlayCanvas.Children.Add(path);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildRingText(ring, context), StandardRoiLayoutHelper.GetTopInfoAnchor(ring.Center.ToWpfPoint(), ring.OuterRadius, context), brush, true);

                if (isSelected)
                {
                    StandardRoiLayoutHelper.DrawCircleHandles(context, ring.Center.ToWpfPoint(), ring.OuterRadius);
                    context.DrawHandle(new Point(ring.Center.X + ring.InnerRadius, ring.Center.Y), ResizeHandle.P1, context.HandleSize / context.Scale, false, brush);
                }
            }
        }

        private sealed class EllipseRoiRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is EllipseRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var ellipse = (EllipseRoi)roi;
                if (!ellipse.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, ellipse.StrokeColor);
                context.DrawEllipseOutline(ellipse.Center.ToWpfPoint(), ellipse.RadiusX, ellipse.RadiusY, ellipse.Angle, brush, (isSelected ? 3 : ellipse.StrokeThickness) / context.Scale, RoiRenderContext.CreateTranslucentFill(brush));
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildEllipseText(ellipse, context), StandardRoiLayoutHelper.GetTopInfoAnchor(ellipse.Center.ToWpfPoint(), ellipse.RadiusY, context), brush, true);

                if (isSelected)
                {
                    StandardRoiLayoutHelper.DrawRotatedBoxHandles(context, ellipse.Center.ToWpfPoint(), ellipse.RadiusX, ellipse.RadiusY, ellipse.Angle);
                }
            }
        }

        private sealed class CircleRoiRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is CircleRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var circle = (CircleRoi)roi;
                if (!circle.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, circle.StrokeColor);
                context.DrawEllipseOutline(circle.Center.ToWpfPoint(), circle.Radius, circle.Radius, 0, brush, (isSelected ? 3 : circle.StrokeThickness) / context.Scale, RoiRenderContext.CreateTranslucentFill(brush));
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildCircleText(circle, context), StandardRoiLayoutHelper.GetTopInfoAnchor(circle.Center.ToWpfPoint(), circle.Radius, context), brush, true);

                if (isSelected)
                {
                    StandardRoiLayoutHelper.DrawCircleHandles(context, circle.Center.ToWpfPoint(), circle.Radius);
                }
            }
        }

        private sealed class PolygonRoiRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is PolygonRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var poly = (PolygonRoi)roi;
                if (!poly.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, poly.StrokeColor);
                if (poly.Points.Count > 1)
                {
                    context.DrawPolyline(poly.Points.ToWpfPointArray(), brush, (isSelected ? 3 : poly.StrokeThickness) / context.Scale, closed: true, fill: RoiRenderContext.CreateTranslucentFill(brush));

                    if (!poly.IsClosed && poly.Points.Count > 2)
                    {
                        context.DrawLineSegment(poly.Points.Last().ToWpfPoint(), poly.Points.First().ToWpfPoint(), brush, 1 / context.Scale, new DoubleCollection { 4, 4 });
                    }
                }

                if (!isSelected)
                {
                    foreach (var point in poly.Points)
                    {
                        context.DrawDot(point.ToWpfPoint(), 4 / context.Scale, brush);
                    }
                }

                if (poly.IsClosed)
                {
                    var metrics = GeometryUtils.GetPolygonMetrics(poly.Points.ToWpfPointArray());
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildPolygonText(poly, metrics, context), StandardRoiLayoutHelper.GetPolygonInfoAnchor(metrics), brush, true);
                }

                if (isSelected)
                {
                    double handleSize = (context.HandleSize + context.PolygonResizeHandlePadding) / context.Scale;
                    foreach (var point in poly.Points)
                    {
                        context.DrawHandle(point.ToWpfPoint(), ResizeHandle.Vertex, handleSize, false);
                    }
                }
            }
        }

        private sealed class PolylineRoiRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is PolylineRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var polyline = (PolylineRoi)roi;
                if (!polyline.IsVisible || polyline.Points.Count == 0) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, polyline.StrokeColor);
                context.DrawPolyline(polyline.Points.ToWpfPointArray(), brush, (isSelected ? 3 : polyline.StrokeThickness) / context.Scale);

                foreach (var point in polyline.Points)
                {
                    context.DrawHandle(
                        point.ToWpfPoint(),
                        isSelected ? ResizeHandle.Vertex : ResizeHandle.None,
                        isSelected ? context.HandleSize / context.Scale : context.PointAnnotationSize / context.Scale,
                        false,
                        brush);
                }

                if (polyline.Points.Count > 1)
                {
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildPolylineText(polyline, context), StandardRoiLayoutHelper.GetPolylineInfoAnchor(polyline.Points.ToWpfPointArray()), brush, true);
                }
            }
        }
    }
}
