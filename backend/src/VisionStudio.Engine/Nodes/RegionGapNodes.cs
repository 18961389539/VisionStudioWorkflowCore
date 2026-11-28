using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 分割·轮廓·特征补全批的公共算量：ROI 构造、轮廓光栅化、凸包与凸性缺陷。
/// 这一批不引入新算法，做的是"数据形态"转换——所以实现重点是边界校验与坐标语义，
/// 而不是数学。
/// </summary>
internal static class RegionGapMath
{
    public const int MaxPolygonPoints = 1000;

    public static VisionRoiPoint[] PolygonPoints(string json)
    {
        if (json.Length > 2_097_152) throw new InvalidOperationException("Polygon JSON exceeds 2 MiB of text.");
        List<double[]>? data;
        try { data = JsonSerializer.Deserialize<List<double[]>>(json); }
        catch (JsonException ex) { throw new InvalidOperationException("Polygon points JSON must be an array of [x,y] pairs.", ex); }
        data ??= [];
        if (data.Count is < 3 or > MaxPolygonPoints)
            throw new InvalidOperationException($"A polygon needs 3..{MaxPolygonPoints} [x,y] pairs.");
        var points = new VisionRoiPoint[data.Count];
        for (var i = 0; i < data.Count; i++)
        {
            var pair = data[i];
            if (pair is null || pair.Length != 2) throw new InvalidOperationException("Polygon points JSON must be an array of [x,y] pairs.");
            if (!double.IsFinite(pair[0]) || !double.IsFinite(pair[1]) || Math.Abs(pair[0]) > 1e8 || Math.Abs(pair[1]) > 1e8)
                throw new InvalidOperationException("Polygon coordinates must be finite within +/-100000000 px.");
            points[i] = new VisionRoiPoint(pair[0], pair[1]);
        }
        return points;
    }

    /// <summary>多边形按质心缩放做近似外扩：Roi 没有轮廓偏置语义，这里只保证"方向正确、幅度近似"。</summary>
    public static VisionRoiPoint[] ScalePolygon(VisionRoiPoint[] points, double expand)
    {
        if (Math.Abs(expand) < 1e-9) return points;
        var cx = points.Average(p => p.X); var cy = points.Average(p => p.Y);
        var radius = points.Average(p => MeasurementMath.Hypot(p.X - cx, p.Y - cy));
        if (radius < 1e-6) return points;
        var scale = Math.Max(.05, (radius + expand) / radius);
        return points.Select(p => new VisionRoiPoint(cx + (p.X - cx) * scale, cy + (p.Y - cy) * scale)).ToArray();
    }

    public static void ValidatePolygon(VisionRoiPoint[] points)
    {
        if (points.Length is < 3 or > MaxPolygonPoints)
            throw new InvalidOperationException($"A polygon needs 3..{MaxPolygonPoints} points to match region.fromRoi's rasterization contract.");
        double twiceArea = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1e8 || Math.Abs(p.Y) > 1e8)
                throw new InvalidOperationException($"Polygon point {i} must be finite within +/-100000000 px.");
            var q = points[(i + 1) % points.Length];
            twiceArea += p.X * q.Y - q.X * p.Y;
        }
        if (!double.IsFinite(twiceArea) || Math.Abs(twiceArea) < 1e-6)
            throw new InvalidOperationException("Polygon points must enclose a finite non-zero area.");
    }

    public static VisionRegion RegionFromMask(Mat binary, CancellationToken cancellationToken)
    {
        var (components, _) = RegionFromMaskNode.BuildComponents(binary, cancellationToken);
        return new VisionRegion(binary.Width, binary.Height, components);
    }

    public static long RegionArea(VisionRegion region)
        => region.Components.Sum(component => component.Runs.Sum(run => (long)(run.EndColumn - run.StartColumn + 1)));

    /// <summary>Andrew 单调链凸包；返回逆时针、不重复首点的顶点序列。</summary>
    public static VisionPoint2D[] ConvexHull(IReadOnlyList<VisionPoint2D> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3) return sorted;
        var hull = new List<VisionPoint2D>(sorted.Length * 2);
        static double Cross(VisionPoint2D o, VisionPoint2D a, VisionPoint2D b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        foreach (var point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        var lower = hull.Count + 1;
        for (var i = sorted.Length - 2; i >= 0; i--)
        {
            var point = sorted[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull.ToArray();
    }

    public static double PolygonArea(IReadOnlyList<VisionPoint2D> points)
    {
        if (points.Count < 3) return 0;
        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) / 2;
    }

    /// <summary>点到多边形边界的最近距离（点在边界上时为 0）。</summary>
    public static double DistanceToBoundary(VisionPoint2D point, IReadOnlyList<VisionPoint2D> polygon)
    {
        var minimum = double.MaxValue;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared > 1e-12 ? Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1) : 0;
            var distance = MeasurementMath.Hypot(point.X - (a.X + t * dx), point.Y - (a.Y + t * dy));
            if (distance < minimum) minimum = distance;
        }
        return minimum == double.MaxValue ? 0 : minimum;
    }
}

public sealed class RoiFromGeometryNode : IVisionNodeExecutor
{
    public string Type => "roi.fromGeometry";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var shape = node.GetString("shape", "rectangle").Trim().ToLowerInvariant();
        if (shape is not ("rectangle" or "circle" or "polygon"))
            throw new InvalidOperationException($"roi.fromGeometry shape '{shape}' is not supported (rectangle | circle | polygon).");
        var expand = node.GetDouble("expand", 0);
        if (!double.IsFinite(expand) || expand is < -1000 or > 1000) throw new InvalidOperationException("roi.fromGeometry expand must be within -1000..1000 px.");
        var geometryPorts = new[] { "rectangle", "circle", "pointList" }
            .Where(port => context.TryGetValue(port, out _)).ToArray();
        if (geometryPorts.Length > 1)
            throw new InvalidOperationException($"roi.fromGeometry shape '{shape}' conflicts with connected geometry ports: {string.Join(", ", geometryPorts)}. Connect only the port matching shape.");
        var expectedPort = shape == "rectangle" ? "rectangle" : shape == "circle" ? "circle" : "pointList";
        if (geometryPorts.Length == 1 && geometryPorts[0] != expectedPort)
            throw new InvalidOperationException($"roi.fromGeometry shape '{shape}' conflicts with port '{geometryPorts[0]}'; expected '{expectedPort}'.");
        var source = geometryPorts.Length == 1 ? $"port:{geometryPorts[0]}" : shape == "polygon" ? "pointsJson" : "numeric-parameters";

        VisionRoi roi; double x; double y; double width; double height; double radius; long pointCount;
        switch (shape)
        {
            case "rectangle":
            {
                var anchorCentre = node.GetString("rectangleAnchor", "topLeft").Trim().ToLowerInvariant() == "center";
                var rotation = 0.0;
                if (context.TryGetValue("rectangle", out var rectangleValue))
                {
                    var rectangle = rectangleValue.Require<VisionRectangle2D>("rectangle");
                    width = rectangle.Width; height = rectangle.Height; rotation = rectangle.AngleDeg;
                    (x, y) = anchorCentre
                        ? (rectangle.X - width / 2, rectangle.Y - height / 2)
                        : (rectangle.X, rectangle.Y);
                }
                else
                {
                    x = node.GetDouble("x", 0); y = node.GetDouble("y", 0);
                    width = node.GetDouble("width", 100); height = node.GetDouble("height", 100);
                }
                ToleranceGeometry.Finite(x, y, width, height);
                if (width <= 0 || height <= 0) throw new InvalidOperationException("roi.fromGeometry rectangle width and height must be positive.");
                // 旋转矩形取轴对齐外接框：ROI 本身没有角度概念，宁可放大也不漏检
                if (Math.Abs(rotation) > 1e-9)
                {
                    var radians = Math.Abs(rotation) * Math.PI / 180;
                    var cos = Math.Abs(Math.Cos(radians)); var sin = Math.Abs(Math.Sin(radians));
                    var cx = x + width / 2; var cy = y + height / 2;
                    var rotatedWidth = width * cos + height * sin; var rotatedHeight = width * sin + height * cos;
                    x = cx - rotatedWidth / 2; y = cy - rotatedHeight / 2;
                    width = rotatedWidth; height = rotatedHeight;
                }
                x -= expand; y -= expand; width += 2 * expand; height += 2 * expand;
                if (width <= 0 || height <= 0) throw new InvalidOperationException("roi.fromGeometry expand shrank the rectangle to nothing.");
                roi = new VisionRoi(VisionRoiType.Rectangle, x, y, width, height);
                radius = 0; pointCount = 4;
                break;
            }
            case "circle":
            {
                double cx; double cy;
                if (context.TryGetValue("circle", out var circleValue))
                {
                    var circle = circleValue.Require<VisionCircle>("circle");
                    cx = circle.X; cy = circle.Y; radius = circle.Radius;
                }
                else
                {
                    cx = node.GetDouble("centerX", 0); cy = node.GetDouble("centerY", 0); radius = node.GetDouble("radius", 50);
                }
                ToleranceGeometry.Finite(cx, cy, radius);
                radius += expand;
                if (radius <= 0) throw new InvalidOperationException("roi.fromGeometry circle radius must stay positive after expand.");
                roi = new VisionRoi(VisionRoiType.Circle, cx, cy, Radius: radius);
                x = cx; y = cy; width = radius * 2; height = radius * 2; pointCount = 1;
                break;
            }
            default:
            {
                VisionRoiPoint[] points;
                if (context.TryGetValue("pointList", out var pointListValue))
                {
                    var list = pointListValue.Require<IReadOnlyList<VisionPoint2D>>("pointList");
                    if (list.Count is < 3 or > RegionGapMath.MaxPolygonPoints)
                        throw new InvalidOperationException($"A polygon needs 3..{RegionGapMath.MaxPolygonPoints} points.");
                    points = list.Select(p => new VisionRoiPoint(p.X, p.Y)).ToArray();
                }
                else points = RegionGapMath.PolygonPoints(node.GetString("pointsJson", "[[20,20],[120,20],[120,120]]"));
                RegionGapMath.ValidatePolygon(points);
                points = RegionGapMath.ScalePolygon(points, expand);
                roi = new VisionRoi(VisionRoiType.Polygon, Points: points);
                thePolygonBounds(points, out x, out y, out width, out height);
                radius = 0; pointCount = points.Length;
                break;
            }
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("shape", shape), ("geometrySource", source),
            ("maxRasterizablePolygonPoints", RegionGapMath.MaxPolygonPoints), ("x", x), ("y", y),
            ("width", width), ("height", height), ("radius", radius), ("expand", expand), ("pointCount", pointCount));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["roi"] = VisionValue.Roi(roi), ["shape"] = VisionValue.String(shape),
                ["x"] = VisionValue.Double(x), ["y"] = VisionValue.Double(y),
                ["width"] = VisionValue.Double(width), ["height"] = VisionValue.Double(height),
                ["radius"] = VisionValue.Double(radius), ["pointCount"] = VisionValue.Integer(pointCount),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["shape"] = shape, ["geometrySource"] = source, ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height,
                ["radius"] = radius, ["expand"] = expand, ["pointCount"] = pointCount,
                ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));

        static void thePolygonBounds(VisionRoiPoint[] points, out double left, out double top, out double w, out double h)
        {
            var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
            var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
            left = minX; top = minY; w = maxX - minX; h = maxY - minY;
        }
    }
}

public sealed class RoiFromDetectionNode : IVisionNodeExecutor
{
    public string Type => "roi.fromDetection";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var detections = context.Require<IReadOnlyList<VisionDetection>>("detections");
        var index = node.GetInt("index", 0);
        if (index < 0 || index > 100000) throw new InvalidOperationException("roi.fromDetection index must be 0..100000.");
        var emptyPolicy = node.GetString("emptyPolicy", "empty").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail")) throw new InvalidOperationException("roi.fromDetection emptyPolicy must be empty or fail.");
        var shape = node.GetString("shape", "rectangle").Trim().ToLowerInvariant();
        if (shape is not ("rectangle" or "circle"))
            throw new InvalidOperationException($"roi.fromDetection shape '{shape}' is not supported (rectangle | circle).");
        var expand = node.GetDouble("expand", 0);
        if (!double.IsFinite(expand) || expand is < -1000 or > 1000) throw new InvalidOperationException("roi.fromDetection expand must be within -1000..1000 px.");

        if (index >= detections.Count)
        {
            var missing = CoordinateValidation.Diagnostics(Type, ("found", false), ("requestedIndex", index),
                ("selectedIndex", -1), ("detectionCount", detections.Count), ("emptyPolicy", emptyPolicy));
            if (emptyPolicy == "fail")
                throw new InvalidOperationException($"roi.fromDetection found no ROI: requestedIndex={index}, detectionCount={detections.Count}, selectedIndex=-1.");
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["found"] = VisionValue.Boolean(false), ["index"] = VisionValue.Integer(index), ["x"] = VisionValue.Double(0), ["y"] = VisionValue.Double(0),
                    ["width"] = VisionValue.Double(0), ["height"] = VisionValue.Double(0), ["radius"] = VisionValue.Double(0),
                    ["diagnostics"] = VisionValue.String(missing)
                },
                new Dictionary<string, object?>
                {
                    ["found"] = false, ["requestedIndex"] = index, ["selectedIndex"] = -1,
                    ["detectionCount"] = detections.Count, ["emptyPolicy"] = emptyPolicy, ["diagnostics"] = missing
                }));
        }

        var detection = detections[index];
        ToleranceGeometry.Finite(detection.X, detection.Y, detection.Width, detection.Height);
        double x, y, width, height, radius;
        VisionRoi roi;
        if (shape == "rectangle")
        {
            width = detection.Width + 2 * expand; height = detection.Height + 2 * expand;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("roi.fromDetection expand shrank the box to nothing.");
            x = detection.X - width / 2; y = detection.Y - height / 2;
            roi = new VisionRoi(VisionRoiType.Rectangle, x, y, width, height);
            radius = 0;
        }
        else
        {
            radius = (detection.Circle.Radius > 0 ? detection.Circle.Radius : Math.Max(detection.Width, detection.Height) / 2) + expand;
            if (radius <= 0) throw new InvalidOperationException("roi.fromDetection circle radius must stay positive after expand.");
            x = detection.X; y = detection.Y; width = radius * 2; height = radius * 2;
            roi = new VisionRoi(VisionRoiType.Circle, x, y, Radius: radius);
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("requestedIndex", index),
            ("selectedIndex", index), ("detectionCount", detections.Count), ("shape", shape),
            ("x", x), ("y", y), ("width", width), ("height", height), ("radius", radius), ("expand", expand));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["roi"] = VisionValue.Roi(roi), ["found"] = VisionValue.Boolean(true), ["index"] = VisionValue.Integer(index),
                ["x"] = VisionValue.Double(x), ["y"] = VisionValue.Double(y),
                ["width"] = VisionValue.Double(width), ["height"] = VisionValue.Double(height),
                ["radius"] = VisionValue.Double(radius), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["requestedIndex"] = index, ["selectedIndex"] = index,
                ["detectionCount"] = detections.Count, ["shape"] = shape, ["x"] = x, ["y"] = y,
                ["width"] = width, ["height"] = height, ["radius"] = radius, ["expand"] = expand,
                ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }
}

public sealed class RegionFromContourNode : IVisionNodeExecutor
{
    public string Type => "region.fromContour";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var contours = context.Require<IReadOnlyList<VisionContour>>("contours");
        var index = node.GetInt("index", 0);
        if (index < 0 || index > 100000) throw new InvalidOperationException("region.fromContour index must be 0..100000.");
        var all = node.GetBool("allContours", false);
        var preserveHierarchy = node.GetBool("preserveHierarchy", false);
        var expand = node.GetInt("expand", 0);
        if (expand is < 0 or > 50) throw new InvalidOperationException("region.fromContour expand must be within 0..50 px.");
        var invert = node.GetBool("invert", false);
        if (contours.Count == 0)
            throw new InvalidOperationException("region.fromContour received an empty contour list; there is no contour to rasterize.");
        if (!all && index >= contours.Count)
            throw new InvalidOperationException($"region.fromContour index {index} is out of range for {contours.Count} contours.");
        for (var i = 0; i < contours.Count; i++)
        {
            var contour = contours[i];
            if (!all && i != index) continue;
            if (contour is null || contour.Points is null || contour.Points.Count < 3)
                throw new InvalidOperationException($"region.fromContour contour {i} must contain at least three points.");
            for (var p = 0; p < contour.Points.Count; p++)
            {
                var point = contour.Points[p];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    throw new InvalidOperationException($"region.fromContour contour {i} point {p} is not finite.");
            }
            ValidateNoSelfIntersection(contour.Points, i);
        }

        var selected = new List<(int SourceIndex, VisionContour Contour)>();
        if (all)
        {
            for (var i = 0; i < contours.Count; i++)
            {
                // Hierarchy mode fills only exteriors; holes stay background. Flat mode fills every contour.
                if (preserveHierarchy && contours[i].IsHole) continue;
                selected.Add((i, contours[i]));
            }
        }
        else selected.Add((index, contours[index]));
        if (selected.Count == 0)
            throw new InvalidOperationException("region.fromContour selected no fillable outer contours.");

        // 画布：优先用接入的图像尺寸（保证区域坐标与图像对齐），否则按轮廓包围盒外扩
        int width; int height;
        var canvasSource = "contour-bounds";
        if (context.TryGetValue("image", out var imageValue) && imageValue.Type == VisionDataType.Image)
        {
            var image = imageValue.Require<IVisionImage>("image");
            width = image.Width; height = image.Height; canvasSource = "image";
        }
        else
        {
            var maxX = selected.Max(c => c.Contour.Points.Max(p => p.X));
            var maxY = selected.Max(c => c.Contour.Points.Max(p => p.Y));
            if (!double.IsFinite(maxX) || !double.IsFinite(maxY) || maxX < 0 || maxY < 0 || maxX > 32000 || maxY > 32000)
                throw new InvalidOperationException("region.fromContour needs an image port for contours that are negative or larger than 32000 px.");
            width = Math.Max(1, (int)Math.Ceiling(maxX) + 1);
            height = Math.Max(1, (int)Math.Ceiling(maxY) + 1);
        }

        var mask = RegionRasterAlgorithms.FromContours(selected.Select(item => item.Contour).ToArray(), width, height, expand, invert, cancellationToken);
        try
        {
            var region = RegionGapMath.RegionFromMask(mask, cancellationToken);
            var area = RegionGapMath.RegionArea(region);
            var image = VisionImage.Own(mask.Clone());
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", region.Components.Count > 0),
                    ("count", region.Components.Count), ("area", area), ("width", width), ("height", height),
                    ("canvasSource", canvasSource), ("selectedContourCount", selected.Count),
                    ("selectedContourIndices", selected.Select(item => item.SourceIndex).ToArray()),
                    ("allContours", all), ("allContoursOverridesIndex", all), ("preserveHierarchy", preserveHierarchy),
                    ("expand", expand), ("invert", invert));
                var sourceMappings = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    contours = selected.Select(item => new { sourceIndex = item.SourceIndex, isHole = item.Contour.IsHole, parentIndex = item.Contour.ParentIndex }).ToArray()
                });
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["region"] = VisionValue.Region(region), ["image"] = VisionValue.Image(image),
                        ["found"] = VisionValue.Boolean(region.Components.Count > 0),
                        ["count"] = VisionValue.Integer(region.Components.Count), ["area"] = VisionValue.Integer(area),
                        ["sourceMappings"] = VisionValue.String(sourceMappings),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["found"] = region.Components.Count > 0, ["count"] = region.Components.Count, ["area"] = area,
                        ["width"] = width, ["height"] = height, ["canvasSource"] = canvasSource,
                        ["selectedContourCount"] = selected.Count, ["allContours"] = all,
                        ["preserveHierarchy"] = preserveHierarchy, ["expand"] = expand,
                        ["invert"] = invert, ["sourceMappings"] = sourceMappings,
                        ["unit"] = "px", ["algorithmBackend"] = "managed",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            mask.Dispose();
        }
    }

    private static void ValidateNoSelfIntersection(IReadOnlyList<VisionPoint2D> points, int contourIndex)
    {
        // Adjacent edges share an endpoint by design. Reject all other crossings, including touches.
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count];
            for (var j = i + 1; j < points.Count; j++)
            {
                if (j == i || j == (i + 1) % points.Count || i == (j + 1) % points.Count) continue;
                var c = points[j]; var d = points[(j + 1) % points.Count];
                if (SegmentsIntersect(a, b, c, d))
                    throw new InvalidOperationException($"region.fromContour contour {contourIndex} self-intersects at edges {i} and {j}.");
            }
        }
    }

    private static bool SegmentsIntersect(VisionPoint2D a, VisionPoint2D b, VisionPoint2D c, VisionPoint2D d)
    {
        static double Cross(VisionPoint2D p, VisionPoint2D q, VisionPoint2D r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        static bool OnSegment(VisionPoint2D p, VisionPoint2D q, VisionPoint2D r)
            => Math.Abs(Cross(p, q, r)) < 1e-9 && r.X >= Math.Min(p.X, q.X) - 1e-9 && r.X <= Math.Max(p.X, q.X) + 1e-9
                && r.Y >= Math.Min(p.Y, q.Y) - 1e-9 && r.Y <= Math.Max(p.Y, q.Y) + 1e-9;
        var abC = Cross(a, b, c); var abD = Cross(a, b, d); var cdA = Cross(c, d, a); var cdB = Cross(c, d, b);
        return (abC * abD < 0 && cdA * cdB < 0) || OnSegment(a, b, c) || OnSegment(a, b, d) || OnSegment(c, d, a) || OnSegment(c, d, b);
    }
}

public sealed class RegionToMaskNode : IVisionNodeExecutor
{
    public string Type => "region.toMask";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var region = context.Require<VisionRegion>("region");
        var foreground = node.GetInt("foreground", 255);
        var background = node.GetInt("background", 0);
        if (foreground is < 0 or > 255 || background is < 0 or > 255)
            throw new InvalidOperationException("region.toMask foreground and background must be within 0..255.");
        if (foreground == background) throw new InvalidOperationException("region.toMask foreground and background must differ.");
        var emitLabels = node.GetBool("emitLabels", false);
        var roundTripSafe = foreground > 0;
        var warnings = new List<object>();
        if (!roundTripSafe) warnings.Add(new { code = "nonzero-roundtrip-impossible", foreground, background, message = "region.fromMask uses a non-zero threshold, so foreground=0 cannot be recovered." });

        var mask = new Mat(region.Height, region.Width, MatType.CV_8UC1, Scalar.All(background));
        var labels = emitLabels ? new Mat(region.Height, region.Width, MatType.CV_32SC1, Scalar.All(0)) : null;
        try
        {
            var step = (int)mask.Step();
            var buffer = new byte[step * region.Height];
            if (background != 0) Array.Fill(buffer, (byte)background);
            var labelStep = labels is null ? 0 : (int)labels.Step();
            var labelBuffer = labels is null ? Array.Empty<int>() : new int[labelStep / sizeof(int) * region.Height];
            long area = 0;
            for (var componentIndex = 0; componentIndex < region.Components.Count; componentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var run in region.Components[componentIndex].Runs)
                {
                    var length = run.EndColumn - run.StartColumn + 1;
                    Array.Fill(buffer, (byte)foreground, run.Row * step + run.StartColumn, length);
                    if (labels is not null) Array.Fill(labelBuffer, componentIndex + 1, run.Row * (labelStep / sizeof(int)) + run.StartColumn, length);
                    area += length;
                }
            }
            Marshal.Copy(buffer, 0, mask.Data, buffer.Length);
            if (labels is not null) Marshal.Copy(labelBuffer, 0, labels.Data, labelBuffer.Length);
            cancellationToken.ThrowIfCancellationRequested();

            var image = VisionImage.Own(mask);
            mask = null!;
            var labelImage = labels is null ? null : VisionImage.Own(labels);
            labels = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var mappings = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    labelZero = "background",
                    labels = region.Components.Select((component, index) => new { label = index + 1, objectIndex = index, area = component.Runs.Sum(r => (long)r.EndColumn - r.StartColumn + 1) }).ToArray()
                });
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("width", region.Width), ("height", region.Height),
                    ("count", region.Components.Count), ("area", area), ("foreground", foreground), ("background", background),
                    ("roundTripSafe", roundTripSafe), ("warningCount", warnings.Count), ("labelsEmitted", labelImage is not null));
                var outputs = new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(image), ["width"] = VisionValue.Integer(region.Width),
                    ["height"] = VisionValue.Integer(region.Height), ["area"] = VisionValue.Integer(area),
                    ["count"] = VisionValue.Integer(region.Components.Count), ["sourceMappings"] = VisionValue.String(mappings),
                    ["warningCount"] = VisionValue.Integer(warnings.Count), ["diagnostics"] = VisionValue.String(diagnostics)
                };
                if (labelImage is not null) outputs["labelImage"] = VisionValue.Image(labelImage);
                return new NodeExecutorResult(outputs,
                    new Dictionary<string, object?>
                    {
                        ["width"] = region.Width, ["height"] = region.Height, ["count"] = region.Components.Count,
                        ["area"] = area, ["foreground"] = foreground, ["background"] = background,
                        ["roundTripSafe"] = roundTripSafe, ["warningCount"] = warnings.Count,
                        ["labelsEmitted"] = labelImage is not null, ["sourceMappings"] = mappings,
                        ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            mask?.Dispose();
            labels?.Dispose();
        }
    }
}

public sealed class ContourConvexityNode : IVisionNodeExecutor
{
    public string Type => "contour.convexity";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var closed = node.GetBool("closed", true);
        if (!closed) return ValueTask.FromResult(ToleranceGeometry.Missing(0, "open-contour-not-evaluated", new(){["closed"]=VisionValue.Boolean(false),["withinTolerance"]=VisionValue.Boolean(false),["evaluationState"]=VisionValue.String("not-evaluated-open-contour")},nodeType:Type));
        var minDepth = node.GetDouble("minDefectDepth", .5);
        if (!double.IsFinite(minDepth) || minDepth < 0) throw new InvalidOperationException("contour.convexity minDefectDepth must be finite and >= 0.");
        var maxDepth = node.GetDouble("maxDefectDepth", 0);
        if (!double.IsFinite(maxDepth) || maxDepth < 0) throw new InvalidOperationException("contour.convexity maxDefectDepth must be finite and >= 0 (0 disables the gate).");
        if (maxDepth > 0 && maxDepth < minDepth) throw new InvalidOperationException($"contour.convexity maxDefectDepth ({maxDepth}) must be >= minDefectDepth ({minDepth}) when enabled.");

        var points = ToleranceGeometry.Select(context, node, closed: true);
        if (points.Length < 4) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "insufficient-points", nodeType: Type));

        var hull = RegionGapMath.ConvexHull(points);
        if (hull.Length < 3) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "degenerate-hull", nodeType: Type));

        var area = RegionGapMath.PolygonArea(points);
        var hullArea = RegionGapMath.PolygonArea(hull);
        if (hullArea < 1e-9) return ValueTask.FromResult(ToleranceGeometry.Missing(points.Length, "degenerate-hull", nodeType: Type));
        var convexity = Math.Clamp(area / hullArea, 0, 1);

        // 逐点求到凸包边界的距离：凹进去的那一段才 > 0，连续超阈值的点归为同一个缺陷
        var depths = new double[points.Length];
        var deepest = 0.0; var deepestIndex = 0;
        for (var i = 0; i < points.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            depths[i] = RegionGapMath.DistanceToBoundary(points[i], hull);
            if (depths[i] > deepest) { deepest = depths[i]; deepestIndex = i; }
        }

        var defects = new List<(int Start, int End, double Depth, VisionPoint2D Apex)>();
        var currentStart = -1; var currentDepth = 0.0; var currentApexIndex = -1;
        for (var i = 0; i < points.Length; i++)
        {
            if (depths[i] > minDepth)
            {
                if (currentStart < 0) { currentStart = i; currentDepth = 0; currentApexIndex = -1; }
                if (depths[i] > currentDepth) { currentDepth = depths[i]; currentApexIndex = i; }
            }
            else if (currentStart >= 0)
            {
                if (currentApexIndex >= 0) defects.Add((currentStart, i - 1, currentDepth, points[currentApexIndex]));
                currentStart = -1;
            }
        }
        if (currentStart >= 0 && currentApexIndex >= 0) defects.Add((currentStart, points.Length - 1, currentDepth, points[currentApexIndex]));

        var within = maxDepth <= 0 || deepest <= maxDepth;
        var defectJson = JsonSerializer.Serialize(defects.Select(d => new
        {
            startIndex = d.Start, endIndex = d.End, depth = Math.Round(d.Depth, 4),
            x = Math.Round(d.Apex.X, 4), y = Math.Round(d.Apex.Y, 4)
        }));
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("pointCount", points.Length),
            ("hullPointCount", hull.Length), ("area", area), ("hullArea", hullArea), ("convexity", convexity),
            ("maxDefectDepth", deepest), ("defectCount", defects.Count), ("withinTolerance", within),
            ("closed",true),("minDefectDepth",minDepth),("maxDefectDepthConfigured",maxDepth),("maxDefectDepthEnabled",maxDepth>0),("maxDefectDepthEffective",maxDepth>0?maxDepth:double.PositiveInfinity));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["closed"] = VisionValue.Boolean(true), ["pointCount"] = VisionValue.Integer(points.Length),
                ["hullPointCount"] = VisionValue.Integer(hull.Length), ["area"] = VisionValue.Double(area),
                ["hullArea"] = VisionValue.Double(hullArea), ["convexity"] = VisionValue.Double(convexity),
                ["maxDefectDepth"] = VisionValue.Double(deepest), ["maxDefectPoint"] = VisionValue.Point(points[deepestIndex]),
                ["defectCount"] = VisionValue.Integer(defects.Count), ["defectsJson"] = VisionValue.String(defectJson),
                ["withinTolerance"] = VisionValue.Boolean(within), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["pointCount"] = points.Length, ["hullPointCount"] = hull.Length,
                ["area"] = area, ["hullArea"] = hullArea, ["convexity"] = convexity,
                ["maxDefectDepth"] = deepest, ["defectCount"] = defects.Count, ["withinTolerance"] = within,
                ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            },
            [
                new("convex-hull", VisionOverlayType.Contour, Points: hull.Select(p => new VisionOverlayPoint(p.X, p.Y)).ToArray()),
                new("max-defect", VisionOverlayType.Point, X: points[deepestIndex].X, Y: points[deepestIndex].Y,
                    Label: $"depth {deepest:0.###}")
            ]));
    }
}
