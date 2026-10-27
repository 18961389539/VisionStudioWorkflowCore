using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.XImgProc;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

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

        using var bgr = sourceMat.Channels() == 1 ? new Mat() : sourceMat.Clone();
        if (sourceMat.Channels() == 1) Cv2.CvtColor(sourceMat, bgr, ColorConversionCodes.GRAY2BGR);
        else if (sourceMat.Channels() == 4) Cv2.CvtColor(sourceMat, bgr, ColorConversionCodes.BGRA2BGR);

        using var slic = SuperpixelSLIC.Create(bgr, SLICType.SLICO, regionSize, (float)ruler);
        slic.Iterate(iterations);
        if (enforce) slic.EnforceLabelConnectivity(regionSize);
        using var labels = new Mat();
        slic.GetLabels(labels);

        var (components, pixelCount, labelCount) = BuildComponentsByLabel(labels, cancellationToken);
        var region = new VisionRegion(labels.Width, labels.Height, components);

        using var boundary = new Mat();
        slic.GetLabelContourMask(boundary, true);
        var boundaryImage = VisionImage.Own(boundary.Clone());
        var labelsImage = VisionImage.Own(labels.Clone());
        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(labelsImage, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(Type, ("regionSize", regionSize), ("ruler", ruler),
                ("iterations", iterations), ("enforceConnectivity", enforce), ("count", components.Count),
                ("pixelCount", pixelCount), ("width", labels.Width), ("height", labels.Height));
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["region"] = VisionValue.Region(region), ["labels"] = VisionValue.Image(labelsImage),
                    ["boundary"] = VisionValue.Image(boundaryImage),
                    ["count"] = VisionValue.Integer(components.Count), ["meanSize"] = VisionValue.Double(components.Count > 0 ? pixelCount / (double)components.Count : 0),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                },
                new Dictionary<string, object?>
                {
                    ["count"] = components.Count, ["meanSize"] = components.Count > 0 ? pixelCount / (double)components.Count : 0,
                    ["labelCount"] = labelCount, ["pixelCount"] = pixelCount, ["regionSize"] = regionSize,
                    ["ruler"] = ruler, ["iterations"] = iterations, ["enforceConnectivity"] = enforce,
                    ["width"] = labels.Width, ["height"] = labels.Height,
                    ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                });
        }, cancellationToken));
    }

    /// <summary>按标签值拆行程：每个超像素一个 component，与 region 的连通域语义一致。</summary>
    private static (List<VisionRegionComponent> Components, long PixelCount, int LabelCount) BuildComponentsByLabel(Mat labels, CancellationToken cancellationToken)
    {
        var runsByLabel = new SortedDictionary<int, List<VisionRegionRun>>();
        var indexer = labels.GetGenericIndexer<int>();
        var width = labels.Width; var height = labels.Height;
        long pixelCount = 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runStart = 0; var runLabel = indexer[y, 0];
            for (var x = 1; x <= width; x++)
            {
                var label = x < width ? indexer[y, x] : int.MinValue;
                if (label == runLabel) continue;
                if (!runsByLabel.TryGetValue(runLabel, out var runs)) runsByLabel[runLabel] = runs = [];
                runs.Add(new VisionRegionRun(y, runStart, x - 1));
                pixelCount += x - runStart;
                runLabel = label;
                runStart = x;
            }
        }
        var components = runsByLabel.Values.Select(runs => new VisionRegionComponent(runs)).ToList();
        return (components, pixelCount, runsByLabel.Count);
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

        var scene = ProfileMath.SelectContour(context, node, closed: true);
        var template = ProfileMath.SelectContour(context, node, closed: true, "templateContours", "templateIndex");
        // 顶点数少的多边形（三角形、矩形）同样合法：签名会在等弧长重采样时被加密
        if (scene.Length < 3 || template.Length < 3)
            return ValueTask.FromResult(ToleranceGeometry.Missing(Math.Min(scene.Length, template.Length), "insufficient-points", nodeType: Type));

        var sceneSignature = Signature(scene, samples);
        var templateSignature = Signature(template, samples);

        // 半径签名：以质心为原点、按平均半径归一化，天然对平移与缩放不变；
        // 旋转等价于签名循环移位，遍历所有移位取最小 RMS。
        var bestShift = 0; var bestCost = double.MaxValue;
        var shifts = allowRotation ? samples : 1;
        for (var shift = 0; shift < shifts; shift++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cost = 0.0;
            for (var i = 0; i < samples; i++)
            {
                var delta = sceneSignature.Normalized[i] - templateSignature.Normalized[(i + shift) % samples];
                cost += delta * delta;
                if (cost >= bestCost) break;
            }
            if (cost < bestCost) { bestCost = cost; bestShift = shift; }
        }
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
            ("allowRotation", allowRotation));
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
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            [new("match-centre", VisionOverlayType.Point, X: sceneSignature.Center.X, Y: sceneSignature.Center.Y,
                Label: $"score {score:0.###}")]));
    }

    private static (double[] Normalized, double[] Angles, VisionPoint2D Center, double MeanRadius) Signature(VisionPoint2D[] points, int samples)
    {
        var resampled = ProfileMath.Resample(points, closed: true, step: 1, budget: 200000);
        // 按弧长等距重采样到固定采样数
        var path = resampled.Append(resampled[0]).ToArray();
        var cumulative = new double[path.Length];
        for (var i = 1; i < path.Length; i++)
            cumulative[i] = cumulative[i - 1] + MeasurementMath.Hypot(path[i].X - path[i - 1].X, path[i].Y - path[i - 1].Y);
        var length = cumulative[^1];
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
        if (mean < 1e-9) throw new InvalidOperationException("contour.matchXld needs a contour with non-zero extent.");
        var angles = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var prev = (i - 1 + samples) % samples; var next = (i + 1) % samples;
            angles[i] = Math.Atan2(ys[next] - ys[prev], xs[next] - xs[prev]) * 180 / Math.PI;
        }
        return (radii.Select(r => r / mean).ToArray(), angles, new VisionPoint2D(centerX, centerY), mean);
    }
}
