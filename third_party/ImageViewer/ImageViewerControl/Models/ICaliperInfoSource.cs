namespace ImageViewer.Models
{
    public interface ICaliperInfoSource
    {
        string Label { get; }
        bool HasDetection { get; }
        int CaliperCount { get; }
        int CaliperSearchRange { get; }
        int CaliperSamplingHalfWidth { get; }
        CaliperEdgePolarity CaliperEdgePolarity { get; }
        int ValidCaliperCount { get; }
        double Confidence { get; }
        double AverageScore { get; }
        double ResidualRms { get; }
        double ResidualMax { get; }
    }
}
