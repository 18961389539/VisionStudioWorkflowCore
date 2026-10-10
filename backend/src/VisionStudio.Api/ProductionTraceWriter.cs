using System.Threading.Channels;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed class ProductionTraceOptions
{
    public int QueueCapacity { get; set; } = 8;
    public long MaxRawArtifactBytesPerRun { get; set; } = 32L * 1024 * 1024;
}

/// <summary>
/// One writer per production loop. Queue saturation applies backpressure; completion drains accepted runs.
/// At most QueueCapacity queued images, one consumer and one waiting producer own sampled image copies.
/// </summary>
public sealed class ProductionTraceWriter : IAsyncDisposable
{
    private sealed record Item(WorkflowRunResult Result, WorkflowDefinition Workflow, RunTraceContext Context, TaskCompletionSource Persisted);
    private readonly Channel<Item> _queue;
    private readonly Func<WorkflowRunResult, WorkflowDefinition, RunTraceContext, CancellationToken, Task> _persist;
    private readonly RunStore _runs;
    private readonly ILogger _logger;
    private readonly Task _worker;
    private Exception? _failure;
    private int _pending;

    public RunArtifactOptions ArtifactOptions { get; }
    public int PendingCount => Volatile.Read(ref _pending);

    public ProductionTraceWriter(TraceabilityStore traces, RunStore runs, ProductionTraceOptions options, ILogger logger)
        : this(async (result, workflow, context, ct) => { await traces.RecordAsync(result, workflow, context, ct); },
            runs, options, logger, traces.GetArtifactOptions(options.MaxRawArtifactBytesPerRun)) { }

    public ProductionTraceWriter(
        Func<WorkflowRunResult, WorkflowDefinition, RunTraceContext, CancellationToken, Task> persist,
        RunStore runs, ProductionTraceOptions options, ILogger logger, RunArtifactOptions? artifacts = null)
    {
        if (options.QueueCapacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(options.QueueCapacity));
        if (options.MaxRawArtifactBytesPerRun <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxRawArtifactBytesPerRun));
        _persist = persist;
        _runs = runs;
        _logger = logger;
        ArtifactOptions = artifacts ?? new RunArtifactOptions(DeferEncoding: true, MaxRawBytes: options.MaxRawArtifactBytesPerRun);
        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    public void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _failure) is { } failure)
            throw new InvalidOperationException("Production trace persistence failed; further cycles are blocked.", failure);
    }

    // Always transfers ownership, including rejection/cancellation: callers must not dispose accepted artifacts.
    public async Task EnqueueAsync(WorkflowRunResult result, WorkflowDefinition workflow, RunTraceContext context, CancellationToken ct = default)
        => await EnqueueCoreAsync(result, workflow, context, waitForPersistence: false, ct);

    /// <summary>Enqueue a production run and wait until its trace transaction has committed.</summary>
    public async Task EnqueueAndPersistAsync(WorkflowRunResult result, WorkflowDefinition workflow, RunTraceContext context, CancellationToken ct = default)
        => await EnqueueCoreAsync(result, workflow, context, waitForPersistence: true, ct);

    private async Task EnqueueCoreAsync(WorkflowRunResult result, WorkflowDefinition workflow, RunTraceContext context, bool waitForPersistence, CancellationToken ct)
    {
        Interlocked.Increment(ref _pending);
        var item = new Item(result, workflow, context, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var accepted = false;
        try
        {
            ThrowIfFaulted();
            await _queue.Writer.WriteAsync(item, ct);
            accepted = true;
            if (waitForPersistence) await item.Persisted.Task.WaitAsync(ct);
        }
        catch (Exception ex)
        {
            if (!accepted)
            {
                Interlocked.Decrement(ref _pending);
                result.DeferredArtifacts?.Dispose();
                _logger.LogError(ex, "Run {RunId} could not be accepted by the production trace queue", result.RunId);
            }
            throw;
        }
    }

    private async Task ProcessAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            using var images = item.Result.DeferredArtifacts;
            try
            {
                var result = item.Result;
                var context = item.Context;
                if (images is not null)
                {
                    if (images.Warning is not null)
                    {
                        context = context with { Note = images.Warning };
                        _logger.LogWarning("Run {RunId}: {Warning}", result.RunId, images.Warning);
                    }
                    try { result = images.Encode(result); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Run {RunId} artifact encoding failed; persisting metadata", result.RunId);
                        context = context with { Note = $"Artifact encoding failed: {ex.Message}" };
                        result = result with { DeferredArtifacts = null };
                    }
                }
                _runs.Put(result);
                await _persist(result, item.Workflow, context, CancellationToken.None);
                item.Persisted.TrySetResult();
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ex, null);
                item.Persisted.TrySetException(new InvalidOperationException($"Run {item.Result.RunId} trace persistence failed.", ex));
                _ = item.Persisted.Task.Exception; // callers using EnqueueAsync do not await the commit task.
                _queue.Writer.TryComplete();
                _logger.LogError(ex, "Run {RunId} trace persistence failed; stopping production and draining accepted records", item.Result.RunId);
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
        ThrowIfFaulted();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker;
    }
}
