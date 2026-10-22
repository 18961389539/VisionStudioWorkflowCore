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

internal static class JLVisionMatching
{
    private const string ShapeModelSetPrefix = "shape-model-set-v1:";

    private sealed record ShapeModelSetItem(string Data, int Width, int Height, double OriginX, double OriginY,
        double? SourceOriginX = null, double? SourceOriginY = null);
    private sealed record ShapeModelSetEnvelope(int Version, ShapeModelSetItem[] Models);
    private readonly record struct ShapeFeaturePoint(double X, double Y);
    private sealed record ShapeContrastReference(ShapeFeaturePoint[] Points, double TrainingMeanContrast,
        double TrainingOriginX, double TrainingOriginY);
    private readonly record struct TrainingPoseAlignment(double X, double Y, double Angle, double ScaleX, double ScaleY, double Score);
    private sealed record ConsensusContour(ShapeFeaturePoint[] Points, double Support);
    private sealed class ShapeFeatureSpatialIndex
    {
        private readonly double _cellSize;
        private readonly Dictionary<(int X, int Y), List<ShapeFeaturePoint>> _cells = [];

        public ShapeFeatureSpatialIndex(IEnumerable<ShapeFeaturePoint> points, double cellSize)
        {
            _cellSize = cellSize;
            foreach (var point in points)
            {
                var key = Cell(point.X, point.Y);
                if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = [];
                cell.Add(point);
            }
        }

        public bool TryNearest(ShapeFeaturePoint query, double maxDistance, out ShapeFeaturePoint nearest)
        {
            nearest = default;
            var key = Cell(query.X, query.Y);
            var maxDistanceSquared = maxDistance * maxDistance;
            var bestDistanceSquared = maxDistanceSquared;
            var found = false;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (_cells.TryGetValue((key.X + dx, key.Y + dy), out var cell))
                        foreach (var point in cell)
                        {
                            var deltaX = point.X - query.X;
                            var deltaY = point.Y - query.Y;
                            var distanceSquared = deltaX * deltaX + deltaY * deltaY;
                            if (distanceSquared > bestDistanceSquared) continue;
                            bestDistanceSquared = distanceSquared;
                            nearest = point;
                            found = true;
                        }
            return found;
        }

        private (int X, int Y) Cell(double x, double y) => ((int)Math.Floor(x / _cellSize), (int)Math.Floor(y / _cellSize));
    }
    private readonly record struct ShapeMatchCandidate(double X, double Y, double Angle, double ScaleX, double ScaleY, double Score,
        int Width, int Height, double OriginX, double OriginY, int Index, double? ClutterScore = null,
        ShapeContrastReference? ContrastReference = null)
    {
        public double Scale => Math.Sqrt(ScaleX * ScaleY);
    }

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
                                searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled,
                                out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                        else model.FindAnisoShapeModel(matchingScene, searchStart, searchEnd - searchStart,
                                searchScaleYMin, searchScaleYMax, searchScaleXMin, searchScaleXMax,
                                minScore, searchMatchLimit, .5, "least_squares", shapePyramidLevels, .8,
                                out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                    }
                    else if (scaleTolerance > 0)
                    {
                        if (maxDeformationPx > 0)
                            FindScaledShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart,
                                searchScaleMin, searchScaleMax, minScore, maxClutterScore, searchMatchLimit,
                                shapePyramidLevels, maxDeformationPx, clutterEnabled, out rows, out cols,
                                out angles, out scalesX, out scores);
                        else model.FindScaledShapeModel(matchingScene, searchStart, searchEnd - searchStart, searchScaleMin, searchScaleMax, minScore, searchMatchLimit, .5, "least_squares", shapePyramidLevels, .8, out rows, out cols, out angles, out scalesX, out scores);
                        scalesY = new JlTuple(scalesX);
                    }
                    else
                    {
                        if (maxDeformationPx > 0)
                            FindShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart, minScore,
                                maxClutterScore, searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled,
                                out rows, out cols, out angles, out scores);
                        else matchingScene.FindShapeModel(model, searchStart, searchEnd - searchStart, minScore, searchMatchLimit, .5, "least_squares", shapePyramidLevels, .8, out rows, out cols, out angles, out scores);
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
                    summary["modelSource"] = "workflow-asset";
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
                                maxClutterScore, searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled,
                                out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                        else model.FindAnisoShapeModel(matchingScene, searchStart, searchEnd - searchStart,
                            searchScaleYMin, searchScaleYMax, searchScaleXMin, searchScaleXMax,
                            minScore, searchMatchLimit, .5, "least_squares", shapePyramidLevels, .8,
                            out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                    }
                    else if (scaleTolerance > 0)
                    {
                        if (clutterEnabled || maxDeformationPx > 0)
                            FindScaledShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart,
                                searchScaleMin, searchScaleMax, minScore, maxClutterScore, searchMatchLimit,
                                shapePyramidLevels, maxDeformationPx, clutterEnabled,
                                out rows, out cols, out angles, out scalesX, out scores);
                        else model.FindScaledShapeModel(matchingScene, searchStart, searchEnd - searchStart, searchScaleMin, searchScaleMax, minScore, searchMatchLimit, .5, "least_squares", shapePyramidLevels, .8, out rows, out cols, out angles, out scalesX, out scores);
                        scalesY = new JlTuple(Enumerable.Range(0, scalesX.Length).Select(index => scalesX[index].D).ToArray());
                    }
                    else
                    {
                        if (clutterEnabled || maxDeformationPx > 0)
                            FindShapeWithOptions(matchingScene, model, searchStart, searchEnd - searchStart, minScore,
                                maxClutterScore, searchMatchLimit, shapePyramidLevels, maxDeformationPx, clutterEnabled,
                                out rows, out cols, out angles, out scores);
                        else matchingScene.FindShapeModel(model, searchStart, searchEnd - searchStart, minScore, searchMatchLimit, .5, "least_squares", shapePyramidLevels, .8, out rows, out cols, out angles, out scores);
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

    private static double Overlap(VisionRectangle2D a, VisionRectangle2D b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / Math.Max(1e-9, a.Width * a.Height + b.Width * b.Height - intersection);
    }

    private static Mat DecodeGrayscaleImage(string encodedImage, string label)
    {
        if (string.IsNullOrWhiteSpace(encodedImage))
            throw new InvalidOperationException("Contrast fraction needs the lossless original training image. Reopen the shape node, fetch its training image, and retrain it before using this threshold.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encodedImage); }
        catch (FormatException ex) { throw new InvalidOperationException($"The {label} is invalid.", ex); }
        using var decoded = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (decoded.Empty()) throw new InvalidOperationException($"The {label} could not be decoded.");
        var gray = new Mat();
        try
        {
            if (decoded.Channels() == 1) decoded.CopyTo(gray);
            else if (decoded.Channels() == 3) Cv2.CvtColor(decoded, gray, ColorConversionCodes.BGR2GRAY);
            else if (decoded.Channels() == 4) Cv2.CvtColor(decoded, gray, ColorConversionCodes.BGRA2GRAY);
            else throw new InvalidOperationException($"The {label} must have 1, 3, or 4 channels.");
            return gray;
        }
        catch { gray.Dispose(); throw; }
    }

    private static ShapeContrastReference CreateContrastReference(JlShapeModel model, Mat? trainingImage,
        double trainingOriginX, double trainingOriginY)
    {
        using var contours = model.GetShapeModelContours(1);
        var points = ExtractShapeContours(contours).SelectMany(contour => contour).ToArray();
        if (points.Length == 0) throw new InvalidOperationException("The shape model contains no contour points for contrast measurement.");
        var stride = Math.Max(1, (int)Math.Ceiling(points.Length / 1024d));
        var samples = points.Where((_, index) => index % stride == 0).ToArray();
        var trainingMean = trainingImage is null ? 0 : MeanPointContrast(trainingImage, samples,
            point => (point.X + trainingOriginX, point.Y + trainingOriginY));
        if (trainingImage is not null && trainingMean <= 1e-6)
            throw new InvalidOperationException("The stored training image has no measurable contrast along the shape-model features.");
        return new(samples, trainingMean, trainingOriginX, trainingOriginY);
    }

    private static double MeasureCandidateContrast(Mat image, ShapeContrastReference reference,
        double poseX, double poseY, double angle, double scaleX, double scaleY)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return MeanPointContrast(image, reference.Points, point =>
            (poseX + cos * scaleX * point.X + sin * scaleY * point.Y,
             poseY - sin * scaleX * point.X + cos * scaleY * point.Y));
    }

    private static double MeanPointContrast(Mat image, IReadOnlyList<ShapeFeaturePoint> points,
        Func<ShapeFeaturePoint, (double X, double Y)> project)
    {
        if (points.Count == 0) return 0;
        double total = 0;
        foreach (var point in points)
        {
            var position = project(point);
            total += LocalGradientContrast(image, position.X, position.Y);
        }
        return total / points.Count;
    }

    private static double LocalGradientContrast(Mat image, double x, double y)
    {
        var centerX = (int)Math.Round(x);
        var centerY = (int)Math.Round(y);
        var best = 0d;
        for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                var px = centerX + offsetX;
                var py = centerY + offsetY;
                if (px <= 0 || py <= 0 || px >= image.Width - 1 || py >= image.Height - 1) continue;
                var gx = image.At<byte>(py, px + 1) - image.At<byte>(py, px - 1);
                var gy = image.At<byte>(py + 1, px) - image.At<byte>(py - 1, px);
                best = Math.Max(best, Math.Min(255, Math.Sqrt(gx * gx + gy * gy)));
            }
        return best;
    }

    private static double CandidateScore(ShapeMatchCandidate candidate, bool scoreUsingClutter) =>
        scoreUsingClutter && candidate.ClutterScore is { } clutter
            ? candidate.Score * (1 - Math.Clamp(clutter, 0, 1))
            : candidate.Score;

    private static JlTuple CreateShapeSearchSubPixel(int maxDeformationPx, bool clutterEnabled) =>
        maxDeformationPx > 0
            ? new JlTuple(new[] { "least_squares", $"max_deformation {maxDeformationPx}" })
            : new JlTuple(clutterEnabled ? "interpolation" : "least_squares");

    private static JlTuple CreateShapeSearchMinScore(double minScore, double maxClutterScore, bool clutterEnabled) =>
        new(clutterEnabled ? new[] { minScore, maxClutterScore } : new[] { minScore });

    private static void FindShapeWithOptions(JlImage image, JlShapeModel model, double angleStart, double angleExtent,
        double minScore, double maxClutterScore, int numMatches, int numLevels, int maxDeformationPx, bool clutterEnabled,
        out JlTuple rows, out JlTuple columns, out JlTuple angles, out JlTuple scores)
    {
        using var minimumScores = CreateShapeSearchMinScore(minScore, maxClutterScore, clutterEnabled);
        using var subPixel = CreateShapeSearchSubPixel(maxDeformationPx, clutterEnabled);
        using var levels = new JlTuple(numLevels);
        using var modelId = new JlTuple(model);
        using var start = new JlTuple(angleStart);
        using var extent = new JlTuple(angleExtent);
        using var matches = new JlTuple(numMatches);
        using var overlap = new JlTuple(.5);
        using var greediness = new JlTuple(.8);
        JlOperatorSet.FindShapeModel(image, modelId, start, extent, minimumScores, matches, overlap, subPixel,
            levels, greediness, out rows, out columns, out angles, out scores);
    }

    private static void FindScaledShapeWithOptions(JlImage image, JlShapeModel model, double angleStart, double angleExtent,
        double scaleMin, double scaleMax, double minScore, double maxClutterScore, int numMatches, int numLevels,
        int maxDeformationPx, bool clutterEnabled, out JlTuple rows, out JlTuple columns, out JlTuple angles,
        out JlTuple scales, out JlTuple scores)
    {
        using var minimumScores = CreateShapeSearchMinScore(minScore, maxClutterScore, clutterEnabled);
        using var subPixel = CreateShapeSearchSubPixel(maxDeformationPx, clutterEnabled);
        using var levels = new JlTuple(numLevels);
        using var modelId = new JlTuple(model);
        using var start = new JlTuple(angleStart);
        using var extent = new JlTuple(angleExtent);
        using var minimumScale = new JlTuple(scaleMin);
        using var maximumScale = new JlTuple(scaleMax);
        using var matches = new JlTuple(numMatches);
        using var overlap = new JlTuple(.5);
        using var greediness = new JlTuple(.8);
        JlOperatorSet.FindScaledShapeModel(image, modelId, start, extent, minimumScale, maximumScale, minimumScores,
            matches, overlap, subPixel, levels, greediness, out rows, out columns, out angles, out scales, out scores);
    }

    private static void FindAnisoShapeWithOptions(JlImage image, JlShapeModel model, double angleStart, double angleExtent,
        double scaleYMin, double scaleYMax, double scaleXMin, double scaleXMax, double minScore, double maxClutterScore,
        int numMatches, int numLevels, int maxDeformationPx, bool clutterEnabled, out JlTuple rows,
        out JlTuple columns, out JlTuple angles, out JlTuple scaleY, out JlTuple scaleX, out JlTuple scores)
    {
        using var minimumScores = CreateShapeSearchMinScore(minScore, maxClutterScore, clutterEnabled);
        using var subPixel = CreateShapeSearchSubPixel(maxDeformationPx, clutterEnabled);
        using var levels = new JlTuple(numLevels);
        using var modelId = new JlTuple(model);
        using var start = new JlTuple(angleStart);
        using var extent = new JlTuple(angleExtent);
        using var yMin = new JlTuple(scaleYMin);
        using var yMax = new JlTuple(scaleYMax);
        using var xMin = new JlTuple(scaleXMin);
        using var xMax = new JlTuple(scaleXMax);
        using var matches = new JlTuple(numMatches);
        using var overlap = new JlTuple(.5);
        using var greediness = new JlTuple(.8);
        JlOperatorSet.FindAnisoShapeModel(image, modelId, start, extent, yMin, yMax, xMin, xMax, minimumScores,
            matches, overlap, subPixel, levels, greediness, out rows, out columns, out angles, out scaleY, out scaleX, out scores);
    }

    private static ShapeModelSetEnvelope? DeserializeShapeModelSet(string savedModel)
    {
        if (!savedModel.StartsWith(ShapeModelSetPrefix, StringComparison.Ordinal)) return null;
        ShapeModelSetEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ShapeModelSetEnvelope>(savedModel[ShapeModelSetPrefix.Length..], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex) { throw new InvalidOperationException("The saved shape model set is invalid.", ex); }
        if (envelope is null || envelope.Version != 1 || envelope.Models is not { Length: >= 2 and <= 4 })
            throw new InvalidOperationException("A shape model set must contain 2–4 compatible trained samples.");
        if (envelope.Models.Any(item => item.Width != envelope.Models[0].Width || item.Height != envelope.Models[0].Height))
            throw new InvalidOperationException("Every sample in a shape model set must use the same trained region size.");
        long totalBytes = 0;
        foreach (var item in envelope.Models)
        {
            if (string.IsNullOrWhiteSpace(item.Data) || item.Width < 8 || item.Height < 8 || item.Width > 100_000 || item.Height > 100_000
                || !double.IsFinite(item.OriginX) || !double.IsFinite(item.OriginY)
                || item.OriginX < 0 || item.OriginY < 0 || item.OriginX >= item.Width || item.OriginY >= item.Height)
                throw new InvalidOperationException("A shape model set contains invalid training geometry.");
            try { totalBytes += Convert.FromBase64String(item.Data).Length; }
            catch (FormatException ex) { throw new InvalidOperationException("A shape model set contains invalid model data.", ex); }
            if (totalBytes > 16 * 1024 * 1024) throw new InvalidOperationException("The saved shape model set exceeds the 16 MB combined limit.");
        }
        return envelope;
    }

    private static Rect RoiBounds(VisionRoi roi)
    {
        double left, top, right, bottom;
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                left = roi.X ?? 0; top = roi.Y ?? 0;
                var width = roi.Width ?? 0; var height = roi.Height ?? 0;
                if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                    throw new InvalidOperationException("Rectangle ROI needs finite coordinates and positive width/height.");
                right = left + width; bottom = top + height;
                break;
            case VisionRoiType.Circle:
                var centerX = roi.X ?? 0; var centerY = roi.Y ?? 0; var radius = roi.Radius ?? 0;
                if (!double.IsFinite(centerX) || !double.IsFinite(centerY) || !double.IsFinite(radius) || radius <= 0)
                    throw new InvalidOperationException("Circle ROI needs finite coordinates and a positive radius.");
                left = centerX - radius; top = centerY - radius; right = centerX + radius; bottom = centerY + radius;
                break;
            case VisionRoiType.Polygon:
                var points = roi.Points ?? [];
                if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    throw new InvalidOperationException("Polygon ROI needs at least three finite points.");
                left = points.Min(point => point.X); top = points.Min(point => point.Y);
                right = points.Max(point => point.X); bottom = points.Max(point => point.Y);
                if (right <= left || bottom <= top) throw new InvalidOperationException("Polygon ROI must enclose a non-empty area.");
                break;
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                if (strokes.Count is < 1 or > 10_000) throw new InvalidOperationException("Brush ROI needs 1..10000 strokes.");
                var additions = strokes.Where(stroke => stroke.Mode == VisionRoiStrokeMode.Add).ToArray();
                if (additions.Length == 0) throw new InvalidOperationException("Brush ROI needs at least one add stroke.");
                var brushPoints = new List<(VisionRoiPoint Point, double Radius)>();
                var pointCount = 0;
                foreach (var stroke in strokes)
                {
                    if (stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                        || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024
                        || stroke.Points is null || stroke.Points.Count == 0)
                        throw new InvalidOperationException("Brush ROI strokes need a valid mode, positive width and points.");
                    pointCount = checked(pointCount + stroke.Points.Count);
                    if (pointCount > 500_000) throw new InvalidOperationException("Brush ROI exceeds the 500000-point limit.");
                    if (stroke.Mode != VisionRoiStrokeMode.Add) continue;
                    foreach (var point in stroke.Points)
                    {
                        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new InvalidOperationException("Brush ROI points must be finite.");
                        brushPoints.Add((point, stroke.Width / 2));
                    }
                }
                left = brushPoints.Min(item => item.Point.X - item.Radius);
                top = brushPoints.Min(item => item.Point.Y - item.Radius);
                right = brushPoints.Max(item => item.Point.X + item.Radius);
                bottom = brushPoints.Max(item => item.Point.Y + item.Radius);
                break;
            }
            default:
                throw new InvalidOperationException("Unsupported ROI type.");
        }
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom)
            || left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue
            || right - left > int.MaxValue || bottom - top > int.MaxValue)
            throw new InvalidOperationException("ROI coordinates exceed the supported image-coordinate range.");
        var x = (int)Math.Floor(left); var y = (int)Math.Floor(top);
        var inclusiveBoundary = roi.Type is VisionRoiType.Circle or VisionRoiType.Polygon or VisionRoiType.Brush;
        var endX = (int)Math.Ceiling(right) + (inclusiveBoundary ? 1 : 0);
        var endY = (int)Math.Ceiling(bottom) + (inclusiveBoundary ? 1 : 0);
        return new Rect(x, y, Math.Max(0, endX - x), Math.Max(0, endY - y));
    }

    private static void FillRoiMask(Mat mask, VisionRoi? roi, Rect cropRect, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (roi is null) { mask.SetTo(Scalar.All(255)); return; }
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
            {
                var clipped = RoiBounds(roi) & cropRect;
                if (clipped.Width > 0 && clipped.Height > 0)
                    Cv2.Rectangle(mask, new Rect(clipped.X - cropRect.X, clipped.Y - cropRect.Y, clipped.Width, clipped.Height), Scalar.All(255), -1);
                break;
            }
            case VisionRoiType.Circle:
            {
                var center = new Point((int)Math.Round((roi.X ?? 0) - cropRect.X), (int)Math.Round((roi.Y ?? 0) - cropRect.Y));
                var radius = (int)Math.Round(roi.Radius ?? 0);
                Cv2.Circle(mask, center, radius, Scalar.All(255), -1);
                break;
            }
            case VisionRoiType.Polygon:
            {
                var roiPoints = roi.Points ?? [];
                var points = new Point[roiPoints.Count];
                for (var index = 0; index < points.Length; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var point = roiPoints[index];
                    points[index] = new Point((int)Math.Round(point.X - cropRect.X), (int)Math.Round(point.Y - cropRect.Y));
                }
                cancellationToken.ThrowIfCancellationRequested();
                Cv2.FillPoly(mask, [points], Scalar.All(255));
                break;
            }
            case VisionRoiType.Brush:
                VisionRoiOpenCvExtensions.DrawBrushStrokes(mask, roi.Strokes, cropRect.X, cropRect.Y, cancellationToken);
                break;
        }
    }

    private static void ApplyShapeModelClutter(JlShapeModel model, JlImage trainingModelImage, int imageWidth, int imageHeight,
        VisionRoi clutterRoi, double cropOffsetX, double cropOffsetY, int clutterContrast, int minContrast, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        model.FindShapeModel(trainingModelImage, 0, 0, .3, 1, .5, "least_squares", 0, .8,
            out var rows, out var cols, out var angles, out _);
        using (rows) using (cols) using (angles)
        {
            if (rows.Length == 0) throw new InvalidOperationException("Clutter ROI could not be aligned to the trained model in its source image.");
            var transform = new JlHomMat2D();
            transform.VectorAngleToRigid(0, 0, 0, rows[0].D + cropOffsetY, cols[0].D + cropOffsetX, angles[0].D);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyShapeModelClutter(model, imageWidth, imageHeight, clutterRoi, transform, clutterContrast, minContrast, cancellationToken);
        }
    }

    private static void ApplyShapeModelClutter(JlShapeModel model, int imageWidth, int imageHeight, VisionRoi clutterRoi,
        JlHomMat2D modelToImage, int clutterContrast, int minContrast, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clutterContrast < minContrast)
            throw new InvalidOperationException("Clutter Contrast must be at least the Minimum Feature Contrast used to train the model.");
        using var mask = clutterRoi.BuildMask(new Size(imageWidth, imageHeight), cancellationToken);
        if (Cv2.CountNonZero(mask) == 0)
            throw new InvalidOperationException("Clutter ROI must overlap the training image and contain at least one pixel.");
        using var clutterImage = JLVisionImages.FromMat(mask);
        using var clutterRegion = clutterImage.Threshold(1d, 255d);
        using var names = new JlTuple();
        using var values = new JlTuple();
        cancellationToken.ThrowIfCancellationRequested();
        try { model.SetShapeModelClutter(clutterRegion, modelToImage, clutterContrast, names, values); }
        catch (JlOperatorException ex) when (ex.Message.Contains("8515", StringComparison.Ordinal))
        { throw new InvalidOperationException("Clutter ROI is too close to the trained model contours. Move it farther from the part edges.", ex); }
    }

    private static bool Contains(VisionRoi roi, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                return x >= roi.X!.Value && y >= roi.Y!.Value && x < roi.X.Value + roi.Width!.Value && y < roi.Y.Value + roi.Height!.Value;
            case VisionRoiType.Circle:
            {
                var dx = x - roi.X!.Value; var dy = y - roi.Y!.Value; var radius = roi.Radius!.Value;
                return dx * dx + dy * dy <= radius * radius;
            }
            case VisionRoiType.Polygon:
            {
                var inside = false; var points = roi.Points ?? [];
                for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                {
                    var a = points[i]; var b = points[j];
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                }
                return inside;
            }
            case VisionRoiType.Brush:
            {
                var selected = false;
                foreach (var stroke in roi.Strokes ?? [])
                {
                    if (stroke.Points.Count == 0) continue;
                    var radiusSquared = stroke.Width * stroke.Width / 4;
                    var included = false;
                    for (var index = 0; index < stroke.Points.Count; index++)
                    {
                        var a = stroke.Points[index];
                        if (index == 0 && DistanceSquared(x, y, a.X, a.Y) <= radiusSquared) included = true;
                        if (index == 0) continue;
                        var b = stroke.Points[index - 1];
                        var dx = a.X - b.X; var dy = a.Y - b.Y;
                        var lengthSquared = dx * dx + dy * dy;
                        var t = lengthSquared <= 1e-12 ? 0 : Math.Clamp(((x - b.X) * dx + (y - b.Y) * dy) / lengthSquared, 0, 1);
                        if (DistanceSquared(x, y, b.X + t * dx, b.Y + t * dy) <= radiusSquared) included = true;
                    }
                    if (included) selected = stroke.Mode == VisionRoiStrokeMode.Add;
                }
                return selected;
            }
            default: return false;
        }

        static double DistanceSquared(double x1, double y1, double x2, double y2)
        {
            var dx = x1 - x2; var dy = y1 - y2;
            return dx * dx + dy * dy;
        }
    }

    private static NodeExecutorResult TrainShape(NodeExecutionContext context, NodeDefinition node, VisionImage scene, bool fromPort, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var combineModelData = node.GetString("trainingCombineModelData", string.Empty);
        if (combineModelData.Length > 0) return CombineShapeModels(node, scene, combineModelData, token);
        Mat? fileTemplate = null;
        try
        {
        Mat trainingMat;
        var capturedImage = node.GetString("trainingImageData", string.Empty);
        if (!string.IsNullOrEmpty(capturedImage))
        {
            if (capturedImage.Length > 24 * 1024 * 1024)
                throw new InvalidOperationException("Captured training image exceeds the 16 MB image limit.");
            fileTemplate = Cv2.ImDecode(Convert.FromBase64String(capturedImage), ImreadModes.Unchanged);
            if (fileTemplate.Empty()) throw new InvalidOperationException("Captured training image could not be decoded. Acquire the training image again.");
            trainingMat = fileTemplate;
        }
        else if (fromPort) trainingMat = context.RequireImage("template").Mat;
        else
        {
            var path = node.GetString("templatePath", string.Empty).Trim();
            if (path.Length == 0) throw new InvalidOperationException("Training requires a connected template image or a templatePath.");
            fileTemplate = Cv2.ImRead(Path.GetFullPath(path), ImreadModes.Unchanged);
            if (fileTemplate.Empty()) throw new InvalidOperationException($"Training image '{Path.GetFullPath(path)}' could not be loaded.");
            trainingMat = fileTemplate;
        }
        if (trainingMat.Empty()) throw new InvalidOperationException("Training requires a non-empty template image.");
        if (node.GetBool("trainingPreviewOnly", false))
        {
            Cv2.ImEncode(".png", trainingMat, out var capturedBytes);
            if (capturedBytes.Length > 16 * 1024 * 1024)
                throw new InvalidOperationException("Training image exceeds the 16 MB capture limit; crop the image before acquiring it.");
            var preview = VisionImage.Own(trainingMat.Clone());
            return new NodeExecutorResult(
                new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(preview), ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0), ["count"] = VisionValue.Integer(0) },
                new Dictionary<string, object?> { ["trainingPreviewReady"] = true, ["trainingImageData"] = Convert.ToBase64String(capturedBytes), ["trainingSource"] = fromPort ? "template-input" : "template-path", ["trainingSourceWidth"] = trainingMat.Width, ["trainingSourceHeight"] = trainingMat.Height });
        }
        var roi = node.GetRoi();
        var clutterRoi = node.GetRoi("clutterRoi");
        TrainingPoseAlignment? trainingRegionAlignment = null;
        var referenceModelData = node.GetString("trainingReferenceModelData", string.Empty);
        if (referenceModelData.Length > 0 && (roi is not null || clutterRoi is not null
            || node.GetString("trainingOriginMode", "centroid").Equals("manual", StringComparison.OrdinalIgnoreCase)))
        {
            var referenceOriginX = node.GetDouble("trainingReferenceOriginImageX", double.NaN);
            var referenceOriginY = node.GetDouble("trainingReferenceOriginImageY", double.NaN);
            if (!double.IsFinite(referenceOriginX) || !double.IsFinite(referenceOriginY))
                throw new InvalidOperationException("Automatic sample ROI alignment requires the first sample's training origin.");
            trainingRegionAlignment = FindTrainingPoseAlignment(trainingMat, referenceModelData, node, token);
            if (roi is not null) roi = TransformTrainingRoi(roi, referenceOriginX, referenceOriginY, trainingRegionAlignment.Value);
            if (clutterRoi is not null) clutterRoi = TransformTrainingRoi(clutterRoi, referenceOriginX, referenceOriginY, trainingRegionAlignment.Value);
        }
        var cropRect = new Rect(0, 0, trainingMat.Width, trainingMat.Height);
        if (roi is not null)
        {
            cropRect = RoiBounds(roi) & new Rect(0, 0, trainingMat.Width, trainingMat.Height);
            if (cropRect.Width < 8 || cropRect.Height < 8) throw new InvalidOperationException("Training ROI must be at least 8 × 8 pixels and lie inside the training image.");
        }
        using var rawCrop = new Mat(trainingMat, cropRect).Clone();
        using var crop = new Mat();
        if (rawCrop.Channels() == 1) rawCrop.CopyTo(crop);
        else if (rawCrop.Channels() == 3) Cv2.CvtColor(rawCrop, crop, ColorConversionCodes.BGR2GRAY);
        else if (rawCrop.Channels() == 4) Cv2.CvtColor(rawCrop, crop, ColorConversionCodes.BGRA2GRAY);
        else throw new InvalidOperationException("Training image must have 1, 3, or 4 channels.");
        using var roiMask = new Mat(crop.Size(), MatType.CV_8UC1, Scalar.All(0));
        FillRoiMask(roiMask, roi, cropRect, token);
        var trainingRegionMoments = Cv2.Moments(roiMask, binaryImage: true);
        if (trainingRegionMoments.M00 < 64) throw new InvalidOperationException("Training region must contain at least 64 pixels.");
        token.ThrowIfCancellationRequested();
        using var originBinary = ContourNodesHelper.Binarize(crop, node.GetString("binarize", "auto"), node.GetDouble("threshold", 128), node.GetBool("invert", false), out _, out _);
        Cv2.BitwiseAnd(originBinary, roiMask, originBinary);
        Cv2.FindContours(originBinary, out Point[][] originContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var originContour = originContours.OrderByDescending(candidate => Cv2.ContourArea(candidate)).FirstOrDefault(candidate => Cv2.ContourArea(candidate) > 0);
        var originMoments = originContour is null ? trainingRegionMoments : Cv2.Moments(originContour);
        var originMode = node.GetString("trainingOriginMode", "centroid");
        var manualOriginImageX = node.GetDouble("trainingOriginX", cropRect.X + crop.Width / 2d);
        var manualOriginImageY = node.GetDouble("trainingOriginY", cropRect.Y + crop.Height / 2d);
        if (trainingRegionAlignment is { } alignment)
        {
            (manualOriginImageX, manualOriginImageY) = TransformTrainingPoint(manualOriginImageX, manualOriginImageY,
                node.GetDouble("trainingReferenceOriginImageX", 0), node.GetDouble("trainingReferenceOriginImageY", 0), alignment);
        }
        var originX = originMode.Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? manualOriginImageX - cropRect.X
            : originMoments.M10 / originMoments.M00;
        var originY = originMode.Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? manualOriginImageY - cropRect.Y
            : originMoments.M01 / originMoments.M00;
        var overrideOriginX = node.GetDouble("trainingOriginOverrideX", double.NaN);
        var overrideOriginY = node.GetDouble("trainingOriginOverrideY", double.NaN);
        if (double.IsFinite(overrideOriginX) || double.IsFinite(overrideOriginY))
        {
            if (!double.IsFinite(overrideOriginX) || !double.IsFinite(overrideOriginY))
                throw new InvalidOperationException("A shared training origin requires both finite X and Y coordinates.");
            originX = overrideOriginX;
            originY = overrideOriginY;
        }
        if (originX < 0 || originY < 0 || originX >= crop.Width || originY >= crop.Height)
            throw new InvalidOperationException("Manual training origin must lie inside the selected training ROI.");
        var range = Math.Clamp(node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)), 0, 180) * Math.PI / 180;
        var pyramidLevels = node.GetInt("pyramidLevels", 0);
        if (pyramidLevels is < 0 or > 8) throw new InvalidOperationException("pyramidLevels must be between 0 (automatic) and 8.");
        var step = Math.Clamp(node.GetDouble("angleStepDeg", 1), .1, 30) * Math.PI / 180;
        var scaleTolerance = Math.Clamp(node.GetDouble("scaleTolerancePercent", 0), 0, 50) / 100d;
        var scaleToleranceX = Math.Clamp(node.GetDouble("scaleToleranceXPercent", 0), 0, 50) / 100d;
        var scaleToleranceY = Math.Clamp(node.GetDouble("scaleToleranceYPercent", 0), 0, 50) / 100d;
        var anisotropicScale = scaleToleranceX > 0 || scaleToleranceY > 0;
        var scaleMin = 1 - scaleTolerance;
        var scaleMax = 1 + scaleTolerance;
        var scaleXMin = anisotropicScale ? 1 - scaleToleranceX : scaleMin;
        var scaleXMax = anisotropicScale ? 1 + scaleToleranceX : scaleMax;
        var scaleYMin = anisotropicScale ? 1 - scaleToleranceY : scaleMin;
        var scaleYMax = anisotropicScale ? 1 + scaleToleranceY : scaleMax;
        var scaleStep = Math.Clamp(node.GetDouble("scaleStepPercent", 1), .1, 10) / 100d;
        var ignorePolarity = node.GetBool("ignorePolarity", false);
        var minContrast = Math.Clamp(node.GetInt("minContrast", 5), 1, 255);
        MatchingAutoTuneRecommendation? autoTune = null;
        if (MatchingAutoTune.IsAutoEnabled(node))
        {
            autoTune = MatchingAutoTune.ResolveForShapeTraining(crop, node);
            pyramidLevels = autoTune.PyramidLevels;
            step = autoTune.AngleStepDeg * Math.PI / 180d;
            minContrast = autoTune.MinContrast;
        }
        var clutterContrast = Math.Clamp(node.GetInt("clutterContrast", minContrast), 1, 255);
        if (clutterRoi is not null && clutterContrast < minContrast)
            throw new InvalidOperationException("Clutter Contrast must be at least the Minimum Feature Contrast used to train the model.");
        using var sourceImage = JLVisionImages.FromMat(crop);
        using var maskImage = JLVisionImages.FromMat(roiMask);
        using var trainingRegion = maskImage.Threshold(1d, 255d);
        using var image = sourceImage.ReduceDomain(trainingRegion);
        using var model = new JlShapeModel();
        using (var levels = pyramidLevels == 0 ? new JlTuple("auto") : new JlTuple(pyramidLevels)) using (var delta = new JlTuple(step)) using (var optimize = new JlTuple("auto")) using (var contrast = new JlTuple("auto")) using (var minimum = new JlTuple(minContrast))
        {
            var metric = ignorePolarity ? "ignore_global_polarity" : "use_polarity";
            if (anisotropicScale)
            {
                using var scaleDelta = new JlTuple(scaleStep);
                model.CreateAnisoShapeModel(image, levels, -range, 2 * range, delta,
                    scaleYMin, scaleYMax, scaleDelta, scaleXMin, scaleXMax, scaleDelta,
                    optimize, metric, contrast, minimum);
            }
            else if (scaleTolerance > 0)
            {
                using var scaleDelta = new JlTuple(scaleStep);
                model.CreateScaledShapeModel(image, levels, -range, 2 * range, delta, scaleMin, scaleMax, scaleDelta, optimize, metric, contrast, minimum);
            }
            else model.CreateShapeModel(image, levels, -range, 2 * range, delta, optimize, metric, contrast, minimum);
        }
        model.SetShapeModelOrigin(originY - (crop.Height - 1) / 2d, originX - (crop.Width - 1) / 2d);
        if (clutterRoi is not null)
            ApplyShapeModelClutter(model, image, trainingMat.Width, trainingMat.Height, clutterRoi,
                cropRect.X, cropRect.Y, clutterContrast, minContrast, token);
        var data = model.SerializeShapeModel();
        if (data.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Trained model exceeds the 16 MB embedded workflow asset limit.");
        Cv2.ImEncode(".jpg", trainingMat, out var previewBytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]);
        if (previewBytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Training preview exceeds the 4 MB embedded workflow asset limit; crop the training image before training.");
        var config = ShapeTrainingConfig(node);
        using var modelContours = model.GetShapeModelContours(1);
        var previewContours = new List<VisionOverlayPoint[]>();
        var featurePoints = new List<(double X, double Y)>();
        for (var contourIndex = 1; contourIndex <= modelContours.CountObj(); contourIndex++)
        {
            using var selectedModelContour = modelContours.SelectObj(contourIndex);
            selectedModelContour.GetContourXld(out var modelRows, out var modelCols);
            using (modelRows) using (modelCols)
            {
                for (var pointIndex = 0; pointIndex < modelRows.Length; pointIndex++)
                    featurePoints.Add((modelCols[pointIndex].D, modelRows[pointIndex].D));
                var stride = Math.Max(1, (int)Math.Ceiling(modelRows.Length / 500d));
                var points = Enumerable.Range(0, modelRows.Length).Where(index => index % stride == 0)
                    .Select(index => new VisionOverlayPoint(modelCols[index].D + originX + cropRect.X, modelRows[index].D + originY + cropRect.Y)).ToArray();
                if (points.Length > 0) previewContours.Add(points);
            }
        }
        var (symmetryPeriodDeg, trainingWarnings) = AnalyzeTrainingQuality(featurePoints);
        var warnings = trainingWarnings.ToList();
        if (autoTune is not null)
        {
            warnings.AddRange(autoTune.Warnings.Select(warning => $"auto-tune: {warning.Message}"));
            if (autoTune.Inspect.Quality is "too-few-points" or "thin")
                warnings.Add($"auto-tune: model quality is '{autoTune.Inspect.Quality}' ({autoTune.Inspect.ModelPointCount} points); check the training ROI and minimum contrast.");
        }
        if (trainingRegionAlignment is { Score: < .55 } lowScoreAlignment)
            warnings.Add($"Training region auto-aligned to sample with a low score ({lowScoreAlignment.Score:0.###}); verify that the ROI follows the intended part.");
        var contourPoints = previewContours.FirstOrDefault() ?? [];
        var previewImage = VisionImage.Own(trainingMat.Clone());
        var result = new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(previewImage), ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0), ["count"] = VisionValue.Integer(0) },
            new Dictionary<string, object?>
            {
                ["trainingSucceeded"] = true, ["trainingModelData"] = Convert.ToBase64String(data),
                ["trainingModelHash"] = Convert.ToHexString(SHA256.HashData(data)),
                ["trainingPreviewImage"] = Convert.ToBase64String(previewBytes),
                ["trainingConfig"] = config, ["trainingWidth"] = crop.Width, ["trainingHeight"] = crop.Height,
                ["trainingSourceWidth"] = trainingMat.Width, ["trainingSourceHeight"] = trainingMat.Height,
                ["trainingOriginX"] = originX, ["trainingOriginY"] = originY,
                ["trainingOriginImageX"] = originX + cropRect.X, ["trainingOriginImageY"] = originY + cropRect.Y,
                ["trainingRegionArea"] = trainingRegionMoments.M00, ["trainingSource"] = fromPort ? "template-input" : "template-path",
                ["trainingContourCount"] = modelContours.CountObj(), ["trainingFeaturePointCount"] = featurePoints.Count,
                ["trainingSymmetryPeriodDeg"] = symmetryPeriodDeg, ["trainingWarnings"] = warnings.ToArray(),
                ["trainingRegionAlignmentScore"] = trainingRegionAlignment?.Score,
                ["trainingRegionAlignmentAngleDeg"] = trainingRegionAlignment is { } alignedPose ? alignedPose.Angle * 180 / Math.PI : null,
                ["trainingRegionAlignmentScaleX"] = trainingRegionAlignment?.ScaleX,
                ["trainingRegionAlignmentScaleY"] = trainingRegionAlignment?.ScaleY,
                ["trainingPyramidLevels"] = pyramidLevels,
                ["trainingOriginMode"] = originMode, ["trainingContour"] = contourPoints, ["trainingContours"] = previewContours.ToArray(),
                ["trainingRegionType"] = roi?.Type.ToString() ?? "FullImage",
                ["trainingScaleMin"] = Math.Min(scaleXMin, scaleYMin), ["trainingScaleMax"] = Math.Max(scaleXMax, scaleYMax),
                ["trainingScaleXMin"] = scaleXMin, ["trainingScaleXMax"] = scaleXMax,
                ["trainingScaleYMin"] = scaleYMin, ["trainingScaleYMax"] = scaleYMax,
                ["trainingScaleToleranceXPercent"] = scaleToleranceX * 100, ["trainingScaleToleranceYPercent"] = scaleToleranceY * 100,
                ["trainingIgnorePolarity"] = ignorePolarity, ["trainingMinContrast"] = minContrast,
                ["trainingClutterEnabled"] = clutterRoi is not null, ["trainingClutterContrast"] = clutterRoi is not null ? clutterContrast : null,
                ["trainingAutoTune"] = autoTune is null ? null : new Dictionary<string, object?>
                {
                    ["version"] = MatchingAutoTune.Version,
                    ["profile"] = node.GetString("autoTuneProfile", "balanced"),
                    ["pyramidLevels"] = autoTune.PyramidLevels,
                    ["angleStepDeg"] = autoTune.AngleStepDeg,
                    ["contrast"] = autoTune.Contrast,
                    ["minContrast"] = autoTune.MinContrast,
                    ["optimization"] = autoTune.Optimization,
                    ["quality"] = autoTune.Inspect.Quality,
                    ["modelPointCount"] = autoTune.Inspect.ModelPointCount,
                    ["modelCoverage"] = autoTune.Inspect.CoverageRatio,
                    ["warnings"] = autoTune.Warnings.Select(warning => warning.Code).ToArray()
                },
                ["trainingModelQuality"] = autoTune?.Inspect.Quality,
                ["trainingModelPointCount"] = autoTune?.Inspect.ModelPointCount
            },
            [
                .. previewContours.Select((points, index) => new VisionOverlay($"shape-training-contour-{index}", VisionOverlayType.Polyline, Points: points, Label: "模型轮廓片段", Stroke: "#13c2c2", StrokeWidth: 2)),
                new VisionOverlay("shape-training-origin", VisionOverlayType.Point, X: originX + cropRect.X, Y: originY + cropRect.Y, Label: "原点", Stroke: "#fa541c", StrokeWidth: 3)
            ]);
        return result;
        }
        finally { fileTemplate?.Dispose(); }
    }

    private static TrainingPoseAlignment FindTrainingPoseAlignment(Mat trainingImage, string serializedModel,
        NodeDefinition node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (serializedModel.Length > 24 * 1024 * 1024)
            throw new InvalidOperationException("First sample model is too large for automatic training-region alignment.");
        byte[] modelBytes;
        try { modelBytes = Convert.FromBase64String(serializedModel); }
        catch (FormatException ex) { throw new InvalidOperationException("First sample model is invalid; retrain the first sample.", ex); }
        using var model = new JlShapeModel();
        model.DeserializeShapeModel(modelBytes);
        using (var parameterName = new JlTuple("use_clutter"))
        using (var parameterValue = new JlTuple("false"))
            model.SetShapeModelParam(parameterName, parameterValue);
        using var gray = new Mat();
        if (trainingImage.Channels() == 1) trainingImage.CopyTo(gray);
        else if (trainingImage.Channels() == 3) Cv2.CvtColor(trainingImage, gray, ColorConversionCodes.BGR2GRAY);
        else if (trainingImage.Channels() == 4) Cv2.CvtColor(trainingImage, gray, ColorConversionCodes.BGRA2GRAY);
        else throw new InvalidOperationException("Training image must have 1, 3, or 4 channels.");
        using var search = JLVisionImages.FromMat(gray);
        var trainingRange = Math.Clamp(node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)), 0, 180) * Math.PI / 180;
        var pyramidLevels = node.GetInt("pyramidLevels", 0);
        var scaleTolerance = Math.Clamp(node.GetDouble("scaleTolerancePercent", 0), 0, 50) / 100d;
        var scaleToleranceX = Math.Clamp(node.GetDouble("scaleToleranceXPercent", 0), 0, 50) / 100d;
        var scaleToleranceY = Math.Clamp(node.GetDouble("scaleToleranceYPercent", 0), 0, 50) / 100d;
        var anisotropicScale = scaleToleranceX > 0 || scaleToleranceY > 0;
        JlTuple rows, cols, angles, scalesX, scalesY, scores;
        if (anisotropicScale)
        {
            model.FindAnisoShapeModel(search, -trainingRange, 2 * trainingRange,
                1 - scaleToleranceY, 1 + scaleToleranceY, 1 - scaleToleranceX, 1 + scaleToleranceX,
                .3, 1, .5, "least_squares", pyramidLevels, .8,
                out rows, out cols, out angles, out scalesY, out scalesX, out scores);
        }
        else if (scaleTolerance > 0)
        {
            model.FindScaledShapeModel(search, -trainingRange, 2 * trainingRange, 1 - scaleTolerance, 1 + scaleTolerance,
                .3, 1, .5, "least_squares", pyramidLevels, .8, out rows, out cols, out angles, out scalesX, out scores);
            scalesY = new JlTuple(Enumerable.Range(0, scalesX.Length).Select(index => scalesX[index].D).ToArray());
        }
        else
        {
            search.FindShapeModel(model, -trainingRange, 2 * trainingRange, .3, 1, .5,
                "least_squares", pyramidLevels, .8, out rows, out cols, out angles, out scores);
            scalesX = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
            scalesY = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
        }
        using (rows) using (cols) using (angles) using (scalesX) using (scalesY) using (scores)
        {
            if (scores.Length == 0)
                throw new InvalidOperationException("Could not find the first sample in this training image, so its model region could not be aligned.");
            var bestIndex = Enumerable.Range(0, scores.Length).MaxBy(index => scores[index].D);
            var bestScore = scores[bestIndex].D;
            if (bestScore < .3)
                throw new InvalidOperationException($"The first sample aligned to this image with a low score ({bestScore:0.###}); check that the same part is visible before training.");
            return new(cols[bestIndex].D, rows[bestIndex].D, angles[bestIndex].D,
                scalesX[bestIndex].D, scalesY[bestIndex].D, bestScore);
        }
    }

    private static VisionRoi TransformTrainingRoi(VisionRoi roi, double sourceOriginX, double sourceOriginY,
        TrainingPoseAlignment pose)
    {
        VisionRoiPoint Transform(VisionRoiPoint point)
        {
            var (x, y) = TransformTrainingPoint(point.X, point.Y, sourceOriginX, sourceOriginY, pose);
            return new(x, y);
        }

        return roi.Type switch
        {
            VisionRoiType.Rectangle => new VisionRoi(VisionRoiType.Polygon, Points:
            [
                Transform(new(roi.X ?? 0, roi.Y ?? 0)),
                Transform(new((roi.X ?? 0) + (roi.Width ?? 0), roi.Y ?? 0)),
                Transform(new((roi.X ?? 0) + (roi.Width ?? 0), (roi.Y ?? 0) + (roi.Height ?? 0))),
                Transform(new(roi.X ?? 0, (roi.Y ?? 0) + (roi.Height ?? 0)))
            ]),
            VisionRoiType.Circle => TransformCircle(),
            VisionRoiType.Polygon => new VisionRoi(VisionRoiType.Polygon, Points: (roi.Points ?? []).Select(Transform).ToArray()),
            VisionRoiType.Brush => new VisionRoi(VisionRoiType.Brush, Strokes: (roi.Strokes ?? []).Select(stroke =>
                new VisionRoiBrushStroke(stroke.Mode, stroke.Width * Math.Sqrt(pose.ScaleX * pose.ScaleY), stroke.Points.Select(Transform).ToArray())).ToArray()),
            _ => throw new InvalidOperationException("Unsupported training region type for automatic alignment.")
        };

        VisionRoi TransformCircle()
        {
            var centerX = roi.X ?? 0;
            var centerY = roi.Y ?? 0;
            var radius = roi.Radius ?? 0;
            if (Math.Abs(pose.ScaleX - pose.ScaleY) < 1e-9)
            {
                var center = Transform(new(centerX, centerY));
                return new VisionRoi(VisionRoiType.Circle, center.X, center.Y, Radius: radius * pose.ScaleX);
            }
            var ellipse = Enumerable.Range(0, 64).Select(index =>
            {
                var angle = index * Math.PI / 32;
                return Transform(new(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle)));
            }).ToArray();
            return new VisionRoi(VisionRoiType.Polygon, Points: ellipse);
        }
    }

    private static (double X, double Y) TransformTrainingPoint(double x, double y, double sourceOriginX,
        double sourceOriginY, TrainingPoseAlignment pose)
    {
        var dx = x - sourceOriginX;
        var dy = y - sourceOriginY;
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        return (pose.X + cos * pose.ScaleX * dx + sin * pose.ScaleY * dy,
            pose.Y - sin * pose.ScaleX * dx + cos * pose.ScaleY * dy);
    }

    private static NodeExecutorResult CombineShapeModels(NodeDefinition node, VisionImage scene, string serializedModelSet, CancellationToken token)
    {
        var modelSet = DeserializeShapeModelSet(serializedModelSet)
            ?? throw new InvalidOperationException("Composite training requires a multi-sample shape model set.");
        var sourceData = node.GetString("trainingCombineSourceImages", string.Empty);
        if (sourceData.Length == 0 || sourceData.Length > 48 * 1024 * 1024)
            throw new InvalidOperationException("Composite training source images are missing or exceed the combined size limit.");
        string[] sourceImages;
        try { sourceImages = JsonSerializer.Deserialize<string[]>(sourceData) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException("Composite training source images are invalid.", ex); }
        if (sourceImages.Length != modelSet.Models.Length || sourceImages.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Composite training needs one original image for every trained sample.");
        if (sourceImages.Sum(data => (long)data.Length) > 32L * 1024 * 1024)
            throw new InvalidOperationException("Composite training source images exceed the 32 MB combined limit.");
        if (modelSet.Models.Any(item => item.SourceOriginX is null || item.SourceOriginY is null
            || !double.IsFinite(item.SourceOriginX.Value) || !double.IsFinite(item.SourceOriginY.Value)))
            throw new InvalidOperationException("Composite training samples need a valid image-space origin. Retrain the samples and try again.");

        var imageWidth = modelSet.Models[0].Width;
        var imageHeight = modelSet.Models[0].Height;
        var fraction = Math.Clamp(node.GetDouble("trainingMinSampleFraction", .75), .5, 1d);
        var featureTolerance = Math.Clamp(node.GetDouble("trainingFeatureTolerancePx", 2), .5, 10d);
        var minimumVotes = Math.Max(1, (int)Math.Ceiling(fraction * modelSet.Models.Length - 1e-9));
        var trainingRange = Math.Clamp(node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)), 0, 180) * Math.PI / 180;
        var pyramidLevels = node.GetInt("pyramidLevels", 0);
        if (pyramidLevels is < 0 or > 8) throw new InvalidOperationException("pyramidLevels must be between 0 (automatic) and 8.");
        var scaleTolerance = Math.Clamp(node.GetDouble("scaleTolerancePercent", 0), 0, 50) / 100d;
        var scaleToleranceX = Math.Clamp(node.GetDouble("scaleToleranceXPercent", 0), 0, 50) / 100d;
        var scaleToleranceY = Math.Clamp(node.GetDouble("scaleToleranceYPercent", 0), 0, 50) / 100d;
        var anisotropicScale = scaleToleranceX > 0 || scaleToleranceY > 0;
        var scaleMin = 1 - scaleTolerance;
        var scaleMax = 1 + scaleTolerance;
        var scaleXMin = anisotropicScale ? 1 - scaleToleranceX : scaleMin;
        var scaleXMax = anisotropicScale ? 1 + scaleToleranceX : scaleMax;
        var scaleYMin = anisotropicScale ? 1 - scaleToleranceY : scaleMin;
        var scaleYMax = anisotropicScale ? 1 + scaleToleranceY : scaleMax;
        var step = Math.Clamp(node.GetDouble("angleStepDeg", 1), .1, 30) * Math.PI / 180;
        var scaleStep = Math.Clamp(node.GetDouble("scaleStepPercent", 1), .1, 10) / 100d;
        var ignorePolarity = node.GetBool("ignorePolarity", false);
        var minContrast = Math.Clamp(node.GetInt("minContrast", 5), 1, 255);
        var clutterRoi = node.GetRoi("clutterRoi");
        var clutterContrast = Math.Clamp(node.GetInt("clutterContrast", minContrast), 1, 255);
        if (clutterRoi is not null && clutterContrast < minContrast)
            throw new InvalidOperationException("Clutter Contrast must be at least the Minimum Feature Contrast used to train the model.");
        var sampleContours = new List<ShapeFeaturePoint[]>[modelSet.Models.Length];
        var sampleFeaturePoints = new List<ShapeFeaturePoint>[modelSet.Models.Length];
        var alignmentScores = new double[modelSet.Models.Length];
        var anchorBytes = Convert.FromBase64String(modelSet.Models[0].Data);
        using var anchorModel = new JlShapeModel();
        anchorModel.DeserializeShapeModel(anchorBytes);
        using (var parameterName = new JlTuple("use_clutter"))
        using (var parameterValue = new JlTuple("false"))
            anchorModel.SetShapeModelParam(parameterName, parameterValue);

        for (var sampleIndex = 0; sampleIndex < modelSet.Models.Length; sampleIndex++)
            {
                token.ThrowIfCancellationRequested();
                var item = modelSet.Models[sampleIndex];
                var modelBytes = Convert.FromBase64String(item.Data);
                using var model = new JlShapeModel();
                model.DeserializeShapeModel(modelBytes);
                using var contours = model.GetShapeModelContours(1);
                sampleContours[sampleIndex] = ExtractShapeContours(contours);
                if (sampleContours[sampleIndex].Count == 0)
                    throw new InvalidOperationException($"Sample {sampleIndex + 1} has no usable geometric features.");

                var matchX = item.SourceOriginX!.Value;
                var matchY = item.SourceOriginY!.Value;
                var angle = 0d;
                var scaleX = 1d;
                var scaleY = 1d;
                var score = 1d;
                if (sampleIndex > 0)
                {
                    byte[] encoded;
                    try { encoded = Convert.FromBase64String(sourceImages[sampleIndex]); }
                    catch (FormatException ex) { throw new InvalidOperationException($"Sample {sampleIndex + 1} original image is invalid.", ex); }
                    using var sampleImage = Cv2.ImDecode(encoded, ImreadModes.Unchanged);
                    if (sampleImage.Empty()) throw new InvalidOperationException($"Sample {sampleIndex + 1} original image could not be decoded.");
                    using var gray = new Mat();
                    if (sampleImage.Channels() == 1) sampleImage.CopyTo(gray);
                    else if (sampleImage.Channels() == 3) Cv2.CvtColor(sampleImage, gray, ColorConversionCodes.BGR2GRAY);
                    else if (sampleImage.Channels() == 4) Cv2.CvtColor(sampleImage, gray, ColorConversionCodes.BGRA2GRAY);
                    else throw new InvalidOperationException("Composite training images must have 1, 3, or 4 channels.");
                    using var search = JLVisionImages.FromMat(gray);
                    JlTuple rows, cols, angles, scalesX, scalesY, scores;
                    if (anisotropicScale)
                    {
                        anchorModel.FindAnisoShapeModel(search, -trainingRange, 2 * trainingRange,
                            scaleYMin, scaleYMax, scaleXMin, scaleXMax,
                            .3, 1, .5, "least_squares", pyramidLevels, .8,
                            out rows, out cols, out angles, out scalesY, out scalesX, out scores);
                    }
                    else if (scaleTolerance > 0)
                    {
                        anchorModel.FindScaledShapeModel(search, -trainingRange, 2 * trainingRange, scaleMin, scaleMax,
                            .3, 1, .5, "least_squares", pyramidLevels, .8, out rows, out cols, out angles, out scalesX, out scores);
                        scalesY = new JlTuple(Enumerable.Range(0, scalesX.Length).Select(index => scalesX[index].D).ToArray());
                    }
                    else
                    {
                        search.FindShapeModel(anchorModel, -trainingRange, 2 * trainingRange, .3, 1, .5,
                            "least_squares", pyramidLevels, .8, out rows, out cols, out angles, out scores);
                        scalesX = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                        scalesY = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                    }
                    using (rows) using (cols) using (angles) using (scalesX) using (scalesY) using (scores)
                    {
                        if (scores.Length == 0 || scores[0].D < .3)
                            throw new InvalidOperationException($"Could not align sample {sampleIndex + 1} to the first sample. Keep the same part in the ROI and expand the trained angle/scale coverage if needed.");
                        matchX = cols[0].D;
                        matchY = rows[0].D;
                        angle = angles[0].D;
                        scaleX = scalesX[0].D;
                        scaleY = scalesY[0].D;
                        score = scores[0].D;
                    }
                }
                alignmentScores[sampleIndex] = score;
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                var sourceOriginX = item.SourceOriginX.Value;
                var sourceOriginY = item.SourceOriginY!.Value;
                sampleFeaturePoints[sampleIndex] = [];
                foreach (var contour in sampleContours[sampleIndex])
                {
                    for (var pointIndex = 0; pointIndex < contour.Length; pointIndex++)
                    {
                        var point = contour[pointIndex];
                        var dx = point.X + sourceOriginX - matchX;
                        var dy = point.Y + sourceOriginY - matchY;
                        contour[pointIndex] = new((cos * dx - sin * dy) / scaleX, (sin * dx + cos * dy) / scaleY);
                        sampleFeaturePoints[sampleIndex].Add(contour[pointIndex]);
                    }
                }
            }

            var indexes = sampleFeaturePoints.Select(points => new ShapeFeatureSpatialIndex(points, featureTolerance)).ToArray();
            var consensus = BuildConsensusContours(sampleContours, indexes, minimumVotes, featureTolerance);
            if (consensus.Count == 0)
                throw new InvalidOperationException($"No stable contours met the {minimumVotes}/{modelSet.Models.Length} sample threshold. Lower the sample fraction, increase feature alignment tolerance, or check that each ROI contains the same part.");

            var centerX = (imageWidth - 1) / 2d;
            var centerY = (imageHeight - 1) / 2d;
            var modelPyramidLevels = pyramidLevels == 0
                ? Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1d, Math.Min(imageWidth, imageHeight) / 6d))) + 1, 1, 8)
                : pyramidLevels;
            byte[] firstImageBytes;
            try { firstImageBytes = Convert.FromBase64String(sourceImages[0]); }
            catch (FormatException ex) { throw new InvalidOperationException("First original training image is invalid.", ex); }
            using var firstImage = Cv2.ImDecode(firstImageBytes, ImreadModes.Unchanged);
            if (firstImage.Empty()) throw new InvalidOperationException("First original training image could not be decoded.");
            using var firstGray = new Mat();
            if (firstImage.Channels() == 1) firstImage.CopyTo(firstGray);
            else if (firstImage.Channels() == 3) Cv2.CvtColor(firstImage, firstGray, ColorConversionCodes.BGR2GRAY);
            else if (firstImage.Channels() == 4) Cv2.CvtColor(firstImage, firstGray, ColorConversionCodes.BGRA2GRAY);
            else throw new InvalidOperationException("Composite training images must have 1, 3, or 4 channels.");
            using var firstImageVision = JLVisionImages.FromMat(firstGray);
            var allContourPoints = consensus.SelectMany(segment => segment.Points).ToArray();
            var minX = allContourPoints.Min(point => point.X + centerX);
            var minY = allContourPoints.Min(point => point.Y + centerY);
            var maxX = allContourPoints.Max(point => point.X + centerX);
            var maxY = allContourPoints.Max(point => point.Y + centerY);
            JlXLDCont? combinedContours = null;
            try
            {
                foreach (var segment in consensus)
                {
                    token.ThrowIfCancellationRequested();
                    using var contour = new JlXLDCont();
                    using var rows = new JlTuple(segment.Points.Select(point => point.Y + centerY).ToArray());
                    using var cols = new JlTuple(segment.Points.Select(point => point.X + centerX).ToArray());
                    contour.GenContourPolygonXld(rows, cols);
                    if (combinedContours is null) combinedContours = new JlXLDCont(contour);
                    else
                    {
                        var next = combinedContours.ConcatObj(contour);
                        combinedContours.Dispose();
                        combinedContours = next;
                    }
                }

                using var model = new JlShapeModel();
                if (anisotropicScale)
                {
                    model.CreateAnisoShapeModelXld(combinedContours!, modelPyramidLevels, -trainingRange, 2 * trainingRange, step,
                        scaleYMin, scaleYMax, scaleStep, scaleXMin, scaleXMax, scaleStep,
                        "auto", "ignore_local_polarity", minContrast);
                }
                else if (scaleTolerance > 0)
                {
                    model.CreateScaledShapeModelXld(combinedContours!, modelPyramidLevels, -trainingRange, 2 * trainingRange, step,
                        scaleMin, scaleMax, scaleStep, "auto", "ignore_local_polarity", minContrast);
                }
                else model.CreateShapeModelXld(combinedContours!, modelPyramidLevels, -trainingRange, 2 * trainingRange, step, "auto", "ignore_local_polarity", minContrast);
                var modelBoundsCenterX = (minX + maxX) / 2d;
                var modelBoundsCenterY = (minY + maxY) / 2d;
                // XLD models use the contour bounding-box center as their native pose origin.
                // Keep the configured training origin by recording that offset for runtime pose correction.
                var poseOffsetX = modelBoundsCenterX - centerX;
                var poseOffsetY = modelBoundsCenterY - centerY;
                using var modelToTrainingImageValues = new JlTuple(new[] { 1d, 0d,
                    modelSet.Models[0].SourceOriginY!.Value + modelBoundsCenterY - centerY,
                    0d, 1d, modelSet.Models[0].SourceOriginX!.Value + modelBoundsCenterX - centerX });
                var modelToTrainingImage = new JlHomMat2D(modelToTrainingImageValues);
                model.SetShapeModelMetric(firstImageVision, modelToTrainingImage,
                    ignorePolarity ? "ignore_global_polarity" : "use_polarity");
                if (clutterRoi is not null)
                    ApplyShapeModelClutter(model, firstImage.Width, firstImage.Height, clutterRoi,
                        modelToTrainingImage, clutterContrast, minContrast, token);
                var modelBytes = model.SerializeShapeModel();
                if (modelBytes.Length > 16 * 1024 * 1024)
                    throw new InvalidOperationException("Composite shape model exceeds the 16 MB embedded workflow asset limit.");
                var modelData = Convert.ToBase64String(modelBytes);
                if (modelData.Length > 24 * 1024 * 1024)
                    throw new InvalidOperationException("Composite shape model exceeds the workflow asset size limit.");

                using var finalModelContours = model.GetShapeModelContours(1);
                var finalContours = ExtractShapeContours(finalModelContours);
                var featurePoints = finalContours.SelectMany(contour => contour).ToArray();
                var modelContoursForPreview = finalContours.Select(contour => contour
                    .Select(point => new VisionOverlayPoint(point.X + poseOffsetX + modelSet.Models[0].SourceOriginX!.Value,
                        point.Y + poseOffsetY + modelSet.Models[0].SourceOriginY!.Value)).ToArray())
                    .Where(points => points.Length > 0).ToArray();
                var (symmetryPeriodDeg, qualityWarnings) = AnalyzeTrainingQuality(featurePoints.Select(point => (point.X + poseOffsetX, point.Y + poseOffsetY)).ToArray());
                var previewOriginX = modelSet.Models[0].SourceOriginX!.Value;
                var previewOriginY = modelSet.Models[0].SourceOriginY!.Value;
                var combinedWarning = $"Composite model keeps features present in at least {minimumVotes} of {modelSet.Models.Length} aligned samples; {Math.Max(0, sampleFeaturePoints.Sum(points => points.Count) - featurePoints.Length)} input feature points were filtered or merged.";
                var warnings = new List<string> { combinedWarning };
                for (var index = 1; index < alignmentScores.Length; index++)
                    if (alignmentScores[index] < .55) warnings.Add($"Sample {index + 1} aligned with a low score ({alignmentScores[index]:0.###}); verify it depicts the same part.");
                warnings.AddRange(qualityWarnings);

                Cv2.ImEncode(".jpg", firstImage, out var previewBytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]);
                if (previewBytes.Length > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Composite training preview exceeds the 4 MB embedded asset limit.");
                var config = ShapeTrainingConfig(node);
                var (trainingRegionArea, trainingRegionType) = CompositeTrainingRegion(node, firstImage.Width, firstImage.Height, token);
                var outputImage = VisionImage.Own(firstImage.Clone());
                var summary = new Dictionary<string, object?>
                {
                    ["trainingSucceeded"] = true, ["trainingModelData"] = modelData,
                    ["trainingModelHash"] = Convert.ToHexString(SHA256.HashData(modelBytes)), ["trainingPreviewImage"] = Convert.ToBase64String(previewBytes),
                    ["trainingConfig"] = config, ["trainingWidth"] = imageWidth, ["trainingHeight"] = imageHeight,
                    ["trainingSourceWidth"] = firstImage.Width, ["trainingSourceHeight"] = firstImage.Height,
                    ["trainingOriginX"] = centerX, ["trainingOriginY"] = centerY,
                    ["trainingOriginImageX"] = previewOriginX, ["trainingOriginImageY"] = previewOriginY,
                    ["trainingPoseOffsetX"] = poseOffsetX, ["trainingPoseOffsetY"] = poseOffsetY,
                    ["trainingRegionArea"] = trainingRegionArea, ["trainingSource"] = "composite-shape-model",
                    ["trainingContourCount"] = finalContours.Count, ["trainingFeaturePointCount"] = featurePoints.Length,
                    ["trainingSymmetryPeriodDeg"] = symmetryPeriodDeg, ["trainingWarnings"] = warnings.ToArray(),
                    ["trainingPyramidLevels"] = pyramidLevels, ["trainingOriginMode"] = node.GetString("trainingOriginMode", "centroid"),
                    ["trainingContour"] = modelContoursForPreview.FirstOrDefault() ?? [], ["trainingContours"] = modelContoursForPreview,
                    ["trainingRegionType"] = trainingRegionType,
                    ["trainingScaleMin"] = Math.Min(scaleXMin, scaleYMin), ["trainingScaleMax"] = Math.Max(scaleXMax, scaleYMax),
                    ["trainingScaleXMin"] = scaleXMin, ["trainingScaleXMax"] = scaleXMax,
                    ["trainingScaleYMin"] = scaleYMin, ["trainingScaleYMax"] = scaleYMax,
                    ["trainingScaleToleranceXPercent"] = scaleToleranceX * 100, ["trainingScaleToleranceYPercent"] = scaleToleranceY * 100,
                    ["trainingIgnorePolarity"] = ignorePolarity, ["trainingMinContrast"] = minContrast,
                    ["trainingClutterEnabled"] = clutterRoi is not null, ["trainingClutterContrast"] = clutterRoi is not null ? clutterContrast : null,
                    ["trainingSampleCount"] = modelSet.Models.Length, ["trainingMinSampleFraction"] = fraction,
                    ["trainingMinimumSampleVotes"] = minimumVotes, ["trainingFeatureTolerancePx"] = featureTolerance,
                    ["trainingConsensusContourCount"] = consensus.Count,
                    ["trainingConsensusFeaturePointCount"] = consensus.Sum(segment => segment.Points.Length),
                    ["trainingSampleAlignmentScores"] = alignmentScores
                };
                var overlays = new List<VisionOverlay>(modelContoursForPreview.Select((points, index) =>
                    new VisionOverlay($"shape-training-contour-{index}", VisionOverlayType.Polyline, Points: points, Label: "复合模型稳定轮廓", Stroke: "#13c2c2", StrokeWidth: 2)))
                {
                    new("shape-training-origin", VisionOverlayType.Point, X: previewOriginX, Y: previewOriginY,
                        Label: "位姿原点", Stroke: "#fa541c", StrokeWidth: 3)
                };
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(outputImage), ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0), ["count"] = VisionValue.Integer(0) },
                    summary, overlays);
            }
            finally { combinedContours?.Dispose(); }
    }

    private static List<ShapeFeaturePoint[]> ExtractShapeContours(JlXLDCont contours)
    {
        var extracted = new List<ShapeFeaturePoint[]>();
        for (var index = 1; index <= contours.CountObj(); index++)
        {
            using var selected = contours.SelectObj(index);
            selected.GetContourXld(out var rows, out var cols);
            using (rows) using (cols)
            {
                if (rows.Length < 2 || rows.Length != cols.Length) continue;
                var points = new ShapeFeaturePoint[rows.Length];
                for (var pointIndex = 0; pointIndex < rows.Length; pointIndex++)
                    points[pointIndex] = new(cols[pointIndex].D, rows[pointIndex].D);
                extracted.Add(points);
            }
        }
        return extracted;
    }

    private static List<ConsensusContour> BuildConsensusContours(IReadOnlyList<List<ShapeFeaturePoint[]>> sampleContours,
        IReadOnlyList<ShapeFeatureSpatialIndex> sampleIndexes, int minimumVotes, double tolerance)
    {
        var proposed = new List<ConsensusContour>();
        for (var sampleIndex = 0; sampleIndex < sampleContours.Count; sampleIndex++)
        foreach (var contour in sampleContours[sampleIndex])
        {
            var segment = new List<ShapeFeaturePoint>();
            var supports = new List<double>();
            var previousCandidate = default(ShapeFeaturePoint);
            var hasPreviousCandidate = false;
            void Flush()
            {
                if (segment.Count >= 4 && PolylineLength(segment) >= tolerance * 2)
                    proposed.Add(new(segment.ToArray(), supports.Average()));
                segment.Clear(); supports.Clear(); hasPreviousCandidate = false;
            }

            foreach (var candidate in contour)
            {
                var matched = new List<ShapeFeaturePoint>(sampleIndexes.Count);
                foreach (var sampleIndexForVote in Enumerable.Range(0, sampleIndexes.Count))
                    if (sampleIndexes[sampleIndexForVote].TryNearest(candidate, tolerance, out var nearest)) matched.Add(nearest);
                if (matched.Count < minimumVotes)
                {
                    Flush();
                    continue;
                }
                var averaged = new ShapeFeaturePoint(matched.Average(point => point.X), matched.Average(point => point.Y));
                if (hasPreviousCandidate && Distance(previousCandidate, candidate) > tolerance * 2.5) Flush();
                segment.Add(averaged);
                supports.Add(matched.Count / (double)sampleIndexes.Count);
                previousCandidate = candidate;
                hasPreviousCandidate = true;
            }
            Flush();
        }

        var accepted = new List<ConsensusContour>();
        foreach (var candidate in proposed.OrderByDescending(segment => segment.Support).ThenByDescending(segment => segment.Points.Length))
        {
            if (accepted.Any(existing => SimilarConsensusContour(existing.Points, candidate.Points, tolerance))) continue;
            accepted.Add(candidate);
        }
        return accepted;
    }

    private static bool SimilarConsensusContour(IReadOnlyList<ShapeFeaturePoint> first, IReadOnlyList<ShapeFeaturePoint> second, double tolerance)
    {
        if (first.Count < 4 || second.Count < 4) return false;
        var firstLength = PolylineLength(first);
        var secondLength = PolylineLength(second);
        if (firstLength <= 0 || secondLength <= 0 || Math.Min(firstLength, secondLength) / Math.Max(firstLength, secondLength) < .6) return false;
        var firstSample = ResamplePolyline(first, 96);
        var secondSample = ResamplePolyline(second, 96);
        static double DirectedMean(IReadOnlyList<ShapeFeaturePoint> source, IReadOnlyList<ShapeFeaturePoint> target) =>
            source.Average(point => target.Min(other => Distance(point, other)));
        return (DirectedMean(firstSample, secondSample) + DirectedMean(secondSample, firstSample)) / 2 <= Math.Max(.5, tolerance * .65);
    }

    private static ShapeFeaturePoint[] ResamplePolyline(IReadOnlyList<ShapeFeaturePoint> points, int maximumCount)
    {
        if (points.Count <= maximumCount) return points.ToArray();
        var stride = (points.Count - 1d) / (maximumCount - 1);
        return Enumerable.Range(0, maximumCount).Select(index => points[(int)Math.Round(index * stride)]).ToArray();
    }

    private static double PolylineLength(IReadOnlyList<ShapeFeaturePoint> points)
    {
        var length = 0d;
        for (var index = 1; index < points.Count; index++) length += Distance(points[index - 1], points[index]);
        return length;
    }

    private static double Distance(ShapeFeaturePoint first, ShapeFeaturePoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static (double Area, string Type) CompositeTrainingRegion(NodeDefinition node, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roi = node.GetRoi();
        if (roi is null) return ((double)width * height, "FullImage");
        var bounds = RoiBounds(roi) & new Rect(0, 0, width, height);
        using var mask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.All(0));
        FillRoiMask(mask, roi, bounds, cancellationToken);
        return (Cv2.CountNonZero(mask), roi.Type.ToString());
    }

    private static (int? SymmetryPeriodDeg, string[] Warnings) AnalyzeTrainingQuality(IReadOnlyList<(double X, double Y)> featurePoints)
    {
        var warnings = new List<string>();
        if (featurePoints.Count < 32)
            warnings.Add("模型边缘特征较少，建议检查模型区域、图像对比度和最低特征对比度。");

        int? symmetryPeriod = null;
        foreach (var degrees in new[] { 90, 180 })
        {
            if (RotationalFeatureOverlap(featurePoints, degrees) < 0.9) continue;
            symmetryPeriod = degrees;
            warnings.Add($"模型边缘在约 {degrees}° 旋转后仍高度重合，方向可能存在周期性歧义；请用非对称定位特征确认角度。");
            break;
        }
        return (symmetryPeriod, warnings.ToArray());
    }

    private static double RotationalFeatureOverlap(IReadOnlyList<(double X, double Y)> points, int degrees)
    {
        if (points.Count < 16) return 0;
        const int maximumSamples = 1200;
        var stride = Math.Max(1, (int)Math.Ceiling(points.Count / (double)maximumSamples));
        var samples = Enumerable.Range(0, points.Count).Where(index => index % stride == 0).Select(index => points[index]).ToArray();
        var radians = degrees * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        const double toleranceSquared = 1.5 * 1.5;
        var overlapping = 0;
        foreach (var (x, y) in samples)
        {
            var rotatedX = cos * x - sin * y;
            var rotatedY = sin * x + cos * y;
            var nearest = samples.Min(point => (point.X - rotatedX) * (point.X - rotatedX) + (point.Y - rotatedY) * (point.Y - rotatedY));
            if (nearest <= toleranceSquared) overlapping++;
        }
        return overlapping / (double)samples.Length;
    }

    private static string ShapeTrainingConfig(NodeDefinition node)
    {
        var clutterRoi = node.GetRoi("clutterRoi");
        var clutterSettings = clutterRoi is null ? "none"
            : FormattableString.Invariant($"{RoiFingerprint(clutterRoi)}:{Math.Clamp(node.GetInt("clutterContrast", node.GetInt("minContrast", 5)), 1, 255)}");
        var autoTag = MatchingAutoTune.AutoFingerprint(node);
        var autoSettings = autoTag.Length == 0 ? string.Empty : $"|{autoTag}";
        return FormattableString.Invariant($"shape-v6|{ShapeTrainingConfigV5(node)[9..]}|{clutterSettings}{autoSettings}");
    }

    private static string ShapeTrainingConfigV5(NodeDefinition node)
    {
        return FormattableString.Invariant($"shape-v5|{ShapeTrainingConfigV4(node)[9..]}|{Math.Clamp(node.GetDouble("scaleToleranceXPercent", 0), 0, 50):R}|{Math.Clamp(node.GetDouble("scaleToleranceYPercent", 0), 0, 50):R}");
    }

    private static string ShapeTrainingConfigV4(NodeDefinition node)
    {
        return FormattableString.Invariant($"shape-v4|{ShapeTrainingConfigV3(node)[9..]}|{Math.Clamp(node.GetDouble("trainingMinSampleFraction", .75), .5, 1):R}|{Math.Clamp(node.GetDouble("trainingFeatureTolerancePx", 2), .5, 10):R}");
    }

    private static string ShapeTrainingConfigV3(NodeDefinition node)
    {
        var roi = node.GetRoi();
        var roiText = RoiFingerprint(roi);
        return FormattableString.Invariant($"shape-v3|{node.GetString("templatePath", string.Empty)}|{node.GetString("binarize", "auto")}|{node.GetDouble("threshold", 128):R}|{node.GetBool("invert", false)}|{node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)):R}|{node.GetDouble("angleStepDeg", 1):R}|{node.GetInt("pyramidLevels", 0)}|{roiText}|{node.GetString("trainingOriginMode", "centroid")}|{node.GetDouble("trainingOriginX", 0):R}|{node.GetDouble("trainingOriginY", 0):R}|{node.GetDouble("scaleTolerancePercent", 0):R}|{(node.GetDouble("scaleTolerancePercent", 0) > 0 ? node.GetDouble("scaleStepPercent", 1) : 0):R}|{node.GetBool("ignorePolarity", false)}|{node.GetInt("minContrast", 5)}");
    }

    private static string ShapeTrainingConfigV2(NodeDefinition node)
    {
        var roi = node.GetRoi();
        var roiText = RoiFingerprint(roi);
        return FormattableString.Invariant($"shape-v2|{node.GetString("templatePath", string.Empty)}|{node.GetString("binarize", "auto")}|{node.GetDouble("threshold", 128):R}|{node.GetBool("invert", false)}|{node.GetDouble("angleRangeDeg", 180):R}|{node.GetDouble("angleStepDeg", 1):R}|{roiText}|{node.GetString("trainingOriginMode", "centroid")}|{node.GetDouble("trainingOriginX", 0):R}|{node.GetDouble("trainingOriginY", 0):R}|{node.GetDouble("scaleTolerancePercent", 0):R}|{(node.GetDouble("scaleTolerancePercent", 0) > 0 ? node.GetDouble("scaleStepPercent", 1) : 0):R}|{node.GetBool("ignorePolarity", false)}|{node.GetInt("minContrast", 5)}");
    }

    private static string ShapeTrainingConfigV1(NodeDefinition node)
    {
        var roi = node.GetRoi();
        var roiText = roi is null ? "none" : roi.Type == VisionRoiType.Rectangle
            ? FormattableString.Invariant($"{roi.X:R},{roi.Y:R},{roi.Width:R},{roi.Height:R}") : roi.Type.ToString();
        return FormattableString.Invariant($"shape-v1|{node.GetString("templatePath", string.Empty)}|{node.GetString("binarize", "auto")}|{node.GetDouble("threshold", 128):R}|{node.GetBool("invert", false)}|{node.GetDouble("angleRangeDeg", 180):R}|{node.GetDouble("angleStepDeg", 1):R}|{roiText}|{node.GetString("trainingOriginMode", "centroid")}|{node.GetDouble("trainingOriginX", 0):R}|{node.GetDouble("trainingOriginY", 0):R}");
    }

    private static bool IsCompatibleLegacyShapeConfig(NodeDefinition node, string savedConfig)
    {
        if (savedConfig.StartsWith("shape-v5|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null)
            return string.Equals(savedConfig, ShapeTrainingConfigV5(node), StringComparison.Ordinal);
        if (savedConfig.StartsWith("shape-v4|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null
            && Math.Abs(node.GetDouble("scaleToleranceXPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceYPercent", 0)) < 1e-9)
            return string.Equals(savedConfig, ShapeTrainingConfigV4(node), StringComparison.Ordinal);
        if (savedConfig.StartsWith("shape-v3|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null
            && Math.Abs(node.GetDouble("trainingMinSampleFraction", .75) - .75) < 1e-9
            && Math.Abs(node.GetDouble("trainingFeatureTolerancePx", 2) - 2) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceXPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceYPercent", 0)) < 1e-9)
            return string.Equals(savedConfig, ShapeTrainingConfigV3(node), StringComparison.Ordinal);
        if (savedConfig.StartsWith("shape-v2|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null
            && node.GetInt("pyramidLevels", 0) == 0
            && Math.Abs(node.GetDouble("scaleToleranceXPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceYPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)) - node.GetDouble("angleRangeDeg", 180)) < 1e-9)
            return string.Equals(savedConfig, ShapeTrainingConfigV2(node), StringComparison.Ordinal);
        var saved = savedConfig.Split('|');
        var current = ShapeTrainingConfigV1(node).Split('|');
        if (saved.Length != current.Length || saved[0] != "shape-v1"
            || node.GetDouble("scaleTolerancePercent", 0) != 0
            || node.GetDouble("scaleToleranceXPercent", 0) != 0
            || node.GetDouble("scaleToleranceYPercent", 0) != 0
            || node.GetRoi("clutterRoi") is not null
            || node.GetInt("pyramidLevels", 0) != 0
            || node.GetBool("ignorePolarity", false)
            || node.GetInt("minContrast", 5) != 5)
            return false;
        return string.Equals(savedConfig, ShapeTrainingConfigV1(node), StringComparison.Ordinal);
    }

    private static string RoiFingerprint(VisionRoi? roi)
    {
        if (roi is null) return "none";
        return roi.Type switch
        {
            VisionRoiType.Rectangle => FormattableString.Invariant($"Rectangle:{roi.X:R},{roi.Y:R},{roi.Width:R},{roi.Height:R}"),
            VisionRoiType.Circle => FormattableString.Invariant($"Circle:{roi.X:R},{roi.Y:R},{roi.Radius:R}"),
            VisionRoiType.Polygon => "Polygon:" + string.Join(';', (roi.Points ?? []).Select(point => FormattableString.Invariant($"{point.X:R},{point.Y:R}"))),
            VisionRoiType.Brush => "Brush:" + string.Join(';', (roi.Strokes ?? []).Select(FormatBrushStroke)),
            _ => roi.Type.ToString()
        };

        static string FormatBrushStroke(VisionRoiBrushStroke stroke) =>
            FormattableString.Invariant($"{stroke.Mode}:{stroke.Width:R}:")
            + string.Join(',', stroke.Points.Select(point => FormattableString.Invariant($"{point.X:R},{point.Y:R}")));
    }

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
        try
        {
            var probe = templateBuffer;
            Mat? probeMask = null;
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
            // rotatedMaskOwned disposed via probeMask path when assigned; avoid double-dispose
            if (rotatedMaskOwned is null) { }
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

    /// <summary>
    /// Binary ignore paint (0/255) → float weight map: 1 outside, <paramref name="softWeight"/> inside.
    /// Optional Gaussian feather mixes the boundary (honest approximate soft edge).
    /// </summary>
    private static Mat BuildIgnoreWeightMap(Mat ignorePaint, double softWeight, int featherPx)
    {
        using var binary = new Mat();
        ignorePaint.ConvertTo(binary, MatType.CV_32FC1, 1 / 255d);
        using var inside = new Mat();
        Cv2.Multiply(binary, 1d - softWeight, inside);
        var weight = new Mat(ignorePaint.Rows, ignorePaint.Cols, MatType.CV_32FC1, Scalar.All(1));
        Cv2.Subtract(weight, inside, weight);
        if (featherPx > 0)
        {
            var k = featherPx * 2 + 1;
            Cv2.GaussianBlur(weight, weight, new Size(k, k), featherPx / 2d);
        }
        return weight;
    }

    private static void ApplySoftIgnoreLerp(Mat image, Mat weight01, double mean)
    {
        using var imgF = new Mat();
        image.ConvertTo(imgF, MatType.CV_32FC1);
        using var centered = new Mat();
        Cv2.Subtract(imgF, mean, centered);
        Cv2.Multiply(centered, weight01, centered);
        Cv2.Add(centered, mean, imgF);
        imgF.ConvertTo(image, image.Type());
    }

    private static double SampleIgnoreWeight(VisionRoi ignoreRoi, double x, double y, double softWeight, int featherPx)
    {
        if (featherPx <= 0)
            return Contains(ignoreRoi, x, y) ? softWeight : 1d;
        if (ignoreRoi.Type == VisionRoiType.Rectangle && ignoreRoi.X is { } rx && ignoreRoi.Y is { } ry
            && ignoreRoi.Width is { } rw && ignoreRoi.Height is { } rh && rw > 0 && rh > 0)
        {
            var dx = x < rx ? rx - x : (x > rx + rw ? x - (rx + rw) : 0d);
            var dy = y < ry ? ry - y : (y > ry + rh ? y - (ry + rh) : 0d);
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist <= 0) return softWeight;
            if (dist >= featherPx) return 1d;
            var t = dist / featherPx;
            return softWeight + (1d - softWeight) * t;
        }
        return Contains(ignoreRoi, x, y) ? softWeight : 1d;
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

    private static double Ssd(JlImage scene, JlImage template, double x, double y, double angle, double originX, double originY)
    {
        using var source = JLVisionImages.ToMat(scene); template.GetImageSize(out int width, out int height);
        using var inverse = new Mat(2, 3, MatType.CV_64FC1); var c = Math.Cos(angle); var s = Math.Sin(angle);
        inverse.Set(0, 0, c); inverse.Set(0, 1, -s); inverse.Set(0, 2, originX - c * x + s * y);
        inverse.Set(1, 0, s); inverse.Set(1, 1, c); inverse.Set(1, 2, originY - s * x - c * y);
        using var aligned = new Mat(); JLVisionOperators.WarpAffine(source, aligned, inverse, new(width, height));
        using var alignedImage = JLVisionImages.FromMat(aligned); using var ar = alignedImage.ConvertImageType("real"); using var tr = template.ConvertImageType("real");
        using var difference = ar.SubImage(tr, 1d, 0d); using var squared = difference.MultImage(difference, 1d, 0d);
        using var a2 = ar.MultImage(ar, 1d, 0d); using var t2 = tr.MultImage(tr, 1d, 0d); using var domain = tr.GetDomain();
        var energy = Math.Sqrt(a2.Intensity(domain, out double _) * t2.Intensity(domain, out double _));
        return energy > 0 ? 1 - Math.Clamp(squared.Intensity(domain, out double _) / energy, 0, 1) : 0;
    }
}
