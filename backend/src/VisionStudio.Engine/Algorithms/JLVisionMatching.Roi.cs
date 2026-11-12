using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
// ROI 边界、掩码填充、遮挡区（clutter）与软忽略权重。
internal static partial class JLVisionMatching
{
    private static Rect RoiBounds(VisionRoi roi)
    {
        double left, top, right, bottom;
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                left = roi.X ?? 0; top = roi.Y ?? 0;
                var width = roi.Width ?? 0; var height = roi.Height ?? 0;
                if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                    throw new InvalidOperationException("Rectangle ROI needs finite coordinates and positive width/height.");
                right = left + width; bottom = top + height;
                break;
            case VisionRoiType.Circle:
                var centerX = roi.X ?? 0; var centerY = roi.Y ?? 0; var radius = roi.Radius ?? 0;
                if (!double.IsFinite(centerX) || !double.IsFinite(centerY) || !double.IsFinite(radius) || radius <= 0)
                    throw new InvalidOperationException("Circle ROI needs finite coordinates and a positive radius.");
                left = centerX - radius; top = centerY - radius; right = centerX + radius; bottom = centerY + radius;
                break;
            case VisionRoiType.Polygon:
                var points = roi.Points ?? [];
                if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    throw new InvalidOperationException("Polygon ROI needs at least three finite points.");
                left = points.Min(point => point.X); top = points.Min(point => point.Y);
                right = points.Max(point => point.X); bottom = points.Max(point => point.Y);
                if (right <= left || bottom <= top) throw new InvalidOperationException("Polygon ROI must enclose a non-empty area.");
                break;
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                if (strokes.Count is < 1 or > 10_000) throw new InvalidOperationException("Brush ROI needs 1..10000 strokes.");
                var additions = strokes.Where(stroke => stroke.Mode == VisionRoiStrokeMode.Add).ToArray();
                if (additions.Length == 0) throw new InvalidOperationException("Brush ROI needs at least one add stroke.");
                var brushPoints = new List<(VisionRoiPoint Point, double Radius)>();
                var pointCount = 0;
                foreach (var stroke in strokes)
                {
                    if (stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                        || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024
                        || stroke.Points is null || stroke.Points.Count == 0)
                        throw new InvalidOperationException("Brush ROI strokes need a valid mode, positive width and points.");
                    pointCount = checked(pointCount + stroke.Points.Count);
                    if (pointCount > 500_000) throw new InvalidOperationException("Brush ROI exceeds the 500000-point limit.");
                    if (stroke.Mode != VisionRoiStrokeMode.Add) continue;
                    foreach (var point in stroke.Points)
                    {
                        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new InvalidOperationException("Brush ROI points must be finite.");
                        brushPoints.Add((point, stroke.Width / 2));
                    }
                }
                left = brushPoints.Min(item => item.Point.X - item.Radius);
                top = brushPoints.Min(item => item.Point.Y - item.Radius);
                right = brushPoints.Max(item => item.Point.X + item.Radius);
                bottom = brushPoints.Max(item => item.Point.Y + item.Radius);
                break;
            }
            default:
                throw new InvalidOperationException("Unsupported ROI type.");
        }
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom)
            || left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue
            || right - left > int.MaxValue || bottom - top > int.MaxValue)
            throw new InvalidOperationException("ROI coordinates exceed the supported image-coordinate range.");
        var x = (int)Math.Floor(left); var y = (int)Math.Floor(top);
        var inclusiveBoundary = roi.Type is VisionRoiType.Circle or VisionRoiType.Polygon or VisionRoiType.Brush;
        var endX = (int)Math.Ceiling(right) + (inclusiveBoundary ? 1 : 0);
        var endY = (int)Math.Ceiling(bottom) + (inclusiveBoundary ? 1 : 0);
        return new Rect(x, y, Math.Max(0, endX - x), Math.Max(0, endY - y));
    }

    private static void FillRoiMask(Mat mask, VisionRoi? roi, Rect cropRect, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (roi is null) { mask.SetTo(Scalar.All(255)); return; }
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
            {
                var clipped = RoiBounds(roi) & cropRect;
                if (clipped.Width > 0 && clipped.Height > 0)
                    Cv2.Rectangle(mask, new Rect(clipped.X - cropRect.X, clipped.Y - cropRect.Y, clipped.Width, clipped.Height), Scalar.All(255), -1);
                break;
            }
            case VisionRoiType.Circle:
            {
                var center = new Point((int)Math.Round((roi.X ?? 0) - cropRect.X), (int)Math.Round((roi.Y ?? 0) - cropRect.Y));
                var radius = (int)Math.Round(roi.Radius ?? 0);
                Cv2.Circle(mask, center, radius, Scalar.All(255), -1);
                break;
            }
            case VisionRoiType.Polygon:
            {
                var roiPoints = roi.Points ?? [];
                var points = new Point[roiPoints.Count];
                for (var index = 0; index < points.Length; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var point = roiPoints[index];
                    points[index] = new Point((int)Math.Round(point.X - cropRect.X), (int)Math.Round(point.Y - cropRect.Y));
                }
                cancellationToken.ThrowIfCancellationRequested();
                Cv2.FillPoly(mask, [points], Scalar.All(255));
                break;
            }
            case VisionRoiType.Brush:
                VisionRoiOpenCvExtensions.DrawBrushStrokes(mask, roi.Strokes, cropRect.X, cropRect.Y, cancellationToken);
                break;
        }
    }

    private static void ApplyShapeModelClutter(JlShapeModel model, JlImage trainingModelImage, int imageWidth, int imageHeight,
        VisionRoi clutterRoi, double cropOffsetX, double cropOffsetY, int clutterContrast, int minContrast, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        model.FindShapeModel(trainingModelImage, 0, 0, .3, 1, .5, "least_squares", 0, .8,
            out var rows, out var cols, out var angles, out _);
        using (rows) using (cols) using (angles)
        {
            if (rows.Length == 0) throw new InvalidOperationException("Clutter ROI could not be aligned to the trained model in its source image.");
            var transform = new JlHomMat2D();
            transform.VectorAngleToRigid(0, 0, 0, rows[0].D + cropOffsetY, cols[0].D + cropOffsetX, angles[0].D);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyShapeModelClutter(model, imageWidth, imageHeight, clutterRoi, transform, clutterContrast, minContrast, cancellationToken);
        }
    }

    private static void ApplyShapeModelClutter(JlShapeModel model, int imageWidth, int imageHeight, VisionRoi clutterRoi,
        JlHomMat2D modelToImage, int clutterContrast, int minContrast, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clutterContrast < minContrast)
            throw new InvalidOperationException("Clutter Contrast must be at least the Minimum Feature Contrast used to train the model.");
        using var mask = clutterRoi.BuildMask(new Size(imageWidth, imageHeight), cancellationToken);
        if (Cv2.CountNonZero(mask) == 0)
            throw new InvalidOperationException("Clutter ROI must overlap the training image and contain at least one pixel.");
        using var clutterImage = JLVisionImages.FromMat(mask);
        using var clutterRegion = clutterImage.Threshold(1d, 255d);
        using var names = new JlTuple();
        using var values = new JlTuple();
        cancellationToken.ThrowIfCancellationRequested();
        try { model.SetShapeModelClutter(clutterRegion, modelToImage, clutterContrast, names, values); }
        catch (JlOperatorException ex) when (ex.Message.Contains("8515", StringComparison.Ordinal))
        { throw new InvalidOperationException("Clutter ROI is too close to the trained model contours. Move it farther from the part edges.", ex); }
    }

    private static bool Contains(VisionRoi roi, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                return x >= roi.X!.Value && y >= roi.Y!.Value && x < roi.X.Value + roi.Width!.Value && y < roi.Y.Value + roi.Height!.Value;
            case VisionRoiType.Circle:
            {
                var dx = x - roi.X!.Value; var dy = y - roi.Y!.Value; var radius = roi.Radius!.Value;
                return dx * dx + dy * dy <= radius * radius;
            }
            case VisionRoiType.Polygon:
            {
                var inside = false; var points = roi.Points ?? [];
                for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                {
                    var a = points[i]; var b = points[j];
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                }
                return inside;
            }
            case VisionRoiType.Brush:
            {
                var selected = false;
                foreach (var stroke in roi.Strokes ?? [])
                {
                    if (stroke.Points.Count == 0) continue;
                    var radiusSquared = stroke.Width * stroke.Width / 4;
                    var included = false;
                    for (var index = 0; index < stroke.Points.Count; index++)
                    {
                        var a = stroke.Points[index];
                        if (index == 0 && DistanceSquared(x, y, a.X, a.Y) <= radiusSquared) included = true;
                        if (index == 0) continue;
                        var b = stroke.Points[index - 1];
                        var dx = a.X - b.X; var dy = a.Y - b.Y;
                        var lengthSquared = dx * dx + dy * dy;
                        var t = lengthSquared <= 1e-12 ? 0 : Math.Clamp(((x - b.X) * dx + (y - b.Y) * dy) / lengthSquared, 0, 1);
                        if (DistanceSquared(x, y, b.X + t * dx, b.Y + t * dy) <= radiusSquared) included = true;
                    }
                    if (included) selected = stroke.Mode == VisionRoiStrokeMode.Add;
                }
                return selected;
            }
            default: return false;
        }

        static double DistanceSquared(double x1, double y1, double x2, double y2)
        {
            var dx = x1 - x2; var dy = y1 - y2;
            return dx * dx + dy * dy;
        }
    }

    /// <summary>
    /// Binary ignore paint (0/255) → float weight map: 1 outside, <paramref name="softWeight"/> inside.
    /// Optional Gaussian feather mixes the boundary (honest approximate soft edge).
    /// </summary>
    private static Mat BuildIgnoreWeightMap(Mat ignorePaint, double softWeight, int featherPx)
    {
        using var binary = new Mat();
        ignorePaint.ConvertTo(binary, MatType.CV_32FC1, 1 / 255d);
        using var inside = new Mat();
        Cv2.Multiply(binary, 1d - softWeight, inside);
        var weight = new Mat(ignorePaint.Rows, ignorePaint.Cols, MatType.CV_32FC1, Scalar.All(1));
        Cv2.Subtract(weight, inside, weight);
        if (featherPx > 0)
        {
            var k = featherPx * 2 + 1;
            Cv2.GaussianBlur(weight, weight, new Size(k, k), featherPx / 2d);
        }
        return weight;
    }

    private static void ApplySoftIgnoreLerp(Mat image, Mat weight01, double mean)
    {
        using var imgF = new Mat();
        image.ConvertTo(imgF, MatType.CV_32FC1);
        using var centered = new Mat();
        Cv2.Subtract(imgF, mean, centered);
        Cv2.Multiply(centered, weight01, centered);
        Cv2.Add(centered, mean, imgF);
        imgF.ConvertTo(image, image.Type());
    }

    private static double SampleIgnoreWeight(VisionRoi ignoreRoi, double x, double y, double softWeight, int featherPx)
    {
        if (featherPx <= 0)
            return Contains(ignoreRoi, x, y) ? softWeight : 1d;
        if (ignoreRoi.Type == VisionRoiType.Rectangle && ignoreRoi.X is { } rx && ignoreRoi.Y is { } ry
            && ignoreRoi.Width is { } rw && ignoreRoi.Height is { } rh && rw > 0 && rh > 0)
        {
            var dx = x < rx ? rx - x : (x > rx + rw ? x - (rx + rw) : 0d);
            var dy = y < ry ? ry - y : (y > ry + rh ? y - (ry + rh) : 0d);
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist <= 0) return softWeight;
            if (dist >= featherPx) return 1d;
            var t = dist / featherPx;
            return softWeight + (1d - softWeight) * t;
        }
        return Contains(ignoreRoi, x, y) ? softWeight : 1d;
    }
}
