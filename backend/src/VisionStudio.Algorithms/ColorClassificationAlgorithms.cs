using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class ColorClassificationAlgorithms
{
    internal readonly record struct ColorClassStat(
        int Index, string Name, long PixelCount, double Ratio,
        double C0, double C1, double C2, string ColorHex, double? Tolerance);

    internal readonly record struct ReferenceTarget(string Name, Vec3f Color, double ToleranceSquared, double Tolerance);

    internal static ClassificationResult Cluster(Mat colors, Mat? mask, string colorSpace,
        int clusterCount, int stride, int maxSamples, CancellationToken cancellationToken)
    {
        var sampleCount = CountSamples(colors, mask, stride, cancellationToken);
        if (sampleCount < clusterCount)
            throw new InvalidOperationException(
                $"color.classify needs at least {clusterCount} sampled pixels for K-means but found {sampleCount}; lower sampleStride or clusterCount.");
        if (sampleCount > maxSamples)
            throw new InvalidOperationException(
                $"color.classify would sample {sampleCount} pixels above maxSamples={maxSamples}; increase sampleStride or maxSamples instead of silently truncating.");

        using var samples = new Mat(sampleCount, 3, MatType.CV_32F);
        FillSamples(colors, mask, stride, samples, cancellationToken);
        using var labels = new Mat(sampleCount, 1, MatType.CV_32S);
        InitializeLabels(samples, labels, clusterCount, colorSpace);
        cancellationToken.ThrowIfCancellationRequested();

        using var centers = new Mat();
        var criteria = new TermCriteria(CriteriaTypes.MaxIter | CriteriaTypes.Eps, 100, colorSpace == "lab" ? 0.2 : 1.0);
        Cv2.Kmeans(samples, clusterCount, labels, criteria, 1, KMeansFlags.UseInitialLabels, centers);
        cancellationToken.ThrowIfCancellationRequested();
        var sortedCenters = ReadAndSortCenters(centers, clusterCount, colorSpace);

        var labelMap = new Mat(colors.Rows, colors.Cols, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            var counts = new long[clusterCount + 1];
            var roiPixels = AssignClusterLabels(colors, mask, labelMap, sortedCenters, counts, cancellationToken);
            var stats = new List<ColorClassStat>(clusterCount);
            for (var index = 0; index < clusterCount; index++)
            {
                var center = sortedCenters[index];
                stats.Add(new ColorClassStat(index + 1, $"cluster-{index + 1}", counts[index + 1],
                    roiPixels == 0 ? 0 : counts[index + 1] / (double)roiPixels,
                    center.Item0, center.Item1, center.Item2,
                    ToHex(colorSpace, center.Item0, center.Item1, center.Item2), null));
            }
            return new ClassificationResult(labelMap, stats, roiPixels, 0, sampleCount, Dominant(stats));
        }
        catch
        {
            labelMap.Dispose();
            throw;
        }
    }

    internal static ClassificationResult ClassifyReferences(Mat colors, Mat? mask, string colorSpace,
        List<ReferenceTarget> targets, CancellationToken cancellationToken)
    {
        var labelMap = new Mat(colors.Rows, colors.Cols, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            var counts = new long[targets.Count + 1];
            var (roiPixels, unmatched) = AssignReferenceLabels(colors, mask, labelMap, targets, counts, cancellationToken);
            var stats = new List<ColorClassStat>(targets.Count);
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                stats.Add(new ColorClassStat(index + 1, target.Name, counts[index + 1],
                    roiPixels == 0 ? 0 : counts[index + 1] / (double)roiPixels,
                    target.Color.Item0, target.Color.Item1, target.Color.Item2,
                    ToHex(colorSpace, target.Color.Item0, target.Color.Item1, target.Color.Item2), target.Tolerance));
            }
            return new ClassificationResult(labelMap, stats, roiPixels, unmatched, 0, Dominant(stats));
        }
        catch
        {
            labelMap.Dispose();
            throw;
        }
    }

    internal static Mat ToColorSpace(Mat source, string colorSpace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bgr = new Mat();
        switch (source.Channels())
        {
            case 3:
                source.CopyTo(bgr);
                break;
            case 1:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                break;
            case 4:
                Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            default:
                throw new InvalidOperationException($"color.classify supports 1, 3, or 4 channel images; received {source.Channels()}.");
        }
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
        var result = new Mat();
        try
        {
            if (colorSpace == "rgb")
            {
                rgb.ConvertTo(result, MatType.CV_32FC3);
                return result;
            }
            using var rgbFloat = new Mat();
            rgb.ConvertTo(rgbFloat, MatType.CV_32FC3, 1d / 255d);
            Cv2.CvtColor(rgbFloat, result, ColorConversionCodes.RGB2Lab);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static int CountSamples(Mat colors, Mat? mask, int stride, CancellationToken cancellationToken)
    {
        var rows = colors.Rows;
        var cols = colors.Cols;
        var count = 0;
        for (var y = 0; y < rows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < cols; x++)
            {
                if (x % stride != 0 || y % stride != 0) continue;
                if (mask is not null && mask.At<byte>(y, x) == 0) continue;
                count++;
            }
        }
        return count;
    }

    private static void FillSamples(Mat colors, Mat? mask, int stride, Mat samples, CancellationToken cancellationToken)
    {
        var rows = colors.Rows;
        var cols = colors.Cols;
        var sample = 0;
        for (var y = 0; y < rows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < cols; x++)
            {
                if (x % stride != 0 || y % stride != 0) continue;
                if (mask is not null && mask.At<byte>(y, x) == 0) continue;
                var color = colors.At<Vec3f>(y, x);
                samples.Set(sample, 0, color.Item0);
                samples.Set(sample, 1, color.Item1);
                samples.Set(sample, 2, color.Item2);
                sample++;
            }
        }
        if (sample != samples.Rows)
            throw new InvalidOperationException("color.classify sampled pixel count changed while filling the K-means matrix.");
    }

    private static void InitializeLabels(Mat samples, Mat labels, int clusterCount, string colorSpace)
    {
        var order = Enumerable.Range(0, samples.Rows).ToArray();
        Array.Sort(order, (left, right) =>
        {
            var comparison = Key(samples.At<float>(left, 0), samples.At<float>(left, 1), samples.At<float>(left, 2), colorSpace)
                .CompareTo(Key(samples.At<float>(right, 0), samples.At<float>(right, 1), samples.At<float>(right, 2), colorSpace));
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        for (var rank = 0; rank < order.Length; rank++)
            labels.Set(order[rank], 0, rank % clusterCount);
    }

    private static Vec3f[] ReadAndSortCenters(Mat centers, int clusterCount, string colorSpace)
    {
        var values = new Vec3f[clusterCount];
        for (var index = 0; index < clusterCount; index++)
        {
            var center = new Vec3f(centers.At<float>(index, 0), centers.At<float>(index, 1), centers.At<float>(index, 2));
            if (!float.IsFinite(center.Item0) || !float.IsFinite(center.Item1) || !float.IsFinite(center.Item2))
                throw new InvalidOperationException("color.classify K-means produced an empty cluster; lower clusterCount or increase sampleStride coverage.");
            values[index] = center;
        }
        Array.Sort(values, (left, right) =>
            Key(left.Item0, left.Item1, left.Item2, colorSpace).CompareTo(Key(right.Item0, right.Item1, right.Item2, colorSpace)));
        return values;
    }

    private static long AssignClusterLabels(
        Mat colors, Mat? mask, Mat labelMap, Vec3f[] centers, long[] counts, CancellationToken cancellationToken)
    {
        var rows = colors.Rows;
        var cols = colors.Cols;
        long roiPixels = 0;
        for (var y = 0; y < rows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < cols; x++)
            {
                if (mask is not null && mask.At<byte>(y, x) == 0) continue;
                roiPixels++;
                var color = colors.At<Vec3f>(y, x);
                var best = 0;
                var bestDistance = double.MaxValue;
                for (var index = 0; index < centers.Length; index++)
                {
                    var distance = DistanceSquared(color, centers[index]);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = index;
                }
                labelMap.Set(y, x, (byte)(best + 1));
                counts[best + 1]++;
            }
        }
        return roiPixels;
    }

    private static (long RoiPixels, long Unmatched) AssignReferenceLabels(
        Mat colors, Mat? mask, Mat labelMap, List<ReferenceTarget> targets, long[] counts, CancellationToken cancellationToken)
    {
        var rows = colors.Rows;
        var cols = colors.Cols;
        long roiPixels = 0;
        long unmatched = 0;
        for (var y = 0; y < rows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < cols; x++)
            {
                if (mask is not null && mask.At<byte>(y, x) == 0) continue;
                roiPixels++;
                var color = colors.At<Vec3f>(y, x);
                var best = -1;
                var bestDistance = double.MaxValue;
                for (var index = 0; index < targets.Count; index++)
                {
                    var target = targets[index];
                    var distance = DistanceSquared(color, target.Color);
                    if (distance > target.ToleranceSquared || distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = index;
                }
                if (best < 0)
                {
                    unmatched++;
                    continue;
                }
                labelMap.Set(y, x, (byte)(best + 1));
                counts[best + 1]++;
            }
        }
        return (roiPixels, unmatched);
    }

    private static double DistanceSquared(Vec3f left, Vec3f right)
    {
        var d0 = (double)left.Item0 - right.Item0;
        var d1 = (double)left.Item1 - right.Item1;
        var d2 = (double)left.Item2 - right.Item2;
        return d0 * d0 + d1 * d1 + d2 * d2;
    }

    private static double Key(float c0, float c1, float c2, string colorSpace) =>
        colorSpace == "lab" ? c0 : (double)c0 + c1 + c2;

    private static int Dominant(List<ColorClassStat> stats)
    {
        var best = 0L;
        var index = 0;
        foreach (var stat in stats)
        {
            if (stat.PixelCount <= best) continue;
            best = stat.PixelCount;
            index = stat.Index;
        }
        return index;
    }

    private static string ToHex(string colorSpace, double c0, double c1, double c2)
    {
        if (colorSpace == "rgb")
            return $"#{ClampByte(c0):X2}{ClampByte(c1):X2}{ClampByte(c2):X2}";
        using var lab = new Mat(1, 1, MatType.CV_32FC3);
        lab.Set(0, 0, new Vec3f((float)c0, (float)c1, (float)c2));
        using var rgb = new Mat();
        Cv2.CvtColor(lab, rgb, ColorConversionCodes.Lab2RGB);
        using var rgb8 = new Mat();
        rgb.ConvertTo(rgb8, MatType.CV_8UC3, 255);
        var value = rgb8.At<Vec3b>(0, 0);
        return $"#{value.Item0:X2}{value.Item1:X2}{value.Item2:X2}";
    }

    private static byte ClampByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    internal sealed record ClassificationResult(
        Mat LabelMap, List<ColorClassStat> Stats, long RoiPixels, long UnmatchedPixels, int SampleCount, int DominantClass);
}
