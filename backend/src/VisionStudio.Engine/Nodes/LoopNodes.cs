using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 通用条件循环（flow.while，2026-10-26 · 缺口分析 P0-3）。
///
/// 语义是 **do-while**：先执行一轮循环体，再判断条件；条件为真继续下一轮，为假退出。
/// 这样定义有两个原因：
/// 1. 条件天然来自循环体内部（例如"本轮测量是否合格"），而经典 while 在首轮执行前就需要一个值，
///    在图结构里只能来自循环体之外的常量，实际不可用；
/// 2. 它正好匹配产线"先拍一次、不 OK 就重拍"的重试模式。
///
/// 本节点自身只做**结构声明与初值校验**：真正的条件求值由编译器生成的循环末尾采样步完成
/// （见 VisionLoopStep），Workflow Core 的 While 原语每轮重新读取 data.LoopConditions。
/// 因此 condition 输入端口标记为可选：它的来源通常是循环体内的节点，首轮执行前并不存在输出。
/// </summary>
public sealed class WhileLoopNode : IVisionNodeExecutor
{
    public string Type => "flow.while";

    /// <summary>迭代预算上限，防止条件恒真把运行时挂死。</summary>
    public const int MaxIterationsCeiling = 10000;

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var maxIterations = node.GetInt("maxIterations", 100);
        if (maxIterations is < 1 or > MaxIterationsCeiling)
            throw new InvalidOperationException($"flow.while maxIterations must be within 1..{MaxIterationsCeiling}.");

        // 条件端口接在循环体之外时（常量条件），这里能读到值；接在循环体内时首轮还没有输出，
        // 属于正常情况——本轮条件由循环末尾的采样步读取。
        var hasCondition = context.TryGetValue("condition", out var conditionValue);
        var initialCondition = hasCondition && conditionValue.Type == VisionDataType.Boolean && conditionValue.Value is true;

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("kind", "do-while"),
            ("maxIterations", maxIterations),
            ("conditionConnected", hasCondition),
            ("initialCondition", initialCondition));

        var outputs = new Dictionary<string, VisionValue>
        {
            ["condition"] = VisionValue.Boolean(hasCondition ? initialCondition : true),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        return ValueTask.FromResult(new NodeExecutorResult(
            outputs,
            new Dictionary<string, object?>
            {
                ["kind"] = "do-while",
                ["maxIterations"] = maxIterations,
                ["conditionConnected"] = hasCondition,
                ["diagnostics"] = diagnostics
            }));
    }
}

/// <summary>
/// 固定次数循环（flow.repeat）。执行循环体恰好 count 次，循环体内可读 index（0 起）/ iteration（1 起）/ total / isLast。
/// count 优先取输入端口，未接线时用参数；真实迭代由编译器生成的 While 原语 + 计数步完成，
/// 本节点负责把次数解析成一个确定的上限（total），并拒绝非正数。
/// </summary>
public sealed class RepeatLoopNode : IVisionNodeExecutor
{
    public string Type => "flow.repeat";

    public const int MaxCountCeiling = 100000;

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var parameterCount = node.GetInt("count", 3);
        var connected = context.TryGetValue("count", out _);
        var total = connected ? (int)Math.Round(context.RequireNumber("count")) : parameterCount;
        if (total is < 1 || total > MaxCountCeiling)
            throw new InvalidOperationException($"flow.repeat count must be within 1..{MaxCountCeiling}; got {total}.");

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("count", total),
            ("countSource", connected ? "input" : "parameter"));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["total"] = VisionValue.Integer(total),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["count"] = total,
                ["countSource"] = connected ? "input" : "parameter",
                ["diagnostics"] = diagnostics
            }));
    }
}

/// <summary>
/// 提前退出当前循环（flow.break）。
///
/// 图结构上的执行由编译器完成：break 节点之后的本轮剩余步骤会被包进一个
/// <c>If(LoopBreaks[id] == false)</c> 守卫，同时把该循环的中断标志置位；
/// 下一轮条件求值读到中断标志后直接退出循环并走 done 路由。
/// 本节点只记录意图与原因，便于追溯。
/// </summary>
public sealed class BreakLoopNode : IVisionNodeExecutor
{
    public string Type => "flow.break";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var reason = node.GetString("reason", string.Empty);
        if (reason.Length > 512)
            throw new InvalidOperationException("flow.break reason must be at most 512 characters.");

        var diagnostics = CoordinateValidation.Diagnostics(Type, ("reason", reason), ("breakRequested", true));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["breakRequested"] = VisionValue.Boolean(true),
                ["reason"] = VisionValue.String(reason),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?> { ["reason"] = reason, ["breakRequested"] = true, ["diagnostics"] = diagnostics }));
    }
}
