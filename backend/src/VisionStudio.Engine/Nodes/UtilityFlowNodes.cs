using VisionStudio.Abstractions;
namespace VisionStudio.Engine.Nodes;
public sealed class DelayNode : IVisionNodeExecutor
{
    public string Type => "flow.delay";
    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var milliseconds = ResultFileWriter.Integer(node, "delayMs", 1000, 0, 600000); var maximum = ResultFileWriter.Integer(node, "maxDelayMs", 600000, 1, 600000);
        if (maximum is < 1 or > 600000 || milliseconds < 0 || milliseconds > maximum)
            throw new InvalidOperationException("Delay must be within 0..maxDelayMs, and maxDelayMs within 1..600000.");
        var precision = ResultFileWriter.Choice(node, "precision", "standard", "standard", "hires");
        var startedUtc = DateTimeOffset.UtcNow;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = milliseconds - System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0) break;
            if (precision == "hires" && remaining <= 1) { Thread.SpinWait(64); continue; }
            await Task.Delay(Math.Max(1, (int)Math.Floor(remaining - (precision == "hires" ? 1 : 0))), cancellationToken).ConfigureAwait(false);
        }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var finishedUtc = DateTimeOffset.UtcNow;
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("requestedMs", milliseconds), ("elapsedMs", elapsed), ("precision", precision));
        return new(new Dictionary<string, VisionValue> { ["requestedMs"] = VisionValue.Integer(milliseconds),
            ["elapsedMs"] = VisionValue.Double(elapsed), ["startedUtc"] = VisionValue.String(startedUtc.ToString("O")),
            ["finishedUtc"] = VisionValue.String(finishedUtc.ToString("O")), ["cancelled"] = VisionValue.Boolean(false),
            ["diagnostics"] = VisionValue.String(diagnostics) }, new Dictionary<string, object?> { ["requestedMs"] = milliseconds,
            ["elapsedMs"] = Math.Round(elapsed, 3), ["precision"] = precision, ["diagnostics"] = diagnostics });    }
}
public sealed class BooleanIfConditionNode : IVisionNodeExecutor
{
    public string Type => "flow.ifBoolean";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException("flow.ifBoolean requires a node definition.");
        var condition = context.Require<bool>("condition");
        var trueLabel = node.GetString("trueLabel", "true").Trim();
        var falseLabel = node.GetString("falseLabel", "false").Trim();
        if (node.GetBool("strictLabels", true) && (string.IsNullOrWhiteSpace(trueLabel) || string.IsNullOrWhiteSpace(falseLabel)))
            throw new InvalidOperationException("flow.ifBoolean trueLabel and falseLabel must be non-empty.");
        if (trueLabel.Length > 32 || falseLabel.Length > 32)
            throw new InvalidOperationException("flow.ifBoolean labels must be at most 32 characters.");
        var branch = condition ? trueLabel : falseLabel;
        var diag = CoordinateValidation.Diagnostics(Type, ("condition", condition), ("branch", branch), ("trueLabel", trueLabel), ("falseLabel", falseLabel));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["condition"] = VisionValue.Boolean(condition), ["branch"] = VisionValue.String(branch), ["label"] = VisionValue.String(branch), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["condition"] = condition, ["branch"] = branch, ["diagnostics"] = diag }));
    }
}
public sealed class SwitchNode : IVisionNodeExecutor
{
    internal const string DefaultCasesJson = "[\"OK\",\"NG\"]";
    public string Type => "flow.switch";

    internal static string[] ReadCases(NodeDefinition node)
    {
        var casesJson = node.GetString("casesJson", DefaultCasesJson);
        string[] cases;
        try { cases = System.Text.Json.JsonSerializer.Deserialize<string[]>(casesJson) ?? []; }
        catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException($"Switch '{node.Id}' casesJson must be a JSON string array.", ex); }
        if (cases.Length is < 1 or > 16 || cases.Any(string.IsNullOrWhiteSpace) || cases.Distinct(StringComparer.Ordinal).Count() != cases.Length)
            throw new InvalidOperationException($"Switch '{node.Id}' requires 1-16 non-empty unique case labels.");
        return cases;
    }

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = context.RequireValue("value"); var cases = ReadCases(node);
        var key = Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (key.Length > 256) throw new InvalidOperationException("flow.switch input value text is too long (max 256 chars).");
        var index = Array.FindIndex(cases, x => string.Equals(x, key, StringComparison.Ordinal)); var matched = index >= 0; var selected = matched ? $"case{index + 1}" : "default";
        var diag = CoordinateValidation.Diagnostics(Type, ("value", key), ("selected", selected), ("matched", matched), ("caseCount", cases.Length), ("caseIndex", matched ? index : -1));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["selected"] = VisionValue.String(selected), ["selectedCase"] = VisionValue.String(selected), ["matched"] = VisionValue.Boolean(matched), ["caseCount"] = VisionValue.Integer(cases.Length), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["selected"] = selected, ["caseCount"] = cases.Length, ["diagnostics"] = diag }));
    }
}
public sealed class FlowMergeNode : IVisionNodeExecutor
{
    public string Type => "flow.merge";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException("flow.merge requires a node definition.");
        if (string.IsNullOrWhiteSpace(node.Id)) throw new InvalidOperationException("flow.merge requires a node id.");
        if (node.Id.Length > 128) throw new InvalidOperationException("flow.merge node id must be at most 128 characters.");
        var id = node.Id.Trim();
        var policy = node.GetString("policy", "any").Trim().ToLowerInvariant();
        if (policy is not ("any" or "all")) throw new InvalidOperationException("flow.merge policy must be any or all.");
        var diag = CoordinateValidation.Diagnostics(Type, ("merged", true), ("marker", "merge"), ("nodeId", id), ("boundary", "merge"), ("semantics", "selected-branch"), ("legacyPolicyIgnored", policy == "all"));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["merged"] = VisionValue.Boolean(true),
                ["nodeId"] = VisionValue.String(id),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["merged"] = true, ["nodeId"] = id, ["boundary"] = "merge", ["diagnostics"] = diag }));
    }
}
public sealed class FlowTryNode : IVisionNodeExecutor
{
    public string Type => "flow.try";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = CoordinateValidation.Diagnostics(Type, ("phase", "pending-body"));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> {
            ["succeeded"] = VisionValue.Boolean(false), ["errorMessage"] = VisionValue.String(""),
            ["attempts"] = VisionValue.Integer(0), ["elapsedMs"] = VisionValue.Double(0),
            ["retried"] = VisionValue.Boolean(false), ["diagnostics"] = VisionValue.String(diagnostics)
        }, new Dictionary<string, object?> { ["phase"] = "try" }));
    }
}
