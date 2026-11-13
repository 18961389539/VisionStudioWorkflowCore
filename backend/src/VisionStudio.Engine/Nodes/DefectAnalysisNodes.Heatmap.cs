using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

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
