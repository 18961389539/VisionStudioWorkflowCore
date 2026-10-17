using VisionStudio.Engine.Algorithms;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

internal static class VisionGeometryHelpers
{
    public static Mat ToGray(Mat source)
    {
        if (source.Channels() == 1)
            return source.Clone();

        var gray = new Mat();
        JLVisionOperators.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    public static Mat ApplyRoi(Mat source, VisionRoi? roi)
    {
        if (roi is null)
            return source.Clone();

        using var mask = roi.BuildMask(source.Size());
        var masked = new Mat(source.Rows, source.Cols, source.Type(), Scalar.All(0));
        source.CopyTo(masked, mask);
        return masked;
    }

    public static VisionOverlay OverlayLine(string id, VisionLine2D line, string stroke = "#13c2c2", double width = 2) =>
        new(id, VisionOverlayType.Line, X: line.Start.X, Y: line.Start.Y, X2: line.End.X, Y2: line.End.Y, Stroke: stroke, StrokeWidth: width);

    public static double NormalizeAngleDifference(double a, double b)
    {
        var diff = Math.Abs(a - b) % 180.0;
        return diff > 90.0 ? 180.0 - diff : diff;
    }
}

public sealed class EdgeNode : IVisionNodeExecutor
{
    public string Type => "feature.edge";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        var low = node.GetDouble("lowThreshold", 50);
        var high = node.GetDouble("highThreshold", 150);
        if (!double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high > 255 || low > high)
            throw new InvalidOperationException("Edge thresholds must satisfy 0 <= lowThreshold <= highThreshold <= 255.");
        var blurSize = node.GetInt("blurSize", 0);
        if (blurSize is not (0 or 3 or 5 or 7)) throw new InvalidOperationException("blurSize must be 0, 3, 5, or 7.");
        var subpixel = node.GetBool("subpixel", false);
        var roi = node.GetRoi();

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        using var roiApplied = VisionGeometryHelpers.ApplyRoi(gray, roi);
        using var analysis = new Mat();
        if (blurSize > 0) JLVisionOperators.GaussianBlur(roiApplied, analysis, new Size(blurSize, blurSize), 0);
        else roiApplied.CopyTo(analysis);
        var edges = new Mat();
        JLVisionOperators.Canny(analysis, edges, low, high);
        var edgePixels = JLVisionOperators.CountNonZero(edges);
        // Chain analysis: HALCON edges_sub_pix / VisionPro edge tools expose individual
        // chains, not only a pixel count. Contours over the binary edge image give
        // deterministic chain lengths and bounding boxes for debugging.
        JLVisionOperators.FindContours(edges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
        // Optional XLD-like subpixel chains: refine contour points along the local
        // gradient (parabolic peak interpolation). Points are stride-sampled and capped
        // per chain so JSON stays bounded on dense edge maps.
        byte[]? grayBytes = null;
        if (subpixel) analysis.GetArray(out grayBytes);
        var chains = contours.Select(c =>
        {
            object? points = null;
            if (subpixel && grayBytes is not null && c.Length > 0)
            {
                var stride = Math.Max(1, (int)Math.Ceiling(c.Length / 200d));
                points = c.Where((_, i) => i % stride == 0)
                    .Select(pt => { var refined = SubpixelEdgeRefinement.Refine(grayBytes, analysis.Width, analysis.Height, pt.X, pt.Y); return new { x = Math.Round(refined.X, 3), y = Math.Round(refined.Y, 3) }; })
                    .ToArray();
            }
            return new
            {
                length = c.Length,
                x = c.Length == 0 ? 0 : c.Min(p => p.X), y = c.Length == 0 ? 0 : c.Min(p => p.Y),
                width = c.Length == 0 ? 0 : c.Max(p => p.X) - c.Min(p => p.X) + 1,
                height = c.Length == 0 ? 0 : c.Max(p => p.Y) - c.Min(p => p.Y) + 1,
                points
            };
        }).OrderByDescending(c => c.length).ToArray();
        var longest = chains.Length == 0 ? 0 : chains[0].length;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(edges)),
                ["edgeCount"] = VisionValue.Integer(edgePixels),
                ["found"] = VisionValue.Boolean(edgePixels > 0),
                ["chainCount"] = VisionValue.Integer(chains.Length),
                ["longestChain"] = VisionValue.Integer(longest),
                ["chains"] = VisionValue.String(JsonSerializer.Serialize(chains.Take(100)))
            },
            new Dictionary<string, object?>
            {
                ["lowThreshold"] = low, ["highThreshold"] = high, ["blurSize"] = blurSize,
                ["edgePixels"] = edgePixels, ["chainCount"] = chains.Length, ["longestChain"] = longest, ["subpixel"] = subpixel,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            }));
    }
}

public sealed class LineNode : IVisionNodeExecutor
{
    public string Type => "feature.line";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        var houghThreshold = node.GetInt("houghThreshold", 45);
        var minLength = node.GetDouble("minLength", 60);
        var maxGap = node.GetDouble("maxGap", 12);
        var cannyLow = node.GetDouble("cannyLow", 50);
        var cannyHigh = node.GetDouble("cannyHigh", 150);
        var maxLines = node.GetInt("maxLines", 10);
        var angleTolerance = node.GetDouble("angleToleranceDeg", 5);
        var distanceTolerance = node.GetDouble("distanceTolerance", 6);
        if (houghThreshold < 1) throw new InvalidOperationException("houghThreshold must be at least 1.");
        if (!double.IsFinite(minLength) || minLength < 1) throw new InvalidOperationException("minLength must be finite and at least 1.");
        if (!double.IsFinite(maxGap) || maxGap < 0) throw new InvalidOperationException("maxGap must be finite and non-negative.");
        if (!double.IsFinite(cannyLow) || !double.IsFinite(cannyHigh) || cannyLow < 0 || cannyHigh > 255 || cannyLow > cannyHigh)
            throw new InvalidOperationException("Canny thresholds must satisfy 0 <= cannyLow <= cannyHigh <= 255.");
        if (maxLines is < 1 or > 100) throw new InvalidOperationException("maxLines must be between 1 and 100.");
        if (!double.IsFinite(angleTolerance) || angleTolerance is < 0.5 or > 45) throw new InvalidOperationException("angleToleranceDeg must be between 0.5 and 45.");
        if (!double.IsFinite(distanceTolerance) || distanceTolerance is < 0 or > 1000) throw new InvalidOperationException("distanceTolerance must be between 0 and 1000.");
        var roi = node.GetRoi();

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        using var analysis = VisionGeometryHelpers.ApplyRoi(gray, roi);
        using var edges = new Mat();
        JLVisionOperators.Canny(analysis, edges, cannyLow, cannyHigh);
        var segments = JLVisionOperators.HoughLinesP(edges, 1, Math.PI / 180.0, houghThreshold, minLength, maxGap);
        cancellationToken.ThrowIfCancellationRequested();

        // Cluster collinear Hough segments (HALCON-style line merging), then refit each
        // cluster with robust total least squares over points sampled along its segments.
        var clusters = new List<List<(VisionPoint2D A, VisionPoint2D B)>>();
        var clusterRho = new List<double>();
        foreach (var segment in segments.OrderByDescending(s => Math.Sqrt(Math.Pow(s.P2.X - s.P1.X, 2) + Math.Pow(s.P2.Y - s.P1.Y, 2))))
        {
            var a = new VisionPoint2D(segment.P1.X, segment.P1.Y);
            var b = new VisionPoint2D(segment.P2.X, segment.P2.Y);
            var angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
            if (angle < 0) angle += 180;
            var length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            var nx = -(b.Y - a.Y) / Math.Max(length, 1e-9); var ny = (b.X - a.X) / Math.Max(length, 1e-9);
            var rho = nx * a.X + ny * a.Y;
            var assigned = -1;
            for (var i = 0; i < clusters.Count; i++)
            {
                var first = clusters[i][0];
                var firstAngle = Math.Atan2(first.B.Y - first.A.Y, first.B.X - first.A.X) * 180 / Math.PI;
                if (firstAngle < 0) firstAngle += 180;
                var diff = Math.Abs(angle - firstAngle); if (diff > 90) diff = 180 - diff;
                if (diff <= angleTolerance && Math.Abs(rho - clusterRho[i]) <= distanceTolerance) { assigned = i; break; }
            }
            if (assigned < 0) { clusters.Add([(a, b)]); clusterRho.Add(rho); }
            else clusters[assigned].Add((a, b));
        }

        var fits = new List<(GeometryFitting.LineFit Fit, int Segments)>();
        foreach (var cluster in clusters)
        {
            var points = new List<VisionPoint2D>();
            foreach (var (a, b) in cluster)
            {
                var steps = Math.Max(1, (int)Math.Ceiling(Distance(new Point((int)a.X, (int)a.Y), new Point((int)b.X, (int)b.Y)) / 4));
                for (var s = 0; s <= steps; s++) points.Add(new(a.X + (b.X - a.X) * s / steps, a.Y + (b.Y - a.Y) * s / steps));
            }
            if (points.Count < 2) continue;
            try { var fit = GeometryFitting.FitLine(points); if (fit.Line.Length >= minLength * 0.5) fits.Add((fit, cluster.Count)); }
            catch (InvalidOperationException) { /* degenerate cluster: no usable line */ }
        }
        var ordered = fits.OrderByDescending(f => f.Fit.Line.Length).ThenBy(f => f.Fit.RmsError).Take(maxLines).ToArray();
        var outputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(sourceImage),
            ["found"] = VisionValue.Boolean(ordered.Length > 0),
            ["count"] = VisionValue.Integer(ordered.Length),
            ["lines"] = VisionValue.String(JsonSerializer.Serialize(ordered.Select(f => new
            {
                x1 = f.Fit.Line.Start.X, y1 = f.Fit.Line.Start.Y, x2 = f.Fit.Line.End.X, y2 = f.Fit.Line.End.Y,
                length = f.Fit.Line.Length, angleDeg = f.Fit.AngleDeg, rmsError = f.Fit.RmsError,
                maxError = f.Fit.MaxError, inlierPoints = f.Fit.InlierCount, segments = f.Segments,
                confidence = LineConfidence(f.Fit)
            })))
        };
        var overlays = new List<VisionOverlay>();
        if (ordered.Length > 0)
        {
            var best = ordered[0].Fit;
            outputs["line"] = VisionValue.Line(best.Line);
            outputs["length"] = VisionValue.Double(best.Line.Length);
            outputs["angle"] = VisionValue.Double(best.AngleDeg);
            outputs["rmsError"] = VisionValue.Double(best.RmsError);
            outputs["confidence"] = VisionValue.Double(LineConfidence(best));
            for (var i = 0; i < ordered.Length; i++)
            {
                overlays.Add(VisionGeometryHelpers.OverlayLine($"line-result-{i}", ordered[i].Fit.Line, i == 0 ? "#13c2c2" : "#597ef7", i == 0 ? 2.5 : 1.5));
            }
            overlays.Add(new VisionOverlay("line-label", VisionOverlayType.Text, Label: $"L={best.Line.Length:0.0} A={best.AngleDeg:0.0}° RMS={best.RmsError:0.00}", X: best.Line.Start.X + 6, Y: best.Line.Start.Y - 8, Stroke: "#36cfc9", StrokeWidth: 1));
        }
        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["found"] = ordered.Length > 0, ["count"] = ordered.Length,
                ["length"] = ordered.Length > 0 ? Math.Round(ordered[0].Fit.Line.Length, 3) : null,
                ["angleDeg"] = ordered.Length > 0 ? Math.Round(ordered[0].Fit.AngleDeg, 3) : null,
                ["rmsError"] = ordered.Length > 0 ? Math.Round(ordered[0].Fit.RmsError, 4) : null,
                ["candidates"] = segments.Length, ["clusters"] = clusters.Count,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            }, overlays));
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    /// <summary>Fit quality in [0,1]: inlier support discounted by RMS error.</summary>
    internal static double LineConfidence(GeometryFitting.LineFit fit)
        => Math.Clamp((double)fit.InlierCount / Math.Max(1, fit.PointCount) * Math.Exp(-fit.RmsError / 3d), 0, 1);
}

public sealed class CircleNode : IVisionNodeExecutor
{
    public string Type => "feature.circle";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        var minRadius = node.GetInt("minRadius", 15);
        var maxRadius = node.GetInt("maxRadius", 180);
        var minDist = node.GetDouble("minDist", 40);
        var param1 = node.GetDouble("edgeThreshold", 100);
        var param2 = node.GetDouble("centerThreshold", 24);
        var blurSize = node.GetInt("blurSize", 9);
        var refineBand = node.GetDouble("refineBand", 3);
        var maxCircles = node.GetInt("maxCircles", 10);
        if (minRadius < 1 || maxRadius < minRadius) throw new InvalidOperationException("Circle radii must satisfy 1 <= minRadius <= maxRadius.");
        if (!double.IsFinite(minDist) || minDist < 1) throw new InvalidOperationException("minDist must be finite and at least 1.");
        if (blurSize is not (3 or 5 or 7 or 9 or 11 or 15)) throw new InvalidOperationException("blurSize must be one of 3, 5, 7, 9, 11, 15.");
        if (!double.IsFinite(refineBand) || refineBand is < 0.5 or > 25) throw new InvalidOperationException("refineBand must be between 0.5 and 25.");
        if (maxCircles is < 1 or > 100) throw new InvalidOperationException("maxCircles must be between 1 and 100.");
        var roi = node.GetRoi();

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        using var analysis = VisionGeometryHelpers.ApplyRoi(gray, roi);
        using var blurred = new Mat();
        JLVisionOperators.GaussianBlur(analysis, blurred, new Size(blurSize, blurSize), 2);
        var circles = JLVisionOperators.HoughCircles(blurred, HoughModes.Gradient, 1, minDist, param1, param2, minRadius, maxRadius);
        cancellationToken.ThrowIfCancellationRequested();

        // Refine each Hough candidate against real Canny edge points with a geometric
        // circle fit (the catalog previously claimed subpixel contour fitting while only
        // returning raw Hough output). Ranking uses inlier support, not largest radius.
        using var edges = new Mat();
        JLVisionOperators.Canny(analysis, edges, 50, 150);
        edges.GetArray(out byte[] edgePixels);
        var refined = new List<(VisionCircle Circle, double? Rms, int Inliers, double CoverageDeg, double Confidence)>();
        foreach (var candidate in circles.Take(25))
        {
            var points = new List<VisionPoint2D>();
            var cx = candidate.Center.X; var cy = candidate.Center.Y;
            var rows = edges.Rows; var cols = edges.Cols;
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
            {
                if (edgePixels[y * cols + x] == 0) continue;
                var distance = Math.Sqrt(Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2));
                if (Math.Abs(distance - candidate.Radius) <= refineBand) points.Add(new(x, y));
            }
            if (points.Count >= 12)
            {
                try
                {
                    var fit = GeometryFitting.FitCircle(points);
                    if (fit.Circle.Radius >= minRadius * 0.5 && fit.Circle.Radius <= maxRadius * 1.5 && fit.InlierCount >= 8)
                    {
                        var coverage = SubpixelEdgeRefinement.AngularCoverageDeg(points, fit.Circle.X, fit.Circle.Y);
                        var confidence = Math.Clamp((double)fit.InlierCount / Math.Max(1, points.Count) * Math.Exp(-fit.RmsError / 3d), 0, 1);
                        refined.Add((fit.Circle, fit.RmsError, fit.InlierCount, coverage, confidence));
                        continue;
                    }
                }
                catch (InvalidOperationException) { /* fall back to the Hough candidate below */ }
            }
            refined.Add((new VisionCircle(cx, cy, candidate.Radius), null, 0, 0, 0));
        }
        var ordered = refined.OrderByDescending(c => c.Inliers).ThenBy(c => c.Rms ?? double.MaxValue).Take(maxCircles).ToArray();
        var outputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(sourceImage),
            ["found"] = VisionValue.Boolean(ordered.Length > 0),
            ["count"] = VisionValue.Integer(ordered.Length),
            ["circles"] = VisionValue.String(JsonSerializer.Serialize(ordered.Select(c => new { x = c.Circle.X, y = c.Circle.Y, radius = c.Circle.Radius, rmsError = c.Rms, inliers = c.Inliers, coverageDeg = c.CoverageDeg, confidence = c.Confidence })))
        };
        var overlays = new List<VisionOverlay>();
        if (ordered.Length > 0)
        {
            var best = ordered[0];
            outputs["circle"] = VisionValue.Circle(best.Circle);
            outputs["center"] = VisionValue.Point(new VisionPoint2D(best.Circle.X, best.Circle.Y));
            outputs["radius"] = VisionValue.Double(best.Circle.Radius);
            if (best.Rms is not null) outputs["rmsError"] = VisionValue.Double(best.Rms.Value);
            outputs["coverageDeg"] = VisionValue.Double(best.CoverageDeg);
            outputs["confidence"] = VisionValue.Double(best.Confidence);
            for (var i = 0; i < ordered.Length; i++)
            {
                overlays.Add(new VisionOverlay($"circle-result-{i}", VisionOverlayType.Circle, X: ordered[i].Circle.X, Y: ordered[i].Circle.Y, Radius: ordered[i].Circle.Radius, Stroke: i == 0 ? "#52c41a" : "#597ef7", StrokeWidth: i == 0 ? 2.5 : 1.5));
            }
            overlays.Add(new VisionOverlay("circle-center", VisionOverlayType.Point, X: best.Circle.X, Y: best.Circle.Y, Stroke: "#ff4d4f", StrokeWidth: 2));
            overlays.Add(new VisionOverlay("circle-label", VisionOverlayType.Text, Label: $"R={best.Circle.Radius:0.0}" + (best.Rms is null ? "" : $" RMS={best.Rms:0.00}"), X: best.Circle.X + 8, Y: best.Circle.Y - 8, Stroke: "#95de64", StrokeWidth: 1));
        }
        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["found"] = ordered.Length > 0, ["count"] = ordered.Length,
                ["x"] = ordered.Length > 0 ? Math.Round(ordered[0].Circle.X, 3) : null,
                ["y"] = ordered.Length > 0 ? Math.Round(ordered[0].Circle.Y, 3) : null,
                ["radius"] = ordered.Length > 0 ? Math.Round(ordered[0].Circle.Radius, 3) : null,
                ["rmsError"] = ordered.Length > 0 ? ordered[0].Rms : null,
                ["candidates"] = circles.Length,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            }, overlays));
    }
}

public sealed class CaliperNode : IVisionNodeExecutor
{
    public string Type => "measure.caliper";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceImage = context.RequireImage("image");
        if (sourceImage.Mat.Empty())
            throw new InvalidOperationException("Caliper measurement requires a non-empty image.");

        var search = SubpixelEdgeCore.ResolveSearchLine(context, node);
        var sampleWidth = SubpixelEdgeCore.NormalizeSampleWidth(SubpixelEdgeCore.ClampedIntParameter(node, "sampleWidth", 5, 1, 99));
        var minContrast = Math.Max(0, SubpixelEdgeCore.RequireFiniteParameter(node, "edgeThreshold", 12));
        var polarity = SubpixelEdgeCore.ChoiceParameter(node, "polarity", "any", "any", "rising", "falling");
        var direction = SubpixelEdgeCore.ChoiceParameter(node, "direction", "strongest", "strongest", "firstfromstart", "firstfromend");
        var refine = SubpixelEdgeCore.ChoiceParameter(node, "refine", "none", "none", "parabolic");
        var roi = ContourNodesHelper.ResolveRoi(context, node);

        Mat? mask = null;
        try
        {
            if (roi is not null)
                mask = roi.BuildMaskForMeasurement(sourceImage.Mat.Size(), cancellationToken);
            var gray = PreprocessImageHelpers.ToGray8(sourceImage.Mat, out var autoGray, out var owned);
            try
            {
                if (MeasurementMath.Hypot(search.End.X - search.Start.X, search.End.Y - search.Start.Y) < 2)
                    throw new InvalidOperationException("The search line is too short (minimum 2 px).");
                var (start, end, clamped, intersects) = SubpixelEdgeCore.ClampLine(search.Start, search.End, gray.Cols, gray.Rows);
                var clippedLine = new VisionLine2D(start, end);
                var searchLine = intersects ? clippedLine : search;
                var clippedLength = MeasurementMath.Hypot(end.X - start.X, end.Y - start.Y);
                var candidates = CaliperEdgeDiagnosticCapture.Enabled ? new List<CaliperEdgeCandidate>() : null;
                double[] profile = [];
                var spacing = 0d;
                var edge = !intersects
                    ? SubpixelEdgeCore.EdgeResult.NotFound("search-line-outside-image")
                    : clippedLength < 2
                        ? SubpixelEdgeCore.EdgeResult.NotFound("search-line-clipped-too-short")
                        : SampleAndFind();
                SubpixelEdgeCore.EdgeResult SampleAndFind()
                {
                    profile = SubpixelEdgeCore.SampleProfile(gray, mask, start, end, sampleWidth, out _, out spacing, cancellationToken);
                    return SubpixelEdgeCore.FindEdge(profile, spacing, polarity, minContrast, direction, refine, cancellationToken, candidates);
                }
                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(sourceImage.CloneOwned()),
                    ["found"] = VisionValue.Boolean(edge.Found),
                    ["searchLine"] = VisionValue.Line(searchLine),
                    ["sampleCount"] = VisionValue.Integer(edge.SampleCount)
                };
                var overlays = new List<VisionOverlay>();
                if (intersects) overlays.Add(VisionGeometryHelpers.OverlayLine("caliper-search", searchLine, "#597ef7", 1.5));
                VisionPoint2D? edgePoint = null;
                if (edge.Found)
                {
                    edgePoint = SubpixelEdgeCore.Locate(start, end, edge.Position);
                    outputs["point"] = VisionValue.Point(edgePoint.Value);
                    outputs["strength"] = VisionValue.Double(edge.Strength);
                    if (double.IsFinite(edge.FitError) && edge.FitError >= 0)
                        outputs["fitError"] = VisionValue.Double(edge.FitError);
                    overlays.Add(new VisionOverlay("caliper-edge", VisionOverlayType.Point,
                        X: edgePoint.Value.X, Y: edgePoint.Value.Y, Stroke: "#f5222d", StrokeWidth: 3));
                    overlays.Add(new VisionOverlay("caliper-label", VisionOverlayType.Text,
                        Label: $"G={edge.Strength:0.0}", X: edgePoint.Value.X + 7, Y: edgePoint.Value.Y - 7,
                        Stroke: "#ff7875", StrokeWidth: 1));
                }

                if (candidates is not null)
                {
                    int? selectedIndex = candidates.Count == 0 ? null : direction switch
                    {
                        "firstfromstart" => Enumerable.Range(0, candidates.Count).MinBy(i => candidates[i].Position),
                        "firstfromend" => Enumerable.Range(0, candidates.Count).MaxBy(i => candidates[i].Position),
                        _ => Enumerable.Range(0, candidates.Count).MaxBy(i => Math.Abs(candidates[i].Amplitude))
                    };
                    CaliperEdgeDiagnosticCapture.Publish(new(gray.Cols, gray.Rows, sourceImage.CoordinateFrame,
                        search.Start, search.End, intersects ? start : null, intersects ? end : null, clamped,
                        intersects ? MeasurementMath.Hypot(start.X - search.Start.X, start.Y - search.Start.Y) : 0,
                        spacing, minContrast, sampleWidth, polarity, direction, refine, edge.Found, edge.Reason,
                        profile, candidates, selectedIndex, edge.Found ? edge.Position : null, edgePoint,
                        edge.Found ? edge.Strength : null,
                        edge.Found && double.IsFinite(edge.FitError) && edge.FitError >= 0 ? edge.FitError : null));
                }

                return ValueTask.FromResult(new NodeExecutorResult(outputs,
                    new Dictionary<string, object?>
                    {
                        ["found"] = edge.Found,
                        ["reason"] = edge.Reason,
                        ["x"] = edge.Found ? Math.Round(edgePoint!.Value.X, 4) : null,
                        ["y"] = edge.Found ? Math.Round(edgePoint!.Value.Y, 4) : null,
                        ["strength"] = edge.Found ? Math.Round(edge.Strength, 3) : null,
                        ["fitError"] = edge.Found && double.IsFinite(edge.FitError) && edge.FitError >= 0 ? Math.Round(edge.FitError, 4) : null,
                        ["sampleCount"] = edge.SampleCount,
                        ["polarity"] = polarity,
                        ["direction"] = direction,
                        ["refine"] = refine,
                        ["edgeThreshold"] = minContrast,
                        ["sampleWidth"] = sampleWidth,
                        ["lineClampedToImage"] = clamped,
                        ["autoGray"] = autoGray,
                        ["roi"] = roi?.Type.ToString() ?? "FullImage"
                    }, overlays));
            }
            finally { owned?.Dispose(); }
        }
        finally { mask?.Dispose(); }
    }

}

public sealed class RotatedRectangleNode : IVisionNodeExecutor
{
    public string Type => "measure.rotatedRect";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sourceImage = context.RequireImage("image");
        var minArea = node.GetDouble("minArea", 500);
        var roi = node.GetRoi();

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        using var analysis = VisionGeometryHelpers.ApplyRoi(gray, roi);
        JLVisionOperators.FindContours(analysis, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var best = contours
            .Select(c => (Contour: c, Area: JLVisionOperators.ContourArea(c)))
            .Where(x => x.Area >= minArea)
            .OrderByDescending(x => x.Area)
            .FirstOrDefault();

        if (best.Contour is null)
            throw new InvalidOperationException($"No contour found with area >= {minArea:0.##}.");

        var rr = JLVisionOperators.MinAreaRect(best.Contour);
        var rectangle = new VisionRectangle2D(rr.Center.X, rr.Center.Y, rr.Size.Width, rr.Size.Height, rr.Angle);
        var points = rr.Points().Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray();
        var overlays = new[]
        {
            new VisionOverlay("rotated-rect", VisionOverlayType.Polygon, Points: points, Stroke: "#fa8c16", StrokeWidth: 2.5),
            new VisionOverlay("rotated-rect-center", VisionOverlayType.Point, X: rectangle.X, Y: rectangle.Y, Stroke: "#ff4d4f", StrokeWidth: 2),
            new VisionOverlay("rotated-rect-label", VisionOverlayType.Text, Label: $"{rectangle.Width:0.0}×{rectangle.Height:0.0} @{rectangle.AngleDeg:0.0}°", X: rectangle.X + 8, Y: rectangle.Y - 8, Stroke: "#ffc069", StrokeWidth: 1)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage),
                ["rectangle"] = VisionValue.Rectangle(rectangle),
                ["center"] = VisionValue.Point(new VisionPoint2D(rectangle.X, rectangle.Y)),
                ["pose"] = VisionValue.Pose(new VisionPose2D(rectangle.X, rectangle.Y, rectangle.AngleDeg)),
                ["angle"] = VisionValue.Double(rectangle.AngleDeg)
            },
            new Dictionary<string, object?>
            {
                ["center"] = $"({rectangle.X:0.0},{rectangle.Y:0.0})",
                ["width"] = Math.Round(rectangle.Width, 2),
                ["height"] = Math.Round(rectangle.Height, 2),
                ["angleDeg"] = Math.Round(rectangle.AngleDeg, 2),
                ["contourArea"] = Math.Round(best.Area, 2)
            },
            overlays));
    }
}

public sealed class IntersectionNode : IVisionNodeExecutor
{
    public string Type => "geometry.intersection";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var a = context.Require<VisionLine2D>("lineA");
        var b = context.Require<VisionLine2D>("lineB");
        var x1 = a.Start.X; var y1 = a.Start.Y; var x2 = a.End.X; var y2 = a.End.Y;
        var x3 = b.Start.X; var y3 = b.Start.Y; var x4 = b.End.X; var y4 = b.End.Y;
        var denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(denominator) < 1e-9)
            throw new InvalidOperationException("Lines are parallel or coincident; intersection is undefined.");

        var determinantA = x1 * y2 - y1 * x2;
        var determinantB = x3 * y4 - y3 * x4;
        var x = (determinantA * (x3 - x4) - (x1 - x2) * determinantB) / denominator;
        var y = (determinantA * (y3 - y4) - (y1 - y2) * determinantB) / denominator;
        var point = new VisionPoint2D(x, y);

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["point"] = VisionValue.Point(point) },
            new Dictionary<string, object?> { ["x"] = Math.Round(x, 3), ["y"] = Math.Round(y, 3) },
            [new VisionOverlay("intersection", VisionOverlayType.Point, X: x, Y: y, Stroke: "#eb2f96", StrokeWidth: 3)]));
    }
}

public sealed class DistanceNode : IVisionNodeExecutor
{
    public string Type => "geometry.distance";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var a = context.Require<VisionPoint2D>("pointA");
        var b = context.Require<VisionPoint2D>("pointB");
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var line = new VisionLine2D(a, b);

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["distance"] = VisionValue.Double(distance) },
            new Dictionary<string, object?> { ["distance"] = Math.Round(distance, 3) },
            [
                VisionGeometryHelpers.OverlayLine("distance-line", line, "#722ed1", 1.5),
                new VisionOverlay("distance-label", VisionOverlayType.Text, Label: $"D={distance:0.00}px", X: (a.X + b.X) / 2 + 5, Y: (a.Y + b.Y) / 2 - 5, Stroke: "#b37feb", StrokeWidth: 1)
            ]));
    }
}

public sealed class AngleNode : IVisionNodeExecutor
{
    public string Type => "geometry.angle";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var a = context.Require<VisionLine2D>("lineA");
        var b = context.Require<VisionLine2D>("lineB");
        var angle = VisionGeometryHelpers.NormalizeAngleDifference(a.AngleDeg, b.AngleDeg);

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["angle"] = VisionValue.Double(angle) },
            new Dictionary<string, object?> { ["angleDeg"] = Math.Round(angle, 3), ["lineA"] = Math.Round(a.AngleDeg, 3), ["lineB"] = Math.Round(b.AngleDeg, 3) },
            [
                VisionGeometryHelpers.OverlayLine("angle-a", a, "#1677ff", 1.5),
                VisionGeometryHelpers.OverlayLine("angle-b", b, "#fa541c", 1.5)
            ]));
    }
}
