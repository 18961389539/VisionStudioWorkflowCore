using System.Threading.Channels;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed class ProductionTraceOptions
{
    public int QueueCapacity { get; set; } = 8;
    public long MaxRawArtifactBytesPerRun { get; set; } = 32L * 1024 * 1024;
    /// <summary>
    /// R05（2026-10-12 复审）：单次生产追溯提交（含图像编码、文件 I/O、数据库提交）的等待预算。
    /// 超限后循环停止进入 Faulted 并保留设备动作意图，直到在途提交排空——不允许"超时返回继续生产"。
    /// </summary>
    public int CommitBudgetMs { get; set; } = 30_000;
    /// <summary>R05：停止时排空在途追溯记录的预算（Dispose 等待上限）；超限放弃等待（设备动作意图仍保底）。</summary>
    public int DrainBudgetMs { get; set; } = 15_000;
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
    private readonly ProductionTraceOptions _options;
    private readonly Task _worker;
    private Exception? _failure;
    private int _pending;

    public RunArtifactOptions ArtifactOptions { get; }
    public int PendingCount => Volatile.Read(ref _pending);

    public ProductionTraceWriter(TraceabilityStore traces, RunStore runs, ProductionTraceOptions options, ILogger logger)
        // R03：生产追溯提交走强持久连接（synchronous=FULL）——意图闭合以"提交已耐久"为前提。
        : this(async (result, workflow, context, ct) => { await traces.RecordAsync(result, workflow, context, ct, strongDurability: true); },
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
        _options = options;
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

    /// <summary>
    /// R05：排空必须有界。旧实现无限等待 worker——存储挂起时 Dispose 永远不会返回。
    /// 超限放弃等待（后台提交可能仍在运行，由 Runtime 的"在途提交"闸门禁止新周期，
    /// 设备动作意图保持未闭合，重启后要求核对——不会静默丢失）。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        var budgetMs = Math.Max(1_000, _options.DrainBudgetMs);
        try
        {
            await _worker.WaitAsync(TimeSpan.FromMilliseconds(budgetMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Production trace drain did not complete within {BudgetMs} ms; the persistence worker may still be running. " +
                "New production cycles stay blocked until it drains, and the device-action intent remains in force.",
                budgetMs);
        }
    }
}
