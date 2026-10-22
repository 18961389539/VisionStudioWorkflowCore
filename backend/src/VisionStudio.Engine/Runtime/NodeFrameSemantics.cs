using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Resolves catalog-declared image frame semantics. The compiler and the dispatcher both read this
/// single declaration, so a plugin that declares <see cref="NodeImageFramePolicy.NewSpatialFrame"/>
/// gets the same frame tracking as a built-in operator without the host knowing its type string.
/// </summary>
internal static class NodeFrameSemantics
{
    /// <summary>First rule whose parameter condition matches, or null when the catalog declares no frames.</summary>
    internal static NodeFrameRule? Resolve(NodeDefinition node, NodeCatalogItem catalog)
    {
        if (catalog.Frames is not { Count: > 0 } rules) return null;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.WhenParameter)) return rule;
            var actual = ParameterValue(node, catalog, rule.WhenParameter);
            if (rule.WhenValues is { Count: > 0 } expected
                && expected.Any(value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase)))
                return rule;
        }

        return null;
    }

    /// <summary>第一条匹配该输出端口与参数条件的输出帧规则。</summary>
    internal static NodeOutputFrameRule? ResolveOutput(NodeDefinition node, NodeCatalogItem catalog, string outputPort)
    {
        if (catalog.OutputFrames is not { Count: > 0 } rules) return null;

        foreach (var rule in rules)
        {
            if (rule.OutputPort is { } declaredPort
                && !declaredPort.Equals(outputPort, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(rule.WhenParameter)) return rule;
            var actual = ParameterValue(node, catalog, rule.WhenParameter);
            if (rule.WhenValues is { Count: > 0 } expected
                && expected.Any(value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase)))
                return rule;
        }

        return null;
    }

    /// <summary>
    /// Image output frame key for the resolved rule. Returns null only when the declaration resolves
    /// to "no frame" (an unconnected <see cref="NodeImageFramePolicy.InputFrame"/> without a fallback).
    /// </summary>
    internal static string? ImageFrameKey(
        string nodeId,
        NodeFrameRule? rule,
        Func<string, string?> inputFrame,
        Func<string, string?> transformTargetFrame)
    {
        if (rule is null) return null;

        return rule.Policy switch
        {
            NodeImageFramePolicy.PreserveInput => inputFrame(rule.InputPort) ?? $"image:{nodeId}:source",
            NodeImageFramePolicy.NewSpatialFrame => $"image:{nodeId}:spatial",
            NodeImageFramePolicy.NewFixedFrame => $"image:{nodeId}:{rule.FrameSuffix ?? "output"}",
            NodeImageFramePolicy.TransformTarget => transformTargetFrame(rule.TransformPort ?? "transform") ?? $"image:{nodeId}:spatial",
            NodeImageFramePolicy.InputFrame => inputFrame(rule.InputPort) ?? rule.FallbackFrame,
            _ => null
        };
    }

    internal static string FrameLabel(string nodeId, NodeFrameRule rule, string defaultLabel)
        => $"{nodeId}.image ({rule.FrameLabel ?? defaultLabel})";

    private static string? ParameterValue(NodeDefinition node, NodeCatalogItem catalog, string name)
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

        return catalog.Parameters
            .FirstOrDefault(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.DefaultValue?.ToString();
    }
}
