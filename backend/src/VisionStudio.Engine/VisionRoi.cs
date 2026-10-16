using OpenCvSharp;

namespace VisionStudio.Engine;

public static class VisionRoiOpenCvExtensions
{
    private const double MaxCoordinate = 10_000_000;
    internal const int MaxPolygonPoints = 500_000;

    public static Mat BuildMask(this VisionRoi roi, Size size)
    {
        ArgumentNullException.ThrowIfNull(roi);
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException("ROI mask dimensions must be positive.");
        ValidatePolygonRasterInput(roi);

        var mask = new Mat(size.Height, size.Width, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            switch (roi.Type)
            {
                case VisionRoiType.Rectangle:
                {
                    var x = roi.X ?? 0; var y = roi.Y ?? 0;
                    var w = roi.Width ?? 0; var h = roi.Height ?? 0;
                    CheckCoordinate(x); CheckCoordinate(y); CheckCoordinate(w); CheckCoordinate(h);
                    if (w < 0 || h < 0) throw new InvalidOperationException("ROI dimensions must be non-negative.");
                    // Intersect the original rectangle with the canvas. Moving its origin first
                    // would enlarge partially visible regions and create regions for off-screen ROIs.
                    var left = (int)Math.Clamp(Math.Ceiling(x), 0, size.Width);
                    var top = (int)Math.Clamp(Math.Ceiling(y), 0, size.Height);
                    var right = (int)Math.Clamp(Math.Ceiling(x + w), 0, size.Width);
                    var bottom = (int)Math.Clamp(Math.Ceiling(y + h), 0, size.Height);
                    if (right > left && bottom > top)
                        Cv2.Rectangle(mask, new Rect(left, top, right - left, bottom - top), Scalar.All(255), -1);
                    break;
                }
                case VisionRoiType.Circle:
                {
                    var x = RasterCoordinate(roi.X ?? 0);
                    var y = RasterCoordinate(roi.Y ?? 0);
                    var radius = RasterCoordinate(roi.Radius ?? 0);
                    if (radius < 0) throw new InvalidOperationException("ROI radius must be non-negative.");
                    if (radius > 0)
                        Cv2.Circle(mask, new Point(x, y), radius, Scalar.All(255), -1, LineTypes.AntiAlias);
                    break;
                }
                case VisionRoiType.Polygon:
                {
                    var points = (roi.Points ?? []).Select(p => new Point(RasterCoordinate(p.X), RasterCoordinate(p.Y))).ToArray();
                    if (points.Length >= 3)
                        Cv2.FillPoly(mask, new[] { points }, Scalar.All(255), LineTypes.AntiAlias);
                    break;
                }
                case VisionRoiType.Brush:
                    DrawBrushStrokes(mask, roi.Strokes);
                    break;
                default:
                    throw new InvalidOperationException($"ROI type '{roi.Type}' is not supported.");
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    /// <summary>Reject malformed or empty regions when an image measurement expects a usable ROI.</summary>
    public static void ValidateForMeasurement(this VisionRoi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ValidateMaskGeometry(roi);
    }

    private static void ValidateMaskGeometry(VisionRoi roi)
    {
        static bool IsBounded(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxCoordinate;

        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
                if (roi.X is not { } x || roi.Y is not { } y || roi.Width is not { } width || roi.Height is not { } height
                    || !IsBounded(x) || !IsBounded(y) || !IsBounded(width) || !IsBounded(height)
                    || width <= 0 || height <= 0 || !IsBounded(x + width) || !IsBounded(y + height))
                    throw new InvalidOperationException("Rectangle ROI requires finite coordinates within ±10000000 px and positive width/height whose far edges remain within that range.");
                break;

            case VisionRoiType.Circle:
                if (roi.X is not { } cx || roi.Y is not { } cy || roi.Radius is not { } radius
                    || !IsBounded(cx) || !IsBounded(cy) || !IsBounded(radius) || radius <= 0)
                    throw new InvalidOperationException("Circle ROI requires finite center coordinates within ±10000000 px and a positive radius within that range.");
                break;

            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                if (points.Count > MaxPolygonPoints)
                    throw new InvalidOperationException($"Polygon ROI exceeds the {MaxPolygonPoints}-point limit.");
                if (points.Count < 3
                    || points.Any(point => point is null || !IsBounded(point.X) || !IsBounded(point.Y)))
                    throw new InvalidOperationException($"Polygon ROI requires 3..{MaxPolygonPoints} finite vertices within ±10000000 px.");
                var doubledArea = points.Select((point, index) =>
                {
                    var next = points[(index + 1) % points.Count];
                    return point.X * next.Y - next.X * point.Y;
                }).Sum();
                if (!double.IsFinite(doubledArea) || Math.Abs(doubledArea) < 1e-6)
                    throw new InvalidOperationException("Polygon ROI vertices must enclose a finite non-zero area.");
                break;
            }

            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                if (strokes.Count is < 1 or > 10_000 || strokes.Any(stroke => stroke is null)
                    || !strokes.Any(stroke => stroke is not null && stroke.Mode == VisionRoiStrokeMode.Add))
                    throw new InvalidOperationException("Brush ROI requires 1..10000 strokes and at least one add stroke.");
                var totalPoints = 0;
                foreach (var stroke in strokes)
                {
                    if (stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                        || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024
                        || stroke.Points is null || stroke.Points.Count == 0
                        || stroke.Points.Any(point => point is null || !IsBounded(point.X) || !IsBounded(point.Y)))
                        throw new InvalidOperationException("Brush ROI strokes require a valid mode, finite points within ±10000000 px, and a width between 0 and 1024 px.");
                    totalPoints = checked(totalPoints + stroke.Points.Count);
                    if (totalPoints > 500_000)
                        throw new InvalidOperationException("Brush ROI may contain at most 500000 points.");
                }
                break;
            }

            default:
                throw new InvalidOperationException($"ROI type '{roi.Type}' is not supported.");
        }
    }

    private static void ValidatePolygonRasterInput(VisionRoi roi)
    {
        if (roi.Type != VisionRoiType.Polygon) return;
        var points = roi.Points ?? [];
        if (points.Count > MaxPolygonPoints)
            throw new InvalidOperationException($"Polygon ROI exceeds the {MaxPolygonPoints}-point limit.");
        if (points.Any(point => point is null))
            throw new InvalidOperationException("Polygon ROI points must be finite.");
    }

    internal static void DrawBrushStrokes(Mat target, IReadOnlyList<VisionRoiBrushStroke>? strokes, double offsetX = 0, double offsetY = 0)
    {
        if (target.Empty()) return;
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            throw new InvalidOperationException("Brush ROI offsets must be finite.");
        if (strokes is null || strokes.Count == 0) return;
        if (strokes.Count > 10_000) throw new InvalidOperationException("Brush ROI exceeds the 10000-stroke limit.");

        var totalPoints = 0;
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase))
                throw new InvalidOperationException("Brush ROI stroke mode must be Add or Erase.");
            if (!double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024)
                throw new InvalidOperationException("Brush ROI width must be finite and in (0, 1024] pixels.");
            if (stroke.Points is null || stroke.Points.Count == 0)
                throw new InvalidOperationException("Every brush ROI stroke needs at least one point.");
            totalPoints = checked(totalPoints + stroke.Points.Count);
            if (totalPoints > 500_000) throw new InvalidOperationException("Brush ROI exceeds the 500000-point limit.");

            var points = stroke.Points.Select(point =>
            {
                if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    throw new InvalidOperationException("Brush ROI points must be finite.");
                // OpenCV clips drawing primitives; preserve off-canvas endpoints so strokes
                // and erasers cannot be displaced onto the image border.
                return new Point(RasterCoordinate(point.X - offsetX), RasterCoordinate(point.Y - offsetY));
            }).ToArray();
            var color = stroke.Mode == VisionRoiStrokeMode.Add ? Scalar.All(255) : Scalar.All(0);
            var thickness = Math.Clamp((int)Math.Round(stroke.Width), 1, 1024);
            var radius = Math.Max(0, thickness / 2);
            foreach (var point in points)
                Cv2.Circle(target, point, radius, color, -1, LineTypes.AntiAlias);
            for (var index = 1; index < points.Length; index++)
                Cv2.Line(target, points[index - 1], points[index], color, thickness, LineTypes.AntiAlias);
        }
    }

    private static void CheckCoordinate(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 10_000_000)
            throw new InvalidOperationException("ROI coordinates and sizes must be finite within +/-10000000 pixels.");
    }

    private static int RasterCoordinate(double value)
    {
        CheckCoordinate(value);
        return (int)Math.Round(value);
    }
}
