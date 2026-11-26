using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    public readonly record struct CircularCaliperDetectionResult(
        Point ReferenceCenter,
        double ReferenceRadius,
        Point DetectedCenter,
        double DetectedRadius,
        Point[] InvalidSamplePoints,
        Point[] EdgePoints,
        Point[] RejectedEdgePoints,
        double[] EdgeScores,
        double AverageScore,
        double ResidualRms,
        double ResidualMax,
        int ValidCaliperCount,
        double Confidence);
}
