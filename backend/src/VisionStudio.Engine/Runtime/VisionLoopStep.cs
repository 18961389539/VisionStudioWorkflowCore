using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 通用循环（flow.while / flow.repeat / flow.break）的迭代步。由编译器生成，不是画布节点。
///
/// 为什么需要独立的 StepBody，而不是让循环节点的执行器自己判断：
/// Workflow Core 的 While 原语在每轮开始前重新求值一个字符串条件表达式，而画布节点的执行器
/// 每轮只运行一次（甚至在循环开始前就运行完了）。把"本轮状态采样"放进循环体末尾的独立步骤，
/// 才能让条件真正按轮刷新。
///
/// Action 语义：
/// - <c>begin</c>：把本轮序号写进循环节点的输出（index / iteration / total / isLast），
///   循环体内的节点通过普通数据边即可读到；必须在循环体最前面执行。
/// - <c>end</c>：累加计数器，并按 Kind 计算下一轮条件——while 采样 SourceNodeId.SourcePort 的
///   布尔输出（同时叠加迭代预算），repeat 用 counter &lt; total。
/// - <c>break</c>：置位中断标志，本轮剩余步骤由编译期生成的 If 守卫跳过，下一轮条件求值失败退出。
/// </summary>
public sealed class VisionLoopStep : StepBodyAsync
{
    public string LoopNodeId { get; set; } = string.Empty;

    /// <summary>begin / end / break。</summary>
    public string Action { get; set; } = "end";

    /// <summary>while 循环的条件来源节点；其它动作留空。</summary>
    public string SourceNodeId { get; set; } = string.Empty;

    /// <summary>while 循环条件来源的输出端口。</summary>
    public string SourcePort { get; set; } = string.Empty;

    /// <summary>while / repeat。决定 end 动作如何计算下一轮条件。</summary>
    public string Kind { get; set; } = "while";

    public override Task<ExecutionResult> RunAsync(IStepExecutionContext context)
    {
        if (context.Workflow.Data is not VisionWorkflowData data)
            throw new InvalidOperationException("Workflow data is not VisionWorkflowData.");
        if (string.IsNullOrWhiteSpace(LoopNodeId))
            throw new InvalidOperationException("VisionLoopStep requires LoopNodeId.");

        switch (Action.Trim().ToLowerInvariant())
        {
            case "begin":
                data.PublishLoopIteration(LoopNodeId, data.GetLoopCounter(LoopNodeId), data.GetLoopTotal(LoopNodeId));
                return Task.FromResult(ExecutionResult.Next());

            case "break":
                data.RequestLoopBreak(LoopNodeId);
                return Task.FromResult(ExecutionResult.Next());

            case "end":
                var iterations = data.IncrementLoopCounter(LoopNodeId);
                var budget = data.GetLoopTotal(LoopNodeId);
                bool condition;
                if (Kind.Equals("repeat", StringComparison.OrdinalIgnoreCase))
                {
                    condition = iterations < budget;
                }
                else
                {
                    condition = SampleCondition(data) && (budget <= 0 || iterations < budget);
                }
                data.SetLoopCondition(LoopNodeId, condition);
                return Task.FromResult(ExecutionResult.Next());

            default:
                throw new InvalidOperationException($"VisionLoopStep action '{Action}' is not supported.");
        }
    }

    /// <summary>
    /// 读取本轮条件。来源节点必须在本轮执行过——若它位于未命中的 If 分支内，
    /// 这里读到的是上一轮留下的值（已在节点文档中说明），不会因为缺失而静默当成 true。
    /// </summary>
    private bool SampleCondition(VisionWorkflowData data)
    {
        if (string.IsNullOrWhiteSpace(SourceNodeId) || string.IsNullOrWhiteSpace(SourcePort))
            throw new InvalidOperationException($"flow.while '{LoopNodeId}' has no condition source wired.");

        if (!data.OutputsByNode.TryGetValue(SourceNodeId, out var outputs) ||
            !outputs.TryGetValue(SourcePort, out var value))
            throw new InvalidOperationException(
                $"flow.while '{LoopNodeId}' cannot read condition '{SourceNodeId}.{SourcePort}' after this iteration. " +
                "The condition source must run inside the loop body (or before the loop); connect a boolean output that exists every iteration.");

        if (value.Type != Abstractions.VisionDataType.Boolean || value.Value is not bool condition)
            throw new InvalidOperationException(
                $"flow.while '{LoopNodeId}' condition '{SourceNodeId}.{SourcePort}' is {value.Type}; a Boolean output is required.");

        return condition;
    }
}
