using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>缺陷分析六个节点的共用部分（2026-10-28）：检测列表读取与画布尺寸推断。</summary>
internal static class DefectAnalysisHelpers
{
    internal static VisionDetection[] ReadDetections(NodeExecutionContext context, string port, string nodeType)
    {
        var detections = context.Require<IReadOnlyList<VisionDetection>>(port);
        if (detections.Count > 10000)
            throw new InvalidOperationException($"{nodeType} {port} budget exceeded (max 10000 detections).");
        foreach (var detection in detections)
        {
            if (!double.IsFinite(detection.X) || !double.IsFinite(detection.Y)
                || !double.IsFinite(detection.Width) || !double.IsFinite(detection.Height)
                || !double.IsFinite(detection.Area))
                throw new InvalidOperationException($"{nodeType} {port} contains a non-finite geometry value.");
        }
        return detections.ToArray();
    }

    /// <summary>画布尺寸：显式参数优先，其次取图像端口，最后按检测包围盒推断。</summary>
    internal static (int Width, int Height) ResolveCanvas(NodeDefinition node, VisionImage? image, VisionDetection[] detections)
    {
        var width = node.GetInt("canvasWidth", 0);
        var height = node.GetInt("canvasHeight", 0);
        if (width > 0 && height > 0) return (width, height);
        if (image is not null && !image.Mat.Empty()) return (image.Width, image.Height);
        if (detections.Length == 0) return (Math.Max(1, width), Math.Max(1, height));
        var minX = detections.Min(d => d.BoundingBox.X);
        var minY = detections.Min(d => d.BoundingBox.Y);
        var maxX = detections.Max(d => d.BoundingBox.X + d.BoundingBox.Width);
        var maxY = detections.Max(d => d.BoundingBox.Y + d.BoundingBox.Height);
        return (Math.Max(1, Math.Max(width, (int)Math.Ceiling(maxX - Math.Min(0, minX)))),
                Math.Max(1, Math.Max(height, (int)Math.Ceiling(maxY - Math.Min(0, minY)))));
    }

    internal static VisionDetection Reindex(VisionDetection detection, int index)
        => detection with { Index = index };

    internal static bool RegionContains(VisionRegion region, double x, double y)
    {
        var column = (int)Math.Round(x);
        var row = (int)Math.Round(y);
        if (column < 0 || row < 0 || column >= region.Width || row >= region.Height) return false;
        foreach (var component in region.Components)
        foreach (var run in component.Runs)
        {
            if (run.Row != row) continue;
            if (column >= run.StartColumn && column <= run.EndColumn) return true;
        }
        return false;
    }

    /// <summary>把方向折叠到 0..180：10 度和 190 度是同一道划痕。</summary>
    internal static double FoldAngle(double degrees)
    {
        var value = degrees % 180.0;
        if (value < 0) value += 180.0;
        return value;
    }
}

/// <summary>
/// 误检抑制（defect.filter）：按面积/长宽比/圆度/对比度/边缘余量逐条判定，
/// 并按原因分桶统计——调参时要知道是哪条规则吃掉了缺陷。
/// </summary>
public sealed class DefectFilterNode : IVisionNodeExecutor
{
    public string Type => "defect.filter";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var detections = DefectAnalysisHelpers.ReadDetections(context, "detections", Type);
        var image = context.TryGetValue("image", out _) ? context.RequireImage("image") : null;
        var region = context.TryGetValue("region", out _) ? context.Require<VisionRegion>("region") : null;

        var minArea = node.GetDouble("minArea", 3);
        var maxArea = node.GetDouble("maxArea", 0);
        var maxAspect = node.GetDouble("maxAspectRatio", 0);
        var minCircularity = node.GetDouble("minCircularity", 0);
        var minContrast = node.GetDouble("minContrast", 0);
        var edgeMargin = node.GetDouble("edgeMarginPx", 0);
        var maxCount = node.GetInt("maxCount", 0);
        if (!double.IsFinite(minArea) || minArea < 0) throw new InvalidOperationException("defect.filter minArea must be >= 0.");
        if (!double.IsFinite(maxArea) || maxArea < 0) throw new InvalidOperationException("defect.filter maxArea must be >= 0 (0 disables).");
        if (!double.IsFinite(maxAspect) || maxAspect < 0) throw new InvalidOperationException("defect.filter maxAspectRatio must be >= 0 (0 disables).");
        if (!double.IsFinite(minCircularity) || minCircularity is < 0 or > 1) throw new InvalidOperationException("defect.filter minCircularity must be within 0..1.");
        if (!double.IsFinite(minContrast) || minContrast < 0) throw new InvalidOperationException("defect.filter minContrast must be >= 0 (0 disables).");
        if (!double.IsFinite(edgeMargin) || edgeMargin < 0) throw new InvalidOperationException("defect.filter edgeMarginPx must be >= 0.");
        if (maxCount is < 0 or > 10000) throw new InvalidOperationException("defect.filter maxCount must be within 0..10000 (0 = all).");
        if (minContrast > 0 && image is null)
            throw new InvalidOperationException("defect.filter minContrast requires the image port; connect the inspected image or set minContrast to 0.");

        var (canvasWidth, canvasHeight) = DefectAnalysisHelpers.ResolveCanvas(node, image, detections);
        var removed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["area"] = 0, ["aspect"] = 0, ["circularity"] = 0, ["contrast"] = 0, ["edge"] = 0, ["region"] = 0, ["count"] = 0
        };
        var kept = new List<VisionDetection>(detections.Length);

        foreach (var detection in detections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (detection.Area < minArea || maxArea > 0 && detection.Area > maxArea) { removed["area"]++; continue; }

            var longSide = Math.Max(detection.Width, detection.Height);
            var shortSide = Math.Min(detection.Width, detection.Height);
            if (maxAspect > 0 && shortSide > 1e-9 && longSide / shortSide > maxAspect) { removed["aspect"]++; continue; }
            if (minCircularity > 0 && detection.Circularity < minCircularity) { removed["circularity"]++; continue; }

            var centreX = detection.BoundingBox.X + detection.BoundingBox.Width / 2;
            var centreY = detection.BoundingBox.Y + detection.BoundingBox.Height / 2;

            if (region is not null && !DefectAnalysisHelpers.RegionContains(region, centreX, centreY)) { removed["region"]++; continue; }

            if (edgeMargin > 0)
            {
                var nearEdge = centreX < edgeMargin || centreY < edgeMargin
                    || centreX > canvasWidth - edgeMargin || centreY > canvasHeight - edgeMargin;
                if (nearEdge) { removed["edge"]++; continue; }
            }

            if (minContrast > 0 && image is not null)
            {
                var contrast = LocalContrast(image.Mat, detection);
                if (contrast < minContrast) { removed["contrast"]++; continue; }
            }

            kept.Add(detection);
        }

        if (maxCount > 0 && kept.Count > maxCount)
        {
            removed["count"] += kept.Count - maxCount;
            kept = kept.OrderByDescending(item => item.Area).Take(maxCount).ToList();
        }

        var result = kept.Select((item, index) => DefectAnalysisHelpers.Reindex(item, index)).ToArray();
        var removedTotal = detections.Length - result.Length;
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("inputCount", detections.Length), ("count", result.Length), ("removedCount", removedTotal),
            ("removedBy", string.Join(",", removed.Where(pair => pair.Value > 0).Select(pair => $"{pair.Key}={pair.Value}"))),
            ("minArea", minArea), ("maxArea", maxArea), ("maxAspectRatio", maxAspect), ("minCircularity", minCircularity),
            ("minContrast", minContrast), ("edgeMarginPx", edgeMargin), ("canvas", $"{canvasWidth}x{canvasHeight}"),
            ("regionGate", region is not null), ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["detections"] = VisionValue.Detections(result),
                ["count"] = VisionValue.Integer(result.Length),
                ["inputCount"] = VisionValue.Integer(detections.Length),
                ["removedCount"] = VisionValue.Integer(removedTotal),
                ["removedByJson"] = VisionValue.String(JsonSerializer.Serialize(removed)),
                ["found"] = VisionValue.Boolean(result.Length > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputCount"] = detections.Length, ["count"] = result.Length, ["removedCount"] = removedTotal,
                ["removedBy"] = removed, ["canvas"] = $"{canvasWidth}x{canvasHeight}",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    private static double LocalContrast(Mat image, VisionDetection detection)
    {
        var x = (int)Math.Floor(detection.BoundingBox.X);
        var y = (int)Math.Floor(detection.BoundingBox.Y);
        var width = (int)Math.Ceiling(detection.BoundingBox.Width);
        var height = (int)Math.Ceiling(detection.BoundingBox.Height);
        x = Math.Clamp(x, 0, Math.Max(0, image.Cols - 1));
        y = Math.Clamp(y, 0, Math.Max(0, image.Rows - 1));
        width = Math.Clamp(width, 0, image.Cols - x);
        height = Math.Clamp(height, 0, image.Rows - y);
        if (width < 1 || height < 1) return 0;
        using var roi = new Mat(image, new Rect(x, y, width, height));
        Cv2.MinMaxLoc(roi, out var min, out var max, out _, out _);
        return max - min;
    }
}

/// <summary>
/// 缺陷几何测量（defect.geometry）：长度/宽度/方向/细长比。
/// 有轮廓时用最小外接矩形（精确），没有时退回包围盒并在诊断里写明。
/// </summary>
public sealed class DefectGeometryNode : IVisionNodeExecutor
{
    public string Type => "defect.geometry";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var detections = DefectAnalysisHelpers.ReadDetections(context, "detections", Type);
        var contours = context.TryGetValue("contours", out _)
            ? context.Require<IReadOnlyList<VisionContour>>("contours")
            : null;

        var maxDefects = node.GetInt("maxDefects", 200);
        if (maxDefects is < 1 or > 10000) throw new InvalidOperationException("defect.geometry maxDefects must be within 1..10000.");
        var elongationThreshold = node.GetDouble("elongationThreshold", 3);
        if (!double.IsFinite(elongationThreshold) || elongationThreshold < 1)
            throw new InvalidOperationException("defect.geometry elongationThreshold must be >= 1.");
        var angleMode = (node.GetString("angleMode", "length-weighted") ?? "length-weighted").Trim().ToLowerInvariant();
        if (angleMode is not ("length-weighted" or "mean"))
            throw new InvalidOperationException("defect.geometry angleMode must be length-weighted or mean.");
        var offset = node.GetInt("contourOffset", 0);
        if (offset < 0) throw new InvalidOperationException("defect.geometry contourOffset must be >= 0.");

        var useContours = contours is not null && contours.Count >= detections.Length + offset;
        var entries = new List<object>();
        double maxLength = 0, maxWidth = 0, totalArea = 0;
        var elongated = 0;
        double angleWeight = 0, angleSum = 0, plainAngleSum = 0, meanCos = 0, meanSin = 0;
        var plainCount = 0;

        for (var index = 0; index < detections.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detection = detections[index];
            double length, width, angle;
            string source;
            if (useContours && contours![offset + index].Points.Count >= 3)
            {
                var points = contours[offset + index].Points.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
                var rect = Cv2.MinAreaRect(points);
                var first = rect.Size.Width;
                var second = rect.Size.Height;
                length = Math.Max(first, second);
                width = Math.Min(first, second);
                // MinAreaRect 的 Angle 是"宽边与 x 轴"的夹角，长边方向要按哪个边更长换算。
                angle = DefectAnalysisHelpers.FoldAngle(first >= second ? rect.Angle : rect.Angle + 90.0);
                source = "contour";
            }
            else
            {
                length = Math.Max(detection.Width, detection.Height);
                width = Math.Min(detection.Width, detection.Height);
                angle = DefectAnalysisHelpers.FoldAngle(detection.BoundingBox.AngleDeg);
                source = "bbox";
            }

            var elongation = width > 1e-9 ? length / width : length > 0 ? double.MaxValue : 0;
            if (length > maxLength) maxLength = length;
            if (width > maxWidth) maxWidth = width;
            totalArea += detection.Area;
            if (elongation >= elongationThreshold)
            {
                elongated++;
                // 角度是轴向量：加权前先按 2θ 展开，否则 179 度和 1 度会互相抵消。
                var radians = angle * 2 * Math.PI / 180.0;
                if (angleMode == "mean")
                {
                    meanCos += Math.Cos(radians);
                    meanSin += Math.Sin(radians);
                }
                else
                {
                    angleWeight += length;
                    angleSum += Math.Cos(radians) * length;
                    plainAngleSum += Math.Sin(radians) * length;
                }
                plainCount++;
            }

            if (index < maxDefects)
            {
                entries.Add(new
                {
                    index = detection.Index,
                    x = Math.Round(detection.X, 3),
                    y = Math.Round(detection.Y, 3),
                    length = Math.Round(length, 3),
                    width = Math.Round(width, 3),
                    angleDeg = Math.Round(angle, 2),
                    elongation = Math.Round(Math.Min(elongation, 999999), 3),
                    area = Math.Round(detection.Area, 2),
                    fill = Math.Round(detection.Width * detection.Height > 1e-9 ? detection.Area / (detection.Width * detection.Height) : 0, 4),
                    classId = detection.ClassId,
                    source
                });
            }
        }

        var dominantAngle = 0d;
        if (elongated > 0)
        {
            var meanAngle = angleMode == "mean"
                ? Math.Atan2(meanSin, meanCos)
                : angleWeight > 1e-9 ? Math.Atan2(plainAngleSum, angleSum) : double.NaN;
            if (double.IsFinite(meanAngle))
                dominantAngle = DefectAnalysisHelpers.FoldAngle(meanAngle * 90.0 / Math.PI);
        }

        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", detections.Length), ("elongatedCount", elongated),
            ("geometrySource", useContours ? "contour" : "bbox"), ("maxDefects", maxDefects),
            ("elongationThreshold", elongationThreshold), ("angleMode", angleMode),
            ("maxLength", maxLength), ("maxWidth", maxWidth), ("dominantAngleDeg", dominantAngle),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["geometryJson"] = VisionValue.String(JsonSerializer.Serialize(entries)),
                ["count"] = VisionValue.Integer(detections.Length),
                ["maxLength"] = VisionValue.Double(maxLength),
                ["maxWidth"] = VisionValue.Double(maxWidth),
                ["dominantAngleDeg"] = VisionValue.Double(dominantAngle),
                ["elongatedCount"] = VisionValue.Integer(elongated),
                ["totalArea"] = VisionValue.Double(totalArea),
                ["found"] = VisionValue.Boolean(detections.Length > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = detections.Length, ["elongatedCount"] = elongated,
                ["geometrySource"] = useContours ? "contour" : "bbox",
                ["maxLength"] = Math.Round(maxLength, 3), ["maxWidth"] = Math.Round(maxWidth, 3),
                ["dominantAngleDeg"] = Math.Round(dominantAngle, 2),
                ["totalArea"] = Math.Round(totalArea, 2),
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = useContours ? "opencv" : "managed", ["diagnostics"] = diagnostics
            }));
    }
}
