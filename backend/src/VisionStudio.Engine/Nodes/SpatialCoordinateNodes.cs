using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class CoordinatePoint3DNode : IVisionNodeExecutor
{
    public string Type => "coordinate.point3D";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var x = ReadNumber("x", 0);
        var y = ReadNumber("y", 0);
        var z = ReadNumber("z", 0);
        var frame = CoordinateValidation.RequireFrame(node.GetString("frame", "Workpiece"), "frame");
        if (string.IsNullOrWhiteSpace(frame))
            throw new InvalidOperationException("coordinate.point3D frame must be a non-empty name.");
        var unit = CoordinateValidation.RequireUnit(node.GetString("unit", "mm"), "unit", "mm", "cm", "m");
        var point = new VisionCoordinatePoint3D(x, y, z, frame, unit);
        var xyMagnitude = double.Hypot(x, y);
        var magnitude = double.Hypot(xyMagnitude, z);
        if (!double.IsFinite(magnitude) || !double.IsFinite(xyMagnitude))
            throw new InvalidOperationException("coordinate.point3D magnitude must be finite.");
        var azimuthDeg = Math.Atan2(y, x) * 180 / Math.PI;
        var elevationDeg = Math.Atan2(z, xyMagnitude) * 180 / Math.PI;
        var isOrigin = Math.Abs(x) < 1e-15 && Math.Abs(y) < 1e-15 && Math.Abs(z) < 1e-15;
        var diagnostics = CoordinateValidation.Diagnostics(Type,
            ("x", x), ("y", y), ("z", z), ("magnitude", magnitude), ("xyMagnitude", xyMagnitude),
            ("azimuthDeg", azimuthDeg), ("elevationDeg", elevationDeg),
            ("isOrigin", isOrigin), ("frame", frame), ("unit", unit));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["point"] = VisionValue.CoordinatePoint3D(point), ["x"] = VisionValue.Double(x),
                ["y"] = VisionValue.Double(y), ["z"] = VisionValue.Double(z),
                ["magnitude"] = VisionValue.Double(magnitude),
                ["xyMagnitude"] = VisionValue.Double(xyMagnitude),
                ["azimuthDeg"] = VisionValue.Double(azimuthDeg),
                ["elevationDeg"] = VisionValue.Double(elevationDeg),
                ["frame"] = VisionValue.String(frame), ["unit"] = VisionValue.String(unit),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["point"] = point, ["magnitude"] = magnitude, ["xyMagnitude"] = xyMagnitude,
                ["azimuthDeg"] = azimuthDeg, ["elevationDeg"] = elevationDeg, ["isOrigin"] = isOrigin,
                ["frame"] = frame, ["unit"] = unit, ["diagnostics"] = diagnostics
            }));

        double ReadNumber(string name, double fallback)
        {
            var value = context.TryGetValue(name, out _) ? context.RequireNumber(name) : node.GetDouble(name, fallback);
            if (!double.IsFinite(value) || Math.Abs(value) > 1e9)
                throw new InvalidOperationException($"{name} must be finite and within +/-1e9.");
            return value;
        }
    }
}

public sealed class TransformCoordinatePoint3DNode : IVisionNodeExecutor
{
    public string Type => "coordinate.transformPoint3D";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var point = context.Require<VisionCoordinatePoint3D>("point");
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            throw new InvalidOperationException("point coordinates must be finite.");
        var sourceFrame = CoordinateValidation.RequireFrame(point.Frame, "point.frame");
        var sourceUnit = CoordinateValidation.RequireUnit(point.Unit, "point.unit", "mm", "cm", "m");
        var transform = context.Require<VisionTransform3D>("transform");
        ValidateTransform(transform);
        if (!sourceFrame.Equals(transform.SourceFrame, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Frame mismatch: point is in '{sourceFrame}', transform expects '{transform.SourceFrame}'.");
        if (!sourceUnit.Equals(transform.Unit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unit mismatch: point uses '{sourceUnit}', transform expects '{transform.Unit}'. Convert the point before applying this transform.");

        var m = transform.Matrix;
        var x = m[0] * point.X + m[1] * point.Y + m[2] * point.Z + m[3];
        var y = m[4] * point.X + m[5] * point.Y + m[6] * point.Z + m[7];
        var z = m[8] * point.X + m[9] * point.Y + m[10] * point.Z + m[11];
        if (new[] { x, y, z }.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1e12))
            throw new InvalidOperationException("coordinate.transformPoint3D produced non-finite or out-of-range coordinates.");
        var result = new VisionCoordinatePoint3D(x, y, z, transform.TargetFrame, transform.Unit);
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("sourceFrame", sourceFrame), ("targetFrame", transform.TargetFrame),
            ("sourceUnit", sourceUnit), ("unit", transform.Unit), ("x", x), ("y", y), ("z", z));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["point"] = VisionValue.CoordinatePoint3D(result), ["x"] = VisionValue.Double(x),
                ["y"] = VisionValue.Double(y), ["z"] = VisionValue.Double(z),
                ["frame"] = VisionValue.String(transform.TargetFrame), ["unit"] = VisionValue.String(transform.Unit),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?> { ["point"] = result, ["diagnostics"] = diagnostics }));
    }

    private static void ValidateTransform(VisionTransform3D transform)
    {
        CoordinateValidation.RequireFrame(transform.SourceFrame, "transform.sourceFrame");
        CoordinateValidation.RequireFrame(transform.TargetFrame, "transform.targetFrame");
        CoordinateValidation.RequireUnit(transform.Unit, "transform.unit", "mm", "cm", "m");
        if (transform.Matrix is not { Length: 16 } || transform.Matrix.Any(x => !double.IsFinite(x) || Math.Abs(x) > 1e9))
            throw new InvalidOperationException("transform.matrix must be a finite row-major 4x4 matrix.");
        var matrix = transform.Matrix;
        if (Math.Abs(matrix[12]) > 1e-6 || Math.Abs(matrix[13]) > 1e-6 || Math.Abs(matrix[14]) > 1e-6 || Math.Abs(matrix[15] - 1) > 1e-6)
            throw new InvalidOperationException("transform.matrix must have homogeneous last row [0,0,0,1].");
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++)
        {
            var dot = 0d;
            for (var k = 0; k < 3; k++) dot += matrix[k * 4 + r] * matrix[k * 4 + c];
            if (Math.Abs(dot - (r == c ? 1 : 0)) > 1e-3)
                throw new InvalidOperationException("transform.matrix rotation must be orthonormal.");
        }
        var determinant = matrix[0] * (matrix[5] * matrix[10] - matrix[6] * matrix[9])
            - matrix[1] * (matrix[4] * matrix[10] - matrix[6] * matrix[8])
            + matrix[2] * (matrix[4] * matrix[9] - matrix[5] * matrix[8]);
        if (Math.Abs(determinant - 1) > 1e-3)
            throw new InvalidOperationException($"transform.matrix rotation determinant must be +1; measured {determinant:0.####}.");
    }
}
