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
        ValidateCropRoi(roi, cancellationToken);

        var candidateBounds = CandidateRasterBounds(roi, source.Size(), cancellationToken);
        if (candidateBounds.Width <= 0 || candidateBounds.Height <= 0)
            throw NoSelectedPixels(roi, source.Size());
        var useLocalRaster = CanRasterizeAtOffset(roi, candidateBounds.X, candidateBounds.Y, cancellationToken);
        var rasterOriginX = useLocalRaster ? candidateBounds.X : 0;
        var rasterOriginY = useLocalRaster ? candidateBounds.Y : 0;
        var rasterSize = useLocalRaster ? candidateBounds.Size : source.Size();
        using var rasterMask = useLocalRaster
            ? VisionRoiOpenCvExtensions.BuildMaskForRegion(roi, rasterSize, rasterOriginX, rasterOriginY, cancellationToken)
            : roi.BuildMask(source.Size(), cancellationToken);
        using var cropMask = JLVisionOperators.CropBinaryMask(rasterMask, out var localBounds, out var selectedPixels, cancellationToken);
        if (selectedPixels == 0)
            throw NoSelectedPixels(roi, source.Size());
        var bounds = new Rect(rasterOriginX + localBounds.X, rasterOriginY + localBounds.Y, localBounds.Width, localBounds.Height);
        cancellationToken.ThrowIfCancellationRequested();
        using var sourceCrop = new Mat(source, bounds);
        var croppedImage = new Mat(sourceCrop.Rows, sourceCrop.Cols, sourceCrop.Type(), Scalar.All(0));
        var maskImage = cropMask.Clone();
        try
        {
            // 非矩形 ROI 输出紧包围框；区域外清零。BGRA 输入会同时得到透明外区。
            cancellationToken.ThrowIfCancellationRequested();
            sourceCrop.CopyTo(croppedImage, cropMask);
            cancellationToken.ThrowIfCancellationRequested();
            var croppedRoi = TranslateRoiToCrop(roi, bounds.X, bounds.Y, cancellationToken);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(VisionImage.Own(croppedImage)),
                    ["roiMask"] = VisionValue.Image(VisionImage.Own(maskImage)),
                    ["croppedRoi"] = VisionValue.Roi(croppedRoi),
                    ["cropOrigin"] = VisionValue.Point(new VisionPoint2D(bounds.X, bounds.Y)),
                    ["transform"] = VisionValue.Transform(new VisionTransform2D(
                        "CurrentImage", "CropImage", "px",
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

    private static InvalidOperationException NoSelectedPixels(VisionRoi roi, Size imageSize)
    {
        var roiName = roi.Type switch
        {
            VisionRoiType.Rectangle => "矩形",
            VisionRoiType.Circle => "圆形",
            VisionRoiType.Polygon => "多边形",
            VisionRoiType.Brush => "笔刷",
            _ => roi.Type.ToString()
        };
        var guidance = roi.Type == VisionRoiType.Brush
            ? "请确认添加笔迹与图像范围有重叠、且未被后续擦除笔迹全部擦除；检查上游位姿/坐标变换。"
            : "请将 ROI 移入图像范围，或检查上游位姿/坐标变换。";
        return new InvalidOperationException(
            $"{roiName}裁剪区域在输入图像（{imageSize.Width}×{imageSize.Height}）中没有覆盖任何像素。{guidance}");
    }

    private static Rect CandidateRasterBounds(VisionRoi roi, Size imageSize, CancellationToken cancellationToken)
    {
        static int Clip(double value, int maximum) => (int)Math.Clamp(value, 0, maximum);
        static Rect ClipBounds(long left, long top, long right, long bottom, Size size)
        {
            var clippedLeft = (int)Math.Clamp(left, 0, size.Width);
            var clippedTop = (int)Math.Clamp(top, 0, size.Height);
            var clippedRight = (int)Math.Clamp(right, 0, size.Width);
            var clippedBottom = (int)Math.Clamp(bottom, 0, size.Height);
            return new Rect(clippedLeft, clippedTop, Math.Max(0, clippedRight - clippedLeft), Math.Max(0, clippedBottom - clippedTop));
        }

        cancellationToken.ThrowIfCancellationRequested();
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
            {
                var x = roi.X!.Value; var y = roi.Y!.Value;
                var width = roi.Width!.Value; var height = roi.Height!.Value;
                return new Rect(
                    Clip(Math.Ceiling(x), imageSize.Width), Clip(Math.Ceiling(y), imageSize.Height),
                    Math.Max(0, Clip(Math.Ceiling(x + width), imageSize.Width) - Clip(Math.Ceiling(x), imageSize.Width)),
                    Math.Max(0, Clip(Math.Ceiling(y + height), imageSize.Height) - Clip(Math.Ceiling(y), imageSize.Height)));
            }
            case VisionRoiType.Circle:
            {
                var centerX = (int)Math.Round(roi.X!.Value);
                var centerY = (int)Math.Round(roi.Y!.Value);
                var radius = (int)Math.Round(roi.Radius!.Value);
                const int antialiasPadding = 2;
                return ClipBounds(centerX - (long)radius - antialiasPadding,
                    centerY - (long)radius - antialiasPadding,
                    centerX + (long)radius + antialiasPadding + 1,
                    centerY + (long)radius + antialiasPadding + 1, imageSize);
            }
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                var minX = int.MaxValue; var minY = int.MaxValue;
                var maxX = int.MinValue; var maxY = int.MinValue;
                for (var index = 0; index < points.Count; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var x = (int)Math.Round(points[index].X);
                    var y = (int)Math.Round(points[index].Y);
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
                const int antialiasPadding = 2;
                return ClipBounds(minX - (long)antialiasPadding, minY - (long)antialiasPadding,
                    maxX + (long)antialiasPadding + 1, maxY + (long)antialiasPadding + 1, imageSize);
            }
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                var minX = int.MaxValue; var minY = int.MaxValue;
                var maxX = int.MinValue; var maxY = int.MinValue;
                var padding = 0;
                var pointIndex = 0;
                foreach (var stroke in strokes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var thickness = Math.Clamp((int)Math.Round(stroke.Width), 1, 1024);
                    padding = Math.Max(padding, thickness / 2 + 2);
                    // Erasers do not add selected pixels, but rasterizing them at the local
                    // canvas edge changes anti-alias blending. Include their full extent too.
                    foreach (var point in stroke.Points)
                    {
                        if ((pointIndex++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var x = (int)Math.Round(point.X);
                        var y = (int)Math.Round(point.Y);
                        minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    }
                }
                if (minX == int.MaxValue) return default;
                return ClipBounds(minX - (long)padding, minY - (long)padding,
                    maxX + (long)padding + 1, maxY + (long)padding + 1, imageSize);
            }
            default:
                throw new InvalidOperationException($"Unsupported crop ROI type '{roi.Type}'.");
        }
    }

    private static bool CanRasterizeAtOffset(VisionRoi roi, int originX, int originY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                return true;
            case VisionRoiType.Circle:
                return (long)Math.Round(roi.X!.Value) - originX is >= int.MinValue and <= int.MaxValue
                    && (long)Math.Round(roi.Y!.Value) - originY is >= int.MinValue and <= int.MaxValue;
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                for (var index = 0; index < points.Count; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var point = points[index];
                    if ((long)Math.Round(point.X) - originX is < int.MinValue or > int.MaxValue
                        || (long)Math.Round(point.Y) - originY is < int.MinValue or > int.MaxValue)
                        return false;
                }
                return true;
            }
            case VisionRoiType.Brush:
            {
                var pointIndex = 0;
                foreach (var stroke in roi.Strokes ?? [])
                    foreach (var point in stroke.Points)
                    {
                        if ((pointIndex++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        if ((long)Math.Round(point.X) - originX is < int.MinValue or > int.MaxValue
                            || (long)Math.Round(point.Y) - originY is < int.MinValue or > int.MaxValue)
                            return false;
                    }
                return true;
            }
            default:
                return false;
        }
    }

    private static VisionRoi TranslateRoiToCrop(VisionRoi roi, int cropX, int cropY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                return roi with { X = (roi.X ?? 0) - cropX, Y = (roi.Y ?? 0) - cropY };
            case VisionRoiType.Circle:
                return roi with
                {
                    X = Math.Round(roi.X ?? 0) - cropX,
                    Y = Math.Round(roi.Y ?? 0) - cropY,
                    Radius = (roi.Radius ?? 0) > 0 ? Math.Max(1, Math.Round(roi.Radius!.Value)) : 0
                };
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                var translated = new VisionRoiPoint[points.Count];
                for (var index = 0; index < translated.Length; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    translated[index] = new VisionRoiPoint(Math.Round(points[index].X) - cropX, Math.Round(points[index].Y) - cropY);
                }
                return roi with { Points = translated };
            }
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                var translatedStrokes = new VisionRoiBrushStroke[strokes.Count];
                for (var strokeIndex = 0; strokeIndex < translatedStrokes.Length; strokeIndex++)
                {
                    if ((strokeIndex & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var stroke = strokes[strokeIndex];
                    var points = stroke.Points;
                    var translatedPoints = new VisionRoiPoint[points.Count];
                    for (var pointIndex = 0; pointIndex < translatedPoints.Length; pointIndex++)
                    {
                        if ((pointIndex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        translatedPoints[pointIndex] = new VisionRoiPoint(
                            Math.Round(points[pointIndex].X) - cropX, Math.Round(points[pointIndex].Y) - cropY);
                    }
                    translatedStrokes[strokeIndex] = stroke with { Points = translatedPoints };
                }
                return roi with { Strokes = translatedStrokes };
            }
            default:
                throw new InvalidOperationException($"Unsupported crop ROI type '{roi.Type}'.");
        }
    }

    private static void ValidateCropRoi(VisionRoi roi, CancellationToken cancellationToken)
    {
        static bool Finite(params double?[] values) => values.All(value => value.HasValue && double.IsFinite(value.Value));
        static bool Bounded(params double?[] values) => values.All(value => value.HasValue && Math.Abs(value.Value) <= 10_000_000);
        cancellationToken.ThrowIfCancellationRequested();
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
                if (points.Count > VisionRoiOpenCvExtensions.MaxPolygonPoints)
                    throw new InvalidOperationException($"多边形裁剪区域最多允许 {VisionRoiOpenCvExtensions.MaxPolygonPoints} 个顶点。");
                if (points.Count < 3)
                    throw new InvalidOperationException("多边形裁剪区域需要至少三个有效顶点，且顶点坐标绝对值不超过 10000000 px。");
                var doubledArea = 0d;
                for (var index = 0; index < points.Count; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var point = points[index];
                    if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                        || Math.Abs(point.X) > 10_000_000 || Math.Abs(point.Y) > 10_000_000)
                        throw new InvalidOperationException("多边形裁剪区域需要至少三个有效顶点，且顶点坐标绝对值不超过 10000000 px。");
                    var next = points[(index + 1) % points.Count];
                    if (next is null)
                        throw new InvalidOperationException("多边形裁剪区域需要至少三个有效顶点，且顶点坐标绝对值不超过 10000000 px。");
                    doubledArea += point.X * next.Y - next.X * point.Y;
                }
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
                var hasAddStroke = false;
                for (var strokeIndex = 0; strokeIndex < strokes.Count; strokeIndex++)
                {
                    if ((strokeIndex & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var stroke = strokes[strokeIndex];
                    if (stroke is null)
                        throw new InvalidOperationException("笔刷裁剪区域需要 1..10000 笔有效笔迹。");
                    if (stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                        || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024
                        || stroke.Points is null || stroke.Points.Count == 0)
                        throw new InvalidOperationException("笔刷笔迹需要有效模式、1..1024 px 笔宽和至少一个点。");
                    hasAddStroke |= stroke.Mode == VisionRoiStrokeMode.Add;
                    totalPoints = checked(totalPoints + stroke.Points.Count);
                    if (totalPoints > 500_000)
                        throw new InvalidOperationException("笔刷裁剪区域最多允许 500000 个笔迹点。");
                    for (var pointIndex = 0; pointIndex < stroke.Points.Count; pointIndex++)
                    {
                        if ((pointIndex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var point = stroke.Points[pointIndex];
                        if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                            || Math.Abs(point.X) > 10_000_000 || Math.Abs(point.Y) > 10_000_000)
                            throw new InvalidOperationException("笔刷点必须为有限坐标，且绝对值不超过 10000000 px。");
                    }
                }
                if (!hasAddStroke)
                    throw new InvalidOperationException("笔刷裁剪区域必须至少包含一笔“添加”笔迹。");
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
