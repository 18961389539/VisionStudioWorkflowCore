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

/// <summary>
/// R10（2026-10-12 复审）：统一运行边界的原始异常观察者。节点异常在结果返回路径上只会保留
/// 简短消息（用户诊断），堆栈/InnerException 会随之丢失。宿主在此接收**原始异常**做结构化
/// 日志（RunId/节点/工作流），日志保留完整异常链。由运行器边界只回调一次，避免同一异常重复刷日志。
/// </summary>
public interface IVisionRunExceptionObserver
{
    void OnRunException(VisionRunExceptionContext context, Exception exception);
}

/// <summary>R10：运行异常上下文——把异常定位到 RunId 与节点/工作流。</summary>
public sealed record VisionRunExceptionContext(
    string RunId,
    string? NodeId,
    string? WorkflowId,
    string? WorkflowName);
