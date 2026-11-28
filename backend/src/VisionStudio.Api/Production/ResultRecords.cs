using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Abstractions;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

/// <summary>
/// SQLite 实现的结果落库（2026-10-26 · 缺口分析 P1-12）。
/// </summary>
public sealed class SqliteResultRecordStore(IVisionStudioDbFactory dbFactory) : IResultRecordStore
{
    public SqliteResultRecordStore(SqliteMetadataDatabase database) : this(new VisionStudioDbFactory(database)) { }

    public async Task AppendAsync(ResultRecord record, CancellationToken cancellationToken = default)
        => _ = await AppendCoreAsync(record, null, checkConflict: false, cancellationToken);

    public async Task<(string RecordId, int PayloadBytes, bool Reused)> AppendIdempotentAsync(
        ResultRecord record, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var (recordId, payloadBytes, reused, _) = await AppendCoreAsync(record, idempotencyKey, checkConflict: false, cancellationToken);
        return (recordId, payloadBytes, reused);
    }

    public Task<(string RecordId, int PayloadBytes, bool Reused, bool Conflict)> AppendCheckedIdempotentAsync(
        ResultRecord record, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(idempotencyKey);
        return AppendCoreAsync(record, idempotencyKey, checkConflict: true, cancellationToken);
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new ArgumentException("Idempotency key must be non-empty and at most 256 characters.", nameof(idempotencyKey));
    }

    private async Task<(string RecordId, int PayloadBytes, bool Reused, bool Conflict)> AppendCoreAsync(
        ResultRecord record, string? idempotencyKey, bool checkConflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Id)) throw new ArgumentException("Record id must be non-empty.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Dataset)) throw new ArgumentException("Dataset must be non-empty.", nameof(record));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Keep the conflict check and read-back in one write transaction.
        await using var transaction = idempotencyKey is null ? null : await dbFactory.BeginImmediateTransactionAsync(db, cancellationToken);
        if (idempotencyKey is not null)
        {
            var existing = await db.ResultRecords
                .Where(x => x.Dataset == record.Dataset && x.IdempotencyKey == idempotencyKey)
                .Select(x => new { x.Id, x.RecordKey, x.PayloadJson })
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                var bytes = Encoding.UTF8.GetByteCount(existing.PayloadJson);
                var conflict = checkConflict && (!string.Equals(existing.RecordKey, record.RecordKey, StringComparison.Ordinal) ||
                    !string.Equals(existing.PayloadJson, record.PayloadJson, StringComparison.Ordinal));
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return (existing.Id, bytes, true, conflict);
            }
        }

        db.ResultRecords.Add(new ResultRecordEntity
        {
            Id = record.Id,
            Dataset = record.Dataset,
            RecordKey = record.RecordKey,
            PayloadJson = record.PayloadJson,
            RunId = record.RunId,
            WorkflowScope = record.WorkflowScope,
            NodeId = record.NodeId,
            CreatedAt = record.CreatedAt,
            IdempotencyKey = idempotencyKey,
            PayloadSha256 = record.PayloadSha256,
            RecordKeyNormalizationVersion = record.RecordKeyNormalizationVersion
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx)
        {
            ExceptionDispatchInfo.Capture(sqliteEx).Throw();
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return (record.Id, record.PayloadBytes, false, false);
    }

    /// <summary>按数据集读取最近的记录（新的在前）。报表与对账使用，节点不调用。</summary>
    public async Task<IReadOnlyList<ResultRecord>> ReadAsync(string dataset, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataset)) throw new ArgumentException("Dataset must be non-empty.", nameof(dataset));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be within 1..10000.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ResultRecords
            .Where(x => x.Dataset == dataset)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit)
            .Select(x => new ResultRecord(x.Id, x.Dataset, x.RecordKey, x.PayloadJson, x.RunId, x.WorkflowScope, x.NodeId, x.CreatedAt,
                x.PayloadSha256, x.RecordKeyNormalizationVersion))
            .ToListAsync(cancellationToken);
    }

    /// <summary>按业务键读取记录（新的在前），用于按件追溯。</summary>
    public async Task<IReadOnlyList<ResultRecord>> ReadByKeyAsync(string recordKey, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Record key must be non-empty.", nameof(recordKey));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be within 1..10000.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ResultRecords
            .Where(x => x.RecordKey == recordKey)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit)
            .Select(x => new ResultRecord(x.Id, x.Dataset, x.RecordKey, x.PayloadJson, x.RunId, x.WorkflowScope, x.NodeId, x.CreatedAt,
                x.PayloadSha256, x.RecordKeyNormalizationVersion))
            .ToListAsync(cancellationToken);
    }
}
