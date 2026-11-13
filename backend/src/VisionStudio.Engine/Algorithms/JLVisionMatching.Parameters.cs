using OpenCvSharp;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-13 从 Execute 的 138-290 行抽出参数解析，纯移动：默认值、钳制范围、异常消息与读取顺序均未改。
// 读取仍在本文件（JLVisionMatching*.cs 家族）内，static-audit 的参数契约按 glob 聚合。
internal static partial class JLVisionMatching
{
    private sealed record MatchParameters
    {
        public double Range { get; set; }
        public double TrainingRange { get; set; }
        public int ShapePyramidLevels { get; set; }
        public double SearchStart { get; set; }
        public double SearchEnd { get; set; }
        public double Step { get; set; }
        public double ScaleTolerance { get; set; }
        public double ScaleToleranceX { get; set; }
        public double ScaleToleranceY { get; set; }
        public bool AnisotropicScale { get; set; }
        public double ScaleMin { get; set; }
        public double ScaleMax { get; set; }
        public double TrainingScaleXMin { get; set; }
        public double TrainingScaleXMax { get; set; }
        public double TrainingScaleYMin { get; set; }
        public double TrainingScaleYMax { get; set; }
        public double SearchScaleXMin { get; set; }
        public double SearchScaleXMax { get; set; }
        public double SearchScaleYMin { get; set; }
        public double SearchScaleYMax { get; set; }
        public double SearchScaleMin { get; set; }
        public double SearchScaleMax { get; set; }
        public double ScaleStep { get; set; }
        public VisionRoi? ClutterRoi { get; set; }
        public bool ClutterEnabled { get; set; }
        public bool IgnorePolarity { get; set; }
        public int MinContrast { get; set; }
        public int ClutterContrast { get; set; }
        public double MinScore { get; set; }
        public string Method { get; set; } = string.Empty;
        public bool SquaredDifference { get; set; }
        public int AcceptLimit { get; set; }
        public int Maximum { get; set; }
        public double DuplicateOverlapThreshold { get; set; }
        public string NmsMode { get; set; } = string.Empty;
        public string NmsSortKey { get; set; } = string.Empty;
        public double NmsAngleToleranceDeg { get; set; }
        public VisionRoi? SearchRoi { get; set; }
        public VisionRoi? IgnoreRoi { get; set; }
        public double NccScaleMin { get; set; }
        public double NccScaleMax { get; set; }
        public double NccScaleStep { get; set; }
        public bool EmitScoreMap { get; set; }
        public string IgnoreMode { get; set; } = string.Empty;
        public double IgnoreSoftWeight { get; set; }
        public int IgnoreFeatherPx { get; set; }
        public string ScoreMapAngleMode { get; set; } = string.Empty;
        public string ScoreMapWeighting { get; set; } = string.Empty;
        public int ScoreMapStackMaxPlanes { get; set; }
        public double MaxClutterScore { get; set; }
        public int MaxDeformationPx { get; set; }
        public double Greediness { get; set; }
        public double MaxOverlap { get; set; }
        public bool ScoreUsingClutter { get; set; }
        public double RuntimeContrastThreshold { get; set; }
        public double ContrastFractionThreshold { get; set; }
        public bool ContrastEnabled { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double MinArea { get; set; }
    }

    private static MatchParameters ParseMatchParameters(NodeExecutionContext context, NodeDefinition node, bool shape, bool hasSavedModel, Mat templateBuffer)
    {
        var range = Math.Clamp(node.GetDouble("angleRangeDeg", shape ? 180 : 0), 0, 180) * Math.PI / 180;
        var trainingRange = Math.Clamp(node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)), 0, 180) * Math.PI / 180;
        var shapePyramidLevels = shape ? node.GetInt("pyramidLevels", 0) : 0;
        if (shapePyramidLevels is < 0 or > 8) throw new InvalidOperationException("pyramidLevels must be between 0 (automatic) and 8.");
        var searchStart = -range;
        var searchEnd = range;
        if (shape && hasSavedModel)
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
        if (shape && hasSavedModel)
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
        var originX = (templateBuffer.Width - 1) / 2d; var originY = (templateBuffer.Height - 1) / 2d;
        var minArea = node.GetDouble("minArea", 200);
        return new MatchParameters
        {
            Range = range,
            TrainingRange = trainingRange,
            ShapePyramidLevels = shapePyramidLevels,
            SearchStart = searchStart,
            SearchEnd = searchEnd,
            Step = step,
            ScaleTolerance = scaleTolerance,
            ScaleToleranceX = scaleToleranceX,
            ScaleToleranceY = scaleToleranceY,
            AnisotropicScale = anisotropicScale,
            ScaleMin = scaleMin,
            ScaleMax = scaleMax,
            TrainingScaleXMin = trainingScaleXMin,
            TrainingScaleXMax = trainingScaleXMax,
            TrainingScaleYMin = trainingScaleYMin,
            TrainingScaleYMax = trainingScaleYMax,
            SearchScaleXMin = searchScaleXMin,
            SearchScaleXMax = searchScaleXMax,
            SearchScaleYMin = searchScaleYMin,
            SearchScaleYMax = searchScaleYMax,
            SearchScaleMin = searchScaleMin,
            SearchScaleMax = searchScaleMax,
            ScaleStep = scaleStep,
            ClutterRoi = clutterRoi,
            ClutterEnabled = clutterEnabled,
            IgnorePolarity = ignorePolarity,
            MinContrast = minContrast,
            ClutterContrast = clutterContrast,
            MinScore = minScore,
            Method = method,
            SquaredDifference = squaredDifference,
            AcceptLimit = acceptLimit,
            Maximum = maximum,
            DuplicateOverlapThreshold = duplicateOverlapThreshold,
            NmsMode = nmsMode,
            NmsSortKey = nmsSortKey,
            NmsAngleToleranceDeg = nmsAngleToleranceDeg,
            SearchRoi = searchRoi,
            IgnoreRoi = ignoreRoi,
            NccScaleMin = nccScaleMin,
            NccScaleMax = nccScaleMax,
            NccScaleStep = nccScaleStep,
            EmitScoreMap = emitScoreMap,
            IgnoreMode = ignoreMode,
            IgnoreSoftWeight = ignoreSoftWeight,
            IgnoreFeatherPx = ignoreFeatherPx,
            ScoreMapAngleMode = scoreMapAngleMode,
            ScoreMapWeighting = scoreMapWeighting,
            ScoreMapStackMaxPlanes = scoreMapStackMaxPlanes,
            MaxClutterScore = maxClutterScore,
            MaxDeformationPx = maxDeformationPx,
            Greediness = greediness,
            MaxOverlap = maxOverlap,
            ScoreUsingClutter = scoreUsingClutter,
            RuntimeContrastThreshold = runtimeContrastThreshold,
            ContrastFractionThreshold = contrastFractionThreshold,
            ContrastEnabled = contrastEnabled,
            OriginX = originX,
            OriginY = originY,
            MinArea = minArea
        };
    }

    private static (Dictionary<string, VisionValue> Outputs, Dictionary<string, object?> Summary) BuildInitialOutputs(
        MatchParameters p, bool shape, bool hasSavedModel, string templateSource)
    {
        var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0) };
        if (shape)
        {
            outputs["scale"] = VisionValue.Double(1);
            outputs["scaleX"] = VisionValue.Double(1);
            outputs["scaleY"] = VisionValue.Double(1);
            if (p.ClutterEnabled) outputs["clutterScore"] = VisionValue.Double(0);
            if (p.ScoreUsingClutter) outputs["nativeScore"] = VisionValue.Double(0);
            if (p.ContrastEnabled) outputs["contrast"] = VisionValue.Double(0);
            if (p.ContrastFractionThreshold > 0) outputs["contrastFraction"] = VisionValue.Double(0);
        }
        var summary = new Dictionary<string, object?> { ["found"] = false, ["bestScore"] = 0d, ["score"] = 0d, ["templateSource"] = templateSource, ["algorithmBackend"] = "jlvision", ["angleRangeDeg"] = p.Range * 180 / Math.PI, ["angleStepDeg"] = p.Step * 180 / Math.PI };
        if (!shape)
        {
            summary["scaleMinPercent"] = p.NccScaleMin * 100;
            summary["scaleMaxPercent"] = p.NccScaleMax * 100;
            summary["scaleStepPercent"] = p.NccScaleStep * 100;
            summary["scaleSearch"] = Math.Abs(p.NccScaleMin - 1) < 1e-9 && Math.Abs(p.NccScaleMax - 1) < 1e-9 ? "fixed" : "discrete-resampled-ncc";
            summary["maxTargets"] = p.AcceptLimit;
            summary["duplicateOverlapThreshold"] = p.DuplicateOverlapThreshold;
            summary["nmsMode"] = p.NmsMode;
            summary["nmsSortKey"] = p.NmsSortKey switch
            {
                "scorethenabsangle" => "scoreThenAbsAngle",
                "scorethenscale" => "scoreThenScale",
                _ => "score"
            };
            summary["nmsAngleToleranceDeg"] = p.NmsAngleToleranceDeg;
            summary["nmsSuppressedCount"] = 0;
            summary["nmsSoftDecayedCount"] = 0;
            summary["searchAngleStartDeg"] = p.SearchStart * 180 / Math.PI;
            summary["searchAngleEndDeg"] = p.SearchEnd * 180 / Math.PI;
            summary["ignorePolarity"] = p.IgnorePolarity;
            summary["ignoreMask"] = p.IgnoreRoi is not null;
            summary["ignoreMode"] = p.IgnoreRoi is null ? "none" : p.IgnoreMode;
            summary["ignoreSoftWeight"] = p.IgnoreSoftWeight;
            summary["ignoreFeatherPx"] = p.IgnoreFeatherPx;
            summary["emitScoreMap"] = p.EmitScoreMap;
            summary["scoreMapAngleMode"] = p.ScoreMapAngleMode;
            summary["scoreMapWeighting"] = p.ScoreMapWeighting;
            summary["scoreMapStackMaxPlanes"] = p.ScoreMapStackMaxPlanes;
        }
        if (shape)
        {
            summary["scaleTolerancePercent"] = p.ScaleTolerance * 100;
            summary["scaleToleranceXPercent"] = p.ScaleToleranceX * 100;
            summary["scaleToleranceYPercent"] = p.ScaleToleranceY * 100;
            summary["ignorePolarity"] = p.IgnorePolarity;
            summary["clutterEnabled"] = p.ClutterEnabled;
            if (p.ClutterEnabled) summary["maxClutterScore"] = p.MaxClutterScore;
            summary["scoreUsingClutter"] = p.ScoreUsingClutter;
            if (p.ScoreUsingClutter) summary["clutterScoreAdjustment"] = "nativeScore × (1 - clutterScore)";
            summary["runtimeContrastThreshold"] = p.RuntimeContrastThreshold;
            summary["contrastFractionThreshold"] = p.ContrastFractionThreshold;
            summary["contrastCheckEnabled"] = p.ContrastEnabled;
            summary["maxDeformationPx"] = p.MaxDeformationPx;
            summary["greediness"] = p.Greediness;
            summary["maxOverlap"] = p.MaxOverlap;
            summary["searchAngleStartDeg"] = p.SearchStart * 180 / Math.PI;
            summary["searchAngleEndDeg"] = p.SearchEnd * 180 / Math.PI;
            summary["searchScaleMinPercent"] = p.SearchScaleMin * 100;
            summary["searchScaleMaxPercent"] = p.SearchScaleMax * 100;
            summary["searchScaleXMinPercent"] = p.SearchScaleXMin * 100;
            summary["searchScaleXMaxPercent"] = p.SearchScaleXMax * 100;
            summary["searchScaleYMinPercent"] = p.SearchScaleYMin * 100;
            summary["searchScaleYMaxPercent"] = p.SearchScaleYMax * 100;
            if (hasSavedModel)
            {
                summary["trainingAngleCoverageDeg"] = p.TrainingRange * 180 / Math.PI;
                summary["trainingScaleMinPercent"] = Math.Min(p.TrainingScaleXMin, p.TrainingScaleYMin) * 100;
                summary["trainingScaleMaxPercent"] = Math.Max(p.TrainingScaleXMax, p.TrainingScaleYMax) * 100;
                summary["trainingScaleXMinPercent"] = p.TrainingScaleXMin * 100;
                summary["trainingScaleXMaxPercent"] = p.TrainingScaleXMax * 100;
                summary["trainingScaleYMinPercent"] = p.TrainingScaleYMin * 100;
                summary["trainingScaleYMaxPercent"] = p.TrainingScaleYMax * 100;
            }
        }
        return (outputs, summary);
    }
}
