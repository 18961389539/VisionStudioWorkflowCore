using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Rendering
{
    internal readonly struct RoiMeasurementDisplay(RoiBase roi, double pixelSize, string unit, CameraCalibration? calibration)
    {
        private readonly DistortionAudit _audit = RoiCalibrationHelper.AuditRoi(roi, calibration);

        public string FormatPrimary() =>
            Format(RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration, _audit).MeasuredValue, unit);

        public string FormatLength(double pixels) => Format(
            CanMeasure ? pixels * RoiCalibrationHelper.GetLengthCorrection(roi, calibration) * pixelSize : null, unit);

        public string FormatArea(double squarePixels) => Format(
            CanMeasure ? squarePixels * RoiCalibrationHelper.GetAreaCorrection(roi, calibration) * pixelSize * pixelSize : null, unit + "²");

        private bool CanMeasure => RoiCalibrationHelper.CanUseMeasurements(calibration, _audit);

        private static string Format(double? value, string displayUnit)
            => value is double number && double.IsFinite(number)
                ? $"{number:F2} {displayUnit}"
                : UiText.Get("MeasurementUnavailable");
    }
}
