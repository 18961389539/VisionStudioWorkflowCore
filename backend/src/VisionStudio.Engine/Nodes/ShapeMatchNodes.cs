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

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        VisionRoi roi;
        if (context.TryGetValue("roi", out var roiValue))
            roi = roiValue.Require<VisionRoi>("roi");
        else
        {
            var parameterRoi = node.GetRoi();
            if (parameterRoi is null)
                throw new InvalidOperationException("ROI transform requires either a connected 'roi' input or a 'roi' parameter.");
            roi = parameterRoi;
        }

        var basePose = new VisionPose2D(node.GetDouble("baseX", 0), node.GetDouble("baseY", 0), node.GetDouble("baseThetaDeg", 0));
        var currentPose = new VisionPose2D(node.GetDouble("currentX", 0), node.GetDouble("currentY", 0), node.GetDouble("currentThetaDeg", 0));
        if (context.TryGetValue("currentPose", out var poseValue))
        {
            var pose = poseValue.Require<VisionPose2D>("currentPose");
            if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.ThetaDeg))
                throw new InvalidOperationException("The connected currentPose has non-finite components.");
            currentPose = pose;
        }
        if (new[] { basePose.X, basePose.Y, basePose.ThetaDeg, currentPose.X, currentPose.Y, currentPose.ThetaDeg }.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("Base/current pose components must be finite.");

        var thetaDeg = currentPose.ThetaDeg - basePose.ThetaDeg;
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
        switch (roi.Type)
        {
            case VisionRoiType.Circle:
            {
                var radius = roi.Radius ?? 0;
                if (radius < 0) throw new InvalidOperationException("The circle ROI radius must be non-negative.");
                var translatedCenter = Apply(new VisionPoint2D(roi.X ?? 0, roi.Y ?? 0));
                transformed = roi with { X = translatedCenter.X, Y = translatedCenter.Y };
                region = CircleRegion(translatedCenter, radius);
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

        var pointsF = region.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        var rect = JLVisionOperators.MinAreaRect(pointsF);
        var rectangle = new VisionRectangle2D(
            rect.Center.X, rect.Center.Y,
            Math.Max(rect.Size.Width, rect.Size.Height), Math.Min(rect.Size.Width, rect.Size.Height),
            rect.Size.Width < rect.Size.Height ? VisionTransform2D.NormalizeAngleDeg(rect.Angle + 90) : rect.Angle);
        var center = new VisionPoint2D(rect.Center.X, rect.Center.Y);
        var circle = new VisionCircle(center.X, center.Y, region.Max(p => MeasurementMath.Hypot(p.X - center.X, p.Y - center.Y)));

        var transform = new VisionTransform2D(
            "BasePose", "CurrentPose", "px",
            cos, sin, currentPose.X - (cos * basePose.X + sin * basePose.Y),
            -sin, cos, currentPose.Y - (-sin * basePose.X + cos * basePose.Y),
            0, 0, 1);

        var overlays = new[]
        {
            new VisionOverlay("roi-transform-region", VisionOverlayType.Rectangle,
                Label: $"dtheta={thetaDeg:0.##}deg",
                X: rectangle.X, Y: rectangle.Y, Width: rectangle.Width, Height: rectangle.Height,
                AngleDeg: rectangle.AngleDeg, Stroke: "#13c2c2", StrokeWidth: 2)
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["roi"] = VisionValue.Roi(transformed),
                ["rectangle"] = VisionValue.Rectangle(rectangle),
                ["circle"] = VisionValue.Circle(circle),
                ["center"] = VisionValue.Point(center),
                ["pose"] = VisionValue.Pose(currentPose),
                ["transform"] = VisionValue.Transform(transform)
            },
            new Dictionary<string, object?>
            {
                ["roiType"] = transformed.Type.ToString(),
                ["thetaDeg"] = Math.Round(thetaDeg, 4),
                ["basePose"] = $"{basePose.X:0.##},{basePose.Y:0.##},{basePose.ThetaDeg:0.##}",
                ["currentPose"] = $"{currentPose.X:0.##},{currentPose.Y:0.##},{currentPose.ThetaDeg:0.##}",
                ["regionCenter"] = $"{center.X:0.##},{center.Y:0.##}",
                ["boundingRadius"] = Math.Round(circle.Radius, 2)
            },
            overlays));
    }

    private static List<VisionPoint2D> CircleRegion(VisionPoint2D center, double radius)
    {
        var points = new List<VisionPoint2D>(16);
        for (var i = 0; i < 16; i++)
        {
            var angle = i / 16.0 * 2 * Math.PI;
            points.Add(new VisionPoint2D(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius));
        }
        return points;
    }
}
