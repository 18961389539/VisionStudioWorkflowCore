using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 颜色聚类 / 分类（color.classify，2026-10-27）。
///
/// 现场的颜色判定长期只能靠 `region.fromColorRange` 手填三通道上下限；对"颜色批次有轻微漂移"
/// 或"色卡有多个标准色"的场景，用户无法让流程自己学习颜色分布。本节点提供两种语义：
/// - cluster：对 ROI 内的像素做 K-means，输出每个像素的类别标签图和各类中心色；
/// - reference：把像素归到给定的参考色（Lab ΔE 或 RGB 欧氏距离），超出容差的像素标为 0。
///
/// 输出标签图固定为 8 位单通道：0 = 背景/未匹配，1..N = 类别序号。颜色转换与 K-means 使用
/// OpenCV 5；分类距离在托管代码中计算，避免把容差语义藏进某个 OpenCV 标志位。
/// </summary>
public sealed class ColorClassifyNode : IVisionNodeExecutor
{
    public string Type => "color.classify";

    private sealed class ReferenceColor
    {
        public string? Name { get; set; }
        public double? L { get; set; }
        public double? A { get; set; }
        public double? B { get; set; }
        public double? R { get; set; }
        public double? G { get; set; }
        public double? Tolerance { get; set; }
    }

    private readonly record struct ColorClassStat(
        int Index, string Name, long PixelCount, double Ratio,
        double C0, double C1, double C2, string ColorHex, double? Tolerance);

    private readonly record struct ReferenceTarget(string Name, Vec3f Color, double ToleranceSquared, double Tolerance);

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        ValidateImage(source.Mat);

        var mode = node.GetString("mode", "cluster").Trim().ToLowerInvariant();
        if (mode is not ("cluster" or "reference"))
            throw new InvalidOperationException("color.classify mode must be cluster or reference.");

        var colorSpace = node.GetString("colorSpace", "lab").Trim().ToLowerInvariant();
        if (colorSpace is not ("lab" or "rgb"))
            throw new InvalidOperationException("color.classify colorSpace must be lab or rgb.");

        var roi = ReadRoi(context, node);
        using var colors = ToColorSpace(source.Mat, colorSpace, cancellationToken);
        using var mask = roi?.BuildMask(source.Mat.Size(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Mat? labelMap = null;
        try
        {
            var result = mode == "cluster"
                ? RunCluster(node, colors, mask, colorSpace, cancellationToken)
                : RunReference(node, colors, mask, colorSpace, cancellationToken);
            labelMap = result.LabelMap;
            var image = VisionImage.Own(labelMap);
            labelMap = null;
            timer.Stop();
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("mode", mode),
                    ("colorSpace", colorSpace),
                    ("classCount", result.Stats.Count(stat => stat.PixelCount > 0)),
                    ("roiPixels", result.RoiPixels),
                    ("unmatchedPixels", result.UnmatchedPixels),
                    ("clusterCount", mode == "cluster" ? result.Stats.Count : null),
                    ("sampleCount", result.SampleCount),
                    ("roiApplied", roi is not null),
                    ("elapsedMs", timer.Elapsed.TotalMilliseconds));
                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(image),
                    ["found"] = VisionValue.Boolean(result.Stats.Any(stat => stat.PixelCount > 0)),
                    ["count"] = VisionValue.Integer(result.Stats.Count(stat => stat.PixelCount > 0)),
                    ["classCounts"] = VisionValue.String(SerializeStats(result.Stats, colorSpace)),
                    ["dominantClass"] = VisionValue.Integer(result.DominantClass),
                    ["unmatchedPixels"] = VisionValue.Integer(result.UnmatchedPixels),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                };
                return new NodeExecutorResult(outputs, new Dictionary<string, object?>
                {
                    ["mode"] = mode,
                    ["colorSpace"] = colorSpace,
                    ["classCount"] = result.Stats.Count(stat => stat.PixelCount > 0),
                    ["roiPixels"] = result.RoiPixels,
                    ["unmatchedPixels"] = result.UnmatchedPixels,
                    ["clusterCount"] = mode == "cluster" ? result.Stats.Count : null,
                    ["sampleCount"] = result.SampleCount,
                    ["roiApplied"] = roi is not null,
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["algorithmBackend"] = "opencv",
                    ["diagnostics"] = diagnostics
                });
            }, cancellationToken));
        }
        finally
        {
            labelMap?.Dispose();
        }
    }

    private static ClassificationResult RunCluster(
        NodeDefinition node, Mat colors, Mat? mask, string colorSpace, CancellationToken cancellationToken)
    {
        var clusterCount = node.GetInt("clusterCount", 4);
        if (clusterCount is < 2 or > 32)
            throw new InvalidOperationException("color.classify clusterCount must be within 2..32.");
        var stride = node.GetInt("sampleStride", 2);
        if (stride is < 1 or > 32)
            throw new InvalidOperationException("color.classify sampleStride must be within 1..32.");
        var maxSamples = node.GetInt("maxSamples", 1_000_000);
        if (maxSamples is < 1000 or > 4_000_000)
            throw new InvalidOperationException("color.classify maxSamples must be within 1000..4000000.");

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

    private static ClassificationResult RunReference(
        NodeDefinition node, Mat colors, Mat? mask, string colorSpace, CancellationToken cancellationToken)
    {
        var targets = ParseReferenceColors(node, colorSpace);
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

    private static List<ReferenceTarget> ParseReferenceColors(NodeDefinition node, string colorSpace)
    {
        var json = node.GetString("referenceColorsJson", string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("color.classify referenceColorsJson is required in reference mode.");
        List<ReferenceColor>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ReferenceColor>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"color.classify referenceColorsJson is invalid: {ex.Message}", ex);
        }
        if (parsed is null || parsed.Count == 0)
            throw new InvalidOperationException("color.classify referenceColorsJson must contain at least one reference color.");
        if (parsed.Count > 32)
            throw new InvalidOperationException("color.classify accepts at most 32 reference colors.");

        var defaultTolerance = node.GetDouble("defaultTolerance", colorSpace == "lab" ? 10 : 30);
        if (!double.IsFinite(defaultTolerance) || defaultTolerance is < 0 or > 500)
            throw new InvalidOperationException("color.classify defaultTolerance must be finite and within 0..500.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<ReferenceTarget>(parsed.Count);
        for (var index = 0; index < parsed.Count; index++)
        {
            var reference = parsed[index];
            var name = string.IsNullOrWhiteSpace(reference.Name) ? $"class-{index + 1}" : reference.Name.Trim();
            if (name.Length > 64)
                throw new InvalidOperationException($"color.classify reference {index + 1} name is longer than 64 characters.");
            if (!names.Add(name))
                throw new InvalidOperationException($"color.classify reference name '{name}' is duplicated.");

            var tolerance = reference.Tolerance ?? defaultTolerance;
            if (!double.IsFinite(tolerance) || tolerance is < 0 or > 500)
                throw new InvalidOperationException($"color.classify reference '{name}' tolerance must be finite and within 0..500.");

            Vec3f color;
            if (colorSpace == "lab")
            {
                if (reference.L is null || reference.A is null || reference.B is null)
                    throw new InvalidOperationException($"color.classify reference '{name}' must provide l, a and b in lab mode.");
                if (!double.IsFinite(reference.L.Value) || reference.L.Value is < 0 or > 100 ||
                    !double.IsFinite(reference.A.Value) || reference.A.Value is < -200 or > 200 ||
                    !double.IsFinite(reference.B.Value) || reference.B.Value is < -200 or > 200)
                    throw new InvalidOperationException($"color.classify reference '{name}' Lab values must be L 0..100 and a/b -200..200.");
                color = new Vec3f((float)reference.L.Value, (float)reference.A.Value, (float)reference.B.Value);
            }
            else
            {
                if (reference.R is null || reference.G is null || reference.B is null)
                    throw new InvalidOperationException($"color.classify reference '{name}' must provide r, g and b in rgb mode.");
                if (!double.IsFinite(reference.R.Value) || reference.R.Value is < 0 or > 255 ||
                    !double.IsFinite(reference.G.Value) || reference.G.Value is < 0 or > 255 ||
                    !double.IsFinite(reference.B.Value) || reference.B.Value is < 0 or > 255)
                    throw new InvalidOperationException($"color.classify reference '{name}' RGB values must be within 0..255.");
                color = new Vec3f((float)reference.R.Value, (float)reference.G.Value, (float)reference.B.Value);
            }
            targets.Add(new ReferenceTarget(name, color, tolerance * tolerance, tolerance));
        }
        return targets;
    }

    private static Mat ToColorSpace(Mat source, string colorSpace, CancellationToken cancellationToken)
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

    private static void ValidateImage(Mat source)
    {
        if (source.Empty())
            throw new InvalidOperationException("color.classify requires a non-empty image.");
        if (source.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("color.classify requires an 8-bit image; convert 16-bit/float sources before color classification.");
        if (source.Channels() is not (1 or 3 or 4))
            throw new InvalidOperationException($"color.classify supports 1, 3, or 4 channel images; received {source.Channels()}.");
    }

    private static VisionRoi? ReadRoi(NodeExecutionContext context, NodeDefinition node) =>
        context.TryGetValue("roi", out var value) ? value.Require<VisionRoi>("roi") : node.GetRoi();

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

    private static string SerializeStats(List<ColorClassStat> stats, string colorSpace)
    {
        var payload = stats.Select(stat =>
        {
            object color = colorSpace == "lab"
                ? new { l = stat.C0, a = stat.C1, b = stat.C2 }
                : new { r = stat.C0, g = stat.C1, b = stat.C2 };
            return new
            {
                index = stat.Index,
                name = stat.Name,
                pixelCount = stat.PixelCount,
                ratio = stat.Ratio,
                color,
                colorHex = stat.ColorHex,
                tolerance = stat.Tolerance
            };
        });
        return JsonSerializer.Serialize(payload);
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

    private sealed record ClassificationResult(
        Mat LabelMap, List<ColorClassStat> Stats, long RoiPixels, long UnmatchedPixels, int SampleCount, int DominantClass);
}
