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

    /// <summary>Writes the final record with an independent bounded token; failure surfaces to the caller.</summary>
    public async Task FinalizeAsync(WorkflowRunResult result, WorkflowDefinition workflow, RunTraceContext context)
    {
        using var timeout = new CancellationTokenSource(WriteTimeout);
        try
        {
            await traces.RecordAsync(result, workflow, context, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Trace finalization for run '{result.RunId}' timed out after {WriteTimeout.TotalSeconds:0} s.");
        }
    }
}