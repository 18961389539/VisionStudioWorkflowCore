using JLVisionLib;
using System.Security.Cryptography;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-13 从 Execute 抽出的三段编排：搜索窗口/ignore 预处理、候选收集、诊断 score map。
// 纯移动：执行顺序、using/try-finally 与 dispose 归属都留在原处（脚本对象仍由 Execute 的 finally 释放）。
internal static partial class JLVisionMatching
{
    private sealed record SearchPrepResult(
        Rect TemplateBox,
        double OriginX,
        double OriginY,
        int SearchOffsetX,
        int SearchOffsetY,
        int SearchMatchLimit,
        JlImage? SearchScene,
        JlImage? IgnoreReducedScene,
        JlImage? SoftIgnoreScene,
        JlImage MatchingScene,
        NodeExecutorResult? EarlyResult);

    private static SearchPrepResult PrepareSearchScene(
        NodeDefinition node,
        MatchParameters p,
        bool shape,
        bool hasSavedModel,
        ShapeModelSetEnvelope? modelSet,
        Mat templateBuffer,
        JlImage gray,
        VisionImage source,
        Dictionary<string, VisionValue> outputs,
        Dictionary<string, object?> summary,
        List<VisionOverlay> overlays,
        CancellationToken token)
    {
        var originX = p.OriginX;
        var originY = p.OriginY;
        var templateBox = new Rect(0, 0, templateBuffer.Width, templateBuffer.Height);
        if (shape && hasSavedModel)
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
        var searchMatchLimit = p.Maximum;
        if (shape && hasSavedModel) searchMatchLimit = Math.Min(1000, Math.Max(searchMatchLimit, p.Maximum * 4));
        JlImage? searchScene = null;
        JlImage? ignoreReducedScene = null;
        JlImage? softIgnoreScene = null;
        var searchRoi = p.SearchRoi;
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
                return new SearchPrepResult(templateBox, originX, originY, 0, 0, 0, null, null, null, gray, new(outputs, summary, overlays));
            }
            // Search regions constrain candidate origins; a margin keeps the full trained contour
            // available when a target lies near the region boundary.
            var radius = Math.Sqrt((double)templateBox.Width * templateBox.Width + (double)templateBox.Height * templateBox.Height);
            var marginScale = shape ? p.SearchScaleMax : Math.Max(p.SearchScaleMax, p.NccScaleMax);
            var margin = (int)Math.Ceiling(radius * marginScale) + 2;
            var expanded = new Rect(clippedSearch.X - margin, clippedSearch.Y - margin,
                clippedSearch.Width + margin * 2, clippedSearch.Height + margin * 2) & imageBounds;
            using var sceneGray = JLVisionImages.ToMat(gray);
            using var matchMat = new Mat(sceneGray, expanded);
            searchScene = JLVisionImages.FromMat(matchMat);
            searchOffsetX = expanded.X;
            searchOffsetY = expanded.Y;
            searchMatchLimit = Math.Min(1000, Math.Max(p.Maximum, p.Maximum * 4));
            summary["searchRegion"] = searchRoi.Type.ToString();
            summary["searchWindow"] = new { x = expanded.X, y = expanded.Y, width = expanded.Width, height = expanded.Height };
        }
        var matchingScene = searchScene ?? gray;
        var ignoreRoi = p.IgnoreRoi;
        if (!shape && ignoreRoi is not null)
        {
            matchingScene.GetImageSize(out int matchW, out int matchH);
            using var ignorePaint = new Mat(matchH, matchW, MatType.CV_8UC1, Scalar.All(0));
            // ignoreRoi is authored in full-image coordinates; map into the optional search crop.
            FillRoiMask(ignorePaint, ignoreRoi, new Rect(searchOffsetX, searchOffsetY, matchW, matchH), token);
            summary["ignoreRegion"] = ignoreRoi.Type.ToString();
            if (p.IgnoreMode == "hard")
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
                using var weight = BuildIgnoreWeightMap(ignorePaint, p.IgnoreSoftWeight, p.IgnoreFeatherPx);
                var mean = Cv2.Mean(sceneMat).Val0;
                ApplySoftIgnoreLerp(soft, weight, mean);
                softIgnoreScene = JLVisionImages.FromMat(soft);
                matchingScene = softIgnoreScene;
                summary["ignoreSoftApplied"] = true;
            }
        }
        return new SearchPrepResult(templateBox, originX, originY, searchOffsetX, searchOffsetY, searchMatchLimit,
            searchScene, ignoreReducedScene, softIgnoreScene, matchingScene, null);
    }

    private sealed record CandidateCollection(List<ShapeMatchCandidate> Candidates, NodeExecutorResult? EarlyResult);

    private static CandidateCollection CollectCandidates(
        NodeExecutionContext context,
        NodeDefinition node,
        MatchParameters p,
        bool shape,
        string savedModel,
        string modelSource,
        bool assetDisabledFallback,
        ShapeModelSetEnvelope? modelSet,
        JlImage template,
        Mat templateBuffer,
        JlImage matchingScene,
        VisionImage source,
        int searchMatchLimit,
        Rect templateBox,
        double originX,
        double originY,
        Dictionary<string, VisionValue> outputs,
        Dictionary<string, object?> summary,
        List<VisionOverlay> overlays,
        CancellationToken token)
    {
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
                if (p.AnisotropicScale)
                {
                    if (p.MaxDeformationPx > 0)
                        FindAnisoShapeWithOptions(matchingScene, model, p.SearchStart, p.SearchEnd - p.SearchStart,
                            p.SearchScaleYMin, p.SearchScaleYMax, p.SearchScaleXMin, p.SearchScaleXMax, p.MinScore, p.MaxClutterScore,
                            searchMatchLimit, p.ShapePyramidLevels, p.MaxDeformationPx, p.ClutterEnabled, p.MaxOverlap, p.Greediness,
                            out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                    else model.FindAnisoShapeModel(matchingScene, p.SearchStart, p.SearchEnd - p.SearchStart,
                            p.SearchScaleYMin, p.SearchScaleYMax, p.SearchScaleXMin, p.SearchScaleXMax,
                            p.MinScore, searchMatchLimit, p.MaxOverlap, "least_squares", p.ShapePyramidLevels, p.Greediness,
                            out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                }
                else if (p.ScaleTolerance > 0)
                {
                    if (p.MaxDeformationPx > 0)
                        FindScaledShapeWithOptions(matchingScene, model, p.SearchStart, p.SearchEnd - p.SearchStart,
                            p.SearchScaleMin, p.SearchScaleMax, p.MinScore, p.MaxClutterScore, searchMatchLimit,
                            p.ShapePyramidLevels, p.MaxDeformationPx, p.ClutterEnabled, p.MaxOverlap, p.Greediness, out rows, out cols,
                            out angles, out scalesX, out scores);
                    else model.FindScaledShapeModel(matchingScene, p.SearchStart, p.SearchEnd - p.SearchStart, p.SearchScaleMin, p.SearchScaleMax, p.MinScore, searchMatchLimit, p.MaxOverlap, "least_squares", p.ShapePyramidLevels, p.Greediness, out rows, out cols, out angles, out scalesX, out scores);
                    scalesY = new JlTuple(scalesX);
                }
                else
                {
                    if (p.MaxDeformationPx > 0)
                        FindShapeWithOptions(matchingScene, model, p.SearchStart, p.SearchEnd - p.SearchStart, p.MinScore,
                            p.MaxClutterScore, searchMatchLimit, p.ShapePyramidLevels, p.MaxDeformationPx, p.ClutterEnabled, p.MaxOverlap, p.Greediness,
                            out rows, out cols, out angles, out scores);
                    else matchingScene.FindShapeModel(model, p.SearchStart, p.SearchEnd - p.SearchStart, p.MinScore, searchMatchLimit, p.MaxOverlap, "least_squares", p.ShapePyramidLevels, p.Greediness, out rows, out cols, out angles, out scores);
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
                    p.ShapePyramidLevels = legacyAuto.PyramidLevels;
                    p.Step = legacyAuto.AngleStepDeg * Math.PI / 180d;
                    p.MinContrast = legacyAuto.MinContrast;
                    if (p.ClutterEnabled && p.ClutterContrast < p.MinContrast)
                        throw new InvalidOperationException(
                            $"Clutter Contrast ({p.ClutterContrast}) must be at least the auto-tuned Minimum Feature Contrast ({p.MinContrast}). Lower Clutter Contrast or lock min_contrast in autoTuneLockedParams.");
                    summary["angleStepDeg"] = p.Step * 180 / Math.PI;
                    summary["pyramidLevels"] = p.ShapePyramidLevels;
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
                { summary["reason"] = "template-has-no-contour"; outputs["image"] = VisionValue.Image(source.CloneOwned()); return new(matchedCandidates, new(outputs, summary, overlays)); }
                var moments = JLVisionOperators.Moments(bestContour);
                originX = moments.M10 / moments.M00; originY = moments.M01 / moments.M00;
                templateBox = JLVisionOperators.BoundingRect(bestContour);
                shapeLease = JLVisionModelCache.Acquire(Key(templateBuffer, true, p.Range, p.Step, originX, originY, p.ShapePyramidLevels, p.ScaleMin, p.ScaleMax, p.ScaleStep, p.IgnorePolarity, p.MinContrast,
                    p.TrainingScaleXMin, p.TrainingScaleXMax, p.TrainingScaleYMin, p.TrainingScaleYMax,
                    p.ClutterEnabled ? $"{RoiFingerprint(p.ClutterRoi)}:{p.ClutterContrast}" : string.Empty), () =>
                {
                    var created = new JlShapeModel();
                    try
                    {
                        using var levels = new JlTuple(p.ShapePyramidLevels);
                        using var delta = new JlTuple(p.Step); using var optimize = new JlTuple("auto"); using var contrast = new JlTuple("auto"); using var minimum = new JlTuple(p.MinContrast);
                        var metric = p.IgnorePolarity ? "ignore_global_polarity" : "use_polarity";
                        if (p.AnisotropicScale)
                        {
                            using var scaleDelta = new JlTuple(p.ScaleStep);
                            created.CreateAnisoShapeModel(template, levels, -p.Range, 2 * p.Range, delta,
                                p.TrainingScaleYMin, p.TrainingScaleYMax, scaleDelta,
                                p.TrainingScaleXMin, p.TrainingScaleXMax, scaleDelta, optimize, metric, contrast, minimum);
                        }
                        else if (p.ScaleTolerance > 0)
                        {
                            using var scaleDelta = new JlTuple(p.ScaleStep);
                            created.CreateScaledShapeModel(template, levels, -p.Range, 2 * p.Range, delta, p.ScaleMin, p.ScaleMax, scaleDelta, optimize, metric, contrast, minimum);
                        }
                        else created.CreateShapeModel(template, levels, -p.Range, 2 * p.Range, delta, optimize, metric, contrast, minimum);
                        created.SetShapeModelOrigin(originY - (templateBuffer.Height - 1) / 2d, originX - (templateBuffer.Width - 1) / 2d);
                        if (p.ClutterEnabled)
                            ApplyShapeModelClutter(created, template, templateBuffer.Width, templateBuffer.Height,
                                p.ClutterRoi!, 0, 0, p.ClutterContrast, p.MinContrast, token);
                        return created;
                    }
                    catch { created.Dispose(); throw; }
                });
                model = (JlShapeModel)shapeLease.Model; summary["modelCacheHit"] = shapeLease.Hit;
            }
            JlTuple rows, cols, angles, scores, scalesX, scalesY;
            try
            {
                if (p.ContrastEnabled)
                {
                    using var contrastTrainingImage = p.ContrastFractionThreshold <= 0 ? null
                        : savedModel.Length == 0 ? JLVisionImages.ToMat(template)
                        : DecodeGrayscaleImage(node.GetString("trainedSourceImage", string.Empty), "saved training image");
                    var trainingOriginImageX = savedModel.Length == 0 ? originX
                        : node.GetDouble("trainedOriginImageX", node.GetDouble("trainedOriginX", originX)) + node.GetDouble("trainedPoseOffsetX", 0);
                    var trainingOriginImageY = savedModel.Length == 0 ? originY
                        : node.GetDouble("trainedOriginImageY", node.GetDouble("trainedOriginY", originY)) + node.GetDouble("trainedPoseOffsetY", 0);
                    contrastReference = CreateContrastReference(model, contrastTrainingImage, trainingOriginImageX, trainingOriginImageY);
                }
                if (p.AnisotropicScale)
                {
                    if (p.ClutterEnabled || p.MaxDeformationPx > 0)
                        FindAnisoShapeWithOptions(matchingScene, model, p.SearchStart, p.SearchEnd - p.SearchStart,
                            p.SearchScaleYMin, p.SearchScaleYMax, p.SearchScaleXMin, p.SearchScaleXMax, p.MinScore,
                            p.MaxClutterScore, searchMatchLimit, p.ShapePyramidLevels, p.MaxDeformationPx, p.ClutterEnabled, p.MaxOverlap, p.Greediness,
                            out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                    else model.FindAnisoShapeModel(matchingScene, p.SearchStart, p.SearchEnd - p.SearchStart,
                        p.SearchScaleYMin, p.SearchScaleYMax, p.SearchScaleXMin, p.SearchScaleXMax,
                        p.MinScore, searchMatchLimit, p.MaxOverlap, "least_squares", p.ShapePyramidLevels, p.Greediness,
                        out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                }
                else if (p.ScaleTolerance > 0)
                {
                    if (p.ClutterEnabled || p.MaxDeformationPx > 0)
                        FindScaledShapeWithOptions(matchingScene, model, p.SearchStart, p.SearchEnd - p.SearchStart,
                            p.SearchScaleMin, p.SearchScaleMax, p.MinScore, p.MaxClutterScore, searchMatchLimit,
                            p.ShapePyramidLevels, p.MaxDeformationPx, p.ClutterEnabled, p.MaxOverlap, p.Greediness,
                            out rows, out cols, out angles, out scalesX, out scores);
                    else model.FindScaledShapeModel(matchingScene, p.SearchStart, p.SearchEnd - p.SearchStart, p.SearchScaleMin, p.SearchScaleMax, p.MinScore, searchMatchLimit, p.MaxOverlap, "least_squares", p.ShapePyramidLevels, p.Greediness, out rows, out cols, out angles, out scalesX, out scores);
                    scalesY = new JlTuple(Enumerable.Range(0, scalesX.Length).Select(index => scalesX[index].D).ToArray());
                }
                else
                {
                    if (p.ClutterEnabled || p.MaxDeformationPx > 0)
                        FindShapeWithOptions(matchingScene, model, p.SearchStart, p.SearchEnd - p.SearchStart, p.MinScore,
                            p.MaxClutterScore, searchMatchLimit, p.ShapePyramidLevels, p.MaxDeformationPx, p.ClutterEnabled, p.MaxOverlap, p.Greediness,
                            out rows, out cols, out angles, out scores);
                    else matchingScene.FindShapeModel(model, p.SearchStart, p.SearchEnd - p.SearchStart, p.MinScore, searchMatchLimit, p.MaxOverlap, "least_squares", p.ShapePyramidLevels, p.Greediness, out rows, out cols, out angles, out scores);
                    scalesX = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                    scalesY = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                }
            }
            finally { shapeLease?.Dispose(); }
            using (rows) using (cols) using (angles) using (scores) using (scalesX) using (scalesY)
            {
                var matchCount = p.ClutterEnabled ? scores.Length / 2 : scores.Length;
                for (var index = 0; index < matchCount; index++)
                    matchedCandidates.Add(new(cols[index].D, rows[index].D, angles[index].D, scalesX[index].D, scalesY[index].D, scores[index].D,
                        templateBox.Width, templateBox.Height, originX, originY, index,
                        p.ClutterEnabled ? scores[matchCount + index].D : null, contrastReference));
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
                p.Step = nccAutoTune.AngleStepDeg * Math.PI / 180d;
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
            summary["method"] = p.Method;
            summary["templateMask"] = templateRoi is not null;
            // Soft-NMS needs a lower Find floor so neighbors arrive and can be decayed below minScore.
            var findMinScore = p.NmsMode == "soft"
                ? Math.Max(0.05, Math.Min(p.MinScore, p.MinScore * 0.5))
                : p.MinScore;
            summary["nccFindMinScore"] = p.SquaredDifference ? 0.001 : findMinScore;
            AppendNccCandidates(matchingScene, template, templateBuffer, templateRoi, p.SearchStart, p.SearchEnd, p.Step, requestedPyramidLevels, pyramidLevels,
                p.SquaredDifference, findMinScore, p.AcceptLimit, p.NccScaleMin, p.NccScaleMax, p.NccScaleStep, p.SearchRoi is not null, p.IgnorePolarity, p.NmsMode, p.DuplicateOverlapThreshold, p.NmsAngleToleranceDeg, token, summary, matchedCandidates);
        }
        return new(matchedCandidates, null);
    }

    private static void EmitScoreMap(
        NodeExecutionContext context,
        NodeDefinition node,
        MatchParameters p,
        bool shape,
        JlImage gray,
        Mat templateBuffer,
        VisionImage source,
        Dictionary<string, VisionValue> outputs,
        Dictionary<string, object?> summary,
        CancellationToken token)
    {
        if (!p.EmitScoreMap)
        {
            if (!shape) summary["scoreMapEmitted"] = false;
            return;
        }
        using var sceneGray = JLVisionImages.ToMat(gray);
        Mat? crop = null;
        try
        {
            Mat scoreScene = sceneGray;
            var ox = 0;
            var oy = 0;
            var searchRoi = p.SearchRoi;
            if (searchRoi is not null)
            {
                var searchBounds = RoiBounds(searchRoi);
                var imageBounds = new Rect(0, 0, source.Width, source.Height);
                var clippedSearch = searchBounds & imageBounds;
                if (clippedSearch.Width > 0 && clippedSearch.Height > 0)
                {
                    var radius = Math.Sqrt((double)templateBuffer.Width * templateBuffer.Width + (double)templateBuffer.Height * templateBuffer.Height);
                    var margin = (int)Math.Ceiling(radius * Math.Max(1d, p.NccScaleMax)) + 2;
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
            var ignoreRoi = p.IgnoreRoi;
            var softIgnore = ignoreRoi is not null && p.IgnoreMode == "soft";
            var useTemplateMask = p.ScoreMapWeighting == "templatemask";
            var anglePlanesDeg = BuildScoreMapAnglePlanesDeg(
                p.ScoreMapAngleMode, p.SearchStart, p.SearchEnd, p.Step, p.ScoreMapStackMaxPlanes,
                summary, outputs);
            var planes = new List<Mat>(anglePlanesDeg.Count);
            try
            {
                foreach (var planeAngle in anglePlanesDeg)
                {
                    token.ThrowIfCancellationRequested();
                    planes.Add(BuildNccScoreMap(
                        scoreScene, templateBuffer, p.SquaredDifference, ox, oy, source.Width, source.Height,
                        ignoreRoi, softIgnore, p.IgnoreSoftWeight, p.IgnoreFeatherPx, planeAngle,
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
                summary["scoreMapMode"] = p.SquaredDifference
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
}
