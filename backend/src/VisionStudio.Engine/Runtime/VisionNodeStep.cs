using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Workflow Core step for coarse orchestration nodes (camera/device/robot/control flow)
/// and conservative plugin nodes. Hot compute nodes are fused into VisionPipelineStep.
/// </summary>
public sealed class VisionNodeStep(VisionNodeRuntime nodeRuntime) : StepBodyAsync
{
    public string NodeId { get; set; } = string.Empty;

    public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
    {
        if (context.Workflow.Data is not VisionWorkflowData data)
            throw new InvalidOperationException("Workflow data is not VisionWorkflowData.");

        await nodeRuntime.ExecuteAsync(data, NodeId, context.CancellationToken);
        return ExecutionResult.Next();
    }
}
