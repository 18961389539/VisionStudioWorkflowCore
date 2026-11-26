using System;
using System.Windows;
using CoreCircularResult = ImageViewer.Core.Analysis.CircularCaliperDetectionResult;
using CoreLineResult = ImageViewer.Core.Analysis.LineCaliperDetectionResult;
using CoreDualLineResult = ImageViewer.Core.Analysis.LineMeasureGradientDetectionResult;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Utils;

namespace ImageViewer.Rendering
{
    internal static class CaliperDetectionDisplayStateFactory
    {
        public static DualEdgeCaliperDetectionDisplayState Create(CaliperMeasureRoi roi, CoreDualLineResult detectionResult)
        {
            ArgumentNullException.ThrowIfNull(roi);

            Vector measurementDirection = roi.GetCaliperMeasurementDirection().ToWpfVector();
            Vector markerDirection = new(-measurementDirection.Y, measurementDirection.X);
            double markerHalfLength = Math.Max(roi.CaliperSamplingHalfWidth + 1, 3);
            LineFitGeometry edge1Geometry = detectionResult.FittedEdge1Geometry;
            LineFitGeometry edge2Geometry = detectionResult.FittedEdge2Geometry;

            return new DualEdgeCaliperDetectionDisplayState
            {
                HasDetection = true,
                DetectedP1 = detectionResult.DetectedP1,
                DetectedP2 = detectionResult.DetectedP2,
                Edge1Start = edge1Geometry.SegmentStart,
                Edge1End = edge1Geometry.SegmentEnd,
                Edge2Start = edge2Geometry.SegmentStart,
                Edge2End = edge2Geometry.SegmentEnd,
                RegionSegments = CaliperOverlayGeometryHelper.BuildDualEdgeCaliperRegionSegments(roi),
                CaliperBars = CaliperOverlayGeometryHelper.BuildDualEdgeCaliperBars(roi),
                InvalidCaliperMarkers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.InvalidCaliperCenters.ToWpfPointArray(), markerDirection, markerHalfLength),
                Edge1Markers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.Edge1Points.ToWpfPointArray(), markerDirection, markerHalfLength),
                Edge2Markers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.Edge2Points.ToWpfPointArray(), markerDirection, markerHalfLength),
                RejectedEdge1Markers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.RejectedEdge1Points.ToWpfPointArray(), markerDirection, markerHalfLength),
                RejectedEdge2Markers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.RejectedEdge2Points.ToWpfPointArray(), markerDirection, markerHalfLength),
                ScoreOverlays = CaliperOverlayGeometryHelper.BuildDualEdgeScoreOverlays(
                    detectionResult.InvalidCaliperCenters.ToWpfPointArray(),
                    detectionResult.Edge1Points.ToWpfPointArray(),
                    detectionResult.Edge2Points.ToWpfPointArray(),
                    detectionResult.Edge1Scores,
                    detectionResult.Edge2Scores,
                    detectionResult.RejectedEdge1Points.ToWpfPointArray(),
                    detectionResult.RejectedEdge2Points.ToWpfPointArray(),
                    measurementDirection),
                Edge1Points = [.. detectionResult.Edge1Points],
                Edge2Points = [.. detectionResult.Edge2Points],
                Edge1AverageScore = detectionResult.Edge1AverageScore,
                Edge2AverageScore = detectionResult.Edge2AverageScore,
                Edge1ResidualRms = detectionResult.Edge1ResidualRms,
                Edge2ResidualRms = detectionResult.Edge2ResidualRms,
                Edge1ResidualMax = detectionResult.Edge1ResidualMax,
                Edge2ResidualMax = detectionResult.Edge2ResidualMax,
                ValidCaliperCount = detectionResult.ValidCaliperCount,
                Edge1AngleDegrees = detectionResult.Edge1AngleDegrees,
                Edge2AngleDegrees = detectionResult.Edge2AngleDegrees,
                ParallelismErrorDegrees = detectionResult.ParallelismErrorDegrees,
                Confidence = detectionResult.Confidence
            };
        }

        public static SingleEdgeCaliperDetectionDisplayState Create(LineCaliperMeasureRoi roi, CoreLineResult detectionResult)
        {
            ArgumentNullException.ThrowIfNull(roi);

            Vector lineDirection = detectionResult.ReferenceP2.ToWpfPoint() - detectionResult.ReferenceP1.ToWpfPoint();
            if (lineDirection.LengthSquared < 1e-6)
            {
                lineDirection = new Vector(1, 0);
            }

            lineDirection.Normalize();
            Vector measurementDirection = new(-lineDirection.Y, lineDirection.X);
            double markerHalfLength = Math.Max(roi.CaliperSamplingHalfWidth + 1, 3);

            return new SingleEdgeCaliperDetectionDisplayState
            {
                HasDetection = true,
                DetectedP1 = detectionResult.DetectedP1,
                DetectedP2 = detectionResult.DetectedP2,
                RegionSegments = CaliperOverlayGeometryHelper.BuildLineCaliperRegionSegments(detectionResult.ReferenceP1.ToWpfPoint(), detectionResult.ReferenceP2.ToWpfPoint(), roi.CaliperSearchRange),
                CaliperBars = CaliperOverlayGeometryHelper.BuildLineCaliperBars(detectionResult.ReferenceP1.ToWpfPoint(), detectionResult.ReferenceP2.ToWpfPoint(), roi.CaliperSearchRange, roi.CaliperCount),
                InvalidCaliperMarkers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.InvalidSamplePoints.ToWpfPointArray(), lineDirection, markerHalfLength),
                EdgeMarkers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.EdgePoints.ToWpfPointArray(), lineDirection, markerHalfLength),
                RejectedEdgeMarkers = CaliperOverlayGeometryHelper.BuildLinearMarkers(detectionResult.RejectedEdgePoints.ToWpfPointArray(), lineDirection, markerHalfLength),
                ScoreOverlays = CaliperOverlayGeometryHelper.BuildLinearScoreOverlays(
                    detectionResult.InvalidSamplePoints.ToWpfPointArray(),
                    detectionResult.EdgePoints.ToWpfPointArray(),
                    detectionResult.EdgeScores,
                    detectionResult.RejectedEdgePoints.ToWpfPointArray(),
                    measurementDirection),
                EdgePoints = [.. detectionResult.EdgePoints],
                AverageScore = detectionResult.AverageScore,
                ResidualRms = detectionResult.ResidualRms,
                ResidualMax = detectionResult.ResidualMax,
                ValidCaliperCount = detectionResult.ValidCaliperCount,
                Confidence = detectionResult.Confidence
            };
        }

        public static SingleEdgeCaliperDetectionDisplayState Create(CircularCaliperMeasureRoi roi, CoreCircularResult detectionResult)
        {
            ArgumentNullException.ThrowIfNull(roi);

            double markerHalfLength = Math.Max(roi.CaliperSamplingHalfWidth + 1, 3);
            LineSegmentOverlay[] regionSegments = roi is ArcCaliperMeasureRoi arcCaliper
                ? CaliperOverlayGeometryHelper.BuildArcCaliperRegionSegments(detectionResult.ReferenceCenter.ToWpfPoint(), detectionResult.ReferenceRadius, arcCaliper.CaliperSearchRange, arcCaliper.CaliperCount, arcCaliper.StartAngle, arcCaliper.SweepAngle)
                : CaliperOverlayGeometryHelper.BuildCircularCaliperRegionSegments(detectionResult.ReferenceCenter.ToWpfPoint(), detectionResult.ReferenceRadius, roi.CaliperSearchRange, roi.CaliperCount);
            LineSegmentOverlay[] caliperBars = roi is ArcCaliperMeasureRoi arcRoi
                ? CaliperOverlayGeometryHelper.BuildArcCaliperBars(detectionResult.ReferenceCenter.ToWpfPoint(), detectionResult.ReferenceRadius, arcRoi.CaliperSearchRange, arcRoi.CaliperCount, arcRoi.StartAngle, arcRoi.SweepAngle)
                : CaliperOverlayGeometryHelper.BuildCircularCaliperBars(detectionResult.ReferenceCenter.ToWpfPoint(), detectionResult.ReferenceRadius, roi.CaliperSearchRange, roi.CaliperCount);

            return new SingleEdgeCaliperDetectionDisplayState
            {
                HasDetection = true,
                DetectedCenter = detectionResult.DetectedCenter,
                DetectedRadius = detectionResult.DetectedRadius,
                RegionSegments = regionSegments,
                CaliperBars = caliperBars,
                InvalidCaliperMarkers = CaliperOverlayGeometryHelper.BuildCircularMarkers(detectionResult.InvalidSamplePoints.ToWpfPointArray(), detectionResult.ReferenceCenter.ToWpfPoint(), markerHalfLength),
                EdgeMarkers = CaliperOverlayGeometryHelper.BuildCircularMarkers(detectionResult.EdgePoints.ToWpfPointArray(), detectionResult.ReferenceCenter.ToWpfPoint(), markerHalfLength),
                RejectedEdgeMarkers = CaliperOverlayGeometryHelper.BuildCircularMarkers(detectionResult.RejectedEdgePoints.ToWpfPointArray(), detectionResult.ReferenceCenter.ToWpfPoint(), markerHalfLength),
                ScoreOverlays = CaliperOverlayGeometryHelper.BuildCircularScoreOverlays(
                    detectionResult.InvalidSamplePoints.ToWpfPointArray(),
                    detectionResult.EdgePoints.ToWpfPointArray(),
                    detectionResult.EdgeScores,
                    detectionResult.RejectedEdgePoints.ToWpfPointArray(),
                    detectionResult.ReferenceCenter.ToWpfPoint()),
                EdgePoints = [.. detectionResult.EdgePoints],
                AverageScore = detectionResult.AverageScore,
                ResidualRms = detectionResult.ResidualRms,
                ResidualMax = detectionResult.ResidualMax,
                ValidCaliperCount = detectionResult.ValidCaliperCount,
                Confidence = detectionResult.Confidence
            };
        }
    }
}
