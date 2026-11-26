namespace ImageViewer.Models
{
    public interface ISingleEdgeCaliperRoi
    {
        int CaliperCount { get; set; }
        int CaliperSearchRange { get; set; }
        int CaliperSamplingHalfWidth { get; set; }
        double CaliperEdgeSigma { get; set; }
        double CaliperMinimumGradient { get; set; }
        int MinimumValidCalipers { get; set; }
        double CaliperOutlierThreshold { get; set; }
        CaliperEdgePolarity CaliperEdgePolarity { get; set; }
        LineFitMode CaliperLineFitMode { get; set; }
        int CaliperFitClippingEndPoints { get; set; }
    }
}
