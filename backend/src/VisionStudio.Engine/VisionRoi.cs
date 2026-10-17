using OpenCvSharp;

namespace VisionStudio.Engine;

public static class VisionRoiOpenCvExtensions
{
    private const double MaxCoordinate = 10_000_000;
    internal const int MaxPolygonPoints = 500_000;

    public static Mat BuildMask(this VisionRoi roi, Size size, CancellationToken cancellationToken = default)
        => BuildMaskForRegion(roi, size, 0, 0, cancellationToken);

    /// <summary>Rasterize the ROI into a canvas whose top-left corresponds to an image-space origin.</summary>
    internal static Mat BuildMaskForRegion(VisionRoi roi, Size size, int originX, int originY, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roi);
        cancellationToken.ThrowIfCancellationRequested();
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException("ROI mask dimensions must be positive.");
        ValidatePolygonRasterInput(roi, cancellationToken);

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
                    var left = (int)Math.Clamp(Math.Ceiling(x) - originX, 0, size.Width);
                    var top = (int)Math.Clamp(Math.Ceiling(y) - originY, 0, size.Height);
                    var right = (int)Math.Clamp(Math.Ceiling(x + w) - originX, 0, size.Width);
                    var bottom = (int)Math.Clamp(Math.Ceiling(y + h) - originY, 0, size.Height);
                    if (right > left && bottom > top)
                        Cv2.Rectangle(mask, new Rect(left, top, right - left, bottom - top), Scalar.All(255), -1);
                    break;
                }
                case VisionRoiType.Circle:
                {
                    var x = checked(RasterCoordinate(roi.X ?? 0) - originX);
                    var y = checked(RasterCoordinate(roi.Y ?? 0) - originY);
                    var sourceRadius = roi.Radius ?? 0;
                    CheckCoordinate(sourceRadius);
                    if (sourceRadius < 0) throw new InvalidOperationException("ROI radius must be non-negative.");
                    var radius = RasterCoordinate(sourceRadius);
                    if (sourceRadius > 0)
                    {
                        // A valid subpixel circle must still select image pixels. OpenCV's integer
                        // circle rasterizer treats radius 0 as empty, so retain a one-pixel minimum.
                        radius = Math.Max(1, radius);
                        Cv2.Circle(mask, new Point(x, y), radius, Scalar.All(255), -1, LineTypes.AntiAlias);
                    }
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
                        points[index] = new Point(checked(RasterCoordinate(point.X) - originX), checked(RasterCoordinate(point.Y) - originY));
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (points.Length >= 3)
                        Cv2.FillPoly(mask, new[] { points }, Scalar.All(255), LineTypes.AntiAlias);
                    break;
                }
                case VisionRoiType.Brush:
                    DrawBrushStrokes(mask, roi.Strokes, originX, originY, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"ROI type '{roi.Type}' is not supported.");
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    /// <summary>Reject malformed or empty regions when an image measurement expects a usable ROI.</summary>
    public static void ValidateForMeasurement(this VisionRoi roi, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ValidateMaskGeometry(roi, cancellationToken);
    }

    private static void ValidateMaskGeometry(VisionRoi roi, CancellationToken cancellationToken)
    {
        static bool IsBounded(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxCoordinate;
        cancellationToken.ThrowIfCancellationRequested();

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
                if (points.Count < 3)
                    throw new InvalidOperationException($"Polygon ROI requires 3..{MaxPolygonPoints} finite vertices within ±10000000 px.");
                var doubledArea = 0d;
                for (var index = 0; index < points.Count; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var point = points[index];
                    if (point is null || !IsBounded(point.X) || !IsBounded(point.Y))
                        throw new InvalidOperationException($"Polygon ROI requires 3..{MaxPolygonPoints} finite vertices within ±10000000 px.");
                    var next = points[(index + 1) % points.Count];
                    if (next is null)
                        throw new InvalidOperationException($"Polygon ROI requires 3..{MaxPolygonPoints} finite vertices within ±10000000 px.");
                    doubledArea += point.X * next.Y - next.X * point.Y;
                }
                if (!double.IsFinite(doubledArea) || Math.Abs(doubledArea) < 1e-6)
                    throw new InvalidOperationException("Polygon ROI vertices must enclose a finite non-zero area.");
                break;
            }

            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                if (strokes.Count is < 1 or > 10_000)
                    throw new InvalidOperationException("Brush ROI requires 1..10000 strokes and at least one add stroke.");
                var totalPoints = 0;
                var hasAddStroke = false;
                for (var strokeIndex = 0; strokeIndex < strokes.Count; strokeIndex++)
                {
                    if ((strokeIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var stroke = strokes[strokeIndex];
                    if (stroke is null)
                        throw new InvalidOperationException("Brush ROI requires 1..10000 strokes and at least one add stroke.");
                    if (stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                        || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024
                        || stroke.Points is null || stroke.Points.Count == 0)
                        throw new InvalidOperationException("Brush ROI strokes require a valid mode, finite points within ±10000000 px, and a width between 0 and 1024 px.");
                    hasAddStroke |= stroke.Mode == VisionRoiStrokeMode.Add;
                    totalPoints = checked(totalPoints + stroke.Points.Count);
                    if (totalPoints > 500_000)
                        throw new InvalidOperationException("Brush ROI may contain at most 500000 points.");
                    for (var pointIndex = 0; pointIndex < stroke.Points.Count; pointIndex++)
                    {
                        if ((pointIndex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var point = stroke.Points[pointIndex];
                        if (point is null || !IsBounded(point.X) || !IsBounded(point.Y))
                            throw new InvalidOperationException("Brush ROI strokes require a valid mode, finite points within ±10000000 px, and a width between 0 and 1024 px.");
                    }
                }
                if (!hasAddStroke)
                    throw new InvalidOperationException("Brush ROI requires 1..10000 strokes and at least one add stroke.");
                break;
            }

            default:
                throw new InvalidOperationException($"ROI type '{roi.Type}' is not supported.");
        }
    }

    private static void ValidatePolygonRasterInput(VisionRoi roi, CancellationToken cancellationToken)
    {
        if (roi.Type != VisionRoiType.Polygon) return;
        var points = roi.Points ?? [];
        if (points.Count > MaxPolygonPoints)
            throw new InvalidOperationException($"Polygon ROI exceeds the {MaxPolygonPoints}-point limit.");
        for (var index = 0; index < points.Count; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (points[index] is null)
                throw new InvalidOperationException("Polygon ROI points must be finite.");
        }
    }

    internal static void DrawBrushStrokes(Mat target, IReadOnlyList<VisionRoiBrushStroke>? strokes, double offsetX = 0, double offsetY = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Empty()) return;
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            throw new InvalidOperationException("Brush ROI offsets must be finite.");
        if (offsetX != Math.Truncate(offsetX) || offsetY != Math.Truncate(offsetY)
            || offsetX < int.MinValue || offsetX > int.MaxValue || offsetY < int.MinValue || offsetY > int.MaxValue)
            throw new InvalidOperationException("Brush ROI offsets must be integer image coordinates.");
        var offsetPixelX = (int)offsetX;
        var offsetPixelY = (int)offsetY;
        if (strokes is null || strokes.Count == 0) return;
        if (strokes.Count > 10_000) throw new InvalidOperationException("Brush ROI exceeds the 10000-stroke limit.");

        var totalPoints = 0;
        for (var strokeIndex = 0; strokeIndex < strokes.Count; strokeIndex++)
        {
            if ((strokeIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var stroke = strokes[strokeIndex];
            if (stroke is null || stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase))
                throw new InvalidOperationException("Brush ROI stroke mode must be Add or Erase.");
            if (!double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024)
                throw new InvalidOperationException("Brush ROI width must be finite and in (0, 1024] pixels.");
            if (stroke.Points is null || stroke.Points.Count == 0)
                throw new InvalidOperationException("Every brush ROI stroke needs at least one point.");
            totalPoints = checked(totalPoints + stroke.Points.Count);
            if (totalPoints > 500_000) throw new InvalidOperationException("Brush ROI exceeds the 500000-point limit.");

            var points = new Point[stroke.Points.Count];
            for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                if ((pointIndex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var point = stroke.Points[pointIndex];
                if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    throw new InvalidOperationException("Brush ROI points must be finite.");
                // OpenCV clips drawing primitives; preserve off-canvas endpoints so strokes
                // and erasers cannot be displaced onto the image border.
                // Rasterize each point in its source-image frame before shifting to a crop.
                // Rounding after subtracting an odd origin changes half-pixel ties.
                points[pointIndex] = new Point(checked(RasterCoordinate(point.X) - offsetPixelX),
                    checked(RasterCoordinate(point.Y) - offsetPixelY));
            }
            var color = stroke.Mode == VisionRoiStrokeMode.Add ? Scalar.All(255) : Scalar.All(0);
            var thickness = Math.Clamp((int)Math.Round(stroke.Width), 1, 1024);
            var radius = Math.Max(0, thickness / 2);
            foreach (var point in points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cv2.Circle(target, point, radius, color, -1, LineTypes.AntiAlias);
            }
            for (var index = 1; index < points.Length; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                Cv2.Line(target, points[index - 1], points[index], color, thickness, LineTypes.AntiAlias);
            }
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
