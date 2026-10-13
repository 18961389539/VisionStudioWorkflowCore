using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>Cancellation-aware workflow delay. Bounds are also enforced at the executor boundary.</summary>
public sealed class DelayNode : IVisionNodeExecutor
{
    public string Type => "flow.delay";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var delayMs = node.GetInt("delayMs", 1000);
        if (delayMs is < 0 or > 600000) throw new InvalidOperationException("flow.delay delayMs must be between 0 and 600000.");
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        return new NodeExecutorResult(new Dictionary<string, VisionValue> { ["elapsedMs"] = VisionValue.Double(delayMs) },
            new Dictionary<string, object?> { ["delayMs"] = delayMs });
    }
}

/// <summary>
/// Boolean condition evaluator; the structured compiler schedules its selected branch.
/// </summary>
public sealed class BooleanIfConditionNode : IVisionNodeExecutor
{
    public string Type => "flow.ifBoolean";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var condition = context.Require<bool>("condition");
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["condition"] = VisionValue.Boolean(condition) },
            new Dictionary<string, object?> { ["condition"] = condition }));
    }
}

/// <summary>
/// Multiway condition evaluator; the structured compiler schedules exactly one selected path.
/// </summary>
public sealed class SwitchNode : IVisionNodeExecutor
{
    public string Type => "flow.switch";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var value = context.RequireValue("value");
        var casesJson = node.GetString("casesJson", "[]");
        string[] cases;
        try { cases = System.Text.Json.JsonSerializer.Deserialize<string[]>(casesJson) ?? []; }
        catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException("flow.switch casesJson must be a JSON string array.", ex); }
        if (cases.Length is < 1 or > 16 || cases.Any(string.IsNullOrWhiteSpace) || cases.Distinct(StringComparer.Ordinal).Count() != cases.Length)
            throw new InvalidOperationException("flow.switch requires 1-16 non-empty unique case labels.");
        var key = Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var index = Array.FindIndex(cases, x => string.Equals(x, key, StringComparison.Ordinal));
        var selected = index < 0 ? "default" : $"case{index + 1}";
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["selected"] = VisionValue.String(selected) },
            new Dictionary<string, object?> { ["selected"] = selected, ["caseCount"] = cases.Length }));
    }
}

/// <summary>Control-flow convergence marker. Scheduling is emitted by StructuredWorkflowIrCompiler.</summary>
public sealed class FlowMergeNode : IVisionNodeExecutor
{
    public string Type => "flow.merge";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(),
            new Dictionary<string, object?> { ["merged"] = true }));
}

/// <summary>Marker executor; VisionNodeRuntime runs the validated, deterministic try body and records its outcome.</summary>
public sealed class FlowTryNode : IVisionNodeExecutor
{
    public string Type => "flow.try";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["succeeded"] = VisionValue.Boolean(false), ["errorMessage"] = VisionValue.String(string.Empty), ["attempts"] = VisionValue.Integer(0)
        }, new Dictionary<string, object?> { ["phase"] = "try" }));
}
