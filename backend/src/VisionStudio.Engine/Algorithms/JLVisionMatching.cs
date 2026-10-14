using JLVisionLib;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
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
        try
        {
            var fromPort = context.TryGetValue("template", out _);
            Mat templateBuffer;
            if (fromPort) templateBuffer = context.RequireImage("template").Mat;
            else
            {
                var path = node.GetString("templatePath", string.Empty).Trim();
                if (path.Length == 0) throw new InvalidOperationException("Matching requires a connected template or templatePath.");
                fileImage = Cv2.ImRead(Path.GetFullPath(path), ImreadModes.Grayscale); templateBuffer = fileImage;
                if (fileImage.Empty()) throw new InvalidOperationException($"Template image '{Path.GetFullPath(path)}' could not be loaded.");
            }
            if (templateBuffer.Empty()) throw new InvalidOperationException("Matching requires a non-empty template image.");
            if (templateBuffer.Width > source.Width || templateBuffer.Height > source.Height) throw new InvalidOperationException("Template is larger than the scene image.");
            using var templateColor = JLVisionImages.FromMat(templateBuffer);
            using var templateChannels = templateColor.CountChannels();
            using var template = templateChannels.I == 1 ? templateColor.CopyImage() : templateColor.Rgb1ToGray();
            var range = Math.Clamp(node.GetDouble("angleRangeDeg", shape ? 180 : 0), 0, 180) * Math.PI / 180;
            var step = Math.Clamp(node.GetDouble("angleStepDeg", shape ? 1 : 5), .1, 30) * Math.PI / 180;
            var minScore = Math.Clamp(node.GetDouble("minScore", shape ? .6 : .5), 0, 1);
            var method = node.GetString("method", "ccoeff-normed").Trim().ToLowerInvariant();
            if (!shape && method is not ("ccoeff-normed" or "sqdiff-normed")) throw new InvalidOperationException($"Unsupported template matching method '{method}'.");
            var squaredDifference = !shape && method == "sqdiff-normed";
            var maximum = shape ? Math.Clamp(node.GetInt("maxTargets", 5), 1, 100) : squaredDifference ? 100 : 1;
            var originX = (templateBuffer.Width - 1) / 2d; var originY = (templateBuffer.Height - 1) / 2d;
            var minArea = node.GetDouble("minArea", 200);
            var templateSource = fromPort ? "input-port" : "file";
            var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(false), ["score"] = VisionValue.Double(0) };
            var summary = new Dictionary<string, object?> { ["found"] = false, ["bestScore"] = 0d, ["score"] = 0d, ["templateSource"] = templateSource, ["algorithmBackend"] = "JLVision", ["angleRangeDeg"] = range * 180 / Math.PI, ["angleStepDeg"] = step * 180 / Math.PI };
            var overlays = new List<VisionOverlay>();
            var detections = new List<VisionDetection>();
            var templateBox = new Rect(0, 0, templateBuffer.Width, templateBuffer.Height);
            JlTuple rows, cols, angles, scores;
            if (shape)
            {
                using var binary = ContourNodesHelper.Binarize(templateBuffer, node.GetString("binarize", "auto"), node.GetDouble("threshold", 128), node.GetBool("invert", false), out _, out _);
                JLVisionOperators.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                var bestContour = contours.OrderByDescending(c => JLVisionOperators.ContourArea(c)).FirstOrDefault();
                if (bestContour is null || JLVisionOperators.ContourArea(bestContour) <= 0)
                { summary["reason"] = "template-has-no-contour"; outputs["image"] = VisionValue.Image(source.CloneOwned()); return new(outputs, summary, overlays); }
                var moments = JLVisionOperators.Moments(bestContour);
                originX = moments.M10 / moments.M00; originY = moments.M01 / moments.M00;
                templateBox = JLVisionOperators.BoundingRect(bestContour);
                using var lease = JLVisionModelCache.Acquire(Key(templateBuffer, true, range, step, originX, originY), () =>
                {
                    var created = new JlShapeModel();
                    try
                    {
                        using var levels = new JlTuple("auto"); using var delta = new JlTuple(step); using var optimize = new JlTuple("auto"); using var contrast = new JlTuple("auto"); using var minimum = new JlTuple(5);
                        created.CreateShapeModel(template, levels, -range, 2 * range, delta, optimize, "use_polarity", contrast, minimum);
                        created.SetShapeModelOrigin(originY - (templateBuffer.Height - 1) / 2d, originX - (templateBuffer.Width - 1) / 2d);
                        return created;
                    }
                    catch { created.Dispose(); throw; }
                });
                var model = (JlShapeModel)lease.Model; summary["modelCacheHit"] = lease.Hit;
                gray.FindShapeModel(model, -range, 2 * range, minScore, maximum, .5, "least_squares", 0, .8, out rows, out cols, out angles, out scores);
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
            }
            using (rows) using (cols) using (angles) using (scores)
            {
                var candidates = Enumerable.Range(0, scores.Length).Select(i => (Index: i, Score: squaredDifference ? Ssd(gray, template, cols[i].D, rows[i].D, angles[i].D, originX, originY) : scores[i].D)).OrderByDescending(x => x.Score).ToArray();
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var i = candidate.Index; var x = cols[i].D; var y = rows[i].D; var radians = angles[i].D; var score = candidate.Score;
                    if (score < minScore || !shape && detections.Count > 0) continue;
                    var cos = Math.Cos(radians); var sin = Math.Sin(radians);
                    var corners = new[] { (templateBox.Left, templateBox.Top), (templateBox.Right - 1, templateBox.Top), (templateBox.Right - 1, templateBox.Bottom - 1), (templateBox.Left, templateBox.Bottom - 1) }
                        .Select(p => new VisionPoint2D(x + cos * (p.Item1 - originX) + sin * (p.Item2 - originY), y - sin * (p.Item1 - originX) + cos * (p.Item2 - originY))).ToArray();
                    var left = corners.Min(p => p.X); var top = corners.Min(p => p.Y); var width = corners.Max(p => p.X) - left + 1; var height = corners.Max(p => p.Y) - top + 1;
                    if (shape && templateBox.Width * templateBox.Height < minArea) continue;
                    var distance = Math.Max(0, node.GetDouble("minCenterDistance", 20));
                    if (shape && detections.Any(d => Math.Sqrt(Math.Pow(d.Circle.X - x, 2) + Math.Pow(d.Circle.Y - y, 2)) < distance)) continue;
                    if (shape && detections.Any(d => Overlap(d.BoundingBox, new(left, top, width, height)) > .3)) continue;
                    var degrees = VisionTransform2D.NormalizeAngleDeg(radians * 180 / Math.PI);
                    if (!outputs["found"].Require<bool>("found"))
                    {
                        outputs["found"] = VisionValue.Boolean(true); outputs["score"] = VisionValue.Double(score);
                        outputs["x"] = VisionValue.Double(shape ? x : left); outputs["y"] = VisionValue.Double(shape ? y : top);
                        outputs["angle"] = VisionValue.Double(degrees); outputs["center"] = VisionValue.Point(shape ? new(x, y) : new(left + width / 2, top + height / 2));
                        outputs["rectangle"] = VisionValue.Rectangle(new(shape ? x : left, shape ? y : top, width, height, shape ? -degrees : degrees));
                        if (shape) outputs["pose"] = VisionValue.Pose(new(x, y, degrees));
                        summary["found"] = true; summary["bestScore"] = score; summary["score"] = score; summary["matchedAngle"] = degrees; summary["angle"] = degrees;
                    }
                    var box = new VisionRectangle2D(left, top, width, height);
                    detections.Add(new(detections.Count, left, top, width, height, templateBox.Width * templateBox.Height, 2 * (width + height), 0, new(x, y, Math.Sqrt(width * width + height * height) / 2), box, score));
                    overlays.Add(new($"match-{i}", VisionOverlayType.Polygon, Points: corners.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray(), Label: $"s={score:0.###} a={degrees:0.#}", Stroke: "#52c41a", StrokeWidth: 2));
                }
            }
            token.ThrowIfCancellationRequested();
            if (shape) { outputs["image"] = VisionValue.Image(source.CloneOwned()); outputs["count"] = VisionValue.Integer(detections.Count); outputs["detections"] = VisionValue.Detections(detections); }
            summary["accepted"] = detections.Count;
            return new(outputs, summary, overlays);
        }
        finally { fileImage?.Dispose(); }
    }

    private static double Overlap(VisionRectangle2D a, VisionRectangle2D b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / Math.Max(1e-9, a.Width * a.Height + b.Width * b.Height - intersection);
    }

    private static string Key(Mat template, bool shape, double range, double step, double x, double y, int levels = 0)
    {
        using var packed = template.IsContinuous() ? null : template.Clone(); var buffer = packed ?? template;
        var bytes = new byte[checked((int)(buffer.Total() * buffer.ElemSize()))]; Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
        return FormattableString.Invariant($"{shape}:{buffer.Width}:{buffer.Height}:{buffer.Type()}:{range:R}:{step:R}:{x:R}:{y:R}:{levels}:{Convert.ToHexString(SHA256.HashData(bytes))}");
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
