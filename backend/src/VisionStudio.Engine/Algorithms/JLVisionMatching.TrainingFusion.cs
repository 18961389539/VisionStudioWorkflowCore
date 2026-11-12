using System.Security.Cryptography;
using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
// 多视角模型融合：CombineShapeModels 与轮廓共识（consensus）。
internal static partial class JLVisionMatching
{
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
}
