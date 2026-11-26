using ImageViewer.Models;

namespace ImageViewer.Services
{
    internal sealed record RoiAnalysisBatchCsvRow(
        string SourcePath,
        string Status,
        string? Error,
        string RoiType,
        string? RoiLabel,
        string Quantity,
        string Unit,
        string PhysicalUnit,
        (string Metric1, string Metric2, string Metric3) Metrics,
        RoiStatistics? Statistics,
        RoiInspectionResult Inspection,
        (double? Nominal, double? Plus, double? Minus)? Tolerance,
        int SourceIndex = -1,
        string? MeasurementItemKey = null)
    {
        public string DisplayLabel => string.IsNullOrWhiteSpace(RoiLabel) ? RoiType : RoiLabel;
    }
}
