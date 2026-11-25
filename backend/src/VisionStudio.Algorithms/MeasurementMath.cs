// 2026-11-25 从 ExtendedMeasurementNodes.cs 下沉到算法层：
// 算法实现（JLVisionMatching / ContourThicknessMeasurement）需要它，
// 它却不该反过来依赖引擎的节点目录。

using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class MeasurementMath
{
    public static double Hypot(double x, double y)
    {
        x = Math.Abs(x); y = Math.Abs(y);
        var max = Math.Max(x, y);
        if (double.IsInfinity(max)) return double.PositiveInfinity;
        if (max == 0) return 0;
        var min = Math.Min(x, y) / max;
        return max * Math.Sqrt(1 + min * min);
    }
}
