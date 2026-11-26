namespace ImageViewer.Models
{
    internal interface ISingleEdgeCaliperDetectionResult
    {
        LineSegmentOverlay[] RegionSegments { get; }
        LineSegmentOverlay[] CaliperBars { get; }
        LineSegmentOverlay[] InvalidCaliperMarkers { get; }
        LineSegmentOverlay[] EdgeMarkers { get; }
        LineSegmentOverlay[] RejectedEdgeMarkers { get; }
        CaliperScoreOverlay[] ScoreOverlays { get; }
        PointD[] EdgePoints { get; }
        double AverageScore { get; }
        double ResidualRms { get; }
        double ResidualMax { get; }
        int ValidCaliperCount { get; }
        double Confidence { get; }
    }
}
