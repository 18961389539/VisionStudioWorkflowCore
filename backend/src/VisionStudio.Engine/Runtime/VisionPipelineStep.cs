using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Coarse Workflow Core step representing one contiguous high-frequency vision segment.
/// </summary>
public sealed class VisionPipelineStep(VisionPipelineExecutor executor) : StepBodyAsync
{
    public string SegmentId { get; set; } = string.Empty;

    public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
    {
        if (context.Workflow.Data is not VisionWorkflowData data)
            throw new InvalidOperationException("Workflow data is not VisionWorkflowData.");

        await executor.ExecuteAsync(data, SegmentId, context.CancellationToken);
        return ExecutionResult.Next();
    }
}
