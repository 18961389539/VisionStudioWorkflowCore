using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class ProductionTraceWriterTests
{
    [Fact]
    public async Task BoundedQueueAppliesBackpressureAndShutdownDrainsInOrder()
    {
        var entered = Signal();
        var release = Signal();
        var saved = new ConcurrentQueue<string>();
        var writer = new ProductionTraceWriter(async (result, _, _, _) =>
        {
            if (result.RunId == "first") { entered.SetResult(); await release.Task; }
            saved.Enqueue(result.RunId);
        }, new RunStore(), new() { QueueCapacity = 1 }, NullLogger.Instance);
        try
        {
            await writer.EnqueueAsync(Result("first"), Workflow, Context);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await writer.EnqueueAsync(Result("second"), Workflow, Context);
            var blocked = writer.EnqueueAsync(Result("third"), Workflow, Context);
            Assert.False(blocked.IsCompleted);
            Assert.Empty(saved);
            release.SetResult();
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); await writer.DisposeAsync(); }
        Assert.Equal(["first", "second", "third"], saved.ToArray());
        Assert.Equal(0, writer.PendingCount);
    }

    [Fact]
    public async Task StopWaitsForAcceptedRecordsAndFailureIsSurfacedAfterDrain()
    {
        var entered = Signal();
        var release = Signal();
        var saved = new ConcurrentQueue<string>();
        var writer = new ProductionTraceWriter(async (result, _, _, _) =>
        {
            if (result.RunId == "failed")
            {
                entered.SetResult();
                await release.Task;
                throw new IOException("storage unavailable");
            }
            saved.Enqueue(result.RunId);
        }, new RunStore(), new() { QueueCapacity = 2 }, NullLogger.Instance);
        await writer.EnqueueAsync(Result("failed"), Workflow, Context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.EnqueueAsync(Result("accepted"), Workflow, Context);
        var stopping = writer.DisposeAsync().AsTask();
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopping);
        Assert.Equal(["accepted"], saved.ToArray());
        Assert.Equal(0, writer.PendingCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.EnqueueAsync(Result("rejected"), Workflow, Context));
    }

    [Fact]
    public async Task EnqueueAndPersist_WaitsForCommitAndSurfacesPersistenceFailure()
    {
        var entered = Signal();
        var release = Signal();
        var fail = false;
        var writer = new ProductionTraceWriter(async (_, _, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            if (fail) throw new IOException("commit failed");
        }, new RunStore(), new(), NullLogger.Instance);
        try
        {
            var pending = writer.EnqueueAndPersistAsync(Result("commit-wait"), Workflow, Context);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted); // queue acceptance alone does not satisfy the production safety gate.
            release.SetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await writer.DisposeAsync(); }

        var enteredFailure = Signal();
        var releaseFailure = Signal();
        var failedWriter = new ProductionTraceWriter(async (_, _, _, _) =>
        {
            enteredFailure.TrySetResult();
            await releaseFailure.Task;
            throw new IOException("commit failed");
        }, new RunStore(), new(), NullLogger.Instance);
        var failed = failedWriter.EnqueueAndPersistAsync(Result("commit-failed"), Workflow, Context);
        await enteredFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(failed.IsCompleted);
        releaseFailure.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedWriter.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task BackgroundEncodingPersistsImagesAndKeepsOriginalStartTime()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var traces = new TraceabilityStore(db, env, Options.Create(new TraceRetentionOptions { OkPreviewSampleEvery = 1, OkReplaySampleEvery = 1 }));
        var runs = new RunStore();
        using var source = new Mat(12, 16, MatType.CV_8UC1, Scalar.All(100));
        var images = RunImageArtifacts.Capture(source, source, "n", 1024);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var result = Result("background") with { DeferredArtifacts = images, StartedAt = start };
        var writer = new ProductionTraceWriter(traces, runs, new(), NullLogger.Instance);
        await writer.EnqueueAsync(result, Workflow, Context);
        source.Dispose();
        await writer.DisposeAsync();
        Assert.True(runs.TryGet(result.RunId, out var artifact));
        Assert.NotEmpty(artifact.PreviewJpeg!);
        var trace = await traces.GetAsync(result.RunId, default);
        Assert.NotNull(trace);
        Assert.Equal(start, trace.StartedAt);
        Assert.True(trace.HasPreview);
        Assert.True(trace.HasReplayInput);
        Assert.Null(images.Encode(result).PreviewJpeg); // The consumer released the owned copies.
    }

    [Fact]
    public async Task OversizedArtifactsStillPersistMetadataAndExplainOmission()
    {
        using var env = new TempWebHostEnvironment();
        var traces = new TraceabilityStore(env);
        using var source = new Mat(12, 16, MatType.CV_8UC1, Scalar.All(100));
        var images = RunImageArtifacts.Capture(source, source, "n", 1);
        var writer = new ProductionTraceWriter(traces, new RunStore(), new(), NullLogger.Instance);
        await writer.EnqueueAsync(Result("oversized") with { DeferredArtifacts = images }, Workflow, Context);
        await writer.DisposeAsync();
        var trace = await traces.GetAsync("oversized", default);
        Assert.NotNull(trace);
        Assert.Contains("limit", trace.Note!);
        Assert.False(trace.HasPreview);
        Assert.False(trace.HasReplayInput);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly WorkflowDefinition Workflow = new("trace-writer", "Trace writer", [], []);
    private static readonly RunTraceContext Context = new("ProductionRuntime");
    private static WorkflowRunResult Result(string id) => new(id, true, 1, false, 0, 0, [], [], null);
}
