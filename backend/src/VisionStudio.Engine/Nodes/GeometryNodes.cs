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
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
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
        var sourceImage = context.RequireImage("image");
        var low = node.GetDouble("lowThreshold", 50);
        var high = node.GetDouble("highThreshold", 150);
        var roi = node.GetRoi();

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        using var analysis = VisionGeometryHelpers.ApplyRoi(gray, roi);
        var edges = new Mat();
        Cv2.Canny(analysis, edges, low, high);
        var edgePixels = Cv2.CountNonZero(edges);

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(edges)),
                ["edgeCount"] = VisionValue.Integer(edgePixels)
            },
            new Dictionary<string, object?>
            {
                ["lowThreshold"] = low,
                ["highThreshold"] = high,
                ["edgePixels"] = edgePixels,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            }));
    }
}

public sealed class LineNode : IVisionNodeExecutor
{
    public string Type => "feature.line";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sourceImage = context.RequireImage("image");
        var houghThreshold = node.GetInt("houghThreshold", 45);
        var minLength = node.GetDouble("minLength", 60);
        var maxGap = node.GetDouble("maxGap", 12);
        var roi = node.GetRoi();

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        using var analysis = VisionGeometryHelpers.ApplyRoi(gray, roi);
        using var edges = new Mat();
        Cv2.Canny(analysis, edges, 50, 150);

        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180.0, houghThreshold, minLength, maxGap);
        if (lines.Length == 0)
            throw new InvalidOperationException("No line found. Adjust ROI/Hough threshold/minimum length.");

        var best = lines
            .OrderByDescending(x => Math.Sqrt(Math.Pow(x.P2.X - x.P1.X, 2) + Math.Pow(x.P2.Y - x.P1.Y, 2)))
            .First();

        var line = new VisionLine2D(new VisionPoint2D(best.P1.X, best.P1.Y), new VisionPoint2D(best.P2.X, best.P2.Y));
        var overlays = new[]
        {
            VisionGeometryHelpers.OverlayLine("line-result", line, "#13c2c2", 2.5),
            new VisionOverlay("line-label", VisionOverlayType.Text, Label: $"L={line.Length:0.0} A={line.AngleDeg:0.0}°", X: line.Start.X + 6, Y: line.Start.Y - 8, Stroke: "#36cfc9", StrokeWidth: 1)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage),
                ["line"] = VisionValue.Line(line),
                ["length"] = VisionValue.Double(line.Length),
                ["angle"] = VisionValue.Double(line.AngleDeg)
            },
            new Dictionary<string, object?>
            {
                ["length"] = Math.Round(line.Length, 2),
                ["angleDeg"] = Math.Round(line.AngleDeg, 2),
                ["candidates"] = lines.Length,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            },
            overlays));
    }
}

public sealed class CircleNode : IVisionNodeExecutor
{
    public string Type => "feature.circle";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sourceImage = context.RequireImage("image");
        var minRadius = node.GetInt("minRadius", 15);
        var maxRadius = node.GetInt("maxRadius", 180);
        var minDist = node.GetDouble("minDist", 40);
        var param1 = node.GetDouble("edgeThreshold", 100);
        var param2 = node.GetDouble("centerThreshold", 24);
        var roi = node.GetRoi();

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        using var analysis = VisionGeometryHelpers.ApplyRoi(gray, roi);
        using var blurred = new Mat();
        Cv2.GaussianBlur(analysis, blurred, new Size(9, 9), 2);

        var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient, 1, minDist, param1, param2, minRadius, maxRadius);
        if (circles.Length == 0)
            throw new InvalidOperationException("No circle found. Adjust ROI/radius/threshold parameters.");

        var best = circles.OrderByDescending(c => c.Radius).First();
        var circle = new VisionCircle(best.Center.X, best.Center.Y, best.Radius);
        var overlays = new[]
        {
            new VisionOverlay("circle-result", VisionOverlayType.Circle, X: circle.X, Y: circle.Y, Radius: circle.Radius, Stroke: "#52c41a", StrokeWidth: 2.5),
            new VisionOverlay("circle-center", VisionOverlayType.Point, X: circle.X, Y: circle.Y, Stroke: "#ff4d4f", StrokeWidth: 2),
            new VisionOverlay("circle-label", VisionOverlayType.Text, Label: $"R={circle.Radius:0.0}", X: circle.X + 8, Y: circle.Y - 8, Stroke: "#95de64", StrokeWidth: 1)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage),
                ["circle"] = VisionValue.Circle(circle),
                ["center"] = VisionValue.Point(new VisionPoint2D(circle.X, circle.Y)),
                ["radius"] = VisionValue.Double(circle.Radius)
            },
            new Dictionary<string, object?>
            {
                ["x"] = Math.Round(circle.X, 2),
                ["y"] = Math.Round(circle.Y, 2),
                ["radius"] = Math.Round(circle.Radius, 2),
                ["candidates"] = circles.Length
            },
            overlays));
    }
}

public sealed class CaliperNode : IVisionNodeExecutor
{
    public string Type => "measure.caliper";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sourceImage = context.RequireImage("image");
        var start = new VisionPoint2D(node.GetDouble("startX", 120), node.GetDouble("startY", 240));
        var end = new VisionPoint2D(node.GetDouble("endX", 520), node.GetDouble("endY", 240));
        var width = Math.Max(1, node.GetInt("sampleWidth", 5));
        var threshold = Math.Max(0, node.GetDouble("edgeThreshold", 12));
        var polarity = node.GetString("polarity", "Any");

        using var gray = VisionGeometryHelpers.ToGray(sourceImage.Mat);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 2)
            throw new InvalidOperationException("Caliper search line is too short.");

        var nx = -dy / length;
        var ny = dx / length;
        var steps = Math.Max(2, (int)Math.Ceiling(length));
        var samples = new double[steps + 1];
        var points = new VisionPoint2D[steps + 1];

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var x = start.X + dx * t;
            var y = start.Y + dy * t;
            points[i] = new VisionPoint2D(x, y);

            double sum = 0;
            var count = 0;
            for (var w = -width / 2; w <= width / 2; w++)
            {
                var px = Math.Clamp((int)Math.Round(x + nx * w), 0, gray.Cols - 1);
                var py = Math.Clamp((int)Math.Round(y + ny * w), 0, gray.Rows - 1);
                sum += gray.At<byte>(py, px);
                count++;
            }
            samples[i] = sum / Math.Max(1, count);
        }

        var bestIndex = -1;
        var bestScore = double.NegativeInfinity;
        var bestSigned = 0d;
        for (var i = 1; i < samples.Length; i++)
        {
            var gradient = samples[i] - samples[i - 1];
            var score = polarity.ToLowerInvariant() switch
            {
                "rising" => gradient,
                "falling" => -gradient,
                _ => Math.Abs(gradient)
            };
            if (score > bestScore)
            {
                bestScore = score;
                bestSigned = gradient;
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || bestScore < threshold)
            throw new InvalidOperationException($"No caliper edge exceeded threshold {threshold:0.##}. Best={Math.Max(0, bestScore):0.##}.");

        var edgePoint = points[bestIndex];
        var searchLine = new VisionLine2D(start, end);
        var overlays = new[]
        {
            VisionGeometryHelpers.OverlayLine("caliper-search", searchLine, "#597ef7", 1.5),
            new VisionOverlay("caliper-edge", VisionOverlayType.Point, X: edgePoint.X, Y: edgePoint.Y, Stroke: "#f5222d", StrokeWidth: 3),
            new VisionOverlay("caliper-label", VisionOverlayType.Text, Label: $"G={bestSigned:0.0}", X: edgePoint.X + 7, Y: edgePoint.Y - 7, Stroke: "#ff7875", StrokeWidth: 1)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage),
                ["point"] = VisionValue.Point(edgePoint),
                ["strength"] = VisionValue.Double(Math.Abs(bestSigned)),
                ["searchLine"] = VisionValue.Line(searchLine)
            },
            new Dictionary<string, object?>
            {
                ["x"] = Math.Round(edgePoint.X, 2),
                ["y"] = Math.Round(edgePoint.Y, 2),
                ["strength"] = Math.Round(Math.Abs(bestSigned), 2),
                ["polarity"] = polarity,
                ["sampleWidth"] = width
            },
            overlays));
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
        Cv2.FindContours(analysis, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var best = contours
            .Select(c => (Contour: c, Area: Cv2.ContourArea(c)))
            .Where(x => x.Area >= minArea)
            .OrderByDescending(x => x.Area)
            .FirstOrDefault();

        if (best.Contour is null)
            throw new InvalidOperationException($"No contour found with area >= {minArea:0.##}.");

        var rr = Cv2.MinAreaRect(best.Contour);
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
