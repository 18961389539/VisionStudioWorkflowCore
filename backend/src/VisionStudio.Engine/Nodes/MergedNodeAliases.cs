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
