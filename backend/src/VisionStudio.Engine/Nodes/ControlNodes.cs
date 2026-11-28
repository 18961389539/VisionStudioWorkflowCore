using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;
public sealed class IfConditionNode : IVisionNodeExecutor, IVisionNodeDefinitionValidator
{
    public string Type => "flow.if";

    void IVisionNodeDefinitionValidator.ValidateDefinition(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputs)
    {
        if (!node.GetString("mode", "threshold").Equals("range", StringComparison.OrdinalIgnoreCase)) return;
        var minimum = node.GetDouble("rangeMin", 0);
        var maximum = node.GetDouble("rangeMax", 100);
        if (minimum > maximum)
            throw new InvalidOperationException($"Node '{node.Id}' rangeMin {minimum} is greater than rangeMax {maximum}.");
    }

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var value = context.RequireNumber("value");
        if (!double.IsFinite(value))
            throw new InvalidOperationException("flow.if requires a finite value.");
        var mode = node.GetString("mode", "threshold").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "threshold";
        if (mode is not ("threshold" or "range"))
            throw new InvalidOperationException($"flow.if mode '{mode}' is not supported.");
        var hysteresis = node.GetDouble("hysteresis", 0);
        if (!double.IsFinite(hysteresis) || hysteresis < 0)
            throw new InvalidOperationException("flow.if hysteresis must be finite and greater than or equal to 0.");

        bool raw;
        double? compare = null;
        string? op = null;
        double? tolerance = null;
        double? rangeMin = null;
        double? rangeMax = null;
        if (mode == "threshold")
        {
            compare = node.GetDouble("threshold", 15000);
            if (!double.IsFinite(compare.Value))
                throw new InvalidOperationException("flow.if threshold must be finite.");
            op = node.GetString("operator", ">").Trim();
            if (op is "==" or "!=")
            {
                tolerance = node.GetDouble("tolerance", 1e-9);
                if (!double.IsFinite(tolerance.Value) || tolerance < 0)
                    throw new InvalidOperationException("flow.if tolerance must be finite and greater than or equal to 0.");
            }
            raw = op switch
            {
                ">" => value > compare.Value,
                ">=" => value >= compare.Value,
                "<" => value < compare.Value,
                "<=" => value <= compare.Value,
                "==" => NearlyEqual(value, compare.Value, tolerance!.Value),
                "!=" => !NearlyEqual(value, compare.Value, tolerance!.Value),
                _ => throw new InvalidOperationException($"Unsupported If operator '{op}'.")
            };
        }
        else
        {
            rangeMin = node.GetDouble("rangeMin", 0);
            rangeMax = node.GetDouble("rangeMax", 100);
            if (!double.IsFinite(rangeMin.Value) || !double.IsFinite(rangeMax.Value))
                throw new InvalidOperationException("flow.if range bounds must be finite.");
            if (rangeMin.Value > rangeMax.Value)
                throw new InvalidOperationException($"flow.if rangeMin {rangeMin.Value} is greater than rangeMax {rangeMax.Value}.");
            raw = value >= rangeMin.Value && value <= rangeMax.Value;
        }

        var condition = raw;
        var hysteresisApplied = false;
        if (hysteresis > 0 && context.TryGetValue("previous", out var previousValue))
        {
            var previous = previousValue.Require<bool>("previous");
            hysteresisApplied = true;
            condition = mode == "range"
                ? ApplyRangeHysteresis(value, rangeMin!.Value, rangeMax!.Value, hysteresis, previous)
                : ApplyThresholdHysteresis(value, compare!.Value, op!, tolerance ?? 0, hysteresis, previous);
        }

        var margin = mode == "range" ? Math.Min(value - rangeMin!.Value, rangeMax!.Value - value) : value - compare!.Value;
        if (!double.IsFinite(margin)) throw new InvalidOperationException("flow.if margin exceeds the finite numeric range.");
        var diag = CoordinateValidation.Diagnostics(Type,
            ("value", value), ("threshold", compare), ("operator", op), ("mode", mode),
            ("rangeMin", rangeMin), ("rangeMax", rangeMax), ("tolerance", tolerance),
            ("rawCondition", raw), ("condition", condition), ("margin", margin), ("hysteresisApplied", hysteresisApplied));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["condition"] = VisionValue.Boolean(condition), ["value"] = VisionValue.Double(value), ["margin"] = VisionValue.Double(margin), ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?>
            {
                ["value"] = Math.Round(value, 3),
                ["operator"] = op,
                ["compare"] = compare,
                ["mode"] = mode,
                ["rangeMin"] = rangeMin,
                ["rangeMax"] = rangeMax,
                ["tolerance"] = tolerance,
                ["rawCondition"] = raw,
                ["condition"] = condition,
                ["hysteresisApplied"] = hysteresisApplied,
                ["diagnostics"] = diag
            }));
    }

    private static bool ApplyThresholdHysteresis(double value, double compare, string op, double tolerance, double hysteresis, bool previous)
        => op switch
        {
            ">" => previous ? value > compare - hysteresis : value > compare + hysteresis,
            ">=" => previous ? value >= compare - hysteresis : value >= compare + hysteresis,
            "<" => previous ? value < compare + hysteresis : value < compare - hysteresis,
            "<=" => previous ? value <= compare + hysteresis : value <= compare - hysteresis,
            "==" => NearlyEqual(value, compare, previous ? tolerance + hysteresis : Math.Max(0, tolerance - hysteresis)),
            "!=" => !NearlyEqual(value, compare, previous ? Math.Max(0, tolerance - hysteresis) : tolerance + hysteresis),
            _ => throw new InvalidOperationException($"Unsupported If operator '{op}'.")
        };

    private static bool NearlyEqual(double value, double compare, double tolerance)
        => tolerance <= 0 ? value == compare : Math.Abs(value - compare) < tolerance;

    private static bool ApplyRangeHysteresis(double value, double rangeMin, double rangeMax, double hysteresis, bool previous)
        => previous
            ? value >= rangeMin - hysteresis && value <= rangeMax + hysteresis
            : value >= rangeMin + hysteresis && value <= rangeMax - hysteresis;
}
public sealed class ParallelMarkerNode : IVisionNodeExecutor
{
    public string Type => "flow.parallel";

    public const int BranchCount = 2;
    public const int MaxLabelLength = 32;
    public const int MaxTimeoutMs = 600_000;

    /// <summary>
    /// 解析分支标签与分支超时。标签用于把"哪条分支超时/被取消"写进报告，
    /// 因此必须成对给出——只有一个标签时两条分支无法区分。
    /// </summary>
    public static (IReadOnlyList<string> Labels, int TimeoutMs) ReadConfiguration(NodeDefinition node)
    {
        var raw = node.GetString("branchLabels", string.Empty).Trim();
        string[] labels = [];
        if (raw.Length > 0)
        {
            labels = raw.Split(',', StringSplitOptions.TrimEntries);
            if (labels.Length != BranchCount)
                throw new InvalidOperationException($"flow.parallel branchLabels must list exactly {BranchCount} comma-separated labels; got {labels.Length}.");
            foreach (var label in labels)
            {
                if (label.Length is 0 or > MaxLabelLength)
                    throw new InvalidOperationException($"flow.parallel branch labels must be 1..{MaxLabelLength} characters; got '{label}'.");
                if (label.Contains('|') || label.Contains(';'))
                    throw new InvalidOperationException($"flow.parallel branch label '{label}' must not contain '|' or ';'.");
            }
        }

        var timeoutMs = node.GetInt("branchTimeoutMs", 0);
        if (timeoutMs is < 0 or > MaxTimeoutMs)
            throw new InvalidOperationException($"flow.parallel branchTimeoutMs must be within 0..{MaxTimeoutMs} (0 disables the branch timeout).");
        return (labels, timeoutMs);
    }

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(node.Id)) throw new InvalidOperationException("flow.parallel requires a non-empty node id.");
        if (node.Id.Length > 128) throw new InvalidOperationException("flow.parallel node id must be at most 128 characters.");

        var (labels, timeoutMs) = ReadConfiguration(node);
        var id = node.Id.Trim();
        var label1 = labels.Count > 0 ? labels[0] : "branch1";
        var label2 = labels.Count > 1 ? labels[1] : "branch2";
        var diag = CoordinateValidation.Diagnostics(Type,
            ("branches", BranchCount), ("marker", "parallel"), ("boundary", "parallel"), ("nodeId", id),
            ("branchLabel1", label1), ("branchLabel2", label2), ("branchTimeoutMs", timeoutMs));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["branches"] = VisionValue.Integer(BranchCount),
                ["nodeId"] = VisionValue.String(id),
                ["marker"] = VisionValue.String("parallel"),
                ["branchLabel1"] = VisionValue.String(label1),
                ["branchLabel2"] = VisionValue.String(label2),
                ["branchTimeoutMs"] = VisionValue.Integer(timeoutMs),
                ["elapsedMs"] = VisionValue.Double(0),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["branches"] = BranchCount, ["nodeId"] = id, ["boundary"] = "parallel",
                ["branchLabel1"] = label1, ["branchLabel2"] = label2, ["branchTimeoutMs"] = timeoutMs,
                ["diagnostics"] = diag
            }));
    }
}

public sealed class JoinNode : IVisionNodeExecutor
{
    public string Type => "flow.join";

    /// <summary>
    /// Join 只做形状校验；分支状态集合与合并结果由运行时补齐（它持有 Parallel 区域与各分支执行记录）。
    /// 这里先给出可预测的空值，避免下游读到"端口不存在"而不是"没有分支"。
    /// </summary>
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(node.Id)) throw new InvalidOperationException("flow.join requires a non-empty node id.");
        if (node.Id.Length > 128) throw new InvalidOperationException("flow.join node id must be at most 128 characters.");
        var id = node.Id.Trim();
        var policy = ResultFileWriter.Choice(node, "mergePolicy", ParallelMergePolicies.AnyNg, ParallelMergePolicies.All);
        var quorum = node.GetInt("quorum", 0);
        if (quorum is < 0 or > 64)
            throw new InvalidOperationException("flow.join quorum must be within 0..64 (0 = all branches).");
        var diag = CoordinateValidation.Diagnostics(Type, ("joined", true), ("marker", "join"), ("boundary", "join"),
            ("nodeId", id), ("mergePolicy", policy), ("quorum", quorum));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["joined"] = VisionValue.Boolean(true),
                ["nodeId"] = VisionValue.String(id),
                ["marker"] = VisionValue.String("join"),
                ["branchCount"] = VisionValue.Integer(0),
                ["completedBranches"] = VisionValue.StringList([]),
                ["failedBranches"] = VisionValue.StringList([]),
                ["cancelledBranches"] = VisionValue.StringList([]),
                ["missingBranches"] = VisionValue.StringList([]),
                ["completedCount"] = VisionValue.Integer(0),
                ["failedCount"] = VisionValue.Integer(0),
                ["cancelledCount"] = VisionValue.Integer(0),
                ["missingCount"] = VisionValue.Integer(0),
                ["mergePolicy"] = VisionValue.String(policy),
                ["policySatisfied"] = VisionValue.Boolean(false),
                ["duplicateToken"] = VisionValue.Boolean(false),
                ["branchDispositionsJson"] = VisionValue.String("{}"),
                ["branchStatesJson"] = VisionValue.String("[]"),
                ["parallelElapsedMs"] = VisionValue.Double(0),
                ["warningCount"] = VisionValue.Integer(0),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["joined"] = true, ["nodeId"] = id, ["boundary"] = "join",
                ["mergePolicy"] = policy, ["quorum"] = quorum, ["warningCount"] = 0, ["diagnostics"] = diag
            }));
    }
}
public sealed class ResultDispositionNode : IVisionNodeExecutor
{
    public string Type => "flow.result";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var pass = context.Require<bool>("pass");
        var okLabel = node.GetString("okLabel", "OK").Trim();
        var ngLabel = node.GetString("ngLabel", "NG").Trim();
        if (string.IsNullOrWhiteSpace(okLabel) || string.IsNullOrWhiteSpace(ngLabel))
            throw new InvalidOperationException("flow.result okLabel and ngLabel must be non-empty.");
        if (okLabel.Length > 32 || ngLabel.Length > 32)
            throw new InvalidOperationException("flow.result labels must be at most 32 characters.");
        var reset = node.GetBool("reset", false);
        var sticky = node.GetBool("sticky", false);
        var disposition = pass ? okLabel : ngLabel;
        var diag = CoordinateValidation.Diagnostics(Type, ("pass", pass), ("disposition", disposition),
            ("okLabel", okLabel), ("ngLabel", ngLabel), ("sticky", sticky), ("reset", reset), ("nodeId", node.Id));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pass"] = VisionValue.Boolean(pass),
                ["disposition"] = VisionValue.String(disposition),
                // 运行时据此清空本作用域的旧值，再写入本次结果（显式重置）。
                ["resetScope"] = VisionValue.Boolean(reset),
                ["sticky"] = VisionValue.Boolean(sticky),
                ["warningCount"] = VisionValue.Integer(0),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["pass"] = pass, ["disposition"] = disposition, ["okLabel"] = okLabel, ["ngLabel"] = ngLabel,
                ["sticky"] = sticky, ["reset"] = reset, ["nodeId"] = node.Id, ["warningCount"] = 0,
                ["diagnostics"] = diag
            }));
    }
}
