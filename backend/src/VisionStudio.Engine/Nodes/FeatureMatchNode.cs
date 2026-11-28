using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using static VisionStudio.Engine.Algorithms.FeatureMatchingAlgorithms;

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
public sealed class FeatureMatchNode : IVisionNodeExecutor, IVisionNodeDefinitionValidator
{
    public string Type => "match.feature";

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
        var (template, templateSource, templateAssetId, templateAssetVersion, templateWidth, templateHeight, templateFingerprint) = AcquireTemplateGray(context, node);
        using var templateGray = template;
        var expectedWidth = node.GetInt("templateAssetWidth", 0);
        var expectedHeight = node.GetInt("templateAssetHeight", 0);
        if (expectedWidth < 0 || expectedHeight < 0 || expectedWidth > 16384 || expectedHeight > 16384)
            throw new InvalidOperationException("match.feature templateAssetWidth/templateAssetHeight must be within 0..16384 (0 disables the check).");
        if (expectedWidth > 0 && expectedWidth != templateWidth || expectedHeight > 0 && expectedHeight != templateHeight)
            throw new InvalidOperationException(
                $"match.feature template size {templateWidth}x{templateHeight} does not match pinned {expectedWidth}x{expectedHeight}; refresh the template metadata or clear the size pins.");

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
        {
            var noFeatureImage = VisionImage.Own(scene.Mat.Clone());
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(
                noFeatureImage, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type,
                    ("templateSource", templateSource), ("templateAssetId", templateAssetId),
                    ("templateAssetVersion", templateAssetVersion), ("found", false),
                    ("reason", "feature-insufficient:no-template-keypoints"),
                    ("templateFingerprint", templateFingerprint));
                return new NodeExecutorResult(new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(noFeatureImage),
                    ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0),
                    ["matchCount"] = VisionValue.Integer(0), ["inlierCount"] = VisionValue.Integer(0),
                    ["inlierRatio"] = VisionValue.Double(0), ["reprojErrorPx"] = VisionValue.Double(0),
                    ["templateKeypoints"] = VisionValue.Integer(0), ["sceneKeypoints"] = VisionValue.Integer(sceneKeypoints.Length),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                }, new Dictionary<string, object?>
                {
                    ["found"] = false, ["reason"] = "feature-insufficient:no-template-keypoints",
                    ["templateFingerprint"] = templateFingerprint, ["diagnostics"] = diagnostics
                });
            }, cancellationToken));
        }

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
                ("templateSource", templateSource),
                ("templateAssetId", templateAssetId),
                ("templateAssetVersion", templateAssetVersion),
                ("templateWidth", templateWidth), ("templateHeight", templateHeight),
                ("templateFingerprint", templateFingerprint),
                ("templatePriority", "input-port>templateAssetId>templatePath"),
                ("acceptanceGate", $"inliers>={options.MinInliers};ratio>={options.MinInlierRatio};rms<={options.MaxReprojErrorPx}"),
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
                    scaleY = outcome.ScaleY,
                    templateAssetId = templateAssetId,
                    templateAssetVersion = templateAssetVersion,
                    templateWidth = templateWidth,
                    templateHeight = templateHeight,
                    templateFingerprint = templateFingerprint
                }));
                outputs["inlierPoints"] = VisionValue.PointList(outcome.InlierScenePoints);
                outputs["detections"] = VisionValue.Detections(BuildDetections(outcome));
            }

            return new NodeExecutorResult(outputs, new Dictionary<string, object?>
            {
                ["detector"] = options.Detector,
                ["templateSource"] = templateSource,
                ["templateAssetId"] = templateAssetId,
                ["templateAssetVersion"] = templateAssetVersion,
                ["templateWidth"] = templateWidth, ["templateHeight"] = templateHeight,
                ["templateFingerprint"] = templateFingerprint,
                ["templatePriority"] = "input-port>templateAssetId>templatePath",
                ["acceptanceGate"] = $"inliers>={options.MinInliers};ratio>={options.MinInlierRatio};rms<={options.MaxReprojErrorPx}",
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

        var maxReprojErrorPx = node.GetDouble("maxReprojErrorPx", 5);
        if (!double.IsFinite(maxReprojErrorPx) || maxReprojErrorPx is < 0 or > 100)
            throw new InvalidOperationException("match.feature maxReprojErrorPx must be finite and within 0..100 (0 disables the RMS gate).");

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
            ransacReprojThresholdPx, minMatches, minInliers, minInlierRatio, maxReprojErrorPx, angleRangeDeg,
            scaleMinPercent, scaleMaxPercent, sceneRoi, templateRoi);
    }

    // ---------------------------------------------------------------- 图像准备

    public void ValidateDefinition(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputs)
    {
        if (!connectedInputs.Contains("template") && !string.IsNullOrWhiteSpace(node.GetString("templateAssetId", "")))
            _ = ReadPinnedTemplateVersion(node);
    }

    private static int ReadPinnedTemplateVersion(NodeDefinition node)
    {
        var version = node.GetInt("templateAssetVersion", 0);
        if (version <= 0)
            throw new InvalidOperationException("match.feature requires a positive templateAssetVersion when using a template asset.");
        return version;
    }

    private static (Mat Image, string Source, string? AssetId, int? AssetVersion, int Width, int Height, string Fingerprint) AcquireTemplateGray(NodeExecutionContext context, NodeDefinition node)
    {
        Mat gray;
        string source;
        string? assetId = null;
        int? assetVersion = null;
        if (context.TryGetValue("template", out _))
        {
            // RequireImage 同时兼容引擎自有 VisionImage 与插件传入的 IVisionImage（借出 Mat）。
            var template = context.RequireImage("template");
            gray = ToGray8(template.Mat, "template");
            source = "input-port";
        }
        else if (!string.IsNullOrWhiteSpace(node.GetString("templateAssetId", "")))
        {
            assetId = node.GetString("templateAssetId", "").Trim();
            assetVersion = ReadPinnedTemplateVersion(node);
            using var assetImage = MatchingTemplateAsset.Load(context, node, assetId, assetVersion.Value, ImreadModes.Unchanged);
            gray = ToGray8(assetImage, "template");
            source = "asset";
        }
        else
        {
            var path = node.GetString("templatePath", string.Empty).Trim();
            if (path.Length == 0)
                throw new InvalidOperationException("match.feature requires a connected template image or templatePath.");
            using var fileImage = Cv2.ImRead(System.IO.Path.GetFullPath(path), ImreadModes.Grayscale);
            if (fileImage.Empty())
                throw new InvalidOperationException($"match.feature could not read templatePath '{path}'.");
            gray = ToGray8(fileImage, "template");
            source = "file";
        }

        var width = gray.Width;
        var height = gray.Height;
        var bytes = new byte[checked((int)(gray.Total() * gray.ElemSize()))];
        Marshal.Copy(gray.Data, bytes, 0, bytes.Length);
        var identity = source == "asset" ? $"{source}:{assetId}:v{assetVersion}:{width}x{height}" : $"{source}:{width}x{height}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + ":" + Convert.ToHexString(SHA256.HashData(bytes)))));
        return (gray, source, assetId, assetVersion, width, height, fingerprint);
    }

    // ---------------------------------------------------------------- 匹配

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


}
