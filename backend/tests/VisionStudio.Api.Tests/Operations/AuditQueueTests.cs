using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Security;
using VisionStudio.Api.Storage.EfCore;

namespace VisionStudio.Api.Tests;

public sealed class AuditQueueTests
{
    [Fact]
    public async Task Burst_UsesBoundedBatches_AndSnapshotsRequestDataBeforeQueueing()
    {
        using var env = new TempWebHostEnvironment();
        var factory = new PausedFactory(new SqliteMetadataDatabase(env));
        await using var store = new AuditEventStore(factory, new AuditQueueOptions { BatchSize = 16 });
        var context = Context("first");
        var first = store.RecordAsync(context, new AuditActionMetadata("first", "test"), 200);
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var tasks = new List<Task> { first };
        try
        {
            context.Request.Path = "/changed-after-enqueue";
            AuditContext.SetTarget(context, "changed-target");
            for (var i = 1; i < 128; i++) tasks.Add(RecordAsync(store, $"event-{i}"));
            Assert.Equal(1, factory.Transactions);
            Assert.False(first.IsCompleted);

            // A production-side metadata write can commit while the audit consumer is still collecting/waiting.
            await using (var connection = await factory.Database.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO result_records(id,dataset,record_key,payload_json,run_id,node_id,created_at) VALUES('production','line','part','{}','run','node',$at);";
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { factory.Release.TrySetResult(); }
        await Task.WhenAll(tasks);

        Assert.InRange(factory.Transactions, 8, 9);
        Assert.Equal(factory.Transactions, store.CommittedBatches);
        Assert.Equal(128, store.PersistedEvents);
        Assert.Equal(0, store.PendingCount);
        var rows = await store.ListAsync(0, 500, null, null);
        Assert.Equal(Enumerable.Range(1, 128).Select(i => (long)i), rows.Reverse().Select(x => x.Sequence));
        var saved = rows.Single(x => x.Action == "first");
        Assert.Equal("/test/first", saved.Path);
        Assert.Null(saved.Target);
        Assert.True((await store.VerifyChainAsync()).Valid);
    }

    [Fact]
    public async Task IndependentWriters_RereadCommittedHeadBetweenBatches()
    {
        using var env = new TempWebHostEnvironment();
        var database = new SqliteMetadataDatabase(env);
        await using var first = new AuditEventStore(database);
        await using var second = new AuditEventStore(database);
        await RecordAsync(first, "first");
        await RecordAsync(second, "second");
        await RecordAsync(first, "after-other-writer");
        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(i => RecordAsync(i % 2 == 0 ? first : second, $"concurrent-{i}")));
        var verification = await first.VerifyChainAsync();
        Assert.True(verification.Valid);
        Assert.Equal(35, verification.Checked);
        Assert.Equal(35, verification.LastSeq);
    }

    [Fact]
    public async Task FullQueue_CancelsOnlyUnacceptedWrites_AndStopDrainsAcceptedWrites()
    {
        using var env = new TempWebHostEnvironment();
        var factory = new PausedFactory(new SqliteMetadataDatabase(env));
        await using var store = new AuditEventStore(factory, new AuditQueueOptions { Capacity = 1, BatchSize = 1 });
        using var acceptedCancellation = new CancellationTokenSource();
        var first = RecordAsync(store, "first", acceptedCancellation.Token);
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = RecordAsync(store, "second");
        using var rejectedCancellation = new CancellationTokenSource();
        var rejected = RecordAsync(store, "cancel-before-admission", rejectedCancellation.Token);
        Task stop;
        try
        {
            Assert.False(rejected.IsCompleted);
            rejectedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
            acceptedCancellation.Cancel();
            Assert.False(first.IsCompleted);
            stop = store.StopAsync(CancellationToken.None);
            Assert.False(stop.IsCompleted);
        }
        finally { factory.Release.TrySetResult(); }
        await Task.WhenAll(first, second, stop);
        Assert.Equal(2, store.PersistedEvents);
        Assert.Equal(0, store.PersistFailures);
        Assert.Equal(0, store.PendingCount);
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => RecordAsync(store, "after-stop"));
        Assert.Equal(1, store.PersistFailures);
        Assert.True((await store.VerifyChainAsync()).Valid);
    }

    [Fact]
    public async Task BatchFailure_RollsBackEarlierRows_ReportsEveryFailure_AndCanResume()
    {
        using var env = new TempWebHostEnvironment();
        var factory = new PausedFactory(new SqliteMetadataDatabase(env));
        await using var store = new AuditEventStore(factory, new AuditQueueOptions { BatchSize = 16 });
        var first = RecordAsync(store, "seed");
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = Enumerable.Range(0, 16).Select(i => RecordAsync(store, $"batch-{i}")).ToArray();
        try
        {
            await using var connection = await factory.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_audit BEFORE INSERT ON audit_events
                WHEN NEW.action='batch-15'
                BEGIN SELECT RAISE(ABORT, 'injected audit failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        finally { factory.Release.TrySetResult(); }
        await first;
        await Assert.ThrowsAsync<DbUpdateException>(() => Task.WhenAll(pending));
        Assert.All(pending, task => Assert.True(task.IsFaulted));
        Assert.Equal(16, store.PersistFailures);
        Assert.Equal(1, store.PersistedEvents);
        Assert.Single(await store.ListAsync(0, 100, null, null));

        await using (var connection = await factory.Database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_audit;";
            await command.ExecuteNonQueryAsync();
        }
        await RecordAsync(store, "recovered");
        var verification = await store.VerifyChainAsync();
        Assert.True(verification.Valid);
        Assert.Equal(2, verification.LastSeq);
        Assert.Equal(2, verification.Checked);
    }

    [Fact]
    public async Task Maintenance_WaitsForAcceptedAudit_AndRejectsNewAdmissions()
    {
        using var env = new TempWebHostEnvironment();
        var factory = new PausedFactory(new SqliteMetadataDatabase(env));
        var maintenance = new StorageMaintenanceCoordinator();
        await using var store = new AuditEventStore(factory, maintenance: maintenance);
        var pending = RecordAsync(store, "before-maintenance");
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var exclusive = maintenance.BeginExclusiveAsync("test", CancellationToken.None);
        try
        {
            Assert.False(exclusive.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(store, "during-maintenance"));
        }
        finally { factory.Release.TrySetResult(); }
        await pending;
        await using (await exclusive)
        {
            Assert.Equal(1, store.PersistedEvents);
            Assert.Equal(0, store.PendingCount);
            maintenance.MarkRestartPending("restore-completed");
            await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(store, "exclusive-restart-pending"));
        }
        Assert.Null(maintenance.TryEnterRequest());
        await RecordAsync(store, "after-maintenance");
        Assert.True((await store.VerifyChainAsync()).Valid);
    }

    [Fact]
    public async Task ShutdownBudgetFailure_IsObservable_AndAcceptedCallsAreNotSilentlyLost()
    {
        using var env = new TempWebHostEnvironment();
        var factory = new PausedFactory(new SqliteMetadataDatabase(env));
        await using var store = new AuditEventStore(factory, new AuditQueueOptions { DrainTimeoutMs = 50 });
        var pending = RecordAsync(store, "blocked");
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => store.StopAsync(CancellationToken.None));
        }
        finally { factory.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await store.StopAsync(CancellationToken.None);
        Assert.Equal(1, store.PersistFailures);
        Assert.Equal(1, store.ShutdownFailures);
        Assert.NotNull(store.LastPersistFailure);
        Assert.Equal(0, store.PendingCount);
    }

    private static DefaultHttpContext Context(string action)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/test/" + action;
        return context;
    }

    private static Task RecordAsync(AuditEventStore store, string action, CancellationToken ct = default) =>
        store.RecordAsync(Context(action), new AuditActionMetadata(action, "test"), 200, ct);

    private sealed class PausedFactory(SqliteMetadataDatabase database) : IVisionStudioDbFactory
    {
        private readonly VisionStudioDbFactory _inner = new(database);
        private int _transactions;
        public SqliteMetadataDatabase Database => database;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Transactions => Volatile.Read(ref _transactions);
        public Task<VisionStudioDbContext> CreateDbContextAsync(CancellationToken ct = default, bool strongDurability = false) =>
            _inner.CreateDbContextAsync(ct, strongDurability);
        public VisionStudioDbContext CreateDbContext() => _inner.CreateDbContext();
        public async Task<ImmediateDbTransaction> BeginImmediateTransactionAsync(VisionStudioDbContext db, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _transactions) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await _inner.BeginImmediateTransactionAsync(db, ct);
        }
    }
}
