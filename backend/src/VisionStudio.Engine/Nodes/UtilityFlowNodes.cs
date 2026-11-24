using System.Text.Json;
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
    private static readonly string[] CasePorts = Enumerable.Range(1, 16).Select(index => $"case{index}").ToArray();

    internal sealed record SwitchCase(string Port, string Label);

    public string Type => "flow.switch";

    internal static StringComparison MatchingComparison(NodeDefinition node)
        => node.GetBool("ignoreCase", false) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static SwitchCase[] ReadCases(NodeDefinition node)
    {
        var casesJson = node.GetString("casesJson", DefaultCasesJson);
        var message = $"Switch '{node.Id}' casesJson must contain 1-16 case labels or objects with a port and label.";
        JsonDocument document;
        try { document = JsonDocument.Parse(casesJson); }
        catch (JsonException ex) { throw new InvalidOperationException(message, ex); }
        using var caseDocument = document;
        var entries = caseDocument.RootElement;
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 16)
            throw new InvalidOperationException(message);

        var legacy = entries[0].ValueKind == JsonValueKind.String;
        var trim = node.GetBool("trimValues", false);
        var comparison = MatchingComparison(node);
        var comparer = comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var ports = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(comparer);
        var cases = new SwitchCase[entries.GetArrayLength()];
        for (var index = 0; index < cases.Length; index++)
        {
            var entry = entries[index];
            string port;
            string label;
            if (legacy && entry.ValueKind == JsonValueKind.String)
            {
                port = CasePorts[index];
                label = entry.GetString()!;
            }
            else if (!legacy && entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("port", out var portElement) && portElement.ValueKind == JsonValueKind.String &&
                entry.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String)
            {
                port = portElement.GetString()!;
                label = labelElement.GetString()!;
            }
            else
            {
                throw new InvalidOperationException(message);
            }

            if (!CasePorts.Contains(port, StringComparer.Ordinal) || !ports.Add(port))
                throw new InvalidOperationException($"Switch '{node.Id}' casesJson requires one unique port (case1 through case16) per case label.");
            if (trim) label = label.Trim();
            if (string.IsNullOrWhiteSpace(label) || !labels.Add(label))
                throw new InvalidOperationException(
                    $"Switch '{node.Id}' requires 1-16 non-empty unique case labels" +
                    (comparison == StringComparison.OrdinalIgnoreCase
                        ? "; ignoreCase=true makes labels that differ only by case ambiguous and rejected."
                        : "."));
            cases[index] = new SwitchCase(port, label);
        }
        return cases;
    }

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = context.RequireValue("value"); var cases = ReadCases(node);
        var key = Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        // 扫码/OCR 文本常带尾随空格或 \r；先裁剪再判长度，避免把可用的值当超长拒绝。
        if (node.GetBool("trimValues", false)) key = key.Trim();
        if (key.Length > 256) throw new InvalidOperationException("flow.switch input value text is too long (max 256 chars).");
        var comparison = MatchingComparison(node);
        var index = Array.FindIndex(cases, item => string.Equals(item.Label, key, comparison));
        var matched = index >= 0;
        var selected = matched ? cases[index].Port : "default";
        var diag = CoordinateValidation.Diagnostics(Type, ("value", key), ("selected", selected), ("matched", matched), ("caseCount", cases.Length), ("caseIndex", matched ? index : -1), ("comparison", comparison == StringComparison.OrdinalIgnoreCase ? "ignoreCase" : "ordinal"));
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
