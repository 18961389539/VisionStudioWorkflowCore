using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 特征点匹配（match.feature，2026-10-27）。
///
/// 现场痛点：`match.shape`（灰边形状匹配）依赖梯度方向模板，在"大角度旋转 + 明显缩放、
/// 纹理丰富但边缘弱、局部被遮挡"的来料上会直接掉分；`match.template`（NCC）连旋转都不支持。
/// 成熟平台（VisionPro PatMax 的 fallback、HALCON 的 `proj_match_points_ransac`、
/// Adaptive Vision 的 Feature Matching）都提供描述子匹配作为兜底。
///
/// 本节点用 OpenCV 5 的 SIFT / ORB 提取关键点 + 二进制/浮点描述子，KNN + Lowe ratio test
/// 得到初始对应，再用 RANSAC 估计"模板像素坐标 → 场景像素坐标"的相似/仿射/单应变换，
/// 输出可被 `roi.transform`、`coordinate.*` 直接消费的 pose。
///
/// 关键取舍：
/// - 只做单目标对齐（首个一致变换），不做多目标穷举——多目标场景应由 shape match 负责；
/// - SIFT 负责旋转 + 缩放 + 轻度透视，ORB 负责高节拍（二进制描述子，Hamming 距离）；
/// - 变换方向固定为"模板 → 场景"，因为下游需要的是"模板上的 ROI 跟随"而不是反向查表；
/// - 匹配不足不抛异常，返回 found=false 并在 diagnostics 里写明 reason（与 match.shape 一致），
///   只有配置错误（模板无特征、参数越界）才硬失败。
/// </summary>
public sealed class FeatureMatchNode : IVisionNodeExecutor
{
    public string Type => "match.feature";

    /// <summary>叠加图最多画多少个内点，避免大图 + 数千内点时绘制成本失控。</summary>
    private const int MaxOverlayInliers = 200;

    /// <summary>规范化后的节点参数，避免在主流程里到处 GetDouble。</summary>
    private sealed record Options(
        string Detector,
        int MaxFeatures,
        double ContrastThreshold,
        double MatchRatio,
        string TransformType,
        double RansacReprojThresholdPx,
        int MinMatches,
        int MinInliers,
        double MinInlierRatio,
        double AngleRangeDeg,
        double ScaleMinPercent,
        double ScaleMaxPercent,
        VisionRoi? SceneRoi,
        VisionRoi? TemplateRoi);

    /// <summary>一次匹配的全部标量结果，便于先判定再统一组装输出。</summary>
    private sealed record MatchOutcome(
        bool Found,
        string Reason,
        int MatchCount,
        int InlierCount,
        double InlierRatio,
        double ReprojRmsPx,
        int SceneKeypointCount,
        int TemplateKeypointCount,
        double X,
        double Y,
        double AngleDeg,
        double Scale,
        double ScaleX,
        double ScaleY,
        VisionPoint2D Center,
        double[] Matrix,
        IReadOnlyList<VisionPoint2D> InlierScenePoints,
        IReadOnlyList<VisionPoint2D> InlierTemplatePoints,
        VisionRectangle2D BoundingBox,
        double RotatedArea,
        double RotatedPerimeter,
        IReadOnlyList<VisionPoint2D> Quad)
    {
        /// <summary>是否估出了可用变换。false 时下游几何端口整体不输出，避免 NaN 进 JSON。</summary>
        public bool HasGeometry => InlierCount > 0;
    }

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var scene = context.RequireImage("image");
        if (scene.Mat.Empty())
            throw new InvalidOperationException("match.feature requires a non-empty scene image.");

        var options = ReadOptions(context, node);

        // 场景与模板都统一转成 8 位单通道：SIFT/ORB 只接受 8 位图，16 位按 1/257 线性压到 8 位
        // （与 image.threshold / measure.caliper 的 16 位处理口径保持一致，不做隐式截断）。
        using var sceneGray = ToGray8(scene.Mat, "scene");
        using var templateGray = AcquireTemplateGray(context, node);

        using var detector = CreateDetector(options);
        using var sceneMask = options.SceneRoi?.BuildMask(sceneGray.Size(), cancellationToken);
        using var templateMask = options.TemplateRoi?.BuildMask(templateGray.Size(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var sceneDescriptors = new Mat();
        using var templateDescriptors = new Mat();
        Detect(detector, sceneGray, sceneMask, sceneDescriptors, out var sceneKeypoints, "scene");
        Detect(detector, templateGray, templateMask, templateDescriptors, out var templateKeypoints, "template");
        cancellationToken.ThrowIfCancellationRequested();

        if (templateDescriptors.Empty())
            throw new InvalidOperationException(
                "match.feature found no template descriptors; lower contrastThreshold, raise maxFeatures, or pick a textured template region.");

        var outcome = Match(options, sceneGray, templateGray, sceneDescriptors, templateDescriptors,
            sceneKeypoints, templateKeypoints, cancellationToken);

        // 叠加图在 BuildOverlayImage 内部完成 Mat → VisionImage 的所有权移交；
        // 结果组装失败时由 CreateOwnedResult 负责释放，这里不再需要 try/finally。
        var image = BuildOverlayImage(scene.Mat, outcome, cancellationToken);
        timer.Stop();

        return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
        {
            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("detector", options.Detector),
                ("transformType", options.TransformType),
                ("found", outcome.Found),
                ("reason", outcome.Reason),
                ("sceneKeypoints", outcome.SceneKeypointCount),
                ("templateKeypoints", outcome.TemplateKeypointCount),
                ("matchCount", outcome.MatchCount),
                ("inlierCount", outcome.InlierCount),
                ("inlierRatio", outcome.InlierRatio),
                ("reprojRmsPx", outcome.ReprojRmsPx),
                ("angleDeg", outcome.AngleDeg),
                ("scale", outcome.Scale),
                ("roiApplied", options.SceneRoi is not null),
                ("elapsedMs", timer.Elapsed.TotalMilliseconds));

            var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["image"] = VisionValue.Image(image),
                ["found"] = VisionValue.Boolean(outcome.Found),
                ["score"] = VisionValue.Double(outcome.Found ? outcome.InlierRatio : 0d),
                ["matchCount"] = VisionValue.Integer(outcome.MatchCount),
                ["inlierCount"] = VisionValue.Integer(outcome.InlierCount),
                ["inlierRatio"] = VisionValue.Double(outcome.InlierRatio),
                ["reprojErrorPx"] = VisionValue.Double(outcome.ReprojRmsPx),
                ["templateKeypoints"] = VisionValue.Integer(outcome.TemplateKeypointCount),
                ["sceneKeypoints"] = VisionValue.Integer(outcome.SceneKeypointCount),
                ["diagnostics"] = VisionValue.String(diagnostics)
            };

            // 几何端口只在真的估出变换时输出：没估出变换就塞 NaN 会污染下游 JSON 与数值节点，
            // 与 match.template 把 x/y/angle/scale 标记为可选端口的口径一致。
            if (outcome.HasGeometry)
            {
                outputs["x"] = VisionValue.Double(outcome.X);
                outputs["y"] = VisionValue.Double(outcome.Y);
                outputs["angle"] = VisionValue.Double(outcome.AngleDeg);
                outputs["scale"] = VisionValue.Double(outcome.Scale);
                outputs["center"] = VisionValue.Point(outcome.Center);
                outputs["pose"] = VisionValue.Pose(new VisionPose2D(outcome.X, outcome.Y, outcome.AngleDeg));
                outputs["transformJson"] = VisionValue.String(JsonSerializer.Serialize(new
                {
                    type = options.TransformType,
                    templateToScene = new[]
                    {
                        new[] { outcome.Matrix[0], outcome.Matrix[1], outcome.Matrix[2] },
                        new[] { outcome.Matrix[3], outcome.Matrix[4], outcome.Matrix[5] },
                        new[] { outcome.Matrix[6], outcome.Matrix[7], outcome.Matrix[8] }
                    },
                    scaleX = outcome.ScaleX,
                    scaleY = outcome.ScaleY
                }));
                outputs["inlierPoints"] = VisionValue.PointList(outcome.InlierScenePoints);
                outputs["detections"] = VisionValue.Detections(BuildDetections(outcome));
            }

            return new NodeExecutorResult(outputs, new Dictionary<string, object?>
            {
                ["detector"] = options.Detector,
                ["transformType"] = options.TransformType,
                ["found"] = outcome.Found,
                ["reason"] = outcome.Reason,
                ["sceneKeypoints"] = outcome.SceneKeypointCount,
                ["templateKeypoints"] = outcome.TemplateKeypointCount,
                ["matchCount"] = outcome.MatchCount,
                ["inlierCount"] = outcome.InlierCount,
                ["inlierRatio"] = outcome.InlierRatio,
                ["reprojRmsPx"] = outcome.ReprojRmsPx,
                ["angleDeg"] = outcome.AngleDeg,
                ["scale"] = outcome.Scale,
                ["roiApplied"] = options.SceneRoi is not null,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv",
                ["matchingBackend"] = "opencv",
                ["diagnostics"] = diagnostics
            });
        }, cancellationToken));
    }

    // ---------------------------------------------------------------- 参数

    private static Options ReadOptions(NodeExecutionContext context, NodeDefinition node)
    {
        var detector = node.GetString("detector", "sift").Trim().ToLowerInvariant();
        if (detector is not ("sift" or "orb"))
            throw new InvalidOperationException("match.feature detector must be sift or orb.");

        var maxFeatures = node.GetInt("maxFeatures", 2000);
        if (maxFeatures is < 100 or > 20000)
            throw new InvalidOperationException("match.feature maxFeatures must be within 100..20000.");

        var contrastThreshold = node.GetDouble("contrastThreshold", 0.04);
        if (!double.IsFinite(contrastThreshold) || contrastThreshold is < 0.001 or > 1)
            throw new InvalidOperationException("match.feature contrastThreshold must be finite and within 0.001..1.");

        var matchRatio = node.GetDouble("matchRatio", 0.75);
        if (!double.IsFinite(matchRatio) || matchRatio is < 0 or > 0.95)
            throw new InvalidOperationException("match.feature matchRatio must be finite and within 0..0.95; 0 disables the ratio test.");

        var transformType = node.GetString("transformType", "similarity").Trim().ToLowerInvariant();
        if (transformType is not ("similarity" or "affine" or "homography"))
            throw new InvalidOperationException("match.feature transformType must be similarity, affine or homography.");

        var ransacReprojThresholdPx = node.GetDouble("ransacReprojThresholdPx", 3);
        if (!double.IsFinite(ransacReprojThresholdPx) || ransacReprojThresholdPx is < 0.1 or > 50)
            throw new InvalidOperationException("match.feature ransacReprojThresholdPx must be finite and within 0.1..50.");

        var minMatches = node.GetInt("minMatches", 8);
        if (minMatches is < 3 or > 500)
            throw new InvalidOperationException("match.feature minMatches must be within 3..500.");

        var minInliers = node.GetInt("minInliers", 6);
        if (minInliers is < 3 or > 500)
            throw new InvalidOperationException("match.feature minInliers must be within 3..500.");

        var minInlierRatio = node.GetDouble("minInlierRatio", 0.2);
        if (!double.IsFinite(minInlierRatio) || minInlierRatio is < 0 or > 1)
            throw new InvalidOperationException("match.feature minInlierRatio must be finite and within 0..1.");

        var angleRangeDeg = node.GetDouble("angleRangeDeg", 180);
        if (!double.IsFinite(angleRangeDeg) || angleRangeDeg is < 0 or > 180)
            throw new InvalidOperationException("match.feature angleRangeDeg must be finite and within 0..180; 180 disables the gate.");

        var scaleMinPercent = node.GetDouble("scaleMinPercent", 50);
        var scaleMaxPercent = node.GetDouble("scaleMaxPercent", 200);
        if (!double.IsFinite(scaleMinPercent) || scaleMinPercent is < 10 or > 400)
            throw new InvalidOperationException("match.feature scaleMinPercent must be finite and within 10..400.");
        if (!double.IsFinite(scaleMaxPercent) || scaleMaxPercent is < 10 or > 400)
            throw new InvalidOperationException("match.feature scaleMaxPercent must be finite and within 10..400.");
        if (scaleMinPercent > scaleMaxPercent)
            throw new InvalidOperationException(
                $"match.feature scaleMinPercent ({scaleMinPercent}) must not exceed scaleMaxPercent ({scaleMaxPercent}).");

        var sceneRoi = context.TryGetValue("roi", out var roiValue)
            ? roiValue.Require<VisionRoi>("roi")
            : node.GetRoi("roi");
        var templateRoi = node.GetRoi("templateRoi");

        return new Options(detector, maxFeatures, contrastThreshold, matchRatio, transformType,
            ransacReprojThresholdPx, minMatches, minInliers, minInlierRatio, angleRangeDeg,
            scaleMinPercent, scaleMaxPercent, sceneRoi, templateRoi);
    }

    // ---------------------------------------------------------------- 图像准备

    /// <summary>统一转 8 位单通道：8 位彩色走灰度转换，16 位按 1/257 压缩，其余一律拒绝。</summary>
    private static Mat ToGray8(Mat source, string role)
    {
        if (source.Empty())
            throw new InvalidOperationException($"match.feature requires a non-empty {role} image.");

        using var scaled = new Mat();
        var working = source;
        if (source.Depth() == MatType.CV_16U)
        {
            source.ConvertTo(scaled, MatType.CV_16U, 1d / 257d);
            working = scaled;
        }
        else if (source.Depth() != MatType.CV_8U)
        {
            throw new InvalidOperationException(
                $"match.feature supports 8-bit or 16-bit {role} images; received {source.Depth()}.");
        }

        var gray = new Mat();
        switch (working.Channels())
        {
            case 1:
                working.CopyTo(gray);
                break;
            case 3:
                Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(working, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                gray.Dispose();
                throw new InvalidOperationException(
                    $"match.feature supports 1, 3, or 4 channel {role} images; received {working.Channels()}.");
        }
        return gray;
    }

    /// <summary>模板来源优先级：template 端口 &gt; templatePath。</summary>
    private static Mat AcquireTemplateGray(NodeExecutionContext context, NodeDefinition node)
    {
        if (context.TryGetValue("template", out _))
        {
            // RequireImage 同时兼容引擎自有 VisionImage 与插件传入的 IVisionImage（借出 Mat）。
            var template = context.RequireImage("template");
            return ToGray8(template.Mat, "template");
        }

        var path = node.GetString("templatePath", string.Empty).Trim();
        if (path.Length == 0)
            throw new InvalidOperationException("match.feature requires a connected template image or templatePath.");

        using var fileImage = Cv2.ImRead(System.IO.Path.GetFullPath(path), ImreadModes.Grayscale);
        if (fileImage.Empty())
            throw new InvalidOperationException($"match.feature could not read templatePath '{path}'.");
        return ToGray8(fileImage, "template");
    }

    private static Feature2D CreateDetector(Options options) => options.Detector == "orb"
        // ORB：nfeatures / scaleFactor / nlevels / edgeThreshold / firstLevel / WTA_K / score / patchSize / fastThreshold
        ? ORB.Create(options.MaxFeatures, 1.2f, 8, 31, 0, 2, ORBScoreType.Harris, 31, 20)
        // SIFT：nfeatures / nOctaveLayers / contrastThreshold / edgeThreshold / sigma
        : SIFT.Create(options.MaxFeatures, 3, options.ContrastThreshold, 10, 1.6);

    private static void Detect(Feature2D detector, Mat gray, Mat? mask, Mat descriptors,
        out KeyPoint[] keypoints, string role)
    {
        using var emptyMask = mask is null ? new Mat() : null;
        detector.DetectAndCompute(gray, mask ?? emptyMask!, out keypoints, descriptors, false);
        if (keypoints.Length != descriptors.Rows && !descriptors.Empty())
            throw new InvalidOperationException(
                $"match.feature {role} descriptors ({descriptors.Rows}) do not match keypoint count ({keypoints.Length}).");
    }

    // ---------------------------------------------------------------- 匹配

    private static MatchOutcome Match(
        Options options, Mat sceneGray, Mat templateGray,
        Mat sceneDescriptors, Mat templateDescriptors,
        KeyPoint[] sceneKeypoints, KeyPoint[] templateKeypoints,
        CancellationToken cancellationToken)
    {
        var empty = new MatchOutcome(false, "no-scene-keypoints", 0, 0, 0, 0,
            sceneKeypoints.Length, templateKeypoints.Length, double.NaN, double.NaN, 0, 1, 1, 1,
            new VisionPoint2D(double.NaN, double.NaN), Identity3x3(), [], [], default, 0, 0, []);

        if (sceneDescriptors.Empty())
            return empty with { Reason = "no-scene-keypoints" };

        // SIFT 是浮点描述子（L2），ORB 是二进制描述子（Hamming）；用错范数会让匹配全废。
        using DescriptorMatcher matcher = options.Detector == "orb"
            ? new BFMatcher(NormTypes.Hamming, false)
            : new BFMatcher(NormTypes.L2, false);

        matcher.Add([templateDescriptors]);
        cancellationToken.ThrowIfCancellationRequested();
        // 场景描述子是 query，模板是 train：DMatch.QueryIdx 指场景点，TrainIdx 指模板点。
        var knn = matcher.KnnMatch(sceneDescriptors, 2, null, false);
        cancellationToken.ThrowIfCancellationRequested();

        var templatePoints = new List<Point2d>(knn.Length);
        var scenePoints = new List<Point2d>(knn.Length);
        foreach (var row in knn)
        {
            if (row.Length == 0) continue;
            var best = row[0];
            if (options.MatchRatio > 0 && row.Length > 1 && best.Distance >= options.MatchRatio * row[1].Distance)
                continue; // Lowe ratio test：最近邻明显优于次近邻才算可靠
            if (best.QueryIdx < 0 || best.QueryIdx >= sceneKeypoints.Length) continue;
            if (best.TrainIdx < 0 || best.TrainIdx >= templateKeypoints.Length) continue;
            templatePoints.Add(new Point2d(templateKeypoints[best.TrainIdx].Pt.X, templateKeypoints[best.TrainIdx].Pt.Y));
            scenePoints.Add(new Point2d(sceneKeypoints[best.QueryIdx].Pt.X, sceneKeypoints[best.QueryIdx].Pt.Y));
        }

        var matchCount = templatePoints.Count;
        if (matchCount < options.MinMatches)
            return empty with { Reason = "too-few-matches", MatchCount = matchCount };

        using var from = Mat.FromArray(templatePoints);
        using var to = Mat.FromArray(scenePoints);
        // Mat 同时能隐式转成 InputArray 与 IEnumerable<Point2d>，显式声明 InputArray 消除 FindHomography 的重载二义性。
        InputArray fromPoints = from;
        InputArray toPoints = to;
        using var inlierMask = new Mat();
        Mat? model = null;
        try
        {
            model = options.TransformType switch
            {
                "homography" => Cv2.FindHomography(fromPoints, toPoints, HomographyMethods.Ransac,
                    options.RansacReprojThresholdPx, inlierMask, 2000, 0.995),
                "affine" => Cv2.EstimateAffine2D(fromPoints, toPoints, inlierMask, RobustEstimationAlgorithms.RANSAC,
                    options.RansacReprojThresholdPx, 2000, 0.99, 10),
                _ => Cv2.EstimateAffinePartial2D(fromPoints, toPoints, inlierMask, RobustEstimationAlgorithms.RANSAC,
                    options.RansacReprojThresholdPx, 2000, 0.99, 10)
            };
            if (model is null || model.Empty())
                return empty with { Reason = "transform-failed", MatchCount = matchCount };

            var matrix = ReadMatrix(model);
            if (matrix is null)
                return empty with { Reason = "transform-degenerate", MatchCount = matchCount };
            var m = matrix;

            var inlierScene = new List<VisionPoint2D>(matchCount);
            var inlierTemplate = new List<VisionPoint2D>(matchCount);
            var squaredError = 0d;
            for (var index = 0; index < matchCount; index++)
            {
                if (inlierMask.Empty() || inlierMask.At<byte>(index, 0) == 0) continue;
                var tp = templatePoints[index];
                var sp = scenePoints[index];
                inlierTemplate.Add(new VisionPoint2D(tp.X, tp.Y));
                inlierScene.Add(new VisionPoint2D(sp.X, sp.Y));
                var mapped = MapPoint(m, tp.X, tp.Y);
                var dx = mapped.X - sp.X;
                var dy = mapped.Y - sp.Y;
                squaredError += dx * dx + dy * dy;
            }

            var inlierCount = inlierScene.Count;
            var inlierRatio = matchCount == 0 ? 0 : inlierCount / (double)matchCount;
            var rms = inlierCount == 0 ? 0 : Math.Sqrt(squaredError / inlierCount);

            // 旋转与缩放在模板中心处取局部雅可比：单应变换下不同位置的角度/尺度本就不同，
            // 用中心点的局部值最符合"把模板 ROI 搬过去"的用途。
            var cx = templateGray.Width / 2.0;
            var cy = templateGray.Height / 2.0;
            var center = MapPoint(m, cx, cy);
            var alongX = MapPoint(m, cx + 1, cy);
            var alongY = MapPoint(m, cx, cy + 1);
            var scaleX = Math.Sqrt(Square(alongX.X - center.X) + Square(alongX.Y - center.Y));
            var scaleY = Math.Sqrt(Square(alongY.X - center.X) + Square(alongY.Y - center.Y));
            var angle = Math.Atan2(alongX.Y - center.Y, alongX.X - center.X) * 180.0 / Math.PI;
            var scale = Math.Sqrt(Math.Max(scaleX * scaleY, 1e-12));

            if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(angle) || !double.IsFinite(scale))
                return empty with { Reason = "transform-degenerate", MatchCount = matchCount, InlierCount = inlierCount };

            var corners = new[]
            {
                MapPoint(m, 0, 0), MapPoint(m, templateGray.Width, 0),
                MapPoint(m, templateGray.Width, templateGray.Height), MapPoint(m, 0, templateGray.Height)
            };
            if (corners.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                return empty with { Reason = "transform-degenerate", MatchCount = matchCount, InlierCount = inlierCount };

            var minX = corners.Min(point => point.X);
            var minY = corners.Min(point => point.Y);
            var maxX = corners.Max(point => point.X);
            var maxY = corners.Max(point => point.Y);
            var area = PolygonArea(corners);
            var perimeter = PolygonPerimeter(corners);
            var normalizedAngle = NormalizeAngle(angle);

            var outcome = new MatchOutcome(true, "none", matchCount, inlierCount, inlierRatio, rms,
                sceneKeypoints.Length, templateKeypoints.Length,
                center.X, center.Y, normalizedAngle, scale, scaleX, scaleY, center, m,
                inlierScene, inlierTemplate,
                new VisionRectangle2D(minX, minY, maxX - minX, maxY - minY), area, perimeter, corners);

            // 判定门限：先看内点数量/比例，再看角度与尺度是否落在工艺允许窗口内。
            if (inlierCount < options.MinInliers)
                return outcome with { Found = false, Reason = "too-few-inliers" };
            if (inlierRatio < options.MinInlierRatio)
                return outcome with { Found = false, Reason = "inlier-ratio-low" };
            if (options.AngleRangeDeg < 180 && Math.Abs(normalizedAngle) > options.AngleRangeDeg + 1e-9)
                return outcome with { Found = false, Reason = "angle-out-of-range" };
            var scalePercent = scale * 100.0;
            if (scalePercent < options.ScaleMinPercent - 1e-9 || scalePercent > options.ScaleMaxPercent + 1e-9)
                return outcome with { Found = false, Reason = "scale-out-of-range" };

            return outcome;
        }
        finally
        {
            model?.Dispose();
        }
    }

    /// <summary>把 2x3 仿射补成 3x3 单应，保证下游只有一种矩阵形状。</summary>
    private static double[]? ReadMatrix(Mat model)
    {
        var values = new double[9];
        values[6] = 0;
        values[7] = 0;
        values[8] = 1;
        if (model.Rows == 2 && model.Cols == 3)
        {
            for (var row = 0; row < 2; row++)
                for (var column = 0; column < 3; column++)
                    values[row * 3 + column] = model.At<double>(row, column);
        }
        else if (model.Rows == 3 && model.Cols == 3)
        {
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 3; column++)
                    values[row * 3 + column] = model.At<double>(row, column);
        }
        else
        {
            return null;
        }

        return values.Any(value => !double.IsFinite(value)) ? null : values;
    }

    private static VisionPoint2D MapPoint(double[] matrix, double x, double y)
    {
        var denominator = matrix[6] * x + matrix[7] * y + matrix[8];
        if (Math.Abs(denominator) < 1e-12)
            return new VisionPoint2D(double.NaN, double.NaN);
        return new VisionPoint2D(
            (matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
            (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator);
    }

    private static double[] Identity3x3() => [1, 0, 0, 0, 1, 0, 0, 0, 1];

    private static double Square(double value) => value * value;

    private static double NormalizeAngle(double degrees)
    {
        var normalized = degrees % 360.0;
        if (normalized <= -180.0) normalized += 360.0;
        if (normalized > 180.0) normalized -= 360.0;
        return normalized;
    }

    private static double PolygonArea(IReadOnlyList<VisionPoint2D> points)
    {
        var sum = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            sum += points[index].X * points[next].Y - points[next].X * points[index].Y;
        }
        return Math.Abs(sum) / 2;
    }

    private static double PolygonPerimeter(IReadOnlyList<VisionPoint2D> points)
    {
        var sum = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            sum += Math.Sqrt(Square(points[next].X - points[index].X) + Square(points[next].Y - points[index].Y));
        }
        return sum;
    }

    // ---------------------------------------------------------------- 输出组装

    private static IReadOnlyList<VisionDetection> BuildDetections(MatchOutcome outcome)
    {
        if (!outcome.HasGeometry) return [];

        var box = outcome.BoundingBox;
        var area = outcome.RotatedArea;
        var perimeter = outcome.RotatedPerimeter;
        var circularity = perimeter > 0 && area > 0 ? 4 * Math.PI * area / (perimeter * perimeter) : 0;
        var centreX = box.X + box.Width / 2;
        var centreY = box.Y + box.Height / 2;
        return
        [
            new VisionDetection(
                0, box.X, box.Y, box.Width, box.Height, area, perimeter, circularity,
                new VisionCircle(centreX, centreY, Math.Sqrt(box.Width * box.Width + box.Height * box.Height) / 2),
                box,
                outcome.Found ? outcome.InlierRatio : null,
                null)
        ];
    }

    /// <summary>叠加图：内点（绿点）+ 模板投影四边形（绿框）+ 中心十字（黄）+ 一行判定文本。</summary>
    private static VisionImage BuildOverlayImage(Mat scene, MatchOutcome outcome, CancellationToken cancellationToken)
    {
        Mat? overlay = new Mat();
        try
        {
            switch (scene.Channels())
            {
                case 1: Cv2.CvtColor(scene, overlay, ColorConversionCodes.GRAY2BGR); break;
                case 4: Cv2.CvtColor(scene, overlay, ColorConversionCodes.BGRA2BGR); break;
                default: scene.CopyTo(overlay); break;
            }

            var drawn = 0;
            foreach (var point in outcome.InlierScenePoints)
            {
                if (drawn++ >= MaxOverlayInliers) break;
                if (drawn % 32 == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
                Cv2.Circle(overlay, new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)), 2, Scalar.LimeGreen, -1);
            }

            if (outcome.HasGeometry && outcome.Quad.Count >= 4)
            {
                // 画真实投影四边形（不是外接矩形）：旋转来料用外接框会看不出真实朝向。
                var quad = outcome.Quad
                    .Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                    .ToArray();
                Cv2.Polylines(overlay, new[] { quad }, true, Scalar.LimeGreen, 2);
                if (double.IsFinite(outcome.Center.X) && double.IsFinite(outcome.Center.Y))
                {
                    var cx = (int)Math.Round(outcome.Center.X);
                    var cy = (int)Math.Round(outcome.Center.Y);
                    Cv2.DrawMarker(overlay, new Point(cx, cy), Scalar.Yellow, MarkerTypes.Cross, 12, 1);
                    var label = FormattableString.Invariant(
                        $"{(outcome.Found ? "OK" : "NG")} inl={outcome.InlierCount}/{outcome.MatchCount} a={outcome.AngleDeg:0.0} s={outcome.Scale:0.000}");
                    Cv2.PutText(overlay, label, new Point(Math.Max(0, cx - 90), Math.Max(12, cy - 12)),
                        HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
                }
            }

            var image = VisionImage.Own(overlay);
            overlay = null;
            return image;
        }
        catch
        {
            overlay?.Dispose();
            throw;
        }
    }
}
