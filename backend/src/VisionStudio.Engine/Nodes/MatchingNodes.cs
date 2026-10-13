using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 批次一（2026-10-13 节点补齐）· 模板匹配与旋转定位：match.template。
/// 输入：image（场景）+ 可选 template 端口；未连模板端口时从 templatePath 读取（灰度）。
/// 输出：found / score / angle / x / y / center / rectangle；未命中（score &lt; minScore）时
/// 仅输出 found=false 与 best score——"没找到"是正常业务结果（NG 判定），不是异常。
///
/// angle 语义（下游机器人引导要用）：**把模板旋转该角度后与场景对齐**（OpenCV 正方向 = 逆时针）。
/// 旋转搜索：以 angleStepDeg 在 ±angleRangeDeg 内扫描，每次把模板旋转 θ 后重新匹配；
/// 模板旋转后的外接尺寸超过场景的角度会被跳过（summary 记录 skippedAngles）。
/// 不变量：不修改输入 Mat；所有临时 Mat 就地释放；输出 geometry 与场景坐标系一致。
/// </summary>
public sealed class TemplateMatchNode : IVisionNodeExecutor
{
    public string Type => "match.template";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sceneImage = context.RequireImage("image");
        var scene = sceneImage.Mat;
        if (scene.Empty())
            throw new InvalidOperationException("Template matching requires a non-empty scene image.");
        var range = Math.Clamp(node.GetDouble("angleRangeDeg", 0), 0, 180);
        var step = Math.Clamp(node.GetDouble("angleStepDeg", 5), 0.5, 30);
        var minScore = Math.Clamp(node.GetDouble("minScore", 0.5), 0, 1);
        var method = node.GetString("method", "ccoeff-normed").Trim().ToLowerInvariant();
        var templatePath = node.GetString("templatePath", string.Empty).Trim();

        var sceneGray = PreprocessImageHelpers.ToGray8(scene, out var sceneConverted, out var ownedSceneGray);
        Mat? templateOwned = null;
        try
        {
            Mat template;
            string templateSource;
            if (context.TryGetValue("template", out _))
            {
                // 模板端口：彩色输入先转灰度；单通道输入克隆一份（保持"模板由本节点负责释放"）。
                var templateImage = context.RequireImage("template");
                var gray = PreprocessImageHelpers.ToGray8(templateImage.Mat, out _, out _);
                templateOwned = ReferenceEquals(gray, templateImage.Mat) ? gray.Clone() : gray;
                template = templateOwned;
                templateSource = "input-port";
            }
            else if (templatePath.Length > 0)
            {
                var resolved = ResolvePath(templatePath);
                var loaded = Cv2.ImRead(resolved, ImreadModes.Grayscale);
                if (loaded.Empty())
                {
                    loaded.Dispose();
                    throw new InvalidOperationException($"Template image '{resolved}' could not be loaded (missing or not a decodable image).");
                }
                templateOwned = loaded;
                template = templateOwned;
                templateSource = "file";
            }
            else
            {
                throw new InvalidOperationException(
                    "Template matching requires either a connected 'template' input or a 'templatePath' parameter.");
            }

            if (template.Empty())
                throw new InvalidOperationException("Template matching requires a non-empty template image.");
            if (template.Width > sceneGray.Width || template.Height > sceneGray.Height)
                throw new InvalidOperationException(
                    $"Template ({template.Width}x{template.Height}) is larger than the image ({sceneGray.Width}x{sceneGray.Height}).");

            var matchMode = method == "sqdiff-normed" ? TemplateMatchModes.SqDiffNormed : TemplateMatchModes.CCoeffNormed;
            var angles = BuildAngles(range, step, out var effectiveStep);
            var skipped = 0;
            var best = (Score: double.NegativeInfinity, Angle: 0d, Location: new Point(0, 0), Width: 0, Height: 0);

            foreach (var angle in angles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Mat rotated;
                var disposeRotated = false;
                if (Math.Abs(angle) < 0.0001)
                {
                    rotated = template;
                }
                else
                {
                    rotated = RotateExpanded(template, angle);
                    disposeRotated = true;
                }
                try
                {
                    if (rotated.Width > sceneGray.Width || rotated.Height > sceneGray.Height)
                    {
                        skipped++;
                        continue;
                    }
                    using var result = new Mat();
                    Cv2.MatchTemplate(sceneGray, rotated, result, matchMode);
                    Cv2.MinMaxLoc(result, out var minValue, out var maxValue, out var minLocation, out var maxLocation);
                    // sqdiff-normed：越小越好 ⇒ 换算为"越大越好"的统一分数；常量模板可能产生 NaN ⇒ 记 0。
                    var score = matchMode == TemplateMatchModes.SqDiffNormed ? 1 - minValue : maxValue;
                    var location = matchMode == TemplateMatchModes.SqDiffNormed ? minLocation : maxLocation;
                    score = double.IsFinite(score) ? Math.Clamp(score, 0, 1) : 0;
                    if (score > best.Score)
                        best = (score, angle, location, rotated.Width, rotated.Height);
                }
                finally
                {
                    if (disposeRotated) rotated.Dispose();
                }
            }

            var found = best.Score >= minScore;
            var outputs = new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(found),
                ["score"] = VisionValue.Double(best.Score)
            };
            var overlays = new List<VisionOverlay>();
            if (found)
            {
                var centerX = best.Location.X + best.Width / 2d;
                var centerY = best.Location.Y + best.Height / 2d;
                outputs["angle"] = VisionValue.Double(best.Angle);
                outputs["x"] = VisionValue.Double(best.Location.X);
                outputs["y"] = VisionValue.Double(best.Location.Y);
                outputs["center"] = VisionValue.Point(new VisionPoint2D(centerX, centerY));
                outputs["rectangle"] = VisionValue.Rectangle(new VisionRectangle2D(best.Location.X, best.Location.Y, best.Width, best.Height, best.Angle));
                overlays.Add(new VisionOverlay(
                    "match-rect",
                    VisionOverlayType.Rectangle,
                    Label: $"score={best.Score:0.###} angle={best.Angle:0.#}",
                    X: best.Location.X,
                    Y: best.Location.Y,
                    Width: best.Width,
                    Height: best.Height,
                    AngleDeg: best.Angle,
                    Stroke: "#52c41a",
                    StrokeWidth: 2));
            }

            return ValueTask.FromResult(new NodeExecutorResult(
                outputs,
                new Dictionary<string, object?>
                {
                    ["found"] = found,
                    ["score"] = best.Score,
                    ["angle"] = found ? best.Angle : null,
                    ["angleRangeDeg"] = range,
                    ["angleStepDeg"] = effectiveStep,
                    ["skippedAngles"] = skipped,
                    ["method"] = matchMode == TemplateMatchModes.SqDiffNormed ? "sqdiff-normed" : "ccoeff-normed",
                    ["templateSource"] = templateSource,
                    ["autoGray"] = sceneConverted,
                    ["templateSize"] = $"{template.Width}x{template.Height}"
                },
                overlays));
        }
        finally
        {
            templateOwned?.Dispose();
            ownedSceneGray?.Dispose();
        }
    }

    /// <summary>±range、步长 step（超限时收敛步长使总次数 ≤ 121，避免旋转扫描把节拍拖垮）。</summary>
    private static List<double> BuildAngles(double range, double step, out double effectiveStep)
    {
        effectiveStep = step;
        if (range <= 0) return [0];
        var count = (int)Math.Floor(2 * range / effectiveStep) + 1;
        if (count > 121)
        {
            effectiveStep = Math.Ceiling(2 * range / 120 * 10) / 10; // 0.1 度粒度收敛
            count = (int)Math.Floor(2 * range / effectiveStep) + 1;
        }
        var angles = new List<double>(count);
        for (var angle = -range; angle <= range + 0.0001; angle += effectiveStep)
            angles.Add(Math.Round(angle, 3));
        return angles;
    }

    /// <summary>把模板绕中心旋转 angleDeg（正=逆时针）并扩展到完整外接尺寸（边界复制，避免黑角干扰匹配）。</summary>
    private static Mat RotateExpanded(Mat source, double angleDeg)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        var cos = Math.Abs(matrix.At<double>(0, 0));
        var sin = Math.Abs(matrix.At<double>(0, 1));
        var width = (int)Math.Ceiling(source.Width * cos + source.Height * sin);
        var height = (int)Math.Ceiling(source.Width * sin + source.Height * cos);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + (width / 2d - center.X));
        matrix.Set(1, 2, matrix.At<double>(1, 2) + (height / 2d - center.Y));
        var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Replicate);
        return rotated;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        var absolute = Path.GetFullPath(path, Environment.CurrentDirectory);
        return absolute;
    }
}
