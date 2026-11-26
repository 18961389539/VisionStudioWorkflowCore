using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    internal static partial class ImageAnalysisService
    {
        /// <summary>
        /// 检测结果置信度低于该值时判定检测失败，避免把几近无意义的结果作为有效测量返回。
        /// Chinese: 对应"分数极低/残差过大/有效卡尺数过少"的综合结果拦截。
        /// English: Minimum confidence required for a detection to be considered valid.
        /// </summary>
        private readonly record struct CaliperEdgeSample(Point Point, double Score);

        internal static double NormalizeCaliperScore(double score, ImageAnalysisQualityProfile? profile = null)
        {
            ImageAnalysisQualityProfile quality = profile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();
            return Math.Clamp(score / quality.MaxCaliperScore * 100.0, 0, 100);
        }

        public static bool TryDetectLineMeasureEdges(BitmapSource bitmap, CaliperMeasureRoi line, out LineMeasureGradientDetectionResult result, ImageAnalysisQualityProfile? profile = null, IntensityImage? intensity = null)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentNullException.ThrowIfNull(line);
            line.EnsureCaliperRegion();
            if (line.CaliperSearchRange <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(line), line.CaliperSearchRange, "CaliperSearchRange must be positive.");
            }

            if (line.CaliperSamplingHalfWidth < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(line), line.CaliperSamplingHalfWidth, "CaliperSamplingHalfWidth must be non-negative.");
            }
            result = default;
            ImageAnalysisQualityProfile quality = profile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();

            Vector measurementDirection = line.GetCaliperMeasurementDirection().ToWpfVector();
            IntensityImage image = intensity ?? VisionAlgorithmEntryPoint.CreateIntensity(bitmap);

            Vector caliperDirection = new(-measurementDirection.Y, measurementDirection.X);
            Point measurementCenter = line.CaliperCenter.ToWpfPoint();
            int halfSearchRange = line.CaliperSearchRange;
            double regionHalfLength = line.GetResolvedCaliperRegionLength() / 2;
            int caliperCount = Math.Clamp(line.CaliperCount, 3, 31);
            int minimumValidCalipers = Math.Min(Math.Max(2, line.MinimumValidCalipers), caliperCount);

            List<Point> invalidCaliperCenters = new(caliperCount);
            List<CaliperEdgeSample> edge1Samples = new(caliperCount);
            List<CaliperEdgeSample> edge2Samples = new(caliperCount);

            for (int i = 0; i < caliperCount; i++)
            {
                double lerp = caliperCount == 1 ? 0.5 : (double)i / (caliperCount - 1);
                double tangentOffset = -regionHalfLength + regionHalfLength * 2 * lerp;
                Point caliperCenter = measurementCenter + caliperDirection * tangentOffset;
                if (!VisionAlgorithmEntryPoint.TryMeasurePair(
                        image,
                        caliperCenter,
                        measurementDirection,
                        halfSearchRange,
                        line.CaliperSamplingHalfWidth,
                        line.CaliperEdgeSigma,
                        line.CaliperMinimumGradient,
                        line.CaliperEdgePolarity,
                        line.MinimumEdgeGap,
                        line.NominalEdgeGap,
                        line.NominalEdgeGapTolerance,
                        out CaliperEdgePair nativePair))
                {
                    invalidCaliperCenters.Add(caliperCenter);
                    continue;
                }

                edge1Samples.Add(new CaliperEdgeSample(nativePair.Edge1.Point.ToWpfPoint(), nativePair.Edge1.Score));
                edge2Samples.Add(new CaliperEdgeSample(nativePair.Edge2.Point.ToWpfPoint(), nativePair.Edge2.Score));
            }

            if (edge1Samples.Count < minimumValidCalipers || edge2Samples.Count < minimumValidCalipers)
            {
                return false;
            }

            List<CaliperEdgeSample> filteredEdge1Samples = FilterInlierSamples(edge1Samples, caliperDirection, regionHalfLength, line.CaliperOutlierThreshold, minimumValidCalipers, line.CaliperLineFitMode, line.CaliperFitClippingEndPoints);
            List<CaliperEdgeSample> filteredEdge2Samples = FilterInlierSamples(edge2Samples, caliperDirection, regionHalfLength, line.CaliperOutlierThreshold, minimumValidCalipers, line.CaliperLineFitMode, line.CaliperFitClippingEndPoints);
            if (filteredEdge1Samples.Count < minimumValidCalipers || filteredEdge2Samples.Count < minimumValidCalipers)
            {
                return false;
            }

            Point[] filteredEdge1Points = [..filteredEdge1Samples.Select(sample => sample.Point)];
            Point[] filteredEdge2Points = [..filteredEdge2Samples.Select(sample => sample.Point)];
            Point[] rejectedEdge1Points = [..edge1Samples.Where(sample => !filteredEdge1Samples.Contains(sample)).Select(sample => sample.Point)];
            Point[] rejectedEdge2Points = [..edge2Samples.Where(sample => !filteredEdge2Samples.Contains(sample)).Select(sample => sample.Point)];
            if (!TryFitLine(filteredEdge1Points, line.CaliperLineFitMode, line.CaliperFitClippingEndPoints, out LineSegmentOverlay fittedEdge1) ||
                !TryFitLine(filteredEdge2Points, line.CaliperLineFitMode, line.CaliperFitClippingEndPoints, out LineSegmentOverlay fittedEdge2))
            {
                return false;
            }
            LineFitGeometry fittedEdge1Geometry = LineFitGeometry.FromSegment(new DetectedLineSegment(fittedEdge1.Start, fittedEdge1.End));
            LineFitGeometry fittedEdge2Geometry = LineFitGeometry.FromSegment(new DetectedLineSegment(fittedEdge2.Start, fittedEdge2.End));
            if (!fittedEdge1Geometry.TryIntersect(measurementCenter.ToPointD(), measurementDirection.ToVectorD(), out PointD detectedP1D) ||
                !fittedEdge2Geometry.TryIntersect(measurementCenter.ToPointD(), measurementDirection.ToVectorD(), out PointD detectedP2D))
            {
                return false;
            }

            Point detectedP1 = detectedP1D.ToWpfPoint();
            Point detectedP2 = detectedP2D.ToWpfPoint();

            if (GeometryUtils.Distance(detectedP1, detectedP2) <= 0.5)
            {
                return false;
            }

            double edge1AverageScore = filteredEdge1Samples.Average(sample => sample.Score);
            double edge2AverageScore = filteredEdge2Samples.Average(sample => sample.Score);
            (double edge1ResidualRms, double edge1ResidualMax) = ComputeResidualMetrics(filteredEdge1Points, fittedEdge1);
            (double edge2ResidualRms, double edge2ResidualMax) = ComputeResidualMetrics(filteredEdge2Points, fittedEdge2);
            double edge1AngleDegrees = fittedEdge1Geometry.AngleDegrees;
            double edge2AngleDegrees = fittedEdge2Geometry.AngleDegrees;
            double parallelismErrorDegrees = Math.Abs(edge1AngleDegrees - edge2AngleDegrees);
            parallelismErrorDegrees = parallelismErrorDegrees > 90 ? 180 - parallelismErrorDegrees : parallelismErrorDegrees;
            double confidence = ComputeConfidence(edge1AverageScore, edge2AverageScore, edge1ResidualRms, edge2ResidualRms, parallelismErrorDegrees, Math.Min(filteredEdge1Points.Length, filteredEdge2Points.Length), caliperCount);
            if (confidence < quality.MinimumDetectionConfidence)
            {
                return false;
            }

            result = new LineMeasureGradientDetectionResult(
                detectedP1,
                detectedP2,
                [..invalidCaliperCenters],
                [..filteredEdge1Points],
                [..filteredEdge2Points],
                [..rejectedEdge1Points],
                [..rejectedEdge2Points],
                [..filteredEdge1Samples.Select(sample => sample.Score)],
                [..filteredEdge2Samples.Select(sample => sample.Score)],
                new DetectedLineSegment(fittedEdge1.Start, fittedEdge1.End),
                new DetectedLineSegment(fittedEdge2.Start, fittedEdge2.End),
                edge1AverageScore,
                edge2AverageScore,
                edge1ResidualRms,
                edge2ResidualRms,
                edge1ResidualMax,
                edge2ResidualMax,
                Math.Min(filteredEdge1Points.Length, filteredEdge2Points.Length),
                edge1AngleDegrees,
                edge2AngleDegrees,
                parallelismErrorDegrees,
                confidence,
                BuildCaliperWidthSamples(filteredEdge1Samples, filteredEdge2Samples, measurementCenter, measurementDirection, caliperDirection));

            return true;
        }

        public static bool TryDetectLineCaliperEdges(BitmapSource bitmap, LineCaliperMeasureRoi line, out LineCaliperDetectionResult result, ImageAnalysisQualityProfile? profile = null, IntensityImage? intensity = null)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentNullException.ThrowIfNull(line);
            result = default;
            ImageAnalysisQualityProfile quality = profile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();

            Vector lineDirection = (line.P2 - line.P1).ToWpfVector();
            double lineLength = lineDirection.Length;
            if (lineLength <= 0.5 || line.CaliperSearchRange <= 0)
            {
                return false;
            }

            lineDirection.Normalize();
            Vector measurementDirection = new(-lineDirection.Y, lineDirection.X);

            IntensityImage image = intensity ?? VisionAlgorithmEntryPoint.CreateIntensity(bitmap);

            int caliperCount = Math.Clamp(line.CaliperCount, 6, 180);
            int minimumValidCalipers = Math.Min(Math.Max(3, line.MinimumValidCalipers), caliperCount);
            List<Point> invalidCaliperCenters = new(caliperCount);
            List<CaliperEdgeSample> edgeSamples = new(caliperCount);

            for (int i = 0; i < caliperCount; i++)
            {
                double lerp = caliperCount == 1 ? 0.5 : (double)i / (caliperCount - 1);
                Point sampleCenter = new(
                    line.P1.X + (line.P2.X - line.P1.X) * lerp,
                    line.P1.Y + (line.P2.Y - line.P1.Y) * lerp);
                if (!VisionAlgorithmEntryPoint.TryMeasureSingle(
                        image,
                        sampleCenter,
                        measurementDirection,
                        line.CaliperSearchRange,
                        line.CaliperSamplingHalfWidth,
                        line.CaliperEdgeSigma,
                        line.CaliperMinimumGradient,
                        line.CaliperEdgePolarity,
                        line.EdgeSelection,
                        out CaliperEdge nativeSample))
                {
                    invalidCaliperCenters.Add(sampleCenter);
                    continue;
                }

                edgeSamples.Add(new CaliperEdgeSample(nativeSample.Point.ToWpfPoint(), nativeSample.Score));
            }

            if (edgeSamples.Count < minimumValidCalipers)
            {
                return false;
            }

            List<CaliperEdgeSample> filteredSamples = FilterInlierSamples(edgeSamples, lineDirection, lineLength / 2, line.CaliperOutlierThreshold, minimumValidCalipers, line.CaliperLineFitMode, line.CaliperFitClippingEndPoints);
            if (filteredSamples.Count < minimumValidCalipers)
            {
                return false;
            }

            Point[] filteredPoints = [..filteredSamples.Select(sample => sample.Point)];
            Point[] rejectedPoints = [..edgeSamples.Where(sample => !filteredSamples.Contains(sample)).Select(sample => sample.Point)];
            if (!TryFitLine(filteredPoints, line.CaliperLineFitMode, line.CaliperFitClippingEndPoints, out LineSegmentOverlay fittedLine))
            {
                return false;
            }
            LineFitGeometry fittedGeometry = LineFitGeometry.FromSegment(new DetectedLineSegment(fittedLine.Start, fittedLine.End));
            if (!fittedGeometry.IsValid)
            {
                return false;
            }

            Point detectedP1 = fittedGeometry.Project(line.P1).ToWpfPoint();
            Point detectedP2 = fittedGeometry.Project(line.P2).ToWpfPoint();
            if (GeometryUtils.Distance(detectedP1, detectedP2) <= 0.5)
            {
                return false;
            }

            double averageScore = filteredSamples.Average(sample => sample.Score);
            (double residualRms, double residualMax) = ComputeResidualMetrics(filteredPoints, fittedLine);
            double angleDegrees = fittedGeometry.AngleDegrees;
            double confidence = ComputeCircularConfidence(averageScore, residualRms, filteredPoints.Length, caliperCount);
            if (confidence < quality.MinimumDetectionConfidence)
            {
                return false;
            }

            result = new LineCaliperDetectionResult(
                line.P1.ToWpfPoint(),
                line.P2.ToWpfPoint(),
                detectedP1,
                detectedP2,
                [..invalidCaliperCenters],
                filteredPoints,
                [..rejectedPoints],
                [..filteredSamples.Select(sample => sample.Score)],
                new DetectedLineSegment(fittedLine.Start, fittedLine.End),
                averageScore,
                residualRms,
                residualMax,
                filteredPoints.Length,
                angleDegrees,
                confidence);

            return true;
        }

        public static bool TryDetectCircularCaliperEdges(BitmapSource bitmap, CircularCaliperMeasureRoi caliper, out CircularCaliperDetectionResult result, ImageAnalysisQualityProfile? profile = null, IntensityImage? intensity = null)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentNullException.ThrowIfNull(caliper);
            result = default;
            ImageAnalysisQualityProfile quality = profile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();

            if (caliper is ArcCaliperMeasureRoi arcCaliper)
            {
                return TryDetectArcCaliperEdges(bitmap, arcCaliper, out result, quality, intensity);
            }

            if (caliper.Radius <= 0 || caliper.CaliperSearchRange <= 0)
            {
                return false;
            }

            IntensityImage image = intensity ?? VisionAlgorithmEntryPoint.CreateIntensity(bitmap);
            return TryDetectCircularCaliperEdgesCore(image, caliper, out result, quality, preferNearestRadialEdge: false);
        }

        /// <summary>
        /// 圆卡尺检测核心：通过径向卡尺做径向边缘采样，再进行鲁棒拟合与质量评估。
        /// Chinese: 每个径向卡尺沿半径方向测量，自动圆搜索仍保持独立的粗定位流程。
        /// English: Core circular caliper detection using a radial caliper for each
        /// radial sample, followed by the existing fit and quality evaluation.
        /// </summary>
        private static bool TryDetectCircularCaliperEdgesCore(
            IntensityImage image,
            CircularCaliperMeasureRoi caliper,
            out CircularCaliperDetectionResult result,
            ImageAnalysisQualityProfile quality,
            bool preferNearestRadialEdge)
        {
            result = default;
            int caliperCount = Math.Clamp(caliper.CaliperCount, 6, 180);
            int minimumValidCalipers = Math.Min(Math.Max(3, caliper.MinimumValidCalipers), caliperCount);
            List<Point> invalidSamplePoints = new(caliperCount);
            List<CaliperEdgeSample> edgeSamples = new(caliperCount);
            for (int i = 0; i < caliperCount; i++)
            {
                double angleRadians = i * Math.PI * 2 / caliperCount;
                Vector radialDirection = new(Math.Cos(angleRadians), Math.Sin(angleRadians));
                Point origin = caliper.Center.ToWpfPoint();
                Point sampleCenter = origin + radialDirection * caliper.Radius;
                bool measured;
                CaliperEdge nativeSample = default;
                if (preferNearestRadialEdge && caliper.Radius > 64)
                {
                    sampleCenter = origin;
                    measured = VisionAlgorithmEntryPoint.TryMeasureSingle(
                        image, sampleCenter,
                        radialDirection, Math.Min(caliper.CaliperSearchRange, 128),
                        caliper.CaliperSamplingHalfWidth, caliper.CaliperEdgeSigma,
                        caliper.CaliperMinimumGradient, caliper.CaliperEdgePolarity,
                        edgeSelection: 1, out nativeSample);
                }
                else if (preferNearestRadialEdge)
                {
                    measured = VisionAlgorithmEntryPoint.TryMeasureSingle(
                        image, sampleCenter,
                        radialDirection, caliper.CaliperSearchRange,
                        caliper.CaliperSamplingHalfWidth, caliper.CaliperEdgeSigma,
                        caliper.CaliperMinimumGradient, caliper.CaliperEdgePolarity,
                        edgeSelection: 1, out nativeSample);
                }
                else
                {
                    // The precision radial path mirrors the direction into the caliper's native
                    // y-up axis so the profile points truly outward (see TryMeasureCircular). The
                    // coarse nearest-edge sweep above keeps the native rectangular direction.
                    Vector caliperDirection = new(radialDirection.X, -radialDirection.Y);
                    measured = VisionAlgorithmEntryPoint.TryMeasureSingle(
                        image, sampleCenter,
                        caliperDirection, caliper.CaliperSearchRange,
                        caliper.CaliperSamplingHalfWidth, caliper.CaliperEdgeSigma,
                        caliper.CaliperMinimumGradient, caliper.CaliperEdgePolarity,
                        caliper.EdgeSelection, out nativeSample);
                }
                if (!measured)
                {
                    invalidSamplePoints.Add(sampleCenter);
                    continue;
                }
                edgeSamples.Add(new CaliperEdgeSample(nativeSample.Point.ToWpfPoint(), nativeSample.Score));
            }
            if (edgeSamples.Count < minimumValidCalipers)
            {
                return false;
            }
            List<CaliperEdgeSample> filteredSamples = FilterCircularInlierSamples(
                edgeSamples, preferNearestRadialEdge ? 2.5 : caliper.CaliperOutlierThreshold,
                minimumValidCalipers, caliper.Center.ToWpfPoint(), caliper.Radius,
                caliper.CaliperLineFitMode);
            if (filteredSamples.Count < minimumValidCalipers)
            {
                return false;
            }
            Point[] filteredPoints = [..filteredSamples.Select(sample => sample.Point)];
            Point[] rejectedPoints = [..edgeSamples.Where(sample => !filteredSamples.Contains(sample)).Select(sample => sample.Point)];
            if (!TryFitCircle(filteredPoints, caliper.CaliperLineFitMode, out Point detectedCenter, out double detectedRadius) || detectedRadius <= 0)
            {
                return false;
            }
            (double residualRms, double residualMax) = ComputeCircularResidualMetrics(filteredPoints, detectedCenter, detectedRadius);
            double averageScore = filteredSamples.Average(sample => sample.Score);
            double confidence = ComputeCircularConfidence(averageScore, residualRms, filteredPoints.Length, caliperCount);
            if (confidence < quality.MinimumDetectionConfidence)
            {
                return false;
            }
            result = new CircularCaliperDetectionResult(
                caliper.Center.ToWpfPoint(), caliper.Radius, detectedCenter, detectedRadius,
                [..invalidSamplePoints], filteredPoints, rejectedPoints,
                [..filteredSamples.Select(sample => sample.Score)], averageScore,
                residualRms, residualMax, filteredPoints.Length, confidence);
            return true;
        }

        private static double[] BuildCaliperWidthSamples(
            IReadOnlyList<CaliperEdgeSample> edge1Samples,
            IReadOnlyList<CaliperEdgeSample> edge2Samples,
            Point measurementCenter,
            Vector measurementDirection,
            Vector caliperDirection)
        {
            var edge1ByOffset = edge1Samples
                .Select(sample => (sample, offset: ProjectAlong(sample.Point, measurementCenter, caliperDirection)))
                .OrderBy(item => item.offset)
                .ToArray();
            var edge2ByOffset = edge2Samples
                .Select(sample => (sample, offset: ProjectAlong(sample.Point, measurementCenter, caliperDirection)))
                .OrderBy(item => item.offset)
                .ToArray();
            bool[] usedEdge2 = new bool[edge2ByOffset.Length];
            var widths = new List<(double offset, double width)>(Math.Min(edge1ByOffset.Length, edge2ByOffset.Length));

            foreach (var edge1 in edge1ByOffset)
            {
                int bestIndex = -1;
                double bestOffsetDifference = double.PositiveInfinity;
                for (int i = 0; i < edge2ByOffset.Length; i++)
                {
                    if (usedEdge2[i])
                    {
                        continue;
                    }

                    double offsetDifference = Math.Abs(edge1.offset - edge2ByOffset[i].offset);
                    if (offsetDifference < bestOffsetDifference)
                    {
                        bestIndex = i;
                        bestOffsetDifference = offsetDifference;
                    }
                }

                if (bestIndex < 0)
                {
                    continue;
                }

                usedEdge2[bestIndex] = true;
                CaliperEdgeSample edge2 = edge2ByOffset[bestIndex].sample;
                double width = Math.Abs(
                    (edge2.Point.X - edge1.sample.Point.X) * measurementDirection.X +
                    (edge2.Point.Y - edge1.sample.Point.Y) * measurementDirection.Y);
                if (double.IsFinite(width) && width > 0.5)
                {
                    widths.Add(((edge1.offset + edge2ByOffset[bestIndex].offset) / 2, width));
                }
            }

            return widths
                .OrderBy(item => item.offset)
                .Select(item => item.width)
                .ToArray();
        }

        private static double ProjectAlong(Point point, Point origin, Vector direction)
        {
            return (point.X - origin.X) * direction.X + (point.Y - origin.Y) * direction.Y;
        }

        /// <summary>
        /// 从一个近似落点自动搜索整幅图像范围内的圆边缘。
        /// Chinese: 自动圆工具只需要一个落点；先用粗步长径向扫描确定候选半径，再只在该半径附近的窄带内做完整精度检测，
        /// 复用圆形卡尺的鲁棒拟合与质量评估，避免为一次点击逐像素扫过整幅图像。
        /// English: Finds a circle from a single approximate click: a coarse radial scan proposes the candidate
        /// radius, then the full-precision circular caliper pipeline runs on a narrow band around it, so one click
        /// never has to sample every pixel of a full-image search.
        /// </summary>
        internal static bool TryDetectAutomaticCircle(BitmapSource bitmap, Point seed, out CircularCaliperMeasureRoi roi, ImageAnalysisQualityProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            roi = null!;

            if (bitmap.PixelWidth < 8 || bitmap.PixelHeight < 8 ||
                double.IsNaN(seed.X) || double.IsNaN(seed.Y) ||
                double.IsInfinity(seed.X) || double.IsInfinity(seed.Y))
            {
                return false;
            }

            double maxDistance = 0;
            foreach (Point corner in new[]
            {
                new Point(0, 0),
                new Point(bitmap.PixelWidth - 1, 0),
                new Point(0, bitmap.PixelHeight - 1),
                new Point(bitmap.PixelWidth - 1, bitmap.PixelHeight - 1)
            })
            {
                maxDistance = Math.Max(maxDistance, GeometryUtils.Distance(seed, corner));
            }

            // The click is only a seed. Run a full radial consensus pass
            // around it so missing arcs and moderate noise are rejected by the inlier filter
            // instead of becoming a biased coarse fit.
            ImageAnalysisQualityProfile quality = profile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();
            double searchExtent = Math.Clamp(maxDistance, 18, 4096);
            int searchRange = Math.Max(9, (int)Math.Ceiling(searchExtent));
            var candidate = new CircularCaliperMeasureRoi
            {
                Center = seed.ToPointD(),
                Radius = searchRange,
                CaliperCount = 96,
                CaliperSearchRange = searchRange,
                CaliperSamplingHalfWidth = 2,
                CaliperEdgeSigma = 1.0,
                MinimumValidCalipers = Math.Min(quality.AutomaticCircleMinimumValidCalipers, 8),
                CaliperMinimumGradient = Math.Min(quality.AutomaticCircleCaliperMinimumGradient, 4.0),
                CaliperOutlierThreshold = 2.5,
                CaliperEdgePolarity = CaliperEdgePolarity.Any,
                EdgeSelection = 1
            };
            IntensityImage image = VisionAlgorithmEntryPoint.CreateIntensity(bitmap);
            ImageAnalysisQualityProfile automaticQuality = quality with
            {
                MinimumDetectionConfidence = Math.Min(quality.MinimumDetectionConfidence, 0.02)
            };
            if (!TryDetectCircularCaliperEdgesCore(image, candidate, out CircularCaliperDetectionResult detection, automaticQuality, preferNearestRadialEdge: true))
            {
                return false;
            }
            double angularCoverage = ComputeCircularAngularCoverageDegrees(detection.EdgePoints, detection.DetectedCenter);
            int imageBoundaryPointCount = detection.EdgePoints.Count(point =>
                point.X <= 1.5 ||
                point.Y <= 1.5 ||
                point.X >= bitmap.PixelWidth - 2.5 ||
                point.Y >= bitmap.PixelHeight - 2.5);
            bool mostlyImageBoundaryEvidence = imageBoundaryPointCount >= Math.Max(
                candidate.MinimumValidCalipers,
                (int)Math.Ceiling(detection.EdgePoints.Length * quality.AutomaticCircleBoundaryEvidenceRatio));
            if (detection.ValidCaliperCount < candidate.MinimumValidCalipers ||
                detection.DetectedRadius < 2 ||
                detection.DetectedRadius > searchExtent * 1.15 ||
                detection.ResidualRms > Math.Max(quality.AutomaticCircleMinimumResidualPixels, detection.DetectedRadius * quality.AutomaticCircleResidualFraction) ||
                 detection.Confidence < automaticQuality.MinimumDetectionConfidence ||
                angularCoverage < quality.AutomaticCircleMinimumAngularCoverageDegrees ||
                mostlyImageBoundaryEvidence)
            {
                return false;
            }

            // Store the fitted circle as the ROI geometry. The click seed and wide search radius
            // are only inputs to detection; they are not the geometry the user edits or exports.
            candidate.Center = detection.DetectedCenter.ToPointD();
            candidate.Radius = detection.DetectedRadius;

            // Keep the committed ROI compact and editable after the wide search has succeeded.
            // Subsequent refreshes use a local caliper around the fitted circle.
            candidate.CaliperSearchRange = Math.Clamp((int)Math.Ceiling(detection.DetectedRadius * 0.15), 8, 64);
            CircularCaliperDetectionResult displayDetection = detection with
            {
                ReferenceCenter = detection.DetectedCenter,
                ReferenceRadius = detection.DetectedRadius
            };
            RoiDetectionResultMapper.Apply(candidate, displayDetection.ToCore());
            roi = candidate;
            return true;
        }

        /// <summary>
        /// 计算边缘点覆盖的最大圆周角度。
        /// Chinese: 仅有一小段弧线也可能得到很小的拟合残差，因此用最大角间隙衡量边缘是否覆盖了足够的圆周。
        /// English: Measures the largest angular span covered by edge points. A short arc can have a small fit residual,
        /// so the maximum angular gap is used to ensure the evidence covers enough of the circumference.
        /// </summary>
        private static double ComputeCircularAngularCoverageDegrees(IReadOnlyList<Point> points, Point center)
        {
            if (points.Count < 3)
            {
                return 0;
            }

            List<double> angles = new(points.Count);
            foreach (Point point in points)
            {
                double angle = Math.Atan2(point.Y - center.Y, point.X - center.X);
                if (angle < 0)
                {
                    angle += Math.PI * 2;
                }

                angles.Add(angle);
            }

            angles.Sort();
            double largestGap = 0;
            for (int i = 1; i < angles.Count; i++)
            {
                largestGap = Math.Max(largestGap, angles[i] - angles[i - 1]);
            }

            largestGap = Math.Max(largestGap, angles[0] + Math.PI * 2 - angles[^1]);
            return Math.Max(0, (Math.PI * 2 - largestGap) * 180 / Math.PI);
        }

        /// <summary>
        /// 在点击点周围沿多方向使用线性卡尺搜索局部边缘并返回最佳点。
        /// </summary>
        internal static bool TrySnapPointToEdge(BitmapSource bitmap, Point seed, out Point snapped, out double score, out double confidence, ImageAnalysisQualityProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            snapped = default;
            score = 0;
            confidence = 0;
            ImageAnalysisQualityProfile quality = profile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();

            if (bitmap.PixelWidth < 5 || bitmap.PixelHeight < 5 ||
                double.IsNaN(seed.X) || double.IsNaN(seed.Y) ||
                double.IsInfinity(seed.X) || double.IsInfinity(seed.Y))
            {
                return false;
            }

            const int searchRange = 12;
            const int averagingHalfWidth = 1;
            const int directionCount = 16;
            const double minimumGradient = 10;

            // The probe never samples farther than searchRange + averagingHalfWidth from the
            // seed, so converting a clamped local window keeps snap feedback interactive on
            // large images instead of rebuilding a full-image intensity plane per pointer move.
            const int windowMargin = 2;
            int windowHalfExtent = searchRange + averagingHalfWidth + windowMargin;
            int windowX = Math.Max(0, (int)Math.Floor(seed.X) - windowHalfExtent);
            int windowY = Math.Max(0, (int)Math.Floor(seed.Y) - windowHalfExtent);
            int windowRight = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(seed.X) + windowHalfExtent + 1);
            int windowBottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(seed.Y) + windowHalfExtent + 1);
            if (windowRight <= windowX || windowBottom <= windowY)
            {
                return false;
            }

            var window = new Int32Rect(windowX, windowY, windowRight - windowX, windowBottom - windowY);
            IntensityImage image = VisionAlgorithmEntryPoint.CreateIntensity(bitmap, window);
            var localSeed = new Point(seed.X - windowX, seed.Y - windowY);
            var candidates = new List<CaliperEdgeSample>(directionCount);
            for (int i = 0; i < directionCount; i++)
            {
                double angle = i * Math.PI / directionCount;
                Vector direction = new(Math.Cos(angle), Math.Sin(angle));
                if (VisionAlgorithmEntryPoint.TryMeasureSingle(
                        image,
                        localSeed,
                        direction,
                        searchRange,
                        averagingHalfWidth,
                        sigma: 1.0,
                        minimumGradient,
                        CaliperEdgePolarity.Any,
                        edgeSelection: 1,
                        out CaliperEdge nativeCandidate))
                {
                    candidates.Add(new CaliperEdgeSample(nativeCandidate.Point.ToWpfPoint(), nativeCandidate.Score));
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            CaliperEdgeSample best = candidates
                .OrderByDescending(candidate => candidate.Score * (0.65 + 0.35 * Math.Clamp(1 - GeometryUtils.Distance(localSeed, candidate.Point) / searchRange, 0, 1)))
                .First();
            double distance = GeometryUtils.Distance(localSeed, best.Point);
            if (best.Score < minimumGradient || distance > searchRange + 1)
            {
                return false;
            }

            snapped = new Point(best.Point.X + windowX, best.Point.Y + windowY);
            score = best.Score;
            int consensusCount = candidates.Count(candidate => GeometryUtils.Distance(candidate.Point, best.Point) <= 3.0);
            double consensus = Math.Clamp((double)consensusCount / Math.Max(2, directionCount * 0.35), 0, 1);
            confidence = Math.Clamp(
                0.7 * best.Score / quality.MaxCaliperScore +
                0.3 * (1 - Math.Min(distance / searchRange, 1)),
                0,
                1);
            confidence *= 0.65 + 0.35 * consensus;
            return confidence >= quality.MinimumDetectionConfidence;
        }

        private static bool TryDetectArcCaliperEdges(BitmapSource bitmap, ArcCaliperMeasureRoi caliper, out CircularCaliperDetectionResult result, ImageAnalysisQualityProfile quality, IntensityImage? intensity = null)
        {
            result = default;
            if (caliper.Radius <= 0 || caliper.CaliperSearchRange <= 0 || Math.Abs(caliper.SweepAngle) < 1)
            {
                return false;
            }

            IntensityImage image = intensity ?? VisionAlgorithmEntryPoint.CreateIntensity(bitmap);

            int caliperCount = Math.Clamp(caliper.CaliperCount, 4, 180);
            int minimumValidCalipers = Math.Min(Math.Max(3, caliper.MinimumValidCalipers), caliperCount);
            List<Point> invalidSamplePoints = new(caliperCount);
            List<CaliperEdgeSample> edgeSamples = new(caliperCount);

            for (int i = 0; i < caliperCount; i++)
            {
                double angleDegrees = caliper.StartAngle + (caliperCount == 1 ? 0 : caliper.SweepAngle * i / (caliperCount - 1));
                double angleRadians = angleDegrees * Math.PI / 180.0;
                Vector radialDirection = new(Math.Cos(angleRadians), Math.Sin(angleRadians));
                Point sampleCenter = caliper.Center.ToWpfPoint() + radialDirection * caliper.Radius;
                double halfAngularWidth = Math.Atan2(
                    Math.Max(0.5, caliper.CaliperSamplingHalfWidth),
                    caliper.Radius);
                if (!VisionAlgorithmEntryPoint.TryMeasureCircular(
                        image,
                        caliper.Center.ToWpfPoint(),
                        caliper.Radius,
                        angleRadians,
                        Math.Clamp(2 * halfAngularWidth, 1e-3, 2 * Math.PI),
                        caliper.CaliperSearchRange,
                        caliper.CaliperSamplingHalfWidth,
                        caliper.CaliperEdgeSigma,
                        caliper.CaliperMinimumGradient,
                        caliper.CaliperEdgePolarity,
                        caliper.EdgeSelection,
                        out CaliperEdge nativeSample))
                {
                    invalidSamplePoints.Add(sampleCenter);
                    continue;
                }

                edgeSamples.Add(new CaliperEdgeSample(nativeSample.Point.ToWpfPoint(), nativeSample.Score));
            }

            if (edgeSamples.Count < minimumValidCalipers)
            {
                return false;
            }

            List<CaliperEdgeSample> filteredSamples = FilterCircularInlierSamples(
                edgeSamples,
                caliper.CaliperOutlierThreshold,
                minimumValidCalipers,
                caliper.Center.ToWpfPoint(),
                caliper.Radius,
                caliper.CaliperLineFitMode);
            if (filteredSamples.Count < minimumValidCalipers)
            {
                return false;
            }

            Point[] filteredPoints = [..filteredSamples.Select(sample => sample.Point)];
            Point[] rejectedPoints = [..edgeSamples.Where(sample => !filteredSamples.Contains(sample)).Select(sample => sample.Point)];
            if (!TryFitCircle(filteredPoints, caliper.CaliperLineFitMode, out Point detectedCenter, out double detectedRadius) || detectedRadius <= 0)
            {
                return false;
            }

            (double residualRms, double residualMax) = ComputeCircularResidualMetrics(filteredPoints, detectedCenter, detectedRadius);
            double averageScore = filteredSamples.Average(sample => sample.Score);
            double confidence = ComputeCircularConfidence(averageScore, residualRms, filteredPoints.Length, caliperCount);
            if (confidence < quality.MinimumDetectionConfidence)
            {
                return false;
            }

            result = new CircularCaliperDetectionResult(
                caliper.Center.ToWpfPoint(),
                caliper.Radius,
                detectedCenter,
                detectedRadius,
                [..invalidSamplePoints],
                filteredPoints,
                [..rejectedPoints],
                [..filteredSamples.Select(sample => sample.Score)],
                averageScore,
                residualRms,
                residualMax,
                filteredPoints.Length,
                confidence);

            return true;
        }

        private static double NormalizeLineAngleDegrees(Vector direction)
        {
            if (direction.LengthSquared < 1e-6)
            {
                return 0;
            }

            double angle = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
            if (angle < 0)
            {
                angle += 180;
            }

            return angle >= 180 ? angle - 180 : angle;
        }

        private static double ComputeConfidence(double edge1AverageScore, double edge2AverageScore, double edge1ResidualRms, double edge2ResidualRms, double parallelismErrorDegrees, int validCaliperCount, int totalCaliperCount)
        {
            double scoreComponent = Math.Clamp(((edge1AverageScore + edge2AverageScore) / 2) / 64.0, 0, 1);
            double residualComponent = 1.0 / (1.0 + Math.Max(edge1ResidualRms, edge2ResidualRms));
            double parallelComponent = 1.0 / (1.0 + parallelismErrorDegrees / 5.0);
            double validRatio = totalCaliperCount <= 0 ? 0 : (double)validCaliperCount / totalCaliperCount;
            return Math.Clamp(scoreComponent * residualComponent * parallelComponent * validRatio, 0, 1);
        }

        private static double ComputeCircularConfidence(double averageScore, double residualRms, int validCaliperCount, int totalCaliperCount)
        {
            double scoreComponent = Math.Clamp(averageScore / 64.0, 0, 1);
            double residualComponent = 1.0 / (1.0 + residualRms);
            double validRatio = totalCaliperCount <= 0 ? 0 : (double)validCaliperCount / totalCaliperCount;
            return Math.Clamp(scoreComponent * residualComponent * validRatio, 0, 1);
        }

    }
}
