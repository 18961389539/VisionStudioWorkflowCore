using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 第五批（2026-10-14 精密定位与测量）· 5A 轮廓族：提取 → 筛选 → 属性。
/// 与批次一 measure.blobs（外轮廓 + 面积/圆度筛选一步到位）分工：
///   · contour.extract  只负责"找到轮廓并带层级落地"，不内置业务筛选；
///   · contour.filter   只负责按条件筛选既有轮廓列表（可反复组合）；
///   · contour.properties 只负责计算派生几何属性并转出 DetectionList 供集合/循环节点复用。
/// 共享约定：
///   · "没找到轮廓"是正常业务结果（count=0 / found=false），不是执行失败；
///   · 输入图像为空、轮廓端口缺失才是参数/输入错误（InvalidOperationException）；
///   · ROI 既可来自参数也可来自 roi 输入端口（端口优先），供 roi.transform 动态驱动。
/// </summary>
internal static class ContourNodesHelper
{
    /// <summary>ROI 解析：roi 输入端口（第五批新增图形类型）优先于 roi 参数。</summary>
    public static VisionRoi? ResolveRoi(NodeExecutionContext context, NodeDefinition node)
    {
        if (context.TryGetValue("roi", out var value))
            return value.Require<VisionRoi>("roi");
        return node.GetRoi();
    }

    /// <summary>
    /// 二值化：auto=Otsu（前景=亮，invert 时取反），fixed=固定阈值，direct=非零像素即前景。
    /// 返回调用方负责释放的 Mat；converted 记录是否发生了颜色→灰度转换（供 summary）。
    /// </summary>
    public static Mat Binarize(Mat source, string mode, double threshold, bool invert, out bool autoGray, out string effectiveMode)
    {
        var gray = PreprocessImageHelpers.ToGray8(source, out autoGray, out var owned);
        try
        {
            mode = mode.Trim().ToLowerInvariant();
            Mat binary;
            if (mode == "fixed")
            {
                binary = new Mat();
                Cv2.Threshold(gray, binary, Math.Clamp(threshold, 0, 255), 255,
                    invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
                effectiveMode = "fixed";
            }
            else if (mode == "direct")
            {
                binary = new Mat();
                Cv2.Threshold(gray, binary, 0, 255, invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
                effectiveMode = "direct";
            }
            else
            {
                binary = new Mat();
                Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                if (invert) Cv2.BitwiseNot(binary, binary);
                effectiveMode = "otsu";
            }

            if (!ReferenceEquals(gray, source)) owned?.Dispose();
            else owned = null;
            return binary;
        }
        catch
        {
            if (!ReferenceEquals(gray, source)) owned?.Dispose();
            throw;
        }
    }

    /// <summary>点列均匀抽稀（保留首末点），把超大轮廓限制在 maxPoints 以内，避免图形数据膨胀。</summary>
    public static VisionPoint2D[] Subsample(Point[] points, int maxPoints)
    {
        if (points.Length <= maxPoints) return ToPoints(points);
        var stride = (points.Length - 1) / (double)(maxPoints - 1);
        var result = new VisionPoint2D[maxPoints];
        for (var i = 0; i < maxPoints; i++)
        {
            var index = (int)Math.Round(i * stride);
            result[i] = new VisionPoint2D(points[index].X, points[index].Y);
        }
        return result;
    }

    public static VisionPoint2D[] ToPoints(Point[] points) =>
        Array.ConvertAll(points, p => new VisionPoint2D(p.X, p.Y));

    /// <summary>OpenCV 轮廓 → 图形契约。index 为输出序号；parent=-1 表示外轮廓。</summary>
    public static VisionContour ToContour(Point[] points, int index, int parent, bool isHole, int maxPoints)
    {
        var sampled = Subsample(points, maxPoints);
        var area = Cv2.ContourArea(points);
        var perimeter = Cv2.ArcLength(points, closed: true);
        var circularity = perimeter > 1e-9 ? Math.Min(1.0, 4 * Math.PI * area / (perimeter * perimeter)) : 0;
        Cv2.MinEnclosingCircle(points, out var center, out var radius);
        var rect = Cv2.BoundingRect(points);
        return new VisionContour(
            index,
            sampled,
            parent,
            isHole,
            area,
            perimeter,
            circularity,
            new VisionRectangle2D(rect.X, rect.Y, rect.Width, rect.Height),
            new VisionCircle(center.X, center.Y, radius));
    }
}

/// <summary>
/// 5A-1 · contour.extract：从图像提取轮廓（外轮廓 / 含孔洞全部 / 仅孔洞）。
/// 输出 contours（带 ParentIndex 层级）、count、found；无轮廓时 found=false。
/// </summary>
public sealed class ContourExtractNode : IVisionNodeExecutor
{
    public string Type => "contour.extract";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Contour extraction requires a non-empty image.");

        var mode = node.GetString("mode", "external").Trim().ToLowerInvariant();
        var binarize = node.GetString("binarize", "auto").Trim().ToLowerInvariant();
        var threshold = node.GetDouble("threshold", 128);
        var invert = node.GetBool("invert", false);
        var minArea = Math.Max(0, node.GetDouble("minArea", 0));
        var maxCount = Math.Clamp(node.GetInt("maxCount", 200), 1, 2000);
        var maxPoints = Math.Clamp(node.GetInt("maxPoints", 2000), 16, 20000);
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? masked = null;
        Mat? binary = null;
        try
        {
            binary = ContourNodesHelper.Binarize(sourceImage.Mat, binarize, threshold, invert, out var autoGray, out var effectiveBinarize);
            var analysis = binary;
            if (roi is not null)
            {
                using var mask = roi.BuildMask(binary.Size());
                masked = new Mat(binary.Rows, binary.Cols, binary.Type(), Scalar.All(0));
                binary.CopyTo(masked, mask);
                analysis = masked;
            }

            var retrieval = mode switch
            {
                "all" or "ccomp" => RetrievalModes.CComp,
                "holes" or "hole" => RetrievalModes.CComp,
                _ => RetrievalModes.External
            };
            Cv2.FindContours(analysis, out Point[][] contours, out HierarchyIndex[]? hierarchy, retrieval, ContourApproximationModes.ApproxSimple);

            var candidates = new List<(int SourceIndex, int SourceParent, bool IsHole, Point[] Points)>();
            for (var i = 0; i < contours.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = retrieval == RetrievalModes.External ? -1 : hierarchy?[i].Parent ?? -1;
                var isHole = parent >= 0;
                if (mode is "holes" or "hole" && !isHole) continue;
                if (Cv2.ContourArea(contours[i]) < minArea) continue;
                candidates.Add((i, parent, isHole, contours[i]));
            }

            // ParentIndex always addresses the emitted list. If its parent was filtered/truncated
            // (or holes-only omitted the external ring), retain IsHole and use -1 for no emitted parent.
            var selected = candidates.Take(maxCount).ToArray();
            var outputIndexBySource = selected.Select((candidate, index) => (candidate.SourceIndex, index))
                .ToDictionary(x => x.SourceIndex, x => x.index);
            var results = selected.Select((candidate, index) =>
            {
                var parent = outputIndexBySource.TryGetValue(candidate.SourceParent, out var mappedParent) ? mappedParent : -1;
                return ContourNodesHelper.ToContour(candidate.Points, index, parent, candidate.IsHole, maxPoints);
            }).ToList();

            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                ["contours"] = VisionValue.Contours(results),
                ["count"] = VisionValue.Integer(results.Count),
                ["found"] = VisionValue.Boolean(results.Count > 0)
            };
            var overlays = new List<VisionOverlay>();
            for (var i = 0; i < results.Count; i++)
            {
                var contour = results[i];
                overlays.Add(new VisionOverlay(
                    $"contour-{i}",
                    VisionOverlayType.Contour,
                    Label: contour.IsHole ? $"hole #{i} A={contour.Area:0}" : $"#{i} A={contour.Area:0}",
                    Points: contour.Points.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray(),
                    Stroke: contour.IsHole ? "#ff4d4f" : "#52c41a",
                    StrokeWidth: 1.5));
            }

            return ValueTask.FromResult(new NodeExecutorResult(
                outputs,
                new Dictionary<string, object?>
                {
                    ["found"] = results.Count > 0,
                    ["count"] = results.Count,
                    ["holes"] = results.Count(x => x.IsHole),
                    ["mode"] = mode,
                    ["binarize"] = effectiveBinarize,
                    ["threshold"] = binarize == "fixed" ? threshold : null,
                    ["invert"] = invert,
                    ["minArea"] = minArea,
                    ["autoGray"] = autoGray,
                    ["roi"] = roi?.Type.ToString() ?? "FullImage",
                    ["pointBudget"] = maxPoints
                },
                overlays));
        }
        finally
        {
            binary?.Dispose();
            masked?.Dispose();
        }
    }
}

/// <summary>
/// 5A-2 · contour.filter：按面积/圆度/位置窗口/层级筛选轮廓列表。
/// 与 measure.blobs 的内置筛选分工：本节点只筛选，不提取；输入必须是轮廓列表。
/// </summary>
public sealed class ContourFilterNode : IVisionNodeExecutor
{
    public string Type => "contour.filter";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contours = context.Require<IReadOnlyList<VisionContour>>("contours");
        var minArea = Math.Max(0, node.GetDouble("minArea", 0));
        var maxArea = Math.Max(0, node.GetDouble("maxArea", 0));
        var minCircularity = Math.Clamp(node.GetDouble("minCircularity", 0), 0, 1);
        var maxCircularity = Math.Clamp(node.GetDouble("maxCircularity", 1), 0, 1);
        var hierarchy = node.GetString("hierarchy", "any").Trim().ToLowerInvariant();
        var windowX = node.GetDouble("windowX", -1);
        var windowY = node.GetDouble("windowY", -1);
        var windowWidth = Math.Max(0, node.GetDouble("windowWidth", 0));
        var windowHeight = Math.Max(0, node.GetDouble("windowHeight", 0));
        var maxCount = Math.Clamp(node.GetInt("maxCount", 200), 1, 2000);

        var kept = new List<VisionContour>();
        var dropped = 0;
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keep = contour.Area >= minArea
                && (maxArea <= 0 || contour.Area <= maxArea)
                && contour.Circularity >= minCircularity - 1e-9
                && contour.Circularity <= maxCircularity + 1e-9
                && (hierarchy switch
                {
                    "external" => !contour.IsHole,
                    "holes" => contour.IsHole,
                    _ => true
                });
            if (keep && windowWidth > 0 && windowHeight > 0 && windowX >= 0 && windowY >= 0)
            {
                // 位置窗口：轮廓外接矩形中心须落在窗口内（窗口越界由调用方保证）。
                var cx = contour.BoundingBox.X + contour.BoundingBox.Width / 2d;
                var cy = contour.BoundingBox.Y + contour.BoundingBox.Height / 2d;
                keep = cx >= windowX && cx <= windowX + windowWidth && cy >= windowY && cy <= windowY + windowHeight;
            }
            if (keep && kept.Count < maxCount) kept.Add(contour);
            else dropped++;
        }

        // Resolve parents against the final filtered/truncated set, then assign contiguous output indices.
        var outputIndexBySource = kept.Select((contour, index) => (contour.Index, index))
            .ToDictionary(x => x.Index, x => x.index);
        var reindexed = kept.Select((contour, index) => contour with
        {
            Index = index,
            ParentIndex = outputIndexBySource.TryGetValue(contour.ParentIndex, out var parentIndex) ? parentIndex : -1
        }).ToArray();
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["contours"] = VisionValue.Contours(reindexed),
                ["count"] = VisionValue.Integer(reindexed.Length)
            },
            new Dictionary<string, object?>
            {
                ["inputCount"] = contours.Count,
                ["count"] = reindexed.Length,
                ["dropped"] = dropped,
                ["minArea"] = minArea,
                ["maxArea"] = maxArea <= 0 ? "unlimited" : maxArea,
                ["minCircularity"] = minCircularity,
                ["maxCircularity"] = maxCircularity,
                ["hierarchy"] = hierarchy,
                ["positionWindow"] = windowWidth > 0 && windowHeight > 0 ? $"{windowX},{windowY} {windowWidth}x{windowHeight}" : "disabled"
            }));
    }
}

/// <summary>
/// 5A-3 · contour.properties：计算轮廓派生几何属性，输出结构化属性列表 +
/// DetectionList（复用集合/循环节点）+ 选中轮廓的快捷几何输出。
/// </summary>
public sealed class ContourPropertiesNode : IVisionNodeExecutor
{
    public string Type => "contour.properties";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contours = context.Require<IReadOnlyList<VisionContour>>("contours");
        var selectBy = node.GetString("selectBy", "largest").Trim().ToLowerInvariant();
        var index = Math.Max(0, node.GetInt("index", 0));

        if (contours.Count == 0)
            throw new InvalidOperationException("Contour properties requires at least one contour; the input list is empty.");

        var properties = contours.Select((c, i) => Compute(i, c)).ToArray();

        var selectedIndex = selectBy switch
        {
            "smallest" => IndexOfExtreme(contours, x => x.Area, ascending: true),
            "first" => 0,
            "last" => contours.Count - 1,
            "index" => Math.Clamp(index, 0, contours.Count - 1),
            _ => IndexOfExtreme(contours, x => x.Area, ascending: false)
        };
        var selected = contours[selectedIndex];
        var selectedProperties = properties[selectedIndex];

        var detections = properties.Select((p, i) => new VisionDetection(
            Index: i,
            X: p.BoundingBox.X,
            Y: p.BoundingBox.Y,
            Width: p.BoundingBox.Width,
            Height: p.BoundingBox.Height,
            Area: p.Area,
            Perimeter: p.Perimeter,
            Circularity: p.Circularity,
            Circle: p.MinEnclosingCircle,
            BoundingBox: p.BoundingBox)).ToArray();

        var outputs = new Dictionary<string, VisionValue>
        {
            ["properties"] = VisionValue.ContourProperties(properties),
            ["detections"] = VisionValue.Detections(detections),
            ["count"] = VisionValue.Integer(properties.Length),
            ["selected"] = VisionValue.Integer(selectedProperties.Index),
            ["center"] = VisionValue.Point(selectedProperties.Centroid),
            ["circle"] = VisionValue.Circle(selectedProperties.MinEnclosingCircle),
            ["rectangle"] = VisionValue.Rectangle(selectedProperties.MinAreaRect),
            ["pose"] = VisionValue.Pose(new VisionPose2D(selectedProperties.Centroid.X, selectedProperties.Centroid.Y, selectedProperties.MinAreaRect.AngleDeg)),
            ["area"] = VisionValue.Double(selectedProperties.Area),
            ["circularity"] = VisionValue.Double(selectedProperties.Circularity)
        };
        var overlays = new[]
        {
            new VisionOverlay("properties-center", VisionOverlayType.Point, X: selectedProperties.Centroid.X, Y: selectedProperties.Centroid.Y, Stroke: "#faad14", StrokeWidth: 3),
            new VisionOverlay("properties-rect", VisionOverlayType.Rectangle,
                Label: $"A={selectedProperties.Area:0} C={selectedProperties.Circularity:0.###}",
                X: selectedProperties.MinAreaRect.X, Y: selectedProperties.MinAreaRect.Y,
                Width: selectedProperties.MinAreaRect.Width, Height: selectedProperties.MinAreaRect.Height,
                AngleDeg: selectedProperties.MinAreaRect.AngleDeg, Stroke: "#faad14", StrokeWidth: 1.5)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            outputs,
            new Dictionary<string, object?>
            {
                ["count"] = properties.Length,
                ["selectBy"] = selectBy,
                ["selectedIndex"] = selectedProperties.Index,
                ["selectedArea"] = Math.Round(selectedProperties.Area, 2),
                ["selectedCircularity"] = Math.Round(selectedProperties.Circularity, 4),
                ["selectedCentroid"] = $"{selectedProperties.Centroid.X:0.##},{selectedProperties.Centroid.Y:0.##}",
                ["selectedSolidity"] = Math.Round(selectedProperties.Solidity, 4),
                ["selectedAspectRatio"] = Math.Round(selectedProperties.AspectRatio, 4)
            },
            overlays));
    }

    private static int IndexOfExtreme(IReadOnlyList<VisionContour> contours, Func<VisionContour, double> key, bool ascending)
    {
        var best = 0;
        for (var i = 1; i < contours.Count; i++)
        {
            var compare = key(contours[i]).CompareTo(key(contours[best]));
            if (ascending ? compare < 0 : compare > 0) best = i;
        }
        return best;
    }

    private static VisionContourProperties Compute(int index, VisionContour contour)
    {
        var moments = Cv2.Moments(contour.Points.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray(), binaryImage: false);
        var centroid = moments.M00 > 1e-9
            ? new VisionPoint2D(moments.M10 / moments.M00, moments.M01 / moments.M00)
            : new VisionPoint2D(contour.BoundingBox.X + contour.BoundingBox.Width / 2d, contour.BoundingBox.Y + contour.BoundingBox.Height / 2d);

        var points = contour.Points.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        var minAreaRect = Cv2.MinAreaRect(points);
        var rotated = new VisionRectangle2D(minAreaRect.Center.X, minAreaRect.Center.Y, minAreaRect.Size.Width, minAreaRect.Size.Height, minAreaRect.Angle);

        var hull = Cv2.ConvexHull(contour.Points.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray());
        var hullArea = Cv2.ContourArea(hull);
        var solidity = hullArea > 1e-9 ? Math.Min(1.0, contour.Area / hullArea) : 0;
        var boxArea = Math.Max(1e-9, contour.BoundingBox.Width * contour.BoundingBox.Height);
        var extent = Math.Min(1.0, contour.Area / boxArea);
        var longSide = Math.Max(minAreaRect.Size.Width, minAreaRect.Size.Height);
        var shortSide = Math.Max(1e-9, Math.Min(minAreaRect.Size.Width, minAreaRect.Size.Height));
        var equivalentDiameter = Math.Sqrt(Math.Max(0, 4 * contour.Area / Math.PI));

        return new VisionContourProperties(
            index,
            contour.Points.Count,
            contour.Area,
            contour.Perimeter,
            contour.Circularity,
            centroid,
            contour.BoundingBox,
            contour.MinEnclosingCircle,
            rotated,
            solidity,
            extent,
            longSide / shortSide,
            equivalentDiameter);
    }
}
