using Microsoft.Data.Sqlite;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed class SqliteDurableRunCounterStore(SqliteMetadataDatabase database) : IDurableRunCounterStore
{
    public async Task<(RunCounterReading Reading, bool Reused)> ApplyAsync(
        DurableCounterEvent input, bool preview, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken, strongDurability: !preview);
        // A write transaction serializes duplicate checks and increments across callers and processes.
        await using var transaction = connection.BeginTransaction(deferred: preview);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$workflow", input.WorkflowId);
        command.Parameters.AddWithValue("$counter", input.CounterId);
        command.Parameters.AddWithValue("$event", input.EventId);
        command.CommandText = """
SELECT count_value, total, pass_count, fail_count FROM durable_counter_events
WHERE workflow_id = $workflow AND counter_id = $counter AND event_id = $event;
""";
        var existing = await ReadAsync(command, cancellationToken);
        if (existing is { } reused) return (reused, true);

        command.CommandText = """
SELECT count_value, total, pass_count, fail_count FROM durable_counter_events
WHERE workflow_id = $workflow AND counter_id = $counter ORDER BY seq DESC LIMIT 1;
""";
        var previous = await ReadAsync(command, cancellationToken);
        var next = (previous ?? default).Apply(input.Step, input.Pass, input.Reset);
        if (preview) return (next, false);

        command.CommandText = """
INSERT INTO durable_counter_events (workflow_id, counter_id, event_id, count_value, total, pass_count, fail_count)
VALUES ($workflow, $counter, $event, $count, $total, $pass, $fail);
""";
        command.Parameters.AddWithValue("$count", next.Count);
        command.Parameters.AddWithValue("$total", next.Total);
        command.Parameters.AddWithValue("$pass", next.Pass);
        command.Parameters.AddWithValue("$fail", next.Fail);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (next, false);
    }

    private static async Task<RunCounterReading?> ReadAsync(SqliteCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new RunCounterReading(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3))
            : null;
    }
}
