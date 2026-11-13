using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class ConstructLineNode : IVisionNodeExecutor
{
    public string Type => "geometry.lineConstruct";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var operation = node.GetString("operation", "perpendicular").ToLowerInvariant();
        var offsetParam = node.GetDouble("offset", 0);
        if (!double.IsFinite(offsetParam)) throw new InvalidOperationException("geometry.lineConstruct offset must be finite.");
        var span = node.GetDouble("spanScale", 1);
        if (!double.IsFinite(span) || span is <= 0 or > 100) throw new InvalidOperationException("geometry.lineConstruct spanScale must be within 0.01..100.");

        VisionLine2D? line = context.TryGetValue("line", out var lineValue) ? lineValue.Require<VisionLine2D>("line") : null;
        VisionLine2D? line2 = context.TryGetValue("line2", out var line2Value) ? line2Value.Require<VisionLine2D>("line2") : null;
        VisionPoint2D? point = context.TryGetValue("point", out var pointValue) ? pointValue.Require<VisionPoint2D>("point") : null;
        VisionCircle? circle = context.TryGetValue("circle", out var circleValue) ? circleValue.Require<VisionCircle>("circle") : null;

        VisionLine2D result; VisionLine2D? second = null; double? reportedOffset = null; double? angle = null;
        switch (operation)
        {
            case "perpendicular":
            {
                var (source, dx, dy, length) = RequireLine(line, "perpendicular needs a line input.");
                if (point is not { } p) throw new InvalidOperationException("geometry.lineConstruct perpendicular needs a point input.");
                ToleranceGeometry.Finite(p.X, p.Y);
                var half = length * span / 2;
                // 过点的法向线段：方向取源线左法向
                result = new VisionLine2D(new VisionPoint2D(p.X + dy * half, p.Y - dx * half),
                    new VisionPoint2D(p.X - dy * half, p.Y + dx * half));
                angle = NormalizeDeg(Math.Atan2(result.End.Y - result.Start.Y, result.End.X - result.Start.X) * 180 / Math.PI);
                break;
            }
            case "parallel":
            {
                var (source, dx, dy, length) = RequireLine(line, "parallel needs a line input.");
                double signed;
                if (point is { } p)
                {
                    ToleranceGeometry.Finite(p.X, p.Y);
                    signed = (p.X - source.Start.X) * -dy + (p.Y - source.Start.Y) * dx;
                }
                else signed = offsetParam;
                // 左法向 (-dy, dx) 平移；signed 为点到源线的有符号距离，因此 point 与 offset 走同一条路径
                result = new VisionLine2D(
                    new VisionPoint2D(source.Start.X - dy * signed, source.Start.Y + dx * signed),
                    new VisionPoint2D(source.End.X - dy * signed, source.End.Y + dx * signed));
                reportedOffset = signed;
                angle = NormalizeDeg(Math.Atan2(result.End.Y - result.Start.Y, result.End.X - result.Start.X) * 180 / Math.PI);
                break;
            }
            case "midline":
            {
                var (a, ax, ay, al) = RequireLine(line, "midline needs line and line2 inputs.");
                var (b, bx, by, bl) = RequireLine(line2, "midline needs line and line2 inputs.");
                var dirX = ax + bx; var dirY = ay + by;
                var norm = MeasurementMath.Hypot(dirX, dirY);
                if (norm < 1e-9) throw new InvalidOperationException("geometry.lineConstruct midline is undefined for opposing directions (180 deg).");
                dirX /= norm; dirY /= norm;
                var mid = new VisionPoint2D((a.Start.X + a.End.X + b.Start.X + b.End.X) / 4, (a.Start.Y + a.End.Y + b.Start.Y + b.End.Y) / 4);
                var half = (al + bl) / 4 * span;
                result = new VisionLine2D(new VisionPoint2D(mid.X - dirX * half, mid.Y - dirY * half),
                    new VisionPoint2D(mid.X + dirX * half, mid.Y + dirY * half));
                angle = NormalizeDeg(Math.Atan2(dirY, dirX) * 180 / Math.PI);
                break;
            }
            case "bisector":
            {
                var (a, ax, ay, al) = RequireLine(line, "bisector needs line and line2 inputs.");
                var (b, bx, by, bl) = RequireLine(line2, "bisector needs line and line2 inputs.");
                var cross = ax * by - ay * bx;
                if (Math.Abs(cross) < 1e-9) throw new InvalidOperationException("geometry.lineConstruct bisector needs non-parallel lines.");
                var t = ((b.Start.X - a.Start.X) * by - (b.Start.Y - a.Start.Y) * bx) / cross;
                var apex = new VisionPoint2D(a.Start.X + t * ax, a.Start.Y + t * ay);
                var dirX = ax + bx; var dirY = ay + by; var norm = MeasurementMath.Hypot(dirX, dirY);
                if (norm < 1e-9) { dirX = -ay; dirY = ax; norm = 1; }
                dirX /= norm; dirY /= norm;
                var half = Math.Min(al, bl) / 2 * span;
                result = new VisionLine2D(apex, new VisionPoint2D(apex.X + dirX * 2 * half, apex.Y + dirY * 2 * half));
                angle = NormalizeDeg(Math.Atan2(dirY, dirX) * 180 / Math.PI);
                break;
            }
            case "tangent":
            {
                if (circle is not { } c) throw new InvalidOperationException("geometry.lineConstruct tangent needs a circle input.");
                if (point is not { } p) throw new InvalidOperationException("geometry.lineConstruct tangent needs a point input.");
                ToleranceGeometry.Finite(c.X, c.Y, c.Radius, p.X, p.Y);
                if (c.Radius <= 0) throw new InvalidOperationException("geometry.lineConstruct tangent requires a positive circle radius.");
                var distance = MeasurementMath.Hypot(p.X - c.X, p.Y - c.Y);
                if (distance <= c.Radius + 1e-9) throw new InvalidOperationException("geometry.lineConstruct tangent needs a point outside the circle.");
                var baseAngle = Math.Atan2(p.Y - c.Y, p.X - c.X);
                var spread = Math.Acos(c.Radius / distance);
                var t1 = new VisionPoint2D(c.X + c.Radius * Math.Cos(baseAngle + spread), c.Y + c.Radius * Math.Sin(baseAngle + spread));
                var t2 = new VisionPoint2D(c.X + c.Radius * Math.Cos(baseAngle - spread), c.Y + c.Radius * Math.Sin(baseAngle - spread));
                result = new VisionLine2D(p, t1); second = new VisionLine2D(p, t2);
                angle = NormalizeDeg(Math.Atan2(t1.Y - p.Y, t1.X - p.X) * 180 / Math.PI);
                break;
            }
            default:
                throw new InvalidOperationException($"geometry.lineConstruct operation '{operation}' is not supported (perpendicular | parallel | midline | bisector | tangent).");
        }

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("found", true), ("operation", operation),
            ("angleDeg", angle ?? 0), ("length", result.Length), ("offset", reportedOffset ?? 0));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(true), ["line"] = VisionValue.Line(result),
            ["angleDeg"] = VisionValue.Double(angle ?? 0), ["length"] = VisionValue.Double(result.Length),
            ["offset"] = VisionValue.Double(reportedOffset ?? 0), ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (second is { } other) outputs["line2"] = VisionValue.Line(other);
        var overlays = new List<VisionOverlay> { ToleranceGeometry.Segment("constructed", result) };
        if (second is { } secondLine) overlays.Add(ToleranceGeometry.Segment("constructed-2", secondLine));
        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["found"] = true, ["operation"] = operation, ["angleDeg"] = angle ?? 0, ["length"] = result.Length,
                ["offset"] = reportedOffset, ["unit"] = "px", ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }, overlays));
    }

    private static (VisionLine2D Line, double Dx, double Dy, double Length) RequireLine(VisionLine2D? line, string message)
    {
        if (line is not { } value) throw new InvalidOperationException("geometry.lineConstruct " + message);
        var (_, dx, dy, length) = ToleranceGeometry.Line(value);
        return (value, dx, dy, length);
    }

    private static double NormalizeDeg(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle <= -180) angle += 360;
        return angle;
    }
}

public sealed class AffineTransformNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformAffine";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var scaleX = Read(context, node, "scaleX", 1);
        var scaleY = Read(context, node, "scaleY", 1);
        var rotation = Read(context, node, "rotationDeg", 0);
        var translateX = Read(context, node, "translateX", 0);
        var translateY = Read(context, node, "translateY", 0);
        var shear = Read(context, node, "shear", 0);
        if (Math.Abs(scaleX) < 1e-12 || Math.Abs(scaleY) < 1e-12)
            throw new InvalidOperationException("coordinate.transformAffine rejects a zero scale: a collapsed axis is not an invertible transform.");
        var sourceFrame = CoordinateValidation.RequireFrame(node.GetString("sourceFrame", "Image"), "sourceFrame");
        var targetFrame = CoordinateValidation.RequireFrame(node.GetString("targetFrame", "World"), "targetFrame");
        var sourceUnit = CoordinateValidation.RequireUnit(node.GetString("sourceUnit", "px"), "sourceUnit");
        var targetUnit = CoordinateValidation.RequireUnit(node.GetString("targetUnit", "mm"), "targetUnit");

        var radians = rotation * Math.PI / 180;
        var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var transform = new VisionTransform2D(sourceFrame, targetFrame, targetUnit,
            cos * scaleX, cos * shear * scaleY - sin * scaleY, translateX,
            sin * scaleX, sin * shear * scaleY + cos * scaleY, translateY,
            0, 0, 1, sourceUnit);
        var determinant = CoordinateValidation.ValidateTransform(transform, "coordinate.transformAffine");
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("determinant", determinant), ("scaleX", scaleX),
            ("scaleY", scaleY), ("rotationDeg", rotation), ("shear", shear),
            ("sourceFrame", sourceFrame), ("targetFrame", targetFrame));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["transform"] = VisionValue.Transform(transform), ["determinant"] = VisionValue.Double(determinant),
                ["scaleX"] = VisionValue.Double(scaleX), ["scaleY"] = VisionValue.Double(scaleY),
                ["rotationDeg"] = VisionValue.Double(rotation), ["translateX"] = VisionValue.Double(translateX),
                ["translateY"] = VisionValue.Double(translateY), ["shear"] = VisionValue.Double(shear),
                ["sourceFrame"] = VisionValue.String(sourceFrame), ["targetFrame"] = VisionValue.String(targetFrame),
                ["unit"] = VisionValue.String(targetUnit), ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["determinant"] = determinant, ["scaleX"] = scaleX, ["scaleY"] = scaleY, ["rotationDeg"] = rotation,
                ["translateX"] = translateX, ["translateY"] = translateY, ["shear"] = shear,
                ["sourceFrame"] = sourceFrame, ["targetFrame"] = targetFrame, ["unit"] = targetUnit,
                ["algorithmBackend"] = "managed", ["diagnostics"] = diagnostics
            }));
    }

    private static double Read(NodeExecutionContext context, NodeDefinition node, string name, double fallback)
        => context.TryGetValue(name, out _) ? context.RequireNumber(name) : node.GetDouble(name, fallback);
}
