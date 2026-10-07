using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class SyntheticImageNode : IVisionNodeExecutor
{
    public string Type => "image.synthetic";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var width = node.GetInt("width", 640);
        var height = node.GetInt("height", 480);
        var centerX = node.GetInt("centerX", width / 2);
        var centerY = node.GetInt("centerY", height / 2);
        var radius = node.GetInt("radius", 80);

        var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(28));
        Cv2.Circle(mat, new Point(centerX, centerY), radius, Scalar.All(225), -1, LineTypes.AntiAlias);
        Cv2.Rectangle(mat, new Rect(70, 65, 115, 70), Scalar.All(95), -1);
        Cv2.Line(mat, new Point(40, height - 60), new Point(width - 40, height - 95), Scalar.All(125), 5, LineTypes.AntiAlias);
        var image = VisionImage.Own(mat);

        var overlays = new List<VisionOverlay>
        {
            new("synthetic-rect", VisionOverlayType.Rectangle, X: 70, Y: 65, Width: 115, Height: 70, Stroke: "#40a9ff", StrokeWidth: 1.5),
            new("synthetic-line", VisionOverlayType.Line, X: 40, Y: height - 60, X2: width - 40, Y2: height - 95, Stroke: "#9254de", StrokeWidth: 1.5)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) },
            new Dictionary<string, object?> { ["size"] = $"{width}x{height}", ["circle"] = $"({centerX},{centerY}) r={radius}" },
            overlays));
    }
}

public sealed class ThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.threshold";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sourceImage = context.RequireImage("image");
        var source = sourceImage.Mat;
        var threshold = node.GetDouble("threshold", 128);
        var roi = node.GetRoi();

        var thresholded = new Mat();
        Cv2.Threshold(source, thresholded, threshold, 255, ThresholdTypes.Binary);

        Mat outputMat;
        if (roi is null)
        {
            outputMat = thresholded;
        }
        else
        {
            using var mask = roi.BuildMask(source.Size());
            outputMat = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.All(0));
            thresholded.CopyTo(outputMat, mask);
            thresholded.Dispose();
        }

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(outputMat)) },
            new Dictionary<string, object?>
            {
                ["threshold"] = threshold,
                ["roi"] = roi?.Type.ToString() ?? "FullImage"
            }));
    }
}

public sealed class LargestBlobNode : IVisionNodeExecutor
{
    public string Type => "measure.blob";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var sourceImage = context.RequireImage("image");
        var source = sourceImage.Mat;
        var minArea = node.GetDouble("minArea", 1000);
        var roi = node.GetRoi();

        Mat? masked = null;
        try
        {
            var analysis = source;
            if (roi is not null)
            {
                using var mask = roi.BuildMask(source.Size());
                masked = new Mat(source.Rows, source.Cols, source.Type(), Scalar.All(0));
                source.CopyTo(masked, mask);
                analysis = masked;
            }

            Cv2.FindContours(analysis, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var best = contours
                .Select(c => (Contour: c, Area: Cv2.ContourArea(c)))
                .Where(x => x.Area >= minArea)
                .OrderByDescending(x => x.Area)
                .FirstOrDefault();

            if (best.Contour is null)
                throw new InvalidOperationException($"No blob found with area >= {minArea:0.##}.");

            Cv2.MinEnclosingCircle(best.Contour, out Point2f center, out float radius);
            var typedCenter = new VisionPoint2D(center.X, center.Y);
            var typedCircle = new VisionCircle(center.X, center.Y, radius);

            var stride = Math.Max(1, (int)Math.Ceiling(best.Contour.Length / 160d));
            var contourPoints = best.Contour
                .Where((_, i) => i % stride == 0)
                .Select(p => new VisionOverlayPoint(p.X, p.Y))
                .ToArray();

            var overlays = new List<VisionOverlay>
            {
                new("blob-contour", VisionOverlayType.Contour, Points: contourPoints, Stroke: "#faad14", StrokeWidth: 1.5),
                new("blob-circle", VisionOverlayType.Circle, X: center.X, Y: center.Y, Radius: radius, Stroke: "#52c41a", StrokeWidth: 2),
                new("blob-center", VisionOverlayType.Point, X: center.X, Y: center.Y, Stroke: "#ff4d4f", StrokeWidth: 2),
                new("blob-label", VisionOverlayType.Text, Label: $"X={center.X:0.0} Y={center.Y:0.0} A={best.Area:0}", X: 12, Y: 22, Stroke: "#ffd666", StrokeWidth: 1)
            };

            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sourceImage),
                ["x"] = VisionValue.Double(center.X),
                ["y"] = VisionValue.Double(center.Y),
                ["area"] = VisionValue.Double(best.Area),
                ["radius"] = VisionValue.Double(radius),
                ["center"] = VisionValue.Point(typedCenter),
                ["circle"] = VisionValue.Circle(typedCircle)
            };
            var summary = new Dictionary<string, object?>
            {
                ["x"] = Math.Round(center.X, 2),
                ["y"] = Math.Round(center.Y, 2),
                ["area"] = Math.Round(best.Area, 2),
                ["radius"] = Math.Round(radius, 2),
                ["roi"] = roi?.Type.ToString() ?? "FullImage",
                ["centerType"] = VisionDataType.Point2D.ToString(),
                ["circleType"] = VisionDataType.Circle.ToString()
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary, overlays));
        }
        finally
        {
            masked?.Dispose();
        }
    }
}
