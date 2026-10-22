using OpenCvSharp;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Shared support for the generic JLVision operator nodes: a normalized 8-bit preview for
/// real-value operator output, region/contour overlay rendering for debug views, and a
/// single diagnostics entry point. Keeping this in one place avoids a copy of the same
/// render/guard code in every JLVision node.
/// </summary>
public static class JLVisionNodeSupport
{
    internal sealed record PreviewFrame(double Scale, double OffsetX, double OffsetY, int Width, int Height)
    {
        internal Point Project(VisionPoint2D point) => new(
            (int)Math.Clamp(Math.Round(point.X * Scale + OffsetX), 0, Width - 1),
            (int)Math.Clamp(Math.Round(point.Y * Scale + OffsetY), 0, Height - 1));
    }

    /// <summary>Bound debug canvases independently of CAD/world coordinate magnitude, retaining negative coordinates.</summary>
    internal static PreviewFrame PreviewViewport(IEnumerable<VisionPoint2D> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new InvalidOperationException("预览坐标必须是有限数值。");
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
        }
        if (double.IsPositiveInfinity(minX)) minX = minY = maxX = maxY = 0;
        var spanX = maxX - minX; var spanY = maxY - minY;
        if (!double.IsFinite(spanX) || !double.IsFinite(spanY)) throw new InvalidOperationException("预览坐标范围过大。");
        const int padding = 8;
        var scale = Math.Min(1, Math.Min(1008 / Math.Max(1, spanX), 752 / Math.Max(1, spanY)));
        return new(scale, padding - minX * scale, padding - minY * scale,
            Math.Clamp((int)Math.Ceiling(spanX * scale) + 2 * padding, 32, 1024),
            Math.Clamp((int)Math.Ceiling(spanY * scale) + 2 * padding, 32, 768));
    }

    internal static void DrawPreviewContours(Mat canvas, IReadOnlyList<VisionContour> contours, Scalar color, PreviewFrame frame)
    {
        foreach (var contour in contours)
        {
            var points = contour.Points.Select(frame.Project).ToArray();
            if (points.Length == 1) Cv2.Circle(canvas, points[0], 2, color, -1);
            else if (points.Length > 1) Cv2.Polylines(canvas, new[] { points }, contour.Closed, color, 1, LineTypes.AntiAlias);
        }
    }

    /// <summary>Build the standard diagnostics payload shared by the JLVision operator nodes.</summary>
    public static string Diagnostics(string nodeType, string level, int warningCount, params (string Key, object? Value)[] fields)
        => NodeCompletionGuards.BuildDiagnostics(nodeType, level, warningCount, 1, fields);

    /// <summary>
    /// Render a displayable 8-bit preview of an operator result: 8-bit output is copied as is,
    /// real/integer output is min-max stretched to 0..255 (equivalent to a HALCON operator
    /// result preview in the graphics window).
    /// </summary>
    public static Mat Preview(Mat source)
    {
        if (source.Empty())
            throw new InvalidOperationException("Cannot render a preview for an empty image.");
        if (source.Depth() == MatType.CV_8U)
            return source.Clone();
        var target = source.Channels() == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3;
        using var scaled = new Mat();
        var preview = new Mat();
        try
        {
            Cv2.Normalize(source, scaled, 0, 255, NormTypes.MinMax);
            scaled.ConvertTo(preview, target);
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    /// <summary>Create a black BGR canvas used to draw overlay visuals for scalar-only nodes.</summary>
    public static Mat Canvas(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new InvalidOperationException($"Canvas size must be positive, got {width}x{height}.");
        return new Mat(height, width, MatType.CV_8UC3, Scalar.All(0));
    }

    /// <summary>Convert a background image to BGR so overlays can be drawn on it in colour.</summary>
    public static Mat OverlayOn(Mat background)
    {
        if (background.Empty())
            throw new InvalidOperationException("Overlay background must be non-empty.");
        var bgr = new Mat();
        if (background.Channels() == 1)
            Cv2.CvtColor(background, bgr, ColorConversionCodes.GRAY2BGR);
        else
            background.CopyTo(bgr);
        return bgr;
    }

    /// <summary>Draw a platform region as filled run rectangles onto a BGR canvas.</summary>
    public static Mat DrawRegionRuns(Mat canvas, VisionRegion region, Scalar color)
    {
        if (region is null)
            throw new InvalidOperationException("Region is required to draw a region overlay.");
        foreach (var component in region.Components)
            foreach (var run in component.Runs)
                Cv2.Rectangle(canvas,
                    new Rect(run.StartColumn, run.Row, Math.Max(1, run.EndColumn - run.StartColumn + 1), 1),
                    color, -1);
        return canvas;
    }

    /// <summary>Draw a list of platform contours as polylines onto a BGR canvas.</summary>
    public static Mat DrawContours(Mat canvas, IReadOnlyList<VisionContour> contours, Scalar color)
    {
        if (contours is null)
            throw new InvalidOperationException("Contours are required to draw a contour overlay.");
        foreach (var contour in contours)
        {
            if (contour.Points.Count < 2) continue;
            var points = contour.Points.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
            Cv2.Polylines(canvas, new[] { points }, contour.Closed, color, 1, LineTypes.AntiAlias);
        }
        return canvas;
    }
}
