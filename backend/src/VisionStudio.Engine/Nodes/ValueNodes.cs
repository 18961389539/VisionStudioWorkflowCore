using VisionStudio.Abstractions;
namespace VisionStudio.Engine.Nodes;
public sealed class ValueNumberNode : IVisionNodeExecutor
{
    public string Type => "value.number";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = CoordinateValidation.RequireFinite(node.GetDouble("value", 0), "value");
        const double longUpperExclusive = 9_223_372_036_854_775_808d;
        if (value >= longUpperExclusive || value < -longUpperExclusive) throw new InvalidOperationException("value.number is outside the supported 64-bit integer range.");
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(rounded)) throw new InvalidOperationException("value.number rounding produced a non-finite value.");
        var integer = (long)rounded; var isInteger = Math.Abs(value - rounded) < 1e-12;
        var diag = CoordinateValidation.Diagnostics(Type, ("value", value), ("integer", integer), ("isInteger", isInteger));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(value), ["integer"] = VisionValue.Integer(integer), ["isInteger"] = VisionValue.Boolean(isInteger), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["value"] = value, ["diagnostics"] = diag }));
    }
}
public sealed class ValueArithmeticNode : IVisionNodeExecutor
{
    public string Type => "value.arithmetic";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = CoordinateValidation.RequireFinite(context.RequireNumber("a"), "a");
        var b = CoordinateValidation.RequireFinite(context.RequireNumber("b"), "b");
        var operation = node.GetString("op", "add").Trim().ToLowerInvariant();
        if (operation is not ("add" or "subtract" or "multiply" or "divide")) throw new InvalidOperationException($"value.arithmetic op '{operation}' is unsupported; use add, subtract, multiply or divide.");
        if (operation == "divide" && b == 0) throw new InvalidOperationException("value.arithmetic divide by zero: input 'b' is 0.");
        var result = operation switch { "subtract" => a - b, "multiply" => a * b, "divide" => a / b, _ => a + b };
        if (!double.IsFinite(result)) throw new InvalidOperationException("value.arithmetic result is not finite.");
        var diag = CoordinateValidation.Diagnostics(Type, ("a", a), ("b", b), ("op", operation), ("result", result));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["result"] = VisionValue.Double(result), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["op"] = operation, ["a"] = a, ["b"] = b, ["result"] = result, ["diagnostics"] = diag }));
    }
}
public sealed class ValueRangeNode : IVisionNodeExecutor
{
    public string Type => "value.range";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = CoordinateValidation.RequireFinite(context.RequireNumber("value"), "value");
        var min = CoordinateValidation.RequireFinite(node.GetDouble("min", 0), "min");
        var max = CoordinateValidation.RequireFinite(node.GetDouble("max", 100), "max");
        var invert = node.GetBool("invert", false);
        var swapped = min > max; var lower = Math.Min(min, max); var upper = Math.Max(min, max);
        var inside = value >= lower && value <= upper; var pass = invert ? !inside : inside;
        var margin = inside ? Math.Min(value - lower, upper - value) : -Math.Min(Math.Abs(value - lower), Math.Abs(value - upper));
        CoordinateValidation.RequireFinite(margin, "range.margin");
        var diag = CoordinateValidation.Diagnostics(Type, ("value", value), ("lower", lower), ("upper", upper), ("inside", inside), ("pass", pass), ("invert", invert), ("boundsSwapped", swapped), ("margin", margin));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(pass), ["inside"] = VisionValue.Boolean(inside), ["margin"] = VisionValue.Double(margin), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["value"] = value, ["min"] = lower, ["max"] = upper, ["invert"] = invert, ["pass"] = pass, ["diagnostics"] = diag }));
    }
}
public sealed class LogicBooleanNode : IVisionNodeExecutor
{
    public string Type => "logic.boolean";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var a = context.Require<bool>("a");
        var operation = node.GetString("op", "and").Trim().ToLowerInvariant();
        if (operation is not ("and" or "or" or "xor" or "not")) throw new InvalidOperationException($"logic.boolean op '{operation}' is unsupported; use and, or, xor or not.");
        var usedB = operation != "not"; var b = usedB ? context.Require<bool>("b") : false;
        var result = operation switch { "or" => a || b, "xor" => a ^ b, "not" => !a, _ => a && b };
        var diag = CoordinateValidation.Diagnostics(Type, ("a", a), ("b", usedB ? b : null), ("op", operation), ("result", result), ("usesB", usedB));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["result"] = VisionValue.Boolean(result), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["op"] = operation, ["a"] = a, ["result"] = result, ["usesB"] = usedB, ["diagnostics"] = diag }));
    }
}
