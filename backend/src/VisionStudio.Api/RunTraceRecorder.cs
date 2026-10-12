using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>
/// Owns the run trace lifecycle for request-driven runs. The start record and the finalization deliberately
/// ignore the request cancellation token: a client disconnect must not erase evidence of device operations
/// that already happened. Writes are bounded so a stalled metadata store cannot pin the caller forever.
/// </summary>
public sealed class RunTraceRecorder(TraceabilityStore traces, ILogger<RunTraceRecorder> logger)
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    public static string NewRunId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Best-effort start record. A failure is logged and the run proceeds: <see cref="FinalizeAsync"/>
    /// upserts the full record even when no start row exists.
    /// </summary>
    public async Task BeginAsync(string runId, DateTimeOffset startedAt, WorkflowDefinition workflow, RunTraceContext context)
    {
        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await traces.BeginAsync(runId, startedAt, workflow, context, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Run {RunId} start record could not be written.", runId);
        }
    }

    /// <summary>
    /// R01：副作用运行的起始记录——失败必须抛出，调用方不得盲跑设备动作。
    /// F03（2026-10-12 复核）：起始记录是"已接受未完成"的恢复锚点，写强持久连接。
    /// 纯计算运行保留 <see cref="BeginAsync"/> 的 best-effort 语义。
    /// </summary>
    public async Task BeginStrictAsync(string runId, DateTimeOffset startedAt, WorkflowDefinition workflow, RunTraceContext context)
    {
        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await traces.BeginAsync(runId, startedAt, workflow, context, timeout.Token, strongDurability: true);
        }
        catch (Exception ex)
        {
            throw new ApiUnavailableException(
                $"Refusing to execute a device side-effect workflow: its start record could not be persisted ({ex.Message}). " +
                "Resolve the metadata storage problem before running device actions.");
        }
    }

    /// <summary>
    /// Writes the final record with an independent bounded token; failure surfaces to the caller.
    /// F03（2026-10-12 复核）：**统一强持久**（synchronous=FULL）——设备动作不只发生在生产循环，
    /// 临时/调试/AdHoc 入口的"意图闭合"同样以"完成证据已耐久"为前提。调用方知道本次是否含副作用，
    /// 但让所有入口共用安全默认可消除"某入口忘传参数导致断电窗口"的漏接；交互式运行频率有限，
    /// 多付一次 fsync 可接受（若未来出现性能热点，再按副作用显式细分）。
    /// </summary>
    public async Task FinalizeAsync(WorkflowRunResult result, WorkflowDefinition workflow, RunTraceContext context)
    {
        using var timeout = new CancellationTokenSource(WriteTimeout);
        try
        {
            await traces.RecordAsync(result, workflow, context, timeout.Token, strongDurability: true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Trace finalization for run '{result.RunId}' timed out after {WriteTimeout.TotalSeconds:0} s.");
        }
    }
}