using VisionStudio.Abstractions;
namespace VisionStudio.Engine.Nodes;
public sealed class DelayNode : IVisionNodeExecutor
{
    public string Type => "flow.delay";
    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var delayMs = node.GetInt("delayMs", 1000);
        if (delayMs is < 0 or > 600000) throw new InvalidOperationException("flow.delay delayMs must be between 0 and 600000.");
        cancellationToken.ThrowIfCancellationRequested();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // Windows timer ticks can complete Task.Delay before the requested duration
        // according to the high-resolution clock. Recheck the actual deadline.
        var remaining = (double)delayMs;
        while (remaining > 0)
        {
            await Task.Delay((int)Math.Ceiling(remaining), cancellationToken).ConfigureAwait(false);
            remaining = delayMs - System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        CoordinateValidation.RequireFinite(elapsed, "delay.elapsedMs");
        var diag = CoordinateValidation.Diagnostics(Type, ("requestedMs", delayMs), ("elapsedMs", elapsed));
        // elapsedMs 是实测耗时（含调度抖动），不是请求值——两者在 diagnostics 里分开记录。
        return new NodeExecutorResult(new Dictionary<string, VisionValue> { ["elapsedMs"] = VisionValue.Double(elapsed), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["delayMs"] = delayMs, ["elapsedMs"] = Math.Round(elapsed, 3), ["diagnostics"] = diag });
    }
}
public sealed class BooleanIfConditionNode : IVisionNodeExecutor
{
    public string Type => "flow.ifBoolean";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var condition = context.Require<bool>("condition"); var branch = condition ? "true" : "false";
        var diag = CoordinateValidation.Diagnostics(Type, ("condition", condition), ("branch", branch));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["condition"] = VisionValue.Boolean(condition), ["branch"] = VisionValue.String(branch), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["condition"] = condition, ["diagnostics"] = diag }));
    }
}
public sealed class SwitchNode : IVisionNodeExecutor
{
    public string Type => "flow.switch";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = context.RequireValue("value"); var casesJson = node.GetString("casesJson", "[]");
        string[] cases;
        try { cases = System.Text.Json.JsonSerializer.Deserialize<string[]>(casesJson) ?? []; } catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException("flow.switch casesJson must be a JSON string array.", ex); }
        if (cases.Length is < 1 or > 16 || cases.Any(string.IsNullOrWhiteSpace) || cases.Distinct(StringComparer.Ordinal).Count() != cases.Length) throw new InvalidOperationException("flow.switch requires 1-16 non-empty unique case labels.");
        var key = Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (key.Length > 256) throw new InvalidOperationException("flow.switch input value text is too long (max 256 chars).");
        var index = Array.FindIndex(cases, x => string.Equals(x, key, StringComparison.Ordinal)); var matched = index >= 0; var selected = matched ? $"case{index + 1}" : "default";
        var diag = CoordinateValidation.Diagnostics(Type, ("value", key), ("selected", selected), ("matched", matched), ("caseCount", cases.Length), ("caseIndex", matched ? index : -1));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["selected"] = VisionValue.String(selected), ["matched"] = VisionValue.Boolean(matched), ["caseCount"] = VisionValue.Integer(cases.Length), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["selected"] = selected, ["caseCount"] = cases.Length, ["diagnostics"] = diag }));
    }
}
public sealed class FlowMergeNode : IVisionNodeExecutor
{
    public string Type => "flow.merge";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("merged", true), ("marker", "merge"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["merged"] = true, ["diagnostics"] = diag }));
    }
}
public sealed class FlowTryNode : IVisionNodeExecutor
{
    public string Type => "flow.try";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diag = CoordinateValidation.Diagnostics(Type, ("phase", "try"), ("markerExecutor", true), ("note", "VisionNodeRuntime executes the try body and overwrites succeeded/errorMessage/attempts"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["succeeded"] = VisionValue.Boolean(false), ["errorMessage"] = VisionValue.String(string.Empty), ["attempts"] = VisionValue.Integer(0), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["phase"] = "try", ["diagnostics"] = diag }));
    }
}
