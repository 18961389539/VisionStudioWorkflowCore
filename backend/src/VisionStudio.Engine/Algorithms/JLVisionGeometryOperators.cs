using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static partial class JLVisionOperators
{
    internal readonly record struct ContourMoments(double M00, double M10, double M01);
    internal readonly record struct NativeLine(VisionPoint2D P1, VisionPoint2D P2);
    internal readonly record struct NativeCircle(VisionPoint2D Center, double Radius);

    internal static JlXLDCont Curve(IEnumerable<Point2f> points)
    {
        var items = points.ToArray();
        using var rows = new JlTuple(items.Select(p => (double)p.Y).ToArray());
        using var cols = new JlTuple(items.Select(p => (double)p.X).ToArray());
        var curve = new JlXLDCont();
        try { curve.GenContourPolygonXld(rows, cols); return curve; }
        catch { curve.Dispose(); throw; }
    }
    public static double ContourArea(IEnumerable<Point> points, bool oriented = false)
        => ContourArea(points.Select(p => new Point2f(p.X, p.Y)), oriented);
    public static double ContourArea(IEnumerable<Point2f> points, bool oriented = false)
    {
        var p = points.ToArray(); var twice = 0d;
        for (var i = 0; i < p.Length; i++) { var b = p[(i + 1) % p.Length]; twice += (double)p[i].X * b.Y - (double)b.X * p[i].Y; }
        return oriented ? twice / 2 : Math.Abs(twice / 2);
    }
    public static double ArcLength(IEnumerable<Point> points, bool closed)
    {
        var p = points.ToArray(); var result = 0d;
        for (var i = 1; i < p.Length; i++) result += Math.Sqrt(Math.Pow(p[i].X - p[i - 1].X, 2) + Math.Pow(p[i].Y - p[i - 1].Y, 2));
        if (closed && p.Length > 1) result += Math.Sqrt(Math.Pow(p[0].X - p[^1].X, 2) + Math.Pow(p[0].Y - p[^1].Y, 2));
        return result;
    }
    public static Rect BoundingRect(IEnumerable<Point> points)
    {
        var p = points.ToArray(); if (p.Length == 0) return default;
        var x = p.Min(p => p.X); var y = p.Min(p => p.Y);
        return new(x, y, p.Max(p => p.X) - x + 1, p.Max(p => p.Y) - y + 1);
    }
    public static Rect BoundingRect(IEnumerable<Point2f> points) => BoundingRect(points.Select(p => new Point((int)Math.Floor(p.X), (int)Math.Floor(p.Y))));
    public static ContourMoments Moments(IEnumerable<Point> points, bool binaryImage = false)
    {
        var p = points.ToArray(); var cross = 0d; var x = 0d; var y = 0d;
        for (var i = 0; i < p.Length; i++)
        { var a = p[i]; var b = p[(i + 1) % p.Length]; var term = (double)a.X * b.Y - (double)b.X * a.Y; cross += term; x += (a.X + b.X) * term; y += (a.Y + b.Y) * term; }
        if (Math.Abs(cross) < 1e-12) return default;
        var area = Math.Abs(cross) / 2;
        return new(area, area * x / (3 * cross), area * y / (3 * cross));
    }
    public static Point[] ConvexHull(IEnumerable<Point> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length <= 2) return sorted;
        double Cross(Point o, Point a, Point b) => (double)(a.X - o.X) * (b.Y - o.Y) - (double)(a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Point>();
        foreach (var p in sorted) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
        var boundary = hull.Count;
        for (var i = sorted.Length - 2; i >= 0; i--) { var p = sorted[i]; while (hull.Count > boundary && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
        hull.RemoveAt(hull.Count - 1); return hull.ToArray();
    }
    public static bool IsContourConvex(IEnumerable<Point2f> points)
    {
        var p = points.ToArray(); var sign = 0;
        for (var i = 0; i < p.Length; i++) { var a = p[i]; var b = p[(i + 1) % p.Length]; var c = p[(i + 2) % p.Length]; var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X); if (Math.Abs(cross) < 1e-9) continue; if (sign != 0 && sign != Math.Sign(cross)) return false; sign = Math.Sign(cross); }
        return sign != 0;
    }
    public static RotatedRect MinAreaRect(IEnumerable<Point> points) => MinAreaRect(points.Select(p => new Point2f(p.X, p.Y)));
    public static RotatedRect MinAreaRect(IEnumerable<Point2f> points)
    {
        var p = points.ToArray(); if (p.Length == 0) throw new InvalidOperationException("Cannot fit an empty contour.");
        using var curve = Curve(p);
        curve.SmallestRectangle2Xld(out double row, out double col, out double phi, out double l1, out double l2);
        return new(new Point2f((float)col, (float)row), new Size2f((float)(2 * l1), (float)(2 * l2)), (float)(-phi * 180 / Math.PI));
    }
    public static void MinEnclosingCircle(IEnumerable<Point> points, out Point2f center, out float radius)
    {
        var p = points.ToArray(); if (p.Length == 0) { center = default; radius = 0; return; }
        if (p.Length == 1) { center = new(p[0].X, p[0].Y); radius = 0; return; }
        using var curve = Curve(p.Select(p => new Point2f(p.X, p.Y)));
        curve.SmallestCircleXld(out double row, out double col, out double r); center = new((float)col, (float)row); radius = (float)r;
    }
    public static Point2f[] BoxPoints(RotatedRect rectangle)
    {
        var angle = rectangle.Angle * Math.PI / 180; var c = Math.Cos(angle); var s = Math.Sin(angle);
        return new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }.Select(p => new Point2f((float)(rectangle.Center.X + c * p.Item1 * rectangle.Size.Width / 2 - s * p.Item2 * rectangle.Size.Height / 2), (float)(rectangle.Center.Y + s * p.Item1 * rectangle.Size.Width / 2 + c * p.Item2 * rectangle.Size.Height / 2))).ToArray();
    }
    public static RotatedRect FitEllipse(IEnumerable<Point2f> points)
    {
        using var curve = Curve(points);
        curve.FitEllipseContourXld("fitzgibbon", -1, 0d, 0, 200, 3, 2d, out double row, out double col, out double phi, out double r1, out double r2, out double _, out double _, out string _);
        return new(new((float)col, (float)row), new((float)(2 * r1), (float)(2 * r2)), (float)(-phi * 180 / Math.PI));
    }

    private static Point[] RegionContour(JlRegion region)
    {
        using var curve = region.GenContourRegionXld("center");
        using var selected = curve.SelectObj(1);
        selected.GetContourXld(out var row, out var col);
        using (row) using (col) return Enumerable.Range(0, row.Length).Select(i => new Point((int)Math.Round(col[i].D), (int)Math.Round(row[i].D))).ToArray();
    }
    public static void FindContours(Mat source, out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes retrieval, ContourApproximationModes approximation)
    {
        using var image = JLVisionImages.FromMat(source);
        using var foreground = image.Threshold(1d, 255d);
        using var components = foreground.Connection();
        var items = new List<(Point[] Points, int Parent)>();
        for (var i = 1; i <= components.CountObj(); i++)
        {
            using var component = components.SelectObj(i);
            if (component.AreaCenter(out double _, out double _) == 0) continue;
            using var solid = component.FillUp();
            var parent = items.Count; items.Add((RegionContour(solid), -1));
            if (retrieval == RetrievalModes.External) continue;
            using var holes = solid.Difference(component);
            using var separated = holes.Connection();
            for (var j = 1; j <= separated.CountObj(); j++)
            { using var hole = separated.SelectObj(j); if (hole.AreaCenter(out double _, out double _) == 0) continue; using var border = hole.DilationCircle(1d); items.Add((RegionContour(border), parent)); }
        }
        if (retrieval == RetrievalModes.External)
        {
            var outer = items.ToArray();
            items = outer.Where((item, index) => !outer.Where((_, other) => other != index)
                .Any(parent => ContourArea(parent.Points) > ContourArea(item.Points) && Contains(parent.Points, item.Points[0]))).ToList();
        }
        contours = items.Select(x => x.Points).ToArray();
        hierarchy = items.Select(x => new HierarchyIndex(-1, -1, -1, x.Parent)).ToArray();
    }
    private static bool Contains(Point[] polygon, Point point)
    {
        var inside = false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (double)(b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
    public static void Canny(Mat source, Mat target, double low, double high)
    {
        using var image = JLVisionImages.FromMat(source);
        using var edges = image.EdgesSubPix("canny", 1d, (int)low, (int)high);
        // A constant/blank image yields an empty XLD object; converting that through
        // RegionToBin produces a channel-less image whose conversion throws. The
        // correct Canny result there is an all-zero edge map (found=false upstream).
        if (edges.CountObj() == 0)
        {
            using var empty = new Mat(source.Height, source.Width, MatType.CV_8UC1, Scalar.All(0));
            empty.CopyTo(target);
            return;
        }
        using var region = edges.GenRegionContourXld("margin");
        using var binary = region.RegionToBin(255, 0, source.Width, source.Height); JLVisionImages.Store(binary, target);
    }
    public static NativeLine[] HoughLinesP(Mat source, double rho, double theta, int threshold, double minLineLength, double maxLineGap)
    {
        using var image = JLVisionImages.FromMat(source);
        using var region = image.Threshold(1d, 255d);
        using var outlines = region.GenContourRegionXld("center");
        using var segments = outlines.SegmentContoursXld("lines", 5, 2d, 1d);
        var result = new List<NativeLine>();
        for (var i = 1; i <= segments.CountObj(); i++)
        {
            using var curve = segments.SelectObj(i); using var length = curve.LengthXld();
            if (length.D < minLineLength || length.D < threshold) continue;
            curve.FitLineContourXld("tukey", -1, 0, 5, 2d, out double r1, out double c1, out double r2, out double c2, out double _, out double _, out double _);
            result.Add(new(new(c1, r1), new(c2, r2)));
        }
        return result.ToArray();
    }
    public static NativeCircle[] HoughCircles(Mat source, HoughModes method, double dp, double minDist, double param1, double param2, int minRadius, int maxRadius)
    {
        using var image = JLVisionImages.FromMat(source);
        using var contours = image.EdgesSubPix("canny", 1d, Math.Max(1, (int)param1 / 2), (int)param1);
        using var segments = contours.SegmentContoursXld("lines_circles", 5, 2d, 1d);
        var result = new List<NativeCircle>();
        for (var i = 1; i <= segments.CountObj(); i++)
        {
            using var curve = segments.SelectObj(i); using var length = curve.LengthXld(); if (length.D < Math.Max(param2, 12)) continue;
            curve.FitCircleContourXld("geotukey", -1, 0d, 0, 3, 2d, out double row, out double col, out double radius, out double _, out double _, out string _);
            if (!double.IsFinite(radius) || radius < minRadius || maxRadius > 0 && radius > maxRadius) continue;
            if (result.Any(c => Math.Sqrt(Math.Pow(c.Center.X - col, 2) + Math.Pow(c.Center.Y - row, 2)) < minDist)) continue;
            result.Add(new(new(col, row), radius));
        }
        return result.ToArray();
    }
}
