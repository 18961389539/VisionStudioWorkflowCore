using System;
using System.Collections.Generic;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    /// <summary>
    /// 畸变校正辅助：按 ROI 的几何中心计算长度/面积校正因子。
    /// Chinese: 校正中心按测量几何取(线段中点、圆/弧圆心、包围盒中心等)，
    /// 使信息面板与导出数值与"以 ROI 几何中心为基准的局部畸变"保持一致。
    /// English: Computes distortion corrections centered on the ROI geometry.
    /// </summary>
    internal static class RoiCalibrationHelper
    {
        public static bool CanUseMeasurements(CameraCalibration? calibration, DistortionAudit? audit)
            => calibration is not { IsValid: false } && audit is not { HasNonConvergedPoints: true };

        /// <summary>长度校正因子（无标定或中心未知时为 1.0）。</summary>
        public static double GetLengthCorrection(RoiBase roi, CameraCalibration? calibration)
        {
            if (calibration is { IsEnabled: true, IsValid: true } &&
                TryGetSegment(roi, out PointD start, out PointD end))
            {
                double rawLength = Distance(start, end);
                if (rawLength > 1e-9)
                {
                    double correctedLength = calibration.CorrectPixelLength(start, end);
                    if (double.IsFinite(correctedLength) && correctedLength > 0)
                    {
                        return correctedLength / rawLength;
                    }
                }
            }

            Point? center = GetMeasurementCenter(roi);
            return center.HasValue && calibration is { IsEnabled: true }
                ? calibration.LocalLengthScaleFactor(center.Value.ToPointD())
                : 1.0;
        }

        /// <summary>面积校正因子（长度校正因子的平方，各向同性畸变下成立）。</summary>
        public static double GetAreaCorrection(RoiBase roi, CameraCalibration? calibration)
        {
            Point? center = GetMeasurementCenter(roi);
            if (center.HasValue && calibration is { IsEnabled: true, IsValid: true })
            {
                return calibration.LocalAreaScaleFactor(center.Value.ToPointD());
            }

            double correction = GetLengthCorrection(roi, calibration);
            return correction * correction;
        }

        /// <summary>
        /// 获取 ROI 的测量几何中心；无长度/面积测量语义的 ROI 返回 null。
        /// </summary>
        public static Point? GetMeasurementCenter(RoiBase roi) => roi switch
        {
            CaliperMeasureRoi caliper => Midpoint(caliper.MeasurementP1.ToWpfPoint(), caliper.MeasurementP2.ToWpfPoint()),
            LineCaliperMeasureRoi lineCaliper => Midpoint(lineCaliper.MeasurementP1.ToWpfPoint(), lineCaliper.MeasurementP2.ToWpfPoint()),
            ArrowAnnotationRoi arrow => Midpoint(arrow.P1.ToWpfPoint(), arrow.P2.ToWpfPoint()),
            LineMeasureRoi line => Midpoint(line.P1.ToWpfPoint(), line.P2.ToWpfPoint()),
            ArcCaliperMeasureRoi arcCaliper => arcCaliper.MeasurementCenter.ToWpfPoint(),
            CircularCaliperMeasureRoi circular => circular.MeasurementCenter.ToWpfPoint(),
            BlobAnalysisRoi blob => blob.Center.ToWpfPoint(),
            RotatedRect rect => rect.Center.ToWpfPoint(),
            FittedEllipseRoi fittedEllipse => fittedEllipse.Center.ToWpfPoint(),
            EllipseRoi ellipse => ellipse.Center.ToWpfPoint(),
            CircleRoi circle => circle.Center.ToWpfPoint(),
            RingRoi ring => ring.Center.ToWpfPoint(),
            PolygonRoi polygon when polygon.IsClosed && polygon.Points.Count >= 3 => GeometryUtils.GetPolygonMetrics(polygon.Points.ToWpfPointArray()).Centroid,
            PolygonRoi polygon => GeometryUtils.GetCentroid(polygon.Points.ToWpfPointArray()),
            PolylineRoi polyline => GeometryUtils.GetCentroid(polyline.Points.ToWpfPointArray()),
            AngleMeasureRoi angle => angle.Vertex.ToWpfPoint(),
            ArcMeasureRoi arc => arc.Center.ToWpfPoint(),
            PointToLineDistanceRoi pointToLine => pointToLine.Point.ToWpfPoint(),
            PointToCircleDistanceRoi pointToCircle => pointToCircle.Point.ToWpfPoint(),
            ConcentricityMeasureRoi concentricity => concentricity.MidCenter.ToWpfPoint(),
            CenterDistanceMeasureRoi centerDistance => centerDistance.MidCenter.ToWpfPoint(),
            ThreePointCircleMeasureRoi threePointCircle when threePointCircle.IsValid => threePointCircle.Center.ToWpfPoint(),
            PointAnnotationRoi point => point.Position.ToWpfPoint(),
            PointCoordinateMeasureRoi pointCoordinate => pointCoordinate.Position.ToWpfPoint(),
            TextAnnotationRoi text => text.Position.ToWpfPoint(),
            _ => null
        };

        /// <summary>
        /// 审计单个 ROI 实际参与畸变校正的点：线段类取两端点，其余取测量中心及其数值 Jacobian 抽样点。
        /// Chinese: 审计点必须与 <see cref="GetLengthCorrection"/> / <see cref="GetAreaCorrection"/>
        /// 真正做反解的点一致，否则报告会给出一个"没问题"的审计而数值却在别处发散。
        /// </summary>
        public static DistortionAudit AuditRoi(RoiBase roi, CameraCalibration? calibration)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return CameraCalibrationAudit.Audit(calibration, GetAuditPoints(roi));
        }

        /// <summary>汇总多个 ROI 的逐点去畸变审计。</summary>
        public static DistortionAudit AuditRois(IEnumerable<RoiBase> rois, CameraCalibration? calibration)
        {
            ArgumentNullException.ThrowIfNull(rois);

            DistortionAudit audit = DistortionAudit.Empty;
            foreach (RoiBase roi in rois)
            {
                audit = audit.Combine(AuditRoi(roi, calibration));
            }

            return audit;
        }

        /// <summary>枚举一个 ROI 在校正计算中实际被反解的点。</summary>
        public static IEnumerable<PointD> GetAuditPoints(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);

            if (roi is PointAnnotationRoi or PointCoordinateMeasureRoi or TextAnnotationRoi)
            {
                yield break;
            }

            if (TryGetSegment(roi, out PointD start, out PointD end) && Distance(start, end) > 1e-9)
            {
                yield return start;
                yield return end;
                yield break;
            }

            if (GetMeasurementCenter(roi) is not Point center)
            {
                yield break;
            }

            PointD centerPoint = center.ToPointD();
            yield return centerPoint;
            // 与 LocalLengthScaleFactor / LocalAreaScaleFactor 的 ±0.5 px 数值 Jacobian 抽样保持一致。
            yield return new PointD(centerPoint.X + 0.5, centerPoint.Y);
            yield return new PointD(centerPoint.X - 0.5, centerPoint.Y);
            yield return new PointD(centerPoint.X, centerPoint.Y + 0.5);
            yield return new PointD(centerPoint.X, centerPoint.Y - 0.5);
        }

        private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

        private static bool TryGetSegment(RoiBase roi, out PointD start, out PointD end)
        {
            switch (roi)
            {
                case ArrowAnnotationRoi arrow:
                    start = arrow.P1;
                    end = arrow.P2;
                    return true;
                case CaliperMeasureRoi caliper:
                    start = caliper.MeasurementP1;
                    end = caliper.MeasurementP2;
                    return true;
                case LineCaliperMeasureRoi lineCaliper:
                    start = lineCaliper.MeasurementP1;
                    end = lineCaliper.MeasurementP2;
                    return true;
                case LineMeasureRoi line:
                    start = line.P1;
                    end = line.P2;
                    return true;
                default:
                    start = default;
                    end = default;
                    return false;
            }
        }

        private static double Distance(PointD start, PointD end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
