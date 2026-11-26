namespace ImageViewer.Models
{
    internal sealed record DualEdgeCaliperConfigurationState(
        PointD CaliperCenter,
        double CaliperAngleDegrees,
        bool HasExplicitCaliperRegion,
        int CaliperCount,
        int CaliperSearchRange,
        int CaliperSamplingHalfWidth,
        double CaliperEdgeSigma,
        double CaliperRegionLength,
        double CaliperMinimumGradient,
        int MinimumValidCalipers,
        double CaliperOutlierThreshold,
        CaliperEdgePolarity CaliperEdgePolarity,
        LineFitMode CaliperLineFitMode,
        int CaliperFitClippingEndPoints,
        double MinimumEdgeGap,
        double NominalEdgeGap,
        double NominalEdgeGapTolerance)
    {
        public static DualEdgeCaliperConfigurationState Capture(CaliperMeasureRoi roi)
        {
            return new DualEdgeCaliperConfigurationState(
                roi.CaliperCenter,
                roi.CaliperAngleDegrees,
                roi.HasExplicitCaliperRegion,
                roi.CaliperCount,
                roi.CaliperSearchRange,
                roi.CaliperSamplingHalfWidth,
                roi.CaliperEdgeSigma,
                roi.CaliperRegionLength,
                roi.CaliperMinimumGradient,
                roi.MinimumValidCalipers,
                roi.CaliperOutlierThreshold,
                roi.CaliperEdgePolarity,
                roi.CaliperLineFitMode,
                roi.CaliperFitClippingEndPoints,
                roi.MinimumEdgeGap,
                roi.NominalEdgeGap,
                roi.NominalEdgeGapTolerance);
        }

        public void ApplyTo(CaliperMeasureRoi roi)
        {
            roi.CaliperCenter = CaliperCenter;
            roi.CaliperAngleDegrees = CaliperAngleDegrees;
            roi.HasExplicitCaliperRegion = HasExplicitCaliperRegion;
            roi.CaliperCount = CaliperCount;
            roi.CaliperSearchRange = CaliperSearchRange;
            roi.CaliperSamplingHalfWidth = CaliperSamplingHalfWidth;
            roi.CaliperEdgeSigma = CaliperEdgeSigma;
            roi.CaliperRegionLength = CaliperRegionLength;
            roi.CaliperMinimumGradient = CaliperMinimumGradient;
            roi.MinimumValidCalipers = MinimumValidCalipers;
            roi.CaliperOutlierThreshold = CaliperOutlierThreshold;
            roi.CaliperEdgePolarity = CaliperEdgePolarity;
            roi.CaliperLineFitMode = CaliperLineFitMode;
            roi.CaliperFitClippingEndPoints = CaliperFitClippingEndPoints;
            roi.MinimumEdgeGap = MinimumEdgeGap;
            roi.NominalEdgeGap = NominalEdgeGap;
            roi.NominalEdgeGapTolerance = NominalEdgeGapTolerance;
        }
    }
}
