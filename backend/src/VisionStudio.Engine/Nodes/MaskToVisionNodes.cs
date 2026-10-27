using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 掩码 → 区域（region.fromMask，2026-10-26 · 缺口分析 P1-17）。
///
/// 背景：`inference.segmentation` 的 `mask` 端口是 Image，此前只能接 `region.fromThreshold` 再阈值一次，
/// 隐含"0/255 二值"的假设，语义晦涩。本节点直接接受掩码图：
/// - `threshold`（默认 1）= "像素值 ≥ 该值即属于区域"，等价于非零判定；
/// - `invert` 取反（深色缺陷掩码、反相输出的模型）；
/// - 输出区域按 **8 邻域连通域** 拆成多个 component，与 native region 的语义一致，
///   下游 region.properties / region.filter 的连通域计数才有意义。
/// </summary>
public sealed class RegionFromMaskNode : IVisionNodeExecutor
{
    public string Type => "region.fromMask";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        using var gray = RecognitionImageHelpers.ToGray8(source.Mat);
        var threshold = ReadThreshold(node);
        var invert = node.GetBool("invert", false);
        using var binary = Binarize(gray, threshold, invert);

        var (components, pixelCount) = BuildComponents(binary, cancellationToken);
        var region = new VisionRegion(binary.Width, binary.Height, components);
        var image = VisionImage.Own(binary.Clone());
        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("threshold", threshold),
                ("invert", invert),
                ("width", binary.Width),
                ("height", binary.Height),
                ("componentCount", components.Count),
                ("pixelCount", pixelCount));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["region"] = VisionValue.Region(region),
                    ["image"] = VisionValue.Image(image),
                    ["count"] = VisionValue.Integer(components.Count),
                    ["area"] = VisionValue.Integer(pixelCount),
                    ["found"] = VisionValue.Boolean(components.Count > 0),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>
                {
                    ["componentCount"] = components.Count,
                    ["pixelCount"] = pixelCount,
                    ["threshold"] = threshold,
                    ["invert"] = invert,
                    ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }

    internal static int ReadThreshold(NodeDefinition node)
    {
        var threshold = node.GetInt("threshold", 1);
        if (threshold is < 1 or > 255)
            throw new InvalidOperationException("mask threshold must be within 1..255 (pixels at or above it belong to the region).");
        return threshold;
    }

    /// <summary>≥threshold 记为前景；invert 再取反。输出恒为 8 位单通道 0/255。</summary>
    internal static Mat Binarize(Mat gray, int threshold, bool invert)
    {
        var binary = new Mat();
        Cv2.Threshold(gray, binary, threshold - 1, 255, ThresholdTypes.Binary);
        if (invert) Cv2.BitwiseNot(binary, binary);
        return binary;
    }

    /// <summary>
    /// 按 8 邻域连通域生成 Region 行程：每个连通域一个 component。
    /// 单遍扫描 + 按标签累积行程，避免把"多块缺陷"合并成一整块。
    /// </summary>
    internal static (List<VisionRegionComponent> Components, long PixelCount) BuildComponents(Mat binary, CancellationToken cancellationToken)
    {
        using var labels = new Mat();
        var labelCount = Cv2.ConnectedComponents(binary, labels, PixelConnectivity.Connectivity8, MatType.CV_32S);
        var runsByLabel = new Dictionary<int, List<VisionRegionRun>>(labelCount);
        var indexer = labels.GetGenericIndexer<int>();
        long pixelCount = 0;
        for (var y = 0; y < labels.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runStart = -1;
            var runLabel = 0;
            for (var x = 0; x < labels.Width; x++)
            {
                var label = indexer[y, x];
                if (label == runLabel)
                {
                    if (label != 0 && runStart < 0) runStart = x;
                    continue;
                }

                // 标签切换：先收束上一段行程。
                if (runLabel != 0 && runStart >= 0)
                {
                    if (!runsByLabel.TryGetValue(runLabel, out var closed))
                        runsByLabel[runLabel] = closed = [];
                    closed.Add(new VisionRegionRun(y, runStart, x - 1));
                    pixelCount += x - runStart;
                }
                runLabel = label;
                runStart = label == 0 ? -1 : x;
            }

            if (runLabel != 0 && runStart >= 0)
            {
                if (!runsByLabel.TryGetValue(runLabel, out var tail))
                    runsByLabel[runLabel] = tail = [];
                tail.Add(new VisionRegionRun(y, runStart, labels.Width - 1));
                pixelCount += labels.Width - runStart;
            }
        }

        // 按标签顺序输出，保证同一掩码两次运行得到相同的 component 顺序。
        var components = runsByLabel.OrderBy(pair => pair.Key)
            .Select(pair => new VisionRegionComponent(pair.Value))
            .ToList();
        return (components, pixelCount);
    }
}

/// <summary>
/// 掩码 → 轮廓（contour.fromMask）。用于把分割掩码直接接到测量/拟合链路，
/// 不必先转 Region 再 `region.toDetections`。
///
/// 语义：
/// - `threshold`/`invert` 与 region.fromMask 完全一致（同一条掩码两个出口，参数不该有两套解释）；
/// - `retrieval=external` 只取外轮廓（测量常用）；`all` 保留孔洞层级，ParentIndex 指向输出了的父轮廓；
/// - `minArea` 过滤噪点轮廓，`maxContours`/`maxPoints` 是预算上限（超限明确失败而不是静默截断）。
/// </summary>
public sealed class ContourFromMaskNode : IVisionNodeExecutor
{
    public string Type => "contour.fromMask";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        using var gray = RecognitionImageHelpers.ToGray8(source.Mat);
        var threshold = RegionFromMaskNode.ReadThreshold(node);
        var invert = node.GetBool("invert", false);
        using var binary = RegionFromMaskNode.Binarize(gray, threshold, invert);

        var retrieval = node.GetString("retrieval", "external").Trim().ToLowerInvariant() switch
        {
            "external" => RetrievalModes.External,
            "all" => RetrievalModes.Tree,
            _ => throw new InvalidOperationException("contour.fromMask retrieval must be external or all.")
        };
        var approximation = node.GetString("approximation", "simple").Trim().ToLowerInvariant() switch
        {
            "simple" => ContourApproximationModes.ApproxSimple,
            "none" => ContourApproximationModes.ApproxNone,
            _ => throw new InvalidOperationException("contour.fromMask approximation must be simple or none.")
        };
        var minArea = node.GetDouble("minArea", 0);
        if (!double.IsFinite(minArea) || minArea < 0)
            throw new InvalidOperationException("contour.fromMask minArea must be finite and >= 0.");
        var maxContours = node.GetInt("maxContours", 512);
        if (maxContours is < 1 or > 4096)
            throw new InvalidOperationException("contour.fromMask maxContours must be within 1..4096.");
        var maxPoints = node.GetInt("maxPoints", 5000);
        if (maxPoints is < 3 or > 200000)
            throw new InvalidOperationException("contour.fromMask maxPoints must be within 3..200000.");

        Cv2.FindContours(binary, out Point[][] contours, out HierarchyIndex[] hierarchy, retrieval, approximation);
        var kept = new List<(int Original, double Area)>();
        for (var index = 0; index < contours.Length; index++)
        {
            var area = Cv2.ContourArea(contours[index]);
            if (area < minArea) continue;
            kept.Add((index, area));
        }
        if (kept.Count > maxContours)
            throw new InvalidOperationException($"contour.fromMask found {kept.Count} contours above minArea={minArea}; raise maxContours or tighten minArea instead of silently truncating.");

        var outputIndexByOriginal = kept
            .Select((item, position) => (item.Original, position))
            .ToDictionary(pair => pair.Original, pair => pair.position);
        var results = new List<VisionContour>(kept.Count);
        foreach (var (original, _) in kept)
        {
            var points = contours[original];
            if (points.Length > maxPoints)
                throw new InvalidOperationException($"contour.fromMask contour {original} has {points.Length} points which exceeds maxPoints={maxPoints}.");
            var parent = hierarchy.Length > original ? hierarchy[original].Parent : -1;
            var parentIndex = parent >= 0 && outputIndexByOriginal.TryGetValue(parent, out var mapped) ? mapped : -1;
            results.Add(ContourNodesHelper.ToContour(points, results.Count, parentIndex, parent >= 0, maxPoints));
        }

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("threshold", threshold),
            ("invert", invert),
            ("retrieval", retrieval == RetrievalModes.External ? "external" : "all"),
            ("approximation", approximation == ContourApproximationModes.ApproxSimple ? "simple" : "none"),
            ("contourCount", results.Count),
            ("minArea", minArea));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["contours"] = VisionValue.Contours(results),
                ["count"] = VisionValue.Integer(results.Count),
                ["found"] = VisionValue.Boolean(results.Count > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["contourCount"] = results.Count,
                ["threshold"] = threshold,
                ["invert"] = invert,
                ["minArea"] = minArea,
                ["diagnostics"] = diagnostics
            }));
    }
}
