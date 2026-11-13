using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VisionStudio.Api;

public sealed record SchemaMigrationStatus(
    int CurrentVersion,
    int TargetVersion,
    bool UpToDate,
    IReadOnlyList<SchemaMigrationHistoryEntry> History);

public sealed record SchemaMigrationHistoryEntry(
    int Version,
    string Name,
    string Checksum,
    DateTimeOffset AppliedAt,
    long DurationMs,
    bool Baselined);

/// <summary>
/// Explicit, ordered SQLite schema migration runner. V0.28 replaces ad-hoc startup
/// EnsureColumn calls with a deterministic migration chain and checksum history.
/// </summary>
public sealed partial class SqliteSchemaMigrationRunner
{
    public const int CurrentVersion = 29;

    public async Task<SchemaMigrationStatus> MigrateAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await EnsureInfrastructureAsync(connection, ct);
        var current = await ReadCurrentVersionAsync(connection, ct);
        if (current > CurrentVersion)
            throw new InvalidOperationException($"Database schema v{current} is newer than this host supports (v{CurrentVersion}).");

        // Existing V0.20-V0.27 installations predate migration_history. Baseline already-applied
        // versions without replaying DDL, then continue normally from the next version.
        if (current > 0)
            await BaselineHistoryAsync(connection, current, ct);

        foreach (var migration in Migrations.Where(x => x.Version > current).OrderBy(x => x.Version))
            await ApplyAsync(connection, migration, ct);

        await VerifyHistoryChecksumsAsync(connection, ct);
        var finalVersion = await ReadCurrentVersionAsync(connection, ct);
        return new SchemaMigrationStatus(finalVersion, CurrentVersion, finalVersion == CurrentVersion, await ReadHistoryAsync(connection, ct));
    }

    public async Task<SchemaMigrationStatus> GetStatusAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await EnsureInfrastructureAsync(connection, ct);
        var current = await ReadCurrentVersionAsync(connection, ct);
        return new SchemaMigrationStatus(current, CurrentVersion, current == CurrentVersion, await ReadHistoryAsync(connection, ct));
    }

    private static async Task EnsureInfrastructureAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
CREATE TABLE IF NOT EXISTS schema_info (
    id INTEGER PRIMARY KEY CHECK(id = 1),
    version INTEGER NOT NULL
);
INSERT OR IGNORE INTO schema_info(id, version) VALUES(1, 0);

CREATE TABLE IF NOT EXISTS schema_migration_history (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    checksum TEXT NOT NULL,
    applied_at TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    baselined INTEGER NOT NULL DEFAULT 0
);
""";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ReadCurrentVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info WHERE id=1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static async Task BaselineHistoryAsync(SqliteConnection connection, int current, CancellationToken ct)
    {
        foreach (var migration in Migrations.Where(x => x.Version <= current))
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT checksum FROM schema_migration_history WHERE version=$version;";
            check.Parameters.AddWithValue("$version", migration.Version);
            var existing = await check.ExecuteScalarAsync(ct) as string;
            if (existing is not null)
            {
                if (!string.Equals(existing, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Schema migration checksum drift detected for v{migration.Version} ({migration.Name}).");
                continue;
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
INSERT INTO schema_migration_history(version,name,checksum,applied_at,duration_ms,baselined)
VALUES($version,$name,$checksum,$at,0,1);
""";
            insert.Parameters.AddWithValue("$version", migration.Version);
            insert.Parameters.AddWithValue("$name", migration.Name);
            insert.Parameters.AddWithValue("$checksum", migration.Checksum);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ApplyAsync(SqliteConnection connection, Migration migration, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            if (!string.IsNullOrWhiteSpace(migration.Sql))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(ct);
            }

            if (migration.CustomApply is not null)
                await migration.CustomApply(connection, tx, ct);

            await using (var version = connection.CreateCommand())
            {
                version.Transaction = tx;
                version.CommandText = "UPDATE schema_info SET version=$version WHERE id=1;";
                version.Parameters.AddWithValue("$version", migration.Version);
                await version.ExecuteNonQueryAsync(ct);
            }

            sw.Stop();
            await using (var history = connection.CreateCommand())
            {
                history.Transaction = tx;
                history.CommandText = """
INSERT INTO schema_migration_history(version,name,checksum,applied_at,duration_ms,baselined)
VALUES($version,$name,$checksum,$at,$duration,0);
""";
                history.Parameters.AddWithValue("$version", migration.Version);
                history.Parameters.AddWithValue("$name", migration.Name);
                history.Parameters.AddWithValue("$checksum", migration.Checksum);
                history.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                history.Parameters.AddWithValue("$duration", sw.ElapsedMilliseconds);
                await history.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task VerifyHistoryChecksumsAsync(SqliteConnection connection, CancellationToken ct)
    {
        foreach (var migration in Migrations)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT checksum FROM schema_migration_history WHERE version=$version;";
            command.Parameters.AddWithValue("$version", migration.Version);
            var value = await command.ExecuteScalarAsync(ct) as string;
            if (value is null) continue;
            if (!string.Equals(value, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Schema migration checksum drift detected for v{migration.Version} ({migration.Name}).");
        }
    }

    private static async Task<IReadOnlyList<SchemaMigrationHistoryEntry>> ReadHistoryAsync(SqliteConnection connection, CancellationToken ct)
    {
        var result = new List<SchemaMigrationHistoryEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version,name,checksum,applied_at,duration_ms,baselined FROM schema_migration_history ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new SchemaMigrationHistoryEntry(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.GetInt64(4), reader.GetInt32(5) != 0));
        return result;
    }

    private static async Task ApplyDependencyColumnsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "run_traces", "dependency_manifest_hash", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "jobs", "published_dependency_manifest_hash", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "job_publication_history", "dependency_manifest_hash", "TEXT NULL", ct);
        await using var index = connection.CreateCommand();
        index.Transaction = tx;
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_run_traces_dependency_manifest ON run_traces(dependency_manifest_hash, started_at DESC);";
        await index.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyCameraSynchronizationPtpEvidenceAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "camera_sync_runs", "ptp_json", "TEXT NOT NULL DEFAULT '[]'", ct);
    }

    private static async Task ApplyCameraTransportTelemetryEvidenceAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "camera_sync_runs", "transport_json", "TEXT NOT NULL DEFAULT '[]'", ct);
    }

    private static async Task ApplyOfflineReplayArtifactsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "run_traces", "has_replay_input", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(connection, tx, "run_traces", "replay_relative_path", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "run_traces", "replay_bytes", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(connection, tx, "run_traces", "replay_source_node_id", "TEXT NULL", ct);
    }

    private static async Task ApplyRunTraceLifecycleColumnsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "run_traces", "error_code", "TEXT NULL", ct);
    }

    private static async Task ApplyProductRecipeColumnsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "jobs", "product_id", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "jobs", "recipe_code", "TEXT NULL", ct);
        await using var index = connection.CreateCommand();
        index.Transaction = tx;
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_product_recipe_code ON jobs(product_id,recipe_code) WHERE product_id IS NOT NULL AND recipe_code IS NOT NULL;";
        await index.ExecuteNonQueryAsync(ct);
    }


    private static async Task ApplyRecipeParameterColumnsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "job_versions", "base_workflow_hash", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "job_versions", "base_workflow_json", "TEXT NULL", ct);
        await EnsureColumnAsync(connection, tx, "job_versions", "parameter_bindings_json", "TEXT NOT NULL DEFAULT '{}'", ct);
        await EnsureColumnAsync(connection, tx, "job_versions", "parameter_snapshot_json", "TEXT NOT NULL DEFAULT '{}'", ct);

        await using var backfill = connection.CreateCommand();
        backfill.Transaction = tx;
        backfill.CommandText = """
UPDATE job_versions
SET base_workflow_hash = COALESCE(base_workflow_hash, workflow_hash),
    base_workflow_json = COALESCE(base_workflow_json, workflow_json),
    parameter_bindings_json = COALESCE(parameter_bindings_json, '{}'),
    parameter_snapshot_json = COALESCE(parameter_snapshot_json, '{}');
""";
        await backfill.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyIndependentRecipeValidationAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        // Replay-safe：恢复旧备份时会重放迁移，裸 ALTER 会在已有该列的库上抛 duplicate column
        // （其余列扩展迁移同样走 EnsureColumnAsync）。UPDATE 自身幂等，可重复执行。
        await EnsureColumnAsync(connection, tx, "jobs", "requires_validation", "INTEGER NOT NULL DEFAULT 0", ct);
        await using var backfill = connection.CreateCommand();
        backfill.Transaction = tx;
        backfill.CommandText = "UPDATE jobs SET requires_validation=1 WHERE product_id IS NOT NULL OR recipe_code IS NOT NULL;";
        await backfill.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyLoginLockoutColumnsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tx, "security_users", ct)) return;
        await EnsureColumnAsync(connection, tx, "security_users", "failed_attempts", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(connection, tx, "security_users", "locked_until", "TEXT NULL", ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, SqliteTransaction tx, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, SqliteTransaction tx, string table, string column, string declaration, CancellationToken ct)
    {
        await using var check = connection.CreateCommand();
        check.Transaction = tx;
        check.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await check.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();

        await using var alter = connection.CreateCommand();
        alter.Transaction = tx;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private sealed class Migration
    {
        public Migration(int version, string name, string sql, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? customApply = null, string? checksumSeed = null)
        {
            Version = version;
            Name = name;
            Sql = sql;
            CustomApply = customApply;
            var normalized = $"{version}\n{name}\n{sql.Trim()}\n{checksumSeed ?? string.Empty}";
            Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        }
        public int Version { get; }
        public string Name { get; }
        public string Sql { get; }
        public Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? CustomApply { get; }
        public string Checksum { get; }
    }

}
