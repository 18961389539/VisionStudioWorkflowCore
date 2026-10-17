using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class JLVisionEdgeMeasurement
{
    internal readonly record struct Edge(bool Found, double Position, double Strength, double Error, int Count, string Reason);
    private const int MinimumNativeSegmentLength = 8;

    public static Edge Find(double[] profile, double spacing, string polarity, double threshold, string direction, string refine,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<(double Position, double Amplitude)>();
        var valid = 0;
        for (var index = 0; index < profile.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (double.IsFinite(profile[index])) valid++;
        }
        if (valid == 0) return new(false, 0, 0, 0, valid, "profile-has-no-finite-samples");
        if (valid < 3) return new(false, 0, 0, 0, valid, "profile-has-insufficient-contiguous-samples");
        var hasMeasurableSegment = false;
        for (var start = 0; start < profile.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (start < profile.Length && !double.IsFinite(profile[start]))
            {
                if ((start & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                start++;
            }
            var end = start;
            while (end < profile.Length && double.IsFinite(profile[end]))
            {
                if ((end & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                end++;
            }
            var length = end - start;
            if (length >= 3)
            {
                hasMeasurableSegment = true;
                // HALCON's Gaussian measurement filter needs a longer support than a very
                // narrow radial band can provide. Use a local gradient fit for these short
                // profiles so small circles return a measurement (or found=false) instead of
                // surfacing HALCON error #3022 to the workflow runner.
                if (length < MinimumNativeSegmentLength)
                {
                    AddShortProfileCandidates(profile, start, length, polarity, threshold, refine, candidates, cancellationToken);
                    start = end + 1;
                    continue;
                }
                using var pixels = new Mat(3, length, MatType.CV_32FC1);
                for (var y = 0; y < 3; y++)
                    for (var x = 0; x < length; x++)
                    {
                        if ((x & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        pixels.Set(y, x, (float)profile[start + x]);
                    }
                using var image = JLVisionImages.FromMat(pixels);
                using var measure = new JlMeasure();
                measure.GenMeasureRectangle2(1d, (length - 1) / 2d, 0d, (length - 1) / 2d, 1d, length, 3, "bilinear");
                cancellationToken.ThrowIfCancellationRequested();
                measure.MeasurePos(image, 1d, Math.Max(threshold, .0001), polarity.ToLowerInvariant() switch { "rising" => "positive", "falling" => "negative", _ => "all" }, "all", out var rows, out var columns, out var amplitude, out var distance);
                using (rows) using (columns) using (amplitude) using (distance)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var i = 0; i < columns.Length; i++)
                    {
                        if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                        candidates.Add((start + columns[i].D, Math.Abs(amplitude[i].D)));
                    }
                }
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

    private static void AddShortProfileCandidates(double[] profile, int start, int length, string polarity, double threshold,
        string refine, List<(double Position, double Amplitude)> candidates, CancellationToken cancellationToken)
    {
        var requestedPolarity = polarity.ToLowerInvariant();
        var gradients = new double[length - 1];
        for (var i = 0; i < gradients.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            gradients[i] = profile[start + i + 1] - profile[start + i];
        }

        for (var i = 0; i < gradients.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var gradient = gradients[i];
            var amplitude = Math.Abs(gradient);
            var leftAmplitude = i > 0 ? Math.Abs(gradients[i - 1]) : 0;
            var rightAmplitude = i + 1 < gradients.Length ? Math.Abs(gradients[i + 1]) : 0;
            // Treat each edge as a gradient peak before applying first/last-edge selection.
            // Otherwise a low threshold can select the blurred edge's rising tail.
            var tiesPreviousPeak = i > 0 && Math.Abs(amplitude - leftAmplitude) <= 1e-12;
            var tiesRightBoundary = i == 0 && Math.Abs(amplitude - rightAmplitude) <= 1e-12;
            if (amplitude < leftAmplitude || amplitude < rightAmplitude || tiesPreviousPeak || tiesRightBoundary)
                continue;
            if (amplitude < Math.Max(threshold, .0001)
                || requestedPolarity == "rising" && gradient <= 0
                || requestedPolarity == "falling" && gradient >= 0)
                continue;

            var position = start + i + .5;
            if (!refine.Equals("none", StringComparison.OrdinalIgnoreCase) && i > 0 && i + 1 < gradients.Length)
            {
                var left = Math.Abs(gradients[i - 1]);
                var center = amplitude;
                var right = Math.Abs(gradients[i + 1]);
                var denominator = left - 2 * center + right;
                if (denominator < -1e-12)
                {
                    var offset = Math.Clamp(.5 * (left - right) / denominator, -.5, .5);
                    position += offset;
                    amplitude = center - .25 * (left - right) * offset;
                }
            }
            candidates.Add((position, amplitude));
        }
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
