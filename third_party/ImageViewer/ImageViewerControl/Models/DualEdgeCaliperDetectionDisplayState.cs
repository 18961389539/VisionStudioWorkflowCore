
namespace ImageViewer.Models
{
    internal sealed class DualEdgeCaliperDetectionDisplayState
    {
        public bool HasDetection { get; set; }
        // P1/P2 remain the hand-drawn measurement reference.  These are the
        // detected intersections used for the final width result.
        public PointD DetectedP1 { get; set; }
        public PointD DetectedP2 { get; set; }
        public PointD Edge1Start { get; set; }
        public PointD Edge1End { get; set; }
        public PointD Edge2Start { get; set; }
        public PointD Edge2End { get; set; }
        public LineSegmentOverlay[] RegionSegments { get; set; } = [];
        public LineSegmentOverlay[] CaliperBars { get; set; } = [];
        public LineSegmentOverlay[] InvalidCaliperMarkers { get; set; } = [];
        public LineSegmentOverlay[] Edge1Markers { get; set; } = [];
        public LineSegmentOverlay[] Edge2Markers { get; set; } = [];
        public LineSegmentOverlay[] RejectedEdge1Markers { get; set; } = [];
        public LineSegmentOverlay[] RejectedEdge2Markers { get; set; } = [];
        public CaliperScoreOverlay[] ScoreOverlays { get; set; } = [];
        public PointD[] Edge1Points { get; set; } = [];
        public PointD[] Edge2Points { get; set; } = [];
        public double Edge1AverageScore { get; set; }
        public double Edge2AverageScore { get; set; }
        public double Edge1ResidualRms { get; set; }
        public double Edge2ResidualRms { get; set; }
        public double Edge1ResidualMax { get; set; }
        public double Edge2ResidualMax { get; set; }
        public int ValidCaliperCount { get; set; }
        public double Edge1AngleDegrees { get; set; }
        public double Edge2AngleDegrees { get; set; }
        public double ParallelismErrorDegrees { get; set; }
        public double Confidence { get; set; }

        public static DualEdgeCaliperDetectionDisplayState Empty => new();

        public static DualEdgeCaliperDetectionDisplayState Capture(CaliperMeasureRoi roi)
        {
            return new DualEdgeCaliperDetectionDisplayState
            {
                HasDetection = roi.HasDetectedEdges,
                DetectedP1 = roi.DetectedP1,
                DetectedP2 = roi.DetectedP2,
                Edge1Start = roi.Edge1Start,
                Edge1End = roi.Edge1End,
                Edge2Start = roi.Edge2Start,
                Edge2End = roi.Edge2End,
                RegionSegments = [.. roi.RegionSegments],
                CaliperBars = [.. roi.CaliperBars],
                InvalidCaliperMarkers = [.. roi.InvalidCaliperMarkers],
                Edge1Markers = [.. roi.Edge1Markers],
                Edge2Markers = [.. roi.Edge2Markers],
                RejectedEdge1Markers = [.. roi.RejectedEdge1Markers],
                RejectedEdge2Markers = [.. roi.RejectedEdge2Markers],
                ScoreOverlays = [.. roi.ScoreOverlays],
                Edge1Points = [.. roi.Edge1Points],
                Edge2Points = [.. roi.Edge2Points],
                Edge1AverageScore = roi.Edge1AverageScore,
                Edge2AverageScore = roi.Edge2AverageScore,
                Edge1ResidualRms = roi.Edge1ResidualRms,
                Edge2ResidualRms = roi.Edge2ResidualRms,
                Edge1ResidualMax = roi.Edge1ResidualMax,
                Edge2ResidualMax = roi.Edge2ResidualMax,
                ValidCaliperCount = roi.ValidCaliperCount,
                Edge1AngleDegrees = roi.Edge1AngleDegrees,
                Edge2AngleDegrees = roi.Edge2AngleDegrees,
                ParallelismErrorDegrees = roi.ParallelismErrorDegrees,
                Confidence = roi.Confidence
            };
        }

        public static DualEdgeCaliperDetectionDisplayState CreateDetected(IDualEdgeCaliperDetectionResult detectionResult)
        {
            ArgumentNullException.ThrowIfNull(detectionResult);

            return new DualEdgeCaliperDetectionDisplayState
            {
                HasDetection = true,
                Edge1Start = detectionResult.FittedEdge1.Start,
                Edge1End = detectionResult.FittedEdge1.End,
                Edge2Start = detectionResult.FittedEdge2.Start,
                Edge2End = detectionResult.FittedEdge2.End,
                RegionSegments = [.. detectionResult.RegionSegments],
                CaliperBars = [.. detectionResult.CaliperBars],
                InvalidCaliperMarkers = [.. detectionResult.InvalidCaliperMarkers],
                Edge1Markers = [.. detectionResult.Edge1Markers],
                Edge2Markers = [.. detectionResult.Edge2Markers],
                RejectedEdge1Markers = [.. detectionResult.RejectedEdge1Markers],
                RejectedEdge2Markers = [.. detectionResult.RejectedEdge2Markers],
                ScoreOverlays = [.. detectionResult.ScoreOverlays],
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

        public void ApplyTo(CaliperMeasureRoi roi)
        {
            roi.HasDetectedEdges = HasDetection;
            roi.SetDetectedMeasurementLine(DetectedP1, DetectedP2);
            roi.Edge1Start = Edge1Start;
            roi.Edge1End = Edge1End;
            roi.Edge2Start = Edge2Start;
            roi.Edge2End = Edge2End;
            roi.RegionSegments = [.. RegionSegments];
            roi.CaliperBars = [.. CaliperBars];
            roi.InvalidCaliperMarkers = [.. InvalidCaliperMarkers];
            roi.Edge1Markers = [.. Edge1Markers];
            roi.Edge2Markers = [.. Edge2Markers];
            roi.RejectedEdge1Markers = [.. RejectedEdge1Markers];
            roi.RejectedEdge2Markers = [.. RejectedEdge2Markers];
            roi.ScoreOverlays = [.. ScoreOverlays];
            roi.Edge1Points = [.. Edge1Points];
            roi.Edge2Points = [.. Edge2Points];
            roi.Edge1AverageScore = Edge1AverageScore;
            roi.Edge2AverageScore = Edge2AverageScore;
            roi.Edge1ResidualRms = Edge1ResidualRms;
            roi.Edge2ResidualRms = Edge2ResidualRms;
            roi.Edge1ResidualMax = Edge1ResidualMax;
            roi.Edge2ResidualMax = Edge2ResidualMax;
            roi.ValidCaliperCount = ValidCaliperCount;
            roi.Edge1AngleDegrees = Edge1AngleDegrees;
            roi.Edge2AngleDegrees = Edge2AngleDegrees;
            roi.ParallelismErrorDegrees = ParallelismErrorDegrees;
            roi.Confidence = Confidence;
        }
    }
}
