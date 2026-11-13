using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 5B-4 · measure.fitEllipse：对轮廓做椭圆拟合（OpenCV FitEllipse），
/// 按面积/索引选中轮廓；输出旋转矩形（中心/长宽/角度）与位姿。
/// 点数不足 5 或退化（共线）时明确报错——这是参数/数据错误，不是"未找到"。
/// </summary>
public sealed class FitEllipseNode : IVisionNodeExecutor
{
    public string Type => "measure.fitEllipse";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var contours = context.Require<IReadOnlyList<VisionContour>>("contours");
        if (contours.Count == 0)
            throw new InvalidOperationException("Ellipse fitting requires at least one contour; the input list is empty.");

        var selectBy = node.GetString("selectBy", "largest").Trim().ToLowerInvariant();
        var index = node.GetInt("index", 0);
        if (selectBy is not ("largest" or "smallest" or "first" or "last" or "index"))
            throw new InvalidOperationException("measure.fitEllipse selectBy must be largest, smallest, first, last or index.");
        if (selectBy == "index" && (index < 0 || index >= contours.Count))
            throw new InvalidOperationException($"measure.fitEllipse index {index} is out of range for {contours.Count} contour(s).");
        var selectedIndex = selectBy switch
        {
            "smallest" => IndexOfExtreme(contours, x => x.Area, ascending: true),
            "first" => 0,
            "last" => contours.Count - 1,
            "index" => index,
            _ => IndexOfExtreme(contours, x => x.Area, ascending: false)
        };
        var contour = contours[selectedIndex];
        if (contour.Points.Count < 5)
            throw new InvalidOperationException(
                $"Ellipse fitting needs at least 5 contour points; selected contour #{selectedIndex} has {contour.Points.Count}.");

        var points = contour.Points.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        RotatedRect ellipse;
        try
        {
            ellipse = JLVisionOperators.FitEllipse(points);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Ellipse fit is degenerate for contour #{selectedIndex} (points may be collinear): {ex.Message}");
        }

        if (!double.IsFinite(ellipse.Center.X) || !double.IsFinite(ellipse.Center.Y) ||
            !double.IsFinite(ellipse.Size.Width) || !double.IsFinite(ellipse.Size.Height) ||
            ellipse.Size.Width <= 0 || ellipse.Size.Height <= 0)
            throw new InvalidOperationException("Ellipse fit produced an invalid rectangle.");

        // 归一化：width = 长轴、height = 短轴，angle = 长轴方向（逆时针，度）。
        double width = ellipse.Size.Width;
        double height = ellipse.Size.Height;
        double angle = ellipse.Angle;
        if (width < height)
        {
            (width, height) = (height, width);
            angle += 90;
        }
        angle = VisionTransform2D.NormalizeAngleDeg(angle);
        var rectangle = new VisionRectangle2D(ellipse.Center.X, ellipse.Center.Y, width, height, angle);
        var center = new VisionPoint2D(ellipse.Center.X, ellipse.Center.Y);

        var overlays = new[]
        {
            new VisionOverlay("fit-ellipse-rect", VisionOverlayType.Rectangle,
                Label: $"{width:0.#}x{height:0.#}@{angle:0.#}deg",
                X: rectangle.X, Y: rectangle.Y, Width: width, Height: height, AngleDeg: angle,
                Stroke: "#722ed1", StrokeWidth: 2)
        };

        var axisRatio = width / height;
        var diag = CoordinateValidation.Diagnostics(Type,
            ("selectedIndex", selectedIndex), ("selectBy", selectBy), ("pointCount", contour.Points.Count),
            ("width", width), ("height", height), ("angleDeg", angle), ("axisRatio", axisRatio),
            ("count", contours.Count));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["contours"] = VisionValue.Contours(contours),
                ["rectangle"] = VisionValue.Rectangle(rectangle),
                ["center"] = VisionValue.Point(center),
                ["pose"] = VisionValue.Pose(new VisionPose2D(center.X, center.Y, angle)),
                ["width"] = VisionValue.Double(width),
                ["height"] = VisionValue.Double(height),
                ["angle"] = VisionValue.Double(angle),
                ["axisRatio"] = VisionValue.Double(axisRatio),
                ["count"] = VisionValue.Integer(contours.Count),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["selectedIndex"] = selectedIndex,
                ["selectBy"] = selectBy,
                ["center"] = $"{center.X:0.###},{center.Y:0.###}",
                ["width"] = Math.Round(width, 3),
                ["height"] = Math.Round(height, 3),
                ["angleDeg"] = Math.Round(angle, 3),
                ["axisRatio"] = Math.Round(axisRatio, 4),
                ["pointCount"] = contour.Points.Count,
                ["diagnostics"] = diag
            },
            overlays));
    }

    private static int IndexOfExtreme(IReadOnlyList<VisionContour> contours, Func<VisionContour, double> key, bool ascending)
    {
        var best = 0;
        for (var i = 1; i < contours.Count; i++)
        {
            var compare = key(contours[i]).CompareTo(key(contours[best]));
            if (ascending ? compare < 0 : compare > 0) best = i;
        }
        return best;
    }
}
