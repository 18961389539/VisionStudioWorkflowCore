namespace ImageViewer.Models
{
    internal static class SingleEdgeCaliperDetectionStateHelper
    {
        public static bool GetHasDetection(LineCaliperMeasureRoi roi) => roi.HasDetectedLine;

        public static void SetHasDetection(LineCaliperMeasureRoi roi, bool value) => roi.HasDetectedLine = value;

        public static bool GetHasDetection(CircularCaliperMeasureRoi roi) => roi.HasDetectedEdges;

        public static void SetHasDetection(CircularCaliperMeasureRoi roi, bool value) => roi.HasDetectedEdges = value;
    }
}
