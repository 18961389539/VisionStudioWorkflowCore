using Microsoft.Data.Sqlite;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

/// <summary>
/// SQLite 实现的结果落库（2026-10-26 · 缺口分析 P1-12）。
///
/// 表结构见 <see cref="SqliteSchemaMigrationRunner"/> 的 migration 23（<c>result_records</c>）：
/// 一条记录一行，payload 存 <c>{type,value}</c> JSON，索引只建在 dataset/created_at 与 record_key 上，
/// 不建在 payload 上——payload 可能很大，报表按数据集与业务键查询即可。
///
/// 普通写入使用单条 INSERT。幂等写入在同一事务内尝试插入，键已存在时读取首条记录，按需比较记录内容。
/// </summary>
public sealed class SqliteResultRecordStore(SqliteMetadataDatabase database) : IResultRecordStore
{
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

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        // Keep the conflict check and read-back in one write transaction.
        await using var transaction = idempotencyKey is null ? null : connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO result_records (id, dataset, record_key, payload_json, run_id, workflow_scope, node_id, created_at, idempotency_key)
VALUES ($id, $dataset, $key, $payload, $run, $scope, $node, $created, $idempotencyKey)
ON CONFLICT(dataset, idempotency_key) DO NOTHING;
""";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$dataset", record.Dataset);
        command.Parameters.AddWithValue("$key", (object?)record.RecordKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", record.PayloadJson);
        command.Parameters.AddWithValue("$run", record.RunId);
        command.Parameters.AddWithValue("$scope", (object?)record.WorkflowScope ?? DBNull.Value);
        command.Parameters.AddWithValue("$node", record.NodeId);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$idempotencyKey", (object?)idempotencyKey ?? DBNull.Value);
        var result = (RecordId: record.Id, record.PayloadBytes, Reused: false, Conflict: false);
        var rowsInserted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (idempotencyKey is not null && rowsInserted == 0)
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            var conflictColumn = checkConflict ? ", NOT (record_key COLLATE BINARY IS $key AND payload_json COLLATE BINARY = $payload)" : string.Empty;
            lookup.CommandText = $"""
SELECT id, length(CAST(payload_json AS BLOB)){conflictColumn} FROM result_records
WHERE dataset = $dataset AND idempotency_key = $idempotencyKey;
""";
            lookup.Parameters.AddWithValue("$dataset", record.Dataset);
            lookup.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
            if (checkConflict)
            {
                lookup.Parameters.AddWithValue("$key", (object?)record.RecordKey ?? DBNull.Value);
                lookup.Parameters.AddWithValue("$payload", record.PayloadJson);
            }
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The existing result record could not be found after an idempotency conflict.");
            result = (reader.GetString(0), reader.GetInt32(1), true, checkConflict && reader.GetBoolean(2));
        }
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>按数据集读取最近的记录（新的在前）。报表与对账使用，节点不调用。</summary>
    public async Task<IReadOnlyList<ResultRecord>> ReadAsync(string dataset, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataset)) throw new ArgumentException("Dataset must be non-empty.", nameof(dataset));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be within 1..10000.");

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, dataset, record_key, payload_json, run_id, workflow_scope, node_id, created_at
FROM result_records
WHERE dataset = $dataset
ORDER BY created_at DESC, id DESC
LIMIT $limit;
""";
        command.Parameters.AddWithValue("$dataset", dataset);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<ResultRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ResultRecord(
                Id: reader.GetString(0),
                Dataset: reader.GetString(1),
                RecordKey: reader.IsDBNull(2) ? null : reader.GetString(2),
                PayloadJson: reader.GetString(3),
                RunId: reader.GetString(4),
                WorkflowScope: reader.IsDBNull(5) ? null : reader.GetString(5),
                NodeId: reader.GetString(6),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return records;
    }

    /// <summary>按业务键读取记录（新的在前），用于按件追溯。</summary>
    public async Task<IReadOnlyList<ResultRecord>> ReadByKeyAsync(string recordKey, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recordKey)) throw new ArgumentException("Record key must be non-empty.", nameof(recordKey));
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be within 1..10000.");

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, dataset, record_key, payload_json, run_id, workflow_scope, node_id, created_at
FROM result_records
WHERE record_key = $key
ORDER BY created_at DESC, id DESC
LIMIT $limit;
""";
        command.Parameters.AddWithValue("$key", recordKey);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<ResultRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ResultRecord(
                Id: reader.GetString(0),
                Dataset: reader.GetString(1),
                RecordKey: reader.IsDBNull(2) ? null : reader.GetString(2),
                PayloadJson: reader.GetString(3),
                RunId: reader.GetString(4),
                WorkflowScope: reader.IsDBNull(5) ? null : reader.GetString(5),
                NodeId: reader.GetString(6),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return records;
    }
}
