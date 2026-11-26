
namespace ImageViewer.Models
{
    internal interface IDualEdgeCaliperDetectionResult
    {
        LineSegmentOverlay[] RegionSegments { get; }
        LineSegmentOverlay[] CaliperBars { get; }
        LineSegmentOverlay[] InvalidCaliperMarkers { get; }
        LineSegmentOverlay[] Edge1Markers { get; }
        LineSegmentOverlay[] Edge2Markers { get; }
        LineSegmentOverlay[] RejectedEdge1Markers { get; }
        LineSegmentOverlay[] RejectedEdge2Markers { get; }
        CaliperScoreOverlay[] ScoreOverlays { get; }
        PointD[] Edge1Points { get; }
        PointD[] Edge2Points { get; }
        LineSegmentOverlay FittedEdge1 { get; }
        LineSegmentOverlay FittedEdge2 { get; }
        double Edge1AverageScore { get; }
        double Edge2AverageScore { get; }
        double Edge1ResidualRms { get; }
        double Edge2ResidualRms { get; }
        double Edge1ResidualMax { get; }
        double Edge2ResidualMax { get; }
        int ValidCaliperCount { get; }
        double Edge1AngleDegrees { get; }
        double Edge2AngleDegrees { get; }
        double ParallelismErrorDegrees { get; }
        double Confidence { get; }
    }
}
