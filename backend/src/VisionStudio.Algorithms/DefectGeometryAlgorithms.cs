using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class DefectGeometryAlgorithms
{
    internal static double LocalContrast(Mat image, VisionDetection detection)
    {
        var x = (int)Math.Floor(detection.BoundingBox.X);
        var y = (int)Math.Floor(detection.BoundingBox.Y);
        var width = (int)Math.Ceiling(detection.BoundingBox.Width);
        var height = (int)Math.Ceiling(detection.BoundingBox.Height);
        x = Math.Clamp(x, 0, Math.Max(0, image.Cols - 1));
        y = Math.Clamp(y, 0, Math.Max(0, image.Rows - 1));
        width = Math.Clamp(width, 0, image.Cols - x);
        height = Math.Clamp(height, 0, image.Rows - y);
        if (width < 1 || height < 1) return 0;
        using var roi = new Mat(image, new Rect(x, y, width, height));
        Cv2.MinMaxLoc(roi, out var min, out var max, out _, out _);
        return max - min;
    }

    internal static (double Length, double Width, double Angle) MeasureContour(IReadOnlyList<VisionPoint2D> contour)
    {
        var points = contour.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
        var rect = Cv2.MinAreaRect(points);
        var first = rect.Size.Width;
        var second = rect.Size.Height;
        var angle = (first >= second ? rect.Angle : rect.Angle + 90.0) % 180.0;
        if (angle < 0) angle += 180.0;
        return (Math.Max(first, second), Math.Min(first, second), angle);
    }
}
