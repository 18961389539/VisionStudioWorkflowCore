using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

/// <summary>Native JLVision NCC model matching with subpixel translation and rotation.</summary>
public sealed class TemplateMatchNode : IVisionNodeExecutor
{
    public string Type => "match.template";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
        => ValueTask.FromResult(JLVisionMatching.Execute(context, node, false, cancellationToken));
}
