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
        private sealed class LineMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is LineMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var line = (LineMeasureRoi)roi;
                if (!line.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, line.StrokeColor);
                Point lineP1 = line.P1.ToWpfPoint();
                Point lineP2 = line.P2.ToWpfPoint();
                context.DrawLineSegment(lineP1, lineP2, brush, (isSelected ? 3 : line.StrokeThickness) / context.Scale);

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(lineP1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(lineP2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildLineMeasureText(line, context), new Point((line.P1.X + line.P2.X) / 2, (line.P1.Y + line.P2.Y) / 2), brush, true);
            }
        }

        private sealed class AngleMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is AngleMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var angle = (AngleMeasureRoi)roi;
                if (!angle.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, angle.StrokeColor);
                Point angleP1 = angle.P1.ToWpfPoint();
                Point angleVertex = angle.Vertex.ToWpfPoint();
                Point angleP2 = angle.P2.ToWpfPoint();
                if (angle.P1 == angle.Vertex)
                {
                    context.DrawLineSegment(angleP1, angleVertex, brush, angle.StrokeThickness / context.Scale);
                }
                else
                {
                    context.DrawLineSegment(angleP1, angleVertex, brush, angle.StrokeThickness / context.Scale);
                    context.DrawLineSegment(angleVertex, angleP2, brush, angle.StrokeThickness / context.Scale);

                    double angleValue = GeometryUtils.SmallestAngle(angleP1, angleVertex, angleP2);
                    double radius = context.AngleArcRadius;
                    context.DrawAngleArc(angleVertex, angleP1, angleP2, radius, brush);

                    Vector v1 = angleP1 - angleVertex;
                    Vector v2 = angleP2 - angleVertex;
                    v1.Normalize();
                    v2.Normalize();
                    Vector vMid = v1 + v2;
                    if (vMid.LengthSquared < 0.0001)
                    {
                        vMid = new Vector(-v1.Y, v1.X);
                    }
                    else
                    {
                        vMid.Normalize();
                    }

                    Point textPos = angleVertex + vMid * ((radius + 10) / context.Scale);
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildAngleMeasureText(angle, angleValue), textPos, brush, true);
                }

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(angleP1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(angleVertex, isSelected ? ResizeHandle.Vertex : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(angleP2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class ArcMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is ArcMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var arc = (ArcMeasureRoi)roi;
                if (!arc.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, arc.StrokeColor);
                double thickness = (isSelected ? 3 : arc.StrokeThickness) / context.Scale;
                Point arcCenter = arc.Center.ToWpfPoint();
                Point arcStartPoint = arc.StartPoint.ToWpfPoint();
                Point arcEndPoint = arc.EndPoint.ToWpfPoint();
                Point arcPoint = arc.ArcPoint.ToWpfPoint();

                if (arc.IsValid)
                {
                    context.DrawArc(arcCenter, arc.Radius, arc.StartAngle, arc.SweepAngle, brush, thickness);
                    context.DrawCircleOutline(arcCenter, 3 / context.Scale, brush, 1 / context.Scale);

                    double midAngle = arc.StartAngle + arc.SweepAngle / 2;
                    double radians = midAngle * Math.PI / 180.0;
                    Point textPos = new(
                        arc.Center.X + (arc.Radius + 15 / context.Scale) * Math.Cos(radians),
                        arc.Center.Y + (arc.Radius + 15 / context.Scale) * Math.Sin(radians));
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildArcMeasureText(arc, context), textPos, brush, true);
                }
                else
                {
                    context.DrawLineSegment(arcStartPoint, arcEndPoint, brush, thickness);
                    context.DrawDot(arcPoint, 4 / context.Scale, brush);
                    context.DrawInfoText("无效圆弧", arcPoint, brush, true);
                }

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(arcStartPoint, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(arcEndPoint, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(arcPoint, isSelected ? ResizeHandle.Vertex : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class PointToLineDistanceRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is PointToLineDistanceRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var p2l = (PointToLineDistanceRoi)roi;
                if (!p2l.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, p2l.StrokeColor);
                double thickness = (isSelected ? 3 : p2l.StrokeThickness) / context.Scale;

                Point p2lLineP1 = p2l.LineP1.ToWpfPoint();
                Point p2lLineP2 = p2l.LineP2.ToWpfPoint();
                Point p2lPoint = p2l.Point.ToWpfPoint();
                Point foot = p2l.FootPoint.ToWpfPoint();
                context.DrawLineSegment(p2lLineP1, p2lLineP2, brush, thickness);
                context.DrawLineSegment(p2lPoint, foot, brush, thickness * 0.6);
                context.DrawDot(foot, 3 / context.Scale, brush);

                Point textPos = new((p2lPoint.X + foot.X) / 2, (p2lPoint.Y + foot.Y) / 2 - 10 / context.Scale);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildPointToLineDistanceText(p2l, context), textPos, brush, true);

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(p2lPoint, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(p2lLineP1, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(p2lLineP2, isSelected ? ResizeHandle.Vertex : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class PointToCircleDistanceRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is PointToCircleDistanceRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var p2c = (PointToCircleDistanceRoi)roi;
                if (!p2c.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, p2c.StrokeColor);
                double thickness = (isSelected ? 3 : p2c.StrokeThickness) / context.Scale;

                Point p2cCenter = p2c.Center.ToWpfPoint();
                Point p2cPoint = p2c.Point.ToWpfPoint();
                Point nearest = p2c.NearestPointOnCircle.ToWpfPoint();
                context.DrawCircleOutline(p2cCenter, p2c.Radius, brush, thickness);
                context.DrawLineSegment(p2cPoint, nearest, brush, thickness * 0.6);
                context.DrawDot(nearest, 3 / context.Scale, brush);

                Point textPos = new((p2cPoint.X + nearest.X) / 2, (p2cPoint.Y + nearest.Y) / 2 - 10 / context.Scale);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildPointToCircleDistanceText(p2c, context), textPos, brush, true);

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(p2cPoint, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(p2cCenter, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class ParallelismMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is ParallelismMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var para = (ParallelismMeasureRoi)roi;
                if (!para.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, para.StrokeColor);
                double thickness = (isSelected ? 3 : para.StrokeThickness) / context.Scale;

                Point paraLine1P1 = para.Line1P1.ToWpfPoint();
                Point paraLine1P2 = para.Line1P2.ToWpfPoint();
                Point paraLine2P1 = para.Line2P1.ToWpfPoint();
                Point paraLine2P2 = para.Line2P2.ToWpfPoint();
                context.DrawLineSegment(paraLine1P1, paraLine1P2, brush, thickness);
                context.DrawLineSegment(paraLine2P1, paraLine2P2, brush, thickness);

                Point midpoint = new(
                    (para.Line1P1.X + para.Line1P2.X + para.Line2P1.X + para.Line2P2.X) / 4,
                    (para.Line1P1.Y + para.Line1P2.Y + para.Line2P1.Y + para.Line2P2.Y) / 4);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildParallelismText(para, context), midpoint, brush, true);

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(paraLine1P1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(paraLine1P2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(paraLine2P1, isSelected ? ResizeHandle.Vertex : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(paraLine2P2, isSelected ? ResizeHandle.P3 : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class PerpendicularityMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is PerpendicularityMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var perp = (PerpendicularityMeasureRoi)roi;
                if (!perp.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, perp.StrokeColor);
                double thickness = (isSelected ? 3 : perp.StrokeThickness) / context.Scale;

                Point perpLine1P1 = perp.Line1P1.ToWpfPoint();
                Point perpLine1P2 = perp.Line1P2.ToWpfPoint();
                Point perpLine2P1 = perp.Line2P1.ToWpfPoint();
                Point perpLine2P2 = perp.Line2P2.ToWpfPoint();
                context.DrawLineSegment(perpLine1P1, perpLine1P2, brush, thickness);
                context.DrawLineSegment(perpLine2P1, perpLine2P2, brush, thickness);

                if (perp.IntersectionPoint.HasValue)
                {
                    context.DrawDot(perp.IntersectionPoint.Value.ToWpfPoint(), 4 / context.Scale, brush);
                }

                Point midpoint = new(
                    (perp.Line1P1.X + perp.Line1P2.X + perp.Line2P1.X + perp.Line2P2.X) / 4,
                    (perp.Line1P1.Y + perp.Line1P2.Y + perp.Line2P1.Y + perp.Line2P2.Y) / 4);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildPerpendicularityText(perp), midpoint, brush, true);

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(perpLine1P1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(perpLine1P2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(perpLine2P1, isSelected ? ResizeHandle.Vertex : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(perpLine2P2, isSelected ? ResizeHandle.P3 : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class ConcentricityMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is ConcentricityMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var conc = (ConcentricityMeasureRoi)roi;
                if (!conc.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, conc.StrokeColor);
                double thickness = (isSelected ? 3 : conc.StrokeThickness) / context.Scale;

                Point concCenter1 = conc.Center1.ToWpfPoint();
                Point concCenter2 = conc.Center2.ToWpfPoint();
                context.DrawCircleOutline(concCenter1, conc.Radius1, brush, thickness);
                context.DrawCircleOutline(concCenter2, conc.Radius2, brush, thickness);
                context.DrawLineSegment(concCenter1, concCenter2, brush, thickness * 0.5);
                context.DrawDot(concCenter1, 3 / context.Scale, brush);
                context.DrawDot(concCenter2, 3 / context.Scale, brush);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildConcentricityText(conc, context), conc.MidCenter.ToWpfPoint(), brush, true);

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(concCenter1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(concCenter2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class CenterDistanceMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is CenterDistanceMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var distance = (CenterDistanceMeasureRoi)roi;
                if (!distance.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, distance.StrokeColor);
                double thickness = (isSelected ? 3 : distance.StrokeThickness) / context.Scale;
                Point center1 = distance.Center1.ToWpfPoint();
                Point center2 = distance.Center2.ToWpfPoint();
                context.DrawLineSegment(center1, center2, brush, thickness);
                context.DrawDot(center1, 3 / context.Scale, brush);
                context.DrawDot(center2, 3 / context.Scale, brush);
                context.DrawInfoText(StandardRoiInfoTextFormatter.BuildCenterDistanceText(distance, context), distance.MidCenter.ToWpfPoint(), brush, true);

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(center1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(center2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
            }
        }

        private sealed class ThreePointCircleMeasureRenderer : IRoiRenderer
        {
            public bool CanRender(RoiBase roi) => roi is ThreePointCircleMeasureRoi;

            public void Render(RoiBase roi, RoiRenderContext context, Brush? strokeOverride, bool isSelected)
            {
                var circle = (ThreePointCircleMeasureRoi)roi;
                if (!circle.IsVisible) return;

                Brush brush = RoiRenderContext.ResolveStroke(strokeOverride, circle.StrokeColor);
                double thickness = (isSelected ? 3 : circle.StrokeThickness) / context.Scale;
                Point p1 = circle.P1.ToWpfPoint();
                Point p2 = circle.P2.ToWpfPoint();
                Point p3 = circle.P3.ToWpfPoint();

                if (circle.IsValid)
                {
                    Point center = circle.Center.ToWpfPoint();
                    context.DrawCircleOutline(center, circle.Radius, brush, thickness);
                    context.DrawDot(center, 3 / context.Scale, brush);
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildThreePointCircleText(circle, context), center, brush, true);
                }
                else
                {
                    context.DrawLineSegment(p1, p2, brush, thickness);
                    context.DrawLineSegment(p2, p3, brush, thickness);
                    context.DrawLineSegment(p3, p1, brush, thickness);
                    context.DrawInfoText(StandardRoiInfoTextFormatter.BuildThreePointCircleText(circle, context), p3, brush, true);
                }

                double handleSize = context.HandleSize / context.Scale;
                context.DrawHandle(p1, isSelected ? ResizeHandle.P1 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(p2, isSelected ? ResizeHandle.P2 : ResizeHandle.None, handleSize, false, brush);
                context.DrawHandle(p3, isSelected ? ResizeHandle.P3 : ResizeHandle.None, handleSize, false, brush);
            }
        }
    }
}
