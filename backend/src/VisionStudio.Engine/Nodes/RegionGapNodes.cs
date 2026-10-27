using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 分割·轮廓·特征补全批的公共算量：ROI 构造、轮廓光栅化、凸包与凸性缺陷。
/// 这一批不引入新算法，做的是"数据形态"转换——所以实现重点是边界校验与坐标语义，
/// 而不是数学。
/// </summary>
internal static class RegionGapMath
{
    public const int MaxPolygonPoints = 20000;

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
                points = RegionGapMath.ScalePolygon(points, expand);
                roi = new VisionRoi(VisionRoiType.Polygon, Points: points);
                thePolygonBounds(points, out x, out y, out width, out height);
                radius = 0; pointCount = points.Length;
                break;
            }
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("shape", shape), ("x", x), ("y", y),
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
                ["shape"] = shape, ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height,
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
        if (index < 0 || index > 1999) throw new InvalidOperationException("roi.fromDetection index must be 0..1999.");
        var shape = node.GetString("shape", "rectangle").Trim().ToLowerInvariant();
        if (shape is not ("rectangle" or "circle"))
            throw new InvalidOperationException($"roi.fromDetection shape '{shape}' is not supported (rectangle | circle).");
        var expand = node.GetDouble("expand", 0);
        if (!double.IsFinite(expand) || expand is < -1000 or > 1000) throw new InvalidOperationException("roi.fromDetection expand must be within -1000..1000 px.");

        if (index >= detections.Count)
        {
            var missing = CoordinateValidation.Diagnostics(Type, ("found", false), ("index", index), ("count", detections.Count));
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["index"] = VisionValue.Integer(index), ["x"] = VisionValue.Double(0), ["y"] = VisionValue.Double(0),
                    ["width"] = VisionValue.Double(0), ["height"] = VisionValue.Double(0), ["radius"] = VisionValue.Double(0),
                    ["diagnostics"] = VisionValue.String(missing)
                },
                new Dictionary<string, object?>
                {
                    ["found"] = false, ["index"] = index, ["count"] = detections.Count, ["diagnostics"] = missing
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

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("index", index), ("shape", shape),
            ("x", x), ("y", y), ("width", width), ("height", height), ("radius", radius), ("expand", expand));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["roi"] = VisionValue.Roi(roi), ["index"] = VisionValue.Integer(index),
                ["x"] = VisionValue.Double(x), ["y"] = VisionValue.Double(y),
                ["width"] = VisionValue.Double(width), ["height"] = VisionValue.Double(height),
                ["radius"] = VisionValue.Double(radius), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["found"] = true, ["index"] = index, ["shape"] = shape, ["x"] = x, ["y"] = y,
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
        if (index < 0 || index > 1999) throw new InvalidOperationException("region.fromContour index must be 0..1999.");
        var all = node.GetBool("allContours", false);
        var expand = node.GetInt("expand", 0);
        if (expand is < 0 or > 50) throw new InvalidOperationException("region.fromContour expand must be within 0..50 px.");
        var invert = node.GetBool("invert", false);

        var selected = new List<VisionContour>();
        if (all) selected.AddRange(contours.Where(contour => contour?.Points is { Count: >= 3 }));
        else if (index < contours.Count && contours[index]?.Points is { Count: >= 3 }) selected.Add(contours[index]!);
        if (selected.Count == 0)
        {
            var missing = CoordinateValidation.Diagnostics(Type, ("found", false), ("index", index), ("count", contours.Count), ("reason", "no-closed-contour"));
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["count"] = VisionValue.Integer(0), ["area"] = VisionValue.Integer(0), ["found"] = VisionValue.Boolean(false),
                    ["diagnostics"] = VisionValue.String(missing)
                },
                new Dictionary<string, object?> { ["found"] = false, ["count"] = 0, ["area"] = 0, ["diagnostics"] = missing }));
        }

        // 画布：优先用接入的图像尺寸（保证区域坐标与图像对齐），否则按轮廓包围盒外扩
        int width; int height;
        if (context.TryGetValue("image", out var imageValue) && imageValue.Type == VisionDataType.Image)
        {
            var image = imageValue.Require<IVisionImage>("image");
            width = image.Width; height = image.Height;
        }
        else
        {
            var maxX = selected.Max(c => c.Points.Max(p => p.X));
            var maxY = selected.Max(c => c.Points.Max(p => p.Y));
            if (!double.IsFinite(maxX) || !double.IsFinite(maxY) || maxX < 0 || maxY < 0 || maxX > 32000 || maxY > 32000)
                throw new InvalidOperationException("region.fromContour needs an image port for contours that are negative or larger than 32000 px.");
            width = Math.Max(1, (int)Math.Ceiling(maxX) + 1);
            height = Math.Max(1, (int)Math.Ceiling(maxY) + 1);
        }

        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            foreach (var contour in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var points = contour.Points.Select(p =>
                {
                    ToleranceGeometry.Finite(p.X, p.Y);
                    return new Point((int)Math.Round(p.X), (int)Math.Round(p.Y));
                }).ToArray();
                if (points.Length < 3) continue;
                Cv2.FillPoly(mask, [points], Scalar.All(255), LineTypes.Link8);
            }
            if (expand > 0)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * expand + 1, 2 * expand + 1));
                Cv2.Dilate(mask, mask, kernel);
            }
            if (invert) Cv2.BitwiseNot(mask, mask);
            cancellationToken.ThrowIfCancellationRequested();

            var region = RegionGapMath.RegionFromMask(mask, cancellationToken);
            var area = RegionGapMath.RegionArea(region);
            var image = VisionImage.Own(mask.Clone());
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", region.Components.Count > 0),
                    ("count", region.Components.Count), ("area", area), ("width", width), ("height", height),
                    ("allContours", all), ("expand", expand), ("invert", invert));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["region"] = VisionValue.Region(region), ["image"] = VisionValue.Image(image),
                        ["found"] = VisionValue.Boolean(region.Components.Count > 0),
                        ["count"] = VisionValue.Integer(region.Components.Count), ["area"] = VisionValue.Integer(area),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["found"] = region.Components.Count > 0, ["count"] = region.Components.Count, ["area"] = area,
                        ["width"] = width, ["height"] = height, ["allContours"] = all, ["expand"] = expand,
                        ["invert"] = invert, ["unit"] = "px", ["algorithmBackend"] = "managed",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            mask.Dispose();
        }
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

        var mask = new Mat(region.Height, region.Width, MatType.CV_8UC1, Scalar.All(background));
        try
        {
            var step = (int)mask.Step();
            var buffer = new byte[step * region.Height];
            if (background != 0) Array.Fill(buffer, (byte)background);
            long area = 0;
            foreach (var component in region.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var run in component.Runs)
                {
                    var length = run.EndColumn - run.StartColumn + 1;
                    Array.Fill(buffer, (byte)foreground, run.Row * step + run.StartColumn, length);
                    area += length;
                }
            }
            Marshal.Copy(buffer, 0, mask.Data, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();

            var image = VisionImage.Own(mask);
            mask = null!;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("width", region.Width), ("height", region.Height),
                    ("count", region.Components.Count), ("area", area), ("foreground", foreground), ("background", background));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image), ["width"] = VisionValue.Integer(region.Width),
                        ["height"] = VisionValue.Integer(region.Height), ["area"] = VisionValue.Integer(area),
                        ["count"] = VisionValue.Integer(region.Components.Count), ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["width"] = region.Width, ["height"] = region.Height, ["count"] = region.Components.Count,
                        ["area"] = area, ["foreground"] = foreground, ["background"] = background,
                        ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            mask?.Dispose();
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
        if (!closed) throw new InvalidOperationException("contour.convexity requires a closed contour: an open polyline has no interior.");
        var minDepth = node.GetDouble("minDefectDepth", .5);
        if (!double.IsFinite(minDepth) || minDepth < 0) throw new InvalidOperationException("contour.convexity minDefectDepth must be finite and >= 0.");
        var maxDepth = node.GetDouble("maxDefectDepth", 0);
        if (!double.IsFinite(maxDepth) || maxDepth < 0) throw new InvalidOperationException("contour.convexity maxDefectDepth must be finite and >= 0 (0 disables the gate).");

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
            ("maxDefectDepth", deepest), ("defectCount", defects.Count), ("withinTolerance", within));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["pointCount"] = VisionValue.Integer(points.Length),
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
