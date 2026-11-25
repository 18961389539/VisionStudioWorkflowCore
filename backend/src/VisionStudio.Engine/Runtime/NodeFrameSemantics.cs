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
    internal const string DefaultInputFrame = "CurrentImage";
    internal const string DefaultReferenceFrame = "ReferenceImage";

    internal static string RoiReferenceFrame(string nodeId) => $"roi:{nodeId}:reference";

    internal readonly record struct TransformFrameEndpoints(string SourceFrame, string TargetFrame);

    internal static TransformFrameEndpoints? ResolveTransformFrames(
        string nodeId,
        NodeFrameRule rule,
        Func<string, string?> resolveInputFrame,
        Func<string, string?> resolveReferenceFrame,
        string? outputFrame)
    {
        var inputFrame = resolveInputFrame(rule.InputPort);
        return rule.Transform switch
        {
            NodeTransformFramePolicy.Spatial when outputFrame is not null =>
                new TransformFrameEndpoints(inputFrame ?? DefaultInputFrame, outputFrame),
            NodeTransformFramePolicy.RoiReference =>
                new TransformFrameEndpoints(RoiReferenceFrame(nodeId), inputFrame ?? DefaultInputFrame),
            NodeTransformFramePolicy.ReferenceInput =>
                new TransformFrameEndpoints(inputFrame ?? DefaultInputFrame, resolveReferenceFrame(rule.ReferencePort ?? "reference") ?? DefaultReferenceFrame),
            _ => null
        };
    }

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
        => NodeParameterValue.Resolve(node, catalog, name);
}
