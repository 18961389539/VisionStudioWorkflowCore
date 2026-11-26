using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    /// <summary>
    /// 双边缘直线卡尺检测结果。
    /// Chinese: 两条 FittedEdge* 是各自边缘样本的有限支撑段；DetectedP1/DetectedP2
    /// 是测量中心线与两条拟合无限直线的交点。
    /// English: FittedEdge1/FittedEdge2 are the finite supports of the two edge fits.
    /// DetectedP1/DetectedP2 are intersections of the measurement centerline with those
    /// fitted infinite lines.
    /// </summary>
    public readonly record struct LineMeasureGradientDetectionResult(
        Point DetectedP1,
        Point DetectedP2,
        Point[] InvalidCaliperCenters,
        Point[] Edge1Points,
        Point[] Edge2Points,
        Point[] RejectedEdge1Points,
        Point[] RejectedEdge2Points,
        double[] Edge1Scores,
        double[] Edge2Scores,
        DetectedLineSegment FittedEdge1,
        DetectedLineSegment FittedEdge2,
        double Edge1AverageScore,
        double Edge2AverageScore,
        double Edge1ResidualRms,
        double Edge2ResidualRms,
        double Edge1ResidualMax,
        double Edge2ResidualMax,
        int ValidCaliperCount,
        double Edge1AngleDegrees,
        double Edge2AngleDegrees,
        double ParallelismErrorDegrees,
        double Confidence,
        double[] WidthSamples)
    {
        /// <summary>第一条边缘的统一无限直线定义。</summary>
        public LineFitGeometry FittedEdge1Geometry => LineFitGeometry.FromSegment(FittedEdge1);

        /// <summary>第二条边缘的统一无限直线定义。</summary>
        public LineFitGeometry FittedEdge2Geometry => LineFitGeometry.FromSegment(FittedEdge2);

        /// <summary>两条拟合边缘在测量中心线上的结果段。</summary>
        public DetectedLineSegment MeasuredSegment => new(
            new PointD(DetectedP1.X, DetectedP1.Y),
            new PointD(DetectedP2.X, DetectedP2.Y));
    }
}
