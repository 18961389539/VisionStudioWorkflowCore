using VisionStudio.Abstractions;
namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 批次一（2026-10-13 节点补齐）· 通用数值/布尔判定族。
/// 用途：把多个测量节点的结果（距离/面积/分数/坐标）组合成完整的 OK/NG 判定——
/// 常量入参、四则运算、范围比较、布尔逻辑，配合 flow.if / flow.result 形成判定链。
/// 设计约定：除零、未知运算符、上下限颠倒等非法配置**显式报错**（不产生 NaN/Infinity，也不静默改成加法或对调区间）。
/// </summary>
public sealed class ValueNumberNode : IVisionNodeExecutor
{
    public string Type => "value.number";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var value = CoordinateValidation.RequireFinite(node.GetDouble("value", 0), "value");
        const double longUpperExclusive = 9_223_372_036_854_775_808d;
        if (value >= longUpperExclusive || value < -longUpperExclusive) throw new InvalidOperationException("value.number is outside the supported 64-bit integer range.");
        var enforceBounds = node.GetBool("enforceBounds", false);
        var min = node.GetDouble("min", 0); var max = node.GetDouble("max", 0);
        if (enforceBounds)
        {
            if (!double.IsFinite(min) || !double.IsFinite(max) || min > max)
                throw new InvalidOperationException("value.number enforceBounds requires finite min <= max.");
            if (value < min || value > max)
                throw new InvalidOperationException($"value.number {value} is outside enforced bounds [{min}, {max}].");
        }
        var roundingText = node.GetString("rounding", "away").Trim().ToLowerInvariant();
        if (roundingText is not ("away" or "even")) throw new InvalidOperationException("value.number rounding must be away or even.");
        var rounding = roundingText == "even" ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero;
        var rounded = Math.Round(value, rounding);
        if (!double.IsFinite(rounded)) throw new InvalidOperationException("value.number rounding produced a non-finite value.");
        var integer = (long)rounded; var isInteger = Math.Abs(value - rounded) < 1e-12;
        var diag = CoordinateValidation.Diagnostics(Type, ("value", value), ("integer", integer), ("isInteger", isInteger), ("enforceBounds", enforceBounds), ("min", min), ("max", max), ("rounding", roundingText));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(value), ["integer"] = VisionValue.Integer(integer), ["isInteger"] = VisionValue.Boolean(isInteger), ["isFinite"] = VisionValue.Boolean(double.IsFinite(value)), ["isZero"] = VisionValue.Boolean(value == 0), ["sign"] = VisionValue.Integer(value > 0 ? 1 : value < 0 ? -1 : 0), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["value"] = value, ["diagnostics"] = diag }));
    }
}
public sealed class ValueArithmeticNode : IVisionNodeExecutor
{
    public string Type => "value.arithmetic";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var a = context.RequireNumber("a");
        if (!double.IsFinite(a))
            throw new InvalidOperationException("value.arithmetic requires finite numeric inputs.");
        var operation = node.GetString("op", "add").Trim().ToLowerInvariant();
        var needsB = operation is "add" or "subtract" or "multiply" or "divide" or "pow" or "min" or "max";
        double? b = null;
        if (needsB)
        {
            if (!context.TryGetValue("b", out _))
                throw new InvalidOperationException($"value.arithmetic operation '{operation}' requires input 'b'.");
            b = context.RequireNumber("b");
            if (!double.IsFinite(b.Value))
                throw new InvalidOperationException("value.arithmetic requires finite numeric inputs.");
        }
        else if (operation is not ("sqrt" or "abs" or "clamp"))
            throw new InvalidOperationException($"value.arithmetic operation '{operation}' is not supported.");

        var result = operation switch
        {
            "add" => a + b!.Value,
            "subtract" => a - b!.Value,
            "multiply" => a * b!.Value,
            "divide" => b!.Value == 0
                ? throw new InvalidOperationException("value.arithmetic divide by zero: input 'b' is 0.")
                : a / b.Value,
            "pow" => Math.Pow(a, b!.Value),
            "sqrt" => a < 0
                ? throw new InvalidOperationException("value.arithmetic sqrt requires input 'a' to be greater than or equal to 0.")
                : Math.Sqrt(a),
            "abs" => Math.Abs(a),
            "min" => Math.Min(a, b!.Value),
            "max" => Math.Max(a, b!.Value),
            "clamp" => Clamp(node, a),
            _ => throw new InvalidOperationException($"value.arithmetic operation '{operation}' is not supported.")
        };
        var onNonFinite = CoordinateValidation.RequireNonEmpty(node.GetString("onNonFinite", "fail"), "onNonFinite").ToLowerInvariant();
        if (onNonFinite is not ("fail" or "zero"))
            throw new InvalidOperationException("value.arithmetic onNonFinite must be fail or zero.");
        var isFinite = double.IsFinite(result);
        if (!isFinite)
        {
            if (onNonFinite == "fail")
                throw new InvalidOperationException("value.arithmetic result is not finite.");
            result = 0;
        }
        var diag = CoordinateValidation.Diagnostics(Type, ("a", a), ("b", b), ("op", operation), ("result", result), ("isFinite", isFinite), ("onNonFinite", onNonFinite));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> {
                ["result"] = VisionValue.Double(result), ["isFinite"] = VisionValue.Boolean(isFinite),
                ["a"] = VisionValue.Double(a), ["b"] = VisionValue.Double(b ?? 0), ["op"] = VisionValue.String(operation),
                ["diagnostics"] = VisionValue.String(diag) },
            new Dictionary<string, object?> { ["op"] = operation, ["a"] = a, ["b"] = b, ["result"] = result, ["isFinite"] = isFinite, ["diagnostics"] = diag }));
    }

    private static double Clamp(NodeDefinition node, double value)
    {
        var minimum = node.GetDouble("clampMin", 0);
        var maximum = node.GetDouble("clampMax", 1);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            throw new InvalidOperationException("value.arithmetic clamp bounds must be finite.");
        if (minimum > maximum)
            throw new InvalidOperationException($"value.arithmetic clamp minimum {minimum} is greater than maximum {maximum}.");
        return Math.Clamp(value, minimum, maximum);
    }
}
public sealed class ValueRangeNode : IVisionNodeExecutor
{
    public string Type => "value.range";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var value = CoordinateValidation.RequireFinite(context.RequireNumber("value"), "value");
        var min = CoordinateValidation.RequireFinite(node.GetDouble("min", 0), "min");
        var max = CoordinateValidation.RequireFinite(node.GetDouble("max", 100), "max");
        var invert = node.GetBool("invert", false);
        var boundPolicy = node.GetString("boundPolicy", "inclusive").Trim().ToLowerInvariant();
        if (boundPolicy is not ("inclusive" or "exclusive")) throw new InvalidOperationException("value.range boundPolicy must be inclusive or exclusive.");
        var swapped = min > max; var lower = Math.Min(min, max); var upper = Math.Max(min, max);
        var inside = boundPolicy == "exclusive"
            ? value > lower && value < upper
            : value >= lower && value <= upper;
        var pass = invert ? !inside : inside;
        var margin = inside ? Math.Min(value - lower, upper - value) : -Math.Min(Math.Abs(value - lower), Math.Abs(value - upper));
        CoordinateValidation.RequireFinite(margin, "range.margin");
        var deviation = value > upper ? value - upper : value < lower ? value - lower : 0d;
        var clamped = Math.Clamp(value, lower, upper);
        var diag = CoordinateValidation.Diagnostics(Type, ("value", value), ("lower", lower), ("upper", upper), ("inside", inside), ("pass", pass), ("invert", invert), ("boundPolicy", boundPolicy), ("boundsSwapped", swapped), ("margin", margin), ("deviation", deviation), ("clamped", clamped));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(pass), ["inside"] = VisionValue.Boolean(inside), ["margin"] = VisionValue.Double(margin), ["deviation"] = VisionValue.Double(deviation), ["clamped"] = VisionValue.Double(clamped), ["diagnostics"] = VisionValue.String(diag) }, new Dictionary<string, object?> { ["value"] = value, ["min"] = lower, ["max"] = upper, ["invert"] = invert, ["boundPolicy"] = boundPolicy, ["pass"] = pass, ["deviation"] = deviation, ["clamped"] = clamped, ["diagnostics"] = diag }));
    }
}
public sealed class LogicBooleanNode : IVisionNodeExecutor
{
    public string Type => "logic.boolean";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var a = context.Require<bool>("a");
        var operation = node.GetString("op", "and").Trim().ToLowerInvariant();
        var b = operation is "and" or "or" or "xor" ? context.Require<bool>("b") : false;

        bool result;
        var usedB = true;
        switch (operation)
        {
            case "or":
                result = a || b;
                break;
            case "xor":
                result = a ^ b;
                break;
            case "and":
                result = a && b;
                break;
            case "not":
                usedB = false;
                result = !a;
                break;
            default:
                throw new InvalidOperationException($"logic.boolean operation '{operation}' is not supported.");
        }

        var invert = node.GetBool("invertResult", false);
        if (invert) result = !result;
        var diag = CoordinateValidation.Diagnostics(Type, ("a", a), ("op", operation), ("result", result), ("usesB", usedB), ("invertResult", invert));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["result"] = VisionValue.Boolean(result),
                ["operationApplied"] = VisionValue.String(operation),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["op"] = operation, ["a"] = a, ["result"] = result, ["usesB"] = usedB, ["invertResult"] = invert, ["diagnostics"] = diag }));
    }
}
