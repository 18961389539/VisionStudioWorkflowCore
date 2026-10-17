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
        if (!double.IsFinite(value))
            throw new InvalidOperationException("flow.if requires a finite value.");
        var compare = node.GetDouble("threshold", 0);
        var op = node.GetString("operator", ">");
        var mode = node.GetString("mode", "threshold").Trim().ToLowerInvariant();
        var tolerance = node.GetDouble("tolerance", 1e-9);
        var hysteresis = node.GetDouble("hysteresis", 0);
        if (!double.IsFinite(compare) || !double.IsFinite(tolerance) || !double.IsFinite(hysteresis) || tolerance < 0 || hysteresis < 0)
            throw new InvalidOperationException("flow.if tolerance and hysteresis must be finite and greater than or equal to 0.");

        bool raw;
        double? rangeMin = null;
        double? rangeMax = null;
        if (mode is "threshold" or "")
        {
            raw = op switch
            {
                ">" => value > compare,
                ">=" => value >= compare,
                "<" => value < compare,
                "<=" => value <= compare,
                "==" => NearlyEqual(value, compare, tolerance),
                "!=" => !NearlyEqual(value, compare, tolerance),
                _ => throw new InvalidOperationException($"Unsupported If operator '{op}'.")
            };
        }
        else if (mode == "range")
        {
            rangeMin = node.GetDouble("rangeMin", 0);
            rangeMax = node.GetDouble("rangeMax", 100);
            if (!double.IsFinite(rangeMin.Value) || !double.IsFinite(rangeMax.Value))
                throw new InvalidOperationException("flow.if range bounds must be finite.");
            if (rangeMin.Value > rangeMax.Value)
                throw new InvalidOperationException($"flow.if rangeMin {rangeMin.Value} is greater than rangeMax {rangeMax.Value}.");
            raw = value >= rangeMin.Value && value <= rangeMax.Value;
        }
        else throw new InvalidOperationException($"flow.if mode '{mode}' is not supported.");

        var condition = raw;
        var hysteresisApplied = false;
        if (hysteresis > 0 && context.TryGetValue("previous", out var previousValue))
        {
            var previous = previousValue.Require<bool>("previous");
            hysteresisApplied = true;
            condition = mode == "range"
                ? ApplyRangeHysteresis(value, rangeMin!.Value, rangeMax!.Value, hysteresis, previous)
                : ApplyThresholdHysteresis(value, compare, op, tolerance, hysteresis, previous);
        }

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["condition"] = VisionValue.Boolean(condition) },
            new Dictionary<string, object?>
            {
                ["value"] = Math.Round(value, 3),
                ["operator"] = op,
                ["compare"] = compare,
                ["mode"] = mode.Length == 0 ? "threshold" : mode,
                ["tolerance"] = tolerance,
                ["condition"] = condition,
                ["hysteresisApplied"] = hysteresisApplied
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
