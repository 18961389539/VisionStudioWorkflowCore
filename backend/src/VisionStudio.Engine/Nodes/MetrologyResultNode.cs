namespace VisionStudio.Engine.Nodes;

public sealed class MetrologyResultNode : IVisionNodeExecutor
{
    public string Type => "measure.metrologyResult";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = context.Require<IReadOnlyList<VisionMetrologyResult>>("results");
        var name = node.GetString("name", "").Trim();
        var index = node.GetDouble("index", 0);
        if (name.Length > 128)
            throw new InvalidOperationException("Metrology result object name must be at most 128 characters.");
        if (name.Length == 0 && (!double.IsFinite(index) || index < 0 || index > 511 || index != Math.Truncate(index)))
            throw new InvalidOperationException("Metrology result index must be an integer in 0..511.");
        if (results.Count > 512)
            throw new InvalidOperationException("Metrology result list must contain at most 512 objects.");
        VisionMetrologyResult? selected = null;
        var matchingNames = 0;
        var indices = new HashSet<int>();
        foreach (var item in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null)
                throw new InvalidOperationException("Metrology result list contains a null object.");
            if (item.Index is < 0 or > 511)
                throw new InvalidOperationException($"Metrology result object index {item.Index} must be in 0..511.");
            if (!indices.Add(item.Index))
                throw new InvalidOperationException($"Metrology result index {item.Index} is not unique; each authored object must have its own index.");
            if (name.Length > 0 ? item.Name == name : item.Index == (int)index)
            {
                selected = item;
                matchingNames++;
            }
        }
        if (name.Length > 0 && matchingNames > 1)
            throw new InvalidOperationException($"Metrology result name '{name}' is not unique; rename the authored objects or select by index.");
        var found = selected?.Found == true;
        if (found) ValidateGeometry(selected!);
        cancellationToken.ThrowIfCancellationRequested();
        var selectedIndex = selected?.Index ?? (name.Length > 0 ? -1 : (int)index);
        var diag = CoordinateValidation.Diagnostics(Type,
            ("selector", name.Length > 0 ? "name" : "index"), ("requestedName", name),
            ("requestedIndex", name.Length > 0 ? null : (object)(int)index),
            ("found", found), ("count", results.Count), ("selectedIndex", selectedIndex), ("kind", selected?.Kind));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(found), ["count"] = VisionValue.Integer(results.Count),
            ["name"] = VisionValue.String(selected?.Name ?? name), ["kind"] = VisionValue.String(selected?.Kind ?? ""),
            ["diagnostics"] = VisionValue.String(diag)
        };
        if (found)
        {
            if (selected!.Center is { } center) outputs["center"] = VisionValue.Point(center);
            if (selected.Circle is { } circle) { outputs["circle"] = VisionValue.Circle(circle); outputs["radius"] = VisionValue.Double(circle.Radius); }
            if (selected.Line is { } line) outputs["line"] = VisionValue.Line(line);
            if (selected.Rectangle is { } rectangle) outputs["rectangle"] = VisionValue.Rectangle(rectangle);
            if (selected.Pose is { } pose) outputs["pose"] = VisionValue.Pose(pose);
            if (selected.ResidualPx is { } residual) outputs["residual"] = VisionValue.Double(residual);
        }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
        {
            ["found"] = found, ["name"] = selected?.Name ?? name, ["index"] = selectedIndex,
            ["kind"] = selected?.Kind, ["selector"] = name.Length > 0 ? "name" : "index", ["diagnostics"] = diag
        }));
    }

    private static void ValidateGeometry(VisionMetrologyResult result)
    {
        var prefix = $"Metrology result '{result.Name}' (index {result.Index})";
        if (result.Center is { } center) CoordinateValidation.RequireFinitePoint(center, prefix + ".center");
        if (result.Circle is { } circle)
        {
            CoordinateValidation.RequireFinitePoint(new VisionPoint2D(circle.X, circle.Y), prefix + ".circle.center");
            if (!double.IsFinite(circle.Radius) || circle.Radius <= 0)
                throw new InvalidOperationException($"{prefix} circle radius must be finite and positive.");
        }
        if (result.Line is { } line) CoordinateValidation.RequireLine(line, prefix + ".line");
        if (result.Rectangle is { } rectangle)
        {
            CoordinateValidation.RequireFinitePoint(new VisionPoint2D(rectangle.X, rectangle.Y), prefix + ".rectangle.center");
            CoordinateValidation.RequireFinite(rectangle.AngleDeg, prefix + ".rectangle.angleDeg");
            if (!double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height) || rectangle.Width <= 0 || rectangle.Height <= 0)
                throw new InvalidOperationException($"{prefix} rectangle dimensions must be finite and positive.");
        }
        if (result.Pose is { } pose)
        {
            CoordinateValidation.RequireFinitePoint(new VisionPoint2D(pose.X, pose.Y), prefix + ".pose.center");
            CoordinateValidation.RequireFinite(pose.ThetaDeg, prefix + ".pose.thetaDeg");
        }
        if (result.ResidualPx is { } residual && (!double.IsFinite(residual) || residual < 0))
            throw new InvalidOperationException($"{prefix} residual must be finite and non-negative.");
    }
}
