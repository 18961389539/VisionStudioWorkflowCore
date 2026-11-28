using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;

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

        if (baselineSamples > series.Count)
            throw new InvalidOperationException($"defect.trend baselineSamples must not exceed count={series.Count}.");
        if (baselineSamples > 0 && baselineSamples < minSamples)
            throw new InvalidOperationException($"defect.trend minSamples={minSamples} must not exceed baselineSamples={baselineSamples}.");

        if (series.Count < minSamples)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"trend needs at least {minSamples} samples, got {series.Count}", series.Count, timer));
        }

        // 控制限必须用"稳定期"估计：把异常点也算进 σ 会把限抬高，异常反而报不出来。
        var baselineCount = baselineSamples > 0 ? baselineSamples : series.Count;
        var baseline = new double[baselineCount];
        for (var index = 0; index < baselineCount; index++) baseline[index] = series[index];

        var mean = baseline.Average();
        var variance = baseline.Sum(value => (value - mean) * (value - mean)) / Math.Max(1, baselineCount - 1);
        var sigma = Math.Sqrt(variance);
        var sigmaZero = sigma <= 1e-12;
        if (sigmaZero) sigma = 0;
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
        var outOfControl = sigmaZero
            ? series.Any(value => Math.Abs(value - mean) > 1e-12)
            : last > ucl || last < lcl;
        var direction = Math.Abs(slope) < 1e-9 ? "flat" : slope > 0 ? "rising" : "falling";
        timer.Stop();

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", series.Count), ("mean", mean), ("sigma", sigma), ("ucl", ucl), ("lcl", lcl),
            ("baselineMean", mean), ("baselineSigma", sigma), ("baselineSampleCount", baselineCount),
            ("baselineStartIndex", 0), ("baselineEndIndex", baselineCount - 1), ("sigmaZero", sigmaZero),
            ("last", last), ("movingAverage", movingAverage), ("slope", slope), ("direction", direction),
            ("outOfControl", outOfControl), ("sigmaK", sigmaK), ("window", window),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["mean"] = VisionValue.Double(mean),
                ["sigma"] = VisionValue.Double(sigma),
                ["baselineMean"] = VisionValue.Double(mean),
                ["baselineSigma"] = VisionValue.Double(sigma),
                ["baselineSampleCount"] = VisionValue.Integer(baselineCount),
                ["sigmaZero"] = VisionValue.Boolean(sigmaZero),
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
                ["baselineMean"] = Math.Round(mean, 4), ["baselineSigma"] = Math.Round(sigma, 4),
                ["baselineSampleCount"] = baselineCount, ["baselineStartIndex"] = 0, ["baselineEndIndex"] = baselineCount - 1,
                ["sigmaZero"] = sigmaZero,
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
/// 缺陷样本入库（defect.dataset）：裁剪缺陷 → 原子写图像/索引 → 输出可追溯的样本清单。
/// 路径被限制在工作目录内；overwrite 需要目录独占锁与既有索引校验。
/// </summary>
public sealed class DefectDatasetNode : IVisionNodeExecutor
{
    private const int IndexSchemaVersion = 1;
    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = true };

    private sealed class DatasetIndex
    {
        public int SchemaVersion { get; set; }
        public string Dataset { get; set; } = string.Empty;
        public List<DatasetIndexSample> Samples { get; set; } = [];
    }

    private sealed class DatasetIndexSample
    {
        public string File { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
    }

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

        var maxSamples = node.GetInt("maxSamples", 50);
        if (maxSamples is < 1 or > 10000) throw new InvalidOperationException("defect.dataset maxSamples must be within 1..10000.");
        var sampling = (node.GetString("samplingStrategy", "detection-order") ?? "detection-order").Trim().ToLowerInvariant();
        if (sampling is not ("detection-order" or "class-stratified" or "severity"))
            throw new InvalidOperationException("defect.dataset samplingStrategy must be detection-order, class-stratified or severity.");
        var margin = node.GetInt("cropMarginPx", 4);
        if (margin is < 0 or > 512) throw new InvalidOperationException("defect.dataset cropMarginPx must be within 0..512.");
        var format = (node.GetString("format", "png") ?? "png").Trim().ToLowerInvariant();
        if (format is not ("png" or "jpg")) throw new InvalidOperationException("defect.dataset format must be png or jpg.");
        var quality = node.GetInt("jpegQuality", 92);
        if (quality is < 1 or > 100) throw new InvalidOperationException("defect.dataset jpegQuality must be within 1..100.");
        var conflict = (node.GetString("conflictPolicy", "suffix") ?? "suffix").Trim().ToLowerInvariant();
        if (conflict is not ("suffix" or "overwrite" or "skip"))
            throw new InvalidOperationException("defect.dataset conflictPolicy must be suffix, overwrite or skip.");

        var workingDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        var root = WorkspacePathSecurity.ResolveRoot(directory, workingDirectory);
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, ".defect-dataset.lock");
        using var directoryLock = AcquireDirectoryLock(lockPath);
        var indexPath = Path.Combine(root, "index.json");
        var existing = LoadIndex(root, indexPath, dataset, conflict == "overwrite");

        var extension = format == "png" ? ".png" : ".jpg";
        var selected = sampling switch
        {
            "severity" => detections.OrderByDescending(d => d.Area).ThenBy(d => d.Index),
            "class-stratified" => Stratify(detections),
            _ => detections
        };
        var selectedDetections = selected.Take(maxSamples).ToArray();
        var unexportedCount = detections.Length - selectedDetections.Length;
        Cv2.ImEncode(".png", image.Mat, out var sourceBytes);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var samples = new List<object>();
        var indexSamples = new List<DatasetIndexSample>();
        var saved = 0;
        var skipped = 0;
        string? stagingDirectory = null;
        string? generationDirectory = null;
        try
        {
            if (conflict == "overwrite")
            {
                var generation = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
                stagingDirectory = Path.Combine(root, $".staging-{Guid.NewGuid():N}");
                generationDirectory = Path.Combine(root, "generations", generation);
                Directory.CreateDirectory(stagingDirectory);
            }

            foreach (var detection in selectedDetections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestedX = (int)Math.Floor(detection.BoundingBox.X) - margin;
                var requestedY = (int)Math.Floor(detection.BoundingBox.Y) - margin;
                var requestedWidth = (int)Math.Ceiling(detection.BoundingBox.Width) + margin * 2;
                var requestedHeight = (int)Math.Ceiling(detection.BoundingBox.Height) + margin * 2;
                var x = Math.Clamp(requestedX, 0, Math.Max(0, image.Width - 1));
                var y = Math.Clamp(requestedY, 0, Math.Max(0, image.Height - 1));
                var width = Math.Clamp(requestedWidth, 1, image.Width - x);
                var height = Math.Clamp(requestedHeight, 1, image.Height - y);
                var overflow = new { left = Math.Max(0, -requestedX), top = Math.Max(0, -requestedY), right = Math.Max(0, requestedX + requestedWidth - image.Width), bottom = Math.Max(0, requestedY + requestedHeight - image.Height) };
                using var crop = new Mat(image.Mat, new Rect(x, y, width, height));
                using var owned = crop.Clone();

                var baseName = $"{dataset}_{detection.Index:D4}";
                var fileName = baseName + extension;
                var relativeFile = fileName;
                var targetPath = Path.Combine(root, fileName);
                if (conflict == "overwrite")
                {
                    targetPath = Path.Combine(stagingDirectory!, fileName);
                    relativeFile = Path.GetRelativePath(root, Path.Combine(generationDirectory!, fileName)).Replace('\\', '/');
                }
                else if (File.Exists(targetPath))
                {
                    if (conflict == "skip") { skipped++; continue; }
                    var counter = 1;
                    do
                    {
                        fileName = $"{baseName}_{counter:D2}{extension}";
                        targetPath = Path.Combine(root, fileName);
                        counter++;
                    } while (File.Exists(targetPath) && counter < 1000);
                    relativeFile = fileName;
                }

                ImageEncodingParam[] parameters = format == "jpg"
                    ? [new ImageEncodingParam(ImwriteFlags.JpegQuality, quality)]
                    : [new ImageEncodingParam(ImwriteFlags.PngCompression, 3)];
                Cv2.ImEncode(extension, owned, out var bytes, parameters);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                WriteFileAtomically(targetPath, bytes, overwrite: false);
                saved++;
                indexSamples.Add(new DatasetIndexSample
                {
                    File = relativeFile.Replace('\\', '/'), Sha256 = hash, Width = width, Height = height
                });
                samples.Add(new
                {
                    id = $"{dataset}#{detection.Index}",
                    file = Path.GetFileName(targetPath),
                    path = (conflict == "overwrite" ? Path.Combine(root, relativeFile) : targetPath).Replace('\\', '/'),
                    sha256 = hash,
                    sourceSha256 = sourceHash,
                    sourceWidth = image.Width,
                    sourceHeight = image.Height,
                    requestedCrop = new { x = requestedX, y = requestedY, width = requestedWidth, height = requestedHeight },
                    crop = new { x, y, width, height },
                    clippedOverflow = overflow,
                    cropContract = "intersect-with-image; no padding",
                    cropWidth = width,
                    cropHeight = height,
                    classId = detection.ClassId,
                    area = Math.Round(detection.Area, 2),
                    x = Math.Round(detection.X, 2),
                    y = Math.Round(detection.Y, 2),
                    width = Math.Round(detection.Width, 2),
                    height = Math.Round(detection.Height, 2)
                });
            }

            if (conflict == "overwrite" && saved > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(generationDirectory!)!);
                Directory.Move(stagingDirectory!, generationDirectory!);
                stagingDirectory = null;
            }

            if (saved > 0)
            {
                var nextSamples = conflict == "overwrite" ? indexSamples : existing.Samples.Concat(indexSamples).ToList();
                var nextIndex = new DatasetIndex
                {
                    SchemaVersion = IndexSchemaVersion, Dataset = dataset, Samples = nextSamples
                };
                WriteIndexAtomically(indexPath, nextIndex);
            }
        }
        catch
        {
            if (stagingDirectory is not null) TryDeleteDirectory(stagingDirectory);
            throw;
        }

        skipped = detections.Length - saved;
        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("savedCount", saved), ("skippedCount", skipped), ("inputCount", detections.Length),
            ("unexportedCount", unexportedCount), ("samplingStrategy", sampling),
            ("selectedDetectionIndices", selectedDetections.Select(d => d.Index).ToArray()),
            ("cropContract", "intersect-with-image; no padding"),
            ("dataset", dataset), ("directory", root), ("format", format), ("maxSamples", maxSamples),
            ("cropMarginPx", margin), ("conflictPolicy", conflict), ("exclusiveDirectory", true),
            ("indexValidated", existing.Samples.Count > 0 || !File.Exists(indexPath)),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["directory"] = VisionValue.String(root.Replace('\\', '/')),
                ["savedCount"] = VisionValue.Integer(saved),
                ["skippedCount"] = VisionValue.Integer(skipped),
                ["unexportedCount"] = VisionValue.Integer(unexportedCount),
                ["samplesJson"] = VisionValue.String(JsonSerializer.Serialize(samples)),
                ["found"] = VisionValue.Boolean(saved > 0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["savedCount"] = saved, ["skippedCount"] = skipped, ["inputCount"] = detections.Length,
                ["unexportedCount"] = unexportedCount, ["samplingStrategy"] = sampling,
                ["selectedDetectionIndices"] = selectedDetections.Select(d => d.Index).ToArray(),
                ["dataset"] = dataset, ["directory"] = root.Replace('\\', '/'),
                ["format"] = format, ["conflictPolicy"] = conflict, ["exclusiveDirectory"] = true,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    private static IEnumerable<VisionDetection> Stratify(IReadOnlyList<VisionDetection> detections)
    {
        var groups = detections.GroupBy(d => d.ClassId ?? 0).OrderBy(g => g.Key).ToArray();
        for (var position = 0; groups.Any(g => g.Count() > position); position++)
        foreach (var group in groups)
        {
            if (position >= group.Count()) continue;
            yield return group.ElementAt(position);
        }
    }

    private static FileStream AcquireDirectoryLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("defect.dataset could not acquire exclusive access to the output directory.", ex);
        }
    }

    private static DatasetIndex LoadIndex(string root, string indexPath, string dataset, bool requireIndexForOverwrite)
    {
        if (!File.Exists(indexPath))
        {
            if (requireIndexForOverwrite && Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Any(path => !Path.GetFileName(path).Equals(".defect-dataset.lock", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("defect.dataset overwrite requires a valid index.json before replacing an existing dataset directory.");
            return new DatasetIndex { SchemaVersion = IndexSchemaVersion, Dataset = dataset, Samples = [] };
        }

        DatasetIndex index;
        try
        {
            index = JsonSerializer.Deserialize<DatasetIndex>(File.ReadAllText(indexPath), IndexJson)
                ?? throw new JsonException("empty payload");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("defect.dataset index.json is invalid; overwrite/append refused.", ex);
        }
        if (index.SchemaVersion != IndexSchemaVersion || !string.Equals(index.Dataset, dataset, StringComparison.Ordinal))
            throw new InvalidOperationException("defect.dataset index.json schema or dataset name does not match; retrain/re-export the dataset explicitly.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in index.Samples)
        {
            if (string.IsNullOrWhiteSpace(sample.File) || !seen.Add(sample.File) ||
                sample.Sha256.Length != 64 || sample.Width <= 0 || sample.Height <= 0)
                throw new InvalidOperationException("defect.dataset index.json contains an invalid or duplicate sample record.");
            var path = WorkspacePathSecurity.ResolveRelative(root, sample.File, "dataset index sample");
            if (!File.Exists(path))
                throw new InvalidOperationException($"defect.dataset index references missing sample '{sample.File}'.");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(actual, sample.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"defect.dataset index hash mismatch for sample '{sample.File}'.");
        }
        return index;
    }

    private static void WriteFileAtomically(string path, byte[] bytes, bool overwrite)
    {
        var temp = SaveImageNode.CreateTempPath(path);
        try
        {
            File.WriteAllBytes(temp, bytes);
            SaveImageNode.Commit(temp, path, overwrite);
        }
        finally
        {
            SaveImageNode.TryDelete(temp);
        }
    }

    private static void WriteIndexAtomically(string path, DatasetIndex index)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, IndexJson));
        WriteFileAtomically(path, bytes, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* preserve the original failure */ }
    }
}
