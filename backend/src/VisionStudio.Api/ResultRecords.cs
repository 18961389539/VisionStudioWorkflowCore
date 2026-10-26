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
/// 单条写入走 SQLite 的隐式事务（单条 INSERT），失败即整条不落库；不提供"部分写入"语义。
/// </summary>
public sealed class SqliteResultRecordStore(SqliteMetadataDatabase database) : IResultRecordStore
{
    public async Task AppendAsync(ResultRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Id)) throw new ArgumentException("Record id must be non-empty.", nameof(record));
        if (string.IsNullOrWhiteSpace(record.Dataset)) throw new ArgumentException("Dataset must be non-empty.", nameof(record));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO result_records (id, dataset, record_key, payload_json, run_id, workflow_scope, node_id, created_at)
VALUES ($id, $dataset, $key, $payload, $run, $scope, $node, $created);
""";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$dataset", record.Dataset);
        command.Parameters.AddWithValue("$key", (object?)record.RecordKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", record.PayloadJson);
        command.Parameters.AddWithValue("$run", record.RunId);
        command.Parameters.AddWithValue("$scope", (object?)record.WorkflowScope ?? DBNull.Value);
        command.Parameters.AddWithValue("$node", record.NodeId);
        command.Parameters.AddWithValue("$created", record.CreatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
