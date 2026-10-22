using VisionStudio.Engine.Algorithms;
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
    public static Mat Binarize(Mat source, string mode, double threshold, bool invert, out bool autoGray, out string effectiveMode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mode = mode.Trim().ToLowerInvariant();
        if (mode is not ("auto" or "otsu" or "fixed" or "direct"))
            throw new InvalidOperationException("Contour binarization must be auto, fixed or direct.");
        if (mode == "fixed" && (!double.IsFinite(threshold) || threshold is < 0 or > 255))
            throw new InvalidOperationException("Fixed contour threshold must be finite in 0..255.");
        effectiveMode = mode is "auto" or "otsu" ? "otsu" : mode;
        var gray = PreprocessImageHelpers.ToGray8(source, out autoGray, out var owned);
        try
        {
            return ImageTransformOutput.Create(binary =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mode is "auto" or "otsu")
                {
                    JLVisionOperators.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                    if (invert) JLVisionOperators.BitwiseNot(binary, binary);
                }
                else
                {
                    JLVisionOperators.Threshold(gray, binary, mode == "fixed" ? threshold : 0, 255,
                        invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
                }
                cancellationToken.ThrowIfCancellationRequested();
            });
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public static IReadOnlyList<VisionContour> ReadContours(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var contours = context.Require<IReadOnlyList<VisionContour>>("contours");
        if (contours.Count > 10000) throw new InvalidOperationException("Contour list exceeds 10000 contours.");
        var indices = new HashSet<int>();
        long totalPoints = 0;
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contour?.Points is null) throw new InvalidOperationException("Contour and its points must be non-null.");
            if (contour.Index < 0 || !indices.Add(contour.Index))
                throw new InvalidOperationException("Contour indices must be unique and nonnegative.");
            if (contour.ParentIndex < -1 || contour.ParentIndex == contour.Index)
                throw new InvalidOperationException("Contour parent index must be -1 or another contour's index.");
            if (!double.IsFinite(contour.Area) || contour.Area < 0 || !double.IsFinite(contour.Perimeter) || contour.Perimeter < 0
                || !double.IsFinite(contour.Circularity) || contour.Circularity is < 0 or > 1)
                throw new InvalidOperationException("Contour area/perimeter must be finite and nonnegative; circularity must be in 0..1.");
            var box = contour.BoundingBox;
            var circle = contour.MinEnclosingCircle;
            if (!double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || box.Width < 0
                || !double.IsFinite(box.Height) || box.Height < 0 || !double.IsFinite(box.AngleDeg)
                || !double.IsFinite(circle.X) || !double.IsFinite(circle.Y) || !double.IsFinite(circle.Radius) || circle.Radius < 0)
                throw new InvalidOperationException("Contour bounding box and enclosing circle must have finite coordinates and nonnegative sizes.");
            totalPoints += contour.Points.Count;
            if (totalPoints > 2_000_000) throw new InvalidOperationException("Contour list exceeds 2000000 points.");
            for (var i = 0; i < contour.Points.Count; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var point = contour.Points[i];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    throw new InvalidOperationException("Contour point coordinates must be finite.");
            }
        }
        return contours;
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
        var area = JLVisionOperators.ContourArea(points);
        var perimeter = JLVisionOperators.ArcLength(points, closed: true);
        var circularity = perimeter > 1e-9 ? Math.Min(1.0, 4 * Math.PI * area / (perimeter * perimeter)) : 0;
        JLVisionOperators.MinEnclosingCircle(points, out var center, out var radius);
        var rect = JLVisionOperators.BoundingRect(points);
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

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Contour extraction requires a non-empty image.");

        var mode = node.GetString("mode", "external").Trim().ToLowerInvariant();
        if (mode is not ("external" or "all" or "ccomp" or "holes" or "hole"))
            throw new InvalidOperationException("contour.extract retrieval must be external, all or holes.");
        var binarize = node.GetString("binarize", "auto").Trim().ToLowerInvariant();
        var threshold = binarize == "fixed" ? node.GetDouble("threshold", 128) : 0;
        var invert = node.GetBool("invert", false);
        var minArea = node.GetDouble("minArea", 0);
        if (!double.IsFinite(minArea) || minArea < 0)
            throw new InvalidOperationException("contour.extract minArea must be finite and nonnegative.");
        var maxCount = node.GetInt("maxCount", 200);
        var maxPoints = node.GetInt("maxPoints", 2000);
        if (maxCount is < 1 or > 2000 || maxPoints is < 16 or > 20000)
            throw new InvalidOperationException("contour.extract maxCount must be 1..2000 and maxPoints must be 16..20000.");
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? masked = null;
        Mat? binary = null;
        try
        {
            binary = ContourNodesHelper.Binarize(sourceImage.Mat, binarize, threshold, invert, out var autoGray, out var effectiveBinarize, cancellationToken);
            var analysis = binary;
            if (roi is not null)
            {
                using var mask = roi.BuildMask(binary.Size(), cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
            JLVisionOperators.FindContours(analysis, out Point[][] contours, out HierarchyIndex[]? hierarchy, retrieval, ContourApproximationModes.ApproxSimple);

            var candidates = new List<(int SourceIndex, int SourceParent, bool IsHole, Point[] Points)>();
            for (var i = 0; i < contours.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = retrieval == RetrievalModes.External ? -1 : hierarchy?[i].Parent ?? -1;
                var isHole = parent >= 0;
                if (mode is "holes" or "hole" && !isHole) continue;
                if (JLVisionOperators.ContourArea(contours[i]) < minArea) continue;
                candidates.Add((i, parent, isHole, contours[i]));
            }

            // ParentIndex always addresses the emitted list. If its parent was filtered/truncated
            // (or holes-only omitted the external ring), retain IsHole and use -1 for no emitted parent.
            var selected = candidates.Take(maxCount).ToArray();
            var outputIndexBySource = selected.Select((candidate, index) => (candidate.SourceIndex, index))
                .ToDictionary(x => x.SourceIndex, x => x.index);
            var results = selected.Select((candidate, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = outputIndexBySource.TryGetValue(candidate.SourceParent, out var mappedParent) ? mappedParent : -1;
                return ContourNodesHelper.ToContour(candidate.Points, index, parent, candidate.IsHole, maxPoints);
            }).ToList();

            var diag = CoordinateValidation.Diagnostics(Type,
                ("found", results.Count > 0), ("count", results.Count), ("holes", results.Count(x => x.IsHole)),
                ("mode", mode), ("binarize", effectiveBinarize), ("minArea", minArea),
                ("maxCount", maxCount), ("pointBudget", maxPoints), ("roi", roi?.Type.ToString() ?? "FullImage"),
                ("truncated", candidates.Count > maxCount));
            var overlays = new List<VisionOverlay>();
            for (var i = 0; i < results.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var contour = results[i];
                overlays.Add(new VisionOverlay(
                    $"contour-{i}",
                    VisionOverlayType.Contour,
                    Label: contour.IsHole ? $"hole #{i} A={contour.Area:0}" : $"#{i} A={contour.Area:0}",
                    Points: contour.Points.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray(),
                    Stroke: contour.IsHole ? "#ff4d4f" : "#52c41a",
                    StrokeWidth: 1.5));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                ["contours"] = VisionValue.Contours(results),
                ["count"] = VisionValue.Integer(results.Count),
                ["found"] = VisionValue.Boolean(results.Count > 0),
                ["diagnostics"] = VisionValue.String(diag)
            };
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
                    ["pointBudget"] = maxPoints,
                    ["diagnostics"] = diag
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

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var contours = ContourNodesHelper.ReadContours(context, cancellationToken);
        var minArea = node.GetDouble("minArea", 0);
        var maxArea = node.GetDouble("maxArea", 0);
        var minCircularity = node.GetDouble("minCircularity", 0);
        var maxCircularity = node.GetDouble("maxCircularity", 1);
        var hierarchy = node.GetString("hierarchy", "any").Trim().ToLowerInvariant();
        var windowX = node.GetDouble("windowX", -1);
        var windowY = node.GetDouble("windowY", -1);
        var windowWidth = node.GetDouble("windowWidth", 0);
        var windowHeight = node.GetDouble("windowHeight", 0);
        if (!double.IsFinite(minArea) || minArea < 0 || !double.IsFinite(maxArea) || maxArea < 0)
            throw new InvalidOperationException("contour.filter area bounds must be finite and nonnegative.");
        if (!double.IsFinite(minCircularity) || minCircularity is < 0 or > 1 || !double.IsFinite(maxCircularity) || maxCircularity is < 0 or > 1)
            throw new InvalidOperationException("contour.filter circularity bounds must be finite in 0..1.");
        if (!double.IsFinite(windowX) || !double.IsFinite(windowY) || !double.IsFinite(windowWidth) || windowWidth < 0 || !double.IsFinite(windowHeight) || windowHeight < 0)
            throw new InvalidOperationException("contour.filter window must have finite coordinates and nonnegative dimensions.");
        var windowEnabled = windowWidth > 0 && windowHeight > 0 && windowX >= 0 && windowY >= 0;
        if (windowEnabled && (!double.IsFinite(windowX + windowWidth) || !double.IsFinite(windowY + windowHeight)))
            throw new InvalidOperationException("contour.filter window bounds must be finite.");
        var maxCountRaw = node.GetInt("maxCount", 200);
        if (maxCountRaw is < 1 or > 2000) throw new InvalidOperationException("contour.filter maxCount must be between 1 and 2000.");
        var maxCount = maxCountRaw;
        if (hierarchy is not ("any" or "external" or "holes")) throw new InvalidOperationException("contour.filter hierarchy must be any, external or holes.");
        if (minArea > maxArea && maxArea > 0) throw new InvalidOperationException("contour.filter minArea must be <= maxArea when maxArea is set.");
        if (minCircularity > maxCircularity) throw new InvalidOperationException("contour.filter minCircularity must be <= maxCircularity.");

        var kept = new List<VisionContour>();
        var dropped = 0;
        var limitDropped = 0;
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
            if (keep && windowEnabled)
            {
                // 位置窗口：轮廓外接矩形中心须落在窗口内（窗口越界由调用方保证）。
                var cx = contour.BoundingBox.X + contour.BoundingBox.Width / 2d;
                var cy = contour.BoundingBox.Y + contour.BoundingBox.Height / 2d;
                keep = cx >= windowX && cx <= windowX + windowWidth && cy >= windowY && cy <= windowY + windowHeight;
            }
            if (keep && kept.Count < maxCount) kept.Add(contour);
            else
            {
                dropped++;
                if (keep) limitDropped++;
            }
        }

        // Resolve parents against the final filtered/truncated set, then assign contiguous output indices.
        var outputIndexBySource = kept.Select((contour, index) => (contour.Index, index))
            .ToDictionary(x => x.Index, x => x.index);
        var reindexed = kept.Select((contour, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return contour with
            {
                Index = index,
                ParentIndex = outputIndexBySource.TryGetValue(contour.ParentIndex, out var parentIndex) ? parentIndex : -1
            };
        }).ToArray();
        var truncated = limitDropped > 0;
        var diag = CoordinateValidation.Diagnostics(Type, ("inputCount", contours.Count), ("count", reindexed.Length),
            ("dropped", dropped), ("filteredCount", dropped - limitDropped), ("limitDroppedCount", limitDropped), ("truncated", truncated),
            ("windowEnabled", windowEnabled), ("minArea", minArea), ("maxArea", maxArea), ("hierarchy", hierarchy), ("maxCount", maxCount));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["contours"] = VisionValue.Contours(reindexed),
                ["count"] = VisionValue.Integer(reindexed.Length),
                ["inputCount"] = VisionValue.Integer(contours.Count),
                ["droppedCount"] = VisionValue.Integer(dropped),
                ["truncated"] = VisionValue.Boolean(truncated),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["inputCount"] = contours.Count,
                ["count"] = reindexed.Length,
                ["dropped"] = dropped,
                ["filteredCount"] = dropped - limitDropped,
                ["limitDroppedCount"] = limitDropped,
                ["truncated"] = truncated,
                ["diagnostics"] = diag,
                ["minArea"] = minArea,
                ["maxArea"] = maxArea <= 0 ? "unlimited" : maxArea,
                ["minCircularity"] = minCircularity,
                ["maxCircularity"] = maxCircularity,
                ["hierarchy"] = hierarchy,
                ["positionWindow"] = windowEnabled ? $"{windowX},{windowY} {windowWidth}x{windowHeight}" : "disabled"
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

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var contours = ContourNodesHelper.ReadContours(context, cancellationToken);
        var selectBy = node.GetString("selectBy", "largest").Trim().ToLowerInvariant();
        var index = selectBy == "index" ? node.GetInt("index", 0) : 0;
        if (selectBy is not ("largest" or "smallest" or "first" or "last" or "index"))
            throw new InvalidOperationException("contour.properties selectBy must be largest, smallest, first, last or index.");
        if (selectBy == "index" && index < 0)
            throw new InvalidOperationException("contour.properties index must be nonnegative when selectBy=index.");

        if (contours.Count == 0)
            throw new InvalidOperationException("Contour properties requires at least one contour; the input list is empty.");
        if (selectBy == "index" && index >= contours.Count)
            throw new InvalidOperationException($"contour.properties index {index} is out of range for {contours.Count} contour(s).");
        foreach (var c in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (c.Points.Count < 3) throw new InvalidOperationException("contour.properties requires every contour to have at least 3 points.");
        }

        var properties = contours.Select((c, i) => Compute(i, c, cancellationToken)).ToArray();

        var selectedIndex = selectBy switch
        {
            "smallest" => IndexOfExtreme(contours, x => x.Area, ascending: true, cancellationToken),
            "first" => 0,
            "last" => contours.Count - 1,
            "index" => index,
            _ => IndexOfExtreme(contours, x => x.Area, ascending: false, cancellationToken)
        };
        var selectedProperties = properties[selectedIndex];

        var detections = properties.Select((p, i) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new VisionDetection(
                Index: i,
                X: p.BoundingBox.X,
                Y: p.BoundingBox.Y,
                Width: p.BoundingBox.Width,
                Height: p.BoundingBox.Height,
                Area: p.Area,
                Perimeter: p.Perimeter,
                Circularity: p.Circularity,
                Circle: p.MinEnclosingCircle,
                BoundingBox: p.BoundingBox);
        }).ToArray();

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
            ["circularity"] = VisionValue.Double(selectedProperties.Circularity),
            ["perimeter"] = VisionValue.Double(selectedProperties.Perimeter),
            ["solidity"] = VisionValue.Double(selectedProperties.Solidity),
            ["extent"] = VisionValue.Double(selectedProperties.Extent),
            ["aspectRatio"] = VisionValue.Double(selectedProperties.AspectRatio),
            ["equivalentDiameter"] = VisionValue.Double(selectedProperties.EquivalentDiameter)
        };
        var diag = CoordinateValidation.Diagnostics(Type,
            ("count", properties.Length), ("selectBy", selectBy), ("selectedIndex", selectedProperties.Index),
            ("area", selectedProperties.Area), ("perimeter", selectedProperties.Perimeter),
            ("circularity", selectedProperties.Circularity), ("solidity", selectedProperties.Solidity),
            ("extent", selectedProperties.Extent), ("aspectRatio", selectedProperties.AspectRatio),
            ("equivalentDiameter", selectedProperties.EquivalentDiameter),
            ("pointCount", selectedProperties.PointCount));
        outputs["diagnostics"] = VisionValue.String(diag);
        var overlays = new[]
        {
            new VisionOverlay("properties-center", VisionOverlayType.Point, X: selectedProperties.Centroid.X, Y: selectedProperties.Centroid.Y, Stroke: "#faad14", StrokeWidth: 3),
            new VisionOverlay("properties-rect", VisionOverlayType.Rectangle,
                Label: $"A={selectedProperties.Area:0} C={selectedProperties.Circularity:0.###} S={selectedProperties.Solidity:0.##}",
                X: selectedProperties.MinAreaRect.X, Y: selectedProperties.MinAreaRect.Y,
                Width: selectedProperties.MinAreaRect.Width, Height: selectedProperties.MinAreaRect.Height,
                AngleDeg: selectedProperties.MinAreaRect.AngleDeg, Stroke: "#faad14", StrokeWidth: 1.5),
            new VisionOverlay("properties-circle", VisionOverlayType.Circle,
                X: selectedProperties.MinEnclosingCircle.X, Y: selectedProperties.MinEnclosingCircle.Y,
                Radius: selectedProperties.MinEnclosingCircle.Radius, Stroke: "#13c2c2", StrokeWidth: 1)
        };

        cancellationToken.ThrowIfCancellationRequested();
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
                ["selectedAspectRatio"] = Math.Round(selectedProperties.AspectRatio, 4),
                ["selectedExtent"] = Math.Round(selectedProperties.Extent, 4),
                ["selectedEquivalentDiameter"] = Math.Round(selectedProperties.EquivalentDiameter, 4),
                ["diagnostics"] = diag
            },
            overlays));
    }

    private static int IndexOfExtreme(IReadOnlyList<VisionContour> contours, Func<VisionContour, double> key, bool ascending, CancellationToken cancellationToken)
    {
        var best = 0;
        for (var i = 1; i < contours.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compare = key(contours[i]).CompareTo(key(contours[best]));
            if (ascending ? compare < 0 : compare > 0) best = i;
        }
        return best;
    }

    private static VisionContourProperties Compute(int index, VisionContour contour, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var native = JlContourCodec.ToNative([contour]);
        var centroid = Centroid(contour, cancellationToken);
        native.SmallestRectangle2Xld(out double row, out double column, out double phi, out double length1, out double length2);
        var rotated = new VisionRectangle2D(column, row, length1 * 2, length2 * 2, -phi * 180 / Math.PI);
        using var hull = native.ShapeTransXld("convex");
        hull.GetContourXld(out var hullRows, out var hullColumns);
        double hullArea = 0;
        using (hullRows) using (hullColumns)
        {
            var originX = hullColumns.Length > 0 ? hullColumns[0].D : 0;
            var originY = hullRows.Length > 0 ? hullRows[0].D : 0;
            var compensation = 0d;
            for (var i = 0; i < hullRows.Length; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var j = (i + 1) % hullRows.Length;
                var x = hullColumns[i].D - originX; var y = hullRows[i].D - originY;
                var nextX = hullColumns[j].D - originX; var nextY = hullRows[j].D - originY;
                Add(ref hullArea, ref compensation, Math.FusedMultiplyAdd(x, nextY, -nextX * y));
            }
            hullArea = Math.Abs(hullArea) / 2;
        }
        var solidity = hullArea > 1e-9 ? Math.Min(1.0, contour.Area / hullArea) : 0;
        var boxArea = Math.Max(1e-9, contour.BoundingBox.Width * contour.BoundingBox.Height);
        var extent = Math.Min(1.0, contour.Area / boxArea);
        var longSide = Math.Max(rotated.Width, rotated.Height);
        var shortSide = Math.Max(1e-9, Math.Min(rotated.Width, rotated.Height));
        var equivalentDiameter = 2 * Math.Sqrt(contour.Area / Math.PI);
        var aspectRatio = longSide / shortSide;
        if (new[] { centroid.X, centroid.Y, rotated.X, rotated.Y, rotated.Width, rotated.Height, rotated.AngleDeg,
            hullArea, solidity, extent, aspectRatio, equivalentDiameter }.Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("contour.properties geometry exceeds the finite numeric range; adjust coordinates or units.");
        cancellationToken.ThrowIfCancellationRequested();

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
            aspectRatio,
            equivalentDiameter);
    }

    private static VisionPoint2D Centroid(VisionContour contour, CancellationToken cancellationToken)
    {
        // Translation removes the large common coordinate offset from products.
        // Compensated sums preserve small edges when a contour has many vertices.
        var origin = contour.Points[0];
        var twiceArea = 0d; var areaError = 0d;
        var momentX = 0d; var momentXError = 0d;
        var momentY = 0d; var momentYError = 0d;
        var meanX = 0d; var meanXError = 0d;
        var meanY = 0d; var meanYError = 0d;
        for (var i = 0; i < contour.Points.Count; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var p = contour.Points[i];
            var x = p.X - origin.X; var y = p.Y - origin.Y;
            Add(ref meanX, ref meanXError, x / contour.Points.Count);
            Add(ref meanY, ref meanYError, y / contour.Points.Count);
            if (!contour.Closed) continue;
            var q = contour.Points[(i + 1) % contour.Points.Count];
            var nextX = q.X - origin.X; var nextY = q.Y - origin.Y;
            var cross = Math.FusedMultiplyAdd(x, nextY, -nextX * y);
            Add(ref twiceArea, ref areaError, cross);
            Add(ref momentX, ref momentXError, (x + nextX) * cross);
            Add(ref momentY, ref momentYError, (y + nextY) * cross);
        }
        return contour.Closed && Math.Abs(twiceArea) > 1e-12
            ? new VisionPoint2D(origin.X + momentX / twiceArea / 3, origin.Y + momentY / twiceArea / 3)
            : new VisionPoint2D(origin.X + meanX, origin.Y + meanY);
    }

    private static void Add(ref double sum, ref double compensation, double value)
    {
        var corrected = value - compensation;
        var next = sum + corrected;
        compensation = (next - sum) - corrected;
        sum = next;
    }
}
