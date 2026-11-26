namespace ImageViewer.Models
{
    internal static class CaliperMeasureRoiExtensions
    {
        public static void ClearDetection(this CaliperMeasureRoi roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            DualEdgeCaliperDetectionDisplayState.Empty.ApplyTo(roi);
        }

        public static void SetDetectionVisualization(this CaliperMeasureRoi roi, DualEdgeCaliperDetectionDisplayState displayState)
        {
            ArgumentNullException.ThrowIfNull(roi);
            ArgumentNullException.ThrowIfNull(displayState);
            displayState.ApplyTo(roi);
        }

        public static void SetDetectionVisualization(this CaliperMeasureRoi roi, IDualEdgeCaliperDetectionResult detectionResult)
        {
            ArgumentNullException.ThrowIfNull(roi);
            ArgumentNullException.ThrowIfNull(detectionResult);
            roi.SetDetectionVisualization(DualEdgeCaliperDetectionDisplayState.CreateDetected(detectionResult));
        }

        public static void CopyStateFrom(this CaliperMeasureRoi target, CaliperMeasureRoi source)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(source);

            DualEdgeCaliperConfigurationState.Capture(source).ApplyTo(target);
            DualEdgeCaliperDetectionDisplayState.Capture(source).ApplyTo(target);
            target.ApplyWidthMeasurement(source.WidthMeasurement);
        }
    }
}
