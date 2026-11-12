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
    //   Model 序列化与训练指纹 / Roi 掩码与软忽略 / Scoring 打分与算子包装。
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
            var range = Math.Clamp(node.GetDouble("angleRangeDeg", shape ? 180 : 0), 0, 180) * Math.PI / 180;
            var trainingRange = Math.Clamp(node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)), 0, 180) * Math.PI / 180;
            var shapePyramidLevels = shape ? node.GetInt("pyramidLevels", 0) : 0;
            if (shapePyramidLevels is < 0 or > 8) throw new InvalidOperationException("pyramidLevels must be between 0 (automatic) and 8.");
            var searchStart = -range;
            var searchEnd = range;
            if (shape && savedModel.Length > 0)
            {
                var requestedStart = Math.Clamp(node.GetDouble("searchAngleStartDeg", -180), -180, 180) * Math.PI / 180;
                var requestedEnd = Math.Clamp(node.GetDouble("searchAngleEndDeg", 180), -180, 180) * Math.PI / 180;
                searchStart = Math.Max(-trainingRange, requestedStart);
                searchEnd = Math.Min(trainingRange, requestedEnd);
                if (requestedStart > requestedEnd || searchStart > searchEnd)
                    throw new InvalidOperationException($"Search angle window [{requestedStart * 180 / Math.PI:0.#}°, {requestedEnd * 180 / Math.PI:0.#}°] does not overlap the model training coverage of ±{trainingRange * 180 / Math.PI:0.#}°.");
            }
            else if (!shape)
            {
                // Asymmetric NCC window (HALCON find_ncc_model style).
                // Catalog defaults for start/end are 0 (no rotation). When both stay at 0 and
                // angleRangeDeg > 0, expand to the legacy symmetric +/- range so older workflows
                // and forms that only bump Angle Range keep working. Set Start/End explicitly
                // for one-sided windows; set Angle Range to 0 when you truly want only 0 deg.
                var angleRangeDeg = range * 180 / Math.PI;
                var startDeg = Math.Clamp(node.GetDouble("searchAngleStartDeg", -angleRangeDeg), -180, 180);
                var endDeg = Math.Clamp(node.GetDouble("searchAngleEndDeg", angleRangeDeg), -180, 180);
                if (Math.Abs(startDeg) < 1e-12 && Math.Abs(endDeg) < 1e-12 && angleRangeDeg > 1e-12)
                {
                    startDeg = -angleRangeDeg;
                    endDeg = angleRangeDeg;
                }
                if (startDeg > endDeg)
                    throw new InvalidOperationException($"Search angle window [{startDeg:0.#}°, {endDeg:0.#}°] is empty (searchAngleStartDeg must be ≤ searchAngleEndDeg).");
                searchStart = startDeg * Math.PI / 180;
                searchEnd = endDeg * Math.PI / 180;
            }

            var step = Math.Clamp(node.GetDouble("angleStepDeg", shape ? 1 : 5), .1, 30) * Math.PI / 180;
            var scaleTolerance = shape ? Math.Clamp(node.GetDouble("scaleTolerancePercent", 0), 0, 50) / 100d : 0d;
            var scaleToleranceX = shape ? Math.Clamp(node.GetDouble("scaleToleranceXPercent", 0), 0, 50) / 100d : 0d;
            var scaleToleranceY = shape ? Math.Clamp(node.GetDouble("scaleToleranceYPercent", 0), 0, 50) / 100d : 0d;
            var anisotropicScale = scaleToleranceX > 0 || scaleToleranceY > 0;
            var scaleMin = 1 - scaleTolerance;
            var scaleMax = 1 + scaleTolerance;
            var trainingScaleXMin = anisotropicScale ? 1 - scaleToleranceX : scaleMin;
            var trainingScaleXMax = anisotropicScale ? 1 + scaleToleranceX : scaleMax;
            var trainingScaleYMin = anisotropicScale ? 1 - scaleToleranceY : scaleMin;
            var trainingScaleYMax = anisotropicScale ? 1 + scaleToleranceY : scaleMax;
            var searchScaleXMin = trainingScaleXMin;
            var searchScaleXMax = trainingScaleXMax;
            var searchScaleYMin = trainingScaleYMin;
            var searchScaleYMax = trainingScaleYMax;
            var searchScaleMin = Math.Min(searchScaleXMin, searchScaleYMin);
            var searchScaleMax = Math.Max(searchScaleXMax, searchScaleYMax);
            if (shape && savedModel.Length > 0)
            {
                var requestedScaleMin = Math.Clamp(node.GetDouble("searchScaleMinPercent", 50), 50, 150) / 100d;
                var requestedScaleMax = Math.Clamp(node.GetDouble("searchScaleMaxPercent", 150), 50, 150) / 100d;
                searchScaleXMin = Math.Max(trainingScaleXMin, requestedScaleMin);
                searchScaleXMax = Math.Min(trainingScaleXMax, requestedScaleMax);
                searchScaleYMin = Math.Max(trainingScaleYMin, requestedScaleMin);
                searchScaleYMax = Math.Min(trainingScaleYMax, requestedScaleMax);
                if (requestedScaleMin > requestedScaleMax || searchScaleXMin > searchScaleXMax || searchScaleYMin > searchScaleYMax)
                    throw new InvalidOperationException($"Search scale window [{requestedScaleMin * 100:0.#}%, {requestedScaleMax * 100:0.#}%] does not overlap the model training X [{trainingScaleXMin * 100:0.#}%, {trainingScaleXMax * 100:0.#}%] and Y [{trainingScaleYMin * 100:0.#}%, {trainingScaleYMax * 100:0.#}%] coverage.");
                searchScaleMin = Math.Min(searchScaleXMin, searchScaleYMin);
                searchScaleMax = Math.Max(searchScaleXMax, searchScaleYMax);
            }
            var scaleStep = shape ? Math.Clamp(node.GetDouble("scaleStepPercent", 1), .1, 10) / 100d : 0d;
            var clutterRoi = shape ? node.GetRoi("clutterRoi") : null;
            var clutterEnabled = clutterRoi is not null;
            var ignorePolarity = node.GetBool("ignorePolarity", false);
            var minContrast = shape ? Math.Clamp(node.GetInt("minContrast", 5), 1, 255) : 5;
            var clutterContrast = shape ? Math.Clamp(node.GetInt("clutterContrast", minContrast), 1, 255) : 5;
            if (clutterEnabled && clutterContrast < minContrast)
                throw new InvalidOperationException("Clutter Contrast must be at least the Minimum Feature Contrast used to train the model.");
            var minScore = Math.Clamp(node.GetDouble("minScore", shape ? .6 : .5), 0, 1);
            var method = node.GetString("method", "ccoeff-normed").Trim().ToLowerInvariant();
            if (!shape && method is not ("ccoeff-normed" or "sqdiff-normed")) throw new InvalidOperationException($"Unsupported template matching method '{method}'.");
            var squaredDifference = !shape && method == "sqdiff-normed";
            var acceptLimit = Math.Clamp(node.GetInt("maxTargets", shape ? 5 : 1), 1, shape ? 100 : 50);
            var maximum = !shape && squaredDifference ? 100 : acceptLimit;
            var duplicateOverlapThreshold = shape ? 0d : node.GetDouble("duplicateOverlapThreshold", 0.3);
            if (!shape && (!double.IsFinite(duplicateOverlapThreshold) || duplicateOverlapThreshold < 0 || duplicateOverlapThreshold > 1))
                throw new InvalidOperationException("match.template duplicateOverlapThreshold must be finite and between 0 and 1.");
            var nmsMode = shape ? "hard" : node.GetString("nmsMode", "hard").Trim().ToLowerInvariant();
            if (!shape && nmsMode is not ("hard" or "soft"))
                throw new InvalidOperationException("match.template nmsMode must be hard or soft.");
            var nmsSortKey = shape ? "score" : node.GetString("nmsSortKey", "score").Trim().ToLowerInvariant();
            if (!shape && nmsSortKey is not ("score" or "scorethenabsangle" or "scorethenscale"))
                throw new InvalidOperationException("match.template nmsSortKey must be score, scoreThenAbsAngle, or scoreThenScale.");
            var nmsAngleToleranceDeg = shape ? 0d : node.GetDouble("nmsAngleToleranceDeg", 0);
            if (!shape && (!double.IsFinite(nmsAngleToleranceDeg) || nmsAngleToleranceDeg < 0 || nmsAngleToleranceDeg > 180))
                throw new InvalidOperationException("match.template nmsAngleToleranceDeg must be finite and between 0 and 180.");
            VisionRoi? searchRoi;
            if (shape) searchRoi = node.GetRoi("searchRoi");
            else if (context.TryGetValue("searchRoi", out var searchRoiInput)) searchRoi = searchRoiInput.Require<VisionRoi>("searchRoi");
            else searchRoi = node.GetRoi("searchRoi");
            VisionRoi? ignoreRoi = null;
            if (!shape)
            {
                if (context.TryGetValue("ignoreRoi", out var ignoreRoiInput)) ignoreRoi = ignoreRoiInput.Require<VisionRoi>("ignoreRoi");
                else ignoreRoi = node.GetRoi("ignoreRoi");
            }
            var nccScaleMin = 1d;
            var nccScaleMax = 1d;
            var nccScaleStep = .1d;
            if (!shape)
            {
                var scaleMinPercent = node.GetDouble("scaleMinPercent", 100);
                var scaleMaxPercent = node.GetDouble("scaleMaxPercent", 100);
                var scaleStepPercent = node.GetDouble("scaleStepPercent", 10);
                if (!double.IsFinite(scaleMinPercent) || !double.IsFinite(scaleMaxPercent) || !double.IsFinite(scaleStepPercent))
                    throw new InvalidOperationException("match.template scale percent values must be finite.");
                scaleMinPercent = Math.Clamp(scaleMinPercent, 50, 200);
                scaleMaxPercent = Math.Clamp(scaleMaxPercent, 50, 200);
                scaleStepPercent = Math.Clamp(scaleStepPercent, 1, 50);
                if (scaleMinPercent > scaleMaxPercent)
                    throw new InvalidOperationException($"match.template scaleMinPercent {scaleMinPercent:0.###} is greater than scaleMaxPercent {scaleMaxPercent:0.###}.");
                nccScaleMin = scaleMinPercent / 100d;
                nccScaleMax = scaleMaxPercent / 100d;
                nccScaleStep = scaleStepPercent / 100d;
            }
            var emitScoreMap = !shape && node.GetBool("emitScoreMap", false);
            var ignoreMode = !shape
                ? node.GetString("ignoreMode", "hard").Trim().ToLowerInvariant()
                : "hard";
            if (!shape && ignoreMode is not ("hard" or "soft"))
                throw new InvalidOperationException($"match.template ignoreMode '{ignoreMode}' is not supported (use hard or soft).");
            var ignoreSoftWeight = !shape ? Math.Clamp(node.GetDouble("ignoreSoftWeight", 0), 0, 1) : 0d;
            var ignoreFeatherPx = !shape ? Math.Clamp(node.GetInt("ignoreFeatherPx", 0), 0, 64) : 0;
            var scoreMapAngleMode = !shape
                ? node.GetString("scoreMapAngleMode", "zero").Trim().ToLowerInvariant()
                : "zero";
            if (!shape && scoreMapAngleMode is not ("zero" or "matched" or "searchmid" or "stack"))
                throw new InvalidOperationException($"match.template scoreMapAngleMode '{scoreMapAngleMode}' is not supported (use zero, matched, searchMid, or stack).");
            var scoreMapWeighting = !shape
                ? node.GetString("scoreMapWeighting", "canvas").Trim().ToLowerInvariant()
                : "canvas";
            if (!shape && scoreMapWeighting is not ("canvas" or "templatemask"))
                throw new InvalidOperationException($"match.template scoreMapWeighting '{scoreMapWeighting}' is not supported (use canvas or templateMask).");
            var scoreMapStackMaxPlanes = !shape ? Math.Clamp(node.GetInt("scoreMapStackMaxPlanes", 9), 1, 17) : 1;
            var maxClutterScore = Math.Clamp(node.GetDouble("maxClutterScore", .2), 0, 1);
            var maxDeformationPx = shape ? Math.Clamp(node.GetInt("maxDeformationPx", 0), 0, 32) : 0;
            var greediness = shape ? Math.Clamp(node.GetDouble("greediness", .8), 0, 1) : .8;
            var maxOverlap = shape ? Math.Clamp(node.GetDouble("maxOverlap", .5), 0, 1) : .5;
            var scoreUsingClutter = shape && node.GetBool("scoreUsingClutter", false);
            if (scoreUsingClutter && !clutterEnabled)
                throw new InvalidOperationException("Score Using Clutter requires a trained Clutter ROI.");
            var runtimeContrastThreshold = shape ? Math.Clamp(node.GetDouble("runtimeContrastThreshold", 0), 0, 255) : 0;
            var contrastFractionThreshold = shape ? Math.Clamp(node.GetDouble("contrastFractionThreshold", 0), 0, 1) : 0;
            var contrastEnabled = runtimeContrastThreshold > 0 || contrastFractionThreshold > 0;
            using var contrastSceneImage = contrastEnabled ? JLVisionImages.ToMat(gray) : null;
            var originX = (templateBuffer.Width - 1) / 2d; var originY = (templateBuffer.Height - 1) / 2d;
            var minArea = node.GetDouble("minArea", 200);
            var templateSource = shape && savedModel.Length > 0 ? "trained-model" : fromPort ? "input-port" : "file";
            var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0) };
            if (shape)
            {
                outputs["scale"] = VisionValue.Double(1);
                outputs["scaleX"] = VisionValue.Double(1);
                outputs["scaleY"] = VisionValue.Double(1);
                if (clutterEnabled) outputs["clutterScore"] = VisionValue.Double(0);
                if (scoreUsingClutter) outputs["nativeScore"] = VisionValue.Double(0);
                if (contrastEnabled) outputs["contrast"] = VisionValue.Double(0);
                if (contrastFractionThreshold > 0) outputs["contrastFraction"] = VisionValue.Double(0);
            }
            var summary = new Dictionary<string, object?> { ["found"] = false, ["bestScore"] = 0d, ["score"] = 0d, ["templateSource"] = templateSource, ["algorithmBackend"] = "jlvision", ["angleRangeDeg"] = range * 180 / Math.PI, ["angleStepDeg"] = step * 180 / Math.PI };
            if (!shape)
            {
                summary["scaleMinPercent"] = nccScaleMin * 100;
                summary["scaleMaxPercent"] = nccScaleMax * 100;
                summary["scaleStepPercent"] = nccScaleStep * 100;
                summary["scaleSearch"] = Math.Abs(nccScaleMin - 1) < 1e-9 && Math.Abs(nccScaleMax - 1) < 1e-9 ? "fixed" : "discrete-resampled-ncc";
                summary["maxTargets"] = acceptLimit;
                summary["duplicateOverlapThreshold"] = duplicateOverlapThreshold;
                summary["nmsMode"] = nmsMode;
                summary["nmsSortKey"] = nmsSortKey switch
                {
                    "scorethenabsangle" => "scoreThenAbsAngle",
                    "scorethenscale" => "scoreThenScale",
                    _ => "score"
                };
                summary["nmsAngleToleranceDeg"] = nmsAngleToleranceDeg;
                summary["nmsSuppressedCount"] = 0;
                summary["nmsSoftDecayedCount"] = 0;
                summary["searchAngleStartDeg"] = searchStart * 180 / Math.PI;
                summary["searchAngleEndDeg"] = searchEnd * 180 / Math.PI;
                summary["ignorePolarity"] = ignorePolarity;
                summary["ignoreMask"] = ignoreRoi is not null;
                summary["ignoreMode"] = ignoreRoi is null ? "none" : ignoreMode;
                summary["ignoreSoftWeight"] = ignoreSoftWeight;
                summary["ignoreFeatherPx"] = ignoreFeatherPx;
                summary["emitScoreMap"] = emitScoreMap;
                summary["scoreMapAngleMode"] = scoreMapAngleMode;
                summary["scoreMapWeighting"] = scoreMapWeighting;
                summary["scoreMapStackMaxPlanes"] = scoreMapStackMaxPlanes;
            }
            if (shape)
            {
                summary["scaleTolerancePercent"] = scaleTolerance * 100;
                summary["scaleToleranceXPercent"] = scaleToleranceX * 100;
                summary["scaleToleranceYPercent"] = scaleToleranceY * 100;
                summary["ignorePolarity"] = ignorePolarity;
                summary["clutterEnabled"] = clutterEnabled;
                if (clutterEnabled) summary["maxClutterScore"] = maxClutterScore;
                summary["scoreUsingClutter"] = scoreUsingClutter;
                if (scoreUsingClutter) summary["clutterScoreAdjustment"] = "nativeScore × (1 - clutterScore)";
                summary["runtimeContrastThreshold"] = runtimeContrastThreshold;
                summary["contrastFractionThreshold"] = contrastFractionThreshold;
                summary["contrastCheckEnabled"] = contrastEnabled;
                summary["maxDeformationPx"] = maxDeformationPx;
                summary["greediness"] = greediness;
                summary["maxOverlap"] = maxOverlap;
                summary["searchAngleStartDeg"] = searchStart * 180 / Math.PI;
                summary["searchAngleEndDeg"] = searchEnd * 180 / Math.PI;
                summary["searchScaleMinPercent"] = searchScaleMin * 100;
                summary["searchScaleMaxPercent"] = searchScaleMax * 100;
                summary["searchScaleXMinPercent"] = searchScaleXMin * 100;
                summary["searchScaleXMaxPercent"] = searchScaleXMax * 100;
                summary["searchScaleYMinPercent"] = searchScaleYMin * 100;
                summary["searchScaleYMaxPercent"] = searchScaleYMax * 100;
                if (savedModel.Length > 0)
                {
                    summary["trainingAngleCoverageDeg"] = trainingRange * 180 / Math.PI;
                    summary["trainingScaleMinPercent"] = Math.Min(trainingScaleXMin, trainingScaleYMin) * 100;
                    summary["trainingScaleMaxPercent"] = Math.Max(trainingScaleXMax, trainingScaleYMax) * 100;
                    summary["trainingScaleXMinPercent"] = trainingScaleXMin * 100;
                    summary["trainingScaleXMaxPercent"] = trainingScaleXMax * 100;
                    summary["trainingScaleYMinPercent"] = trainingScaleYMin * 100;
                    summary["trainingScaleYMaxPercent"] = trainingScaleYMax * 100;
                }
            }
            var overlays = new List<VisionOverlay>();
            var detections = new List<VisionDetection>();
            var modelSet = shape ? DeserializeShapeModelSet(savedModel) : null;
            if (contrastEnabled && modelSet is { Models.Length: > 0 })
                throw new InvalidOperationException("Runtime contrast checks require one fused native shape model; retrain this legacy model set as a composite model first.");
            if (clutterEnabled && modelSet is { Models.Length: > 0 })
                throw new InvalidOperationException("Clutter scoring requires one fused native shape model; retrain this legacy model set as a composite model first.");
            var templateBox = new Rect(0, 0, templateBuffer.Width, templateBuffer.Height);
            if (shape && savedModel.Length > 0)
            {
                templateBox = new Rect(0, 0, (int)node.GetInt("trainedWidth", 1), (int)node.GetInt("trainedHeight", 1));
                originX = node.GetDouble("trainedOriginX", 0);
                originY = node.GetDouble("trainedOriginY", 0);
                if (modelSet is { Models.Length: > 0 })
                {
                    templateBox = new Rect(0, 0, modelSet.Models[0].Width, modelSet.Models[0].Height);
                    originX = modelSet.Models[0].OriginX;
                    originY = modelSet.Models[0].OriginY;
                    summary["modelVariantCount"] = modelSet.Models.Length;
                }
            }
            var searchOffsetX = 0;
            var searchOffsetY = 0;
            var searchMatchLimit = maximum;
            if (shape && savedModel.Length > 0) searchMatchLimit = Math.Min(1000, Math.Max(searchMatchLimit, maximum * 4));
            if (searchRoi is not null)
            {
                var searchBounds = RoiBounds(searchRoi);
                var imageBounds = new Rect(0, 0, source.Width, source.Height);
                var clippedSearch = searchBounds & imageBounds;
                if (clippedSearch.Width <= 0 || clippedSearch.Height <= 0)
                {
                    summary["searchRegionEmpty"] = true;
                    if (shape) outputs["image"] = VisionValue.Image(source.CloneOwned());
                    outputs["count"] = VisionValue.Integer(0);
                    outputs["detections"] = VisionValue.Detections([]);
                    summary["accepted"] = 0;
                    return new(outputs, summary, overlays);
                }
                // Search regions constrain candidate origins; a margin keeps the full trained contour
                // available when a target lies near the region boundary.
            var radius = Math.Sqrt((double)templateBox.Width * templateBox.Width + (double)templateBox.Height * templateBox.Height);
                var marginScale = shape ? searchScaleMax : Math.Max(searchScaleMax, nccScaleMax);
                var margin = (int)Math.Ceiling(radius * marginScale) + 2;
                var expanded = new Rect(clippedSearch.X - margin, clippedSearch.Y - margin,
                    clippedSearch.Width + margin * 2, clippedSearch.Height + margin * 2) & imageBounds;
                using var sceneGray = JLVisionImages.ToMat(gray);
                using var matchMat = new Mat(sceneGray, expanded);
                searchScene = JLVisionImages.FromMat(matchMat);
                searchOffsetX = expanded.X;
                searchOffsetY = expanded.Y;
                searchMatchLimit = Math.Min(1000, Math.Max(maximum, maximum * 4));
                summary["searchRegion"] = searchRoi.Type.ToString();
                summary["searchWindow"] = new { x = expanded.X, y = expanded.Y, width = expanded.Width, height = expanded.Height };
            }
            var matchingScene = searchScene ?? gray;
            if (!shape && ignoreRoi is not null)
            {
                matchingScene.GetImageSize(out int matchW, out int matchH);
                using var ignorePaint = new Mat(matchH, matchW, MatType.CV_8UC1, Scalar.All(0));
                // ignoreRoi is authored in full-image coordinates; map into the optional search crop.
                FillRoiMask(ignorePaint, ignoreRoi, new Rect(searchOffsetX, searchOffsetY, matchW, matchH), token);
                summary["ignoreRegion"] = ignoreRoi.Type.ToString();
                if (ignoreMode == "hard")
                {
                    using var domainMat = new Mat(matchH, matchW, MatType.CV_8UC1, Scalar.All(255));
                    domainMat.SetTo(Scalar.All(0), ignorePaint);
                    if (Cv2.CountNonZero(domainMat) < 16)
                        throw new InvalidOperationException("ignoreRoi leaves fewer than 16 searchable pixels in the search window.");
                    using var domainImage = JLVisionImages.FromMat(domainMat);
                    using var domainRegion = domainImage.Threshold(1d, 255d);
                    ignoreReducedScene = matchingScene.ReduceDomain(domainRegion);
                    matchingScene = ignoreReducedScene;
                }
                else
                {
                    // Soft: lerp ignored pixels toward scene mean by (1 - weight). Feather blurs the binary mask.
                    // Honest: this depresses correlation; it is not a true weighted NCC integral.
                    using var sceneMat = JLVisionImages.ToMat(matchingScene);
                    using var soft = sceneMat.Clone();
                    using var weight = BuildIgnoreWeightMap(ignorePaint, ignoreSoftWeight, ignoreFeatherPx);
                    var mean = Cv2.Mean(sceneMat).Val0;
                    ApplySoftIgnoreLerp(soft, weight, mean);
                    softIgnoreScene = JLVisionImages.FromMat(soft);
                    matchingScene = softIgnoreScene;
                    summary["ignoreSoftApplied"] = true;
                }
            }
            var matchedCandidates = new List<ShapeMatchCandidate>();
            ShapeContrastReference? contrastReference = null;
            if (modelSet is { Models.Length: > 0 })
            {
                var cacheHits = 0;
                for (var modelIndex = 0; modelIndex < modelSet.Models.Length; modelIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = modelSet.Models[modelIndex];
                    byte[] modelBytes;
                    try { modelBytes = Convert.FromBase64String(item.Data); }
                    catch (FormatException ex) { throw new InvalidOperationException($"Training sample {modelIndex + 1} contains invalid model data.", ex); }
                    var modelKey = "trained:" + Convert.ToHexString(SHA256.HashData(modelBytes));
                    using var lease = JLVisionModelCache.Acquire(modelKey, () =>
                    {
                        var loaded = new JlShapeModel();
                        try { loaded.DeserializeShapeModel(modelBytes); return loaded; }
                        catch { loaded.Dispose(); throw; }
                    });
                    cacheHits += lease.Hit ? 1 : 0;
                    var model = (JlShapeModel)lease.Model;
                    JlTuple rows, cols, angles, scores, scalesX, scalesY;
                    if (anisotropicScale)
                    {
                        if (maxDeformationPx > 0)
                            FindAnisoShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart,
                                searchScaleYMin, searchScaleYMax, searchScaleXMin, searchScaleXMax, minScore, maxClutterScore,
                                searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled, maxOverlap, greediness,
                                out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                        else model.FindAnisoShapeModel(matchingScene, searchStart, searchEnd - searchStart,
                                searchScaleYMin, searchScaleYMax, searchScaleXMin, searchScaleXMax,
                                minScore, searchMatchLimit, maxOverlap, "least_squares", shapePyramidLevels, greediness,
                                out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                    }
                    else if (scaleTolerance > 0)
                    {
                        if (maxDeformationPx > 0)
                            FindScaledShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart,
                                searchScaleMin, searchScaleMax, minScore, maxClutterScore, searchMatchLimit,
                                shapePyramidLevels, maxDeformationPx, clutterEnabled, maxOverlap, greediness, out rows, out cols,
                                out angles, out scalesX, out scores);
                        else model.FindScaledShapeModel(matchingScene, searchStart, searchEnd - searchStart, searchScaleMin, searchScaleMax, minScore, searchMatchLimit, maxOverlap, "least_squares", shapePyramidLevels, greediness, out rows, out cols, out angles, out scalesX, out scores);
                        scalesY = new JlTuple(scalesX);
                    }
                    else
                    {
                        if (maxDeformationPx > 0)
                            FindShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart, minScore,
                                maxClutterScore, searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled, maxOverlap, greediness,
                                out rows, out cols, out angles, out scores);
                        else matchingScene.FindShapeModel(model, searchStart, searchEnd - searchStart, minScore, searchMatchLimit, maxOverlap, "least_squares", shapePyramidLevels, greediness, out rows, out cols, out angles, out scores);
                        scalesX = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                        scalesY = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                    }
                    using (rows) using (cols) using (angles) using (scores) using (scalesX) using (scalesY)
                    {
                        for (var index = 0; index < scores.Length; index++)
                            matchedCandidates.Add(new(cols[index].D, rows[index].D, angles[index].D, scalesX[index].D, scalesY[index].D, scores[index].D,
                                item.Width, item.Height, item.OriginX, item.OriginY, modelIndex * searchMatchLimit + index));
                    }
                }
                summary["modelCacheHit"] = cacheHits == modelSet.Models.Length;
                summary["modelCacheHits"] = cacheHits;
                summary["modelSource"] = "workflow-asset-set";
            }
            else if (shape)
            {
                JLVisionModelCache.Lease? shapeLease = null;
                JlShapeModel model;
                if (savedModel.Length > 0)
                {
                    byte[] modelBytes;
                    try { modelBytes = Convert.FromBase64String(savedModel); }
                    catch (FormatException ex) { throw new InvalidOperationException("The saved shape model data is invalid.", ex); }
                    if (modelBytes.Length > 16 * 1024 * 1024) throw new InvalidOperationException("The saved shape model exceeds the 16 MB limit.");
                    var modelKey = "trained:" + Convert.ToHexString(SHA256.HashData(modelBytes));
                    shapeLease = JLVisionModelCache.Acquire(modelKey, () =>
                    {
                        var loaded = new JlShapeModel();
                        try { loaded.DeserializeShapeModel(modelBytes); return loaded; }
                        catch { loaded.Dispose(); throw; }
                    });
                    model = (JlShapeModel)shapeLease.Model;
                    summary["modelCacheHit"] = shapeLease.Hit;
                    summary["modelSource"] = modelSource.Length > 0 ? modelSource : "workflow-asset";
                    summary["modelSha256"] = Convert.ToHexString(SHA256.HashData(modelBytes));
                    if (assetDisabledFallback) summary["modelAssetDisabled"] = true;
                }
                else
                {
                    if (MatchingAutoTune.IsAutoEnabled(node))
                    {
                        // legacy 路径没有训练步骤，运行期按模板身份缓存调用一次 determine。
                        var legacyAuto = MatchingAutoTune.ResolveForShapeLegacy(node, templateBuffer, hasTemplateRoi: false);
                        shapePyramidLevels = legacyAuto.PyramidLevels;
                        step = legacyAuto.AngleStepDeg * Math.PI / 180d;
                        minContrast = legacyAuto.MinContrast;
                        if (clutterEnabled && clutterContrast < minContrast)
                            throw new InvalidOperationException(
                                $"Clutter Contrast ({clutterContrast}) must be at least the auto-tuned Minimum Feature Contrast ({minContrast}). Lower Clutter Contrast or lock min_contrast in autoTuneLockedParams.");
                        summary["angleStepDeg"] = step * 180 / Math.PI;
                        summary["pyramidLevels"] = shapePyramidLevels;
                        summary["autoTuneShape"] = new Dictionary<string, object?>
                        {
                            ["version"] = MatchingAutoTune.Version,
                            ["profile"] = node.GetString("autoTuneProfile", "balanced"),
                            ["pyramidLevels"] = legacyAuto.PyramidLevels,
                            ["angleStepDeg"] = legacyAuto.AngleStepDeg,
                            ["contrast"] = legacyAuto.Contrast,
                            ["minContrast"] = legacyAuto.MinContrast,
                            ["quality"] = legacyAuto.Inspect.Quality,
                            ["warnings"] = legacyAuto.Warnings.Select(warning => warning.Code).ToArray()
                        };
                    }
                    using var binary = ContourNodesHelper.Binarize(templateBuffer, node.GetString("binarize", "auto"), node.GetDouble("threshold", 128), node.GetBool("invert", false), out _, out _);
                    JLVisionOperators.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    var bestContour = contours.OrderByDescending(c => JLVisionOperators.ContourArea(c)).FirstOrDefault();
                    if (bestContour is null || JLVisionOperators.ContourArea(bestContour) <= 0)
                    { summary["reason"] = "template-has-no-contour"; outputs["image"] = VisionValue.Image(source.CloneOwned()); return new(outputs, summary, overlays); }
                    var moments = JLVisionOperators.Moments(bestContour);
                    originX = moments.M10 / moments.M00; originY = moments.M01 / moments.M00;
                    templateBox = JLVisionOperators.BoundingRect(bestContour);
                    shapeLease = JLVisionModelCache.Acquire(Key(templateBuffer, true, range, step, originX, originY, shapePyramidLevels, scaleMin, scaleMax, scaleStep, ignorePolarity, minContrast,
                        trainingScaleXMin, trainingScaleXMax, trainingScaleYMin, trainingScaleYMax,
                        clutterEnabled ? $"{RoiFingerprint(clutterRoi)}:{clutterContrast}" : string.Empty), () =>
                    {
                        var created = new JlShapeModel();
                        try
                        {
                            using var levels = new JlTuple(shapePyramidLevels);
                            using var delta = new JlTuple(step); using var optimize = new JlTuple("auto"); using var contrast = new JlTuple("auto"); using var minimum = new JlTuple(minContrast);
                            var metric = ignorePolarity ? "ignore_global_polarity" : "use_polarity";
                            if (anisotropicScale)
                            {
                                using var scaleDelta = new JlTuple(scaleStep);
                                created.CreateAnisoShapeModel(template, levels, -range, 2 * range, delta,
                                    trainingScaleYMin, trainingScaleYMax, scaleDelta,
                                    trainingScaleXMin, trainingScaleXMax, scaleDelta, optimize, metric, contrast, minimum);
                            }
                            else if (scaleTolerance > 0)
                            {
                                using var scaleDelta = new JlTuple(scaleStep);
                                created.CreateScaledShapeModel(template, levels, -range, 2 * range, delta, scaleMin, scaleMax, scaleDelta, optimize, metric, contrast, minimum);
                            }
                            else created.CreateShapeModel(template, levels, -range, 2 * range, delta, optimize, metric, contrast, minimum);
                            created.SetShapeModelOrigin(originY - (templateBuffer.Height - 1) / 2d, originX - (templateBuffer.Width - 1) / 2d);
                            if (clutterEnabled)
                                ApplyShapeModelClutter(created, template, templateBuffer.Width, templateBuffer.Height,
                                    clutterRoi!, 0, 0, clutterContrast, minContrast, token);
                            return created;
                        }
                        catch { created.Dispose(); throw; }
                    });
                    model = (JlShapeModel)shapeLease.Model; summary["modelCacheHit"] = shapeLease.Hit;
                }
                JlTuple rows, cols, angles, scores, scalesX, scalesY;
                try
                {
                    if (contrastEnabled)
                    {
                        using var contrastTrainingImage = contrastFractionThreshold <= 0 ? null
                            : savedModel.Length == 0 ? JLVisionImages.ToMat(template)
                            : DecodeGrayscaleImage(node.GetString("trainedSourceImage", string.Empty), "saved training image");
                        var trainingOriginImageX = savedModel.Length == 0 ? originX
                            : node.GetDouble("trainedOriginImageX", node.GetDouble("trainedOriginX", originX)) + node.GetDouble("trainedPoseOffsetX", 0);
                        var trainingOriginImageY = savedModel.Length == 0 ? originY
                            : node.GetDouble("trainedOriginImageY", node.GetDouble("trainedOriginY", originY)) + node.GetDouble("trainedPoseOffsetY", 0);
                        contrastReference = CreateContrastReference(model, contrastTrainingImage, trainingOriginImageX, trainingOriginImageY);
                    }
                    if (anisotropicScale)
                    {
                        if (clutterEnabled || maxDeformationPx > 0)
                            FindAnisoShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart,
                                searchScaleYMin, searchScaleYMax, searchScaleXMin, searchScaleXMax, minScore,
                                maxClutterScore, searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled, maxOverlap, greediness,
                                out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                        else model.FindAnisoShapeModel(matchingScene, searchStart, searchEnd - searchStart,
                            searchScaleYMin, searchScaleYMax, searchScaleXMin, searchScaleXMax,
                            minScore, searchMatchLimit, maxOverlap, "least_squares", shapePyramidLevels, greediness,
                            out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                    }
                    else if (scaleTolerance > 0)
                    {
                        if (clutterEnabled || maxDeformationPx > 0)
                            FindScaledShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart,
                                searchScaleMin, searchScaleMax, minScore, maxClutterScore, searchMatchLimit,
                                shapePyramidLevels, maxDeformationPx, clutterEnabled, maxOverlap, greediness,
                                out rows, out cols, out angles, out scalesX, out scores);
                        else model.FindScaledShapeModel(matchingScene, searchStart, searchEnd - searchStart, searchScaleMin, searchScaleMax, minScore, searchMatchLimit, maxOverlap, "least_squares", shapePyramidLevels, greediness, out rows, out cols, out angles, out scalesX, out scores);
                        scalesY = new JlTuple(Enumerable.Range(0, scalesX.Length).Select(index => scalesX[index].D).ToArray());
                    }
                    else
                    {
                        if (clutterEnabled || maxDeformationPx > 0)
                            FindShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart, minScore,
                                maxClutterScore, searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled, maxOverlap, greediness,
                                out rows, out cols, out angles, out scores);
                        else matchingScene.FindShapeModel(model, searchStart, searchEnd - searchStart, minScore, searchMatchLimit, maxOverlap, "least_squares", shapePyramidLevels, greediness, out rows, out cols, out angles, out scores);
                        scalesX = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                        scalesY = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                    }
                }
                finally { shapeLease?.Dispose(); }
                using (rows) using (cols) using (angles) using (scores) using (scalesX) using (scalesY)
                {
                    var matchCount = clutterEnabled ? scores.Length / 2 : scores.Length;
                    for (var index = 0; index < matchCount; index++)
                        matchedCandidates.Add(new(cols[index].D, rows[index].D, angles[index].D, scalesX[index].D, scalesY[index].D, scores[index].D,
                            templateBox.Width, templateBox.Height, originX, originY, index,
                            clutterEnabled ? scores[matchCount + index].D : null, contrastReference));
                }
            }
            else
            {
                VisionRoi? templateRoi = context.TryGetValue("templateRoi", out var templateRoiInput)
                    ? templateRoiInput.Require<VisionRoi>("templateRoi")
                    : node.GetRoi("templateRoi");
                var requestedPyramidLevels = node.GetInt("pyramidLevels", 0);
                if (requestedPyramidLevels is < 0 or > 8) throw new InvalidOperationException("pyramidLevels must be between 0 and 8.");
                if (MatchingAutoTune.IsAutoEnabled(node))
                {
                    // NCC 运行期调参：模板指纹缓存，同一模板只调用一次 determine。
                    var nccAutoTune = MatchingAutoTune.ResolveForNcc(node, templateBuffer, templateRoi is not null);
                    requestedPyramidLevels = nccAutoTune.PyramidLevels;
                    step = nccAutoTune.AngleStepDeg * Math.PI / 180d;
                    summary["autoTuneNcc"] = new Dictionary<string, object?>
                    {
                        ["version"] = MatchingAutoTune.Version,
                        ["profile"] = node.GetString("autoTuneProfile", "balanced"),
                        ["pyramidLevels"] = nccAutoTune.PyramidLevels,
                        ["angleStepDeg"] = nccAutoTune.AngleStepDeg,
                        ["warnings"] = nccAutoTune.Warnings.Select(warning => warning.Code).ToArray()
                    };
                }
                var pyramidLevels = requestedPyramidLevels == 0
                    ? Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1d, Math.Min(templateBuffer.Width, templateBuffer.Height) / 6d))) + 1, 1, 8)
                    : requestedPyramidLevels;
                summary["pyramidLevels"] = pyramidLevels;
                summary["method"] = method;
                summary["templateMask"] = templateRoi is not null;
                // Soft-NMS needs a lower Find floor so neighbors arrive and can be decayed below minScore.
                var findMinScore = nmsMode == "soft"
                    ? Math.Max(0.05, Math.Min(minScore, minScore * 0.5))
                    : minScore;
                summary["nccFindMinScore"] = squaredDifference ? 0.001 : findMinScore;
                AppendNccCandidates(matchingScene, template, templateBuffer, templateRoi, searchStart, searchEnd, step, requestedPyramidLevels, pyramidLevels,
                    squaredDifference, findMinScore, acceptLimit, nccScaleMin, nccScaleMax, nccScaleStep, searchRoi is not null, ignorePolarity, nmsMode, duplicateOverlapThreshold, nmsAngleToleranceDeg, token, summary, matchedCandidates);
            }
            var nmsAccepted = new List<(VisionRectangle2D Box, double Cx, double Cy, double AngleDeg)>();
            var nmsSuppressedCount = 0;
            var nmsSoftDecayedCount = 0;
            IEnumerable<ShapeMatchCandidate> rankedCandidates = matchedCandidates.OrderByDescending(candidate => CandidateScore(candidate, scoreUsingClutter));
            if (!shape)
            {
                rankedCandidates = nmsSortKey switch
                {
                    "scorethenabsangle" => matchedCandidates
                        .OrderByDescending(candidate => CandidateScore(candidate, scoreUsingClutter))
                        .ThenBy(candidate => Math.Abs(VisionTransform2D.NormalizeAngleDeg(candidate.Angle * 180 / Math.PI))),
                    "scorethenscale" => matchedCandidates
                        .OrderByDescending(candidate => CandidateScore(candidate, scoreUsingClutter))
                        .ThenBy(candidate => Math.Abs(candidate.Scale - 1d)),
                    _ => matchedCandidates
                        .OrderByDescending(candidate => CandidateScore(candidate, scoreUsingClutter))
                        .ThenBy(candidate => candidate.Index)
                };
            }
            foreach (var candidate in rankedCandidates)
            {
                token.ThrowIfCancellationRequested();
                var x = candidate.X + searchOffsetX; var y = candidate.Y + searchOffsetY;
                var radians = candidate.Angle; var scale = candidate.Scale; var nativeScore = candidate.Score;
                var score = CandidateScore(candidate, scoreUsingClutter);
                if (!shape && ignoreRoi is not null && ignoreMode == "soft")
                {
                    var softW = SampleIgnoreWeight(ignoreRoi, x, y, ignoreSoftWeight, ignoreFeatherPx);
                    score *= softW;
                    if (score < minScore) continue;
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
                if ((!shape || savedModel.Length > 0) && (radians < searchStart - 1e-9 || radians > searchEnd + 1e-9)) continue;
                if (shape && savedModel.Length > 0 && (candidate.ScaleX < searchScaleXMin - 1e-9 || candidate.ScaleX > searchScaleXMax + 1e-9
                    || candidate.ScaleY < searchScaleYMin - 1e-9 || candidate.ScaleY > searchScaleYMax + 1e-9)) continue;
                if (searchRoi is not null && !Contains(searchRoi, x, y)) continue;
                if (!shape && ignoreRoi is not null && ignoreMode == "hard" && Contains(ignoreRoi, x, y)) continue;
                double? contrast = null;
                double? contrastFraction = null;
                if (contrastEnabled)
                {
                    var reference = candidate.ContrastReference
                        ?? throw new InvalidOperationException("The shape model has no contrast reference points.");
                    contrast = MeasureCandidateContrast(contrastSceneImage!, reference,
                        candidate.X + searchOffsetX, candidate.Y + searchOffsetY, radians, candidate.ScaleX, candidate.ScaleY);
                    if (contrast.Value < runtimeContrastThreshold) continue;
                    if (contrastFractionThreshold > 0)
                    {
                        contrastFraction = Math.Clamp(contrast.Value / reference.TrainingMeanContrast, 0, 1);
                        if (contrastFraction.Value < contrastFractionThreshold) continue;
                    }
                }
                // Soft-NMS must see candidates below the final gate so decay can drop them;
                // hard NMS / shape keep the early reject for speed.
                if (score < minScore && (shape || nmsMode != "soft")) continue;
                var cos = Math.Cos(radians); var sin = Math.Sin(radians);
                var corners = new[] { (candidateBox.Left, candidateBox.Top), (candidateBox.Right - 1, candidateBox.Top), (candidateBox.Right - 1, candidateBox.Bottom - 1), (candidateBox.Left, candidateBox.Bottom - 1) }
                    .Select(p => new VisionPoint2D(x + cos * candidate.ScaleX * (p.Item1 - candidate.OriginX) + sin * candidate.ScaleY * (p.Item2 - candidate.OriginY),
                        y - sin * candidate.ScaleX * (p.Item1 - candidate.OriginX) + cos * candidate.ScaleY * (p.Item2 - candidate.OriginY))).ToArray();
                var left = corners.Min(p => p.X); var top = corners.Min(p => p.Y); var width = corners.Max(p => p.X) - left + 1; var height = corners.Max(p => p.Y) - top + 1;
                if (shape && candidateBox.Width * candidateBox.Height * candidate.ScaleX * candidate.ScaleY < minArea) continue;
                var distance = Math.Max(0, node.GetDouble("minCenterDistance", shape ? 20 : 0));
                if (distance > 0 && detections.Any(d => Math.Sqrt(Math.Pow(d.Circle.X - x, 2) + Math.Pow(d.Circle.Y - y, 2)) < distance)) continue;
                if (shape && detections.Any(d => Overlap(d.BoundingBox, new(left, top, width, height)) > .3)) continue;
                var degrees = VisionTransform2D.NormalizeAngleDeg(radians * 180 / Math.PI);
                if (!shape && duplicateOverlapThreshold > 0)
                {
                    var candidateBox2d = new VisionRectangle2D(left, top, width, height);
                    var softDecayed = false;
                    var hardReject = false;
                    foreach (var prior in nmsAccepted)
                    {
                        var iou = Overlap(prior.Box, candidateBox2d);
                        if (iou < duplicateOverlapThreshold) continue;
                        if (nmsAngleToleranceDeg > 0)
                        {
                            var delta = Math.Abs(VisionTransform2D.NormalizeAngleDeg(degrees - prior.AngleDeg));
                            delta = Math.Min(delta, 360d - delta);
                            if (delta > nmsAngleToleranceDeg) continue; // different orientation — keep both
                        }
                        if (nmsMode == "soft")
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
                        if (score < minScore)
                        {
                            nmsSuppressedCount++;
                            continue;
                        }
                    }
                }
                if (!shape && nmsMode == "soft" && score < minScore) continue;
                if (detections.Count >= acceptLimit) continue;
                if (!outputs["found"].Require<bool>("found"))
                {
                    outputs["found"] = VisionValue.Boolean(true); outputs["score"] = VisionValue.Double(score);
                    if (shape)
                    {
                        outputs["scale"] = VisionValue.Double(scale);
                        outputs["scaleX"] = VisionValue.Double(candidate.ScaleX);
                        outputs["scaleY"] = VisionValue.Double(candidate.ScaleY);
                        if (clutterEnabled) outputs["clutterScore"] = VisionValue.Double(candidate.ClutterScore ?? 0);
                        if (scoreUsingClutter) outputs["nativeScore"] = VisionValue.Double(nativeScore);
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
                        if (clutterEnabled) summary["matchedClutterScore"] = candidate.ClutterScore;
                        if (scoreUsingClutter) summary["matchedNativeScore"] = nativeScore;
                        if (contrast is { } measuredContrast) summary["matchedContrast"] = measuredContrast;
                        if (contrastFraction is { } measuredFraction) summary["matchedContrastFraction"] = measuredFraction;
                    }
                }
                var box = new VisionRectangle2D(left, top, width, height);
                detections.Add(new(detections.Count, left, top, width, height, candidateBox.Width * candidateBox.Height * candidate.ScaleX * candidate.ScaleY, 2 * (width + height), 0, new(x, y, Math.Sqrt(width * width + height * height) / 2), box, score));
                if (!shape) nmsAccepted.Add((box, x, y, degrees));
                var scaleLabel = anisotropicScale ? $" ×{candidate.ScaleX:0.###}/{candidate.ScaleY:0.###}" : $" ×{scale:0.###}";
                var clutterLabel = clutterEnabled ? $" c={candidate.ClutterScore ?? 0:0.###}" : string.Empty;
                var templateLabel = Math.Abs(scale - 1d) < 1e-6 ? $"s={score:0.###} a={degrees:0.#}" : $"s={score:0.###} a={degrees:0.#} ×{scale:0.###}";
                overlays.Add(new($"match-{candidate.Index}", VisionOverlayType.Polygon, Points: corners.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray(), Label: shape ? $"s={score:0.###} a={degrees:0.#}{scaleLabel}{clutterLabel}" : templateLabel, Stroke: "#52c41a", StrokeWidth: 2));
            }
            token.ThrowIfCancellationRequested();
            if (shape) outputs["image"] = VisionValue.Image(source.CloneOwned());
            if (!shape && emitScoreMap)
            {
                using var sceneGray = JLVisionImages.ToMat(gray);
                Mat? crop = null;
                try
                {
                    Mat scoreScene = sceneGray;
                    var ox = 0;
                    var oy = 0;
                    if (searchRoi is not null)
                    {
                        var searchBounds = RoiBounds(searchRoi);
                        var imageBounds = new Rect(0, 0, source.Width, source.Height);
                        var clippedSearch = searchBounds & imageBounds;
                        if (clippedSearch.Width > 0 && clippedSearch.Height > 0)
                        {
                            var radius = Math.Sqrt((double)templateBuffer.Width * templateBuffer.Width + (double)templateBuffer.Height * templateBuffer.Height);
                            var margin = (int)Math.Ceiling(radius * Math.Max(1d, nccScaleMax)) + 2;
                            var expanded = new Rect(clippedSearch.X - margin, clippedSearch.Y - margin,
                                clippedSearch.Width + margin * 2, clippedSearch.Height + margin * 2) & imageBounds;
                            crop = new Mat(sceneGray, expanded);
                            scoreScene = crop;
                            ox = expanded.X;
                            oy = expanded.Y;
                        }
                    }
                    VisionRoi? scoreTemplateRoi = context.TryGetValue("templateRoi", out var scoreTemplateRoiInput)
                        ? scoreTemplateRoiInput.Require<VisionRoi>("templateRoi")
                        : node.GetRoi("templateRoi");
                    var softIgnore = ignoreRoi is not null && ignoreMode == "soft";
                    var useTemplateMask = scoreMapWeighting == "templatemask";
                    var anglePlanesDeg = BuildScoreMapAnglePlanesDeg(
                        scoreMapAngleMode, searchStart, searchEnd, step, scoreMapStackMaxPlanes,
                        summary, outputs);
                    var planes = new List<Mat>(anglePlanesDeg.Count);
                    try
                    {
                        foreach (var planeAngle in anglePlanesDeg)
                        {
                            token.ThrowIfCancellationRequested();
                            planes.Add(BuildNccScoreMap(
                                scoreScene, templateBuffer, squaredDifference, ox, oy, source.Width, source.Height,
                                ignoreRoi, softIgnore, ignoreSoftWeight, ignoreFeatherPx, planeAngle,
                                scoreTemplateRoi, useTemplateMask, token));
                        }
                        using var scoreMap = planes.Count == 1
                            ? planes[0].Clone()
                            : MaxProjectScoreMaps(planes);
                        outputs["scoreMap"] = VisionValue.Image(VisionImage.Own(scoreMap.Clone()));
                        if (planes.Count > 1)
                        {
                            using var stack = StackScoreMapMontage(planes);
                            outputs["scoreMapStack"] = VisionValue.Image(VisionImage.Own(stack.Clone()));
                        }
                        Cv2.MinMaxLoc(scoreMap, out _, out var peak, out _, out var peakLoc);
                        var peakAngle = anglePlanesDeg[0];
                        if (planes.Count > 1)
                            peakAngle = anglePlanesDeg[FindScoreMapPeakPlane(planes, peakLoc)];
                        summary["scoreMapEmitted"] = true;
                        summary["scoreMapStackCount"] = planes.Count;
                        summary["scoreMapStackAnglesDeg"] = anglePlanesDeg.Select(a => Math.Round(a, 3)).ToArray();
                        var angleTag = planes.Count > 1
                            ? $"stack{planes.Count}"
                            : (Math.Abs(peakAngle) < 1e-9 ? "0deg" : $"rot{peakAngle:0.#}deg");
                        var weightTag = useTemplateMask ? "templatemask" : "canvas";
                        summary["scoreMapMode"] = squaredDifference
                            ? $"opencv-sqdiff-normed-unit-scale-{weightTag}-{angleTag}"
                            : $"opencv-ccoeff-normed-unit-scale-{weightTag}-{angleTag}";
                        summary["scoreMapAngleDeg"] = peakAngle;
                        summary["scoreMapConvention"] = "template-top-left";
                        summary["scoreMapPeakValue"] = peak;
                        summary["scoreMapPeakX"] = peakLoc.X;
                        summary["scoreMapPeakY"] = peakLoc.Y;
                        summary["scoreMapIgnore"] = ignoreRoi is null ? "none" : (softIgnore ? "soft-weight" : "hard-zero");
                        summary["scoreMapTemplateMask"] = useTemplateMask;
                    }
                    finally
                    {
                        foreach (var plane in planes) plane.Dispose();
                    }
                }
                finally { crop?.Dispose(); }
            }
            else if (!shape)
            {
                summary["scoreMapEmitted"] = false;
            }
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
