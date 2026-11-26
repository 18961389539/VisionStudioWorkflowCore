
namespace ImageViewer.Models
{
    internal static class SingleEdgeCaliperRoiExtensions
    {
        public static void ClearDetection(this ISingleEdgeCaliperDetectionDisplayStateOwner roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            SingleEdgeCaliperDetectionDisplayState.Empty.ApplyTo(roi);
        }

        public static void SetDetectionVisualization(this ISingleEdgeCaliperDetectionDisplayStateOwner roi, SingleEdgeCaliperDetectionDisplayState displayState)
        {
            ArgumentNullException.ThrowIfNull(roi);
            ArgumentNullException.ThrowIfNull(displayState);
            displayState.ApplyTo(roi);
        }

        public static void SetDetectionVisualization(this ISingleEdgeCaliperDetectionDisplayStateOwner roi, ISingleEdgeCaliperDetectionResult detectionResult)
        {
            ArgumentNullException.ThrowIfNull(roi);
            ArgumentNullException.ThrowIfNull(detectionResult);
            roi.SetDetectionVisualization(SingleEdgeCaliperDetectionDisplayState.CreateDetected(detectionResult));
        }

        public static void SetDetectionVisualization(
            this ISingleEdgeCaliperDetectionDisplayStateOwner roi,
            LineSegmentOverlay[] regionSegments,
            LineSegmentOverlay[] caliperBars,
            LineSegmentOverlay[] invalidCaliperMarkers,
            LineSegmentOverlay[] edgeMarkers,
            LineSegmentOverlay[] rejectedEdgeMarkers,
            CaliperScoreOverlay[] scoreOverlays,
            PointD[] edgePoints)
        {
            roi.SetDetectionVisualization(
                SingleEdgeCaliperDetectionDisplayState.CreateDetected(
                    regionSegments,
                    caliperBars,
                    invalidCaliperMarkers,
                    edgeMarkers,
                    rejectedEdgeMarkers,
                    scoreOverlays,
                    edgePoints));
        }

        public static void CopyStateFrom<TCaliper>(this TCaliper target, TCaliper source)
            where TCaliper : ISingleEdgeCaliperRoi, ISingleEdgeCaliperDetectionDisplayStateOwner
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(source);

            SingleEdgeCaliperConfigurationState.Capture(source).ApplyTo(target);
            SingleEdgeCaliperDetectionDisplayState.Capture(source).ApplyTo(target);
        }

        public static void CopyDetectionStateFrom(this ISingleEdgeCaliperDetectionDisplayStateOwner target, ISingleEdgeCaliperDetectionDisplayStateOwner source)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(source);
            SingleEdgeCaliperDetectionDisplayState.Capture(source).ApplyTo(target);
        }
    }
}
