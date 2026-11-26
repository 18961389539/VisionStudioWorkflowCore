using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    /// <summary>
    /// 单边缘直线卡尺检测结果。
    /// Chinese: <see cref="FittedLine"/> 是拟合样本的有限支撑段；<see cref="DetectedP1"/> 和
    /// <see cref="DetectedP2"/> 是将原始 ROI 两端投影到拟合无限直线后的显示/测量段。
    /// English: FittedLine is the finite support of the fitted samples. DetectedP1/DetectedP2
    /// are the display and measurement endpoints obtained by projecting the original ROI ends
    /// onto the fitted infinite line.
    /// </summary>
    public readonly record struct LineCaliperDetectionResult(
        Point ReferenceP1,
        Point ReferenceP2,
        Point DetectedP1,
        Point DetectedP2,
        Point[] InvalidSamplePoints,
        Point[] EdgePoints,
        Point[] RejectedEdgePoints,
        double[] EdgeScores,
        DetectedLineSegment FittedLine,
        double AverageScore,
        double ResidualRms,
        double ResidualMax,
        int ValidCaliperCount,
        double AngleDegrees,
        double Confidence)
    {
        /// <summary>把有限拟合段提升为统一的无限直线定义。</summary>
        public LineFitGeometry FittedGeometry => LineFitGeometry.FromSegment(FittedLine);

        /// <summary>用于显示和测量的结果段，不是拟合点的支撑段。</summary>
        public DetectedLineSegment MeasuredSegment => new(
            new PointD(DetectedP1.X, DetectedP1.Y),
            new PointD(DetectedP2.X, DetectedP2.Y));
    }
}
