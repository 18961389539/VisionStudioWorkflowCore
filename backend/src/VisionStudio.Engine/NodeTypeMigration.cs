using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>
/// 已合并节点的类型兼容层。
///
/// 目录里不再收录这些旧类型（它们只是通用节点的一个参数取值），但 2026-10 之前保存的流程仍会
/// 引用旧类型。本层在编译/校验入口把旧类型改写成规范类型并补齐参数，使旧文档无需人工改动即可
/// 继续运行；新目录只暴露一个入口，避免同一实现存在两份目录条目和两份参数表。
///
/// 迁移只做「类型 + 参数」改写，不改边：下表的每组旧/新节点端口完全一致（由
/// NodeTypeMigrationTests 的端口等价断言锁定），因此不需要重写 edge 的 sourcePort/targetPort。
/// </summary>
public static class NodeTypeMigration
{
    /// <summary>旧类型 → 规范类型 + 需要补齐的参数（已存在的同名参数不覆盖）。</summary>
    private static readonly Dictionary<string, (string Target, (string Key, string Value)[] Parameters)> Rules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image.blur.gaussian"] = ("image.filter", [("method", "gaussian")]),
            ["image.blur.median"] = ("image.filter", [("method", "median")]),
            ["image.topHat"] = ("image.morphology", [("operation", "whiteTopHat")]),
            ["image.bottomHat"] = ("image.morphology", [("operation", "blackBottomHat")]),
            ["image.morphGradient"] = ("image.morphology", [("operation", "gradient")]),
            ["region.fromThreshold"] = ("region.fromRange", [("mode", "gray")]),
            ["measure.perpendicularity"] = ("measure.parallelism", [("mode", "perpendicular")])
        };

    /// <summary>旧类型 → 规范类型；不是旧类型时返回 null。</summary>
    public static string? CanonicalType(string? type) =>
        type is not null && Rules.TryGetValue(type, out var rule) ? rule.Target : null;

    /// <summary>旧类型 → 它的替代类型（含已合并但端口不兼容、仍需单独告警的类型）。</summary>
    public static IReadOnlyDictionary<string, string> ReplacementMap { get; } =
        Rules.ToDictionary(pair => pair.Key, pair => pair.Value.Target, StringComparer.OrdinalIgnoreCase);

    /// <summary>按需改写单个节点；不是旧类型时返回 false。</summary>
    public static bool TryMigrate(NodeDefinition node, out NodeDefinition migrated)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Type is null || !Rules.TryGetValue(node.Type, out var rule))
        {
            migrated = node;
            return false;
        }

        var parameters = node.Parameters is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(node.Parameters, StringComparer.Ordinal);
        foreach (var (key, value) in rule.Parameters)
            parameters.TryAdd(key, JsonSerializer.SerializeToElement(value));

        migrated = node with { Type = rule.Target, Parameters = parameters };
        return true;
    }

    /// <summary>改写整份流程；没有旧类型时原样返回同一实例。</summary>
    public static WorkflowDefinition Normalize(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var changed = false;
        var nodes = new List<NodeDefinition>(workflow.Nodes.Count);
        foreach (var node in workflow.Nodes)
        {
            if (TryMigrate(node, out var migrated))
            {
                nodes.Add(migrated);
                changed = true;
            }
            else
            {
                nodes.Add(node);
            }
        }

        return changed ? workflow with { Nodes = nodes } : workflow;
    }
}
