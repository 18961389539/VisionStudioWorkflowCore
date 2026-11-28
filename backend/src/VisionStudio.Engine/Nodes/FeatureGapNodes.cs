using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 行业件补全批：超像素预分割 + 轮廓匹配。
///
/// 这两个是第十节的 P2 项——不是每台设备都用得上，但缺了就只能整体换工具链：
/// 纹理件没有规则预分割、低对比件没有形状匹配。两者都输出可直接接下游的类型
/// （Region / 数值），不引入新端口类型。
/// </summary>
public sealed class SuperpixelNode : IVisionNodeExecutor
{
    public string Type => "feature.superpixel";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("feature.superpixel requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("feature.superpixel requires an 8-bit image; convert 16-bit sources with image.depthScale first.");

        var regionSize = node.GetInt("regionSize", 10);
        if (regionSize is < 2 or > 200) throw new InvalidOperationException("feature.superpixel regionSize must be within 2..200.");
        var ruler = node.GetDouble("ruler", 10);
        if (!double.IsFinite(ruler) || ruler is < 1 or > 100) throw new InvalidOperationException("feature.superpixel ruler must be within 1..100.");
        var iterations = node.GetInt("iterations", 5);
        if (iterations is < 1 or > 20) throw new InvalidOperationException("feature.superpixel iterations must be within 1..20.");
        var enforce = node.GetBool("enforceConnectivity", true);

        var segmentation = SuperpixelAlgorithms.Segment(sourceMat, regionSize, ruler, iterations, enforce);
        using var labels = segmentation.Labels;
        using var boundary = segmentation.Boundary;

        var (components, pixelCount, labelCount, fragmentedLabelCount) = BuildComponentsByLabel(labels, enforce, cancellationToken);
        var region = new VisionRegion(labels.Width, labels.Height, components);
        var minimumSide = Math.Min(labels.Width, labels.Height);
        var degeneratePartition = regionSize * 2 >= minimumSide || (long)regionSize * regionSize * 4 >= (long)labels.Width * labels.Height;

        var boundaryImage = VisionImage.Own(boundary.Clone());
        var labelsImage = VisionImage.Own(labels.Clone());
        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(labelsImage, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(Type, ("regionSize", regionSize), ("ruler", ruler),
                ("iterations", iterations), ("enforceConnectivity", enforce), ("count", components.Count),
                ("labelCount", labelCount), ("fragmentedLabelCount", fragmentedLabelCount),
                ("warningCodes", degeneratePartition ? "degeneratePartition" : "none"),
                ("pixelCount", pixelCount), ("width", labels.Width), ("height", labels.Height));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["region"] = VisionValue.Region(region), ["labels"] = VisionValue.Image(labelsImage),
                    ["boundary"] = VisionValue.Image(boundaryImage),
                    ["count"] = VisionValue.Integer(components.Count),
                    ["labelCount"] = VisionValue.Integer(labelCount),
                    ["fragmentedLabelCount"] = VisionValue.Integer(fragmentedLabelCount),
                    ["meanSize"] = VisionValue.Double(components.Count > 0 ? pixelCount / (double)components.Count : 0),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>
                {
                    ["count"] = components.Count, ["meanSize"] = components.Count > 0 ? pixelCount / (double)components.Count : 0,
                    ["labelCount"] = labelCount, ["fragmentedLabelCount"] = fragmentedLabelCount,
                    ["degeneratePartition"] = degeneratePartition, ["pixelCount"] = pixelCount, ["regionSize"] = regionSize,
                    ["ruler"] = ruler, ["iterations"] = iterations, ["enforceConnectivity"] = enforce,
                    ["width"] = labels.Width, ["height"] = labels.Height,
                    ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }

    private static (List<VisionRegionComponent> Components, long PixelCount, int LabelCount, int FragmentedLabelCount)
        BuildComponentsByLabel(Mat labels, bool enforceConnectivity, CancellationToken cancellationToken)
    {
        var runsByLabel = new SortedDictionary<int, List<VisionRegionRun>>();
        var width = labels.Width;
        var height = labels.Height;
        if (width <= 0 || height <= 0) return ([], 0, 0, 0);
        long pixelCount = 0;
        var rowBuffer = ArrayPool<int>.Shared.Rent(width);
        var previousRuns = new List<(int Label, int Start, int End, int RunIndex)>();
        var parents = new List<int>();
        int Find(int index) { while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; } return index; }
        void Union(int left, int right) { left = Find(left); right = Find(right); if (left != right) parents[right] = left; }
        try
        {
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(labels.Ptr(y), rowBuffer, 0, width);
                var currentRuns = new List<(int Label, int Start, int End, int RunIndex)>();
                var runStart = 0;
                var runLabel = rowBuffer[0];
                for (var x = 1; x <= width; x++)
                {
                    var label = x < width ? rowBuffer[x] : int.MinValue;
                    if (label == runLabel) continue;
                    if (!runsByLabel.TryGetValue(runLabel, out var runs)) runsByLabel[runLabel] = runs = [];
                    runs.Add(new VisionRegionRun(y, runStart, x - 1));
                    pixelCount += x - runStart;
                    var runIndex = parents.Count;
                    parents.Add(runIndex);
                    currentRuns.Add((runLabel, runStart, x - 1, runIndex));
                    if (!enforceConnectivity)
                    {
                        foreach (var prior in previousRuns)
                        {
                            if (prior.Label == runLabel && prior.End >= runStart && prior.Start <= x - 1)
                                Union(runIndex, prior.RunIndex);
                        }
                    }
                    runLabel = label;
                    runStart = x;
                }
                previousRuns = currentRuns;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rowBuffer);
        }

        var componentRuns = new Dictionary<int, List<VisionRegionRun>>();
        if (enforceConnectivity)
        {
            foreach (var pair in runsByLabel)
                componentRuns[componentRuns.Count] = pair.Value;
        }
        else
        {
            // Rebuild grouped runs from the original per-label rows using each run's root.
            var cursor = 0;
            foreach (var pair in runsByLabel)
            {
                foreach (var run in pair.Value)
                {
                    var root = Find(cursor++);
                    if (!componentRuns.TryGetValue(root, out var runs)) componentRuns[root] = runs = [];
                    runs.Add(run);
                }
            }
        }
        var components = componentRuns.OrderBy(pair => pair.Key).Select(pair => new VisionRegionComponent(pair.Value)).ToList();
        var connectedCount = components.Count;
        return (components, pixelCount, runsByLabel.Count, Math.Max(0, runsByLabel.Count - connectedCount));
    }
}

public sealed class ContourMatchXldNode : IVisionNodeExecutor
{
    public string Type => "contour.matchXld";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var samples = node.GetInt("samples", 256);
        if (samples is < 64 or > 2048) throw new InvalidOperationException("contour.matchXld samples must be within 64..2048.");
        var minScore = node.GetDouble("minScore", .7);
        if (!double.IsFinite(minScore) || minScore is < 0 or > 1) throw new InvalidOperationException("contour.matchXld minScore must be within 0..1.");
        var allowRotation = node.GetBool("allowRotation", true);
        var angleStart = node.GetDouble("angleStartDeg", -180);
        var angleEnd = node.GetDouble("angleEndDeg", 180);
        var angleStep = node.GetDouble("angleStepDeg", 360d / samples);
        if (!double.IsFinite(angleStart) || !double.IsFinite(angleEnd) || !double.IsFinite(angleStep)
            || angleStart < -180 || angleEnd > 180 || angleStart > angleEnd || angleStep <= 0 || angleStep > 360)
            throw new InvalidOperationException("contour.matchXld angleStartDeg/angleEndDeg must be ordered within [-180,180] and angleStepDeg must be in (0,360].");

        var scene = ProfileMath.SelectContour(context, node, closed: true);
        var template = ProfileMath.SelectContour(context, node, closed: true, "templateContours", "templateIndex");
        // 顶点数少的多边形（三角形、矩形）同样合法：签名会在等弧长重采样时被加密
        if (scene.Length < 3 || template.Length < 3)
            return ValueTask.FromResult(ToleranceGeometry.Missing(Math.Min(scene.Length, template.Length), "insufficient-points", nodeType: Type));

        if (!ContourSignature.TryCreate(scene, samples, out var sceneSignature, out var sceneError))
            return ValueTask.FromResult(ToleranceGeometry.Missing(scene.Length, sceneError, nodeType: Type));
        if (!ContourSignature.TryCreate(template, samples, out var templateSignature, out var templateError))
            return ValueTask.FromResult(ToleranceGeometry.Missing(template.Length, templateError, nodeType: Type));

        // 半径签名：以质心为原点、按平均半径归一化，天然对平移与缩放不变；
        // 旋转等价于签名循环移位。只搜索配置的角度窗口，避免全圆周期对称造成错误最优角。
        var bestShift = 0; var bestCost = double.MaxValue;
        var firstShift = allowRotation ? (int)Math.Ceiling(angleStart / 360d * samples) : 0;
        var lastShift = allowRotation ? (int)Math.Floor(angleEnd / 360d * samples) : 0;
        var shiftStep = allowRotation ? Math.Max(1, (int)Math.Round(angleStep / 360d * samples)) : 1;
        if (lastShift < firstShift) lastShift = firstShift;
        var searchedShifts = 0;
        for (var shift = firstShift; shift <= lastShift; shift += shiftStep)
        {
            cancellationToken.ThrowIfCancellationRequested();
            searchedShifts++;
            var normalizedShift = ((shift % samples) + samples) % samples;
            var cost = 0.0;
            for (var i = 0; i < samples; i++)
            {
                var delta = sceneSignature.Normalized[i] - templateSignature.Normalized[(i + normalizedShift) % samples];
                cost += delta * delta;
                if (cost >= bestCost) break;
            }
            if (cost < bestCost) { bestCost = cost; bestShift = normalizedShift; }
        }
        var actualAngleStart = allowRotation ? firstShift * 360d / samples : 0;
        var actualAngleEnd = allowRotation ? (firstShift + (searchedShifts - 1) * shiftStep) * 360d / samples : 0;
        var actualAngleStep = allowRotation ? shiftStep * 360d / samples : 0;
        var rms = Math.Sqrt(bestCost / samples);
        var score = Math.Clamp(1 - rms, 0, 1);
        var found = score >= minScore;
        // 半径签名对旋转不敏感（弧长参数化本身随形状一起转），所以转角要从**切线角差**取：
        // 刚体旋转时每个样本的切线方向都恰好差 θ，圆周均值即可反解出 θ。
        var rotation = 0.0;
        if (allowRotation)
        {
            double sin = 0, cos = 0;
            for (var i = 0; i < samples; i++)
            {
                var delta = (sceneSignature.Angles[i] - templateSignature.Angles[(i + bestShift) % samples]) * Math.PI / 180;
                sin += Math.Sin(delta); cos += Math.Cos(delta);
            }
            rotation = Math.Atan2(sin, cos) * 180 / Math.PI;
        }
        var scale = templateSignature.MeanRadius > 1e-9 ? sceneSignature.MeanRadius / templateSignature.MeanRadius : 0;

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", found), ("score", score),
            ("rotationDeg", rotation), ("scale", scale), ("samples", samples), ("minScore", minScore),
            ("allowRotation", allowRotation), ("angleStartDeg", angleStart), ("angleEndDeg", angleEnd),
            ("angleStepDeg", angleStep), ("searchAngleStartDeg", actualAngleStart),
            ("searchAngleEndDeg", actualAngleEnd), ("searchAngleStepDeg", actualAngleStep),
            ("searchShiftCount", searchedShifts));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(found), ["score"] = VisionValue.Double(score),
                ["rotationDeg"] = VisionValue.Double(rotation), ["scale"] = VisionValue.Double(scale),
                ["center"] = VisionValue.Point(sceneSignature.Center), ["sampleCount"] = VisionValue.Integer(samples),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = found, ["score"] = score, ["rotationDeg"] = rotation, ["scale"] = scale,
                ["center"] = sceneSignature.Center, ["rms"] = rms, ["samples"] = samples,
                ["minScore"] = minScore, ["allowRotation"] = allowRotation,
                ["angleStartDeg"] = angleStart, ["angleEndDeg"] = angleEnd, ["angleStepDeg"] = angleStep,
                ["searchAngleStartDeg"] = actualAngleStart, ["searchAngleEndDeg"] = actualAngleEnd,
                ["searchAngleStepDeg"] = actualAngleStep, ["searchShiftCount"] = searchedShifts,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            [new("match-centre", VisionOverlayType.Point, X: sceneSignature.Center.X, Y: sceneSignature.Center.Y,
                Label: $"score {score:0.###}")]));
    }

    private static bool TryCreateSignature(VisionPoint2D[] points, int samples,
        out ContourSignature signature, out string error)
        => ContourSignature.TryCreate(points, samples, out signature, out error);
}

internal readonly record struct ContourSignature(
    double[] Normalized, double[] Angles, VisionPoint2D Center, double MeanRadius)
{
    public static bool TryCreate(VisionPoint2D[] points, int samples,
        out ContourSignature signature, out string error)
    {
        signature = default;
        error = string.Empty;
        if (points is null || points.Length < 3)
        {
            error = "degenerate-contour: at least three distinct points are required";
            return false;
        }
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            error = "degenerate-contour: coordinates must be finite";
            return false;
        }
        var distinct = new HashSet<(long, long)>();
        foreach (var point in points)
            distinct.Add(((long)Math.Round(point.X * 1e9), (long)Math.Round(point.Y * 1e9)));
        if (distinct.Count < 3)
        {
            error = "degenerate-contour: fewer than three distinct points";
            return false;
        }

        VisionPoint2D[] resampled;
        try { resampled = ProfileMath.Resample(points, closed: true, step: 1, budget: 200000); }
        catch (InvalidOperationException ex)
        {
            error = "degenerate-contour: " + ex.Message;
            return false;
        }
        if (resampled.Length < 4)
        {
            error = "degenerate-contour: resampling produced too few points";
            return false;
        }

        // closed resampling excludes the duplicated endpoint; the cyclic last->first edge must
        // remain comparable to the mean edge. A jump indicates broken/non-closed input geometry.
        var path = resampled.Append(resampled[0]).ToArray();
        var cumulative = new double[path.Length];
        for (var i = 1; i < path.Length; i++)
            cumulative[i] = cumulative[i - 1] + MeasurementMath.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
        var length = cumulative[^1];
        if (!double.IsFinite(length) || length < 1e-6)
        {
            error = "degenerate-contour: perimeter is zero or non-finite";
            return false;
        }
        var meanEdge = length / resampled.Length;
        var closingEdge = MeasurementMath.Hypot(resampled[^1].X - resampled[0].X, resampled[^1].Y - resampled[0].Y);
        if (closingEdge > Math.Max(1e-6, meanEdge * 4))
        {
            error = "degenerate-contour: resampled contour is not continuous at the closing edge";
            return false;
        }

        var centerX = resampled.Average(p => p.X); var centerY = resampled.Average(p => p.Y);
        var radii = new double[samples];
        var xs = new double[samples]; var ys = new double[samples];
        var segment = 1;
        for (var i = 0; i < samples; i++)
        {
            var target = length * i / samples;
            while (segment < path.Length - 1 && cumulative[segment] < target) segment++;
            var span = cumulative[segment] - cumulative[segment - 1];
            var fraction = span > 1e-12 ? (target - cumulative[segment - 1]) / span : 0;
            var x = path[segment - 1].X + fraction * (path[segment].X - path[segment - 1].X);
            var y = path[segment - 1].Y + fraction * (path[segment].Y - path[segment - 1].Y);
            radii[i] = MeasurementMath.Hypot(x - centerX, y - centerY);
            xs[i] = x; ys[i] = y;
        }
        var mean = radii.Average();
        if (!double.IsFinite(mean) || mean < 1e-9)
        {
            error = "degenerate-contour: non-zero extent is required";
            return false;
        }
        var angles = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var prev = (i - 1 + samples) % samples; var next = (i + 1) % samples;
            angles[i] = Math.Atan2(ys[next] - ys[prev], xs[next] - xs[prev]) * 180 / Math.PI;
        }
        signature = new ContourSignature(radii.Select(r => r / mean).ToArray(), angles,
            new VisionPoint2D(centerX, centerY), mean);
        return true;
    }
}
