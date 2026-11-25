// 2026-11-25 从 GeometryToleranceNodes.cs 下沉到算法层：
// 算法实现（JLVisionMatching / ContourThicknessMeasurement）需要它，
// 它却不该反过来依赖引擎的节点目录。

using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class ToleranceGeometry
{
    public const int MaxPoints = 20000;
    public static void Finite(params double[] values)
    {
        if (values.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100_000_000))
            throw new InvalidOperationException("Geometry coordinates and lengths must be finite within +/-100000000 px.");
    }
    public static VisionCircle Circle(NodeExecutionContext context, string port)
    {
        var circle = context.Require<VisionCircle>(port); Finite(circle.X, circle.Y, circle.Radius);
        if (circle.Radius <= 0) throw new InvalidOperationException("Circle radius must be positive.");
        return circle;
    }
    public static (VisionLine2D Line, double X, double Y, double Length) Line(NodeExecutionContext context, string port)
        => Line(context.Require<VisionLine2D>(port));
    public static (VisionLine2D Line, double X, double Y, double Length) Line(VisionLine2D line)
    {
        Finite(line.Start.X, line.Start.Y, line.End.X, line.End.Y);
        var length = MeasurementMath.Hypot(line.End.X - line.Start.X, line.End.Y - line.Start.Y);
        if (length < 1e-9) throw new InvalidOperationException("A nonzero line segment is required.");
        return (line, (line.End.X - line.Start.X) / length, (line.End.Y - line.Start.Y) / length, length);
    }
    public static VisionPoint2D[] Points(IReadOnlyList<VisionPoint2D> points, bool closed = true)
    {
        if (points.Count > MaxPoints) throw new InvalidOperationException("Contour point budget exceeded (20000).");
        var result = new List<VisionPoint2D>();
        foreach (var point in points)
        {
            Finite(point.X, point.Y);
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        }
        if (closed && result.Count > 1 && result[^1] == result[0]) result.RemoveAt(result.Count - 1);
        return result.ToArray();
    }
    public static VisionPoint2D[] Select(NodeExecutionContext context, NodeDefinition node, string port = "contours", string parameter = "index", bool closed = true)
    {
        var list = context.Require<IReadOnlyList<VisionContour>>(port); var index = node.GetInt(parameter, 0);
        if (index < 0 || index > 1999 || list.Count > 2000) throw new InvalidOperationException("Contour index must be 0..1999 and list budget is 2000 contours.");
        if (index >= list.Count) return [];
        var item = list[index];
        if (item?.Points is null) throw new InvalidOperationException("Contour and its points must be non-null.");
        return Points(item.Points, closed);
    }
    public static JlXLDCont Curve(IReadOnlyList<VisionPoint2D> points, bool closed)
    {
        var data = closed && points.Count > 1 && points[^1] != points[0] ? points.Append(points[0]).ToArray() : points.ToArray();
        using var rows = new JlTuple(data.Select(p => p.Y).ToArray()); using var cols = new JlTuple(data.Select(p => p.X).ToArray());
        var curve = new JlXLDCont();
        try { curve.GenContourPolygonXld(rows, cols); return curve; }
        catch { curve.Dispose(); throw; }
    }
    public static NodeExecutorResult Missing(int count, string reason, Dictionary<string, VisionValue>? extra = null, string? nodeType = null)
    {
        var outputs = extra ?? new(); outputs["found"] = VisionValue.Boolean(false); outputs["sampleCount"] = VisionValue.Integer(count);
        var diag = CoordinateValidation.Diagnostics(nodeType ?? "measure.tolerance", ("found", false), ("sampleCount", count), ("reason", reason));
        outputs["diagnostics"] = VisionValue.String(diag);
        return new(outputs, new Dictionary<string, object?> { ["found"] = false, ["sampleCount"] = count, ["reason"] = reason, ["unit"] = "px", ["diagnostics"] = diag });
    }
    public static VisionOverlay Segment(string id, VisionLine2D line, string? label = null)
        => new(id, VisionOverlayType.Line, X: line.Start.X, Y: line.Start.Y, X2: line.End.X, Y2: line.End.Y, Label: label);
}
