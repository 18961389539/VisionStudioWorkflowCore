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
        private sealed class RotatedRectBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is RotatedRect;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var rect = (RotatedRect)roi;
                var matrix = new Matrix();
                matrix.RotateAt(-rect.Angle, rect.Center.X, rect.Center.Y);
                Point localPoint = matrix.Transform(point);
                double halfW = rect.Width / 2;
                double halfH = rect.Height / 2;
                return localPoint.X >= rect.Center.X - halfW && localPoint.X <= rect.Center.X + halfW &&
                       localPoint.Y >= rect.Center.Y - halfH && localPoint.Y <= rect.Center.Y + halfH;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var rect = (RotatedRect)roi;
                double halfW = rect.Width / 2;
                double halfH = rect.Height / 2;
                double hSize = (handleSize + handleHitPadding) / scale;
                var handlePositions = CreateBoxHandlePositions(rect.Center.ToWpfPoint(), halfW, halfH);
                handlePositions[ResizeHandle.Rotation] = new Point(rect.Center.X, rect.Center.Y - halfH - infoTextOffset / scale);
                var rotateTransform = new RotateTransform(rect.Angle, rect.Center.X, rect.Center.Y);

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
                var rect = (RotatedRect)roi;
                rect.Center = new Point(rect.Center.X + dx, rect.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var rect = (RotatedRect)roi;
                if (handle == ResizeHandle.Rotation)
                {
                    double angle = Math.Atan2(currentPos.Y - rect.Center.Y, currentPos.X - rect.Center.X) * 180 / Math.PI;
                    rect.Angle = angle + 90;
                    return;
                }

                double rad = -rect.Angle * Math.PI / 180.0;
                double dxLocal = dx * Math.Cos(rad) - dy * Math.Sin(rad);
                double dyLocal = dx * Math.Sin(rad) + dy * Math.Cos(rad);
                double halfW = rect.Width / 2;
                double halfH = rect.Height / 2;
                double left = -halfW;
                double top = -halfH;
                double right = halfW;
                double bottom = halfH;
                UpdateBounds(handle, dxLocal, dyLocal, ref left, ref top, ref right, ref bottom, minimumRoiDimension);
                rect.Width = right - left;
                rect.Height = bottom - top;
                double offsetX = (left + right) / 2;
                double offsetY = (top + bottom) / 2;
                double radBack = rect.Angle * Math.PI / 180.0;
                double globalOffsetX = offsetX * Math.Cos(radBack) - offsetY * Math.Sin(radBack);
                double globalOffsetY = offsetX * Math.Sin(radBack) + offsetY * Math.Cos(radBack);
                rect.Center = new Point(rect.Center.X + globalOffsetX, rect.Center.Y + globalOffsetY).ToPointD();
            }
        }

        private sealed class EllipseRoiBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is EllipseRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var ellipse = (EllipseRoi)roi;
                var matrix = new Matrix();
                matrix.RotateAt(-ellipse.Angle, ellipse.Center.X, ellipse.Center.Y);
                Point localPoint = matrix.Transform(point);
                double dx = localPoint.X - ellipse.Center.X;
                double dy = localPoint.Y - ellipse.Center.Y;
                return ellipse.RadiusX > 0 && ellipse.RadiusY > 0 &&
                       (dx * dx) / (ellipse.RadiusX * ellipse.RadiusX) + (dy * dy) / (ellipse.RadiusY * ellipse.RadiusY) <= 1;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var ellipse = (EllipseRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                var handlePositions = CreateBoxHandlePositions(ellipse.Center.ToWpfPoint(), ellipse.RadiusX, ellipse.RadiusY);
                handlePositions[ResizeHandle.Rotation] = new Point(ellipse.Center.X, ellipse.Center.Y - ellipse.RadiusY - infoTextOffset / scale);
                var rotateTransform = new RotateTransform(ellipse.Angle, ellipse.Center.X, ellipse.Center.Y);

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
                var ellipse = (EllipseRoi)roi;
                ellipse.Center = new Point(ellipse.Center.X + dx, ellipse.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var ellipse = (EllipseRoi)roi;
                if (handle == ResizeHandle.Rotation)
                {
                    double angle = Math.Atan2(currentPos.Y - ellipse.Center.Y, currentPos.X - ellipse.Center.X) * 180 / Math.PI;
                    ellipse.Angle = angle + 90;
                    return;
                }

                double rad = -ellipse.Angle * Math.PI / 180.0;
                double dxLocal = dx * Math.Cos(rad) - dy * Math.Sin(rad);
                double dyLocal = dx * Math.Sin(rad) + dy * Math.Cos(rad);
                double left = -ellipse.RadiusX;
                double top = -ellipse.RadiusY;
                double right = ellipse.RadiusX;
                double bottom = ellipse.RadiusY;
                UpdateBounds(handle, dxLocal, dyLocal, ref left, ref top, ref right, ref bottom, minimumRoiDimension);
                ellipse.RadiusX = (right - left) / 2;
                ellipse.RadiusY = (bottom - top) / 2;
                double offsetX = (left + right) / 2;
                double offsetY = (top + bottom) / 2;
                double radBack = ellipse.Angle * Math.PI / 180.0;
                double globalOffsetX = offsetX * Math.Cos(radBack) - offsetY * Math.Sin(radBack);
                double globalOffsetY = offsetX * Math.Sin(radBack) + offsetY * Math.Cos(radBack);
                ellipse.Center = new Point(ellipse.Center.X + globalOffsetX, ellipse.Center.Y + globalOffsetY).ToPointD();
            }
        }

        private sealed class CircleRoiBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is CircleRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var circle = (CircleRoi)roi;
                return GeometryUtils.Distance(circle.Center.ToWpfPoint(), point) <= circle.Radius;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var circle = (CircleRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                foreach (var kvp in CreateBoxHandlePositions(circle.Center.ToWpfPoint(), circle.Radius, circle.Radius))
                {
                    if (IsWithinHandle(point, kvp.Value, hSize))
                    {
                        return kvp.Key;
                    }
                }

                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var circle = (CircleRoi)roi;
                circle.Center = new Point(circle.Center.X + dx, circle.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var circle = (CircleRoi)roi;
                if (handle != ResizeHandle.None)
                {
                    circle.Radius = Math.Max(minimumRoiDimension, GeometryUtils.Distance(circle.Center.ToWpfPoint(), currentPos));
                }
            }
        }

        private sealed class RingRoiBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is RingRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var ring = (RingRoi)roi;
                double distance = GeometryUtils.Distance(ring.Center.ToWpfPoint(), point);
                return distance >= ring.InnerRadius - hitTestTolerance / scale && distance <= ring.OuterRadius + hitTestTolerance / scale;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var ring = (RingRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                foreach (var kvp in CreateBoxHandlePositions(ring.Center.ToWpfPoint(), ring.OuterRadius, ring.OuterRadius))
                {
                    if (IsWithinHandle(point, kvp.Value, hSize))
                    {
                        return kvp.Key;
                    }
                }

                if (IsNear(point, new Point(ring.Center.X + ring.InnerRadius, ring.Center.Y), hSize))
                {
                    return ResizeHandle.P1;
                }

                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var ring = (RingRoi)roi;
                ring.Center = new Point(ring.Center.X + dx, ring.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var ring = (RingRoi)roi;
                double radius = Math.Max(minimumRoiDimension, GeometryUtils.Distance(ring.Center.ToWpfPoint(), currentPos));
                if (handle == ResizeHandle.P1)
                {
                    ring.InnerRadius = Math.Min(radius, Math.Max(minimumRoiDimension, ring.OuterRadius - minimumRoiDimension));
                }
                else if (handle != ResizeHandle.None)
                {
                    ring.OuterRadius = Math.Max(radius, ring.InnerRadius + minimumRoiDimension);
                }
            }
        }

        private sealed class PolygonRoiBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is PolygonRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var poly = (PolygonRoi)roi;
                return GeometryUtils.IsPointInPolygon(point, poly.Points.ToWpfPointArray());
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                return ResizeHandle.None;
            }

            public int GetVertexIndexAt(RoiBase roi, Point point, double scale, double handleSize, double polygonVertexHitPadding)
            {
                var poly = (PolygonRoi)roi;
                double hSize = (handleSize + polygonVertexHitPadding) / scale;
                for (int i = 0; i < poly.Points.Count; i++)
                {
                    if (IsWithinHandle(point, poly.Points[i].ToWpfPoint(), hSize))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public int GetSegmentIndexAt(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var poly = (PolygonRoi)roi;
                double threshold = hitTestTolerance / scale;
                for (int i = 0; i < poly.Points.Count; i++)
                {
                    Point p1 = poly.Points[i].ToWpfPoint();
                    Point p2 = poly.Points[(i + 1) % poly.Points.Count].ToWpfPoint();
                    if (GeometryUtils.IsPointNearSegment(point, p1, p2, threshold))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var poly = (PolygonRoi)roi;
                for (int i = 0; i < poly.Points.Count; i++)
                {
                    poly.Points[i] = new Point(poly.Points[i].X + dx, poly.Points[i].Y + dy).ToPointD();
                }
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
            }
        }

        private sealed class PolylineRoiBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is PolylineRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var polyline = (PolylineRoi)roi;
                for (int i = 1; i < polyline.Points.Count; i++)
                {
                    if (GeometryUtils.IsPointNearSegment(point, polyline.Points[i - 1].ToWpfPoint(), polyline.Points[i].ToWpfPoint(), hitTestTolerance / scale))
                    {
                        return true;
                    }
                }

                return false;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                return ResizeHandle.None;
            }

            public int GetVertexIndexAt(RoiBase roi, Point point, double scale, double handleSize, double polygonVertexHitPadding)
            {
                var polyline = (PolylineRoi)roi;
                double hSize = (handleSize + polygonVertexHitPadding) / scale;
                for (int i = 0; i < polyline.Points.Count; i++)
                {
                    if (IsWithinHandle(point, polyline.Points[i].ToWpfPoint(), hSize))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public int GetSegmentIndexAt(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var polyline = (PolylineRoi)roi;
                double threshold = hitTestTolerance / scale;
                for (int i = 0; i < polyline.Points.Count - 1; i++)
                {
                    if (GeometryUtils.IsPointNearSegment(
                        point,
                        polyline.Points[i].ToWpfPoint(),
                        polyline.Points[i + 1].ToWpfPoint(),
                        threshold))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var polyline = (PolylineRoi)roi;
                for (int i = 0; i < polyline.Points.Count; i++)
                {
                    polyline.Points[i] = new Point(polyline.Points[i].X + dx, polyline.Points[i].Y + dy).ToPointD();
                }
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
            }
        }

        private sealed class BlobAnalysisBehavior : IRoiBehavior
        {
            public bool CanHandle(RoiBase roi) => roi is BlobAnalysisRoi;

            public bool HitTest(RoiBase roi, Point point, double scale, double hitTestTolerance)
            {
                var rect = (BlobAnalysisRoi)roi;
                var matrix = new Matrix();
                matrix.RotateAt(-rect.Angle, rect.Center.X, rect.Center.Y);
                Point localPoint = matrix.Transform(point);
                double halfW = rect.Width / 2;
                double halfH = rect.Height / 2;
                return localPoint.X >= rect.Center.X - halfW && localPoint.X <= rect.Center.X + halfW &&
                       localPoint.Y >= rect.Center.Y - halfH && localPoint.Y <= rect.Center.Y + halfH;
            }

            public ResizeHandle GetHandleAt(RoiBase roi, Point point, double scale, double handleSize, double handleHitPadding, double infoTextOffset, double polygonVertexHitPadding)
            {
                var rect = (BlobAnalysisRoi)roi;
                double hSize = (handleSize + handleHitPadding) / scale;
                foreach (var kvp in CreateBoxHandlePositions(rect.Center.ToWpfPoint(), rect.Width / 2, rect.Height / 2))
                {
                    var matrix = new Matrix();
                    matrix.RotateAt(rect.Angle, rect.Center.X, rect.Center.Y);
                    if (IsWithinHandle(point, matrix.Transform(kvp.Value), hSize))
                    {
                        return kvp.Key;
                    }
                }

                var rotateMatrix = new Matrix();
                rotateMatrix.RotateAt(rect.Angle, rect.Center.X, rect.Center.Y);
                Point rotationHandlePos = rotateMatrix.Transform(new Point(rect.Center.X, rect.Center.Y - rect.Height / 2 - infoTextOffset / scale));
                if (IsWithinHandle(point, rotationHandlePos, hSize))
                {
                    return ResizeHandle.Rotation;
                }

                return ResizeHandle.None;
            }

            public void Move(RoiBase roi, double dx, double dy)
            {
                var rect = (BlobAnalysisRoi)roi;
                rect.Center = new Point(rect.Center.X + dx, rect.Center.Y + dy).ToPointD();
            }

            public void Resize(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos, double minimumRoiDimension)
            {
                var rect = (BlobAnalysisRoi)roi;
                if (handle == ResizeHandle.Rotation)
                {
                    double angle = Math.Atan2(currentPos.Y - rect.Center.Y, currentPos.X - rect.Center.X) * 180 / Math.PI;
                    rect.Angle = angle + 90;
                    return;
                }

                double rad = -rect.Angle * Math.PI / 180.0;
                double dxLocal = dx * Math.Cos(rad) - dy * Math.Sin(rad);
                double dyLocal = dx * Math.Sin(rad) + dy * Math.Cos(rad);
                double halfW = rect.Width / 2;
                double halfH = rect.Height / 2;
                double left = -halfW;
                double top = -halfH;
                double right = halfW;
                double bottom = halfH;
                UpdateBounds(handle, dxLocal, dyLocal, ref left, ref top, ref right, ref bottom, minimumRoiDimension);
                rect.Width = right - left;
                rect.Height = bottom - top;
                double offsetX = (left + right) / 2;
                double offsetY = (top + bottom) / 2;
                double radBack = rect.Angle * Math.PI / 180.0;
                double globalOffsetX = offsetX * Math.Cos(radBack) - offsetY * Math.Sin(radBack);
                double globalOffsetY = offsetX * Math.Sin(radBack) + offsetY * Math.Cos(radBack);
                rect.Center = new Point(rect.Center.X + globalOffsetX, rect.Center.Y + globalOffsetY).ToPointD();
            }
        }
    }
}
