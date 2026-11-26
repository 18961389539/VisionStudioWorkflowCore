namespace ImageViewer.Models
{
    internal sealed record SingleEdgeCaliperConfigurationState(
        int CaliperCount,
        int CaliperSearchRange,
        int CaliperSamplingHalfWidth,
        double CaliperEdgeSigma,
        double CaliperMinimumGradient,
        int MinimumValidCalipers,
        double CaliperOutlierThreshold,
        CaliperEdgePolarity CaliperEdgePolarity,
        LineFitMode CaliperLineFitMode,
        int CaliperFitClippingEndPoints)
    {
        public static SingleEdgeCaliperConfigurationState Capture(ISingleEdgeCaliperRoi roi)
        {
            return new SingleEdgeCaliperConfigurationState(
                roi.CaliperCount,
                roi.CaliperSearchRange,
                roi.CaliperSamplingHalfWidth,
                roi.CaliperEdgeSigma,
                roi.CaliperMinimumGradient,
                roi.MinimumValidCalipers,
                roi.CaliperOutlierThreshold,
                roi.CaliperEdgePolarity,
                roi.CaliperLineFitMode,
                roi.CaliperFitClippingEndPoints);
        }

        public void ApplyTo(ISingleEdgeCaliperRoi roi)
        {
            roi.CaliperCount = CaliperCount;
            roi.CaliperSearchRange = CaliperSearchRange;
            roi.CaliperSamplingHalfWidth = CaliperSamplingHalfWidth;
            roi.CaliperEdgeSigma = CaliperEdgeSigma;
            roi.CaliperMinimumGradient = CaliperMinimumGradient;
            roi.MinimumValidCalipers = MinimumValidCalipers;
            roi.CaliperOutlierThreshold = CaliperOutlierThreshold;
            roi.CaliperEdgePolarity = CaliperEdgePolarity;
            roi.CaliperLineFitMode = CaliperLineFitMode;
            roi.CaliperFitClippingEndPoints = CaliperFitClippingEndPoints;
        }
    }
}
