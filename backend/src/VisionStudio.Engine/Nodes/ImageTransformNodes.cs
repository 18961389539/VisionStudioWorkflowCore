using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class ImageCropNode : IVisionNodeExecutor
{
    public string Type => "image.crop";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        var x = node.GetInt("x", 0); var y = node.GetInt("y", 0);
        var width = node.GetInt("width", source.Width); var height = node.GetInt("height", source.Height);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > source.Width || (long)y + height > source.Height)
            throw new InvalidOperationException($"Crop rectangle ({x},{y},{width},{height}) must be positive and fit inside {source.Width}x{source.Height}.");
        using var image = JLVisionImages.FromMat(source);
        using var region = image.CropRectangle1(y, x, y + height - 1, x + width - 1);
        var output = JLVisionImages.ToMat(region);
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) }, new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height }));
    }
}

public sealed class ImageResizeNode : IVisionNodeExecutor
{
    public string Type => "image.resize";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        var width = node.GetInt("width", source.Width); var height = node.GetInt("height", source.Height);
        if (width < 1 || height < 1 || width > 16384 || height > 16384) throw new InvalidOperationException("Resize width and height must be between 1 and 16384.");
        var method = node.GetString("interpolation", "linear").Trim().ToLowerInvariant();
        var interpolation = method switch { "nearest" => InterpolationFlags.Nearest, "cubic" => InterpolationFlags.Cubic, "area" => InterpolationFlags.Area, "linear" => InterpolationFlags.Linear, _ => throw new InvalidOperationException("Interpolation must be nearest, linear, cubic, or area.") };
        var output = ImageTransformOutput.Create(mat => JLVisionOperators.Resize(source, mat, new Size(width, height), interpolation: interpolation));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) }, new Dictionary<string, object?> { ["width"] = width, ["height"] = height, ["interpolation"] = method }));
    }
}

public sealed class ImageRotateNode : IVisionNodeExecutor
{
    public string Type => "image.rotate";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        var angle = node.GetDouble("angleDeg", 0);
        if (!double.IsFinite(angle)) throw new InvalidOperationException("Rotation angle must be finite.");
        var expand = node.GetBool("expand", true);
        var center = new Point2f((source.Width - 1) / 2f, (source.Height - 1) / 2f);
        using var matrix = JLVisionOperators.GetRotationMatrix2D(center, angle, 1);
        var width = source.Width; var height = source.Height;
        if (expand)
        {
            var cos = Math.Abs(matrix.At<double>(0, 0)); var sin = Math.Abs(matrix.At<double>(0, 1));
            width = Math.Max(1, (int)Math.Ceiling(source.Width * cos + source.Height * sin - 1e-10));
            height = Math.Max(1, (int)Math.Ceiling(source.Width * sin + source.Height * cos - 1e-10));
            matrix.Set(0, 2, matrix.At<double>(0, 2) + (width - 1) / 2d - center.X);
            matrix.Set(1, 2, matrix.At<double>(1, 2) + (height - 1) / 2d - center.Y);
        }
        var output = ImageTransformOutput.Create(mat => JLVisionOperators.WarpAffine(source, mat, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0)));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) }, new Dictionary<string, object?> { ["angleDeg"] = angle, ["expand"] = expand, ["width"] = width, ["height"] = height }));
    }
}

public sealed class PerspectiveCorrectionNode : IVisionNodeExecutor
{
    public string Type => "image.perspective";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        var points = Enumerable.Range(1, 4).Select(i => new Point2f((float)node.GetDouble($"x{i}", double.NaN), (float)node.GetDouble($"y{i}", double.NaN))).ToArray();
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) throw new InvalidOperationException("Four finite source corner points (x1,y1 through x4,y4) are required in top-left, top-right, bottom-right, bottom-left order.");
        var width = node.GetInt("width", 0); var height = node.GetInt("height", 0);
        if (width < 1 || height < 1 || width > 16384 || height > 16384) throw new InvalidOperationException("Perspective output width and height must be between 1 and 16384.");
        if (Math.Abs(JLVisionOperators.ContourArea(points, oriented: false)) < 1) throw new InvalidOperationException("Perspective source corners must enclose a non-degenerate quadrilateral.");
        if (!JLVisionOperators.IsContourConvex(points)) throw new InvalidOperationException("Perspective source corners must form a convex quadrilateral in perimeter order.");
        Point2f[] target = [new(0, 0), new(width - 1, 0), new(width - 1, height - 1), new(0, height - 1)];
        using var matrix = JLVisionOperators.GetPerspectiveTransform(points, target);
        var output = ImageTransformOutput.Create(mat => JLVisionOperators.WarpPerspective(source, mat, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0)));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)) }, new Dictionary<string, object?> { ["width"] = width, ["height"] = height }));
    }
}

internal static class ImageTransformOutput
{
    public static Mat Create(Action<Mat> transform)
    {
        var output = new Mat();
        try { transform(output); return output; }
        catch { output.Dispose(); throw; }
    }
}
