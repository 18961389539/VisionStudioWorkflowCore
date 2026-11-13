using JLVisionLib;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Algorithms;

internal static partial class JLVisionMatching
{
    // 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
    // 本文件只保留入口与编排（Execute）；其余按域在同名 partial 里：
    //   Types 类型与常量 / Training + TrainingFusion 训练 / Search 搜索 /
    //   Model 序列化与训练指纹 / Roi 掩码与软忽略 / Scoring 打分与算子包装 /
    //   Parameters 参数解析 / Orchestration 搜索预处理、候选收集、score map。
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, bool shape, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty()) throw new InvalidOperationException("Matching requires a non-empty scene image.");
        using var scene = JLVisionImages.FromMat(source.Mat);
        using var channels = scene.CountChannels();
        using var gray = channels.I == 1 ? scene.CopyImage() : scene.Rgb1ToGray();
        Mat? fileImage = null;
        Mat? savedModelPlaceholder = null;
        JlImage? searchScene = null;
        JlImage? ignoreReducedScene = null;
        JlImage? softIgnoreScene = null;
        try
        {
            var fromPort = context.TryGetValue("template", out _);
            var legacyMode = shape && node.GetString("modelMode", string.Empty).Equals("legacy", StringComparison.OrdinalIgnoreCase);
            var savedModel = shape && !legacyMode ? node.GetString("trainedModelData", string.Empty).Trim() : string.Empty;
            // P0：modelAssetId 优先于内联模型。资产缺失/哈希不符必须失败——静默退回内联等于偷偷换模型。
            var modelSource = savedModel.Length > 0 ? "workflow-asset" : string.Empty;
            var assetDisabledFallback = false;
            if (shape && !legacyMode)
            {
                var assetId = node.GetString("modelAssetId", string.Empty).Trim();
                if (assetId.Length > 0)
                {
                    var assetVersion = node.GetInt("modelAssetVersion", 0);
                    if (assetVersion < 0)
                        throw new InvalidOperationException($"Shape node '{node.Id}' modelAssetVersion must be >= 0 (0 means the asset's latest version).");
                    var assetSource = context.ModelAssets
                        ?? throw new InvalidOperationException(
                            $"Shape node '{node.Id}' references model asset '{assetId}', but this host did not provide a model asset source. " +
                            "Run the node through a host that registers IModelAssetSource, or clear modelAssetId to use the inline trained model.");
                    if (!assetSource.IsEnabled)
                    {
                        // 运维回滚开关：显式关闭时用内联模型并标注，不当作"资产缺失"。
                        if (savedModel.Length == 0)
                            throw new InvalidOperationException(
                                $"Shape node '{node.Id}' has modelAssets disabled (ModelAssets:Enabled=false) and no inline trained model. " +
                                "Enable model assets or restore an inline model before running.");
                        modelSource = "inline-fallback";
                        assetDisabledFallback = true;
                    }
                    else
                    {
                    var assetBytes = assetSource.TryRead(assetId, assetVersion)
                        ?? throw new InvalidOperationException(
                            $"Shape node '{node.Id}' references model asset '{assetId}' v{(assetVersion == 0 ? "latest" : assetVersion.ToString())}, " +
                            "which does not exist or was deleted. Repoint the node or restore the asset.");
                    if (assetBytes.Length == 0)
                        throw new InvalidOperationException($"Shape node '{node.Id}' model asset '{assetId}' contains no model bytes.");
                    if (assetBytes.Length > 16 * 1024 * 1024)
                        throw new InvalidOperationException($"Shape node '{node.Id}' model asset '{assetId}' is {assetBytes.Length} bytes, above the 16 MB limit.");
                    var assetSha = Convert.ToHexString(SHA256.HashData(assetBytes));
                    var expectedSha = node.GetString("modelAssetSha256", string.Empty).Trim();
                    if (expectedSha.Length > 0 && !string.Equals(expectedSha, assetSha, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Shape node '{node.Id}' model asset '{assetId}' hash mismatch: pinned {expectedSha}, loaded {assetSha}. " +
                            "The asset bytes changed; repin the node or restore the expected version.");
                    savedModel = Convert.ToBase64String(assetBytes);
                    modelSource = "model-asset";
                    }
                }
            }
            if (shape && node.GetBool("searchPreviewOnly", false))
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source.CloneOwned()), ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0), ["scale"] = VisionValue.Double(1), ["count"] = VisionValue.Integer(0) },
                    new Dictionary<string, object?> { ["searchPreviewReady"] = true, ["sceneWidth"] = source.Width, ["sceneHeight"] = source.Height }, []);
            if (shape && node.GetBool("trainingMode", false))
                return TrainShape(context, node, source, fromPort, token);
            if (shape && savedModel.Length == 0 && node.GetString("modelMode", "legacy").Equals("trained", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This shape node requires a trained model. Select a training image and train the model before running.");

            if (shape && savedModel.Length > 0)
            {
                var trainingConfig = ShapeTrainingConfig(node);
                var savedConfig = node.GetString("trainedModelConfig", string.Empty);
                if (!string.Equals(trainingConfig, savedConfig, StringComparison.Ordinal) && !IsCompatibleLegacyShapeConfig(node, savedConfig))
                    throw new InvalidOperationException("Shape model training parameters changed. Train the model again before matching.");
            }
            Mat templateBuffer;
            if (shape && savedModel.Length > 0) templateBuffer = savedModelPlaceholder = new Mat(1, 1, MatType.CV_8UC1); // geometry metadata is restored below
            else if (fromPort) templateBuffer = context.RequireImage("template").Mat;
            else if (!string.IsNullOrWhiteSpace(node.GetString("templateAssetId", string.Empty)))
            {
                // 模板图资产：优先于 templatePath；端口连接时仍以端口为准（动态图像优先于静态资产）。
                var templateAssetId = node.GetString("templateAssetId", string.Empty).Trim();
                var templateAssetVersion = node.GetInt("templateAssetVersion", 0);
                if (templateAssetVersion < 0)
                    throw new InvalidOperationException($"Match node '{node.Id}' templateAssetVersion must be >= 0 (0 means latest).");
                var assetSource = context.ModelAssets
                    ?? throw new InvalidOperationException(
                        $"Match node '{node.Id}' references template asset '{templateAssetId}', but this host did not provide an asset source. " +
                        "Run through a host that registers the asset source, or clear templateAssetId to use Template File.");
                var templateBytes = assetSource.TryRead(templateAssetId, templateAssetVersion)
                    ?? throw new InvalidOperationException(
                        $"Match node '{node.Id}' references template asset '{templateAssetId}' v{(templateAssetVersion == 0 ? "latest" : templateAssetVersion.ToString())}, " +
                        "which does not exist or was deleted. Repoint the node or restore the asset.");
                fileImage = Cv2.ImDecode(templateBytes, ImreadModes.Grayscale);
                if (fileImage.Empty())
                {
                    var length = templateBytes.Length;
                    fileImage.Dispose();
                    throw new InvalidOperationException($"Match node '{node.Id}' template asset '{templateAssetId}' ({length} bytes) could not be decoded as an image.");
                }
                templateBuffer = fileImage;
            }
            else
            {
                var path = node.GetString("templatePath", string.Empty).Trim();
                if (path.Length == 0) throw new InvalidOperationException("Matching requires a connected template or templatePath.");
                fileImage = Cv2.ImRead(Path.GetFullPath(path), ImreadModes.Grayscale); templateBuffer = fileImage;
                if (fileImage.Empty()) throw new InvalidOperationException($"Template image '{Path.GetFullPath(path)}' could not be loaded.");
            }
            if (templateBuffer.Empty()) throw new InvalidOperationException("Matching requires a non-empty template image.");
            if (shape && savedModel.Length == 0 && (templateBuffer.Width > source.Width || templateBuffer.Height > source.Height)) throw new InvalidOperationException("Template is larger than the scene image.");
            using var templateColor = JLVisionImages.FromMat(templateBuffer);
            using var templateChannels = templateColor.CountChannels();
            using var template = templateChannels.I == 1 ? templateColor.CopyImage() : templateColor.Rgb1ToGray();
            var p = ParseMatchParameters(context, node, shape, savedModel.Length > 0, templateBuffer);
            using var contrastSceneImage = p.ContrastEnabled ? JLVisionImages.ToMat(gray) : null;
            var templateSource = shape && savedModel.Length > 0 ? "trained-model" : fromPort ? "input-port" : "file";
            var (outputs, summary) = BuildInitialOutputs(p, shape, savedModel.Length > 0, templateSource);
            var overlays = new List<VisionOverlay>();
            var detections = new List<VisionDetection>();
            var modelSet = shape ? DeserializeShapeModelSet(savedModel) : null;
            if (p.ContrastEnabled && modelSet is { Models.Length: > 0 })
                throw new InvalidOperationException("Runtime contrast checks require one fused native shape model; retrain this legacy model set as a composite model first.");
            if (p.ClutterEnabled && modelSet is { Models.Length: > 0 })
                throw new InvalidOperationException("Clutter scoring requires one fused native shape model; retrain this legacy model set as a composite model first.");
            var prepared = PrepareSearchScene(node, p, shape, savedModel.Length > 0, modelSet, templateBuffer, gray, source, outputs, summary, overlays, token);
            if (prepared.EarlyResult is { } emptyRegion) return emptyRegion;
            searchScene = prepared.SearchScene;
            ignoreReducedScene = prepared.IgnoreReducedScene;
            softIgnoreScene = prepared.SoftIgnoreScene;
            var matchingScene = prepared.MatchingScene;
            var collected = CollectCandidates(context, node, p, shape, savedModel, modelSource, assetDisabledFallback, modelSet,
                template, templateBuffer, matchingScene, source, prepared.SearchMatchLimit, prepared.TemplateBox, prepared.OriginX, prepared.OriginY,
                outputs, summary, overlays, token);
            if (collected.EarlyResult is { } contourless) return contourless;
            var matchedCandidates = collected.Candidates;
            var nmsAccepted = new List<(VisionRectangle2D Box, double Cx, double Cy, double AngleDeg)>();
            var nmsSuppressedCount = 0;
            var nmsSoftDecayedCount = 0;
            IEnumerable<ShapeMatchCandidate> rankedCandidates = matchedCandidates.OrderByDescending(candidate => CandidateScore(candidate, p.ScoreUsingClutter));
            if (!shape)
            {
                rankedCandidates = p.NmsSortKey switch
                {
                    "scorethenabsangle" => matchedCandidates
                        .OrderByDescending(candidate => CandidateScore(candidate, p.ScoreUsingClutter))
                        .ThenBy(candidate => Math.Abs(VisionTransform2D.NormalizeAngleDeg(candidate.Angle * 180 / Math.PI))),
                    "scorethenscale" => matchedCandidates
                        .OrderByDescending(candidate => CandidateScore(candidate, p.ScoreUsingClutter))
                        .ThenBy(candidate => Math.Abs(candidate.Scale - 1d)),
                    _ => matchedCandidates
                        .OrderByDescending(candidate => CandidateScore(candidate, p.ScoreUsingClutter))
                        .ThenBy(candidate => candidate.Index)
                };
            }
            var searchRoi = p.SearchRoi;
            var ignoreRoi = p.IgnoreRoi;
            foreach (var candidate in rankedCandidates)
            {
                token.ThrowIfCancellationRequested();
                var x = candidate.X + prepared.SearchOffsetX; var y = candidate.Y + prepared.SearchOffsetY;
                var radians = candidate.Angle; var scale = candidate.Scale; var nativeScore = candidate.Score;
                var score = CandidateScore(candidate, p.ScoreUsingClutter);
                if (!shape && ignoreRoi is not null && p.IgnoreMode == "soft")
                {
                    var softW = SampleIgnoreWeight(ignoreRoi, x, y, p.IgnoreSoftWeight, p.IgnoreFeatherPx);
                    score *= softW;
                    if (score < p.MinScore) continue;
                }
                var poseOffsetX = shape && savedModel.Length > 0 ? node.GetDouble("trainedPoseOffsetX", 0) : 0;
                var poseOffsetY = shape && savedModel.Length > 0 ? node.GetDouble("trainedPoseOffsetY", 0) : 0;
                if (poseOffsetX != 0 || poseOffsetY != 0)
                {
                    var correctionCos = Math.Cos(radians); var correctionSin = Math.Sin(radians);
                    x -= correctionCos * candidate.ScaleX * poseOffsetX + correctionSin * candidate.ScaleY * poseOffsetY;
                    y -= -correctionSin * candidate.ScaleX * poseOffsetX + correctionCos * candidate.ScaleY * poseOffsetY;
                }
                var candidateBox = new Rect(0, 0, candidate.Width, candidate.Height);
                // Some native builds return poses outside the requested angular window. Keep the
                // runtime contract exact even when the matcher supplies such a candidate.
                if ((!shape || savedModel.Length > 0) && (radians < p.SearchStart - 1e-9 || radians > p.SearchEnd + 1e-9)) continue;
                if (shape && savedModel.Length > 0 && (candidate.ScaleX < p.SearchScaleXMin - 1e-9 || candidate.ScaleX > p.SearchScaleXMax + 1e-9
                    || candidate.ScaleY < p.SearchScaleYMin - 1e-9 || candidate.ScaleY > p.SearchScaleYMax + 1e-9)) continue;
                if (searchRoi is not null && !Contains(searchRoi, x, y)) continue;
                if (!shape && ignoreRoi is not null && p.IgnoreMode == "hard" && Contains(ignoreRoi, x, y)) continue;
                double? contrast = null;
                double? contrastFraction = null;
                if (p.ContrastEnabled)
                {
                    var reference = candidate.ContrastReference
                        ?? throw new InvalidOperationException("The shape model has no contrast reference points.");
                    contrast = MeasureCandidateContrast(contrastSceneImage!, reference,
                        candidate.X + prepared.SearchOffsetX, candidate.Y + prepared.SearchOffsetY, radians, candidate.ScaleX, candidate.ScaleY);
                    if (contrast.Value < p.RuntimeContrastThreshold) continue;
                    if (p.ContrastFractionThreshold > 0)
                    {
                        contrastFraction = Math.Clamp(contrast.Value / reference.TrainingMeanContrast, 0, 1);
                        if (contrastFraction.Value < p.ContrastFractionThreshold) continue;
                    }
                }
                // Soft-NMS must see candidates below the final gate so decay can drop them;
                // hard NMS / shape keep the early reject for speed.
                if (score < p.MinScore && (shape || p.NmsMode != "soft")) continue;
                var cos = Math.Cos(radians); var sin = Math.Sin(radians);
                var corners = new[] { (candidateBox.Left, candidateBox.Top), (candidateBox.Right - 1, candidateBox.Top), (candidateBox.Right - 1, candidateBox.Bottom - 1), (candidateBox.Left, candidateBox.Bottom - 1) }
                    .Select(pt => new VisionPoint2D(x + cos * candidate.ScaleX * (pt.Item1 - candidate.OriginX) + sin * candidate.ScaleY * (pt.Item2 - candidate.OriginY),
                        y - sin * candidate.ScaleX * (pt.Item1 - candidate.OriginX) + cos * candidate.ScaleY * (pt.Item2 - candidate.OriginY))).ToArray();
                var left = corners.Min(pt => pt.X); var top = corners.Min(pt => pt.Y); var width = corners.Max(pt => pt.X) - left + 1; var height = corners.Max(pt => pt.Y) - top + 1;
                if (shape && candidateBox.Width * candidateBox.Height * candidate.ScaleX * candidate.ScaleY < p.MinArea) continue;
                var distance = Math.Max(0, node.GetDouble("minCenterDistance", shape ? 20 : 0));
                if (distance > 0 && detections.Any(d => Math.Sqrt(Math.Pow(d.Circle.X - x, 2) + Math.Pow(d.Circle.Y - y, 2)) < distance)) continue;
                if (shape && detections.Any(d => Overlap(d.BoundingBox, new(left, top, width, height)) > .3)) continue;
                var degrees = VisionTransform2D.NormalizeAngleDeg(radians * 180 / Math.PI);
                if (!shape && p.DuplicateOverlapThreshold > 0)
                {
                    var candidateBox2d = new VisionRectangle2D(left, top, width, height);
                    var softDecayed = false;
                    var hardReject = false;
                    foreach (var prior in nmsAccepted)
                    {
                        var iou = Overlap(prior.Box, candidateBox2d);
                        if (iou < p.DuplicateOverlapThreshold) continue;
                        if (p.NmsAngleToleranceDeg > 0)
                        {
                            var delta = Math.Abs(VisionTransform2D.NormalizeAngleDeg(degrees - prior.AngleDeg));
                            delta = Math.Min(delta, 360d - delta);
                            if (delta > p.NmsAngleToleranceDeg) continue; // different orientation — keep both
                        }
                        if (p.NmsMode == "soft")
                        {
                            score *= Math.Max(0d, 1d - iou); // linear Soft-NMS
                            softDecayed = true;
                        }
                        else
                        {
                            hardReject = true;
                            break;
                        }
                    }
                    if (hardReject)
                    {
                        nmsSuppressedCount++;
                        continue;
                    }
                    if (softDecayed)
                    {
                        nmsSoftDecayedCount++;
                        if (score < p.MinScore)
                        {
                            nmsSuppressedCount++;
                            continue;
                        }
                    }
                }
                if (!shape && p.NmsMode == "soft" && score < p.MinScore) continue;
                if (detections.Count >= p.AcceptLimit) continue;
                if (!outputs["found"].Require<bool>("found"))
                {
                    outputs["found"] = VisionValue.Boolean(true); outputs["score"] = VisionValue.Double(score);
                    if (shape)
                    {
                        outputs["scale"] = VisionValue.Double(scale);
                        outputs["scaleX"] = VisionValue.Double(candidate.ScaleX);
                        outputs["scaleY"] = VisionValue.Double(candidate.ScaleY);
                        if (p.ClutterEnabled) outputs["clutterScore"] = VisionValue.Double(candidate.ClutterScore ?? 0);
                        if (p.ScoreUsingClutter) outputs["nativeScore"] = VisionValue.Double(nativeScore);
                        if (contrast is { } measuredContrast) outputs["contrast"] = VisionValue.Double(measuredContrast);
                        if (contrastFraction is { } measuredFraction) outputs["contrastFraction"] = VisionValue.Double(measuredFraction);
                    }
                    if (!shape)
                    {
                        outputs["scale"] = VisionValue.Double(scale);
                        summary["matchedScale"] = scale;
                    }
                    outputs["x"] = VisionValue.Double(shape ? x : left); outputs["y"] = VisionValue.Double(shape ? y : top);
                    outputs["angle"] = VisionValue.Double(degrees); outputs["center"] = VisionValue.Point(shape ? new(x, y) : new(left + width / 2, top + height / 2));
                    outputs["rectangle"] = VisionValue.Rectangle(new(shape ? x : left, shape ? y : top, width, height, shape ? -degrees : degrees));
                    if (shape) outputs["pose"] = VisionValue.Pose(new(x, y, degrees));
                    summary["found"] = true; summary["bestScore"] = score; summary["score"] = score; summary["matchedAngle"] = degrees; summary["angle"] = degrees;
                    if (shape)
                    {
                        summary["matchedScale"] = scale;
                        summary["matchedScaleX"] = candidate.ScaleX;
                        summary["matchedScaleY"] = candidate.ScaleY;
                        if (p.ClutterEnabled) summary["matchedClutterScore"] = candidate.ClutterScore;
                        if (p.ScoreUsingClutter) summary["matchedNativeScore"] = nativeScore;
                        if (contrast is { } measuredContrast) summary["matchedContrast"] = measuredContrast;
                        if (contrastFraction is { } measuredFraction) summary["matchedContrastFraction"] = measuredFraction;
                    }
                }
                var box = new VisionRectangle2D(left, top, width, height);
                detections.Add(new(detections.Count, left, top, width, height, candidateBox.Width * candidateBox.Height * candidate.ScaleX * candidate.ScaleY, 2 * (width + height), 0, new(x, y, Math.Sqrt(width * width + height * height) / 2), box, score));
                if (!shape) nmsAccepted.Add((box, x, y, degrees));
                var scaleLabel = p.AnisotropicScale ? $" ×{candidate.ScaleX:0.###}/{candidate.ScaleY:0.###}" : $" ×{scale:0.###}";
                var clutterLabel = p.ClutterEnabled ? $" c={candidate.ClutterScore ?? 0:0.###}" : string.Empty;
                var templateLabel = Math.Abs(scale - 1d) < 1e-6 ? $"s={score:0.###} a={degrees:0.#}" : $"s={score:0.###} a={degrees:0.#} ×{scale:0.###}";
                overlays.Add(new($"match-{candidate.Index}", VisionOverlayType.Polygon, Points: corners.Select(pt => new VisionOverlayPoint(pt.X, pt.Y)).ToArray(), Label: shape ? $"s={score:0.###} a={degrees:0.#}{scaleLabel}{clutterLabel}" : templateLabel, Stroke: "#52c41a", StrokeWidth: 2));
            }
            token.ThrowIfCancellationRequested();
            if (shape) outputs["image"] = VisionValue.Image(source.CloneOwned());
            EmitScoreMap(context, node, p, shape, gray, templateBuffer, source, outputs, summary, token);
            outputs["count"] = VisionValue.Integer(detections.Count);
            outputs["detections"] = VisionValue.Detections(detections);
            summary["accepted"] = detections.Count;
            if (!shape)
            {
                summary["nmsSuppressedCount"] = nmsSuppressedCount;
                summary["nmsSoftDecayedCount"] = nmsSoftDecayedCount;
            }
            return new(outputs, summary, overlays);
        }
        finally
        {
            softIgnoreScene?.Dispose();
            ignoreReducedScene?.Dispose();
            searchScene?.Dispose();
            fileImage?.Dispose();
            savedModelPlaceholder?.Dispose();
        }
    }
}
