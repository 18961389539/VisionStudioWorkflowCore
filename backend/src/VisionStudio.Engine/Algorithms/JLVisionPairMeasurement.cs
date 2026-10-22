using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class JLVisionPairMeasurement
{
    internal readonly record struct Pair(double First, double Second, double FirstAmplitude, double SecondAmplitude)
    {
        public double Width => Second - First;
        public double Strength => Math.Min(Math.Abs(FirstAmplitude), Math.Abs(SecondAmplitude));
    }

    internal sealed record Result(IReadOnlyList<Pair> Pairs, int SampleCount, string Reason);

    public static Result Find(double[] profile, double spacing, string polarity, double threshold, CancellationToken cancellationToken)
    {
        var pairs = new List<Pair>();
        var valid = 0;
        var measurable = false;
        // Each finite run is measured separately: an ROI hole must never create a pair
        // between edges on opposite sides of the excluded region.
        for (var start = 0; start < profile.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(profile[start])) { start++; continue; }
            var end = start;
            while (end < profile.Length && double.IsFinite(profile[end]))
            {
                if ((end & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                end++;
            }
            var length = end - start;
            valid += length;
            // sigma=1 needs at least eight samples of filter support.
            if (length >= 8)
            {
                measurable = true;
                using var pixels = new Mat(3, length, MatType.CV_32FC1);
                for (var x = 0; x < length; x++)
                {
                    if ((x & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    for (var y = 0; y < 3; y++) pixels.Set(y, x, (float)profile[start + x]);
                }
                using var image = JLVisionImages.FromMat(pixels);
                using var measure = new JlMeasure();
                measure.GenMeasureRectangle2(1d, (length - 1) / 2d, 0d, (length - 1) / 2d, 1d, length, 3, "bilinear");
                // Calling both transitions makes "any" include both bright strips and
                // dark gaps rather than letting the first edge determine all pair polarity.
                if (polarity != "dark") AddPairs("positive");
                if (polarity != "bright") AddPairs("negative");

                void AddPairs(string transition)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    measure.MeasurePairs(image, 1d, Math.Max(threshold, .0001), transition, "all",
                        out var row1, out var col1, out var amp1, out var row2, out var col2, out var amp2,
                        out var intra, out var inter);
                    using (row1) using (col1) using (amp1) using (row2) using (col2) using (amp2) using (intra) using (inter)
                    {
                        for (var i = 0; i < col1.Length; i++)
                        {
                            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                            var first = (start + col1[i].D) * spacing;
                            var second = (start + col2[i].D) * spacing;
                            if (double.IsFinite(first) && double.IsFinite(second) && second > first)
                                pairs.Add(new(first, second, amp1[i].D, amp2[i].D));
                        }
                    }
                }
            }
            start = end;
        }
        pairs.Sort((a, b) => a.First.CompareTo(b.First));
        return new(pairs, valid, pairs.Count > 0 ? "ok" : valid == 0 ? "profile-has-no-finite-samples"
            : !measurable ? "profile-has-insufficient-contiguous-samples" : "no-edge-pair-above-threshold");
    }
}
