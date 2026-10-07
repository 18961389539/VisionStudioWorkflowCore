namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Vendor-neutral runtime boundary used by the API. The MVP implementation is backed by Workflow Core.
/// </summary>
public interface IVisionWorkflowRunner
{
    /// <summary>
    /// Executes a workflow. Callers that pre-record run evidence (for example the API trace lifecycle)
    /// may supply <paramref name="runId"/> so the start record and the final result share one identity;
    /// otherwise an identifier is generated.
    /// </summary>
    Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition workflow,
        VisionRunOptions? options = null,
        string? runId = null,
        CancellationToken cancellationToken = default);
}
