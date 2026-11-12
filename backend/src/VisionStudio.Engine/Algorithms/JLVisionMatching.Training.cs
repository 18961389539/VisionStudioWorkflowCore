using System.Security.Cryptography;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
// shape 训练：训练位姿对齐、ROI/点坐标变换、训练区域与训练质量自检。
internal static partial class JLVisionMatching
{
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
}
