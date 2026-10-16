using JLVisionLib;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Algorithms;

internal static class JLVisionMatching
{
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
            if (!(shape && savedModel.Length > 0) && (templateBuffer.Width > source.Width || templateBuffer.Height > source.Height)) throw new InvalidOperationException("Template is larger than the scene image.");
            using var templateColor = JLVisionImages.FromMat(templateBuffer);
            using var templateChannels = templateColor.CountChannels();
            using var template = templateChannels.I == 1 ? templateColor.CopyImage() : templateColor.Rgb1ToGray();
            var range = Math.Clamp(node.GetDouble("angleRangeDeg", shape ? 180 : 0), 0, 180) * Math.PI / 180;
            var step = Math.Clamp(node.GetDouble("angleStepDeg", shape ? 1 : 5), .1, 30) * Math.PI / 180;
            var scaleTolerance = shape ? Math.Clamp(node.GetDouble("scaleTolerancePercent", 0), 0, 50) / 100d : 0d;
            var scaleMin = 1 - scaleTolerance;
            var scaleMax = 1 + scaleTolerance;
            var scaleStep = shape ? Math.Clamp(node.GetDouble("scaleStepPercent", 1), .1, 10) / 100d : 0d;
            var ignorePolarity = shape && node.GetBool("ignorePolarity", false);
            var minContrast = shape ? Math.Clamp(node.GetInt("minContrast", 5), 1, 255) : 5;
            var minScore = Math.Clamp(node.GetDouble("minScore", shape ? .6 : .5), 0, 1);
            var method = node.GetString("method", "ccoeff-normed").Trim().ToLowerInvariant();
            if (!shape && method is not ("ccoeff-normed" or "sqdiff-normed")) throw new InvalidOperationException($"Unsupported template matching method '{method}'.");
            var squaredDifference = !shape && method == "sqdiff-normed";
            var maximum = shape ? Math.Clamp(node.GetInt("maxTargets", 5), 1, 100) : squaredDifference ? 100 : 1;
            var searchRoi = shape ? node.GetRoi("searchRoi") : null;
            var originX = (templateBuffer.Width - 1) / 2d; var originY = (templateBuffer.Height - 1) / 2d;
            var minArea = node.GetDouble("minArea", 200);
            var templateSource = shape && savedModel.Length > 0 ? "trained-model" : fromPort ? "input-port" : "file";
            var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0) };
            if (shape) outputs["scale"] = VisionValue.Double(1);
            var summary = new Dictionary<string, object?> { ["found"] = false, ["bestScore"] = 0d, ["score"] = 0d, ["templateSource"] = templateSource, ["algorithmBackend"] = "JLVision", ["angleRangeDeg"] = range * 180 / Math.PI, ["angleStepDeg"] = step * 180 / Math.PI };
            if (shape)
            {
                summary["scaleTolerancePercent"] = scaleTolerance * 100;
                summary["ignorePolarity"] = ignorePolarity;
            }
            var overlays = new List<VisionOverlay>();
            var detections = new List<VisionDetection>();
            var templateBox = new Rect(0, 0, templateBuffer.Width, templateBuffer.Height);
            if (shape && savedModel.Length > 0)
            {
                templateBox = new Rect(0, 0, (int)node.GetInt("trainedWidth", 1), (int)node.GetInt("trainedHeight", 1));
                originX = node.GetDouble("trainedOriginX", 0);
                originY = node.GetDouble("trainedOriginY", 0);
            }
            var searchOffsetX = 0;
            var searchOffsetY = 0;
            var searchMatchLimit = maximum;
            JlTuple rows, cols, angles, scores, scales;
            if (searchRoi is not null)
            {
                var searchBounds = RoiBounds(searchRoi);
                var imageBounds = new Rect(0, 0, source.Width, source.Height);
                var clippedSearch = searchBounds & imageBounds;
                if (clippedSearch.Width <= 0 || clippedSearch.Height <= 0)
                {
                    summary["searchRegionEmpty"] = true;
                    outputs["image"] = VisionValue.Image(source.CloneOwned());
                    outputs["count"] = VisionValue.Integer(0);
                    outputs["detections"] = VisionValue.Detections([]);
                    summary["accepted"] = 0;
                    return new(outputs, summary, overlays);
                }
                // Search regions constrain candidate origins; a margin keeps the full trained contour
                // available when a target lies near the region boundary.
            var radius = Math.Sqrt((double)templateBox.Width * templateBox.Width + (double)templateBox.Height * templateBox.Height);
                var margin = (int)Math.Ceiling(radius * scaleMax) + 2;
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
            if (shape)
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
                    using var binary = ContourNodesHelper.Binarize(templateBuffer, node.GetString("binarize", "auto"), node.GetDouble("threshold", 128), node.GetBool("invert", false), out _, out _);
                    JLVisionOperators.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    var bestContour = contours.OrderByDescending(c => JLVisionOperators.ContourArea(c)).FirstOrDefault();
                    if (bestContour is null || JLVisionOperators.ContourArea(bestContour) <= 0)
                    { summary["reason"] = "template-has-no-contour"; outputs["image"] = VisionValue.Image(source.CloneOwned()); return new(outputs, summary, overlays); }
                    var moments = JLVisionOperators.Moments(bestContour);
                    originX = moments.M10 / moments.M00; originY = moments.M01 / moments.M00;
                    templateBox = JLVisionOperators.BoundingRect(bestContour);
                    shapeLease = JLVisionModelCache.Acquire(Key(templateBuffer, true, range, step, originX, originY, 0, scaleMin, scaleMax, scaleStep, ignorePolarity, minContrast), () =>
                    {
                        var created = new JlShapeModel();
                        try
                        {
                            using var levels = new JlTuple("auto"); using var delta = new JlTuple(step); using var optimize = new JlTuple("auto"); using var contrast = new JlTuple("auto"); using var minimum = new JlTuple(minContrast);
                            var metric = ignorePolarity ? "ignore_global_polarity" : "use_polarity";
                            if (scaleTolerance > 0)
                            {
                                using var scaleDelta = new JlTuple(scaleStep);
                                created.CreateScaledShapeModel(template, levels, -range, 2 * range, delta, scaleMin, scaleMax, scaleDelta, optimize, metric, contrast, minimum);
                            }
                            else created.CreateShapeModel(template, levels, -range, 2 * range, delta, optimize, metric, contrast, minimum);
                            created.SetShapeModelOrigin(originY - (templateBuffer.Height - 1) / 2d, originX - (templateBuffer.Width - 1) / 2d);
                            return created;
                        }
                        catch { created.Dispose(); throw; }
                    });
                    model = (JlShapeModel)shapeLease.Model; summary["modelCacheHit"] = shapeLease.Hit;
                }
                try
                {
                    if (scaleTolerance > 0)
                        model.FindScaledShapeModel(matchingScene, -range, 2 * range, scaleMin, scaleMax, minScore, searchMatchLimit, .5, "least_squares", 0, .8, out rows, out cols, out angles, out scales, out scores);
                    else
                    {
                        matchingScene.FindShapeModel(model, -range, 2 * range, minScore, searchMatchLimit, .5, "least_squares", 0, .8, out rows, out cols, out angles, out scores);
                        scales = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
                    }
                }
                finally { shapeLease?.Dispose(); }
            }
            else
            {
                var pyramidLevels = node.GetInt("pyramidLevels", 0);
                if (pyramidLevels is < 0 or > 8) throw new InvalidOperationException("pyramidLevels must be between 0 and 8.");
                if (pyramidLevels == 0) pyramidLevels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1d, Math.Min(templateBuffer.Width, templateBuffer.Height) / 6d))) + 1, 1, 8);
                summary["pyramidLevels"] = pyramidLevels;
                using var lease = JLVisionModelCache.Acquire(Key(templateBuffer, false, range, step, originX, originY, pyramidLevels), () =>
                {
                    var created = new JlNCCModel();
                    try { created.CreateNccModel(template, pyramidLevels, -range, 2 * range, step, "use_polarity"); return created; }
                    catch { created.Dispose(); throw; }
                });
                var model = (JlNCCModel)lease.Model; summary["modelCacheHit"] = lease.Hit; summary["method"] = method;
                gray.FindNccModel(model, -range, 2 * range, squaredDifference ? .001 : minScore, maximum, .5, "true", 0, out rows, out cols, out angles, out scores);
                scales = new JlTuple(Enumerable.Repeat(1d, scores.Length).ToArray());
            }
            using (rows) using (cols) using (angles) using (scores) using (scales)
            {
                var candidates = Enumerable.Range(0, scores.Length).Select(i => (Index: i, Score: squaredDifference ? Ssd(gray, template, cols[i].D, rows[i].D, angles[i].D, originX, originY) : scores[i].D)).OrderByDescending(x => x.Score).ToArray();
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var i = candidate.Index; var x = cols[i].D + searchOffsetX; var y = rows[i].D + searchOffsetY; var radians = angles[i].D; var scale = scales[i].D; var score = candidate.Score;
                    if (score < minScore || !shape && detections.Count > 0) continue;
                    if (shape && searchRoi is not null && !Contains(searchRoi, x, y)) continue;
                    var cos = Math.Cos(radians); var sin = Math.Sin(radians);
                    var corners = new[] { (templateBox.Left, templateBox.Top), (templateBox.Right - 1, templateBox.Top), (templateBox.Right - 1, templateBox.Bottom - 1), (templateBox.Left, templateBox.Bottom - 1) }
                        .Select(p => new VisionPoint2D(x + scale * (cos * (p.Item1 - originX) + sin * (p.Item2 - originY)), y + scale * (-sin * (p.Item1 - originX) + cos * (p.Item2 - originY)))).ToArray();
                    var left = corners.Min(p => p.X); var top = corners.Min(p => p.Y); var width = corners.Max(p => p.X) - left + 1; var height = corners.Max(p => p.Y) - top + 1;
                    if (shape && templateBox.Width * templateBox.Height < minArea) continue;
                    var distance = Math.Max(0, node.GetDouble("minCenterDistance", 20));
                    if (shape && detections.Any(d => Math.Sqrt(Math.Pow(d.Circle.X - x, 2) + Math.Pow(d.Circle.Y - y, 2)) < distance)) continue;
                    if (shape && detections.Any(d => Overlap(d.BoundingBox, new(left, top, width, height)) > .3)) continue;
                    var degrees = VisionTransform2D.NormalizeAngleDeg(radians * 180 / Math.PI);
                    if (shape && detections.Count >= maximum) continue;
                    if (!outputs["found"].Require<bool>("found"))
                    {
                        outputs["found"] = VisionValue.Boolean(true); outputs["score"] = VisionValue.Double(score);
                        if (shape) outputs["scale"] = VisionValue.Double(scale);
                        outputs["x"] = VisionValue.Double(shape ? x : left); outputs["y"] = VisionValue.Double(shape ? y : top);
                        outputs["angle"] = VisionValue.Double(degrees); outputs["center"] = VisionValue.Point(shape ? new(x, y) : new(left + width / 2, top + height / 2));
                        outputs["rectangle"] = VisionValue.Rectangle(new(shape ? x : left, shape ? y : top, width, height, shape ? -degrees : degrees));
                        if (shape) outputs["pose"] = VisionValue.Pose(new(x, y, degrees));
                        summary["found"] = true; summary["bestScore"] = score; summary["score"] = score; summary["matchedAngle"] = degrees; summary["angle"] = degrees;
                        if (shape) summary["matchedScale"] = scale;
                    }
                    var box = new VisionRectangle2D(left, top, width, height);
                    detections.Add(new(detections.Count, left, top, width, height, templateBox.Width * templateBox.Height * scale * scale, 2 * (width + height), 0, new(x, y, Math.Sqrt(width * width + height * height) / 2), box, score));
                    overlays.Add(new($"match-{i}", VisionOverlayType.Polygon, Points: corners.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray(), Label: shape ? $"s={score:0.###} a={degrees:0.#} ×{scale:0.###}" : $"s={score:0.###} a={degrees:0.#}", Stroke: "#52c41a", StrokeWidth: 2));
                }
            }
            token.ThrowIfCancellationRequested();
            if (shape) { outputs["image"] = VisionValue.Image(source.CloneOwned()); outputs["count"] = VisionValue.Integer(detections.Count); outputs["detections"] = VisionValue.Detections(detections); }
            summary["accepted"] = detections.Count;
            return new(outputs, summary, overlays);
        }
        finally { searchScene?.Dispose(); fileImage?.Dispose(); savedModelPlaceholder?.Dispose(); }
    }

    private static double Overlap(VisionRectangle2D a, VisionRectangle2D b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / Math.Max(1e-9, a.Width * a.Height + b.Width * b.Height - intersection);
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

    private static void FillRoiMask(Mat mask, VisionRoi? roi, Rect cropRect)
    {
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
                var points = (roi.Points ?? []).Select(point => new Point((int)Math.Round(point.X - cropRect.X), (int)Math.Round(point.Y - cropRect.Y))).ToArray();
                Cv2.FillPoly(mask, [points], Scalar.All(255));
                break;
            }
            case VisionRoiType.Brush:
                VisionRoiOpenCvExtensions.DrawBrushStrokes(mask, roi.Strokes, cropRect.X, cropRect.Y);
                break;
        }
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
        FillRoiMask(roiMask, roi, cropRect);
        if (Cv2.CountNonZero(roiMask) < 64) throw new InvalidOperationException("Training region must contain at least 64 pixels.");
        using var binary = ContourNodesHelper.Binarize(crop, node.GetString("binarize", "auto"), node.GetDouble("threshold", 128), node.GetBool("invert", false), out _, out _);
        Cv2.BitwiseAnd(binary, roiMask, binary);
        Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var contour = contours.OrderByDescending(c => Cv2.ContourArea(c)).FirstOrDefault();
        if (contour is null || Cv2.ContourArea(contour) <= 0) throw new InvalidOperationException("The selected training region has no usable contour. Adjust binarization or ROI.");
        token.ThrowIfCancellationRequested();
        var moments = Cv2.Moments(contour);
        var originMode = node.GetString("trainingOriginMode", "centroid");
        var originX = originMode.Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? node.GetDouble("trainingOriginX", cropRect.X + crop.Width / 2d) - cropRect.X : moments.M10 / moments.M00;
        var originY = originMode.Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? node.GetDouble("trainingOriginY", cropRect.Y + crop.Height / 2d) - cropRect.Y : moments.M01 / moments.M00;
        if (originX < 0 || originY < 0 || originX >= crop.Width || originY >= crop.Height)
            throw new InvalidOperationException("Manual training origin must lie inside the selected training ROI.");
        var range = Math.Clamp(node.GetDouble("angleRangeDeg", 180), 0, 180) * Math.PI / 180;
        var step = Math.Clamp(node.GetDouble("angleStepDeg", 1), .1, 30) * Math.PI / 180;
        var scaleTolerance = Math.Clamp(node.GetDouble("scaleTolerancePercent", 0), 0, 50) / 100d;
        var scaleMin = 1 - scaleTolerance;
        var scaleMax = 1 + scaleTolerance;
        var scaleStep = Math.Clamp(node.GetDouble("scaleStepPercent", 1), .1, 10) / 100d;
        var ignorePolarity = node.GetBool("ignorePolarity", false);
        var minContrast = Math.Clamp(node.GetInt("minContrast", 5), 1, 255);
        using var sourceImage = JLVisionImages.FromMat(crop);
        using var maskImage = JLVisionImages.FromMat(roiMask);
        using var trainingRegion = maskImage.Threshold(1d, 255d);
        using var image = sourceImage.ReduceDomain(trainingRegion);
        using var model = new JlShapeModel();
        using (var levels = new JlTuple("auto")) using (var delta = new JlTuple(step)) using (var optimize = new JlTuple("auto")) using (var contrast = new JlTuple("auto")) using (var minimum = new JlTuple(minContrast))
        {
            var metric = ignorePolarity ? "ignore_global_polarity" : "use_polarity";
            if (scaleTolerance > 0)
            {
                using var scaleDelta = new JlTuple(scaleStep);
                model.CreateScaledShapeModel(image, levels, -range, 2 * range, delta, scaleMin, scaleMax, scaleDelta, optimize, metric, contrast, minimum);
            }
            else model.CreateShapeModel(image, levels, -range, 2 * range, delta, optimize, metric, contrast, minimum);
        }
        model.SetShapeModelOrigin(originY - (crop.Height - 1) / 2d, originX - (crop.Width - 1) / 2d);
        var data = model.SerializeShapeModel();
        if (data.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Trained model exceeds the 16 MB embedded workflow asset limit.");
        Cv2.ImEncode(".jpg", trainingMat, out var previewBytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]);
        if (previewBytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Training preview exceeds the 4 MB embedded workflow asset limit; crop the training image before training.");
        var config = ShapeTrainingConfig(node);
        using var modelContours = model.GetShapeModelContours(1);
        var previewContours = new List<VisionOverlayPoint[]>();
        for (var contourIndex = 1; contourIndex <= modelContours.CountObj(); contourIndex++)
        {
            using var selectedModelContour = modelContours.SelectObj(contourIndex);
            selectedModelContour.GetContourXld(out var modelRows, out var modelCols);
            using (modelRows) using (modelCols)
            {
                var stride = Math.Max(1, (int)Math.Ceiling(modelRows.Length / 500d));
                var points = Enumerable.Range(0, modelRows.Length).Where(index => index % stride == 0)
                    .Select(index => new VisionOverlayPoint(modelCols[index].D + originX + cropRect.X, modelRows[index].D + originY + cropRect.Y)).ToArray();
                if (points.Length > 0) previewContours.Add(points);
            }
        }
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
                ["trainingContourArea"] = Cv2.ContourArea(contour), ["trainingSource"] = fromPort ? "template-input" : "template-path",
                ["trainingOriginMode"] = originMode, ["trainingContour"] = contourPoints, ["trainingContours"] = previewContours.ToArray(),
                ["trainingRegionType"] = roi?.Type.ToString() ?? "FullImage", ["trainingScaleMin"] = scaleMin, ["trainingScaleMax"] = scaleMax,
                ["trainingIgnorePolarity"] = ignorePolarity, ["trainingMinContrast"] = minContrast
            },
            [
                .. previewContours.Select((points, index) => new VisionOverlay($"shape-training-contour-{index}", VisionOverlayType.Polygon, Points: points, Label: "模型轮廓片段", Stroke: "#13c2c2", StrokeWidth: 2)),
                new VisionOverlay("shape-training-origin", VisionOverlayType.Point, X: originX + cropRect.X, Y: originY + cropRect.Y, Label: "原点", Stroke: "#fa541c", StrokeWidth: 3)
            ]);
        return result;
        }
        finally { fileTemplate?.Dispose(); }
    }

    private static string ShapeTrainingConfig(NodeDefinition node)
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

    private static bool IsCompatibleLegacyShapeConfig(NodeDefinition node, string savedConfig) =>
        string.Equals(savedConfig, ShapeTrainingConfigV1(node), StringComparison.Ordinal)
        && node.GetDouble("scaleTolerancePercent", 0) == 0
        && !node.GetBool("ignorePolarity", false)
        && node.GetInt("minContrast", 5) == 5;

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

    private static string Key(Mat template, bool shape, double range, double step, double x, double y, int levels = 0,
        double scaleMin = 1, double scaleMax = 1, double scaleStep = 0, bool ignorePolarity = false, int minContrast = 5)
    {
        using var packed = template.IsContinuous() ? null : template.Clone(); var buffer = packed ?? template;
        var bytes = new byte[checked((int)(buffer.Total() * buffer.ElemSize()))]; Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
        return FormattableString.Invariant($"{shape}:{buffer.Width}:{buffer.Height}:{buffer.Type()}:{range:R}:{step:R}:{x:R}:{y:R}:{levels}:{scaleMin:R}:{scaleMax:R}:{scaleStep:R}:{ignorePolarity}:{minContrast}:{Convert.ToHexString(SHA256.HashData(bytes))}");
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
