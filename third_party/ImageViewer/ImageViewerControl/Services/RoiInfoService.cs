using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Localization;
using ImageViewer.Plugins;
using ImageViewer.Utils;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services
{
    public static class RoiInfoService
    {
        public static string BuildInfo(RoiBase roi, BitmapSource? bitmap, double pixelSize, string? physicalUnit, RoiPluginRegistry pluginRegistry, bool includeStatistics = true, CameraCalibration? calibration = null)
        {
            ArgumentNullException.ThrowIfNull(roi);
            ImageViewerValidation.ValidatePixelSize(pixelSize);
            var roiPlugins = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            List<string> lines = new();
            if (!string.IsNullOrWhiteSpace(roi.Label))
            {
                lines.Add(roi.Label);
            }

            RoiInspectionResult inspection = RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration);
            if (inspection.DistortionAudit is { HasNonConvergedPoints: true } audit)
            {
                lines.Add(UiText.FormatInvariant("InfoLineCalibrationUndistortionWarning",
                    audit.NonConvergedPointCount, audit.PointCount, audit.MaximumResidualPixels, audit.MaximumUncertaintyPixels));
            }
            else if (RoiCalibrationHelper.CanUseMeasurements(calibration, inspection.DistortionAudit))
            {
                var pluginLines = roiPlugins.FindByRoi(roi)?.BuildInfoLines(roi, bitmap, pixelSize, physicalUnit);
                if (pluginLines is { Count: > 0 })
                {
                    lines.AddRange(pluginLines);
                }
                else
                {
                    AppendBuiltInInfoLines(lines, roi, pixelSize, physicalUnit, calibration, inspection);
                }
            }

            // Tolerance judgement is a cross-cutting inspection result.  It must
            // be shown even when a custom ROI plugin supplies its own geometry
            // lines; otherwise the information panel can omit the specification
            // decision for plugin-backed ROIs.
            AppendToleranceJudgementLine(lines, inspection);

            if (includeStatistics && bitmap != null && ImageAnalysisService.TryCalculateStatistics(bitmap, roi, out RoiStatistics statistics))
            {
                AppendStatisticsLines(lines, statistics);
            }

            AppendCalibrationLine(lines, pixelSize, physicalUnit, calibration);

            return string.Join(Environment.NewLine, lines);
        }

        public static bool TryBuildStatisticsText(BitmapSource? bitmap, RoiBase roi, out string text)
        {
            text = string.Empty;
            if (bitmap == null || !ImageAnalysisService.TryCalculateStatistics(bitmap, roi, out RoiStatistics statistics))
            {
                return false;
            }

            List<string> lines = new();
            AppendStatisticsLines(lines, statistics);
            text = string.Join(Environment.NewLine, lines);
            return true;
        }

        private static void AppendBuiltInInfoLines(List<string> lines, RoiBase roi, double pixelSize, string? physicalUnit, CameraCalibration? calibration, RoiInspectionResult inspection)
        {
            double correction = RoiCalibrationHelper.GetLengthCorrection(roi, calibration);
            double areaCorrection = RoiCalibrationHelper.GetAreaCorrection(roi, calibration);
            string FormatPrimaryLength() => FormatMeasuredLength(inspection.MeasuredValue, physicalUnit);

            switch (roi)
            {
                case BlobAnalysisRoi blob:
                    AppendBlobAnalysisInfoLines(lines, blob, pixelSize, physicalUnit, correction, areaCorrection);
                    break;
                case CaliperMeasureRoi caliper:
                    AppendDualEdgeCaliperInfoLines(lines, caliper, pixelSize, physicalUnit, correction, inspection.MeasuredValue);
                    break;
                case LineCaliperMeasureRoi lineCaliper:
                    AppendSingleEdgeCaliperInfoLines(
                        lines,
                        UiText.Get("InfoTitleLineCaliper"),
                        $"Distance:{FormatPrimaryLength()}",
                        lineCaliper,
                        pixelSize,
                        physicalUnit,
                        correction,
                        $"Angle:{lineCaliper.AngleDegrees:F2}°");
                    break;
                case ArcCaliperMeasureRoi arcCaliper:
                    AppendSingleEdgeCaliperInfoLines(
                        lines,
                        UiText.Get("InfoTitleArcCaliper"),
                        UiText.FormatInvariant("InfoLineArcCaliperGeometry", FormatPrimaryLength(),
                            inspection.MeasuredValue.HasValue ? FormatLength(arcCaliper.MeasurementRadius * correction, pixelSize, physicalUnit) : UiText.Get("MeasurementUnavailable"),
                            arcCaliper.SweepAngle),
                        arcCaliper,
                        pixelSize,
                        physicalUnit,
                        correction,
                        UiText.FormatInvariant("InfoLineArcCaliperDetail", arcCaliper.MeasurementCenter.X, arcCaliper.MeasurementCenter.Y, arcCaliper.StartAngle));
                    break;
                case CircularCaliperMeasureRoi circularCaliper:
                    AppendSingleEdgeCaliperInfoLines(
                        lines,
                        UiText.Get("InfoTitleCircularCaliper"),
                        $"Radius:{FormatPrimaryLength()}",
                        circularCaliper,
                        pixelSize,
                        physicalUnit,
                        correction,
                        UiText.FormatInvariant("InfoLineCircularCaliperDetail", circularCaliper.MeasurementCenter.X, circularCaliper.MeasurementCenter.Y));
                    break;
                case RotatedRect rect:
                    lines.Add(UiText.FormatInvariant("InfoLineRectDimensions", FormatLength(rect.Width * correction, pixelSize, physicalUnit), FormatLength(rect.Height * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLineArea", FormatArea(rect.Width * rect.Height * areaCorrection, pixelSize, physicalUnit)));
                    break;
                case FittedEllipseRoi fittedEllipse when fittedEllipse.HasFitEvidence:
                    lines.Add(UiText.FormatInvariant("InfoLineFittedEllipse", FormatLength(fittedEllipse.RadiusX * correction, pixelSize, physicalUnit), FormatLength(fittedEllipse.RadiusY * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLineAnglePoints", fittedEllipse.Angle, fittedEllipse.SourcePointCount));
                    if (fittedEllipse.FitInlierCount > 0)
                    {
                        lines.Add(UiText.FormatInvariant(
                            "InfoLineFitQuality",
                            FormatLength(fittedEllipse.FitResidualRms * correction, pixelSize, physicalUnit),
                            fittedEllipse.FitInlierCount,
                            fittedEllipse.FitOutlierCount,
                            fittedEllipse.FitAspectRatio));
                    }
                    break;
                case EllipseRoi ellipse:
                    lines.Add(UiText.FormatInvariant("InfoLineEllipse", FormatLength(ellipse.RadiusX * correction, pixelSize, physicalUnit), FormatLength(ellipse.RadiusY * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLineArea", FormatArea(Math.PI * ellipse.RadiusX * ellipse.RadiusY * areaCorrection, pixelSize, physicalUnit)));
                    break;
                case CircleRoi circle:
                    lines.Add(UiText.FormatInvariant("InfoLineCircle", FormatPrimaryLength()));
                    lines.Add(UiText.FormatInvariant("InfoLineArea", FormatArea(Math.PI * circle.Radius * circle.Radius * areaCorrection, pixelSize, physicalUnit)));
                    break;
                case RingRoi ring:
                    lines.Add(UiText.FormatInvariant("InfoLineRing", FormatLength(ring.InnerRadius * correction, pixelSize, physicalUnit), FormatLength(ring.OuterRadius * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLineArea", FormatArea(ring.Area * areaCorrection, pixelSize, physicalUnit)));
                    break;
                case PolygonRoi polygon:
                    Point[] polygonPoints = polygon.Points.ToWpfPointArray();
                    if (polygon.IsClosed && polygonPoints.Length >= 3)
                    {
                        var polygonMetrics = GeometryUtils.GetPolygonMetrics(polygonPoints);
                        lines.Add(UiText.FormatInvariant("InfoLinePolygon", FormatLength(polygonMetrics.Perimeter * correction, pixelSize, physicalUnit)));
                        lines.Add(UiText.FormatInvariant("InfoLinePolygonArea", FormatArea(polygonMetrics.Area * areaCorrection, pixelSize, physicalUnit)));
                        lines.Add(UiText.FormatInvariant("InfoLinePolygonDetail", polygonPoints.Length, polygonMetrics.Centroid.X, polygonMetrics.Centroid.Y));
                    }
                    else if (polygonPoints.Length > 1)
                    {
                        lines.Add(UiText.FormatInvariant("InfoLinePolygon", FormatLength(GeometryUtils.PolylineLength(polygonPoints) * correction, pixelSize, physicalUnit)));
                        lines.Add(UiText.Get("InfoLinePolygonOpen"));
                    }
                    break;
                case PolylineRoi polyline when polyline.Points.Count > 1:
                    Point[] polylinePoints = polyline.Points.ToWpfPointArray();
                    IReadOnlyList<double> segmentLengths = GeometryUtils.GetPolylineSegmentLengths(polylinePoints);
                    lines.Add(UiText.FormatInvariant("InfoLinePolyline", FormatLength(GeometryUtils.PolylineLength(polylinePoints) * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLinePolylineDetail", polylinePoints.Length, segmentLengths.Count, FormatLength(segmentLengths.Min() * correction, pixelSize, physicalUnit), FormatLength(segmentLengths.Max() * correction, pixelSize, physicalUnit)));
                    break;
                case ArrowAnnotationRoi arrow:
                    lines.Add(UiText.FormatInvariant("InfoLineArrow", FormatLength(GeometryUtils.Distance(arrow.P1.ToWpfPoint(), arrow.P2.ToWpfPoint()) * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLinePointPair", arrow.P1.X, arrow.P1.Y, arrow.P2.X, arrow.P2.Y));
                    break;
                case LineMeasureRoi line:
                    lines.Add(UiText.FormatInvariant("InfoLineLineMeasure", FormatPrimaryLength()));
                    lines.Add(UiText.FormatInvariant("InfoLinePointPair", line.P1.X, line.P1.Y, line.P2.X, line.P2.Y));
                    break;
                case AngleMeasureRoi angle:
                    lines.Add(UiText.FormatInvariant("InfoLineAngleMeasure", GeometryUtils.SmallestAngle(angle.P1.ToWpfPoint(), angle.Vertex.ToWpfPoint(), angle.P2.ToWpfPoint())));
                    break;
                case ArcMeasureRoi arc:
                    if (arc.IsValid)
                    {
                        lines.Add(UiText.FormatInvariant("InfoLineArcMeasureRadius", FormatPrimaryLength()));
                        lines.Add(UiText.FormatInvariant("InfoLineArcMeasureDetail", FormatLength(arc.ArcLength * correction, pixelSize, physicalUnit), arc.CentralAngle));
                        lines.Add(UiText.FormatInvariant("InfoLineCenterPoint", arc.Center.X, arc.Center.Y));
                    }
                    else
                    {
                        lines.Add(UiText.Get("InfoLineInvalidArc"));
                    }
                    break;
                case ThreePointCircleMeasureRoi threePointCircle:
                    if (threePointCircle.IsValid)
                    {
                        lines.Add(UiText.FormatInvariant("InfoLineThreePointCircle", FormatPrimaryLength()));
                        lines.Add(UiText.FormatInvariant("InfoLineThreePointCircleDetail", threePointCircle.Center.X, threePointCircle.Center.Y));
                    }
                    else
                    {
                        lines.Add(UiText.Get("InfoLineInvalidThreePointCircle"));
                    }
                    break;
                case PointToLineDistanceRoi pointToLine:
                    lines.Add(UiText.FormatInvariant("InfoLinePointToLine", FormatLength(pointToLine.Distance * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLinePointFoot", pointToLine.Point.X, pointToLine.Point.Y, pointToLine.FootPoint.X, pointToLine.FootPoint.Y));
                    break;
                case PointToCircleDistanceRoi pointToCircle:
                    lines.Add(UiText.FormatInvariant("InfoLinePointToCircle", FormatLength(pointToCircle.DistanceToCircle * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLinePointCircleDetail", pointToCircle.Center.X, pointToCircle.Center.Y, FormatLength(pointToCircle.Radius * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.FormatInvariant("InfoLineNearestPoint", pointToCircle.NearestPointOnCircle.X, pointToCircle.NearestPointOnCircle.Y));
                    break;
                case ParallelismMeasureRoi parallelism:
                    lines.Add(UiText.FormatInvariant("InfoLineParallelism", parallelism.AngleDifference));
                    lines.Add(UiText.FormatInvariant("InfoLineParallelismDetail", FormatLength(parallelism.AverageDistance * correction, pixelSize, physicalUnit), parallelism.ParallelismError));
                    lines.Add(UiText.Get("InfoLineAngularUnitNote"));
                    lines.Add(UiText.Get("InfoLineGeometricToleranceNote"));
                    break;
                case PerpendicularityMeasureRoi perpendicularity:
                    lines.Add(UiText.FormatInvariant("InfoLinePerpendicularity", perpendicularity.AngleBetweenLines));
                    lines.Add(UiText.FormatInvariant("InfoLinePerpendicularityDetail", perpendicularity.PerpendicularityError));
                    lines.Add(UiText.Get("InfoLineAngularUnitNote"));
                    lines.Add(UiText.Get("InfoLineGeometricToleranceNote"));
                    break;
                case ConcentricityMeasureRoi concentricity:
                    // 判定用的就是"公差带直径"(= 2 × 中心距)，界面必须显示同一个量，否则用户会拿中心距去比公差。
                    lines.Add(UiText.FormatInvariant(
                        "InfoLineConcentricity",
                        FormatPrimaryLength()));
                    lines.Add(UiText.FormatInvariant(
                        "InfoLineConcentricityDetail",
                        FormatLength(concentricity.CenterDistance * correction, pixelSize, physicalUnit),
                        FormatLength(concentricity.Radius1 * correction, pixelSize, physicalUnit),
                        FormatLength(concentricity.Radius2 * correction, pixelSize, physicalUnit)));
                    lines.Add(UiText.Get("InfoLineGeometricToleranceNote"));
                    break;
                case CenterDistanceMeasureRoi centerDistance:
                    lines.Add(UiText.FormatInvariant("InfoLineCenterDistance", FormatPrimaryLength()));
                    lines.Add(UiText.FormatInvariant("InfoLineCenterDistanceDetail", centerDistance.Center1.X, centerDistance.Center1.Y, centerDistance.Center2.X, centerDistance.Center2.Y));
                    break;
                case PointAnnotationRoi point:
                    lines.Add(UiText.FormatInvariant("InfoLinePointAnnotation", point.Position.X, point.Position.Y));
                    break;
                case PointCoordinateMeasureRoi pointCoordinate:
                    lines.Add(UiText.FormatInvariant("InfoLinePointCoordinate", pointCoordinate.Position.X, pointCoordinate.Position.Y));
                    if (pointCoordinate.IsEdgeSnapped)
                    {
                        lines.Add(UiText.FormatInvariant("InfoLinePointEdgeQuality", pointCoordinate.EdgeConfidence * 100, pointCoordinate.EdgeScore));
                    }
                    break;
                case TextAnnotationRoi text:
                    lines.Add(UiText.FormatInvariant("InfoLineTextAnnotation", text.Position.X, text.Position.Y));
                    break;
            }

        }

        /// <summary>
        /// 若 ROI 配置了公差，则追加合格/超差判定行（使用主测量值，物理单位）。
        /// Chinese: 主测量值仅对距离/半径类测量 ROI 有效；无主测量或未配置公差时不做判定。
        /// English: Appends a pass/fail judgement line when a tolerance is configured for the ROI.
        /// </summary>
        private static void AppendToleranceJudgementLine(List<string> lines, RoiInspectionResult inspection)
        {
            if (inspection.SpecificationStatus is not (RoiSpecificationStatus.Passed or RoiSpecificationStatus.Failed) ||
                inspection.MeasuredValue is not { } measured)
            {
                return;
            }

            string key = inspection.SpecificationStatus == RoiSpecificationStatus.Passed
                ? "InfoTolerancePass"
                : "InfoToleranceFail";
            lines.Add(UiText.FormatInvariant(key, measured, inspection.NominalValue ?? 0));
        }

        private static void AppendBlobAnalysisInfoLines(List<string> lines, BlobAnalysisRoi blob, double pixelSize, string? physicalUnit, double correction, double areaCorrection)
        {
            lines.Add(UiText.FormatInvariant("InfoLineBlobAnalysis", FormatLength(blob.Width * correction, pixelSize, physicalUnit), FormatLength(blob.Height * correction, pixelSize, physicalUnit), blob.Angle));
            lines.Add(blob.UseOtsu
                ? UiText.FormatInvariant("InfoLineBlobThresholdOtsu", blob.DetectDark ? UiText.Get("InfoPolarityDark") : UiText.Get("InfoPolarityLight"), blob.MinArea)
                : UiText.FormatInvariant("InfoLineBlobThresholdManual", blob.ManualThreshold, blob.DetectDark ? UiText.Get("InfoPolarityDark") : UiText.Get("InfoPolarityLight"), blob.MinArea));

            if (blob.DetectedBlobs.Count == 0)
            {
                lines.Add(UiText.FormatInvariant("InfoLineBlobCount", 0));
                return;
            }

            lines.Add(UiText.FormatInvariant("InfoLineBlobSummary", blob.DetectedBlobs.Count, FormatArea(blob.DetectedBlobs.Sum(item => item.Area) * areaCorrection, pixelSize, physicalUnit)));

            foreach (BlobFeature detectedBlob in blob.DetectedBlobs.OrderByDescending(item => item.Area).Take(5))
            {
                lines.Add(UiText.FormatInvariant("InfoLineBlobItem", detectedBlob.Label, FormatArea(detectedBlob.Area * areaCorrection, pixelSize, physicalUnit), FormatLength(detectedBlob.BoundingBox.Width * correction, pixelSize, physicalUnit), FormatLength(detectedBlob.BoundingBox.Height * correction, pixelSize, physicalUnit), detectedBlob.Centroid.X, detectedBlob.Centroid.Y));
                lines.Add(UiText.FormatInvariant("InfoLineBlobBox", detectedBlob.BoundingBox.X, detectedBlob.BoundingBox.Y, FormatLength(detectedBlob.BoundingBox.Width * correction, pixelSize, physicalUnit), FormatLength(detectedBlob.BoundingBox.Height * correction, pixelSize, physicalUnit)));
            }

            int remainingBlobCount = blob.DetectedBlobs.Count - 5;
            if (remainingBlobCount > 0)
            {
                lines.Add(UiText.FormatInvariant("InfoLineBlobRemaining", remainingBlobCount));
            }
        }

        private static string GetDisplayUnit(string? physicalUnit)
        {
            return string.IsNullOrWhiteSpace(physicalUnit) ? UiText.Get("InfoUnitPixels") : physicalUnit;
        }

        private static void AppendDualEdgeCaliperInfoLines(List<string> lines, CaliperMeasureRoi caliper, double pixelSize, string? physicalUnit, double correction, double? measuredWidth)
        {
            lines.Add(UiText.FormatInvariant("InfoLineCaliperMeasure", FormatMeasuredLength(measuredWidth, physicalUnit)));
            lines.Add(UiText.FormatInvariant("InfoLineRegionSearch", FormatLength(caliper.GetResolvedCaliperRegionLength() * correction, pixelSize, physicalUnit), FormatLength(caliper.CaliperSearchRange * 2 * correction, pixelSize, physicalUnit)));
            AppendCaliperParameterLine(lines, caliper);
            if (!caliper.HasDetectedEdges)
            {
                // 未检出时必须给出可操作的说明：否则面板只显示手画线长，用户会以为检测根本没跑。
                int invalidCalipers = caliper.InvalidCaliperMarkers.Length;
                lines.Add(invalidCalipers > 0
                    ? UiText.FormatInvariant(
                        "InfoLineCaliperValidCalipers",
                        caliper.CaliperCount - invalidCalipers,
                        caliper.CaliperCount,
                        caliper.MinimumValidCalipers)
                    : UiText.Get("InfoLineCaliperNotDetected"));
                return;
            }

            AppendDetectionQualityLines(lines, caliper);
            CaliperWidthMeasurementResult width = caliper.WidthMeasurement;
            if (width.IsMeasured)
            {
                lines.Add(UiText.FormatInvariant(
                    "InfoLineCaliperDetectedWidth",
                    FormatMeasuredLength(measuredWidth, physicalUnit),
                    FormatLength(width.Mean * correction, pixelSize, physicalUnit),
                    FormatLength(width.Median * correction, pixelSize, physicalUnit)));
                lines.Add(UiText.FormatInvariant(
                    "InfoLineCaliperWidthSpread",
                    FormatLength(width.Minimum * correction, pixelSize, physicalUnit),
                    FormatLength(width.Maximum * correction, pixelSize, physicalUnit),
                    FormatLength(width.Range * correction, pixelSize, physicalUnit),
                    FormatLength(width.StandardDeviation * correction, pixelSize, physicalUnit),
                    width.SampleCount));
            }
            lines.Add(UiText.FormatInvariant("InfoLineCaliperAngles", caliper.Edge1AngleDegrees, caliper.Edge2AngleDegrees, caliper.ParallelismErrorDegrees));
        }

        private static void AppendSingleEdgeCaliperInfoLines(List<string> lines, string title, string geometryLine, ICaliperInfoSource caliper, double pixelSize, string? physicalUnit, double correction, string? detectionDetailLine = null)
        {
            lines.Add(UiText.FormatInvariant("InfoLineSingleCaliper", title, geometryLine));
            lines.Add(UiText.FormatInvariant("InfoLineSingleSearch", FormatLength(caliper.CaliperSearchRange * 2 * correction, pixelSize, physicalUnit)));
            AppendCaliperParameterLine(lines, caliper);
            if (caliper is CircularCaliperMeasureRoi circularCaliper && circularCaliper is not ArcCaliperMeasureRoi)
            {
                lines.Add(GetCircularQualityLine(circularCaliper));
            }

            if (!caliper.HasDetection)
            {
                return;
            }

            AppendDetectionQualityLines(lines, caliper);
            if (!string.IsNullOrWhiteSpace(detectionDetailLine))
            {
                lines.Add(detectionDetailLine);
            }
        }

        private static void AppendCaliperParameterLine(List<string> lines, ICaliperInfoSource caliper)
        {
            lines.Add(UiText.FormatInvariant("InfoLineCaliperParameters", caliper.CaliperCount, caliper.CaliperSamplingHalfWidth * 2 + 1, caliper.CaliperEdgePolarity));
        }

        private static void AppendDetectionQualityLines(List<string> lines, ICaliperInfoSource caliper)
        {
            lines.Add(UiText.FormatInvariant("InfoLineFit", caliper.ValidCaliperCount, ImageAnalysisService.NormalizeCaliperScore(caliper.AverageScore)));
            lines.Add(UiText.FormatInvariant("InfoLineResidual", caliper.ResidualRms, caliper.ResidualMax));
            lines.Add(UiText.FormatInvariant("InfoLineConfidence", caliper.Confidence));
        }

        private static string GetCircularQualityLine(CircularCaliperMeasureRoi caliper)
        {
            return caliper.QualityStatus switch
            {
                CircularCaliperQualityStatus.Passed => UiText.FormatInvariant("InfoLineCircularQualityPassed", caliper.QualityAngularCoverageDegrees),
                CircularCaliperQualityStatus.Review => UiText.FormatInvariant("InfoLineCircularQualityReview", caliper.QualityAngularCoverageDegrees, GetCircularQualityReason(caliper.QualityReason)),
                CircularCaliperQualityStatus.Failed => UiText.FormatInvariant("InfoLineCircularQualityFailed", GetCircularQualityReason(caliper.QualityReason)),
                _ => UiText.Get("InfoLineCircularQualityNotMeasured")
            };
        }

        private static string GetCircularQualityReason(CircularCaliperQualityReason reason)
        {
            return reason switch
            {
                CircularCaliperQualityReason.DetectionFailed => UiText.Get("CircularQualityReasonDetectionFailed"),
                CircularCaliperQualityReason.InvalidGeometry => UiText.Get("CircularQualityReasonInvalidGeometry"),
                CircularCaliperQualityReason.LowConfidence => UiText.Get("CircularQualityReasonLowConfidence"),
                CircularCaliperQualityReason.InsufficientValidCalipers => UiText.Get("CircularQualityReasonInsufficientValidCalipers"),
                CircularCaliperQualityReason.InsufficientAngularCoverage => UiText.Get("CircularQualityReasonInsufficientCoverage"),
                CircularCaliperQualityReason.HighResidual => UiText.Get("CircularQualityReasonHighResidual"),
                CircularCaliperQualityReason.BorderlineEvidence => UiText.Get("CircularQualityReasonBorderlineEvidence"),
                _ => UiText.Get("CircularQualityReasonUnknown")
            };
        }

        private static void AppendStatisticsLines(List<string> lines, RoiStatistics statistics)
        {
            lines.Add(UiText.FormatInvariant("InfoLineStatisticsMean", statistics.Mean, statistics.Min, statistics.Max));
            lines.Add(UiText.FormatInvariant("InfoLineStatisticsStdDev", statistics.StandardDeviation, statistics.PixelCount));
        }

        private static void AppendCalibrationLine(List<string> lines, double pixelSize, string? physicalUnit, CameraCalibration? calibration)
        {
            if (!string.IsNullOrWhiteSpace(physicalUnit))
            {
                lines.Add(UiText.FormatInvariant("InfoLineCalibration", pixelSize, GetDisplayUnit(physicalUnit)));
            }

            if (calibration is null)
            {
                return;
            }

            // 畸变模型与精度元数据是两条独立证据：只有比例+精度记录（无畸变系数）也必须显示，
            // 否则用户看不到"这次标定准到什么程度"。
            if (calibration.IsValid && calibration.HasExtendedDistortionModel)
            {
                lines.Add(UiText.FormatInvariant(
                    "InfoLineCalibrationDistortionExtended",
                    calibration.K1,
                    calibration.K2,
                    calibration.K3,
                    calibration.TangentialP1,
                    calibration.TangentialP2,
                    calibration.ReprojectionErrorRms,
                    calibration.MeasurementUncertaintyPixels));
            }
            else if (calibration.IsValid && calibration.IsEnabled)
            {
                lines.Add(UiText.FormatInvariant("InfoLineCalibrationDistortion", calibration.K1, calibration.K2));
            }

            if (!calibration.HasExtendedDistortionModel && (calibration.ReprojectionErrorRms > 0 || calibration.MeasurementUncertaintyPixels > 0))
            {
                lines.Add(UiText.FormatInvariant(
                    "InfoLineCalibrationAccuracy",
                    calibration.ReprojectionErrorRms,
                    calibration.MeasurementUncertaintyPixels));
            }

            if (!calibration.IsValid)
            {
                lines.Add(UiText.Get("InfoLineCalibrationInvalid"));
            }
            else if (!string.IsNullOrWhiteSpace(calibration.CalibrationId))
            {
                lines.Add(UiText.FormatInvariant("InfoLineCalibrationIdentity", calibration.CalibrationId));
            }

        }

        private static string FormatLength(double pixelLength, double pixelSize, string? physicalUnit)
        {
            return $"{pixelLength * pixelSize:F2} {GetDisplayUnit(physicalUnit)}";
        }

        private static string FormatMeasuredLength(double? value, string? physicalUnit)
            => value is double number ? FormatLength(number, 1, physicalUnit) : UiText.Get("MeasurementUnavailable");

        private static string FormatArea(double pixelArea, double pixelSize, string? physicalUnit)
        {
            return $"{pixelArea * pixelSize * pixelSize:F2} {GetDisplayUnit(physicalUnit)}²";
        }
    }
}
