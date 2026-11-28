using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using static VisionStudio.Engine.Algorithms.ColorClassificationAlgorithms;

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
        var maxUnmatchedRatio = node.GetDouble("maxUnmatchedRatio", 0.5);
        if (!double.IsFinite(maxUnmatchedRatio) || maxUnmatchedRatio is < 0 or > 1)
            throw new InvalidOperationException("color.classify maxUnmatchedRatio must be finite and within 0..1.");
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
            var unmatchedRatio = result.RoiPixels == 0 ? 0 : result.UnmatchedPixels / (double)result.RoiPixels;
            var unmatchedThresholdExceeded = mode == "reference" && unmatchedRatio > maxUnmatchedRatio + 1e-9;
            var found = result.Stats.Any(stat => stat.PixelCount > 0) && !unmatchedThresholdExceeded;
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
                    ("unmatchedRatio", unmatchedRatio),
                    ("maxUnmatchedRatio", maxUnmatchedRatio),
                    ("unmatchedThresholdExceeded", unmatchedThresholdExceeded),
                    ("failureReason", unmatchedThresholdExceeded ? "unmatched-reference-colors" : null),
                    ("clusterCount", mode == "cluster" ? result.Stats.Count : null),
                    ("sampleCount", result.SampleCount),
                    ("roiApplied", roi is not null),
                    ("elapsedMs", timer.Elapsed.TotalMilliseconds));
                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(image),
                    ["found"] = VisionValue.Boolean(found),
                    ["count"] = VisionValue.Integer(result.Stats.Count(stat => stat.PixelCount > 0)),
                    ["classCounts"] = VisionValue.String(SerializeStats(result.Stats, colorSpace)),
                    ["dominantClass"] = VisionValue.Integer(result.DominantClass),
                    ["unmatchedPixels"] = VisionValue.Integer(result.UnmatchedPixels),
                    ["unmatchedRatio"] = VisionValue.Double(unmatchedRatio),
                    ["unmatchedThresholdExceeded"] = VisionValue.Boolean(unmatchedThresholdExceeded),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                };
                return new NodeExecutorResult(outputs, new Dictionary<string, object?>
                {
                    ["mode"] = mode,
                    ["colorSpace"] = colorSpace,
                    ["classCount"] = result.Stats.Count(stat => stat.PixelCount > 0),
                    ["roiPixels"] = result.RoiPixels,
                    ["unmatchedPixels"] = result.UnmatchedPixels,
                    ["unmatchedRatio"] = unmatchedRatio,
                    ["maxUnmatchedRatio"] = maxUnmatchedRatio,
                    ["unmatchedThresholdExceeded"] = unmatchedThresholdExceeded,
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

        return Cluster(colors, mask, colorSpace, clusterCount, stride, maxSamples, cancellationToken);
    }

    private static ClassificationResult RunReference(
        NodeDefinition node, Mat colors, Mat? mask, string colorSpace, CancellationToken cancellationToken)
    {
        var targets = ParseReferenceColors(node, colorSpace);
        return ClassifyReferences(colors, mask, colorSpace, targets, cancellationToken);
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


}
