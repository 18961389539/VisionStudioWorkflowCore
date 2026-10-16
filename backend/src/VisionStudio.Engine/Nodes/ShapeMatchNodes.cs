using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>JLVision native shape-model matching with cached models and subpixel pose output.</summary>
public sealed class ShapeMatchNode : IVisionNodeExecutor
{
    public string Type => "match.shape";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(JLVisionMatching.Execute(context, node, true, cancellationToken));
}

/// <summary>
/// 5C-2 · roi.transform：定位后 ROI 修正。以基准位姿（ROI  authored 时的位姿）与当前位姿
/// 计算刚体变换，使测量区域跟随工件移动/旋转：
///   · p' = R(θ)·(p - base) + current，θ = currentTheta - baseTheta；
///   · 矩形 ROI 在 θ=0 时平移保持矩形；θ≠0 时转为多边形（随工件旋转，掩膜语义保持）；
///   · 圆形 ROI 平移；多边形 ROI 整体刚体变换。
/// roi 输入端口优先于 roi 参数；currentPose 输入端口优先于 currentX/Y/θ 参数。
/// </summary>
public sealed class RoiTransformNode : IVisionNodeExecutor
{
    public string Type => "roi.transform";
    private const double MaxRoiCoordinate = 10_000_000;

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var debugImage = context.TryGetValue("debugImage", out _)
            ? context.RequireImage("debugImage")
            : null;

        var basePose = new VisionPose2D(node.GetDouble("baseX", 0), node.GetDouble("baseY", 0), node.GetDouble("baseThetaDeg", 0));
        var currentPose = new VisionPose2D(node.GetDouble("currentX", 0), node.GetDouble("currentY", 0), node.GetDouble("currentThetaDeg", 0));
        if (context.TryGetValue("currentPose", out var poseValue))
            currentPose = poseValue.Require<VisionPose2D>("currentPose");
        if (new[] { basePose.X, basePose.Y, basePose.ThetaDeg, currentPose.X, currentPose.Y, currentPose.ThetaDeg }.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("Base/current pose components must be finite (non-finite pose received).");
        if (Math.Abs(basePose.X) > MaxRoiCoordinate || Math.Abs(basePose.Y) > MaxRoiCoordinate
            || Math.Abs(currentPose.X) > MaxRoiCoordinate || Math.Abs(currentPose.Y) > MaxRoiCoordinate)
            throw new InvalidOperationException($"Base/current pose coordinates must be within ±{MaxRoiCoordinate:0} px.");

        // A single node execution freezes pixels and the actual connected pose together.
        // This also works before any ROI exists, without fabricating a correction result.
        if (node.GetBool("captureReference", false))
        {
            if (debugImage is null || debugImage.Mat.Empty())
                throw new InvalidOperationException("Connect a non-empty debugImage before capturing the reference.");
            Cv2.ImEncode(".png", debugImage.Mat, out var bytes);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(debugImage.CloneOwned()),
                    ["pose"] = VisionValue.Pose(currentPose)
                },
                new Dictionary<string, object?>
                {
                    ["referenceCaptured"] = true, ["referenceImage"] = Convert.ToBase64String(bytes),
                    ["referenceWidth"] = debugImage.Width, ["referenceHeight"] = debugImage.Height,
                    ["referenceX"] = currentPose.X, ["referenceY"] = currentPose.Y, ["referenceThetaDeg"] = currentPose.ThetaDeg
                }, []));
        }
        if (debugImage is not null && !string.IsNullOrEmpty(node.GetString("referenceImage", ""))
            && (node.GetInt("referenceWidth", 0) != debugImage.Width || node.GetInt("referenceHeight", 0) != debugImage.Height))
            throw new InvalidOperationException("Scene dimensions differ from the saved reference. Capture a new reference before correcting the ROI.");

        VisionRoi roi;
        if (context.TryGetValue("roi", out var roiValue))
            roi = roiValue.Require<VisionRoi>("roi");
        else
        {
            var parameterRoi = node.GetRoi();
            if (parameterRoi is null)
            {
                if (node.GetBool("debugImagePreviewOnly", false) && debugImage is not null)
                    return ValueTask.FromResult(new NodeExecutorResult(
                        new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(debugImage.CloneOwned()) },
                        new Dictionary<string, object?> { ["debugImageConnected"] = true, ["debugImagePreviewReady"] = true },
                        []));
                throw new InvalidOperationException("ROI transform requires either a connected 'roi' input or a 'roi' parameter.");
            }
            roi = parameterRoi;
        }
        ValidateTransformRoi(roi);

        var anglePeriodDeg = node.GetDouble("anglePeriodDeg", 360);
        if (!double.IsFinite(anglePeriodDeg) || anglePeriodDeg <= 0 || anglePeriodDeg > 360)
            throw new InvalidOperationException("Angle period must be greater than 0 and at most 360 degrees.");
        var thetaDeg = (currentPose.ThetaDeg - basePose.ThetaDeg) % anglePeriodDeg;
        var halfPeriodDeg = anglePeriodDeg / 2;
        if (thetaDeg > halfPeriodDeg) thetaDeg -= anglePeriodDeg;
        else if (thetaDeg < -halfPeriodDeg) thetaDeg += anglePeriodDeg;
        var radians = thetaDeg * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        VisionPoint2D Apply(VisionPoint2D p)
        {
            var dx = p.X - basePose.X;
            var dy = p.Y - basePose.Y;
            // 图像坐标系（y 向下）中的逆时针旋转矩阵：与 match.shape 的角度语义一致。
            return new VisionPoint2D(cos * dx + sin * dy + currentPose.X, -sin * dx + cos * dy + currentPose.Y);
        }

        VisionRoi transformed;
        List<VisionPoint2D> region;
        double regionPadding = 0;
        switch (roi.Type)
        {
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                if (strokes.Count is < 1 or > 10_000) throw new InvalidOperationException("The brush ROI needs 1..10000 strokes.");
                var points = strokes.Where(stroke => stroke.Mode == VisionRoiStrokeMode.Add).SelectMany(stroke => stroke.Points ?? []).ToArray();
                if (points.Length == 0 || strokes.SelectMany(stroke => stroke.Points ?? []).Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    throw new InvalidOperationException("The brush ROI needs finite points in at least one add stroke.");
                if (strokes.Any(stroke => stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                    || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024 || stroke.Points is null || stroke.Points.Count == 0))
                    throw new InvalidOperationException("Brush ROI strokes need a valid mode, positive width and points.");
                var movedStrokes = strokes.Select(stroke => stroke with
                {
                    Points = stroke.Points.Select(point =>
                    {
                        var moved = Apply(new VisionPoint2D(point.X, point.Y));
                        return new VisionRoiPoint(moved.X, moved.Y);
                    }).ToArray()
                }).ToArray();
                transformed = roi with { Strokes = movedStrokes };
                region = points.Select(point => Apply(new VisionPoint2D(point.X, point.Y))).Distinct().ToList();
                regionPadding = strokes.Where(stroke => stroke.Mode == VisionRoiStrokeMode.Add).Max(stroke => stroke.Width / 2);
                break;
            }
            case VisionRoiType.Circle:
            {
                var radius = roi.Radius ?? 0;
                if (radius < 0) throw new InvalidOperationException("The circle ROI radius must be non-negative.");
                var translatedCenter = Apply(new VisionPoint2D(roi.X ?? 0, roi.Y ?? 0));
                transformed = roi with { X = translatedCenter.X, Y = translatedCenter.Y };
                region = [];
                break;
            }
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                if (points.Count < 3) throw new InvalidOperationException("The polygon ROI needs at least 3 points.");
                var moved = points.Select(p => Apply(new VisionPoint2D(p.X, p.Y))).ToArray();
                transformed = roi with { Points = moved.Select(p => new VisionRoiPoint(p.X, p.Y)).ToArray() };
                region = moved.ToList();
                break;
            }
            default:
            {
                var x = roi.X ?? 0;
                var y = roi.Y ?? 0;
                var width = roi.Width ?? 0;
                var height = roi.Height ?? 0;
                if (width < 0 || height < 0) throw new InvalidOperationException("The rectangle ROI width/height must be non-negative.");
                var corners = new[]
                {
                    new VisionPoint2D(x, y),
                    new VisionPoint2D(x + width, y),
                    new VisionPoint2D(x + width, y + height),
                    new VisionPoint2D(x, y + height)
                }.Select(Apply).ToArray();
                if (Math.Abs(thetaDeg) < 1e-9)
                {
                    var minX = corners.Min(c => c.X);
                    var minY = corners.Min(c => c.Y);
                    transformed = roi with { X = minX, Y = minY, Width = width, Height = height };
                }
                else
                {
                    // 旋转后不再保持轴对齐：转为多边形 ROI（4 角），掩膜语义随工件保持。
                    transformed = new VisionRoi(VisionRoiType.Polygon, Points: corners.Select(c => new VisionRoiPoint(c.X, c.Y)).ToArray());
                }
                region = corners.ToList();
                break;
            }
        }

        // Reject invalid coordinates here, before they reach image-mask rasterizers or measurement nodes.
        ValidateTransformRoi(transformed);
        VisionRectangle2D rectangle;
        VisionPoint2D center;
        VisionCircle circle;
        if (transformed.Type == VisionRoiType.Circle)
        {
            center = new VisionPoint2D(transformed.X ?? 0, transformed.Y ?? 0);
            var radius = transformed.Radius ?? 0;
            // Preserve the exact circle bounds instead of measuring an inscribed polygon approximation.
            rectangle = new VisionRectangle2D(center.X, center.Y, radius * 2, radius * 2);
            circle = new VisionCircle(center.X, center.Y, radius);
        }
        else
        {
            if (region.Count == 1)
            {
                center = region[0];
                rectangle = new VisionRectangle2D(center.X, center.Y, regionPadding * 2, regionPadding * 2);
            }
            else
            {
                var pointsF = region.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
                var rect = JLVisionOperators.MinAreaRect(pointsF);
                center = new VisionPoint2D(rect.Center.X, rect.Center.Y);
                rectangle = new VisionRectangle2D(
                    center.X, center.Y,
                    Math.Max(rect.Size.Width, rect.Size.Height) + regionPadding * 2,
                    Math.Min(rect.Size.Width, rect.Size.Height) + regionPadding * 2,
                    rect.Size.Width < rect.Size.Height ? VisionTransform2D.NormalizeAngleDeg(rect.Angle + 90) : rect.Angle);
            }
            var boundingRadius = region.Max(p => MeasurementMath.Hypot(p.X - center.X, p.Y - center.Y)) + regionPadding;
            circle = new VisionCircle(center.X, center.Y, boundingRadius);
        }

        var transform = new VisionTransform2D(
            "BasePose", "CurrentPose", "px",
            cos, sin, currentPose.X - (cos * basePose.X + sin * basePose.Y),
            -sin, cos, currentPose.Y - (-sin * basePose.X + cos * basePose.Y),
            0, 0, 1);

        var outputs = new Dictionary<string, VisionValue>
        {
            ["roi"] = VisionValue.Roi(transformed),
            ["rectangle"] = VisionValue.Rectangle(rectangle),
            ["circle"] = VisionValue.Circle(circle),
            ["center"] = VisionValue.Point(center),
            ["pose"] = VisionValue.Pose(currentPose),
            ["transform"] = VisionValue.Transform(transform)
        };
        var overlays = new List<VisionOverlay>();
        if (debugImage is not null)
        {
            // A cloned pass-through image gives the run-image viewer the same coordinate space
            // in which both ROIs and the base/current poses are defined.
            outputs["image"] = VisionValue.Image(debugImage.CloneOwned());
            overlays.AddRange(RoiDebugOverlays(roi, "roi-transform-base", "基准 ROI", "#faad14"));
            overlays.AddRange(RoiDebugOverlays(transformed, "roi-transform-corrected", "修正后 ROI", "#13c2c2"));
            overlays.Add(new VisionOverlay("roi-transform-base-pose", VisionOverlayType.Point,
                Label: "基准位姿", X: basePose.X, Y: basePose.Y, Stroke: "#faad14", StrokeWidth: 3));
            overlays.Add(new VisionOverlay("roi-transform-base-pose-label", VisionOverlayType.Text,
                Label: $"基准 ({basePose.X:0.#}, {basePose.Y:0.#}, {basePose.ThetaDeg:0.#}°)",
                X: basePose.X + 8, Y: basePose.Y - 8, Stroke: "#ffd666"));
            overlays.Add(new VisionOverlay("roi-transform-pose-shift", VisionOverlayType.Line,
                X: basePose.X, Y: basePose.Y, X2: currentPose.X, Y2: currentPose.Y,
                Stroke: "#b37feb", StrokeWidth: 2));
            overlays.Add(new VisionOverlay("roi-transform-current-pose", VisionOverlayType.Point,
                Label: "当前位姿", X: currentPose.X, Y: currentPose.Y, Stroke: "#722ed1", StrokeWidth: 3));
            overlays.Add(new VisionOverlay("roi-transform-current-pose-label", VisionOverlayType.Text,
                Label: $"当前 ({currentPose.X:0.#}, {currentPose.Y:0.#}, {currentPose.ThetaDeg:0.#}°) · Δθ {thetaDeg:0.##}°",
                X: currentPose.X + 8, Y: currentPose.Y + 16, Stroke: "#d3adf7"));
        }

        return ValueTask.FromResult(new NodeExecutorResult(
            outputs,
            new Dictionary<string, object?>
            {
                ["roiType"] = transformed.Type.ToString(),
                ["thetaDeg"] = Math.Round(thetaDeg, 4),
                ["basePose"] = $"{basePose.X:0.##},{basePose.Y:0.##},{basePose.ThetaDeg:0.##}",
                ["currentPose"] = $"{currentPose.X:0.##},{currentPose.Y:0.##},{currentPose.ThetaDeg:0.##}",
                ["regionCenter"] = $"{center.X:0.##},{center.Y:0.##}",
                ["boundingRadius"] = Math.Round(circle.Radius, 2),
                ["debugImageConnected"] = outputs.ContainsKey("image")
            },
            overlays));
    }

    private static IReadOnlyList<VisionOverlay> RoiDebugOverlays(VisionRoi roi, string idPrefix, string label, string stroke)
    {
        switch (roi.Type)
        {
            case VisionRoiType.Circle:
            {
                var x = roi.X ?? 0;
                var y = roi.Y ?? 0;
                var radius = roi.Radius ?? 0;
                return
                [
                    new VisionOverlay($"{idPrefix}-shape", VisionOverlayType.Circle, X: x, Y: y, Radius: radius, Stroke: stroke, StrokeWidth: 2.5),
                    new VisionOverlay($"{idPrefix}-label", VisionOverlayType.Text, Label: label, X: x + radius + 4, Y: y - radius - 4, Stroke: stroke)
                ];
            }
            case VisionRoiType.Polygon:
            {
                var points = (roi.Points ?? []).Select(point => new VisionOverlayPoint(point.X, point.Y)).ToArray();
                if (points.Length == 0) return [];
                return
                [
                    new VisionOverlay($"{idPrefix}-shape", VisionOverlayType.Polygon, Points: points, Stroke: stroke, StrokeWidth: 2.5),
                    new VisionOverlay($"{idPrefix}-label", VisionOverlayType.Text, Label: label, X: points[0].X, Y: points[0].Y - 8, Stroke: stroke)
                ];
            }
            case VisionRoiType.Brush:
            {
                var overlays = new List<VisionOverlay>();
                foreach (var (brushStroke, index) in (roi.Strokes ?? []).Take(256).Select((item, index) => (item, index)))
                {
                    var points = SampleBrushPoints(brushStroke.Points, 96);
                    if (points.Length == 0) continue;
                    var brushColor = brushStroke.Mode == VisionRoiStrokeMode.Erase ? "#ff4d4f" : stroke;
                    var brushLabel = $"{label} · {(brushStroke.Mode == VisionRoiStrokeMode.Erase ? "擦除" : "添加")}";
                    if (points.Length == 1)
                        overlays.Add(new VisionOverlay($"{idPrefix}-stroke-{index}", VisionOverlayType.Circle,
                            Label: brushLabel, X: points[0].X, Y: points[0].Y, Radius: brushStroke.Width / 2,
                            Stroke: brushColor, StrokeWidth: 2));
                    else
                        overlays.Add(new VisionOverlay($"{idPrefix}-stroke-{index}", VisionOverlayType.Polyline,
                            Label: brushLabel, Points: points, Stroke: brushColor, StrokeWidth: brushStroke.Width));
                }
                return overlays;
            }
            default:
            {
                var x = roi.X ?? 0;
                var y = roi.Y ?? 0;
                var width = roi.Width ?? 0;
                var height = roi.Height ?? 0;
                return
                [
                    new VisionOverlay($"{idPrefix}-shape", VisionOverlayType.Rectangle,
                        X: x, Y: y, Width: width, Height: height, Stroke: stroke, StrokeWidth: 2.5),
                    new VisionOverlay($"{idPrefix}-label", VisionOverlayType.Text, Label: label, X: x, Y: y - 8, Stroke: stroke)
                ];
            }
        }
    }

    private static void ValidateTransformRoi(VisionRoi roi)
    {
        static bool Finite(params double?[] values) => values.All(value => value.HasValue && double.IsFinite(value.Value));
        static bool Bounded(params double?[] values) => values.All(value => value.HasValue && Math.Abs(value.Value) <= MaxRoiCoordinate);
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
            {
                var x = roi.X ?? 0;
                var y = roi.Y ?? 0;
                var width = roi.Width ?? 0;
                var height = roi.Height ?? 0;
                if (!Finite(roi.X, roi.Y, roi.Width, roi.Height) || !Bounded(roi.X, roi.Y, roi.Width, roi.Height)
                    || width <= 0 || height <= 0 || !double.IsFinite(x + width) || !double.IsFinite(y + height)
                    || Math.Abs(x + width) > MaxRoiCoordinate || Math.Abs(y + height) > MaxRoiCoordinate)
                    throw new InvalidOperationException($"The rectangle ROI needs positive dimensions and coordinates within ±{MaxRoiCoordinate:0} px.");
                break;
            }
            case VisionRoiType.Circle:
                if (!Finite(roi.X, roi.Y, roi.Radius) || !Bounded(roi.X, roi.Y, roi.Radius) || roi.Radius <= 0)
                    throw new InvalidOperationException($"The circle ROI needs a positive radius and coordinates within ±{MaxRoiCoordinate:0} px.");
                break;
            case VisionRoiType.Polygon:
            {
                var points = roi.Points ?? [];
                if (points.Count < 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                    || Math.Abs(point.X) > MaxRoiCoordinate || Math.Abs(point.Y) > MaxRoiCoordinate))
                    throw new InvalidOperationException($"The polygon ROI needs at least three finite points within ±{MaxRoiCoordinate:0} px.");
                var area2 = points.Select((point, index) =>
                {
                    var next = points[(index + 1) % points.Count];
                    return point.X * next.Y - next.X * point.Y;
                }).Sum();
                if (!double.IsFinite(area2) || Math.Abs(area2) < 1e-6)
                    throw new InvalidOperationException("The polygon ROI must enclose a finite non-zero area.");
                break;
            }
            case VisionRoiType.Brush:
            {
                var strokes = roi.Strokes ?? [];
                var totalPoints = 0;
                if (strokes.Count is < 1 or > 10_000 || !strokes.Any(stroke => stroke.Mode == VisionRoiStrokeMode.Add))
                    throw new InvalidOperationException("The brush ROI needs 1..10000 strokes and at least one add stroke.");
                foreach (var stroke in strokes)
                {
                    if (stroke.Mode is not (VisionRoiStrokeMode.Add or VisionRoiStrokeMode.Erase)
                        || !double.IsFinite(stroke.Width) || stroke.Width <= 0 || stroke.Width > 1024
                        || stroke.Points is null || stroke.Points.Count == 0
                        || stroke.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                            || Math.Abs(point.X) > MaxRoiCoordinate || Math.Abs(point.Y) > MaxRoiCoordinate))
                        throw new InvalidOperationException($"Brush ROI strokes need a valid mode, finite points within ±{MaxRoiCoordinate:0} px and a width between 0 and 1024 px.");
                    totalPoints = checked(totalPoints + stroke.Points.Count);
                    if (totalPoints > 500_000)
                        throw new InvalidOperationException("The brush ROI may contain at most 500000 points.");
                }
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported ROI type '{roi.Type}'.");
        }
    }

    private static VisionOverlayPoint[] SampleBrushPoints(IReadOnlyList<VisionRoiPoint> points, int maximum)
    {
        if (points.Count <= maximum) return points.Select(point => new VisionOverlayPoint(point.X, point.Y)).ToArray();
        var stride = (points.Count - 1d) / (maximum - 1);
        return Enumerable.Range(0, maximum)
            .Select(index => points[(int)Math.Round(index * stride)])
            .Select(point => new VisionOverlayPoint(point.X, point.Y))
            .ToArray();
    }

}
