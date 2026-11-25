using System.Buffers;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

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
        var mask = MaskThreshold.Read(node);
        using var binary = mask.Apply(source.Mat);

        var (components, pixelCount) = BuildComponents(binary, cancellationToken);
        var region = new VisionRegion(binary.Width, binary.Height, components);
        var image = VisionImage.Own(binary.Clone());
        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("maskMode", mask.Mode),
                ("threshold", mask.Value),
                ("invert", mask.Invert),
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
                    ["maskMode"] = mask.Mode,
                    ["threshold"] = mask.Value,
                    ["invert"] = mask.Invert,
                    ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }

    /// <summary>
    /// 按 8 邻域连通域生成 Region 行程：每个连通域一个 component。
    /// 单遍扫描 + 按标签累积行程，避免把"多块缺陷"合并成一整块。
    /// </summary>
    internal static (List<VisionRegionComponent> Components, long PixelCount) BuildComponents(Mat binary, CancellationToken cancellationToken)
    {
        using var labels = new Mat();
        var labelCount = Cv2.ConnectedComponents(binary, labels, PixelConnectivity.Connectivity8, MatType.CV_32S);
        if (labelCount - 1 > VisionRegion.MaxComponents)
            throw new InvalidOperationException($"region.fromMask found {labelCount - 1} components; the limit is {VisionRegion.MaxComponents}. Reduce mask noise before conversion.");
        var runsByLabel = new Dictionary<int, List<VisionRegionRun>>(labelCount);
        var width = labels.Width;
        var height = labels.Height;
        long pixelCount = 0;
        var runCount = 0;

        void AddRun(int label, int row, int start, int end)
        {
            if (++runCount > VisionRegion.MaxRuns)
                throw new InvalidOperationException($"region.fromMask exceeded the {VisionRegion.MaxRuns} run limit. Simplify the mask before conversion.");
            if (!runsByLabel.TryGetValue(label, out var runs))
                runsByLabel[label] = runs = [];
            runs.Add(new VisionRegionRun(row, start, end));
            pixelCount += end - start + 1;
        }

        if (width > 0 && height > 0)
        {
            var rowBuffer = ArrayPool<int>.Shared.Rent(width);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Marshal.Copy(labels.Ptr(y), rowBuffer, 0, width);
                    var runStart = -1;
                    var runLabel = 0;
                    for (var x = 0; x < width; x++)
                    {
                        var label = rowBuffer[x];
                        if (label == runLabel)
                        {
                            if (label != 0 && runStart < 0) runStart = x;
                            continue;
                        }

                        // 标签切换：先收束上一段行程。
                        if (runLabel != 0 && runStart >= 0)
                            AddRun(runLabel, y, runStart, x - 1);
                        runLabel = label;
                        runStart = label == 0 ? -1 : x;
                    }

                    if (runLabel != 0 && runStart >= 0)
                        AddRun(runLabel, y, runStart, width - 1);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rowBuffer);
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
        var mask = MaskThreshold.Read(node);
        using var binary = mask.Apply(source.Mat);

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
        var depths = Enumerable.Repeat(-1, hierarchy.Length).ToArray();
        int Depth(int index)
        {
            var pending = new Stack<int>();
            var current = index;
            while (current >= 0 && depths[current] < 0)
            {
                pending.Push(current);
                current = hierarchy[current].Parent;
            }
            var depth = current < 0 ? -1 : depths[current];
            while (pending.TryPop(out var item)) depths[item] = ++depth;
            return depths[index];
        }

        var results = new List<VisionContour>(kept.Count);
        foreach (var (original, _) in kept)
        {
            var points = contours[original];
            if (points.Length > maxPoints)
                throw new InvalidOperationException($"contour.fromMask contour {original} has {points.Length} points which exceeds maxPoints={maxPoints}.");
            var parent = hierarchy.Length > original ? hierarchy[original].Parent : -1;
            var parentIndex = parent >= 0 && outputIndexByOriginal.TryGetValue(parent, out var mapped) ? mapped : -1;
            var isHole = hierarchy.Length > original && Depth(original) % 2 == 1;
            results.Add(ContourNodesHelper.ToContour(points, results.Count, parentIndex, isHole, maxPoints));
        }

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("maskMode", mask.Mode),
            ("threshold", mask.Value),
            ("invert", mask.Invert),
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
                ["maskMode"] = mask.Mode,
                ["threshold"] = mask.Value,
                ["invert"] = mask.Invert,
                ["minArea"] = minArea,
                ["diagnostics"] = diagnostics
            }));
    }
}

/// <summary>Shared inclusive threshold and optional inversion. Output is always a byte mask with values 0 and 255.</summary>
internal sealed record class MaskThreshold(string Mode, double Value, bool Invert)
{
    public static MaskThreshold Read(NodeDefinition node)
    {
        var mode = node.GetString("maskMode", "byte").Trim().ToLowerInvariant();
        var threshold = mode switch
        {
            "byte" => node.GetInt("threshold", 1),
            "probability" => node.GetDouble("probabilityThreshold", 0.5),
            _ => throw new InvalidOperationException("maskMode must be byte or probability.")
        };
        if (mode == "byte" && threshold is < 1 or > 255)
            throw new InvalidOperationException("mask threshold must be within 1..255 (pixels at or above it belong to the region).");
        if (mode == "probability" && (!double.IsFinite(threshold) || threshold is < 0 or > 1))
            throw new InvalidOperationException("probabilityThreshold must be finite and within 0..1.");
        return new(mode, threshold, node.GetBool("invert", false));
    }

    public Mat Apply(Mat source)
    {
        var binary = new Mat();
        try
        {
            if (Mode == "probability")
            {
                if (source.Empty() || (source.Type() != MatType.CV_32FC1 && source.Type() != MatType.CV_64FC1))
                    throw new InvalidOperationException("Probability masks require a non-empty, single-channel float32 or float64 image.");
                Cv2.InRange(source, Scalar.All(0), Scalar.All(1), binary);
                if (JLVisionOperators.CountNonZero(binary) != source.Total())
                    throw new InvalidOperationException("Probability mask pixels must be finite and within 0..1.");
                Cv2.InRange(source, Scalar.All(Value), Scalar.All(1), binary);
            }
            else
            {
                using var gray = RecognitionImageHelpers.ToGray8(source);
                Cv2.Threshold(gray, binary, Value - 1, 255, ThresholdTypes.Binary);
            }
            if (Invert) Cv2.BitwiseNot(binary, binary);
            return binary;
        }
        catch
        {
            binary.Dispose();
            throw;
        }
    }
}
