using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api.Storage.EfCore;

namespace VisionStudio.Api.Security;

public sealed class AuditQueueOptions
{
    public int Capacity { get; set; } = 1024;
    public int BatchSize { get; set; } = 64;
    public int FlushIntervalMs { get; set; } = 5;
    public int DrainTimeoutMs { get; set; } = 15_000;
}

public sealed partial class AuditEventStore : IHostedService, IDisposable, IAsyncDisposable
{
    private sealed record PendingAudit(AuditChainEntry Entry, TaskCompletionSource Committed);

    private readonly IVisionStudioDbFactory _dbFactory;
    private readonly ILogger<AuditEventStore> _logger;
    private readonly StorageMaintenanceCoordinator _maintenance;
    private readonly Channel<PendingAudit> _queue;
    private readonly CancellationTokenSource _abort = new();
    private readonly int _batchSize;
    private readonly int _flushIntervalMs;
    private readonly int _drainTimeoutMs;
    private readonly Task _writer;
    private int _stopping;
    private int _disposed;
    private int _pending;
    private long _committedBatches;
    private long _persistedEvents;
    private long _shutdownFailures;

    public int PendingCount => Volatile.Read(ref _pending);
    public long CommittedBatches => Interlocked.Read(ref _committedBatches);
    public long PersistedEvents => Interlocked.Read(ref _persistedEvents);
    public long ShutdownFailures => Interlocked.Read(ref _shutdownFailures);

    public AuditEventStore(SqliteMetadataDatabase database) : this(new VisionStudioDbFactory(database)) { }

    public AuditEventStore(IVisionStudioDbFactory dbFactory, AuditQueueOptions? options = null,
        ILogger<AuditEventStore>? logger = null, StorageMaintenanceCoordinator? maintenance = null)
    {
        options ??= new AuditQueueOptions();
        if (options.Capacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(options.Capacity));
        if (options.BatchSize is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(options.BatchSize));
        if (options.FlushIntervalMs is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(options.FlushIntervalMs));
        if (options.DrainTimeoutMs is < 1 or > 120000) throw new ArgumentOutOfRangeException(nameof(options.DrainTimeoutMs));
        _dbFactory = dbFactory;
        _logger = logger ?? NullLogger<AuditEventStore>.Instance;
        _maintenance = maintenance ?? new StorageMaintenanceCoordinator();
        _batchSize = options.BatchSize;
        _flushIntervalMs = options.FlushIntervalMs;
        _drainTimeoutMs = options.DrainTimeoutMs;
        _queue = Channel.CreateBounded<PendingAudit>(new BoundedChannelOptions(options.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writer = Task.Run(ProcessQueueAsync);
    }

    private async Task EnqueueAsync(AuditChainEntry entry, CancellationToken ct)
    {
        IDisposable? lease = null;
        var item = new PendingAudit(entry, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Interlocked.Increment(ref _pending);
        try
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                lease = _maintenance.TryEnterRequest(allowRestartPending: true)
                    ?? throw new InvalidOperationException("Audit writes are blocked by storage maintenance.");
                await _queue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordPersistFailure(ex);
                _logger.LogError(ex, "Audit queue rejected {Action} {Resource}", entry.Action, entry.Resource);
                throw;
            }
            // Admission can be cancelled; an accepted event must finish committing even if the request disconnects.
            await item.Committed.Task.ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<PendingAudit>(_batchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_abort.Token).ConfigureAwait(false))
            {
                while (batch.Count < _batchSize && _queue.Reader.TryRead(out var item)) batch.Add(item);
                if (batch.Count < _batchSize && Volatile.Read(ref _stopping) == 0)
                {
                    // Collect before opening the database: no SQLite write lock is held during the batching window.
                    await Task.Delay(_flushIntervalMs, _abort.Token).ConfigureAwait(false);
                    while (batch.Count < _batchSize && _queue.Reader.TryRead(out var item)) batch.Add(item);
                }
                try
                {
                    await PersistBatchAsync(batch, _abort.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _committedBatches);
                    Interlocked.Add(ref _persistedEvents, batch.Count);
                    foreach (var item in batch) item.Committed.TrySetResult();
                }
                catch (Exception ex)
                {
                    FailBatch(batch, ex);
                }
                batch.Clear();
            }
        }
        catch (Exception ex)
        {
            _queue.Writer.TryComplete(ex);
            while (_queue.Reader.TryRead(out var item)) batch.Add(item);
            FailBatch(batch, ex);
        }
    }

    private void FailBatch(IReadOnlyList<PendingAudit> batch, Exception exception)
    {
        if (batch.Count == 0) return;
        RecordPersistFailure(exception, batch.Count);
        _logger.LogError(exception, "Audit batch commit failed for {Count} events; audit trail degraded. No automatic replay.", batch.Count);
        foreach (var item in batch) item.Committed.TrySetException(exception);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, 1);
        _queue.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromMilliseconds(_drainTimeoutMs), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _abort.Cancel();
            Interlocked.Increment(ref _shutdownFailures);
            RecordPersistFailure(ex, count: 0);
            _logger.LogError(ex, "Audit queue did not drain within the shutdown budget; {Pending} calls remain outstanding.", PendingCount);
            throw;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // A timed-out SQLite call can outlive disposal; release its cancellation source once it exits.
            _ = _writer.ContinueWith(_ => _abort.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
