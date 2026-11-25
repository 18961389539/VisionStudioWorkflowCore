using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

internal static class JLVisionPointFitting
{
    private static JlXLDCont Curve(VisionPoint2D[] points)
    {
        using var rows = new JlTuple(points.Select(p => p.Y).ToArray()); using var cols = new JlTuple(points.Select(p => p.X).ToArray());
        var curve = new JlXLDCont();
        try { curve.GenContourPolygonXld(rows, cols); return curve; }
        catch { curve.Dispose(); throw; }
    }
    public static VisionLine2D Line(VisionPoint2D[] points)
    {
        if (points.Length < 2) throw new InvalidOperationException("A line fit requires at least two points.");
        if (points.All(p => p == points[0])) throw new InvalidOperationException("A line cannot be fit when all points coincide.");
        using var curve = Curve(points);
        try
        {
            curve.FitLineContourXld("regression", -1, 0, 5, 2d, out double r1, out double c1, out double r2, out double c2, out double _, out double _, out double _);
            var first = new VisionPoint2D(c1, r1); var last = new VisionPoint2D(c2, r2);
            return c1 < c2 || c1 == c2 && r1 <= r2 ? new(first, last) : new(last, first);
        }
        catch (JlException ex) { throw new InvalidOperationException($"JLVision line fit failed: {ex.Message}", ex); }
    }
    public static VisionCircle Circle(VisionPoint2D[] points)
    {
        if (points.Length < 3) throw new InvalidOperationException("A circle fit requires at least three points.");
        using var curve = Curve(points);
        try
        {
            curve.FitCircleContourXld("geometric", -1, 0d, 0, 3, 2d, out double row, out double col, out double radius, out double _, out double _, out string _);
            if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(row) || !double.IsFinite(col)) throw new InvalidOperationException("JLVision circle fit is degenerate.");
            return new(col, row, radius);
        }
        catch (JlException ex) { throw new InvalidOperationException($"JLVision circle fit failed: {ex.Message}", ex); }
    }
}
