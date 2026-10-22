using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Managed chamfer shape matching for match.shape engine=managedChamfer|chamfer (explicit opt-in; never auto).
/// Model: Canny contour points of the template (templateRoi rectangle crop if
/// given) with Sobel gradient normals for polarity. Scene: Canny edges → chamfer
/// distance transform + optional gradient maps → coarse-to-fine search.
/// Polarity: ignorePolarity=false requires scene gradient to agree with the
/// rotated model normal (Sobel direction); ignorePolarity=true matches either side.
/// Clutter: when clutterRoi is set, unexplained scene-edge fraction in that ROI
/// becomes clutterScore; maxClutterScore rejects; scoreUsingClutter multiplies.
/// Depth vs PatMax/HALCON: managed chamfer now includes pyramid coarse-to-fine,
/// continuous parabolic angle/scale refine, XLD-ish contour snap along normals,
/// Sobel polarity and unexplained-edge clutter. Still short of PatMax feature
/// vectors, trained clutter models, and full HALCON XLD least-squares deformation.
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
            using var templateGx = new Mat(); using var templateGy = new Mat();
            Cv2.Sobel(templateGray, templateGx, MatType.CV_32F, 1, 0, 3);
            Cv2.Sobel(templateGray, templateGy, MatType.CV_32F, 0, 1, 3);
            var rawPoints = OrientedEdgePoints(templateEdges, templateGx, templateGy, originX, originY);
            var maxPoints = Math.Clamp(node.GetInt("chamferMaxModelPoints", 240), 16, 2000);
            var model = ChamferShapeMatching.BuildModel(rawPoints, maxPoints);
            if (model.Length < 8) throw new InvalidOperationException($"Managed chamfer model needs at least 8 template edge points, found {model.Length}. Lower the Canny thresholds or use a template with stronger contours.");
            var orientedCount = model.Count(p => p.HasNormal);
            var modelMetrics = ChamferShapeMatching.ModelMetrics(model);

            using var sceneEdges = new Mat(); JLVisionOperators.Canny(sceneGray, sceneEdges, low, high);
            if (node.GetRoi("searchRoi") is { Type: VisionRoiType.Rectangle } searchRoi)
            {
                using var mask = new Mat(sceneEdges.Size(), MatType.CV_8UC1, Scalar.All(0));
                Cv2.Rectangle(mask, new Rect((int)(searchRoi.X ?? 0), (int)(searchRoi.Y ?? 0), (int)(searchRoi.Width ?? 0), (int)(searchRoi.Height ?? 0)), Scalar.All(255), -1);
                using var masked = new Mat(); sceneEdges.CopyTo(masked, mask); masked.CopyTo(sceneEdges);
            }

            var ignorePolarity = node.GetBool("ignorePolarity", false);
            var minContrast = Math.Clamp(node.GetDouble("minContrast", 5), 0, 255);
            var polarityCosine = Math.Clamp(node.GetDouble("chamferPolarityCosine", 0.0), -1, 1);
            var clutterRoi = node.GetRoi("clutterRoi");
            var clutterEnabled = clutterRoi is { Type: VisionRoiType.Rectangle };
            var clutterContrast = Math.Clamp(node.GetDouble("clutterContrast", minContrast), 1, 255);
            if (clutterEnabled && clutterContrast < minContrast)
                throw new InvalidOperationException("Clutter Contrast must be at least the Minimum Feature Contrast used for polarity gradients.");
            var maxClutterScore = Math.Clamp(node.GetDouble("maxClutterScore", .2), 0, 1);
            var scoreUsingClutter = node.GetBool("scoreUsingClutter", false);
            if (scoreUsingClutter && !clutterEnabled)
                throw new InvalidOperationException("Score Using Clutter requires a Clutter ROI for the managed chamfer engine.");

            float[,]? gradX = null; float[,]? gradY = null;
            if (!ignorePolarity)
            {
                using var sceneGx = new Mat(); using var sceneGy = new Mat();
                Cv2.Sobel(sceneGray, sceneGx, MatType.CV_32F, 1, 0, 3);
                Cv2.Sobel(sceneGray, sceneGy, MatType.CV_32F, 0, 1, 3);
                gradX = ToFloatGrid(sceneGx); gradY = ToFloatGrid(sceneGy);
            }

            var edgeMask = EdgeMask(sceneEdges);
            var dist = ChamferShapeMatching.DistanceTransform(edgeMask);
            var angleMin = Math.Clamp(node.GetDouble("searchAngleStartDeg", -180), -180, 180);
            var angleMax = Math.Clamp(node.GetDouble("searchAngleEndDeg", 180), -180, 180);
            if (angleMin > angleMax) throw new InvalidOperationException("searchAngleStartDeg must be <= searchAngleEndDeg for managed chamfer matching.");

            var clutterRect = clutterEnabled
                ? new Rect((int)(clutterRoi!.X ?? 0), (int)(clutterRoi.Y ?? 0), (int)(clutterRoi.Width ?? 0), (int)(clutterRoi.Height ?? 0))
                : default;
            var requestedPyramid = node.GetInt("pyramidLevels", 0);
            var pyramidLevels = Math.Clamp(requestedPyramid <= 0 ? 1 : requestedPyramid, 1, 4);
            // Extra pyramid levels only refine seeds (Search never coarsens CoarseStride).
            var continuousRefine = node.GetBool("chamferContinuousRefine", true);
            var xldSnap = node.GetBool("chamferXldSnap", true);
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
                MaxModelPoints: maxPoints,
                IgnorePolarity: ignorePolarity,
                PolarityCosine: polarityCosine,
                MinGradient: ignorePolarity ? 0 : minContrast,
                ClutterEnabled: clutterEnabled,
                MaxClutterScore: maxClutterScore,
                ScoreUsingClutter: scoreUsingClutter,
                ClutterTolerancePx: Math.Clamp(node.GetDouble("chamferTolerancePx", 2.5), 0.5, 10) * 1.5,
                ClutterRoiX: clutterRect.X, ClutterRoiY: clutterRect.Y,
                ClutterRoiWidth: clutterRect.Width, ClutterRoiHeight: clutterRect.Height,
                PyramidLevels: pyramidLevels,
                ContinuousPoseRefine: continuousRefine,
                XldContourSnap: xldSnap);

            var hits = ChamferShapeMatching.Search(dist, model, options, edgeMask, gradX, gradY);

            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(source.CloneOwned()),
                ["found"] = VisionValue.Boolean(hits.Count > 0),
                ["score"] = VisionValue.Double(hits.Count > 0 ? hits[0].Score : 0),
                ["scale"] = VisionValue.Double(hits.Count > 0 ? hits[0].Scale : 1),
                ["scaleX"] = VisionValue.Double(hits.Count > 0 ? hits[0].Scale : 1),
                ["scaleY"] = VisionValue.Double(hits.Count > 0 ? hits[0].Scale : 1),
                ["count"] = VisionValue.Integer(hits.Count),
                ["detections"] = VisionValue.Detections(hits.Select((hit, i) => Detection(i, hit, templateGray.Width, templateGray.Height)).ToArray()),
                ["modelPointCount"] = VisionValue.Integer(model.Length),
                ["orientedModelPointCount"] = VisionValue.Integer(orientedCount),
                ["polarityMode"] = VisionValue.String(ignorePolarity ? "ignore_polarity" : "use_polarity"),
                ["pyramidLevels"] = VisionValue.Integer(pyramidLevels),
                ["angleRefineDeltaDeg"] = VisionValue.Double(hits.Count > 0 ? hits[0].AngleRefineDeltaDeg : 0),
                ["scaleRefineDelta"] = VisionValue.Double(hits.Count > 0 ? hits[0].ScaleRefineDelta : 0),
                ["xldSnapDeltaPx"] = VisionValue.Double(hits.Count > 0 ? hits[0].XldSnapDeltaPx : 0),
                ["modelMeanRadiusPx"] = VisionValue.Double(modelMetrics.MeanRadiusPx),
                ["modelMeanNormalMagnitude"] = VisionValue.Double(modelMetrics.MeanNormalMagnitude)
            };
            if (clutterEnabled)
            {
                outputs["clutterScore"] = VisionValue.Double(hits.Count > 0 ? hits[0].ClutterScore : 0);
                if (scoreUsingClutter) outputs["nativeScore"] = VisionValue.Double(hits.Count > 0 ? hits[0].NativeScore : 0);
            }
            if (hits.Count > 0)
            {
                var b = hits[0];
                outputs["x"] = VisionValue.Double(b.X); outputs["y"] = VisionValue.Double(b.Y);
                outputs["angle"] = VisionValue.Double(b.AngleDeg);
                outputs["center"] = VisionValue.Point(new(b.X, b.Y));
                outputs["pose"] = VisionValue.Pose(new(b.X, b.Y, b.AngleDeg));
                var det = Detection(0, b, templateGray.Width, templateGray.Height);
                outputs["rectangle"] = VisionValue.Rectangle(det.BoundingBox);
                outputs["polarityMatchFraction"] = VisionValue.Double(b.PolarityMatchFraction);
                outputs["coverageScore"] = VisionValue.Double(b.CoverageScore);
                outputs["distanceScore"] = VisionValue.Double(b.DistanceScore);
            }
            var overlays = new List<VisionOverlay>();
            foreach (var (hit, i) in hits.Select((h, i) => (h, i)))
            {
                var corners = Corners(hit, templateGray.Width, templateGray.Height, originX, originY);
                overlays.Add(new($"chamfer-match-{i}", VisionOverlayType.Polygon, Points: corners.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray(),
                    Label: $"chamfer s={hit.Score:0.###} cov={hit.Coverage:0.##} pol={hit.PolarityMatchFraction:0.##} clut={hit.ClutterScore:0.##}", Stroke: "#52c41a", StrokeWidth: 2));
                overlays.Add(new($"chamfer-match-{i}-center", VisionOverlayType.Point, X: hit.X, Y: hit.Y, Stroke: "#52c41a", StrokeWidth: 3));
            }
            if (clutterEnabled)
                overlays.Add(new("chamfer-clutter-roi", VisionOverlayType.Rectangle, X: clutterRect.X, Y: clutterRect.Y, Width: clutterRect.Width, Height: clutterRect.Height, Stroke: "#fa541c", StrokeWidth: 1));

            var summary = new Dictionary<string, object?>
            {
                ["found"] = hits.Count > 0, ["accepted"] = hits.Count, ["algorithmBackend"] = "managed",
                ["score"] = hits.Count > 0 ? hits[0].Score : 0d, ["bestScore"] = hits.Count > 0 ? hits[0].Score : 0d,
                ["modelPoints"] = model.Length, ["orientedModelPoints"] = orientedCount,
                ["templateEdgePoints"] = rawPoints.Count,
                ["polarityMode"] = ignorePolarity ? "ignore_polarity" : "use_polarity",
                ["ignorePolarity"] = ignorePolarity,
                ["polarityMatchFraction"] = hits.Count > 0 ? hits[0].PolarityMatchFraction : (double?)null,
                ["coverage"] = hits.Count > 0 ? hits[0].Coverage : 0d,
                ["coverageScore"] = hits.Count > 0 ? hits[0].CoverageScore : (double?)null,
                ["distanceScore"] = hits.Count > 0 ? hits[0].DistanceScore : (double?)null,
                ["meanEdgeDistancePx"] = hits.Count > 0 ? hits[0].MeanDistancePx : (double?)null,
                ["clutterEnabled"] = clutterEnabled,
                ["clutterScore"] = clutterEnabled && hits.Count > 0 ? hits[0].ClutterScore : (double?)null,
                ["scoreUsingClutter"] = scoreUsingClutter,
                ["maxClutterScore"] = clutterEnabled ? maxClutterScore : (double?)null,
                ["matchedAngle"] = hits.Count > 0 ? hits[0].AngleDeg : (double?)null,
                ["matchedScale"] = hits.Count > 0 ? hits[0].Scale : (double?)null,
                ["pyramidLevels"] = pyramidLevels,
                ["continuousPoseRefine"] = continuousRefine,
                ["xldContourSnap"] = xldSnap,
                ["angleRefineDeltaDeg"] = hits.Count > 0 ? hits[0].AngleRefineDeltaDeg : (double?)null,
                ["scaleRefineDelta"] = hits.Count > 0 ? hits[0].ScaleRefineDelta : (double?)null,
                ["xldSnapDeltaPx"] = hits.Count > 0 ? hits[0].XldSnapDeltaPx : (double?)null,
                ["modelMeanRadiusPx"] = modelMetrics.MeanRadiusPx,
                ["modelMeanNormalMagnitude"] = modelMetrics.MeanNormalMagnitude,
                ["subpixelMethod"] = "parabolic_xy+continuous_angle_scale+xld_normal_snap",
                ["limits"] = "pyramid+continuous angle/scale refine+XLD-ish normal snap; Sobel polarity (not PatMax features); unexplained-edge clutter (not native clutter model); no full HALCON XLD LS deformation"
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
        return new(index, left, top, w, h, w * h, 2 * (w + h), 0, new(hit.X, hit.Y, Math.Sqrt(w * w + h * h) / 2), box, hit.Score);
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

    private static List<ChamferShapeMatching.ModelPoint> OrientedEdgePoints(Mat edges, Mat gx, Mat gy, double originX, double originY)
    {
        edges.GetArray(out byte[] data);
        gx.GetArray(out float[] gxData); gy.GetArray(out float[] gyData);
        var points = new List<ChamferShapeMatching.ModelPoint>();
        var height = edges.Height; var width = edges.Width;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (data[y * width + x] == 0) continue;
            var fx = gxData[y * width + x]; var fy = gyData[y * width + x];
            var mag = Math.Sqrt(fx * fx + fy * fy);
            var nx = mag > 1e-3 ? fx / mag : 0d;
            var ny = mag > 1e-3 ? fy / mag : 0d;
            points.Add(new(x - originX, y - originY, nx, ny));
        }
        return points;
    }

    private static float[,] ToFloatGrid(Mat mat)
    {
        mat.GetArray(out float[] data);
        var height = mat.Height; var width = mat.Width;
        var grid = new float[height, width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            grid[y, x] = data[y * width + x];
        return grid;
    }

    private static bool[,] EdgeMask(Mat edges)
    {
        edges.GetArray(out byte[] data); var height = edges.Height; var width = edges.Width;
        var mask = new bool[height, width];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) mask[y, x] = data[y * width + x] > 0;
        return mask;
    }
}