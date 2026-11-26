#pragma warning disable CS1591
using ImageViewer.Models;

namespace ImageViewer.Core.Analysis;

/// <summary>Framework-neutral result of circular or arc caliper edge detection.</summary>
public readonly record struct CircularCaliperDetectionResult(
    PointD ReferenceCenter,
    double ReferenceRadius,
    PointD DetectedCenter,
    double DetectedRadius,
    PointD[] InvalidSamplePoints,
    PointD[] EdgePoints,
    PointD[] RejectedEdgePoints,
    double[] EdgeScores,
    double AverageScore,
    double ResidualRms,
    double ResidualMax,
    int ValidCaliperCount,
    double Confidence);

/// <summary>Framework-neutral result of single-edge line caliper detection.</summary>
public readonly record struct LineCaliperDetectionResult(
    PointD ReferenceP1,
    PointD ReferenceP2,
    PointD DetectedP1,
    PointD DetectedP2,
    PointD[] InvalidSamplePoints,
    PointD[] EdgePoints,
    PointD[] RejectedEdgePoints,
    double[] EdgeScores,
    DetectedLineSegment FittedLine,
    double AverageScore,
    double ResidualRms,
    double ResidualMax,
    int ValidCaliperCount,
    double AngleDegrees,
    double Confidence)
{
    public LineFitGeometry FittedGeometry => LineFitGeometry.FromSegment(FittedLine);

    public DetectedLineSegment MeasuredSegment => new(DetectedP1, DetectedP2);
}

/// <summary>Framework-neutral result of dual-edge line measurement detection.</summary>
public readonly record struct LineMeasureGradientDetectionResult(
    PointD DetectedP1,
    PointD DetectedP2,
    PointD[] InvalidCaliperCenters,
    PointD[] Edge1Points,
    PointD[] Edge2Points,
    PointD[] RejectedEdge1Points,
    PointD[] RejectedEdge2Points,
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
    public LineFitGeometry FittedEdge1Geometry => LineFitGeometry.FromSegment(FittedEdge1);

    public LineFitGeometry FittedEdge2Geometry => LineFitGeometry.FromSegment(FittedEdge2);

    public DetectedLineSegment MeasuredSegment => new(DetectedP1, DetectedP2);
}
#pragma warning restore CS1591
