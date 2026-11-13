using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

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
