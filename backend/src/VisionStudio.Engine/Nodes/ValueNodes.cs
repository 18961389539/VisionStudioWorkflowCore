using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 批次一（2026-10-13 节点补齐）· 通用数值/布尔判定族。
/// 用途：把多个测量节点的结果（距离/面积/分数/坐标）组合成完整的 OK/NG 判定——
/// 常量入参、四则运算、范围比较、布尔逻辑，配合 flow.if / flow.result 形成判定链。
/// 设计约定：除零等非法运算**显式报错**（不产生 NaN/Infinity 静默污染下游判定）。
/// </summary>
public sealed class ValueNumberNode : IVisionNodeExecutor
{
    public string Type => "value.number";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var value = node.GetDouble("value", 0);
        if (!double.IsFinite(value))
            throw new InvalidOperationException("value.number requires a finite value.");
        const double longUpperExclusive = 9_223_372_036_854_775_808d;
        if (value >= longUpperExclusive || value < -longUpperExclusive)
            throw new InvalidOperationException("value.number is outside the supported 64-bit integer range.");
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["value"] = VisionValue.Double(value),
                ["integer"] = VisionValue.Integer((long)Math.Round(value, MidpointRounding.AwayFromZero))
            },
            new Dictionary<string, object?> { ["value"] = value }));
    }
}

public sealed class ValueArithmeticNode : IVisionNodeExecutor
{
    public string Type => "value.arithmetic";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var a = context.RequireNumber("a");
        var b = context.RequireNumber("b");
        if (!double.IsFinite(a) || !double.IsFinite(b))
            throw new InvalidOperationException("value.arithmetic requires finite numeric inputs.");
        var operation = node.GetString("op", "add").Trim().ToLowerInvariant();

        var result = operation switch
        {
            "subtract" => a - b,
            "multiply" => a * b,
            "divide" => b == 0
                ? throw new InvalidOperationException("value.arithmetic divide by zero: input 'b' is 0.")
                : a / b,
            _ => a + b
        };
        if (!double.IsFinite(result))
            throw new InvalidOperationException("value.arithmetic result is not finite.");
        operation = operation switch { "subtract" or "multiply" or "divide" => operation, _ => "add" };

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["result"] = VisionValue.Double(result) },
            new Dictionary<string, object?> { ["op"] = operation, ["a"] = a, ["b"] = b, ["result"] = result }));
    }
}

public sealed class ValueRangeNode : IVisionNodeExecutor
{
    public string Type => "value.range";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var value = context.RequireNumber("value");
        var min = node.GetDouble("min", 0);
        var max = node.GetDouble("max", 100);
        var invert = node.GetBool("invert", false);
        if (!double.IsFinite(value) || !double.IsFinite(min) || !double.IsFinite(max))
            throw new InvalidOperationException("value.range requires a finite value and finite bounds.");

        // 闭区间 [min, max]；min > max 时交换（容错现场配置顺序），invert 输出取反。
        var lower = Math.Min(min, max);
        var upper = Math.Max(min, max);
        var inside = value >= lower && value <= upper;
        var pass = invert ? !inside : inside;

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(pass) },
            new Dictionary<string, object?>
            {
                ["value"] = value,
                ["min"] = lower,
                ["max"] = upper,
                ["invert"] = invert,
                ["pass"] = pass
            }));
    }
}

public sealed class LogicBooleanNode : IVisionNodeExecutor
{
    public string Type => "logic.boolean";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var a = context.Require<bool>("a");
        var operation = node.GetString("op", "and").Trim().ToLowerInvariant();

        bool result;
        var usedB = true;
        switch (operation)
        {
            case "or":
                result = a || context.Require<bool>("b");
                break;
            case "xor":
                result = a ^ context.Require<bool>("b");
                break;
            case "not":
                usedB = false;
                result = !a;
                break;
            default:
                operation = "and";
                result = a && context.Require<bool>("b");
                break;
        }

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue> { ["result"] = VisionValue.Boolean(result) },
            new Dictionary<string, object?> { ["op"] = operation, ["a"] = a, ["result"] = result, ["usesB"] = usedB }));
    }
}
