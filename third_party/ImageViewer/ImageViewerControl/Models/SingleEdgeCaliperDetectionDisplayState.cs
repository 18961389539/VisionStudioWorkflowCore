
namespace ImageViewer.Models
{
    internal sealed class SingleEdgeCaliperDetectionDisplayState
    {
        public bool HasDetection { get; set; }

        // Line-caliper results are kept separate from the hand-drawn reference
        // segment.  The reference segment is the ROI's P1/P2; these points are
        // the projected result returned by the detector.
        public PointD DetectedP1 { get; set; }

        public PointD DetectedP2 { get; set; }

        // Circular-caliper results are kept separate from the hand-drawn
        // center/radius for the same reason as line-caliper results above.
        public PointD DetectedCenter { get; set; }

        public double DetectedRadius { get; set; }

        public LineSegmentOverlay[] RegionSegments { get; set; } = [];

        public LineSegmentOverlay[] CaliperBars { get; set; } = [];

        public LineSegmentOverlay[] InvalidCaliperMarkers { get; set; } = [];

        public LineSegmentOverlay[] EdgeMarkers { get; set; } = [];

        public LineSegmentOverlay[] RejectedEdgeMarkers { get; set; } = [];

        public CaliperScoreOverlay[] ScoreOverlays { get; set; } = [];

        public PointD[] EdgePoints { get; set; } = [];

        public double AverageScore { get; set; }

        public double ResidualRms { get; set; }

        public double ResidualMax { get; set; }

        public int ValidCaliperCount { get; set; }

        public double Confidence { get; set; }

        public static SingleEdgeCaliperDetectionDisplayState Empty => new();

        public static SingleEdgeCaliperDetectionDisplayState Capture(ISingleEdgeCaliperDetectionDisplayStateOwner roi)
        {
            return new SingleEdgeCaliperDetectionDisplayState
            {
                HasDetection = roi.HasDetection,
                DetectedP1 = roi is LineCaliperMeasureRoi ? ((LineCaliperMeasureRoi)roi).DetectedP1 : default,
                DetectedP2 = roi is LineCaliperMeasureRoi ? ((LineCaliperMeasureRoi)roi).DetectedP2 : default,
                DetectedCenter = roi is CircularCaliperMeasureRoi ? ((CircularCaliperMeasureRoi)roi).DetectedCenter : default,
                DetectedRadius = roi is CircularCaliperMeasureRoi ? ((CircularCaliperMeasureRoi)roi).DetectedRadius : 0,
                RegionSegments = [.. roi.RegionSegments],
                CaliperBars = [.. roi.CaliperBars],
                InvalidCaliperMarkers = [.. roi.InvalidCaliperMarkers],
                EdgeMarkers = [.. roi.EdgeMarkers],
                RejectedEdgeMarkers = [.. roi.RejectedEdgeMarkers],
                ScoreOverlays = [.. roi.ScoreOverlays],
                EdgePoints = [.. roi.EdgePoints],
                AverageScore = roi.AverageScore,
                ResidualRms = roi.ResidualRms,
                ResidualMax = roi.ResidualMax,
                ValidCaliperCount = roi.ValidCaliperCount,
                Confidence = roi.Confidence
            };
        }

        public static SingleEdgeCaliperDetectionDisplayState CreateDetected(
            LineSegmentOverlay[] regionSegments,
            LineSegmentOverlay[] caliperBars,
            LineSegmentOverlay[] invalidCaliperMarkers,
            LineSegmentOverlay[] edgeMarkers,
            LineSegmentOverlay[] rejectedEdgeMarkers,
            CaliperScoreOverlay[] scoreOverlays,
            PointD[] edgePoints)
        {
            return new SingleEdgeCaliperDetectionDisplayState
            {
                HasDetection = true,
                RegionSegments = [.. regionSegments],
                CaliperBars = [.. caliperBars],
                InvalidCaliperMarkers = [.. invalidCaliperMarkers],
                EdgeMarkers = [.. edgeMarkers],
                RejectedEdgeMarkers = [.. rejectedEdgeMarkers],
                ScoreOverlays = [.. scoreOverlays],
                EdgePoints = [.. edgePoints]
            };
        }

        public static SingleEdgeCaliperDetectionDisplayState CreateDetected(ISingleEdgeCaliperDetectionResult detectionResult)
        {
            ArgumentNullException.ThrowIfNull(detectionResult);

            return new SingleEdgeCaliperDetectionDisplayState
            {
                HasDetection = true,
                RegionSegments = [.. detectionResult.RegionSegments],
                CaliperBars = [.. detectionResult.CaliperBars],
                InvalidCaliperMarkers = [.. detectionResult.InvalidCaliperMarkers],
                EdgeMarkers = [.. detectionResult.EdgeMarkers],
                RejectedEdgeMarkers = [.. detectionResult.RejectedEdgeMarkers],
                ScoreOverlays = [.. detectionResult.ScoreOverlays],
                EdgePoints = [.. detectionResult.EdgePoints],
                AverageScore = detectionResult.AverageScore,
                ResidualRms = detectionResult.ResidualRms,
                ResidualMax = detectionResult.ResidualMax,
                ValidCaliperCount = detectionResult.ValidCaliperCount,
                Confidence = detectionResult.Confidence
            };
        }

        public SingleEdgeCaliperDetectionDisplayState Clone()
        {
            return new SingleEdgeCaliperDetectionDisplayState
            {
                HasDetection = HasDetection,
                DetectedP1 = DetectedP1,
                DetectedP2 = DetectedP2,
                DetectedCenter = DetectedCenter,
                DetectedRadius = DetectedRadius,
                RegionSegments = [.. RegionSegments],
                CaliperBars = [.. CaliperBars],
                InvalidCaliperMarkers = [.. InvalidCaliperMarkers],
                EdgeMarkers = [.. EdgeMarkers],
                RejectedEdgeMarkers = [.. RejectedEdgeMarkers],
                ScoreOverlays = [.. ScoreOverlays],
                EdgePoints = [.. EdgePoints],
                AverageScore = AverageScore,
                ResidualRms = ResidualRms,
                ResidualMax = ResidualMax,
                ValidCaliperCount = ValidCaliperCount,
                Confidence = Confidence
            };
        }

        public void ApplyTo(ISingleEdgeCaliperDetectionDisplayStateOwner roi)
        {
            roi.HasDetection = HasDetection;
            if (roi is LineCaliperMeasureRoi line)
            {
                line.SetDetectedLine(DetectedP1, DetectedP2);
            }
            else if (roi is CircularCaliperMeasureRoi circular)
            {
                circular.SetDetectedCircle(DetectedCenter, DetectedRadius);
            }
            roi.RegionSegments = [.. RegionSegments];
            roi.CaliperBars = [.. CaliperBars];
            roi.InvalidCaliperMarkers = [.. InvalidCaliperMarkers];
            roi.EdgeMarkers = [.. EdgeMarkers];
            roi.RejectedEdgeMarkers = [.. RejectedEdgeMarkers];
            roi.ScoreOverlays = [.. ScoreOverlays];
            roi.EdgePoints = [.. EdgePoints];
            roi.AverageScore = AverageScore;
            roi.ResidualRms = ResidualRms;
            roi.ResidualMax = ResidualMax;
            roi.ValidCaliperCount = ValidCaliperCount;
            roi.Confidence = Confidence;
        }

    }
}
