namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 已合并节点的兼容别名执行器。
///
/// 这些类型已从目录移除——它们只是规范节点的一个参数取值（高斯/中值 = image.filter 的 method，
/// 顶帽/底帽/梯度 = image.morphology 的 operation，灰度区间 = region.fromRange 的 mode，
/// 垂直度 = measure.parallelism 的 mode）。但 2026-10 之前保存的流程仍会引用旧类型，
/// 因此保留同名执行器作为别名：**不复制任何算法**，直接调用规范节点的实现，
/// 行为与规范节点逐位一致。
///
/// 注册表因此允许“目录之外的别名”，漂移检查对 <see cref="NodeTypeMigration.ReplacementMap"/>
/// 中的类型豁免。新文档在编译/加载时由 <see cref="NodeTypeMigration"/> 改写为规范类型，
/// 别名只服务历史文档；等到不再有旧文档时，整份文件可以删除。
/// </summary>
public sealed class GaussianBlurNode : IVisionNodeExecutor
{
    public string Type => "image.blur.gaussian";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        return ValueTask.FromResult(GaussianBlurFilter.Execute(context, node, Type, cancellationToken));
    }
}

public sealed class MedianBlurNode : IVisionNodeExecutor
{
    public string Type => "image.blur.median";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        return ValueTask.FromResult(MedianBlurFilter.Execute(context, node, Type, cancellationToken));
    }
}

public sealed class ImageTopHatNode : IVisionNodeExecutor
{
    public string Type => "image.topHat";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        return ValueTask.FromResult(GrayMorphology.Execute(Type, context, node, "topHat", cancellationToken));
    }
}

public sealed class ImageBottomHatNode : IVisionNodeExecutor
{
    public string Type => "image.bottomHat";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        return ValueTask.FromResult(GrayMorphology.Execute(Type, context, node, "bottomHat", cancellationToken));
    }
}

public sealed class ImageMorphGradientNode : IVisionNodeExecutor
{
    public string Type => "image.morphGradient";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        return ValueTask.FromResult(GrayMorphology.Execute(Type, context, node, "gradient", cancellationToken));
    }
}

public sealed class RegionFromThresholdNode : IVisionNodeExecutor
{
    public string Type => "region.fromThreshold";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        return ValueTask.FromResult(RegionFromThresholdCore.Execute(context, node, Type, cancellationToken));
    }
}

public sealed class PerpendicularityNode : IVisionNodeExecutor
{
    public string Type => "measure.perpendicularity";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        _ = context.Require<VisionLine2D>("a");
        _ = context.Require<VisionLine2D>("b");
        var limit = node.GetDouble("maxAngleErrorDeg", 0);
        if (!double.IsFinite(limit) || limit < 0 || limit > 90)
            throw new InvalidOperationException("measure.perpendicularity maxAngleErrorDeg must be finite in [0, 90] (0 disables).");
        var result = LineOrientationQuality.Execute(context, node, Type, perpendicular: true, cancellationToken);
        if (!result.Outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("measure.perpendicularity diagnostics missing.");
        return ValueTask.FromResult(result);
    }
}

/// <summary>兼容别名参数改写：克隆参数并覆盖指定键（不修改调用方传入的节点）。</summary>
internal static class AliasParameters
{
    public static NodeDefinition With(NodeDefinition node, params (string Key, string Value)[] overrides)
    {
        var parameters = node.Parameters is null
            ? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, System.Text.Json.JsonElement>(node.Parameters, StringComparer.Ordinal);
        foreach (var (key, value) in overrides)
            parameters[key] = System.Text.Json.JsonSerializer.SerializeToElement(value);
        return node with { Parameters = parameters };
    }
}

public sealed class GrayscaleNode : IVisionNodeExecutor
{
    public string Type => "image.grayscale";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        // 旧 mode 与 image.channel 的 channel 同义：luminance/bt601/gray → gray，average/mean → average，max → max。
        var mode = (node.GetString("mode", "luminance") ?? "luminance").Trim().ToLowerInvariant();
        var channel = mode switch
        {
            "luminance" or "bt601" or "gray" => "gray",
            "average" or "mean" => "average",
            "max" => "max",
            _ => throw new InvalidOperationException($"{Type} mode '{mode}' is unsupported; use luminance, average or max.")
        };
        return new ChannelExtractNode().ExecuteAsync(context, AliasParameters.With(node, ("channel", channel)), cancellationToken);
    }
}

public sealed class AdaptiveThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.threshold.adaptive";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        return new ThresholdNode().ExecuteAsync(context, AliasParameters.With(node, ("mode", "adaptive")), cancellationToken);
    }
}

public sealed class OtsuThresholdNode : IVisionNodeExecutor
{
    public string Type => "image.threshold.otsu";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        return new ThresholdNode().ExecuteAsync(context, AliasParameters.With(node, ("mode", "otsu")), cancellationToken);
    }
}

public sealed class TextMatchNode : IVisionNodeExecutor
{
    public string Type => "text.match";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        // text.match 的 mode 取值与 text.validate 完全一致（equals/contains/startsWith/endsWith）。
        return new TextValidationNode().ExecuteAsync(context, node, cancellationToken);
    }
}

public sealed class TextRegexNode : IVisionNodeExecutor
{
    public string Type => "text.regex";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        // 旧 text.regex 显式拒绝空 pattern；text.validate 允许，因此在这里补回。
        if (string.IsNullOrWhiteSpace(node.GetString("pattern", "[A-Z0-9]+")))
            throw new InvalidOperationException("text.regex pattern must be non-empty.");
        var withMode = AliasParameters.With(node, ("mode", "regex"), ("countMatches", "true"));
        // 旧节点的默认空输入策略是 fail；旧文档显式写了 nomatch 时保持不变。
        var effective = string.IsNullOrWhiteSpace(node.GetString("emptyInputPolicy", null))
            ? AliasParameters.With(withMode, ("emptyInputPolicy", "fail"))
            : withMode;
        return new TextValidationNode().ExecuteAsync(context, effective, cancellationToken);
    }
}

public sealed class RegionFromColorRangeNode : IVisionNodeExecutor
{
    public string Type => "region.fromColorRange";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        return new RegionFromRangeNode().ExecuteAsync(context, AliasParameters.With(node, ("mode", "color")), cancellationToken);
    }
}

public sealed class LargestBlobNode : IVisionNodeExecutor
{
    public string Type => "measure.blob";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        return new MultiBlobNode().ExecuteAsync(context, AliasParameters.With(node, ("mode", "largest")), cancellationToken);
    }
}
