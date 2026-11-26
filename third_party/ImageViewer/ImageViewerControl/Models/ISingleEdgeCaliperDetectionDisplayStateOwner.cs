
namespace ImageViewer.Models
{
    public interface ISingleEdgeCaliperDetectionDisplayStateOwner
    {
        bool HasDetection { get; set; }
        LineSegmentOverlay[] RegionSegments { get; set; }
        LineSegmentOverlay[] CaliperBars { get; set; }
        LineSegmentOverlay[] InvalidCaliperMarkers { get; set; }
        LineSegmentOverlay[] EdgeMarkers { get; set; }
        LineSegmentOverlay[] RejectedEdgeMarkers { get; set; }
        CaliperScoreOverlay[] ScoreOverlays { get; set; }
        PointD[] EdgePoints { get; set; }
        double AverageScore { get; set; }
        double ResidualRms { get; set; }
        double ResidualMax { get; set; }
        int ValidCaliperCount { get; set; }
        double Confidence { get; set; }
    }
}