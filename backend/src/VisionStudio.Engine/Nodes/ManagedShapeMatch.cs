using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Managed chamfer shape matching for match.shape engine=managedChamfer|auto.
/// Model: Canny contour points of the template (templateRoi rectangle crop if
/// given), origin at the crop centre. Scene: Canny edges -> chamfer distance
/// transform -> coarse-to-fine angle/scale/translation search (see
/// ChamferShapeMatching). Outputs mirror the native match.shape outputs.
/// Limits (honest): discrete angle/scale sampling, no gradient-direction or
/// polarity term, no XLD least-squares refinement, no deformation/clutter
/// model; score is edge coverage/distance, not PatMax/HALCON shape score.
/// </summary>
internal static class ManagedShapeMatch
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty()) throw new InvalidOperationException("Matching requires a non-empty scene image.");
        Mat? fileImage = null;
        try
        {
            Mat templateMat;
            if (context.TryGetValue("template", out _)) templateMat = context.RequireImage("template").Mat;
            else
            {
                var path = node.GetString("templatePath", string.Empty).Trim();
                if (path.Length == 0) throw new InvalidOperationException("Managed chamfer matching requires a connected template or templatePath (trained native models are not used by this engine).");
                fileImage = Cv2.ImRead(Path.GetFullPath(path), ImreadModes.Grayscale);
                if (fileImage.Empty()) throw new InvalidOperationException($"Template image '{Path.GetFullPath(path)}' could not be loaded.");
                templateMat = fileImage;
            }
            var low = Math.Clamp(node.GetDouble("chamferCannyLow", 50), 0, 255);
            var high = Math.Clamp(node.GetDouble("chamferCannyHigh", 150), 0, 255);
            if (low > high) throw new InvalidOperationException("chamferCannyLow must be <= chamferCannyHigh.");
            using var sceneGray = Gray(source.Mat);
            using var templateGrayFull = Gray(templateMat);
            var crop = new Rect(0, 0, templateGrayFull.Width, templateGrayFull.Height);
            if (node.GetRoi("templateRoi") is { Type: VisionRoiType.Rectangle } roi)
            {
                var r = new Rect((int)(roi.X ?? 0), (int)(roi.Y ?? 0), (int)(roi.Width ?? 0), (int)(roi.Height ?? 0)) & crop;
                if (r.Width <= 0 || r.Height <= 0) throw new InvalidOperationException("templateRoi does not overlap the template image.");
                crop = r;
            }
            using var templateGray = new Mat(templateGrayFull, crop);
            using var templateEdges = new Mat(); JLVisionOperators.Canny(templateGray, templateEdges, low, high);
            var originX = (templateGray.Width - 1) / 2d; var originY = (templateGray.Height - 1) / 2d;
            var rawPoints = EdgePoints(templateEdges).Select(p => new ChamferShapeMatching.ModelPoint(p.X - originX, p.Y - originY)).ToList();
            var maxPoints = Math.Clamp(node.GetInt("chamferMaxModelPoints", 240), 16, 2000);
            var model = ChamferShapeMatching.BuildModel(rawPoints, maxPoints);
            if (model.Length < 8) throw new InvalidOperationException($"Managed chamfer model needs at least 8 template edge points, found {model.Length}. Lower the Canny thresholds or use a template with stronger contours.");

            using var sceneEdges = new Mat(); JLVisionOperators.Canny(sceneGray, sceneEdges, low, high);
            if (node.GetRoi("searchRoi") is { Type: VisionRoiType.Rectangle } searchRoi)
            {
                using var mask = new Mat(sceneEdges.Size(), MatType.CV_8UC1, Scalar.All(0));
                Cv2.Rectangle(mask, new Rect((int)(searchRoi.X ?? 0), (int)(searchRoi.Y ?? 0), (int)(searchRoi.Width ?? 0), (int)(searchRoi.Height ?? 0)), Scalar.All(255), -1);
                using var masked = new Mat(); sceneEdges.CopyTo(masked, mask); masked.CopyTo(sceneEdges);
            }
            var dist = ChamferShapeMatching.DistanceTransform(EdgeMask(sceneEdges));
            var angleMin = Math.Clamp(node.GetDouble("searchAngleStartDeg", -180), -180, 180);
            var angleMax = Math.Clamp(node.GetDouble("searchAngleEndDeg", 180), -180, 180);
            if (angleMin > angleMax) throw new InvalidOperationException("searchAngleStartDeg must be <= searchAngleEndDeg for managed chamfer matching.");
            var options = new ChamferShapeMatching.Options(
                AngleMinDeg: angleMin, AngleMaxDeg: angleMax,
                AngleStepDeg: Math.Clamp(node.GetDouble("chamferAngleStepDeg", 5), 0.5, 30),
                ScaleMin: Math.Clamp(node.GetDouble("searchScaleMinPercent", 100), 10, 400) / 100,
                ScaleMax: Math.Clamp(node.GetDouble("searchScaleMaxPercent", 100), 10, 400) / 100,
                ScaleStep: Math.Clamp(node.GetDouble("chamferScaleStepPercent", 10), 1, 50) / 100,
                MinScore: Math.Clamp(node.GetDouble("minScore", .6), 0, 1),
                MaxTargets: Math.Clamp(node.GetInt("maxTargets", 5), 1, 100),
                MinCenterDistance: Math.Max(0, node.GetDouble("minCenterDistance", 20)),
                TolerancePx: Math.Clamp(node.GetDouble("chamferTolerancePx", 2.5), 0.5, 10),
                MaxModelPoints: maxPoints);
            var hits = ChamferShapeMatching.Search(dist, model, options);

            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(source.CloneOwned()),
                ["found"] = VisionValue.Boolean(hits.Count > 0),
                ["score"] = VisionValue.Double(hits.Count > 0 ? hits[0].Score : 0),
                ["scale"] = VisionValue.Double(hits.Count > 0 ? hits[0].Scale : 1),
                ["scaleX"] = VisionValue.Double(hits.Count > 0 ? hits[0].Scale : 1),
                ["scaleY"] = VisionValue.Double(hits.Count > 0 ? hits[0].Scale : 1),
                ["count"] = VisionValue.Integer(hits.Count),
                ["detections"] = VisionValue.Detections(hits.Select((hit, i) => Detection(i, hit, templateGray.Width, templateGray.Height)).ToArray())
            };
            var overlays = new List<VisionOverlay>();
            if (hits.Count > 0)
            {
                var b = hits[0];
                outputs["x"] = VisionValue.Double(b.X); outputs["y"] = VisionValue.Double(b.Y);
                outputs["angle"] = VisionValue.Double(b.AngleDeg);
                outputs["center"] = VisionValue.Point(new(b.X, b.Y));
                outputs["pose"] = VisionValue.Pose(new(b.X, b.Y, b.AngleDeg));
                var det = Detection(0, b, templateGray.Width, templateGray.Height);
                outputs["rectangle"] = VisionValue.Rectangle(det.BoundingBox);
            }
            foreach (var (hit, i) in hits.Select((h, i) => (h, i)))
            {
                var corners = Corners(hit, templateGray.Width, templateGray.Height, originX, originY);
                overlays.Add(new($"chamfer-match-{i}", VisionOverlayType.Polygon, Points: corners.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray(),
                    Label: $"chamfer s={hit.Score:0.###} cov={hit.Coverage:0.##} a={hit.AngleDeg:0.#} x{hit.Scale:0.##}", Stroke: "#52c41a", StrokeWidth: 2));
                overlays.Add(new($"chamfer-match-{i}-center", VisionOverlayType.Point, X: hit.X, Y: hit.Y, Stroke: "#52c41a", StrokeWidth: 3));
            }
            var summary = new Dictionary<string, object?>
            {
                ["found"] = hits.Count > 0, ["accepted"] = hits.Count, ["algorithmBackend"] = "ManagedChamfer",
                ["score"] = hits.Count > 0 ? hits[0].Score : 0d, ["bestScore"] = hits.Count > 0 ? hits[0].Score : 0d,
                ["modelPoints"] = model.Length, ["templateEdgePoints"] = rawPoints.Count,
                ["coverage"] = hits.Count > 0 ? hits[0].Coverage : 0d,
                ["meanEdgeDistancePx"] = hits.Count > 0 ? hits[0].MeanDistancePx : (double?)null,
                ["matchedAngle"] = hits.Count > 0 ? hits[0].AngleDeg : (double?)null,
                ["matchedScale"] = hits.Count > 0 ? hits[0].Scale : (double?)null,
                ["limits"] = "discrete angle/scale sampling; no polarity/gradient-direction, XLD refinement, deformation or clutter model"
            };
            return new(outputs, summary, overlays);
        }
        finally { fileImage?.Dispose(); }
    }

    private static VisionDetection Detection(int index, ChamferShapeMatching.Hit hit, int tw, int th)
    {
        var corners = Corners(hit, tw, th, (tw - 1) / 2d, (th - 1) / 2d);
        var left = corners.Min(p => p.X); var top = corners.Min(p => p.Y);
        var w = Math.Max(1, corners.Max(p => p.X) - left); var h = Math.Max(1, corners.Max(p => p.Y) - top);
        var box = new VisionRectangle2D(left, top, w, h);
        return new(index, left, top, w, h, w * h, 2 * (w + h), 0, new(hit.X, hit.Y, Math.Sqrt(w*w+h*h) / 2), box, hit.Score);
    }

    private static (double X, double Y)[] Corners(ChamferShapeMatching.Hit hit, int tw, int th, double ox, double oy)
    {
        var rad = hit.AngleDeg * Math.PI / 180; var cos = Math.Cos(rad); var sin = Math.Sin(rad);
        return new[] { (0d, 0d), (tw - 1d, 0d), (tw - 1d, th - 1d), (0d, th - 1d) }
            .Select(p => (hit.X + (cos * (p.Item1 - ox) + sin * (p.Item2 - oy)) * hit.Scale,
                          hit.Y + (-sin * (p.Item1 - ox) + cos * (p.Item2 - oy)) * hit.Scale)).ToArray();
    }

    private static Mat Gray(Mat source)
    {
        var gray = new Mat();
        if (source.Channels() == 1) source.CopyTo(gray); else JLVisionOperators.CvtColor(source, gray, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static List<(int X, int Y)> EdgePoints(Mat edges)
    {
        edges.GetArray(out byte[] data);
        var points = new List<(int, int)>(); var height = edges.Height; var width = edges.Width;
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) if (data[y * width + x] > 0) points.Add((x, y));
        return points;
    }

    private static bool[,] EdgeMask(Mat edges)
    {
        edges.GetArray(out byte[] data); var height = edges.Height; var width = edges.Width;
        var mask = new bool[height, width];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) mask[y, x] = data[y * width + x] > 0;
        return mask;
    }
}
