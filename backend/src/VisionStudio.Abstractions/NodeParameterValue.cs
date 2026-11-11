using System.Globalization;
using System.Text.Json;

namespace VisionStudio.Abstractions;

/// <summary>
/// "节点值优先、目录默认值兜底"的唯一实现（2026-11-10 设计审计收口）。
///
/// 此前 Engine/Api 各自实现了五份，且多处用 CurrentCulture 格式化数值默认值：
/// 在逗号小数的区域（de-DE / fr-FR）默认值 0.5 会被读成 "0,5"，
/// 导致动作声明、frame 条件、端口必需性与运行时依赖对同一个节点给出不同结论。
/// 统一后用 invariant 文本比较；节点显式值按 JSON 标量原样读取（数字取 raw text）。
/// </summary>
public static class NodeParameterValue
{
    /// <summary>节点显式标量 → 原样字符串；否则目录默认值 → invariant 文本；都没有则 null。</summary>
    public static string? Resolve(NodeDefinition node, NodeCatalogItem catalog, string name)
    {
        if (node.Parameters is not null && node.Parameters.TryGetValue(name, out var raw))
        {
            return raw.ValueKind switch
            {
                JsonValueKind.String => raw.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => raw.GetRawText(),
                _ => null
            };
        }

        var descriptor = catalog.Parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return descriptor is null ? null : FormatInvariant(descriptor.DefaultValue);
    }

    /// <summary>目录默认值的 invariant 文本；null 默认值与 Convert.ToString(null) 一致返回空串。</summary>
    public static string FormatInvariant(object? value)
    {
        if (value is null) return string.Empty;
        if (value is JsonElement json)
            return json.ValueKind == JsonValueKind.String ? json.GetString() ?? string.Empty : json.ToString();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
