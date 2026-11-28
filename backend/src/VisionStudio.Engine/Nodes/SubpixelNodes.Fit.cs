using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;

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
        var maxRms = node.GetDouble("maxRmsError", 0);
        if (!double.IsFinite(maxRms) || maxRms is < 0 or > 100000)
            throw new InvalidOperationException("measure.fitEllipse maxRmsError must be finite within 0..100000 (0 disables the gate).");
        var maxRadial = node.GetDouble("maxRadialError", 0);
        if (!double.IsFinite(maxRadial) || maxRadial is < 0 or > 100000)
            throw new InvalidOperationException("measure.fitEllipse maxRadialError must be finite within 0..100000 (0 disables the gate).");
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
        // 径向残差：把点转到椭圆主轴坐标系，r=1 表示落在椭圆上。
        // 用 (r-1)*|p-c| 近似最近点距离，足以识别混入的邻边和毛刺，不引入迭代求解。
        var semiMajor = width / 2;
        var semiMinor = height / 2;
        var radians = angle * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var residuals = new double[contour.Points.Count];
        var sumSquares = 0d;
        var maxRadialError = 0d;
        for (var i = 0; i < contour.Points.Count; i++)
        {
            var dx = contour.Points[i].X - center.X;
            var dy = contour.Points[i].Y - center.Y;
            var localX = (dx * cos + dy * sin) / Math.Max(semiMajor, 1e-9);
            var localY = (-dx * sin + dy * cos) / Math.Max(semiMinor, 1e-9);
            var factor = Math.Sqrt(localX * localX + localY * localY);
            var residual = Math.Abs(factor - 1) * Math.Sqrt(dx * dx + dy * dy);
            residuals[i] = residual;
            sumSquares += residual * residual;
            if (residual > maxRadialError) maxRadialError = residual;
        }
        var rmsError = Math.Sqrt(sumSquares / residuals.Length);
        var rmsPassed = maxRms <= 0 || rmsError <= maxRms;
        var radialPassed = maxRadial <= 0 || maxRadialError <= maxRadial;
        var gatesEnabled = maxRms > 0 || maxRadial > 0;
        var found = rmsPassed && radialPassed;
        var reason = !rmsPassed ? "rms-exceeded" : !radialPassed ? "radial-error-exceeded" : "ok";
        var evaluationState = !gatesEnabled ? "not-evaluated" : found ? "pass" : "fail";
        // 长轴方向无极性：反向点序只会让 angle 相差 180°，angleMod180 保持稳定。
        var angleMod180 = VisionTransform2D.NormalizeAngleDeg(angle) % 180;
        if (angleMod180 < 0) angleMod180 += 180;

        var diag = CoordinateValidation.Diagnostics(Type,
            ("selectedIndex", selectedIndex), ("candidateCount", contours.Count), ("selectBy", selectBy), ("selectionMetric", "contour-area"),
            ("pointCount", contour.Points.Count),
            ("width", width), ("height", height), ("angleDeg", angle), ("angleMod180", angleMod180), ("axisRatio", axisRatio),
            ("rmsError", rmsError), ("maxRadialError", maxRadialError),
            ("maxRmsError", maxRms), ("maxRadialErrorLimit", maxRadial),
            ("gatesEnabled", gatesEnabled), ("found", found), ("reason", reason), ("evaluationState", evaluationState),
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
                ["angleMod180"] = VisionValue.Double(angleMod180),
                ["semiMajor"] = VisionValue.Double(semiMajor),
                ["semiMinor"] = VisionValue.Double(semiMinor),
                ["axisPolarityKnown"] = VisionValue.Boolean(false),
                ["axisRatio"] = VisionValue.Double(axisRatio),
                ["rmsError"] = VisionValue.Double(rmsError),
                ["maxRadialError"] = VisionValue.Double(maxRadialError),
                ["selectedIndex"] = VisionValue.Integer(selectedIndex),
                ["candidateCount"] = VisionValue.Integer(contours.Count),
                ["selectionMetric"] = VisionValue.String("contour-area"),
                ["found"] = VisionValue.Boolean(found),
                ["reason"] = VisionValue.String(reason),
                ["evaluationState"] = VisionValue.String(evaluationState),
                ["count"] = VisionValue.Integer(contours.Count),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["selectedIndex"] = selectedIndex,
                ["selectBy"] = selectBy,
                ["selectionMetric"] = "contour-area",
                ["candidateCount"] = contours.Count,
                ["center"] = $"{center.X:0.###},{center.Y:0.###}",
                ["width"] = Math.Round(width, 3),
                ["height"] = Math.Round(height, 3),
                ["angleDeg"] = Math.Round(angle, 3),
                ["angleMod180"] = Math.Round(angleMod180, 3),
                ["axisRatio"] = Math.Round(axisRatio, 4),
                ["rmsError"] = Math.Round(rmsError, 4),
                ["maxRadialError"] = Math.Round(maxRadialError, 4),
                ["found"] = found, ["reason"] = reason, ["evaluationState"] = evaluationState,
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
