using VisionStudio.Engine.Runtime;
namespace VisionStudio.Engine.Nodes;
public sealed class IfConditionNode : IVisionNodeExecutor
{
    public string Type => "flow.if";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = CoordinateValidation.RequireFinite(context.RequireNumber("value"), "value");
        var compare = CoordinateValidation.RequireFinite(node.GetDouble("threshold", 0), "threshold");
        var op = node.GetString("operator", ">").Trim();
        if (op is not (">" or ">=" or "<" or "<=" or "==" or "!=")) throw new InvalidOperationException($"Unsupported If operator '{op}'; use >, >=, <, <=, == or !=.");
        var condition = op switch { ">" => value > compare, ">=" => value >= compare, "<" => value < compare, "<=" => value <= compare, "==" => Math.Abs(value - compare) < 1e-9, _ => Math.Abs(value - compare) >= 1e-9 };
        var margin = CoordinateValidation.RequireFinite(value - compare, "if.margin");
        var diag = CoordinateValidation.Diagnostics(Type, ("value", value), ("threshold", compare), ("operator", op), ("condition", condition), ("margin", margin));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["condition"] = VisionValue.Boolean(condition), ["value"] = VisionValue.Double(value), ["margin"] = VisionValue.Double(margin), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["value"] = Math.Round(value, 3), ["operator"] = op, ["compare"] = compare, ["condition"] = condition, ["diagnostics"] = diag }));
    }
}
public sealed class ParallelMarkerNode : IVisionNodeExecutor
{
    public string Type => "flow.parallel";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("branches", 2), ("marker", "parallel"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["branches"] = 2, ["diagnostics"] = diag }));
    }
}
public sealed class JoinNode : IVisionNodeExecutor
{
    public string Type => "flow.join";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("joined", true), ("marker", "join"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["joined"] = true, ["diagnostics"] = diag }));
    }
}
public sealed class ResultDispositionNode : IVisionNodeExecutor
{
    public string Type => "flow.result";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pass = context.Require<bool>("pass"); var disposition = pass ? "OK" : "NG";
        var diag = CoordinateValidation.Diagnostics(Type, ("pass", pass), ("disposition", disposition));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(pass), ["disposition"] = VisionValue.String(disposition), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["pass"] = pass, ["disposition"] = disposition, ["diagnostics"] = diag }));
    }
}
