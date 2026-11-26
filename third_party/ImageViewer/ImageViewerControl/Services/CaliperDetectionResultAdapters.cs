using System.Linq;
using System.Windows;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using ImageViewer.Utils;
using CoreCircularResult = ImageViewer.Core.Analysis.CircularCaliperDetectionResult;
using CoreLineResult = ImageViewer.Core.Analysis.LineCaliperDetectionResult;
using CoreDualLineResult = ImageViewer.Core.Analysis.LineMeasureGradientDetectionResult;

namespace ImageViewer.Services
{
    /// <summary>
    /// Converts the legacy WPF-facing result records to and from the Core contracts.
    /// New analysis and rendering code should use the Core result records directly.
    /// </summary>
    internal static class CaliperDetectionResultAdapters
    {
        public static CoreCircularResult ToCore(this CircularCaliperDetectionResult result) => new(
            result.ReferenceCenter.ToPointD(),
            result.ReferenceRadius,
            result.DetectedCenter.ToPointD(),
            result.DetectedRadius,
            result.InvalidSamplePoints.ToPointDs().ToArray(),
            result.EdgePoints.ToPointDs().ToArray(),
            result.RejectedEdgePoints.ToPointDs().ToArray(),
            result.EdgeScores,
            result.AverageScore,
            result.ResidualRms,
            result.ResidualMax,
            result.ValidCaliperCount,
            result.Confidence);

        public static CoreLineResult ToCore(this LineCaliperDetectionResult result) => new(
            result.ReferenceP1.ToPointD(),
            result.ReferenceP2.ToPointD(),
            result.DetectedP1.ToPointD(),
            result.DetectedP2.ToPointD(),
            result.InvalidSamplePoints.ToPointDs().ToArray(),
            result.EdgePoints.ToPointDs().ToArray(),
            result.RejectedEdgePoints.ToPointDs().ToArray(),
            result.EdgeScores,
            result.FittedLine,
            result.AverageScore,
            result.ResidualRms,
            result.ResidualMax,
            result.ValidCaliperCount,
            result.AngleDegrees,
            result.Confidence);

        public static CoreDualLineResult ToCore(this LineMeasureGradientDetectionResult result) => new(
            result.DetectedP1.ToPointD(),
            result.DetectedP2.ToPointD(),
            result.InvalidCaliperCenters.ToPointDs().ToArray(),
            result.Edge1Points.ToPointDs().ToArray(),
            result.Edge2Points.ToPointDs().ToArray(),
            result.RejectedEdge1Points.ToPointDs().ToArray(),
            result.RejectedEdge2Points.ToPointDs().ToArray(),
            result.Edge1Scores,
            result.Edge2Scores,
            result.FittedEdge1,
            result.FittedEdge2,
            result.Edge1AverageScore,
            result.Edge2AverageScore,
            result.Edge1ResidualRms,
            result.Edge2ResidualRms,
            result.Edge1ResidualMax,
            result.Edge2ResidualMax,
            result.ValidCaliperCount,
            result.Edge1AngleDegrees,
            result.Edge2AngleDegrees,
            result.ParallelismErrorDegrees,
            result.Confidence,
            result.WidthSamples);

        public static CircularCaliperDetectionResult ToWpf(this CoreCircularResult result) => new(
            result.ReferenceCenter.ToWpfPoint(),
            result.ReferenceRadius,
            result.DetectedCenter.ToWpfPoint(),
            result.DetectedRadius,
            result.InvalidSamplePoints.ToWpfPointArray(),
            result.EdgePoints.ToWpfPointArray(),
            result.RejectedEdgePoints.ToWpfPointArray(),
            result.EdgeScores,
            result.AverageScore,
            result.ResidualRms,
            result.ResidualMax,
            result.ValidCaliperCount,
            result.Confidence);

        public static LineCaliperDetectionResult ToWpf(this CoreLineResult result) => new(
            result.ReferenceP1.ToWpfPoint(),
            result.ReferenceP2.ToWpfPoint(),
            result.DetectedP1.ToWpfPoint(),
            result.DetectedP2.ToWpfPoint(),
            result.InvalidSamplePoints.ToWpfPointArray(),
            result.EdgePoints.ToWpfPointArray(),
            result.RejectedEdgePoints.ToWpfPointArray(),
            result.EdgeScores,
            result.FittedLine,
            result.AverageScore,
            result.ResidualRms,
            result.ResidualMax,
            result.ValidCaliperCount,
            result.AngleDegrees,
            result.Confidence);

        public static LineMeasureGradientDetectionResult ToWpf(this CoreDualLineResult result) => new(
            result.DetectedP1.ToWpfPoint(),
            result.DetectedP2.ToWpfPoint(),
            result.InvalidCaliperCenters.ToWpfPointArray(),
            result.Edge1Points.ToWpfPointArray(),
            result.Edge2Points.ToWpfPointArray(),
            result.RejectedEdge1Points.ToWpfPointArray(),
            result.RejectedEdge2Points.ToWpfPointArray(),
            result.Edge1Scores,
            result.Edge2Scores,
            result.FittedEdge1,
            result.FittedEdge2,
            result.Edge1AverageScore,
            result.Edge2AverageScore,
            result.Edge1ResidualRms,
            result.Edge2ResidualRms,
            result.Edge1ResidualMax,
            result.Edge2ResidualMax,
            result.ValidCaliperCount,
            result.Edge1AngleDegrees,
            result.Edge2AngleDegrees,
            result.ParallelismErrorDegrees,
            result.Confidence,
            result.WidthSamples);
    }
}
