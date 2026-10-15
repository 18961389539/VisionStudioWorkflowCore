using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class CalibrationDetectBoardNode : IVisionNodeExecutor
{
    public string Type => "calibration.detectBoard";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        var columns = node.GetInt("columns", 7); var rows = node.GetInt("rows", 6); var spacing = node.GetDouble("spacingMm", 10);
        var error = node.GetDouble("maxGridError", 2); var pattern = node.GetString("pattern", "chessboard");
        if (columns is < 2 or > 30 || rows is < 2 or > 30 || rows * columns < 6 || !double.IsFinite(spacing) || spacing <= 0 || spacing > 10000 || !double.IsFinite(error) || error is < .1 or > 20)
            throw new InvalidOperationException("Board needs 2..30 rows/columns, at least six points, positive spacing <=10000 mm and grid error 0.1..20 px.");
        if (pattern is not ("chessboard" or "circles")) throw new InvalidOperationException("Board pattern must be chessboard or circles.");
        var request = new CameraCalibrationRequest(source.Width, source.Height, []);
        if (context.TryGetValue("calibrationDataJson", out var previous))
        {
            try { request = JsonSerializer.Deserialize<CameraCalibrationRequest>(previous.Require<string>("calibrationDataJson"), Json) ?? throw new JsonException("empty request"); }
            catch (JsonException ex) { throw new InvalidOperationException("Invalid calibrationDataJson.", ex); }
            if (request.ImageWidth != source.Width || request.ImageHeight != source.Height || request.Views is null || request.Views.Count > 100)
                throw new InvalidOperationException("Accumulated board views must use the same image dimensions and contain at most 100 views.");
            // Copy the list so callers' payloads are never mutated.
            request = request with { Views = request.Views.ToList() };
        }
        var candidateCount = 0;
        var points = new List<VisionPoint2D>();
        using var native = JLVisionImages.FromMat(source.Mat); using var channels = native.CountChannels();
        using var gray = channels.I == 1 ? native.CopyImage() : native.Rgb1ToGray();
        if (pattern == "chessboard")
        {
            var contrast = node.GetDouble("saddleThreshold", 10);
            if (!double.IsFinite(contrast) || contrast is < .1 or > 255) throw new InvalidOperationException("Saddle contrast must be 0.1..255.");
            gray.SaddlePointsSubPix("facet", 1, contrast, out var ys, out var xs);
            using (ys) using (xs)
            {
                candidateCount = ys.Length;
                if (ys.Length > 3600) return ValueTask.FromResult(Result(null, "too-many-candidates"));
                for (var i = 0; i < ys.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var p = new VisionPoint2D(xs[i].D, ys[i].D);
                    points.Add(p);
                }
                // The native facet detector may report two subpixel extrema at the same
                // discrete chess intersection. Merge only these subpixel duplicates.
                var clusters = new List<List<VisionPoint2D>>();
                foreach (var p in points)
                {
                    var cluster = clusters.FirstOrDefault(c => MeasurementMath.Hypot(c[0].X - p.X, c[0].Y - p.Y) < 1.5);
                    if (cluster is null) clusters.Add([p]); else cluster.Add(p);
                }
                points = clusters.Select(c => new VisionPoint2D(c.Average(p => p.X), c.Average(p => p.Y))).ToList();
            }
        }
        else
        {
            var threshold = node.GetDouble("threshold", 128); var minimum = node.GetDouble("minArea", 20); var maximum = node.GetDouble("maxArea", 10000); var circularity = node.GetDouble("minCircularity", .6);
            if (new[] { threshold, minimum, maximum, circularity }.Any(v => !double.IsFinite(v)) || threshold is < 0 or > 255 || minimum < 1 || maximum < minimum || circularity is < 0 or > 1)
                throw new InvalidOperationException("Invalid circle grid threshold, area or circularity.");
            using var binary = gray.Threshold(node.GetBool("darkObjects", true) ? 0 : threshold, node.GetBool("darkObjects", true) ? threshold : 255);
            using var connected = binary.Connection(); using var byArea = connected.SelectShape("area", "and", minimum, maximum);
            using var dots = byArea.SelectShape("circularity", "and", circularity, 1);
            using var areas = dots.AreaCenter(out JlTuple ys, out JlTuple xs);
            using (ys) using (xs)
            {
                candidateCount = ys.Length;
                if (ys.Length > 900) return ValueTask.FromResult(Result(null, "too-many-candidates"));
                for (var i = 0; i < ys.Length; i++) points.Add(new(xs[i].D, ys[i].D));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = BoardLattice.Order(points, columns, rows, error, cancellationToken);
        if (ordered is null) return ValueTask.FromResult(Result(null, "incomplete-or-inconsistent-grid"));
        if (request.Views.Count >= 100) throw new InvalidOperationException("Calibration view budget exceeded (100).");
        var objects = Enumerable.Range(0, rows * columns).Select(i => new List<double> { i % columns * spacing, i / columns * spacing, 0 }).ToList();
        var view = new CameraCalibrationView(objects, ordered.Select(p => new List<double> { p.X, p.Y }).ToList());
        request.Views.Add(view);
        return ValueTask.FromResult(Result(view, null));

        NodeExecutorResult Result(CameraCalibrationView? view, string? reason)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(view is not null), ["count"] = VisionValue.Integer(view?.ImagePoints.Count ?? 0),
                ["calibrationDataJson"] = VisionValue.String(JsonSerializer.Serialize(request)) };
            if (view is not null) outputs["viewJson"] = VisionValue.String(JsonSerializer.Serialize(view));
            var overlays = view?.ImagePoints.Select((p, i) => new VisionOverlay($"board-{i}", VisionOverlayType.Circle, X: p[0], Y: p[1], Radius: 3, Label: i.ToString())).ToArray();
            outputs["image"] = VisionValue.Image(source.CloneOwned());
            return new(outputs, new Dictionary<string, object?> { ["found"] = view is not null, ["count"] = view?.ImagePoints.Count ?? 0,
                ["views"] = request.Views.Count, ["pattern"] = pattern, ["candidateCount"] = candidateCount, ["gridCandidates"] = points.Count,
                ["reason"] = reason, ["algorithmBackend"] = "JLVision" }, overlays);
        }
    }

}

/// <summary>Order a complete isolated lattice, verifying every point against its projective position.</summary>
internal static class BoardLattice
{
    public static VisionPoint2D[]? Order(IReadOnlyList<VisionPoint2D> points, int columns, int rows, double tolerance, CancellationToken token)
    {
        if (points.Count != rows * columns || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return null;
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).Distinct().ToArray();
        if (sorted.Length != points.Count) return null;
        var hull = new List<VisionPoint2D>();
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count;
        foreach (var p in sorted.Reverse().Skip(1))
        {
            while (hull.Count > lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        if (hull.Count < 4) return null;
        while (hull.Count > 4)
        {
            token.ThrowIfCancellationRequested();
            var remove = Enumerable.Range(0, hull.Count).MinBy(i => Math.Abs(Cross(hull[(i + hull.Count - 1) % hull.Count], hull[i], hull[(i + 1) % hull.Count])));
            hull.RemoveAt(remove);
        }
        var start = Enumerable.Range(0, 4).MinBy(i => hull[i].X + hull[i].Y);
        VisionPoint2D[]? best = null; var bestError = double.PositiveInfinity;
        foreach (var winding in new[] { 1, -1 })
        {
            token.ThrowIfCancellationRequested();
            var corners = Enumerable.Range(0, 4).Select(i => hull[(start + winding * i + 8) % 4]).ToArray();
            if (Math.Abs(Cross(corners[0], corners[1], corners[2])) < 1) continue;
            Point2f[] lattice = [new(0, 0), new(columns - 1, 0), new(columns - 1, rows - 1), new(0, rows - 1)];
            var imageCorners = corners.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
            using var toGrid = JLVisionOperators.GetPerspectiveTransform(imageCorners, lattice);
            using var toImage = JLVisionOperators.GetPerspectiveTransform(lattice, imageCorners);
            var assigned = new VisionPoint2D[points.Count]; var used = new bool[points.Count]; var maximum = 0d; var valid = true;
            foreach (var point in points)
            {
                var grid = Apply(toGrid, point); var col = (int)Math.Round(grid.X); var row = (int)Math.Round(grid.Y);
                if (!double.IsFinite(grid.X) || !double.IsFinite(grid.Y) || col < 0 || col >= columns || row < 0 || row >= rows || used[row * columns + col]) { valid = false; break; }
                var predicted = Apply(toImage, new(col, row));
                var residual = MeasurementMath.Hypot(point.X - predicted.X, point.Y - predicted.Y);
                if (!double.IsFinite(residual) || residual > tolerance) { valid = false; break; }
                maximum = Math.Max(maximum, residual); used[row * columns + col] = true; assigned[row * columns + col] = point;
            }
            if (valid && maximum < bestError) { best = assigned; bestError = maximum; }
        }
        return best;
    }
    private static double Cross(VisionPoint2D a, VisionPoint2D b, VisionPoint2D c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static VisionPoint2D Apply(Mat matrix, VisionPoint2D p)
    {
        var z = matrix.At<double>(2, 0) * p.X + matrix.At<double>(2, 1) * p.Y + matrix.At<double>(2, 2);
        return new((matrix.At<double>(0, 0) * p.X + matrix.At<double>(0, 1) * p.Y + matrix.At<double>(0, 2)) / z,
            (matrix.At<double>(1, 0) * p.X + matrix.At<double>(1, 1) * p.Y + matrix.At<double>(1, 2)) / z);
    }
}
