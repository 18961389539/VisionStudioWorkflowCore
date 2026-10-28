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
        double angleWeight = 0, angleSum = 0, plainAngleSum = 0;
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
                angleWeight += length;
                angleSum += Math.Cos(radians) * length;
                plainAngleSum += Math.Sin(radians) * length;
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
        if (elongated > 0 && angleWeight > 1e-9)
        {
            var meanAngle = angleMode == "mean" ? Math.Atan2(plainAngleSum, angleSum) : Math.Atan2(plainAngleSum, angleSum);
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

/// <summary>
/// 边缘缺陷（defect.edgeDefect）：沿轮廓的法向偏差 → 超差区段 → 崩边/毛刺缺陷列表。
/// tolerance.profile 回答"边缘是否超差"，本节点回答"缺口在哪、多深"。
/// </summary>
public sealed class EdgeDefectNode : IVisionNodeExecutor
{
    public string Type => "defect.edgeDefect";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var samples = context.Require<IReadOnlyList<VisionPoint2D>>("samples");
        if (samples.Count > 20000)
            throw new InvalidOperationException("defect.edgeDefect sample budget exceeded (max 20000 points).");
        if (samples.Count < 3)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing($"edge defects need at least 3 samples, got {samples.Count}", timer));
        }

        var hasLine = context.TryGetValue("nominalLine", out _);
        var hasCircle = context.TryGetValue("nominalCircle", out _);
        if (hasLine == hasCircle)
            throw new InvalidOperationException("defect.edgeDefect requires exactly one nominal: nominalLine or nominalCircle.");

        var maxDeviation = node.GetDouble("maxDeviation", 0.5);
        if (!double.IsFinite(maxDeviation) || maxDeviation <= 0)
            throw new InvalidOperationException("defect.edgeDefect maxDeviation must be > 0.");
        var polarity = (node.GetString("polarity", "both") ?? "both").Trim().ToLowerInvariant();
        if (polarity is not ("both" or "excess" or "short"))
            throw new InvalidOperationException("defect.edgeDefect polarity must be both, excess or short.");
        var minLength = node.GetDouble("minSegmentLength", 5);
        if (!double.IsFinite(minLength) || minLength < 0)
            throw new InvalidOperationException("defect.edgeDefect minSegmentLength must be >= 0.");
        var mergeGap = node.GetDouble("mergeGapPx", 2);
        if (!double.IsFinite(mergeGap) || mergeGap < 0)
            throw new InvalidOperationException("defect.edgeDefect mergeGapPx must be >= 0.");

        var deviations = new double[samples.Count];
        var arcLengths = new double[samples.Count];
        if (hasLine)
        {
            var line = context.Require<VisionLine2D>("nominalLine");
            var dx = line.End.X - line.Start.X;
            var dy = line.End.Y - line.Start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) throw new InvalidOperationException("defect.edgeDefect nominalLine must have non-zero length.");
            var ux = dx / length;
            var uy = dy / length;
            for (var index = 0; index < samples.Count; index++)
            {
                var rx = samples[index].X - line.Start.X;
                var ry = samples[index].Y - line.Start.Y;
                deviations[index] = ry * ux - rx * uy;
            }
        }
        else
        {
            var circle = context.Require<VisionCircle>("nominalCircle");
            if (circle.Radius <= 0) throw new InvalidOperationException("defect.edgeDefect nominalCircle radius must be positive.");
            for (var index = 0; index < samples.Count; index++)
            {
                var dx = samples[index].X - circle.X;
                var dy = samples[index].Y - circle.Y;
                deviations[index] = Math.Sqrt(dx * dx + dy * dy) - circle.Radius;
            }
        }

        for (var index = 1; index < samples.Count; index++)
        {
            var dx = samples[index].X - samples[index - 1].X;
            var dy = samples[index].Y - samples[index - 1].Y;
            arcLengths[index] = Math.Sqrt(dx * dx + dy * dy);
        }

        bool Over(double deviation) => polarity switch
        {
            "excess" => deviation > maxDeviation,
            "short" => deviation < -maxDeviation,
            _ => Math.Abs(deviation) > maxDeviation
        };

        var segments = new List<(int Start, int End)>();
        int? runStart = null;
        for (var index = 0; index < samples.Count; index++)
        {
            if (Over(deviations[index]))
            {
                runStart ??= index;
                continue;
            }
            if (runStart is not null)
            {
                segments.Add((runStart.Value, index - 1));
                runStart = null;
            }
        }
        if (runStart is not null) segments.Add((runStart.Value, samples.Count - 1));

        // 同一处崩边中间常夹着一个回到公差内的毛刺，按弧长间隔合并回一段。
        var merged = new List<(int Start, int End)>();
        foreach (var segment in segments)
        {
            if (merged.Count == 0) { merged.Add(segment); continue; }
            var previous = merged[^1];
            var gap = 0d;
            for (var index = previous.End + 1; index <= segment.Start; index++) gap += arcLengths[index];
            if (gap <= mergeGap) merged[^1] = (previous.Start, segment.End);
            else merged.Add(segment);
        }

        var defects = new List<VisionDetection>();
        var segmentInfos = new List<object>();
        double maxDepth = 0, totalLength = 0;
        foreach (var segment in merged)
        {
            double length = 0, depth = 0;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (var index = segment.Start; index <= segment.End; index++)
            {
                length += arcLengths[index];
                if (Math.Abs(deviations[index]) > Math.Abs(depth)) depth = deviations[index];
                minX = Math.Min(minX, samples[index].X);
                minY = Math.Min(minY, samples[index].Y);
                maxX = Math.Max(maxX, samples[index].X);
                maxY = Math.Max(maxY, samples[index].Y);
            }
            if (length < minLength) continue;

            var width = Math.Max(1, maxX - minX);
            var height = Math.Max(1, maxY - minY);
            var box = new VisionRectangle2D(minX, minY, width, height);
            var centreX = (minX + maxX) / 2;
            var centreY = (minY + maxY) / 2;
            defects.Add(new VisionDetection(defects.Count, centreX, centreY, width, height, length, length, 0,
                new VisionCircle(centreX, centreY, Math.Max(width, height) / 2), box));
            maxDepth = Math.Max(maxDepth, Math.Abs(depth));
            totalLength += length;
            segmentInfos.Add(new
            {
                index = defects.Count - 1,
                startIndex = segment.Start,
                endIndex = segment.End,
                length = Math.Round(length, 3),
                depth = Math.Round(depth, 4),
                x = Math.Round(centreX, 3),
                y = Math.Round(centreY, 3)
            });
        }

        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", defects.Count), ("samples", samples.Count),
            ("maxDeviation", maxDeviation), ("polarity", polarity),
            ("minSegmentLength", minLength), ("mergeGapPx", mergeGap),
            ("maxDepth", maxDepth), ("totalLength", totalLength),
            ("nominal", hasLine ? "line" : "circle"),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["detections"] = VisionValue.Detections(defects.ToArray()),
                ["count"] = VisionValue.Integer(defects.Count),
                ["maxDepth"] = VisionValue.Double(maxDepth),
                ["totalLength"] = VisionValue.Double(totalLength),
                ["segmentsJson"] = VisionValue.String(JsonSerializer.Serialize(segmentInfos)),
                ["found"] = VisionValue.Boolean(defects.Count > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = defects.Count, ["maxDepth"] = Math.Round(maxDepth, 4),
                ["totalLength"] = Math.Round(totalLength, 3), ["nominal"] = hasLine ? "line" : "circle",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    private static NodeExecutorResult Missing(string reason, Stopwatch timer)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "defect.edgeDefect", ("found", false), ("reason", reason),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["detections"] = VisionValue.Detections([]),
                ["count"] = VisionValue.Integer(0),
                ["found"] = VisionValue.Boolean(false),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            });
    }
}

/// <summary>
/// 缺陷空间分布热力图（defect.heatmap）：网格计数 + 渲染 + 集中度指标。
/// 回答"缺陷是否集中在同一区域"，这是调工艺还是修模的分水岭。
/// </summary>
public sealed class DefectHeatmapNode : IVisionNodeExecutor
{
    public string Type => "defect.heatmap";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var detections = DefectAnalysisHelpers.ReadDetections(context, "detections", Type);
        var cols = node.GetInt("gridCols", 16);
        var rows = node.GetInt("gridRows", 12);
        if (cols is < 2 or > 256 || rows is < 2 or > 256)
            throw new InvalidOperationException("defect.heatmap gridCols/gridRows must be within 2..256.");
        var cellPixels = node.GetInt("cellPixels", 24);
        if (cellPixels is < 4 or > 512) throw new InvalidOperationException("defect.heatmap cellPixels must be within 4..512.");
        var colormap = (node.GetString("colormap", "heat") ?? "heat").Trim().ToLowerInvariant();
        if (colormap is not ("heat" or "gray")) throw new InvalidOperationException("defect.heatmap colormap must be heat or gray.");

        var (canvasWidth, canvasHeight) = DefectAnalysisHelpers.ResolveCanvas(node, null, detections);
        if (canvasWidth < 1 || canvasHeight < 1)
            throw new InvalidOperationException("defect.heatmap needs a positive canvas; set canvasWidth/canvasHeight or feed at least one detection.");

        var counts = new int[rows, cols];
        foreach (var detection in detections.DistinctBy(item => item.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var centreX = detection.BoundingBox.X + detection.BoundingBox.Width / 2;
            var centreY = detection.BoundingBox.Y + detection.BoundingBox.Height / 2;
            var column = (int)(centreX / canvasWidth * cols);
            var row = (int)(centreY / canvasHeight * rows);
            column = Math.Clamp(column, 0, cols - 1);
            row = Math.Clamp(row, 0, rows - 1);
            counts[row, column]++;
        }

        var total = counts.Cast<int>().Sum();
        var flat = counts.Cast<int>().Where(value => value > 0).OrderByDescending(value => value).ToArray();
        var occupied = flat.Length;
        var topThree = flat.Take(3).Sum();
        var concentration = total > 0 ? (double)topThree / total : 0;

        var hotspotRow = 0;
        var hotspotColumn = 0;
        var hotspotCount = 0;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < cols; column++)
        {
            if (counts[row, column] <= hotspotCount) continue;
            hotspotCount = counts[row, column];
            hotspotRow = row;
            hotspotColumn = column;
        }

        var maxCell = Math.Max(1, hotspotCount);
        using var canvas = new Mat(rows * cellPixels, cols * cellPixels, MatType.CV_8UC3, Scalar.All(18));
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < cols; column++)
        {
            var count = counts[row, column];
            var colour = count == 0
                ? (colormap == "gray" ? new Scalar(24, 24, 24) : new Scalar(28, 20, 16))
                : Scale(colormap, (double)count / maxCell);
            var rect = new Rect(column * cellPixels, row * cellPixels, cellPixels - 1, cellPixels - 1);
            Cv2.Rectangle(canvas, rect, colour, -1);
        }

        var hotspotX = (hotspotColumn + 0.5) * canvasWidth / cols;
        var hotspotY = (hotspotRow + 0.5) * canvasHeight / rows;
        var countsJson = JsonSerializer.Serialize(new
        {
            gridCols = cols,
            gridRows = rows,
            canvas = new { width = canvasWidth, height = canvasHeight },
            rows = Enumerable.Range(0, rows).Select(row => Enumerable.Range(0, cols).Select(column => counts[row, column]).ToArray()).ToArray()
        });
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", detections.Length), ("occupiedCells", occupied), ("totalCells", rows * cols),
            ("hotspotCell", $"{hotspotColumn},{hotspotRow}"), ("hotspotCount", hotspotCount),
            ("concentration", concentration), ("canvas", $"{canvasWidth}x{canvasHeight}"),
            ("grid", $"{cols}x{rows}"), ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = VisionValue.Image(VisionImage.Own(canvas.Clone())),
                ["countsJson"] = VisionValue.String(countsJson),
                ["hotspotX"] = VisionValue.Double(hotspotX),
                ["hotspotY"] = VisionValue.Double(hotspotY),
                ["hotspotCount"] = VisionValue.Integer(hotspotCount),
                ["concentration"] = VisionValue.Double(concentration),
                ["count"] = VisionValue.Integer(detections.Length),
                ["occupiedCells"] = VisionValue.Integer(occupied),
                ["found"] = VisionValue.Boolean(total > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = detections.Length, ["occupiedCells"] = occupied, ["totalCells"] = rows * cols,
                ["hotspotX"] = Math.Round(hotspotX, 2), ["hotspotY"] = Math.Round(hotspotY, 2),
                ["hotspotCount"] = hotspotCount, ["concentration"] = Math.Round(concentration, 4),
                ["canvas"] = $"{canvasWidth}x{canvasHeight}", ["grid"] = $"{cols}x{rows}",
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    private static Scalar Scale(string colormap, double ratio)
    {
        var value = Math.Clamp(ratio, 0, 1);
        if (colormap == "gray")
        {
            var level = (byte)Math.Clamp(40 + 215 * value, 0, 255);
            return new Scalar(level, level, level);
        }
        // 蓝 → 青 → 黄 → 红，按 BGR 分量线性插值。
        var red = Math.Clamp(value * 2, 0, 1);
        var green = Math.Clamp(value < 0.5 ? value * 2 : (1 - value) * 2, 0, 1);
        var blue = Math.Clamp(1 - value * 2, 0, 1);
        return new Scalar(blue * 220, green * 200, red * 235);
    }
}

/// <summary>
/// 缺陷趋势（defect.trend）：一条数量序列的均值/σ/控制限/移动平均/斜率。
/// 无状态节点——历史序列由上游累积后喂进来。
/// </summary>
public sealed class DefectTrendNode : IVisionNodeExecutor
{
    public string Type => "defect.trend";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var series = context.Require<IReadOnlyList<double>>("series");
        if (series.Count > 100000) throw new InvalidOperationException("defect.trend series budget exceeded (max 100000 samples).");
        if (series.Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("defect.trend series contains a non-finite value.");

        var window = node.GetInt("window", 5);
        if (window is < 2 or > 1000) throw new InvalidOperationException("defect.trend window must be within 2..1000.");
        var sigmaK = node.GetDouble("sigmaK", 3);
        if (!double.IsFinite(sigmaK) || sigmaK is < 1 or > 6) throw new InvalidOperationException("defect.trend sigmaK must be within 1..6.");
        var minSamples = node.GetInt("minSamples", 5);
        if (minSamples is < 2 or > 100000) throw new InvalidOperationException("defect.trend minSamples must be within 2..100000.");
        var baselineSamples = node.GetInt("baselineSamples", 0);
        if (baselineSamples is < 0 or > 100000) throw new InvalidOperationException("defect.trend baselineSamples must be within 0..100000 (0 = whole series).");

        if (series.Count < minSamples)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"trend needs at least {minSamples} samples, got {series.Count}", series.Count, timer));
        }

        // 控制限必须用"稳定期"估计：把异常点也算进 σ 会把限抬高，异常反而报不出来。
        var baselineCount = baselineSamples > 0 ? Math.Min(baselineSamples, series.Count) : series.Count;
        if (baselineCount < 2) baselineCount = series.Count;
        var baseline = new double[baselineCount];
        for (var index = 0; index < baselineCount; index++) baseline[index] = series[index];

        var mean = baseline.Average();
        var variance = baseline.Sum(value => (value - mean) * (value - mean)) / Math.Max(1, baselineCount - 1);
        var sigma = Math.Sqrt(variance);
        var ucl = mean + sigmaK * sigma;
        var lcl = Math.Max(0, mean - sigmaK * sigma);
        var last = series[^1];
        var start = Math.Max(0, series.Count - window);
        var movingAverage = 0d;
        for (var index = start; index < series.Count; index++) movingAverage += series[index];
        movingAverage /= series.Count - start;

        // 最小二乘斜率（每样本单位），用下标当 x。
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        for (var index = 0; index < series.Count; index++)
        {
            sumX += index; sumY += series[index];
            sumXy += index * series[index]; sumXx += (double)index * index;
        }
        var denominator = series.Count * sumXx - sumX * sumX;
        var slope = Math.Abs(denominator) < 1e-12 ? 0 : (series.Count * sumXy - sumX * sumY) / denominator;
        var outOfControl = last > ucl || last < lcl;
        var direction = Math.Abs(slope) < 1e-9 ? "flat" : slope > 0 ? "rising" : "falling";
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", series.Count), ("mean", mean), ("sigma", sigma), ("ucl", ucl), ("lcl", lcl),
            ("last", last), ("movingAverage", movingAverage), ("slope", slope), ("direction", direction),
            ("outOfControl", outOfControl), ("sigmaK", sigmaK), ("window", window),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["mean"] = VisionValue.Double(mean),
                ["sigma"] = VisionValue.Double(sigma),
                ["ucl"] = VisionValue.Double(ucl),
                ["lcl"] = VisionValue.Double(lcl),
                ["last"] = VisionValue.Double(last),
                ["movingAverage"] = VisionValue.Double(movingAverage),
                ["slope"] = VisionValue.Double(slope),
                ["direction"] = VisionValue.String(direction),
                ["outOfControl"] = VisionValue.Boolean(outOfControl),
                ["count"] = VisionValue.Integer(series.Count),
                ["found"] = VisionValue.Boolean(true),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = series.Count, ["mean"] = Math.Round(mean, 4), ["sigma"] = Math.Round(sigma, 4),
                ["ucl"] = Math.Round(ucl, 4), ["lcl"] = Math.Round(lcl, 4),
                ["last"] = Math.Round(last, 4), ["movingAverage"] = Math.Round(movingAverage, 4),
                ["slope"] = Math.Round(slope, 6), ["direction"] = direction, ["outOfControl"] = outOfControl,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    private static NodeExecutorResult Missing(string reason, int count, Stopwatch timer)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "defect.trend", ("found", false), ("reason", reason), ("count", count),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(false),
                ["count"] = VisionValue.Integer(count),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason, ["count"] = count,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            });
    }
}

/// <summary>
/// 缺陷样本入库（defect.dataset）：裁剪缺陷 → 写图像文件 → 输出可追溯的样本清单。
/// 路径必须是工作目录下的相对路径，绝对路径与 ".." 一律拒绝。
/// </summary>
public sealed class DefectDatasetNode : IVisionNodeExecutor
{
    public string Type => "defect.dataset";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var detections = DefectAnalysisHelpers.ReadDetections(context, "detections", Type);
        var image = context.RequireImage("image");
        if (image.Mat.Empty() || image.Width < 1 || image.Height < 1)
            throw new InvalidOperationException("defect.dataset requires a non-empty image.");

        var dataset = (node.GetString("dataset", "defect-samples") ?? "defect-samples").Trim();
        if (dataset.Length is 0 or > 64 || dataset.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("defect.dataset dataset name must be 1..64 characters without invalid file name characters.");
        var directory = (node.GetString("directory", "artifacts/defect-samples") ?? "artifacts/defect-samples").Trim();
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("defect.dataset directory is required.");
        if (Path.IsPathRooted(directory) || directory.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("defect.dataset directory must be a relative path inside the working directory; absolute paths and '..' are rejected.");

        var maxSamples = node.GetInt("maxSamples", 50);
        if (maxSamples is < 1 or > 10000) throw new InvalidOperationException("defect.dataset maxSamples must be within 1..10000.");
        var margin = node.GetInt("cropMarginPx", 4);
        if (margin is < 0 or > 512) throw new InvalidOperationException("defect.dataset cropMarginPx must be within 0..512.");
        var format = (node.GetString("format", "png") ?? "png").Trim().ToLowerInvariant();
        if (format is not ("png" or "jpg")) throw new InvalidOperationException("defect.dataset format must be png or jpg.");
        var quality = node.GetInt("jpegQuality", 92);
        if (quality is < 1 or > 100) throw new InvalidOperationException("defect.dataset jpegQuality must be within 1..100.");
        var conflict = (node.GetString("conflictPolicy", "suffix") ?? "suffix").Trim().ToLowerInvariant();
        if (conflict is not ("suffix" or "overwrite" or "skip"))
            throw new InvalidOperationException("defect.dataset conflictPolicy must be suffix, overwrite or skip.");

        var root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), directory));
        var workingDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (!root.StartsWith(workingDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("defect.dataset resolved directory escapes the working directory.");
        Directory.CreateDirectory(root);

        var extension = format == "png" ? ".png" : ".jpg";
        var samples = new List<object>();
        int saved = 0, skipped = 0;
        foreach (var detection in detections.Take(maxSamples))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = (int)Math.Floor(detection.BoundingBox.X) - margin;
            var y = (int)Math.Floor(detection.BoundingBox.Y) - margin;
            var width = (int)Math.Ceiling(detection.BoundingBox.Width) + margin * 2;
            var height = (int)Math.Ceiling(detection.BoundingBox.Height) + margin * 2;
            x = Math.Clamp(x, 0, Math.Max(0, image.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, image.Height - 1));
            width = Math.Clamp(width, 1, image.Width - x);
            height = Math.Clamp(height, 1, image.Height - y);
            using var crop = new Mat(image.Mat, new Rect(x, y, width, height));
            using var owned = crop.Clone();

            var baseName = $"{dataset}_{detection.Index:D4}";
            var path = Path.Combine(root, baseName + extension);
            if (File.Exists(path))
            {
                if (conflict == "skip") { skipped++; continue; }
                if (conflict == "suffix")
                {
                    var counter = 1;
                    do { path = Path.Combine(root, $"{baseName}_{counter:D2}{extension}"); counter++; }
                    while (File.Exists(path) && counter < 1000);
                }
            }

            ImageEncodingParam[] parameters = format == "jpg"
                ? [new ImageEncodingParam(ImwriteFlags.JpegQuality, quality)]
                : [new ImageEncodingParam(ImwriteFlags.PngCompression, 3)];
            Cv2.ImEncode(extension, owned, out var bytes, parameters);
            File.WriteAllBytes(path, bytes);
            saved++;
            samples.Add(new
            {
                id = $"{dataset}#{detection.Index}",
                file = Path.GetFileName(path),
                path = path.Replace('\\', '/'),
                classId = detection.ClassId,
                area = Math.Round(detection.Area, 2),
                x = Math.Round(detection.X, 2),
                y = Math.Round(detection.Y, 2),
                width = Math.Round(detection.Width, 2),
                height = Math.Round(detection.Height, 2)
            });
        }

        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("savedCount", saved), ("skippedCount", skipped), ("inputCount", detections.Length),
            ("dataset", dataset), ("directory", root), ("format", format), ("maxSamples", maxSamples),
            ("cropMarginPx", margin), ("conflictPolicy", conflict),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["directory"] = VisionValue.String(root.Replace('\\', '/')),
                ["savedCount"] = VisionValue.Integer(saved),
                ["skippedCount"] = VisionValue.Integer(skipped),
                ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)),
                ["found"] = VisionValue.Boolean(saved > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["savedCount"] = saved, ["skippedCount"] = skipped, ["inputCount"] = detections.Length,
                ["dataset"] = dataset, ["directory"] = root.Replace('\\', '/'),
                ["format"] = format, ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }
}
