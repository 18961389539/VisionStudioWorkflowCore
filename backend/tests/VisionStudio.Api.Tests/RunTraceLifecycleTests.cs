using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Trace lifecycle: a start record, a finalization that survives interrupted requests, and
/// cancelled/unknown execution statuses with structured error codes.
/// </summary>
public sealed class RunTraceLifecycleTests
{
    [Fact]
    public async Task BeginThenRecord_UpdatesSameRow_AndMarksCancelledWithCode()
    {
        using var env = new TempWebHostEnvironment();
        var store = Store(new SqliteMetadataDatabase(env), env);
        var workflow = Workflow(1);

        await store.BeginAsync("run-cancel", DateTimeOffset.UtcNow, workflow, new RunTraceContext("AdHoc"), default);
        var started = await store.GetAsync("run-cancel", default);

        Assert.NotNull(started);
        Assert.Equal("Running", started!.ExecutionStatus);
        Assert.Equal("PENDING", started.Disposition);
        Assert.Equal(0, started.NodeCount);
        // Ad-hoc start records embed the workflow so an interrupted run stays resolvable.
        Assert.NotNull(await store.GetWorkflowSnapshotAsync("run-cancel", default));

        await store.RecordAsync(
            new WorkflowRunResult("run-cancel", false, 12, false, 0, 0, [], [], null, "Run was cancelled before completion.", "Error")
            {
                ErrorCode = VisionRunErrorCodes.Cancelled
            },
            workflow,
            new RunTraceContext("AdHoc"),
            default);

        var finalized = await store.GetAsync("run-cancel", default);
        Assert.NotNull(finalized);
        Assert.Equal("Cancelled", finalized!.ExecutionStatus);
        Assert.Equal(VisionRunErrorCodes.Cancelled, finalized.ErrorCode);
        Assert.Equal("ERROR", finalized.Disposition);
        Assert.Equal(12d, finalized.TotalDurationMs);
        Assert.Equal(1L, await CountRowsAsync(store));
    }

    [Fact]
    public async Task RecordWithoutBegin_InsertsFullRow_WithNodeFailureCode()
    {
        using var env = new TempWebHostEnvironment();
        var store = Store(new SqliteMetadataDatabase(env), env);
        var workflow = Workflow(2);

        // Production cycles enqueue only the final result; the upsert must still create the row.
        var record = await store.RecordAsync(
            new WorkflowRunResult("run-production", false, 33, false, 0, 0, [], [], null, "n1: boom", "Error")
            {
                ErrorCode = VisionRunErrorCodes.NodeFailure
            },
            workflow,
            new RunTraceContext("ProductionRuntime", "job-a", 1, "hash-a"),
            default);

        Assert.Equal("Failed", record.ExecutionStatus);
        Assert.Equal(VisionRunErrorCodes.NodeFailure, record.ErrorCode);
        var reread = await store.GetAsync("run-production", default);
        Assert.Equal(VisionRunErrorCodes.NodeFailure, reread!.ErrorCode);
    }

    [Fact]
    public async Task SweepStaleRuns_MarksInterruptedRunsUnknown_AndLeavesFinalizedRows()
    {
        using var env = new TempWebHostEnvironment();
        var store = Store(new SqliteMetadataDatabase(env), env);
        var workflow = Workflow(3);

        await store.BeginAsync("run-interrupted", DateTimeOffset.UtcNow, workflow, new RunTraceContext("AdHoc"), default);
        await store.BeginAsync("run-finished", DateTimeOffset.UtcNow, workflow, new RunTraceContext("AdHoc"), default);
        await store.RecordAsync(
            new WorkflowRunResult("run-finished", true, 5, false, 0, 0, [], [], null, QualityDisposition: "OK"),
            workflow,
            new RunTraceContext("AdHoc"),
            default);

        var swept = await store.SweepStaleRunsAsync(TimeSpan.Zero, default);

        Assert.Equal(1, swept);
        var interrupted = await store.GetAsync("run-interrupted", default);
        Assert.Equal("Unknown", interrupted!.ExecutionStatus);
        Assert.Contains("unknown", interrupted.Error);
        Assert.NotNull(interrupted.Note);
        var finished = await store.GetAsync("run-finished", default);
        Assert.Equal("Complete", finished!.ExecutionStatus);
    }

    [Fact]
    public async Task SweepStaleRuns_RespectsMaximumAge()
    {
        using var env = new TempWebHostEnvironment();
        var store = Store(new SqliteMetadataDatabase(env), env);

        await store.BeginAsync("run-active", DateTimeOffset.UtcNow, Workflow(4), new RunTraceContext("AdHoc"), default);

        Assert.Equal(0, await store.SweepStaleRunsAsync(TimeSpan.FromHours(1), default));
        Assert.Equal("Running", (await store.GetAsync("run-active", default))!.ExecutionStatus);
    }

    private static async Task<long> CountRowsAsync(TraceabilityStore store)
    {
        var page = await store.PageAsync(0, 10, null, null, default);
        return page.Total;
    }

    private static TraceabilityStore Store(SqliteMetadataDatabase db, TempWebHostEnvironment env)
        => new(db, env, Options.Create(new TraceRetentionOptions()));

    private static WorkflowDefinition Workflow(int revision)
        => new("trace-lifecycle", "Trace Lifecycle",
            [new NodeDefinition("n1", "test.lifecycle", "Test", null, new Dictionary<string, JsonElement> { ["revision"] = JsonSerializer.SerializeToElement(revision) })],
            []);
}