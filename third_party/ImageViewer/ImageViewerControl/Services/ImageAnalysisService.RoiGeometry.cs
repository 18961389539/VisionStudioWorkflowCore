using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    /// <summary>
    /// ROI 几何与统计
    /// Chinese: ROI 包围盒、点包含判断与区域灰度统计。
    /// English: ROI bounds, point containment and region intensity statistics.
    /// </summary>
    internal static partial class ImageAnalysisService
    {

        public static bool TryCalculateStatistics(BitmapSource bitmap, RoiBase roi, out RoiStatistics statistics)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentNullException.ThrowIfNull(roi);

            bitmap = NormalizeBitmap(bitmap);

            statistics = new RoiStatistics();
            Rect bounds = GetRoiBounds(roi);
            if (bounds.IsEmpty)
            {
                return false;
            }

            int minX = Math.Max(0, (int)Math.Floor(bounds.X));
            int minY = Math.Max(0, (int)Math.Floor(bounds.Y));
            int maxX = Math.Min(bitmap.PixelWidth - 1, (int)Math.Ceiling(bounds.Right));
            int maxY = Math.Min(bitmap.PixelHeight - 1, (int)Math.Ceiling(bounds.Bottom));
            if (maxX < minX || maxY < minY)
            {
                return false;
            }

            int roiW = maxX - minX + 1;
            int roiH = maxY - minY + 1;
            int bytesPerPixel = (bitmap.Format.BitsPerPixel + 7) / 8;
            int stride = roiW * bytesPerPixel;
            byte[] pixels = new byte[roiH * stride];
            bitmap.CopyPixels(new Int32Rect(minX, minY, roiW, roiH), pixels, stride, 0);

            int count = 0;
            long sum = 0;
            double sumSquares = 0;
            ushort min = ushort.MaxValue;
            ushort max = ushort.MinValue;

            for (int localY = 0; localY < roiH; localY++)
            {
                for (int localX = 0; localX < roiW; localX++)
                {
                    Point samplePoint = new(minX + localX + 0.5, minY + localY + 0.5);
                    if (!Contains(roi, samplePoint))
                    {
                        continue;
                    }

                    int index = localY * stride + localX * bytesPerPixel;
                    ushort value = GetPixelIntensity(pixels, index, bytesPerPixel, bitmap.Format);
                    count++;
                    sum += value;
                    sumSquares += (double)value * value;
                    if (value < min)
                    {
                        min = value;
                    }

                    if (value > max)
                    {
                        max = value;
                    }
                }
            }

            if (count == 0)
            {
                return false;
            }

            double mean = (double)sum / count;
            double variance = Math.Max(0, sumSquares / count - mean * mean);
            statistics = new RoiStatistics
            {
                PixelCount = count,
                Mean = mean,
                Min = min,
                Max = max,
                StandardDeviation = Math.Sqrt(variance)
            };
            return true;
        }

        /// <summary>
        /// ROI 的图像坐标包围盒。
        /// Chinese: 未覆盖的类型返回 <see cref="Rect.Empty"/>——调用方必须把 Empty 当作“未知”，
        /// 不能当作“零尺寸”（渲染层据此决定是否做可视区域裁剪，未知类型一律照常渲染）。
        /// </summary>
        internal static Rect GetRoiBounds(RoiBase roi)
        {
            return roi switch
            {
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
                PointAnnotationRoi point => new Rect(point.Position.X - 1, point.Position.Y - 1, 2, 2),
                PointCoordinateMeasureRoi pointCoordinate => new Rect(pointCoordinate.Position.X - 1, pointCoordinate.Position.Y - 1, 2, 2),
                ThreePointCircleMeasureRoi threePointCircle when threePointCircle.IsValid => new Rect(threePointCircle.Center.X - threePointCircle.Radius, threePointCircle.Center.Y - threePointCircle.Radius, threePointCircle.Radius * 2, threePointCircle.Radius * 2),
                ThreePointCircleMeasureRoi threePointCircle => GeometryUtils.GetBoundingBox(new[] { threePointCircle.P1.ToWpfPoint(), threePointCircle.P2.ToWpfPoint(), threePointCircle.P3.ToWpfPoint() }),
                RingRoi ring => new Rect(ring.Center.X - ring.OuterRadius, ring.Center.Y - ring.OuterRadius, ring.OuterRadius * 2, ring.OuterRadius * 2),
                PolygonRoi poly when poly.IsClosed && poly.Points.Count >= 3 => GeometryUtils.GetBoundingBox(poly.Points.ToWpfPoints()),
                PolylineRoi polyline when polyline.Points.Count > 1 => GeometryUtils.GetBoundingBox(polyline.Points.ToWpfPoints()),
                _ => Rect.Empty
            };
        }

        private static bool Contains(RoiBase roi, Point point)
        {
            switch (roi)
            {
                case RotatedRect rect:
                    var rectMatrix = new Matrix();
                    rectMatrix.RotateAt(-rect.Angle, rect.Center.X, rect.Center.Y);
                    Point rectPoint = rectMatrix.Transform(point);
                    double rectHalfW = rect.Width / 2;
                    double rectHalfH = rect.Height / 2;
                    return rectPoint.X >= rect.Center.X - rectHalfW && rectPoint.X <= rect.Center.X + rectHalfW &&
                           rectPoint.Y >= rect.Center.Y - rectHalfH && rectPoint.Y <= rect.Center.Y + rectHalfH;

                case EllipseRoi ellipse:
                    var ellipseMatrix = new Matrix();
                    ellipseMatrix.RotateAt(-ellipse.Angle, ellipse.Center.X, ellipse.Center.Y);
                    Point ellipsePoint = ellipseMatrix.Transform(point);
                    double dx = ellipsePoint.X - ellipse.Center.X;
                    double dy = ellipsePoint.Y - ellipse.Center.Y;
                    return ellipse.RadiusX > 0 && ellipse.RadiusY > 0 &&
                           (dx * dx) / (ellipse.RadiusX * ellipse.RadiusX) + (dy * dy) / (ellipse.RadiusY * ellipse.RadiusY) <= 1;

                case CircleRoi circle:
                    return GeometryUtils.Distance(circle.Center.ToWpfPoint(), point) <= circle.Radius;

                case PointAnnotationRoi annotation:
                    return GeometryUtils.Distance(annotation.Position.ToWpfPoint(), point) <= 1;

                case PointCoordinateMeasureRoi pointCoordinate:
                    return GeometryUtils.Distance(pointCoordinate.Position.ToWpfPoint(), point) <= 1;

                case ThreePointCircleMeasureRoi threePointCircle:
                    return threePointCircle.IsValid && GeometryUtils.Distance(threePointCircle.Center.ToWpfPoint(), point) <= threePointCircle.Radius;

                case PolylineRoi polyline:
                    for (int i = 1; i < polyline.Points.Count; i++)
                    {
                        if (GeometryUtils.DistanceToSegment(point, polyline.Points[i - 1].ToWpfPoint(), polyline.Points[i].ToWpfPoint()) <= 1.5)
                        {
                            return true;
                        }
                    }

                    return false;

                case RingRoi ring:
                    double distance = GeometryUtils.Distance(ring.Center.ToWpfPoint(), point);
                    return distance >= ring.InnerRadius && distance <= ring.OuterRadius;

                case PolygonRoi poly:
                    return poly.IsClosed && GeometryUtils.IsPointInPolygon(point, poly.Points.ToWpfPointArray());

                default:
                    return false;
            }
        }
    }
}
