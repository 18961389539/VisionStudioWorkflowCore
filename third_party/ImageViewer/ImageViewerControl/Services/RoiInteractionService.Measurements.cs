using System;
using System.Windows;
using System.Windows.Media;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    public sealed partial class RoiInteractionService
    {
        private sealed class LineMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is LineMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var line = (LineMeasureRoi)roi;
                return GeometryUtils.IsPointNearSegment(point, line.P1.ToWpfPoint(), line.P2.ToWpfPoint(), hitTestTolerance / scale);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var line = (LineMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, line.P1.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, line.P2.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var line = (LineMeasureRoi)roi;
                line.P1 = new Point(line.P1.X + dx, line.P1.Y + dy).ToPointD();
                line.P2 = new Point(line.P2.X + dx, line.P2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var line = (LineMeasureRoi)roi;
                if (handle == ResizeHandle.P1) line.P1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) line.P2 = currentPos.ToPointD();
            }
        }

        private sealed class CaliperMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is CaliperMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var caliper = (CaliperMeasureRoi)roi;
                caliper.EnsureCaliperRegion();
                var matrix = new Matrix();
                matrix.RotateAt(-(caliper.CaliperAngleDegrees + 90), caliper.CaliperCenter.X, caliper.CaliperCenter.Y);
                Point localPoint = matrix.Transform(point);
                double halfW = caliper.GetResolvedCaliperRegionLength() / 2;
                double halfH = caliper.CaliperSearchRange;
                return localPoint.X >= caliper.CaliperCenter.X - halfW && localPoint.X <= caliper.CaliperCenter.X + halfW &&
                       localPoint.Y >= caliper.CaliperCenter.Y - halfH && localPoint.Y <= caliper.CaliperCenter.Y + halfH;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var caliper = (CaliperMeasureRoi)roi;
                caliper.EnsureCaliperRegion();
                double halfW = caliper.GetResolvedCaliperRegionLength() / 2;
                double halfH = caliper.CaliperSearchRange;
                double hSize = (handleSize + handleHitPadding) / scale;
                var handlePositions = CreateBoxHandlePositions(caliper.CaliperCenter.ToWpfPoint(), halfW, halfH);
                handlePositions[ResizeHandle.Rotation] = new Point(caliper.CaliperCenter.X, caliper.CaliperCenter.Y - halfH - infoTextOffset / scale);
                var rotateTransform = new RotateTransform(caliper.CaliperAngleDegrees + 90, caliper.CaliperCenter.X, caliper.CaliperCenter.Y);

                foreach (var kvp in handlePositions)
                {
                    if (IsWithinHandle(point, rotateTransform.Transform(kvp.Value), hSize))
                    {
                        return kvp.Key;
                    }
                }

                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var caliper = (CaliperMeasureRoi)roi;
                caliper.EnsureCaliperRegion();
                caliper.CaliperCenter = new Point(caliper.CaliperCenter.X + dx, caliper.CaliperCenter.Y + dy).ToPointD();
                caliper.P1 = new Point(caliper.P1.X + dx, caliper.P1.Y + dy).ToPointD();
                caliper.P2 = new Point(caliper.P2.X + dx, caliper.P2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var caliper = (CaliperMeasureRoi)roi;
                caliper.EnsureCaliperRegion();
                if (handle == ResizeHandle.Rotation)
                {
                    double angle = Math.Atan2(currentPos.Y - caliper.CaliperCenter.Y, currentPos.X - caliper.CaliperCenter.X) * 180 / Math.PI;
                    caliper.CaliperAngleDegrees = angle;
                    return;
                }

                double visualAngle = caliper.CaliperAngleDegrees + 90;
                double rad = -visualAngle * Math.PI / 180.0;
                double dxLocal = dx * Math.Cos(rad) - dy * Math.Sin(rad);
                double dyLocal = dx * Math.Sin(rad) + dy * Math.Cos(rad);
                double halfW = caliper.GetResolvedCaliperRegionLength() / 2;
                double halfH = caliper.CaliperSearchRange;
                double left = -halfW;
                double top = -halfH;
                double right = halfW;
                double bottom = halfH;
                UpdateBounds(handle, dxLocal, dyLocal, ref left, ref top, ref right, ref bottom, minimumRoiDimension);
                caliper.CaliperRegionLength = right - left;
                caliper.CaliperSearchRange = Math.Max(1, (int)Math.Round((bottom - top) / 2));
                double offsetX = (left + right) / 2;
                double offsetY = (top + bottom) / 2;
                double radBack = visualAngle * Math.PI / 180.0;
                double globalOffsetX = offsetX * Math.Cos(radBack) - offsetY * Math.Sin(radBack);
                double globalOffsetY = offsetX * Math.Sin(radBack) + offsetY * Math.Cos(radBack);
                caliper.CaliperCenter = new Point(caliper.CaliperCenter.X + globalOffsetX, caliper.CaliperCenter.Y + globalOffsetY).ToPointD();
            }
        }

        /// <summary>
        /// 线卡尺（单边缘拟合直线）交互。
        /// Chinese: 拖动两个端点改变测量线；拖动黄框两侧长边中点改变搜索范围（CaliperSearchRange），
        /// 范围按当前点到中心线的法向距离设定，两侧对称。
        /// English: Single-edge line caliper. Endpoints move the reference line; the middle of either long
        /// side sets the caliper search range from the normal distance to the center line, symmetrically.
        /// </summary>
        private sealed class LineCaliperMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is LineCaliperMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var line = (LineCaliperMeasureRoi)roi;
                Point p1 = line.P1.ToWpfPoint();
                Point p2 = line.P2.ToWpfPoint();
                if (GeometryUtils.IsPointNearSegment(point, p1, p2, hitTestTolerance / scale))
                {
                    return true;
                }

                Point center = MidPoint(line);
                var matrix = new Matrix();
                matrix.RotateAt(-GetAngleDegrees(line), center.X, center.Y);
                Point local = matrix.Transform(point);
                double halfLength = GeometryUtils.Distance(p1, p2) / 2;
                return Math.Abs(local.X - center.X) <= halfLength &&
                       Math.Abs(local.Y - center.Y) <= line.CaliperSearchRange;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var line = (LineCaliperMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, line.P1.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, line.P2.ToWpfPoint(), hSize)) return ResizeHandle.P2;

                GetSearchHandlePositions(line, out Point positive, out Point negative);
                if (IsNear(point, positive, hSize)) return ResizeHandle.BottomCenter;
                if (IsNear(point, negative, hSize)) return ResizeHandle.TopCenter;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var line = (LineCaliperMeasureRoi)roi;
                line.P1 = new Point(line.P1.X + dx, line.P1.Y + dy).ToPointD();
                line.P2 = new Point(line.P2.X + dx, line.P2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var line = (LineCaliperMeasureRoi)roi;
                if (handle == ResizeHandle.P1)
                {
                    line.P1 = currentPos.ToPointD();
                    return;
                }

                if (handle == ResizeHandle.P2)
                {
                    line.P2 = currentPos.ToPointD();
                    return;
                }

                if (handle is not (ResizeHandle.BottomCenter or ResizeHandle.TopCenter))
                {
                    return;
                }

                // Symmetric range: either side sets |normal distance| from the center line.
                Point center = MidPoint(line);
                double projected = Math.Abs(Vector.Multiply(currentPos - center, GetNormal(line)));
                line.CaliperSearchRange = Math.Max(1, (int)Math.Round(projected));
            }

            private static Point MidPoint(LineCaliperMeasureRoi line) =>
                new((line.P1.X + line.P2.X) / 2, (line.P1.Y + line.P2.Y) / 2);

            private static double GetAngleDegrees(LineCaliperMeasureRoi line) =>
                Math.Atan2(line.P2.Y - line.P1.Y, line.P2.X - line.P1.X) * 180 / Math.PI;

            private static Vector GetNormal(LineCaliperMeasureRoi line)
            {
                double radians = GetAngleDegrees(line) * Math.PI / 180;
                return new Vector(-Math.Sin(radians), Math.Cos(radians));
            }

            private static void GetSearchHandlePositions(LineCaliperMeasureRoi line, out Point positive, out Point negative)
            {
                Point center = MidPoint(line);
                Vector normal = GetNormal(line);
                positive = center + (normal * line.CaliperSearchRange);
                negative = center - (normal * line.CaliperSearchRange);
            }
        }

        private sealed class AngleMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is AngleMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var angle = (AngleMeasureRoi)roi;
                return GeometryUtils.IsPointNearSegment(point, angle.P1.ToWpfPoint(), angle.Vertex.ToWpfPoint(), hitTestTolerance / scale) ||
                       GeometryUtils.IsPointNearSegment(point, angle.Vertex.ToWpfPoint(), angle.P2.ToWpfPoint(), hitTestTolerance / scale);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var angle = (AngleMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, angle.P1.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, angle.Vertex.ToWpfPoint(), hSize)) return ResizeHandle.Vertex;
                if (IsNear(point, angle.P2.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var angle = (AngleMeasureRoi)roi;
                angle.P1 = new Point(angle.P1.X + dx, angle.P1.Y + dy).ToPointD();
                angle.Vertex = new Point(angle.Vertex.X + dx, angle.Vertex.Y + dy).ToPointD();
                angle.P2 = new Point(angle.P2.X + dx, angle.P2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var angle = (AngleMeasureRoi)roi;
                if (handle == ResizeHandle.P1) angle.P1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.Vertex) angle.Vertex = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) angle.P2 = currentPos.ToPointD();
            }
        }

        private sealed class ArcMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is ArcMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var arc = (ArcMeasureRoi)roi;
                return GeometryUtils.IsPointNearSegment(point, arc.StartPoint.ToWpfPoint(), arc.EndPoint.ToWpfPoint(), hitTestTolerance / scale) ||
                       GeometryUtils.IsPointNearSegment(point, arc.StartPoint.ToWpfPoint(), arc.ArcPoint.ToWpfPoint(), hitTestTolerance / scale) ||
                       GeometryUtils.IsPointNearSegment(point, arc.ArcPoint.ToWpfPoint(), arc.EndPoint.ToWpfPoint(), hitTestTolerance / scale);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var arc = (ArcMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, arc.StartPoint.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, arc.EndPoint.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                if (IsNear(point, arc.ArcPoint.ToWpfPoint(), hSize)) return ResizeHandle.Vertex;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var arc = (ArcMeasureRoi)roi;
                arc.StartPoint = new Point(arc.StartPoint.X + dx, arc.StartPoint.Y + dy).ToPointD();
                arc.EndPoint = new Point(arc.EndPoint.X + dx, arc.EndPoint.Y + dy).ToPointD();
                arc.ArcPoint = new Point(arc.ArcPoint.X + dx, arc.ArcPoint.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var arc = (ArcMeasureRoi)roi;
                if (handle == ResizeHandle.P1) arc.StartPoint = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) arc.EndPoint = currentPos.ToPointD();
                else if (handle == ResizeHandle.Vertex) arc.ArcPoint = currentPos.ToPointD();
            }
        }

        private sealed class PointToLineDistanceBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is PointToLineDistanceRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var p2l = (PointToLineDistanceRoi)roi;
                return GeometryUtils.Distance(p2l.Point.ToWpfPoint(), point) <= hitTestTolerance / scale ||
                       GeometryUtils.IsPointNearSegment(point, p2l.LineP1.ToWpfPoint(), p2l.LineP2.ToWpfPoint(), hitTestTolerance / scale);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var p2l = (PointToLineDistanceRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, p2l.Point.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, p2l.LineP1.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                if (IsNear(point, p2l.LineP2.ToWpfPoint(), hSize)) return ResizeHandle.Vertex;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var p2l = (PointToLineDistanceRoi)roi;
                p2l.Point = new Point(p2l.Point.X + dx, p2l.Point.Y + dy).ToPointD();
                p2l.LineP1 = new Point(p2l.LineP1.X + dx, p2l.LineP1.Y + dy).ToPointD();
                p2l.LineP2 = new Point(p2l.LineP2.X + dx, p2l.LineP2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var p2l = (PointToLineDistanceRoi)roi;
                if (handle == ResizeHandle.P1) p2l.Point = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) p2l.LineP1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.Vertex) p2l.LineP2 = currentPos.ToPointD();
            }
        }

        private sealed class PointToCircleDistanceBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is PointToCircleDistanceRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var p2c = (PointToCircleDistanceRoi)roi;
                return GeometryUtils.Distance(p2c.Point.ToWpfPoint(), point) <= hitTestTolerance / scale ||
                       GeometryUtils.Distance(p2c.Center.ToWpfPoint(), point) <= p2c.Radius + hitTestTolerance / scale;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var p2c = (PointToCircleDistanceRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, p2c.Point.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, p2c.Center.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var p2c = (PointToCircleDistanceRoi)roi;
                p2c.Point = new Point(p2c.Point.X + dx, p2c.Point.Y + dy).ToPointD();
                p2c.Center = new Point(p2c.Center.X + dx, p2c.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var p2c = (PointToCircleDistanceRoi)roi;
                if (handle == ResizeHandle.P1) p2c.Point = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2)
                {
                    double dx2 = currentPos.X - p2c.Center.X;
                    double dy2 = currentPos.Y - p2c.Center.Y;
                    p2c.Radius = Math.Max(minimumRoiDimension, Math.Sqrt(dx2 * dx2 + dy2 * dy2));
                }
            }
        }

        private sealed class ParallelismMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is ParallelismMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var para = (ParallelismMeasureRoi)roi;
                return GeometryUtils.IsPointNearSegment(point, para.Line1P1.ToWpfPoint(), para.Line1P2.ToWpfPoint(), hitTestTolerance / scale) ||
                       GeometryUtils.IsPointNearSegment(point, para.Line2P1.ToWpfPoint(), para.Line2P2.ToWpfPoint(), hitTestTolerance / scale);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var para = (ParallelismMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, para.Line1P1.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, para.Line1P2.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                if (IsNear(point, para.Line2P1.ToWpfPoint(), hSize)) return ResizeHandle.Vertex;
                if (IsNear(point, para.Line2P2.ToWpfPoint(), hSize)) return ResizeHandle.P3;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var para = (ParallelismMeasureRoi)roi;
                para.Line1P1 = new Point(para.Line1P1.X + dx, para.Line1P1.Y + dy).ToPointD();
                para.Line1P2 = new Point(para.Line1P2.X + dx, para.Line1P2.Y + dy).ToPointD();
                para.Line2P1 = new Point(para.Line2P1.X + dx, para.Line2P1.Y + dy).ToPointD();
                para.Line2P2 = new Point(para.Line2P2.X + dx, para.Line2P2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var para = (ParallelismMeasureRoi)roi;
                if (handle == ResizeHandle.P1) para.Line1P1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) para.Line1P2 = currentPos.ToPointD();
                else if (handle == ResizeHandle.Vertex) para.Line2P1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P3) para.Line2P2 = currentPos.ToPointD();
            }
        }

        private sealed class PerpendicularityMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is PerpendicularityMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var perp = (PerpendicularityMeasureRoi)roi;
                return GeometryUtils.IsPointNearSegment(point, perp.Line1P1.ToWpfPoint(), perp.Line1P2.ToWpfPoint(), hitTestTolerance / scale) ||
                       GeometryUtils.IsPointNearSegment(point, perp.Line2P1.ToWpfPoint(), perp.Line2P2.ToWpfPoint(), hitTestTolerance / scale);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var perp = (PerpendicularityMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, perp.Line1P1.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, perp.Line1P2.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                if (IsNear(point, perp.Line2P1.ToWpfPoint(), hSize)) return ResizeHandle.Vertex;
                if (IsNear(point, perp.Line2P2.ToWpfPoint(), hSize)) return ResizeHandle.P3;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var perp = (PerpendicularityMeasureRoi)roi;
                perp.Line1P1 = new Point(perp.Line1P1.X + dx, perp.Line1P1.Y + dy).ToPointD();
                perp.Line1P2 = new Point(perp.Line1P2.X + dx, perp.Line1P2.Y + dy).ToPointD();
                perp.Line2P1 = new Point(perp.Line2P1.X + dx, perp.Line2P1.Y + dy).ToPointD();
                perp.Line2P2 = new Point(perp.Line2P2.X + dx, perp.Line2P2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var perp = (PerpendicularityMeasureRoi)roi;
                if (handle == ResizeHandle.P1) perp.Line1P1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) perp.Line1P2 = currentPos.ToPointD();
                else if (handle == ResizeHandle.Vertex) perp.Line2P1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P3) perp.Line2P2 = currentPos.ToPointD();
            }
        }

        private sealed class ConcentricityMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is ConcentricityMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var conc = (ConcentricityMeasureRoi)roi;
                return GeometryUtils.Distance(conc.Center1.ToWpfPoint(), point) <= conc.Radius1 + hitTestTolerance / scale ||
                       GeometryUtils.Distance(conc.Center2.ToWpfPoint(), point) <= conc.Radius2 + hitTestTolerance / scale;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var conc = (ConcentricityMeasureRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, conc.Center1.ToWpfPoint(), hSize)) return ResizeHandle.P1;
                if (IsNear(point, conc.Center2.ToWpfPoint(), hSize)) return ResizeHandle.P2;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var conc = (ConcentricityMeasureRoi)roi;
                conc.Center1 = new Point(conc.Center1.X + dx, conc.Center1.Y + dy).ToPointD();
                conc.Center2 = new Point(conc.Center2.X + dx, conc.Center2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var conc = (ConcentricityMeasureRoi)roi;
                if (handle == ResizeHandle.P1)
                {
                    double dx2 = currentPos.X - conc.Center1.X;
                    double dy2 = currentPos.Y - conc.Center1.Y;
                    conc.Radius1 = Math.Max(minimumRoiDimension, Math.Sqrt(dx2 * dx2 + dy2 * dy2));
                }
                else if (handle == ResizeHandle.P2)
                {
                    double dx2 = currentPos.X - conc.Center2.X;
                    double dy2 = currentPos.Y - conc.Center2.Y;
                    conc.Radius2 = Math.Max(minimumRoiDimension, Math.Sqrt(dx2 * dx2 + dy2 * dy2));
                }
            }
        }

        private sealed class CenterDistanceMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is CenterDistanceMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var distance = (CenterDistanceMeasureRoi)roi;
                double tolerance = hitTestTolerance / scale;
                return GeometryUtils.Distance(distance.Center1.ToWpfPoint(), point) <= tolerance ||
                       GeometryUtils.Distance(distance.Center2.ToWpfPoint(), point) <= tolerance ||
                       GeometryUtils.IsPointNearSegment(point, distance.Center1.ToWpfPoint(), distance.Center2.ToWpfPoint(), tolerance);
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var distance = (CenterDistanceMeasureRoi)roi;
                double size = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, distance.Center1.ToWpfPoint(), size)) return ResizeHandle.P1;
                if (IsNear(point, distance.Center2.ToWpfPoint(), size)) return ResizeHandle.P2;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var distance = (CenterDistanceMeasureRoi)roi;
                distance.Center1 = new Point(distance.Center1.X + dx, distance.Center1.Y + dy).ToPointD();
                distance.Center2 = new Point(distance.Center2.X + dx, distance.Center2.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var distance = (CenterDistanceMeasureRoi)roi;
                if (handle == ResizeHandle.P1) distance.Center1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) distance.Center2 = currentPos.ToPointD();
            }
        }

        private sealed class ThreePointCircleMeasureBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is ThreePointCircleMeasureRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var circle = (ThreePointCircleMeasureRoi)roi;
                double tolerance = hitTestTolerance / scale;
                if (circle.IsValid && Math.Abs(GeometryUtils.Distance(circle.Center.ToWpfPoint(), point) - circle.Radius) <= tolerance)
                {
                    return true;
                }

                return GeometryUtils.Distance(circle.P1.ToWpfPoint(), point) <= tolerance ||
                       GeometryUtils.Distance(circle.P2.ToWpfPoint(), point) <= tolerance ||
                       GeometryUtils.Distance(circle.P3.ToWpfPoint(), point) <= tolerance;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var circle = (ThreePointCircleMeasureRoi)roi;
                double size = (handleSize + handleHitPadding) / scale;
                if (IsNear(point, circle.P1.ToWpfPoint(), size)) return ResizeHandle.P1;
                if (IsNear(point, circle.P2.ToWpfPoint(), size)) return ResizeHandle.P2;
                if (IsNear(point, circle.P3.ToWpfPoint(), size)) return ResizeHandle.P3;
                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var circle = (ThreePointCircleMeasureRoi)roi;
                circle.P1 = new Point(circle.P1.X + dx, circle.P1.Y + dy).ToPointD();
                circle.P2 = new Point(circle.P2.X + dx, circle.P2.Y + dy).ToPointD();
                circle.P3 = new Point(circle.P3.X + dx, circle.P3.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var circle = (ThreePointCircleMeasureRoi)roi;
                if (handle == ResizeHandle.P1) circle.P1 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P2) circle.P2 = currentPos.ToPointD();
                else if (handle == ResizeHandle.P3) circle.P3 = currentPos.ToPointD();
            }
        }
    }
}
