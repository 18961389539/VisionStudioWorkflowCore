using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>
/// 发布门禁：把节点成熟度从"工具箱里的一个徽标"变成"发布时的一次显式确认"（2026-11-12）。
///
/// 问题：成熟度此前只做 UI 披露。工程师在画布上加了 <c>inference.obb</c>（无端到端验证）或
/// <c>measure.thread</c>（中径是估计值），点击"发布"没有任何阻力；发布之后这条信息就消失了——
/// 配方管理里只看到"已验证 / 已发布"，看不到"内含未验证节点"。数据验证记录衡量的是数据集上的
/// 准确率，不能替代"这个节点的算法路径从未被断言"。
///
/// 因此发布含 Preview / Experimental 节点的版本时必须显式确认，并把确认结果写进审计目标字段
/// （发布历史里可回查"谁在什么时候确认了哪些未成熟节点"）。回滚不设门禁：目标版本当初发布时
/// 已经确认过，回滚是恢复到既有结论，不是引入新风险。
/// </summary>
public static class NodeMaturityGate
{
    /// <summary>一个非 Stable 节点实例。理由取自 <see cref="NodeMaturityCatalog"/>，写给现场看。</summary>
    public sealed record Finding(string NodeId, string NodeType, string NodeName, string Maturity, string Reason);

    /// <summary>门禁拒绝响应体：前端据此展示"哪些节点、为什么、要不要继续"。</summary>
    public sealed record AcknowledgementRequired(string Code, string Message, IReadOnlyList<Finding> Findings)
    {
        public const string ResponseCode = "NODE_MATURITY_ACK_REQUIRED";
    }

    /// <summary>
    /// 扫描工作流里所有非 Stable 节点，按 Experimental 优先排序（风险高的排前面）。
    ///
    /// 已知边界：<c>module.call</c> 引用的模块内部节点不在本工作流的 Nodes 列表里，本门禁不递归展开。
    /// 模块发布走的是同一条 <c>jobs publish</c> 路径，因此模块自身发布时已被门禁覆盖；
    /// 这里只保证"当前这份工作流里写着的节点"没有静默带入未成熟能力。
    /// </summary>
    public static IReadOnlyList<Finding> Scan(WorkflowDefinition? workflow)
    {
        if (workflow?.Nodes is not { Count: > 0 }) return [];
        var findings = new List<Finding>();
        foreach (var node in workflow.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Type)) continue;
            if (!NodeMaturityCatalog.Declarations.TryGetValue(node.Type, out var declaration)) continue;
            if (declaration.Maturity == NodeMaturity.Stable) continue;
            findings.Add(new Finding(
                node.Id,
                node.Type,
                string.IsNullOrWhiteSpace(node.Name) ? node.Type : node.Name!,
                declaration.Maturity.ToString(),
                declaration.Reason));
        }
        return findings
            .OrderByDescending(f => f.Maturity == nameof(NodeMaturity.Experimental))
            .ThenBy(f => f.NodeType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.NodeId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>审计目标字段的摘要（写入 audit_events.target，发布历史可检索）。</summary>
    public static string Describe(IReadOnlyList<Finding> findings)
    {
        var experimental = findings.Count(f => f.Maturity == nameof(NodeMaturity.Experimental));
        var preview = findings.Count - experimental;
        var types = string.Join(",", findings.Select(f => f.NodeType).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        return $"maturity:experimental={experimental},preview={preview};types={types}";
    }
}
