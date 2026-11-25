using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed class SqliteDurableRunCounterStore(IVisionStudioDbFactory dbFactory) : IDurableRunCounterStore
{
    public SqliteDurableRunCounterStore(SqliteMetadataDatabase database) : this(new VisionStudioDbFactory(database)) { }

    public async Task<(RunCounterReading Reading, bool Reused)> ApplyAsync(
        DurableCounterEvent input, bool preview, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken, strongDurability: !preview);
        // A write transaction serializes duplicate checks and increments across callers and processes.
        await using var transaction = preview
            ? null
            : await dbFactory.BeginImmediateTransactionAsync(db, cancellationToken);

        var existing = await db.DurableCounterEvents
            .Where(x => x.WorkflowId == input.WorkflowId && x.CounterId == input.CounterId && x.EventId == input.EventId)
            .Select(x => new RunCounterReading(x.CountValue, x.Total, x.PassCount, x.FailCount))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { } reused && await db.DurableCounterEvents.AnyAsync(x => x.WorkflowId == input.WorkflowId && x.CounterId == input.CounterId && x.EventId == input.EventId, cancellationToken))
            return (reused, true);

        var hasPrev = await db.DurableCounterEvents
            .AnyAsync(x => x.WorkflowId == input.WorkflowId && x.CounterId == input.CounterId, cancellationToken);
        RunCounterReading? previous = hasPrev
            ? await db.DurableCounterEvents
                .Where(x => x.WorkflowId == input.WorkflowId && x.CounterId == input.CounterId)
                .OrderByDescending(x => x.Seq)
                .Select(x => new RunCounterReading(x.CountValue, x.Total, x.PassCount, x.FailCount))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var next = (previous ?? default).Apply(input.Step, input.Pass, input.Reset);
        if (preview) return (next, false);

        db.DurableCounterEvents.Add(new DurableCounterEventEntity
        {
            WorkflowId = input.WorkflowId,
            CounterId = input.CounterId,
            EventId = input.EventId,
            CountValue = next.Count,
            Total = next.Total,
            PassCount = next.Pass,
            FailCount = next.Fail
        });
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return (next, false);
    }
}
