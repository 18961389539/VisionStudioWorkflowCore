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
        if (source.Empty())
            throw new InvalidOperationException("Image crop requires a non-empty image.");
        var roi = context.TryGetValue("roi", out var roiValue)
            ? roiValue.Require<VisionRoi>("roi")
            : node.GetRoi() ?? new VisionRoi(
                VisionRoiType.Rectangle,
                X: node.GetDouble("x", 0),
                Y: node.GetDouble("y", 0),
                Width: node.GetDouble("width", source.Width),
                Height: node.GetDouble("height", source.Height));
        ValidateCropRoi(roi);

        using var rasterMask = roi.BuildMask(source.Size());
        using var maskImageNative = JLVisionImages.FromMat(rasterMask);
        using var selectedRegion = maskImageNative.Threshold(1d, 255d);
        var selectedPixels = selectedRegion.AreaCenter(out double _, out double _);
        if (selectedPixels == 0)
            throw new InvalidOperationException("裁剪区域没有覆盖任何图像像素。");
        selectedRegion.SmallestRectangle1(out int top, out int left, out int bottom, out int right);
        var bounds = new Rect(left, top, right - left + 1, bottom - top + 1);
        using var binaryImage = selectedRegion.RegionToBin(255, 0, source.Width, source.Height);
        using var binaryMask = JLVisionImages.ToMat(binaryImage);

        using var sourceCrop = new Mat(source, bounds);
        using var cropMask = new Mat(binaryMask, bounds);
        var croppedImage = new Mat(sourceCrop.Rows, sourceCrop.Cols, sourceCrop.Type(), Scalar.All(0));
        var maskImage = cropMask.Clone();
        try
        {
            // 非矩形 ROI 输出紧包围框；区域外清零。BGRA 输入会同时得到透明外区。
            sourceCrop.CopyTo(croppedImage, cropMask);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(VisionImage.Own(croppedImage)),
                    ["roiMask"] = VisionValue.Image(VisionImage.Own(maskImage)),
                    ["cropOrigin"] = VisionValue.Point(new VisionPoint2D(bounds.X, bounds.Y)),
                    ["transform"] = VisionValue.Transform(new VisionTransform2D(
                        "InputImage", "CropImage", "px",
                        1, 0, -bounds.X,
                        0, 1, -bounds.Y,
                        0, 0, 1))
                },
                new Dictionary<string, object?>
                {
                    ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height,
                    ["cropOriginX"] = bounds.X, ["cropOriginY"] = bounds.Y,
                    ["roiType"] = roi.Type.ToString(), ["selectedPixels"] = selectedPixels
                }));
        }
        catch
        {
            croppedImage.Dispose();
            maskImage.Dispose();
            throw;
        }
    }

    private static void ValidateCropRoi(VisionRoi roi)
    {
        static bool Finite(params double?[] values) => values.All(value => value.HasValue && double.IsFinite(value.Value));
        static bool Bounded(params double?[] values) => values.All(value => value.HasValue && Math.Abs(value.Value) <= 10_000_000);
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
            {
                var w = roi.Width ?? 0; var h = roi.Height ?? 0;
                if (!Finite(roi.X, roi.Y, roi.Width, roi.Height) || !Bounded(roi.X, roi.Y, roi.Width, roi.Height) || w <= 0 || h <= 0)
                    throw new InvalidOperationException("矩形裁剪区域必须为正尺寸，且坐标绝对值不超过 10000000 px。");
                break;
            }
            case VisionRoiType.Circle:
            {
                var radius = roi.Radius ?? 0;
                if (!Finite(roi.X, roi.Y, roi.Radius) || !Bounded(roi.X, roi.Y, roi.Radius) || radius <= 0)
                    throw new InvalidOperationException("圆形裁剪区域必须为正半径，且坐标绝对值不超过 10000000 px。");
                break;
            }
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                    || Math.Abs(point.X) > 10_000_000 || Math.Abs(point.Y) > 10_000_000))
                    throw new InvalidOperationException("多边形裁剪区域需要至少三个有效顶点，且顶点坐标绝对值不超过 10000000 px。");
                var doubledArea = points.Select((point, index) =>
                {
                    var next = points[(index + 1) % points.Count];
                    return point.X * next.Y - next.X * point.Y;
                }).Sum();
                if (Math.Abs(doubledArea) < 1e-6)
                    throw new InvalidOperationException("多边形裁剪区域的顶点不能共线，且必须围成有效面积。");
                break;
            }
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                if (strokes.Count == 0 || strokes.Count > 10_000)
                    throw new InvalidOperationException("笔刷裁剪区域需要 1..10000 笔有效笔迹。");
                var totalPoints = 0;
                foreach (var stroke in strokes)
                {
                    if (stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                        || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 512
                        || stroke.Points is null || stroke.Points.Count == 0)
                        throw new InvalidOperationException("笔刷笔迹需要有效模式、1..512 px 笔宽和至少一个点。");
                    totalPoints = checked(totalPoints + stroke.Points.Count);
                    if (totalPoints > 500_000)
                        throw new InvalidOperationException("笔刷裁剪区域最多允许 500000 个笔迹点。");
                    if (stroke.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                        || Math.Abs(point.X) > 10_000_000 || Math.Abs(point.Y) > 10_000_000))
                        throw new InvalidOperationException("笔刷点必须为有限坐标，且绝对值不超过 10000000 px。");
                }
                break;
            }
        }
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
