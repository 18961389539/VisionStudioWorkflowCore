using VisionStudio.Engine.Runtime;
namespace VisionStudio.Engine.Nodes;
public sealed class IfConditionNode : IVisionNodeExecutor
{
    public string Type => "flow.if";
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
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

if (node is null) throw new InvalidOperationException("flow.parallel requires a node definition.");
        if (string.IsNullOrWhiteSpace(node.Id)) throw new InvalidOperationException("flow.parallel requires a non-empty node id.");
        if (node.Id.Length > 128) throw new InvalidOperationException("flow.parallel node id must be at most 128 characters.");
        const int branches = 2;
        var id = node.Id.Trim();
        var diag = CoordinateValidation.Diagnostics(Type, ("branches", branches), ("marker", "parallel"), ("boundary", "parallel"), ("nodeId", id));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["branches"] = VisionValue.Integer(branches),
                ["nodeId"] = VisionValue.String(id),
                ["marker"] = VisionValue.String("parallel"),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["branches"] = branches, ["nodeId"] = id, ["boundary"] = "parallel", ["diagnostics"] = diag }));
    }
}
public sealed class JoinNode : IVisionNodeExecutor
{
    public string Type => "flow.join";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(node.Id)) throw new InvalidOperationException("flow.join requires a non-empty node id.");
        if (node.Id.Length > 128) throw new InvalidOperationException("flow.join node id must be at most 128 characters.");
        var id = node.Id.Trim();
        var diag = CoordinateValidation.Diagnostics(Type, ("joined", true), ("marker", "join"), ("boundary", "join"), ("nodeId", id));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["joined"] = VisionValue.Boolean(true),
                ["nodeId"] = VisionValue.String(id),
                ["marker"] = VisionValue.String("join"),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["joined"] = true, ["nodeId"] = id, ["boundary"] = "join", ["diagnostics"] = diag }));
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
        var disposition = pass ? okLabel : ngLabel;
        var diag = CoordinateValidation.Diagnostics(Type, ("pass", pass), ("disposition", disposition), ("okLabel", okLabel), ("ngLabel", ngLabel));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pass"] = VisionValue.Boolean(pass),
                ["disposition"] = VisionValue.String(disposition),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["pass"] = pass, ["disposition"] = disposition, ["okLabel"] = okLabel, ["ngLabel"] = ngLabel, ["diagnostics"] = diag }));
    }
}
