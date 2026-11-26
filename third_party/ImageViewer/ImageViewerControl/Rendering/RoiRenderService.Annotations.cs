using System;
using System.Windows;
using System.Windows.Media;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Rendering
{
    public sealed partial class RoiRenderService
    {
        private sealed class PointAnnotationRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is PointAnnotationRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var annotation = (PointAnnotationRoi)roi;
                if (!annotation.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, annotation.StrokeColor);
                double size = (isSelected ? context.PointAnnotationSize + 2 : context.PointAnnotationSize) / context.Scale;
                context.DrawHandle(annotation.Position.ToWpfPoint(), ResizeHandle.None, size, false, brush);
                if (!string.IsNullOrWhiteSpace(annotation.Label))
                {
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildPointAnnotationText(annotation), StandardRoiLayoutHelper.GetAnnotationInfoAnchor(annotation.Position.ToWpfPoint(), context), brush);
                }
            }
        }

        private sealed class PointCoordinateMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is PointCoordinateMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var point = (PointCoordinateMeasureRoi)roi;
                if (!point.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, point.StrokeColor);
                Point position = point.Position.ToWpfPoint();
                double arm = (isSelected ? context.PointAnnotationSize + 4 : context.PointAnnotationSize + 2) / context.Scale;
                double thickness = Math.Max(1, point.StrokeThickness) / context.Scale;
                context.DrawLineSegment(new Point(position.X - arm, position.Y), new Point(position.X + arm, position.Y), brush, thickness);
                context.DrawLineSegment(new Point(position.X, position.Y - arm), new Point(position.X, position.Y + arm), brush, thickness);
                context.DrawHandle(position, ResizeHandle.None, (context.PointAnnotationSize + 1) / context.Scale, false, brush);
                context.DrawInfoText(
                    StandardRoiInfoTextFormatter.BuildPointCoordinateText(point),
                    StandardRoiLayoutHelper.GetAnnotationInfoAnchor(position, context),
                    brush);
            }
        }

        private sealed class TextAnnotationRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is TextAnnotationRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var annotation = (TextAnnotationRoi)roi;
                if (!annotation.IsVisible || string.IsNullOrWhiteSpace(annotation.Label)) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, annotation.StrokeColor);
                context.DrawHandle(annotation.Position.ToWpfPoint(), ResizeHandle.None, context.PointAnnotationSize / context.Scale, false, brush);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildTextAnnotationText(annotation), StandardRoiLayoutHelper.GetAnnotationInfoAnchor(annotation.Position.ToWpfPoint(), context), brush);
                if (isSelected)
                {
                    context.DrawHandle(annotation.Position.ToWpfPoint(), ResizeHandle.None, (context.PointAnnotationSize + 2) / context.Scale, true, brush);
                }
            }
        }

        private sealed class ArrowAnnotationRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is ArrowAnnotationRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var arrow = (ArrowAnnotationRoi)roi;
                if (!arrow.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, arrow.StrokeColor);
                double thickness = (isSelected ? 3 : arrow.StrokeThickness) / context.Scale;
                Point arrowP1 = arrow.P1.ToWpfPoint();
                Point arrowP2 = arrow.P2.ToWpfPoint();
                context.DrawLineSegment(arrowP1, arrowP2, brush, thickness);

                Vector direction = arrowP1 - arrowP2;
                if (direction.LengthSquared > 1e-6)
                {
                    direction.Normalize();
                    Vector normal = new(-direction.Y, direction.X);
                    double headLength = arrow.ArrowHeadLength / context.Scale;
                    Point left = arrowP2 + direction * headLength + normal * (headLength * 0.45);
                    Point right = arrowP2 + direction * headLength - normal * (headLength * 0.45);
                    context.DrawLineSegment(arrowP2, left, brush, thickness);
                    context.DrawLineSegment(arrowP2, right, brush, thickness);
                }

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(arrowP1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(arrowP2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);

                if (!string.IsNullOrWhiteSpace(arrow.Label))
                {
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildArrowAnnotationText(arrow), StandardRoiLayoutHelper.GetMidpointInfoAnchor(arrowP1, arrowP2), brush, true);
                }
            }
        }
    }
}
