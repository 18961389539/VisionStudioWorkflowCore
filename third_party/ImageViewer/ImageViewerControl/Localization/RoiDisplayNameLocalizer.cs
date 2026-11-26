using ImageViewer.Models;

namespace ImageViewer.Localization
{
    internal static class RoiDisplayNameLocalizer
    {
        public static string GetDisplayName(RoiBase roi)
        {
            var type = roi.GetType();

            return type switch
            {
                _ when type == typeof(RotatedRect) => UiText.Get("RoiDisplayRotatedRect"),
                _ when type == typeof(BlobAnalysisRoi) => UiText.Get("RoiDisplayBlobAnalysis"),
                _ when type == typeof(FittedEllipseRoi) => UiText.Get("RoiDisplayFittedEllipse"),
                _ when type == typeof(EllipseRoi) => UiText.Get("RoiDisplayEllipse"),
                _ when type == typeof(PolygonRoi) => UiText.Get("RoiDisplayPolygon"),
                _ when type == typeof(RingRoi) => UiText.Get("RoiDisplayRing"),
                _ when type == typeof(CircleRoi) => UiText.Get("RoiDisplayCircle"),
                _ when type == typeof(PolylineRoi) => UiText.Get("RoiDisplayPolyline"),
                _ when type == typeof(ArrowAnnotationRoi) => UiText.Get("RoiDisplayArrowAnnotation"),
                _ when type == typeof(PointAnnotationRoi) => UiText.Get("RoiDisplayPointAnnotation"),
                _ when type == typeof(PointCoordinateMeasureRoi) => UiText.Get("RoiDisplayPointCoordinate"),
                _ when type == typeof(TextAnnotationRoi) => UiText.Get("RoiDisplayTextAnnotation"),
                _ when type == typeof(LineCaliperMeasureRoi) => UiText.Get("RoiDisplayLineCaliperMeasure"),
                _ when type == typeof(CircularCaliperMeasureRoi) => UiText.Get("RoiDisplayCircularCaliperMeasure"),
                _ when type == typeof(ArcCaliperMeasureRoi) => UiText.Get("RoiDisplayArcCaliperMeasure"),
                _ when type == typeof(CaliperMeasureRoi) => UiText.Get("RoiDisplayCaliperMeasure"),
                _ when type == typeof(LineMeasureRoi) => UiText.Get("RoiDisplayLineMeasure"),
                _ when type == typeof(AngleMeasureRoi) => UiText.Get("RoiDisplayAngleMeasure"),
                _ when type == typeof(ArcMeasureRoi) => UiText.Get("RoiDisplayArcMeasure"),
                _ when type == typeof(ThreePointCircleMeasureRoi) => UiText.Get("RoiDisplayThreePointCircle"),
                _ when type == typeof(PointToLineDistanceRoi) => UiText.Get("RoiDisplayPointToLineDistance"),
                _ when type == typeof(PointToCircleDistanceRoi) => UiText.Get("RoiDisplayPointToCircleDistance"),
                _ when type == typeof(ParallelismMeasureRoi) => UiText.Get("RoiDisplayParallelismMeasure"),
                _ when type == typeof(PerpendicularityMeasureRoi) => UiText.Get("RoiDisplayPerpendicularityMeasure"),
                _ when type == typeof(ConcentricityMeasureRoi) => UiText.Get("RoiDisplayConcentricityMeasure"),
                _ when type == typeof(CenterDistanceMeasureRoi) => UiText.Get("RoiDisplayCenterDistance"),
                _ => roi.RoiTypeName
            };
        }
    }
}
