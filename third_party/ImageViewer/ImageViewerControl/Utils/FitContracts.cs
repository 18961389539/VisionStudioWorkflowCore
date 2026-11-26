using System.Collections.Generic;
using System.Windows;

namespace ImageViewer.Utils
{
    /// <summary>
    /// Robust loss used by geometric fitting.
    /// </summary>
    public enum RobustFitLoss
    {
        LeastSquares,
        Huber,
        Tukey
    }

    /// <summary>
    /// Options for the point based ellipse fitter.
    /// Values are deliberately serializable through the export metadata so a result can be reproduced later.
    /// </summary>
    public sealed record EllipseFitOptions
    {
        public RobustFitLoss Loss { get; init; } = RobustFitLoss.Tukey;
        public int MaxIterations { get; init; } = 48;
        public int MaxRansacSamples { get; init; } = 96;
        public int MinimumInliers { get; init; } = 5;
        public double InlierThreshold { get; init; }
        public double HuberK { get; init; } = 1.345;
        public double TukeyK { get; init; } = 4.685;
        public int RandomSeed { get; init; } = 173;

        public static EllipseFitOptions Default { get; } = new();

        internal EllipseFitOptions Normalize()
        {
            return this with
            {
                MaxIterations = System.Math.Clamp(MaxIterations, 8, 200),
                MaxRansacSamples = System.Math.Clamp(MaxRansacSamples, 0, 512),
                MinimumInliers = System.Math.Max(5, MinimumInliers),
                InlierThreshold = double.IsFinite(InlierThreshold) ? System.Math.Max(0, InlierThreshold) : 0,
                HuberK = double.IsFinite(HuberK) ? System.Math.Max(0.1, HuberK) : 1.345,
                TukeyK = double.IsFinite(TukeyK) ? System.Math.Max(0.5, TukeyK) : 4.685
            };
        }
    }

    /// <summary>
    /// Diagnostics returned by ellipse fitting.
    /// </summary>
    public sealed record EllipseFitResult(
        Point Center,
        double RadiusX,
        double RadiusY,
        double AngleDegrees,
        double ResidualRms,
        double ResidualMedian,
        double ResidualMax,
        double NoiseScale,
        int InlierCount,
        int OutlierCount,
        double AspectRatio,
        IReadOnlyList<bool> InlierMask,
        string Algorithm);

    public static class FittingAlgorithmMetadata
    {
        public const string Version = "ImageAnalysisService.v2";
        public const string Ellipse = "ellipse.geometric.robust.v2";
        public const string CaliperEdge = "jlvvision.measure.subpixel.v1";
        public const string LineFit = "jlvvision.fit_line_contour_xld.v1";
        public const string LineGeometry = "line.source-pixel.normal-form.v1";
    }
}
