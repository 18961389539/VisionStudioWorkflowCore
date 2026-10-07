using System.Text.Json;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed record FrameTransformDefinition(
    string SourceFrame,
    string TargetFrame,
    string Unit,
    double X,
    double Y,
    double AngleDeg);

public sealed class ConstantTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformConstant";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var source = node.GetString("sourceFrame", "Workpiece");
        var target = node.GetString("targetFrame", "RobotBase");
        var unit = node.GetString("unit", "mm");
        var x = node.GetDouble("x", 0); var y = node.GetDouble("y", 0); var angle = node.GetDouble("angleDeg", 0);
        var transform = VisionTransform2DMath.Rigid(source, target, unit, x, y, angle);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["transform"] = VisionValue.Transform(transform) },
            new Dictionary<string, object?> { ["transform"] = $"{source} → {target}", ["x"] = x, ["y"] = y, ["angleDeg"] = angle, ["unit"] = unit }));
    }
}

public sealed class InverseTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformInverse";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var input = context.Require<VisionTransform2D>("transform");
        var output = VisionTransform2DMath.Inverse(input);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["transform"] = VisionValue.Transform(output) },
            new Dictionary<string, object?> { ["transform"] = $"{output.SourceFrame} → {output.TargetFrame}" }));
    }
}

public sealed class ComposeTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCompose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var a = context.Require<VisionTransform2D>("transformA");
        var b = context.Require<VisionTransform2D>("transformB");
        var output = VisionTransform2DMath.Compose(a, b);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["transform"] = VisionValue.Transform(output) },
            new Dictionary<string, object?> { ["transform"] = $"{output.SourceFrame} → {output.TargetFrame}", ["via"] = a.TargetFrame }));
    }
}

public sealed class FrameTreeNode : IVisionNodeExecutor
{
    public string Type => "coordinate.frameTree";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var json = node.GetString("transformsJson", "[]");
        var defs = JsonSerializer.Deserialize<List<FrameTransformDefinition>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var transforms = defs.Select(x => VisionTransform2DMath.Rigid(x.SourceFrame, x.TargetFrame, x.Unit, x.X, x.Y, x.AngleDeg)).ToArray();
        var tree = new VisionFrameTree2D(transforms);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["tree"] = VisionValue.FrameTree(tree) },
            new Dictionary<string, object?> { ["transforms"] = transforms.Length, ["frames"] = transforms.SelectMany(x => new[] { x.SourceFrame, x.TargetFrame }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }));
    }
}

public sealed class ResolveFrameTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.frameResolve";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var tree = context.Require<VisionFrameTree2D>("tree");
        var source = node.GetString("sourceFrame", "Workpiece");
        var target = node.GetString("targetFrame", "RobotBase");
        var result = tree.Resolve(source, target);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["transform"] = VisionValue.Transform(result.Transform), ["path"] = VisionValue.String(string.Join(" → ", result.Path)) },
            new Dictionary<string, object?> { ["path"] = string.Join(" → ", result.Path), ["sourceUnit"] = result.Transform.SourceUnit, ["targetUnit"] = result.Transform.TargetUnit }));
    }
}

public sealed class CoordinatePoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.pose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var pose = new VisionCoordinatePose2D(
            node.GetDouble("x", 0), node.GetDouble("y", 0), node.GetDouble("thetaDeg", 0),
            node.GetString("frame", "RobotBase"), node.GetString("unit", "mm"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["pose"] = VisionValue.CoordinatePose(pose) },
            new Dictionary<string, object?> { ["pose"] = $"X={pose.X:0.###}, Y={pose.Y:0.###}, R={pose.ThetaDeg:0.###}°", ["frame"] = pose.Frame, ["unit"] = pose.Unit }));
    }
}

public sealed class PoseToTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.poseToTransform";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var pose = context.Require<VisionCoordinatePose2D>("pose");
        var sourceFrame = node.GetString("sourceFrame", "Tool");
        var transform = VisionTransform2DMath.Rigid(sourceFrame, pose.Frame, pose.Unit, pose.X, pose.Y, pose.ThetaDeg);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["transform"] = VisionValue.Transform(transform) },
            new Dictionary<string, object?> { ["transform"] = $"{sourceFrame} → {pose.Frame}", ["x"] = pose.X, ["y"] = pose.Y, ["angleDeg"] = pose.ThetaDeg }));
    }
}

public sealed class TransformCoordinatePointNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCoordinatePoint";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var point = context.Require<VisionCoordinatePoint2D>("point");
        var transform = context.Require<VisionTransform2D>("transform");
        var q = VisionTransform2DMath.Apply(transform, point);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["point"] = VisionValue.CoordinatePoint(q) },
            new Dictionary<string, object?> { ["point"] = $"({q.X:0.###},{q.Y:0.###}) {q.Unit}", ["frame"] = q.Frame }));
    }
}

public sealed class TransformCoordinatePoseNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformCoordinatePose";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var pose = context.Require<VisionCoordinatePose2D>("pose");
        var transform = context.Require<VisionTransform2D>("transform");
        var q = VisionTransform2DMath.Apply(transform, pose);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["pose"] = VisionValue.CoordinatePose(q) },
            new Dictionary<string, object?> { ["pose"] = $"X={q.X:0.###}, Y={q.Y:0.###}, R={q.ThetaDeg:0.###}°", ["frame"] = q.Frame, ["unit"] = q.Unit }));
    }
}

public sealed class RobotGuidance2DNode : IVisionNodeExecutor
{
    public string Type => "robot.guidance2d";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var pose = context.Require<VisionCoordinatePose2D>("pose");
        var expectedFrame = node.GetString("expectedFrame", "RobotBase");
        if (!pose.Frame.Equals(expectedFrame, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Robot guidance expected pose in '{expectedFrame}', got '{pose.Frame}'. Resolve the frame transform first.");

        var offsetX = node.GetDouble("offsetX", 0); var offsetY = node.GetDouble("offsetY", 0); var angleOffset = node.GetDouble("angleOffsetDeg", 0);
        var rad = pose.ThetaDeg * Math.PI / 180.0;
        var x = pose.X + Math.Cos(rad) * offsetX - Math.Sin(rad) * offsetY;
        var y = pose.Y + Math.Sin(rad) * offsetX + Math.Cos(rad) * offsetY;
        var r = VisionTransform2D.NormalizeAngleDeg(pose.ThetaDeg + angleOffset);
        var robot = node.GetString("robot", "ABB");
        var mode = node.GetString("guidanceMode", "EyeToHand");
        var target = new VisionRobotTarget2D(x, y, r, pose.Frame, pose.Unit, robot, mode);
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["target"] = VisionValue.RobotTarget(target), ["x"] = VisionValue.Double(x), ["y"] = VisionValue.Double(y), ["r"] = VisionValue.Double(r)
            },
            new Dictionary<string, object?> { ["robot"] = robot, ["mode"] = mode, ["target"] = $"X={x:0.###}, Y={y:0.###}, R={r:0.###}°", ["frame"] = pose.Frame, ["unit"] = pose.Unit }));
    }
}

/// <summary>
/// Planar J4/TCP compensation hook. The raw RobotTarget2D is interpreted as the desired TCP pose.
/// The net eccentric vector is (TCP offset - calibrated J4 pivot offset) in tool-local coordinates.
/// The output XY is the J4 pivot command position needed to place the TCP at the desired pose.
/// This is intentionally 2D and does not replace robot kinematics or safety validation.
/// </summary>
public sealed class J4TcpCompensationNode : IVisionNodeExecutor
{
    public string Type => "robot.j4TcpCompensation";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var target = context.Require<VisionRobotTarget2D>("target");
        var tcpX = node.GetDouble("tcpOffsetX", 0); var tcpY = node.GetDouble("tcpOffsetY", 0);
        var pivotX = node.GetDouble("j4PivotOffsetX", 0); var pivotY = node.GetDouble("j4PivotOffsetY", 0);
        var zero = node.GetDouble("angleZeroOffsetDeg", 0);
        var commandR = VisionTransform2D.NormalizeAngleDeg(target.RDeg + zero);
        var ex = tcpX - pivotX; var ey = tcpY - pivotY;
        var rad = commandR * Math.PI / 180.0;
        var rotatedX = Math.Cos(rad) * ex - Math.Sin(rad) * ey;
        var rotatedY = Math.Sin(rad) * ex + Math.Cos(rad) * ey;
        var commandX = target.X - rotatedX;
        var commandY = target.Y - rotatedY;
        var compensated = target with { X = commandX, Y = commandY, RDeg = commandR };
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["target"] = VisionValue.RobotTarget(compensated), ["x"] = VisionValue.Double(commandX), ["y"] = VisionValue.Double(commandY), ["r"] = VisionValue.Double(commandR)
            },
            new Dictionary<string, object?>
            {
                ["desiredTcp"] = $"X={target.X:0.###}, Y={target.Y:0.###}, R={target.RDeg:0.###}°",
                ["commandPivot"] = $"X={commandX:0.###}, Y={commandY:0.###}, R={commandR:0.###}°",
                ["eccentricity"] = $"({ex:0.###},{ey:0.###}) {target.Unit}"
            }));
    }
}
