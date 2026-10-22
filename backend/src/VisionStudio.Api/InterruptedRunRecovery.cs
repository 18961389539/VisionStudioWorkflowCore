using Microsoft.Data.Sqlite;

namespace VisionStudio.Api;

/// <summary>
/// Asynchronous runs (dataset validation, camera-sync commissioning, plugin benchmark) are executed by
/// in-process workers and only their rows outlive the process. A host that stops unexpectedly therefore
/// leaves those rows at 'Running'/'Queued' forever: the UI keeps polling a run that cannot progress, and
/// operations guarded by "no running run" (for example deleting the dataset) stay blocked until someone
/// edits the database by hand.
///
/// This host is a single-process deployment (the same assumption <see cref="WorkflowStore"/> documents),
/// so every unfinished row that exists before this process starts has no owner. Marking them once at
/// startup is deterministic — no time-based guessing — and idempotent.
/// </summary>
public sealed class InterruptedRunRecovery(
    SqliteMetadataDatabase database,
    ILogger<InterruptedRunRecovery> logger)
{
    public const string Status = "Interrupted";

    internal const string Reason =
        "Host restarted before this run finished; the run no longer has an owner and its results are partial.";

    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var completedAt = DateTimeOffset.UtcNow.ToUniversalTime().ToString("O");
        var recovered = 0;

        recovered += await MarkAsync(connection, "validation_runs", "status='Running'", completedAt, cancellationToken);
        recovered += await MarkAsync(connection, "camera_sync_commissioning_tests",
            "status IN ('Queued','Running')", completedAt, cancellationToken);
        recovered += await MarkAsync(connection, "plugin_benchmark_runs", "status='Running'", completedAt, cancellationToken);

        if (recovered > 0)
        {
            logger.LogWarning(
                "Marked {Count} unfinished run(s) from a previous host as '{Status}'; their datasets and groups are usable again.",
                recovered, Status);
        }

        return recovered;
    }

    private static async Task<int> MarkAsync(
        SqliteConnection connection,
        string table,
        string predicate,
        string completedAt,
        CancellationToken cancellationToken)
    {
        // Table names and predicates are compile-time constants; no user input reaches the SQL text.
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {table}
               SET status=$status,
                   completed_at=COALESCE(completed_at,$completed),
                   error=COALESCE(error,$error)
             WHERE {predicate};
            """;
        command.Parameters.AddWithValue("$status", Status);
        command.Parameters.AddWithValue("$completed", completedAt);
        command.Parameters.AddWithValue("$error", Reason);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Runs recovery first (after storage migration, before anything can start a new run) so the API never
/// serves a 'Running' row that this process does not own.
/// </summary>
public sealed class InterruptedRunRecoveryHostedService(InterruptedRunRecovery recovery) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
        => await recovery.RecoverAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
