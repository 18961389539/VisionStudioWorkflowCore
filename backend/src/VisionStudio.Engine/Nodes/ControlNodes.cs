using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class IfConditionNode : IVisionNodeExecutor
{
    public string Type => "flow.if";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        var value = context.RequireNumber("value");
        var compare = node.GetDouble("threshold", 0);
        var op = node.GetString("operator", ">");

        var condition = op switch
        {
            ">" => value > compare,
            ">=" => value >= compare,
            "<" => value < compare,
            "<=" => value <= compare,
            "==" => Math.Abs(value - compare) < 1e-9,
            "!=" => Math.Abs(value - compare) >= 1e-9,
            _ => throw new InvalidOperationException($"Unsupported If operator '{op}'.")
        };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["condition"] = VisionValue.Boolean(condition) },
            new Dictionary<string, object?>
            {
                ["value"] = Math.Round(value, 3),
                ["operator"] = op,
                ["compare"] = compare,
                ["condition"] = condition
            }));
    }
}

public sealed class ParallelMarkerNode : IVisionNodeExecutor
{
    public string Type => "flow.parallel";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(),
            new Dictionary<string, object?> { ["branches"] = 2 }));
}

public sealed class JoinNode : IVisionNodeExecutor
{
    public string Type => "flow.join";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(),
            new Dictionary<string, object?> { ["joined"] = true }));
}


public sealed class ResultDispositionNode : IVisionNodeExecutor
{
    public string Type => "flow.result";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context,
        NodeDefinition node,
        CancellationToken cancellationToken)
    {
        var pass = context.Require<bool>("pass");
        var disposition = pass ? "OK" : "NG";
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pass"] = VisionValue.Boolean(pass),
                ["disposition"] = VisionValue.String(disposition)
            },
            new Dictionary<string, object?>
            {
                ["pass"] = pass,
                ["disposition"] = disposition
            }));
    }
}
