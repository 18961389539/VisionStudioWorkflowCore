using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
// 金字塔 NCC 搜索与诊断得分图：候选枚举、尺度采样、角度平面与缓存键。
internal static partial class JLVisionMatching
{
    /// <summary>
    /// Pyramid NCC with optional discrete uniform scale samples and an asymmetric angle window.
    /// NCC has no continuous scaled model — uniform scale is a discrete set of resampled templates.
    /// When <paramref name="templateRoi"/> is set, the template is ReduceDomain-masked before
    /// <c>CreateNccModel</c>; <c>FindNccModel</c> then uses that masked model (JLVision/HALCON-style —
    /// there is no separate mask argument on Find). SqDiff re-scoring uses the template domain via Intensity.
    /// Scene ignoreRoi is separate (hard ReduceDomain / soft mean-lerp) and is not a soft scene-weighted NCC.
    /// <paramref name="ignorePolarity"/> maps to CreateNccModel metric ignore_global_polarity (vs use_polarity).
    /// Clutter and local deformation still belong to match.shape.
    /// </summary>
    private static void AppendNccCandidates(
        JlImage matchingScene,
        JlImage grayTemplate,
        Mat templateBuffer,
        VisionRoi? templateRoi,
        double searchStart,
        double searchEnd,
        double step,
        int requestedPyramidLevels,
        int resolvedPyramidLevels,
        bool squaredDifference,
        double minScore,
        int acceptLimit,
        double scaleMin,
        double scaleMax,
        double scaleStep,
        bool widenedSearch,
        bool ignorePolarity,
        string nmsMode,
        double duplicateOverlapThreshold,
        double nmsAngleToleranceDeg,
        CancellationToken token,
        Dictionary<string, object?> summary,
        List<ShapeMatchCandidate> matchedCandidates)
    {
        matchingScene.GetImageSize(out int sceneWidth, out int sceneHeight);
        var scales = TemplateScales(scaleMin, scaleMax, scaleStep);
        var trainRange = Math.Max(Math.Abs(searchStart), Math.Abs(searchEnd));
        var angleExtent = searchEnd - searchStart;
        var metric = ignorePolarity ? "ignore_global_polarity" : "use_polarity";
        summary["scaleSamples"] = scales.Count;
        summary["polarityMode"] = metric;
        summary["nccTrainingAngleCoverageDeg"] = trainRange * 180 / Math.PI;
        summary["subpixel"] = true;
        var searched = 0;
        var cacheHit = true;
        var candidateIndex = 0;
        var rawCandidateCount = 0;
        var peakScore = 0d;
        foreach (var scale in scales)
        {
            token.ThrowIfCancellationRequested();
            var scaledWidth = Math.Max(1, (int)Math.Round(templateBuffer.Width * scale));
            var scaledHeight = Math.Max(1, (int)Math.Round(templateBuffer.Height * scale));
            if (scaledWidth > sceneWidth || scaledHeight > sceneHeight) continue;
            searched++;
            var unscaled = Math.Abs(scale - 1d) < 1e-9;
            var masked = templateRoi is not null;
            Mat? ownedGray = null;
            JlImage? ownedTemplate = null;
            JlImage? ownedMask = null;
            JlRegion? ownedRegion = null;
            JlImage? ownedReduced = null;
            try
            {
                JlImage modelImage;
                Mat keyImage;
                int modelWidth;
                int modelHeight;
                if (unscaled && !masked)
                {
                    modelImage = grayTemplate;
                    keyImage = templateBuffer;
                    modelWidth = templateBuffer.Width;
                    modelHeight = templateBuffer.Height;
                }
                else
                {
                    using var grayMat = JLVisionImages.ToMat(grayTemplate);
                    ownedGray = new Mat();
                    if (unscaled) grayMat.CopyTo(ownedGray);
                    else JLVisionOperators.Resize(grayMat, ownedGray, new Size(scaledWidth, scaledHeight), 0, 0,
                        scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear);
                    ownedTemplate = JLVisionImages.FromMat(ownedGray);
                    modelImage = ownedTemplate;
                    keyImage = ownedGray;
                    modelWidth = ownedGray.Width;
                    modelHeight = ownedGray.Height;
                    if (masked)
                    {
                        using var mask = new Mat(templateBuffer.Height, templateBuffer.Width, MatType.CV_8UC1, Scalar.All(0));
                        FillRoiMask(mask, templateRoi, new Rect(0, 0, templateBuffer.Width, templateBuffer.Height), token);
                        using var scaledMask = new Mat();
                        if (unscaled) mask.CopyTo(scaledMask);
                        else JLVisionOperators.Resize(mask, scaledMask, new Size(modelWidth, modelHeight), 0, 0, InterpolationFlags.Nearest);
                        if (Cv2.CountNonZero(scaledMask) < 16)
                            throw new InvalidOperationException("Template mask must cover at least 16 pixels.");
                        ownedMask = JLVisionImages.FromMat(scaledMask);
                        ownedRegion = ownedMask.Threshold(1d, 255d);
                        ownedReduced = ownedTemplate.ReduceDomain(ownedRegion);
                        modelImage = ownedReduced;
                    }
                }

                var modelOriginX = (modelWidth - 1) / 2d;
                var modelOriginY = (modelHeight - 1) / 2d;
                var levels = requestedPyramidLevels == 0 && !unscaled
                    ? Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1d, Math.Min(modelWidth, modelHeight) / 6d))) + 1, 1, 8)
                    : resolvedPyramidLevels;
                var fingerprint = unscaled && !masked ? "" : RoiFingerprint(templateRoi) + ":" + scale.ToString("R");
                using var lease = JLVisionModelCache.Acquire(
                    Key(keyImage, false, trainRange, step, modelOriginX, modelOriginY, levels, ignorePolarity: ignorePolarity, clutterConfig: fingerprint),
                    () =>
                    {
                        var created = new JlNCCModel();
                        try
                        {
                            // Train covering +/- trainRange so any asymmetric Find window inside that span is valid.
                            created.CreateNccModel(modelImage, levels, -trainRange, 2 * trainRange, step, metric);
                            return created;
                        }
                        catch
                        {
                            created.Dispose();
                            throw;
                        }
                    });
                cacheHit &= lease.Hit;
                var numMatches = squaredDifference || acceptLimit > 1 || scales.Count > 1 || widenedSearch || angleExtent > 1e-9
                    ? Math.Min(100, Math.Max(acceptLimit, acceptLimit * 4))
                    : 1;
                var model = (JlNCCModel)lease.Model;
                if (masked)
                {
                    using var modelRegion = model.GetNccModelRegion();
                    var regionArea = modelRegion.AreaCenter(out double regionRow, out double regionCol);
                    summary["findUsesTemplateMask"] = true;
                    summary["nccModelRegionArea"] = regionArea;
                    summary["nccModelRegionRow"] = regionRow;
                    summary["nccModelRegionCol"] = regionCol;
                    summary["nccTemplateMaskMode"] = "create-ncc-reduce-domain";
                }
                else if (!summary.ContainsKey("findUsesTemplateMask"))
                {
                    summary["findUsesTemplateMask"] = false;
                    summary["nccTemplateMaskMode"] = "none";
                }
                // JLVision-first MaxOverlap:
                //  - single-target: HALCON-style 0.5 coarse dedupe
                //  - hard multi-match: pass duplicateOverlapThreshold to FindNccModel (primary dedupe)
                //  - soft multi-match (explicit nmsMode=soft): raise to 0.95 so neighbors reach C# Soft-NMS
                //  - duplicateOverlapThreshold=0 (NMS off): raise so all near-duplicates arrive for center-distance only
                // Fine AABB / angle-aware pass remains in C#; Soft-NMS is advanced opt-in only.
                // Angle-aware hard NMS must still see overlapping different orientations, so raise MaxOverlap
                // when nmsAngleToleranceDeg > 0 (C# keeps same-orientation suppress).
                double nativeOverlap;
                string overlapPolicy;
                if (acceptLimit <= 1)
                {
                    nativeOverlap = 0.5;
                    overlapPolicy = "single-0.5";
                }
                else if (nmsMode == "soft")
                {
                    nativeOverlap = 0.95;
                    overlapPolicy = "soft-raised-0.95";
                }
                else if (duplicateOverlapThreshold <= 0)
                {
                    nativeOverlap = 0.95;
                    overlapPolicy = "nms-off-raised-0.95";
                }
                else if (nmsAngleToleranceDeg > 0)
                {
                    nativeOverlap = 0.95;
                    overlapPolicy = "hard-angle-aware-raised-0.95";
                }
                else
                {
                    nativeOverlap = Math.Clamp(duplicateOverlapThreshold, 0.05, 0.95);
                    overlapPolicy = "hard-native-maxoverlap";
                }
                summary["nccNativeOverlap"] = nativeOverlap;
                summary["nccOverlapPolicy"] = overlapPolicy;
                matchingScene.FindNccModel(model, searchStart, angleExtent, squaredDifference ? .001 : minScore, numMatches, nativeOverlap, "true", 0,
                    out var rows, out var cols, out var angles, out var scores);
                using (rows)
                using (cols)
                using (angles)
                using (scores)
                {
                    for (var index = 0; index < scores.Length; index++)
                    {
                        var angle = angles[index].D;
                        if (angle < searchStart - 1e-9 || angle > searchEnd + 1e-9) continue;
                        var candidateScore = squaredDifference
                            ? Ssd(matchingScene, modelImage, cols[index].D, rows[index].D, angle, modelOriginX, modelOriginY)
                            : scores[index].D;
                        rawCandidateCount++;
                        if (candidateScore > peakScore) peakScore = candidateScore;
                        matchedCandidates.Add(new(cols[index].D, rows[index].D, angle, scale, scale, candidateScore,
                            modelWidth, modelHeight, modelOriginX, modelOriginY, candidateIndex++));
                    }
                }
            }
            finally
            {
                ownedReduced?.Dispose();
                ownedRegion?.Dispose();
                ownedMask?.Dispose();
                ownedTemplate?.Dispose();
                ownedGray?.Dispose();
            }
        }

        if (searched == 0) throw new InvalidOperationException("Template is larger than the scene image.");
        summary["modelCacheHit"] = cacheHit;
        summary["rawCandidateCount"] = rawCandidateCount;
        summary["peakScore"] = rawCandidateCount > 0 ? peakScore : 0d;
        if (!summary.ContainsKey("findUsesTemplateMask"))
        {
            summary["findUsesTemplateMask"] = false;
            summary["nccTemplateMaskMode"] = "none";
        }
    }




    /// <summary>
    /// Diagnostic OpenCV NCC response at unit scale and a chosen template rotation (not the JLVision pyramid surface).
    /// When <paramref name="useTemplateMask"/> is true, calls MatchTemplate with an 8-bit template mask
    /// (from templateRoi, or all-ones) — true OpenCV mask-weighted correlation for the supported methods.
    /// Scene ignoreRoi is still applied on the canvas afterward (hard zero / soft weight), not as an OpenCV scene mask.
    /// </summary>
    private static Mat BuildNccScoreMap(
        Mat scoreScene,
        Mat templateBuffer,
        bool squaredDifference,
        int offsetX,
        int offsetY,
        int fullWidth,
        int fullHeight,
        VisionRoi? ignoreRoi,
        bool softIgnore,
        double ignoreSoftWeight,
        int ignoreFeatherPx,
        double templateAngleDeg,
        VisionRoi? templateRoi,
        bool useTemplateMask,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Mat? rotatedOwned = null;
        Mat? rotatedMaskOwned = null;
        Mat? probeMask = null;
        try
        {
            var probe = templateBuffer;
            if (useTemplateMask)
            {
                using var mask = new Mat(templateBuffer.Height, templateBuffer.Width, MatType.CV_8UC1, Scalar.All(255));
                if (templateRoi is not null)
                {
                    mask.SetTo(Scalar.All(0));
                    FillRoiMask(mask, templateRoi, new Rect(0, 0, templateBuffer.Width, templateBuffer.Height), token);
                    if (Cv2.CountNonZero(mask) < 16)
                        throw new InvalidOperationException("scoreMap templateMask must cover at least 16 template pixels.");
                }
                probeMask = mask.Clone();
            }
            if (Math.Abs(templateAngleDeg) > 1e-9)
            {
                rotatedOwned = RotateTemplateExpanded(templateBuffer, templateAngleDeg);
                probe = rotatedOwned;
                if (probeMask is not null)
                {
                    rotatedMaskOwned = RotateTemplateExpanded(probeMask, templateAngleDeg, InterpolationFlags.Nearest);
                    probeMask.Dispose();
                    probeMask = rotatedMaskOwned;
                }
            }
            if (probe.Width > scoreScene.Width || probe.Height > scoreScene.Height)
                throw new InvalidOperationException("Template is larger than the score-map search window.");
            using var result = new Mat();
            var mode = squaredDifference ? TemplateMatchModes.SqDiffNormed : TemplateMatchModes.CCoeffNormed;
            if (probeMask is not null)
                Cv2.MatchTemplate(scoreScene, probe, result, mode, probeMask);
            else
                Cv2.MatchTemplate(scoreScene, probe, result, mode);
            if (squaredDifference)
                Cv2.Subtract(Scalar.All(1), result, result);
            using var scaled = new Mat();
            result.ConvertTo(scaled, MatType.CV_8UC1, 255);
            var canvas = new Mat(fullHeight, fullWidth, MatType.CV_8UC1, Scalar.All(0));
            var dest = new Rect(offsetX, offsetY, scaled.Width, scaled.Height) & new Rect(0, 0, fullWidth, fullHeight);
            if (dest.Width > 0 && dest.Height > 0)
            {
                using var srcRoi = new Mat(scaled, new Rect(0, 0, dest.Width, dest.Height));
                using var dstRoi = new Mat(canvas, dest);
                srcRoi.CopyTo(dstRoi);
            }
            if (ignoreRoi is not null)
            {
                using var ignorePaint = new Mat(fullHeight, fullWidth, MatType.CV_8UC1, Scalar.All(0));
                FillRoiMask(ignorePaint, ignoreRoi, new Rect(0, 0, fullWidth, fullHeight), token);
                if (!softIgnore)
                    canvas.SetTo(Scalar.All(0), ignorePaint);
                else
                {
                    using var weight = BuildIgnoreWeightMap(ignorePaint, ignoreSoftWeight, ignoreFeatherPx);
                    using var canvasF = new Mat();
                    canvas.ConvertTo(canvasF, MatType.CV_32FC1);
                    Cv2.Multiply(canvasF, weight, canvasF);
                    canvasF.ConvertTo(canvas, MatType.CV_8UC1);
                }
            }
            probeMask?.Dispose();
            return canvas;
        }
        finally
        {
            // MatchTemplate can throw after the rotated mask was assigned; dispose every owned
            // buffer here instead of relying on the success path.
            probeMask?.Dispose();
            rotatedMaskOwned?.Dispose();
            rotatedOwned?.Dispose();
        }
    }

    private static Mat BuildUnitScaleNccScoreMap(
        Mat scoreScene,
        Mat templateBuffer,
        bool squaredDifference,
        int offsetX,
        int offsetY,
        int fullWidth,
        int fullHeight,
        VisionRoi? ignoreRoi,
        CancellationToken token)
        => BuildNccScoreMap(scoreScene, templateBuffer, squaredDifference, offsetX, offsetY, fullWidth, fullHeight,
            ignoreRoi, softIgnore: false, ignoreSoftWeight: 0, ignoreFeatherPx: 0, templateAngleDeg: 0,
            templateRoi: null, useTemplateMask: false, token);

    private static List<double> BuildScoreMapAnglePlanesDeg(
        string scoreMapAngleMode,
        double searchStart,
        double searchEnd,
        double stepRad,
        int maxPlanes,
        Dictionary<string, object?> summary,
        Dictionary<string, VisionValue> outputs)
    {
        if (scoreMapAngleMode == "stack")
        {
            var startDeg = searchStart * 180 / Math.PI;
            var endDeg = searchEnd * 180 / Math.PI;
            if (Math.Abs(endDeg - startDeg) < 1e-9) return [startDeg];
            var stepDeg = Math.Max(0.1, stepRad * 180 / Math.PI);
            var natural = (int)Math.Floor(Math.Abs(endDeg - startDeg) / stepDeg) + 1;
            var count = Math.Clamp(natural, 2, maxPlanes);
            var planes = new List<double>(count);
            for (var i = 0; i < count; i++)
                planes.Add(startDeg + (endDeg - startDeg) * i / (count - 1));
            return planes;
        }
        var mapAngleDeg = 0d;
        if (scoreMapAngleMode == "searchmid")
            mapAngleDeg = (searchStart + searchEnd) * 0.5 * 180 / Math.PI;
        else if (scoreMapAngleMode == "matched")
        {
            if (summary.TryGetValue("matchedAngle", out var matchedObj) && matchedObj is double matchedDeg)
                mapAngleDeg = matchedDeg;
            else if (outputs.TryGetValue("angle", out var angleVal))
                mapAngleDeg = angleVal.Require<double>("angle");
        }
        return [mapAngleDeg];
    }

    private static Mat MaxProjectScoreMaps(IReadOnlyList<Mat> planes)
    {
        var proj = planes[0].Clone();
        for (var i = 1; i < planes.Count; i++)
            Cv2.Max(proj, planes[i], proj);
        return proj;
    }

    private static Mat StackScoreMapMontage(IReadOnlyList<Mat> planes)
    {
        var h = planes[0].Height;
        var w = planes[0].Width;
        var stack = new Mat(h * planes.Count, w, MatType.CV_8UC1, Scalar.All(0));
        for (var i = 0; i < planes.Count; i++)
        {
            using var dst = new Mat(stack, new Rect(0, i * h, w, h));
            planes[i].CopyTo(dst);
        }
        return stack;
    }

    private static int FindScoreMapPeakPlane(IReadOnlyList<Mat> planes, Point peakLoc)
    {
        var best = 0;
        var bestVal = -1;
        for (var i = 0; i < planes.Count; i++)
        {
            if ((uint)peakLoc.X >= (uint)planes[i].Width || (uint)peakLoc.Y >= (uint)planes[i].Height) continue;
            var v = planes[i].At<byte>(peakLoc.Y, peakLoc.X);
            if (v > bestVal) { bestVal = v; best = i; }
        }
        return best;
    }

    private static Mat RotateTemplateExpanded(Mat source, double angleDeg, InterpolationFlags interpolation = InterpolationFlags.Linear)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        var cos = Math.Abs(matrix.At<double>(0, 0));
        var sin = Math.Abs(matrix.At<double>(0, 1));
        var width = Math.Max(1, (int)Math.Round(source.Width * cos + source.Height * sin));
        var height = Math.Max(1, (int)Math.Round(source.Width * sin + source.Height * cos));
        matrix.Set(0, 2, matrix.At<double>(0, 2) + (width / 2d - center.X));
        matrix.Set(1, 2, matrix.At<double>(1, 2) + (height / 2d - center.Y));
        var rotated = new Mat();
        var border = interpolation == InterpolationFlags.Nearest ? BorderTypes.Constant : BorderTypes.Replicate;
        Cv2.WarpAffine(source, rotated, matrix, new Size(width, height), interpolation, border, Scalar.All(0));
        return rotated;
    }

    private static List<double> TemplateScales(double scaleMin, double scaleMax, double scaleStep)
    {
        if (Math.Abs(scaleMin - 1d) < 1e-9 && Math.Abs(scaleMax - 1d) < 1e-9) return [1d];
        var samples = new List<double>();
        for (var percent = scaleMin * 100d; percent <= scaleMax * 100d + 1e-6; percent += scaleStep * 100d)
        {
            samples.Add(percent / 100d);
            if (samples.Count > 41)
                throw new InvalidOperationException("match.template scale search exceeds 41 samples. Increase scaleStepPercent or narrow the scale range.");
        }
        if (samples.Count == 0 || Math.Abs(samples[^1] - scaleMax) > 1e-6)
        {
            samples.Add(scaleMax);
            if (samples.Count > 41)
                throw new InvalidOperationException("match.template scale search exceeds 41 samples. Increase scaleStepPercent or narrow the scale range.");
        }
        return samples;
    }

    private static string Key(Mat template, bool shape, double range, double step, double x, double y, int levels = 0,
        double scaleMin = 1, double scaleMax = 1, double scaleStep = 0, bool ignorePolarity = false, int minContrast = 5,
        double scaleXMin = 1, double scaleXMax = 1, double scaleYMin = 1, double scaleYMax = 1, string clutterConfig = "")
    {
        using var packed = template.IsContinuous() ? null : template.Clone(); var buffer = packed ?? template;
        var bytes = new byte[checked((int)(buffer.Total() * buffer.ElemSize()))]; Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
        return FormattableString.Invariant($"{shape}:{buffer.Width}:{buffer.Height}:{buffer.Type()}:{range:R}:{step:R}:{x:R}:{y:R}:{levels}:{scaleMin:R}:{scaleMax:R}:{scaleStep:R}:{scaleXMin:R}:{scaleXMax:R}:{scaleYMin:R}:{scaleYMax:R}:{ignorePolarity}:{minContrast}:{clutterConfig}:{Convert.ToHexString(SHA256.HashData(bytes))}");
    }
}
