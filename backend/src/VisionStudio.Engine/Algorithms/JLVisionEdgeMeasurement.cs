using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class JLVisionEdgeMeasurement
{
    internal readonly record struct Edge(bool Found, double Position, double Strength, double Error, int Count, string Reason);

    public static Edge Find(double[] profile, double spacing, string polarity, double threshold, string direction, string refine)
    {
        var candidates = new List<(double Position, double Amplitude)>();
        var valid = profile.Count(double.IsFinite);
        if (valid == 0) return new(false, 0, 0, 0, valid, "profile-has-no-finite-samples");
        if (valid < 3) return new(false, 0, 0, 0, valid, "profile-has-insufficient-contiguous-samples");
        var hasMeasurableSegment = false;
        for (var start = 0; start < profile.Length;)
        {
            while (start < profile.Length && !double.IsFinite(profile[start])) start++;
            var end = start;
            while (end < profile.Length && double.IsFinite(profile[end])) end++;
            var length = end - start;
            if (length >= 3)
            {
                hasMeasurableSegment = true;
                using var pixels = new Mat(3, length, MatType.CV_32FC1);
                for (var y = 0; y < 3; y++) for (var x = 0; x < length; x++) pixels.Set(y, x, (float)profile[start + x]);
                using var image = JLVisionImages.FromMat(pixels);
                using var measure = new JlMeasure();
                measure.GenMeasureRectangle2(1d, (length - 1) / 2d, 0d, (length - 1) / 2d, 1d, length, 3, "bilinear");
                measure.MeasurePos(image, 1d, Math.Max(threshold, .0001), polarity.ToLowerInvariant() switch { "rising" => "positive", "falling" => "negative", _ => "all" }, "all", out var rows, out var columns, out var amplitude, out var distance);
                using (rows) using (columns) using (amplitude) using (distance)
                    for (var i = 0; i < columns.Length; i++) candidates.Add((start + columns[i].D, Math.Abs(amplitude[i].D)));
            }
            start = end + 1;
        }
        if (candidates.Count == 0)
            return new(false, 0, 0, 0, valid, hasMeasurableSegment ? "no-edge-above-threshold" : "profile-has-insufficient-contiguous-samples");
        var edge = direction.ToLowerInvariant() switch
        { "firstfromstart" => candidates.MinBy(x => x.Position), "firstfromend" => candidates.MaxBy(x => x.Position), _ => candidates.MaxBy(x => x.Amplitude) };
        var position = refine == "none" ? Math.Round(edge.Position - .5) + .5 : edge.Position;
        var error = PeakResidual(profile, edge.Position);
        return new(true, position * spacing, edge.Amplitude, error, valid, "ok");
    }

    private static double PeakResidual(double[] profile, double position)
    {
        var peak = (int)Math.Round(position + .5);
        if (peak < 3 || peak + 2 >= profile.Length) return -1;
        var ys = Enumerable.Range(-2, 5).Select(i => Math.Abs(profile[peak + i] - profile[peak + i - 1])).ToArray();
        if (ys.Any(x => !double.IsFinite(x))) return -1;
        var sum = ys.Sum(); var xy = 0d; var xxy = 0d;
        for (var i = 0; i < 5; i++) { xy += (i - 2) * ys[i]; xxy += (i - 2) * (i - 2) * ys[i]; }
        var a = (xxy - 2 * sum) / 14; var b = xy / 10; var c = (sum - 10 * a) / 5;
        return Math.Sqrt(Enumerable.Range(-2, 5).Sum(x => Math.Pow(ys[x + 2] - (a * x * x + b * x + c), 2)) / 5);
    }
}
