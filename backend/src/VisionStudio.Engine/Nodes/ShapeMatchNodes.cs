using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 第五批（2026-10-14）· 5C 形状定位：match.shape（极坐标半径签名匹配，二维平移 + 旋转）。
///
/// 首版假设（文档化限制，不包装成通用缺陷/形态度量）：
///   · 目标关于质心近似星形（按固定角度取射线与完整轮廓的最远交点）；
///   · 尺度固定：尺度变化另行扩展（签名长度失配会体现为低分而非错定位）；
///   · 二值化后以外轮廓参与匹配（孔洞不参与签名）。
/// 匹配分数 = 1 - 归一化 RMS 半径差（循环平移搜索），范围 [0,1]；
/// 多目标按分数贪婪抑制（minCenterDistance）。未命中返回 found=false + best score。
/// </summary>
public sealed class ShapeMatchNode : IVisionNodeExecutor
{
    public string Type => "match.shape";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sceneImage = context.RequireImage("image");
        if (sceneImage.Mat.Empty())
            throw new InvalidOperationException("Shape matching requires a non-empty scene image.");

        var templatePath = node.GetString("templatePath", string.Empty).Trim();
        var binarize = node.GetString("binarize", "auto").Trim().ToLowerInvariant();
        var threshold = node.GetDouble("threshold", 128);
        var invert = node.GetBool("invert", false);
        var minArea = Math.Max(0, node.GetDouble("minArea", 200));
        var angleRange = Math.Clamp(node.GetDouble("angleRangeDeg", 180), 0, 180);
        var angleStep = Math.Clamp(node.GetDouble("angleStepDeg", 1), 0.1, 30);
        var minScore = Math.Clamp(node.GetDouble("minScore", 0.6), 0, 1);
        var maxTargets = Math.Clamp(node.GetInt("maxTargets", 5), 1, 100);
        var minCenterDistance = Math.Max(0, node.GetDouble("minCenterDistance", 20));
        var samples = Math.Clamp(node.GetInt("signatureSamples", 180), 16, 720);

        Mat? templateOwned = null;
        Mat? sceneBinary = null;
        Mat? templateBinary = null;
        try
        {
            // ---- 模板 ----
            Mat template;
            string templateSource;
            if (context.TryGetValue("template", out _))
            {
                var templateImage = context.RequireImage("template");
                var gray = PreprocessImageHelpers.ToGray8(templateImage.Mat, out _, out _);
                templateOwned = ReferenceEquals(gray, templateImage.Mat) ? gray.Clone() : gray;
                template = templateOwned;
                templateSource = "input-port";
            }
            else if (templatePath.Length > 0)
            {
                var resolved = Path.IsPathRooted(templatePath) ? Path.GetFullPath(templatePath) : Path.GetFullPath(templatePath, Environment.CurrentDirectory);
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
                throw new InvalidOperationException("Shape matching requires either a connected 'template' input or a 'templatePath' parameter.");
            }
            if (template.Empty())
                throw new InvalidOperationException("Shape matching requires a non-empty template image.");

            templateBinary = ContourNodesHelper.Binarize(template, binarize, threshold, invert, out _, out _);
            Cv2.FindContours(templateBinary, out Point[][] templateContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var templateBest = Largest(templateContours);
            if (templateBest is null)
                return ValueTask.FromResult(NotMatched(0, templateSource, "template-has-no-contour", sceneImage));
            var templateSignature = BuildSignature(templateBest.Value.Points, samples, out var templateCenter, out var templateMeanRadius);
            if (templateSignature is null)
                return ValueTask.FromResult(NotMatched(0, templateSource, "template-signature-degenerate", sceneImage));

            // ---- 场景 ----
            sceneBinary = ContourNodesHelper.Binarize(sceneImage.Mat, binarize, threshold, invert, out var sceneAutoGray, out _);
            Cv2.FindContours(sceneBinary, out Point[][] sceneContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            var candidates = new List<(VisionPoint2D Center, double Score, double Angle, RotatedRect Rect, double Area)>();
            foreach (var contour in sceneContours)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var area = Cv2.ContourArea(contour);
                if (area < minArea) continue;
                var signature = BuildSignature(contour, samples, out var center, out _);
                if (signature is null) continue;
                var best = BestShift(signature, templateSignature, angleRange, angleStep, out var effectiveStep);
                candidates.Add((center, best.Score, best.Angle, Cv2.MinAreaRect(contour), area));
            }

            // ---- 贪婪多目标抑制 ----
            var accepted = new List<(VisionPoint2D Center, double Score, double Angle, RotatedRect Rect)>();
            foreach (var candidate in candidates.OrderByDescending(x => x.Score))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Score < minScore) break;
                if (accepted.Count >= maxTargets) break;
                if (minCenterDistance > 0 && accepted.Any(a => MeasurementMath.Hypot(a.Center.X - candidate.Center.X, a.Center.Y - candidate.Center.Y) < minCenterDistance))
                    continue;
                accepted.Add((candidate.Center, candidate.Score, candidate.Angle, candidate.Rect));
            }

            var found = accepted.Count > 0;
            var outputs = new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(found),
                ["score"] = VisionValue.Double(candidates.Count > 0 ? candidates.Max(x => x.Score) : 0),
                ["image"] = VisionValue.Image(VisionImage.Own(sceneImage.Mat.Clone()))
            };
            var summary = new Dictionary<string, object?>
            {
                ["found"] = found,
                ["bestScore"] = candidates.Count > 0 ? Math.Round(candidates.Max(x => x.Score), 4) : 0,
                ["candidates"] = candidates.Count,
                ["accepted"] = accepted.Count,
                ["minScore"] = minScore,
                ["angleRangeDeg"] = angleRange,
                ["angleStepDeg"] = angleStep,
                ["templateSource"] = templateSource,
                ["binarize"] = binarize,
                ["autoGray"] = sceneAutoGray,
                ["templateCenter"] = $"{templateCenter.X:0.##},{templateCenter.Y:0.##}",
                ["templateMeanRadius"] = Math.Round(templateMeanRadius, 2)
            };

            if (found)
            {
                var best = accepted[0];
                outputs["x"] = VisionValue.Double(best.Center.X);
                outputs["y"] = VisionValue.Double(best.Center.Y);
                outputs["angle"] = VisionValue.Double(best.Angle);
                outputs["center"] = VisionValue.Point(best.Center);
                outputs["pose"] = VisionValue.Pose(new VisionPose2D(best.Center.X, best.Center.Y, best.Angle));
                outputs["rectangle"] = VisionValue.Rectangle(ToRotatedRectangle(best.Rect));
                outputs["count"] = VisionValue.Integer(accepted.Count);
                var detections = accepted.Select((a, i) =>
                {
                    var contour = RectContour(a.Rect);
                    var rect = Cv2.BoundingRect(contour);
                    return new VisionDetection(
                        Index: i,
                        X: rect.X, Y: rect.Y, Width: rect.Width, Height: rect.Height,
                        Area: Cv2.ContourArea(contour),
                        Perimeter: Cv2.ArcLength(contour, true),
                        Circularity: 0,
                        Circle: new VisionCircle(a.Center.X, a.Center.Y, Math.Max(rect.Width, rect.Height) / 2d),
                        BoundingBox: new VisionRectangle2D(rect.X, rect.Y, rect.Width, rect.Height),
                        Confidence: a.Score);
                }).ToArray();
                outputs["detections"] = VisionValue.Detections(detections);
                summary["matchedAngle"] = Math.Round(best.Angle, 3);
                summary["matchedCenter"] = $"{best.Center.X:0.##},{best.Center.Y:0.##}";
            }

            var overlays = new List<VisionOverlay>();
            for (var i = 0; i < accepted.Count; i++)
            {
                var a = accepted[i];
                var box = Cv2.BoundingRect(RectContour(a.Rect));
                overlays.Add(new VisionOverlay($"shape-match-{i}", VisionOverlayType.Rectangle,
                    Label: $"#{i} s={a.Score:0.###} a={a.Angle:0.#}",
                    X: box.X, Y: box.Y, Width: box.Width, Height: box.Height,
                    Stroke: "#52c41a", StrokeWidth: 2));
                overlays.Add(new VisionOverlay($"shape-match-center-{i}", VisionOverlayType.Point, X: a.Center.X, Y: a.Center.Y, Stroke: "#52c41a", StrokeWidth: 3));
            }

            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
        }
        finally
        {
            templateOwned?.Dispose();
            sceneBinary?.Dispose();
            templateBinary?.Dispose();
        }
    }

    private static NodeExecutorResult NotMatched(double score, string templateSource, string reason, VisionImage sceneImage) =>
        new(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(false),
                ["score"] = VisionValue.Double(score),
                ["image"] = VisionValue.Image(VisionImage.Own(sceneImage.Mat.Clone()))
            },
            new Dictionary<string, object?>
            {
                ["found"] = false,
                ["reason"] = reason,
                ["templateSource"] = templateSource
            });

    /// <summary>旋转矩形的 4 角轮廓（用于面积/外接框/overlay）。</summary>
    private static Point[] RectContour(RotatedRect rect) =>
        Cv2.BoxPoints(rect).Select(v => new Point((int)Math.Round(v.X), (int)Math.Round(v.Y))).ToArray();

    private static VisionRectangle2D ToRotatedRectangle(RotatedRect rect)
    {
        double width = rect.Size.Width;
        double height = rect.Size.Height;
        double angle = rect.Angle;
        if (width < height)
        {
            (width, height) = (height, width);
            angle += 90;
        }
        return new VisionRectangle2D(rect.Center.X, rect.Center.Y, width, height, VisionTransform2D.NormalizeAngleDeg(angle));
    }

    private static (Point[] Points, double Area)? Largest(Point[][] contours)
    {
        (Point[] Points, double Area)? best = null;
        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area <= 0) continue;
            if (best is null || area > best.Value.Area) best = (contour, area);
        }
        return best;
    }

    /// <summary>
    /// 极坐标半径签名：对每个固定角度计算射线与完整闭合轮廓边的交点，取最远交点。
    /// 这种采样不会因 ApproxSimple 只保留矩形四角而把不同方向边长混成常数。
    /// </summary>
    private static double[]? BuildSignature(Point[] contour, int samples, out VisionPoint2D center, out double meanRadius)
    {
        var moments = Cv2.Moments(contour, binaryImage: false);
        if (moments.M00 > 1e-9)
            center = new VisionPoint2D(moments.M10 / moments.M00, moments.M01 / moments.M00);
        else
        {
            var rect = Cv2.BoundingRect(contour);
            center = new VisionPoint2D(rect.X + rect.Width / 2d, rect.Y + rect.Height / 2d);
        }

        if (contour.Length < 3) { meanRadius = 0; return null; }
        var signature = new double[samples];
        for (var bin = 0; bin < samples; bin++)
        {
            var angle = bin * (2 * Math.PI / samples);
            var ux = Math.Cos(angle); var uy = Math.Sin(angle);
            var farthest = double.NaN;
            for (var i = 0; i < contour.Length; i++)
            {
                var a = contour[i]; var b = contour[(i + 1) % contour.Length];
                var ax = a.X - center.X; var ay = a.Y - center.Y;
                var ex = b.X - a.X; var ey = b.Y - a.Y;
                var denominator = ux * ey - uy * ex;
                if (Math.Abs(denominator) < 1e-10) continue;
                var t = (ax * ey - ay * ex) / denominator;
                var u = (ax * uy - ay * ux) / denominator;
                if (t >= 0 && u >= -1e-9 && u <= 1 + 1e-9 && (!double.IsFinite(farthest) || t > farthest)) farthest = t;
            }
            signature[bin] = farthest;
        }
        FillCircular(signature);
        meanRadius = signature.Where(double.IsFinite).DefaultIfEmpty(0).Average();
        return signature.Any(double.IsFinite) ? signature : null;
    }

    private static void FillCircular(double[] signature)
    {
        var n = signature.Length;
        if (n == 0) return;
        if (signature.All(double.IsNaN)) return;
        for (var pass = 0; pass < 2; pass++)
        {
            var last = double.NaN;
            for (var i = 0; i < n; i++)
            {
                if (double.IsFinite(signature[i])) last = signature[i];
                else if (double.IsFinite(last)) signature[i] = last;
            }
            var next = double.NaN;
            for (var i = n - 1; i >= 0; i--)
            {
                if (double.IsFinite(signature[i])) next = signature[i];
                else if (double.IsFinite(next)) signature[i] = next;
            }
        }
        // 首尾跨接处仍可能留下 NaN（环绕未闭合）——用最近有效值兜底。
        if (signature.Any(double.IsNaN))
        {
            var fallback = signature.First(double.IsFinite);
            for (var i = 0; i < n; i++)
                if (double.IsNaN(signature[i])) signature[i] = fallback;
        }
    }

    /// <summary>循环平移搜索：score(δ) = 1 - RMS(r_s - shift(r_t, δ)) / mean(r_t)。
    /// 分数并列（旋转对称形状）时取 |δ| 最小者，让对称目标报告接近 0 的角度而不是搜索边界。</summary>
    private static (double Score, double Angle) BestShift(double[] scene, double[] template, double range, double step, out double effectiveStep)
    {
        effectiveStep = step;
        var best = (Score: double.NegativeInfinity, Angle: 0d);
        var n = scene.Length;
        var normalization = Math.Max(1e-6, template.Where(double.IsFinite).Average());
        for (var angle = -range; angle <= range + 1e-9; angle += effectiveStep)
        {
            var shiftBins = angle / 360.0 * n;
            var baseIndex = (int)Math.Floor(shiftBins);
            var fraction = shiftBins - baseIndex;
            var sumSquares = 0d;
            for (var i = 0; i < n; i++)
            {
                var i0 = ((i + baseIndex) % n + n) % n;
                var i1 = (i0 + 1) % n;
                var interpolated = template[i0] * (1 - fraction) + template[i1] * fraction;
                var diff = scene[i] - interpolated;
                sumSquares += diff * diff;
            }
            var rmse = Math.Sqrt(sumSquares / n);
            var score = 1 - rmse / normalization;
            if (score > best.Score + 1e-9 ||
                (Math.Abs(score - best.Score) <= 1e-9 &&
                 (Math.Abs(angle) < Math.Abs(best.Angle) || (Math.Abs(Math.Abs(angle) - Math.Abs(best.Angle)) <= 1e-9 && angle > best.Angle))))
                best = (score, angle);
        }
        return (Math.Clamp(best.Score, 0, 1), best.Angle);
    }
}

/// <summary>
/// 5C-2 · roi.transform：定位后 ROI 修正。以基准位姿（ROI  authored 时的位姿）与当前位姿
/// 计算刚体变换，使测量区域跟随工件移动/旋转：
///   · p' = R(θ)·(p - base) + current，θ = currentTheta - baseTheta；
///   · 矩形 ROI 在 θ=0 时平移保持矩形；θ≠0 时转为多边形（随工件旋转，掩膜语义保持）；
///   · 圆形 ROI 平移；多边形 ROI 整体刚体变换。
/// roi 输入端口优先于 roi 参数；currentPose 输入端口优先于 currentX/Y/θ 参数。
/// </summary>
public sealed class RoiTransformNode : IVisionNodeExecutor
{
    public string Type => "roi.transform";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        VisionRoi roi;
        if (context.TryGetValue("roi", out var roiValue))
            roi = roiValue.Require<VisionRoi>("roi");
        else
        {
            var parameterRoi = node.GetRoi();
            if (parameterRoi is null)
                throw new InvalidOperationException("ROI transform requires either a connected 'roi' input or a 'roi' parameter.");
            roi = parameterRoi;
        }

        var basePose = new VisionPose2D(node.GetDouble("baseX", 0), node.GetDouble("baseY", 0), node.GetDouble("baseThetaDeg", 0));
        var currentPose = new VisionPose2D(node.GetDouble("currentX", 0), node.GetDouble("currentY", 0), node.GetDouble("currentThetaDeg", 0));
        if (context.TryGetValue("currentPose", out var poseValue))
        {
            var pose = poseValue.Require<VisionPose2D>("currentPose");
            if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.ThetaDeg))
                throw new InvalidOperationException("The connected currentPose has non-finite components.");
            currentPose = pose;
        }
        if (new[] { basePose.X, basePose.Y, basePose.ThetaDeg, currentPose.X, currentPose.Y, currentPose.ThetaDeg }.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("Base/current pose components must be finite.");

        var thetaDeg = currentPose.ThetaDeg - basePose.ThetaDeg;
        var radians = thetaDeg * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        VisionPoint2D Apply(VisionPoint2D p)
        {
            var dx = p.X - basePose.X;
            var dy = p.Y - basePose.Y;
            // 图像坐标系（y 向下）中的逆时针旋转矩阵：与 match.shape 的角度语义一致。
            return new VisionPoint2D(cos * dx + sin * dy + currentPose.X, -sin * dx + cos * dy + currentPose.Y);
        }

        VisionRoi transformed;
        List<VisionPoint2D> region;
        switch (roi.Type)
        {
            case VisionRoiType.Circle:
            {
                var radius = roi.Radius ?? 0;
                if (radius < 0) throw new InvalidOperationException("The circle ROI radius must be non-negative.");
                var translatedCenter = Apply(new VisionPoint2D(roi.X ?? 0, roi.Y ?? 0));
                transformed = roi with { X = translatedCenter.X, Y = translatedCenter.Y };
                region = CircleRegion(translatedCenter, radius);
                break;
            }
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                if (points.Count < 3) throw new InvalidOperationException("The polygon ROI needs at least 3 points.");
                var moved = points.Select(p => Apply(new VisionPoint2D(p.X, p.Y))).ToArray();
                transformed = roi with { Points = moved.Select(p => new VisionRoiPoint(p.X, p.Y)).ToArray() };
                region = moved.ToList();
                break;
            }
            default:
            {
                var x = roi.X ?? 0;
                var y = roi.Y ?? 0;
                var width = roi.Width ?? 0;
                var height = roi.Height ?? 0;
                if (width < 0 || height < 0) throw new InvalidOperationException("The rectangle ROI width/height must be non-negative.");
                var corners = new[]
                {
                    new VisionPoint2D(x, y),
                    new VisionPoint2D(x + width, y),
                    new VisionPoint2D(x + width, y + height),
                    new VisionPoint2D(x, y + height)
                }.Select(Apply).ToArray();
                if (Math.Abs(thetaDeg) < 1e-9)
                {
                    var minX = corners.Min(c => c.X);
                    var minY = corners.Min(c => c.Y);
                    transformed = roi with { X = minX, Y = minY, Width = width, Height = height };
                }
                else
                {
                    // 旋转后不再保持轴对齐：转为多边形 ROI（4 角），掩膜语义随工件保持。
                    transformed = new VisionRoi(VisionRoiType.Polygon, Points: corners.Select(c => new VisionRoiPoint(c.X, c.Y)).ToArray());
                }
                region = corners.ToList();
                break;
            }
        }

        var pointsF = region.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        var rect = Cv2.MinAreaRect(pointsF);
        var rectangle = new VisionRectangle2D(
            rect.Center.X, rect.Center.Y,
            Math.Max(rect.Size.Width, rect.Size.Height), Math.Min(rect.Size.Width, rect.Size.Height),
            rect.Size.Width < rect.Size.Height ? VisionTransform2D.NormalizeAngleDeg(rect.Angle + 90) : rect.Angle);
        var center = new VisionPoint2D(rect.Center.X, rect.Center.Y);
        var circle = new VisionCircle(center.X, center.Y, region.Max(p => MeasurementMath.Hypot(p.X - center.X, p.Y - center.Y)));

        var transform = new VisionTransform2D(
            "BasePose", "CurrentPose", "px",
            cos, sin, currentPose.X - (cos * basePose.X + sin * basePose.Y),
            -sin, cos, currentPose.Y - (-sin * basePose.X + cos * basePose.Y),
            0, 0, 1);

        var overlays = new[]
        {
            new VisionOverlay("roi-transform-region", VisionOverlayType.Rectangle,
                Label: $"dtheta={thetaDeg:0.##}deg",
                X: rectangle.X, Y: rectangle.Y, Width: rectangle.Width, Height: rectangle.Height,
                AngleDeg: rectangle.AngleDeg, Stroke: "#13c2c2", StrokeWidth: 2)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["roi"] = VisionValue.Roi(transformed),
                ["rectangle"] = VisionValue.Rectangle(rectangle),
                ["circle"] = VisionValue.Circle(circle),
                ["center"] = VisionValue.Point(center),
                ["pose"] = VisionValue.Pose(currentPose),
                ["transform"] = VisionValue.Transform(transform)
            },
            new Dictionary<string, object?>
            {
                ["roiType"] = transformed.Type.ToString(),
                ["thetaDeg"] = Math.Round(thetaDeg, 4),
                ["basePose"] = $"{basePose.X:0.##},{basePose.Y:0.##},{basePose.ThetaDeg:0.##}",
                ["currentPose"] = $"{currentPose.X:0.##},{currentPose.Y:0.##},{currentPose.ThetaDeg:0.##}",
                ["regionCenter"] = $"{center.X:0.##},{center.Y:0.##}",
                ["boundingRadius"] = Math.Round(circle.Radius, 2)
            },
            overlays));
    }

    private static List<VisionPoint2D> CircleRegion(VisionPoint2D center, double radius)
    {
        var points = new List<VisionPoint2D>(16);
        for (var i = 0; i < 16; i++)
        {
            var angle = i / 16.0 * 2 * Math.PI;
            points.Add(new VisionPoint2D(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius));
        }
        return points;
    }
}
