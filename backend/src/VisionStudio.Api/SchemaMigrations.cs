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
public sealed class SqliteSchemaMigrationRunner
{
    public const int CurrentVersion = 20;

    private static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, "V0.20 base metadata schema", BaseSchemaSql),
        new(2, "V0.24 local security and audit schema", SecuritySchemaSql),
        new(3, "V0.25 runtime dependency provenance", DependencySchemaSql, ApplyDependencyColumnsAsync,
            "runtime_dependency_manifests + jobs.published_dependency_manifest_hash + job_publication_history.dependency_manifest_hash + run_traces.dependency_manifest_hash + ix_run_traces_dependency_manifest"),
        new(4, "V0.28 formal migration runner adoption", MigrationRunnerAdoptionSql),
        new(5, "V0.34 camera commissioning feature profiles", CameraFeatureProfileSchemaSql),
        new(6, "V0.35 multi-camera synchronization groups", CameraSynchronizationSchemaSql),
        new(7, "V0.37 camera synchronization run history", CameraSynchronizationRunHistorySchemaSql),
        new(8, "V0.39 camera synchronization commissioning tests", CameraSynchronizationCommissioningTestSchemaSql),
        new(9, "V0.42 camera synchronization PTP timing evidence", "", ApplyCameraSynchronizationPtpEvidenceAsync,
            "camera_sync_runs.ptp_json TEXT NOT NULL DEFAULT '[]'"),
        new(10, "V0.45 camera vendor transport telemetry evidence", "", ApplyCameraTransportTelemetryEvidenceAsync,
            "camera_sync_runs.transport_json TEXT NOT NULL DEFAULT '[]'"),
        new(11, "V0.47 offline replay artifacts", "", ApplyOfflineReplayArtifactsAsync,
            "run_traces replay artifact metadata columns"),
        new(12, "V0.48 dataset and batch validation", DatasetValidationSchemaSql),
        new(13, "V0.50 product recipe lifecycle", ProductRecipeSchemaSql, ApplyProductRecipeColumnsAsync,
            "products + job_version_validations + jobs.product_id + jobs.recipe_code"),
        new(14, "V0.51 recipe parameter binding", RecipeParameterSchemaSql, ApplyRecipeParameterColumnsAsync,
            "product_parameter_values + recipe_parameter_values + job_versions base/effective workflow parameter snapshots"),
        new(15, "V0.52 reusable workflow modules", WorkflowModuleSchemaSql),
        new(16, "V0.58 plugin performance benchmark history", PluginBenchmarkSchemaSql),
        new(17, "V0.60 run node observability history", RunObservabilitySchemaSql),
        new(18, "V0.62 investigation cases and signature evidence", InvestigationCaseSchemaSql),
        new(19, "V0.63 case verification gate and dataset regression evidence", InvestigationVerificationSchemaSql),
        new(20, "V0.64 run trace lifecycle and structured error codes", "", ApplyRunTraceLifecycleColumnsAsync,
            "run_traces.error_code")
    ];

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

    private const string BaseSchemaSql = """
CREATE TABLE IF NOT EXISTS jobs (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL,
    published_version INTEGER NULL
);
CREATE TABLE IF NOT EXISTS job_versions (
    job_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    workflow_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT NOT NULL,
    workflow_json TEXT NOT NULL,
    PRIMARY KEY(job_id, version),
    FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_job_versions_created ON job_versions(job_id, created_at DESC);
CREATE TABLE IF NOT EXISTS job_publication_history (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    action TEXT NOT NULL,
    at TEXT NOT NULL,
    FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_job_publication_history_job ON job_publication_history(job_id, seq);

CREATE TABLE IF NOT EXISTS calibration_assets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL,
    published_version INTEGER NULL
);
CREATE TABLE IF NOT EXISTS calibration_versions (
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    snapshot_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT NOT NULL,
    workspace_json TEXT NOT NULL,
    result_json TEXT NOT NULL,
    PRIMARY KEY(asset_id, version),
    FOREIGN KEY(asset_id) REFERENCES calibration_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_calibration_versions_created ON calibration_versions(asset_id, created_at DESC);
CREATE TABLE IF NOT EXISTS calibration_publication_history (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    action TEXT NOT NULL,
    at TEXT NOT NULL,
    FOREIGN KEY(asset_id) REFERENCES calibration_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_calibration_publication_history_asset ON calibration_publication_history(asset_id, seq);

CREATE TABLE IF NOT EXISTS run_traces (
    run_id TEXT PRIMARY KEY,
    started_at TEXT NOT NULL,
    source TEXT NOT NULL,
    job_id TEXT NULL,
    job_version INTEGER NULL,
    workflow_id TEXT NOT NULL,
    workflow_name TEXT NOT NULL,
    workflow_hash TEXT NULL,
    execution_status TEXT NOT NULL,
    disposition TEXT NOT NULL,
    total_duration_ms REAL NOT NULL,
    node_count INTEGER NOT NULL,
    overlay_count INTEGER NOT NULL,
    has_preview INTEGER NOT NULL,
    preview_relative_path TEXT NULL,
    preview_bytes INTEGER NOT NULL DEFAULT 0,
    error TEXT NULL,
    note TEXT NULL,
    node_reports_json TEXT NOT NULL,
    workflow_json TEXT NULL,
    overlays_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_run_traces_started ON run_traces(started_at DESC);
CREATE INDEX IF NOT EXISTS ix_run_traces_job_started ON run_traces(job_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_run_traces_disposition_started ON run_traces(disposition, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_run_traces_preview_started ON run_traces(has_preview, started_at);

CREATE TABLE IF NOT EXISTS migration_state (
    key TEXT PRIMARY KEY,
    completed_at TEXT NOT NULL,
    detail TEXT NULL
);
""";

    private const string SecuritySchemaSql = """
CREATE TABLE IF NOT EXISTS security_users (
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL,
    normalized_username TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    role TEXT NOT NULL CHECK(role IN ('Operator','Engineer','Administrator')),
    enabled INTEGER NOT NULL,
    password_salt BLOB NOT NULL,
    password_hash BLOB NOT NULL,
    password_iterations INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_login_at TEXT NULL,
    password_changed_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_security_users_role ON security_users(role, enabled);

CREATE TABLE IF NOT EXISTS security_sessions (
    token_hash TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    revoked_at TEXT NULL,
    FOREIGN KEY(user_id) REFERENCES security_users(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_security_sessions_user ON security_sessions(user_id, expires_at DESC);
CREATE INDEX IF NOT EXISTS ix_security_sessions_expiry ON security_sessions(expires_at, revoked_at);

CREATE TABLE IF NOT EXISTS audit_events (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    at TEXT NOT NULL,
    user_id TEXT NULL,
    username TEXT NULL,
    role TEXT NULL,
    action TEXT NOT NULL,
    resource TEXT NOT NULL,
    method TEXT NOT NULL,
    path TEXT NOT NULL,
    status_code INTEGER NOT NULL,
    success INTEGER NOT NULL,
    target TEXT NULL,
    correlation_id TEXT NULL,
    client_ip TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_events_at ON audit_events(at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_events_user_at ON audit_events(username, at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_events_action_at ON audit_events(action, at DESC);
""";

    private const string DependencySchemaSql = """
CREATE TABLE IF NOT EXISTS runtime_dependency_manifests (
    manifest_hash TEXT PRIMARY KEY,
    schema_version INTEGER NOT NULL,
    captured_at TEXT NOT NULL,
    manifest_json TEXT NOT NULL
);
""";

    private const string MigrationRunnerAdoptionSql = """
CREATE INDEX IF NOT EXISTS ix_schema_migration_history_applied ON schema_migration_history(applied_at DESC);
""";

    private const string CameraFeatureProfileSchemaSql = """
CREATE TABLE IF NOT EXISTS camera_feature_profiles (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    driver TEXT NOT NULL,
    source_camera_id TEXT NULL,
    profile_hash TEXT NOT NULL,
    profile_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_camera_feature_profiles_driver_name ON camera_feature_profiles(driver, name);
CREATE INDEX IF NOT EXISTS ix_camera_feature_profiles_hash ON camera_feature_profiles(profile_hash);
""";

    private const string CameraSynchronizationSchemaSql = """
CREATE TABLE IF NOT EXISTS camera_sync_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    driver TEXT NOT NULL,
    camera_ids_json TEXT NOT NULL,
    device_key INTEGER NOT NULL,
    group_key INTEGER NOT NULL,
    group_mask INTEGER NOT NULL,
    broadcast_address TEXT NOT NULL,
    require_ptp_locked INTEGER NOT NULL,
    max_ptp_offset_ns INTEGER NOT NULL,
    max_trigger_skew_us REAL NOT NULL,
    scheduled_lead_time_ms INTEGER NOT NULL,
    config_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_camera_sync_groups_driver ON camera_sync_groups(driver, name);
CREATE INDEX IF NOT EXISTS ix_camera_sync_groups_hash ON camera_sync_groups(config_hash);
""";


    private const string CameraSynchronizationRunHistorySchemaSql = """
CREATE TABLE IF NOT EXISTS camera_sync_runs (
    run_id TEXT PRIMARY KEY,
    group_id TEXT NOT NULL,
    group_config_hash TEXT NOT NULL,
    source TEXT NOT NULL,
    scheduled INTEGER NOT NULL,
    requested_at TEXT NOT NULL,
    completed_at TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    scheduled_device_time_ns INTEGER NULL,
    timestamp_basis TEXT NOT NULL,
    trigger_skew_us REAL NULL,
    max_allowed_skew_us REAL NOT NULL,
    within_tolerance INTEGER NULL,
    outcome TEXT NOT NULL,
    error TEXT NULL,
    command_json TEXT NULL,
    frames_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_camera_sync_runs_group_completed ON camera_sync_runs(group_id, completed_at DESC);
CREATE INDEX IF NOT EXISTS ix_camera_sync_runs_outcome_completed ON camera_sync_runs(outcome, completed_at DESC);
""";

    private const string DatasetValidationSchemaSql = """
CREATE TABLE IF NOT EXISTS validation_datasets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    topology_hash TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_validation_datasets_updated ON validation_datasets(updated_at DESC);

CREATE TABLE IF NOT EXISTS validation_dataset_items (
    dataset_id TEXT NOT NULL,
    item_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK(source_kind IN ('TRACE','MEDIA')),
    source_ref TEXT NOT NULL,
    expected_disposition TEXT NOT NULL CHECK(expected_disposition IN ('OK','NG')),
    note TEXT NULL,
    sort_order INTEGER NOT NULL,
    added_at TEXT NOT NULL,
    PRIMARY KEY(dataset_id,item_id),
    UNIQUE(dataset_id,source_kind,source_ref),
    FOREIGN KEY(dataset_id) REFERENCES validation_datasets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_validation_dataset_items_source ON validation_dataset_items(source_kind,source_ref);

CREATE TABLE IF NOT EXISTS validation_runs (
    run_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL,
    status TEXT NOT NULL,
    source_workflow_run_id TEXT NOT NULL,
    workflow_hash TEXT NOT NULL,
    candidate_workflow_json TEXT NOT NULL,
    requested_count INTEGER NOT NULL,
    completed_count INTEGER NOT NULL,
    started_at TEXT NOT NULL,
    completed_at TEXT NULL,
    cancel_requested INTEGER NOT NULL DEFAULT 0,
    error TEXT NULL,
    summary_json TEXT NULL,
    FOREIGN KEY(dataset_id) REFERENCES validation_datasets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_validation_runs_dataset_started ON validation_runs(dataset_id,started_at DESC);
CREATE INDEX IF NOT EXISTS ix_validation_runs_status_started ON validation_runs(status,started_at DESC);

CREATE TABLE IF NOT EXISTS validation_results (
    run_id TEXT NOT NULL,
    item_id TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    source_ref TEXT NOT NULL,
    expected_disposition TEXT NOT NULL,
    actual_disposition TEXT NOT NULL,
    classification TEXT NOT NULL,
    replay_run_id TEXT NULL,
    success INTEGER NOT NULL,
    duration_ms REAL NOT NULL,
    failed_nodes_json TEXT NOT NULL,
    error TEXT NULL,
    completed_at TEXT NOT NULL,
    PRIMARY KEY(run_id,item_id),
    FOREIGN KEY(run_id) REFERENCES validation_runs(run_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_validation_results_classification ON validation_results(run_id,classification);
CREATE INDEX IF NOT EXISTS ix_validation_results_source ON validation_results(source_kind,source_ref);
""";

    private const string ProductRecipeSchemaSql = """
CREATE TABLE IF NOT EXISTS products (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_products_updated ON products(updated_at DESC);

CREATE TABLE IF NOT EXISTS job_version_validations (
    job_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    validation_run_id TEXT NOT NULL,
    dataset_id TEXT NOT NULL,
    workflow_hash TEXT NOT NULL,
    accepted INTEGER NOT NULL,
    policy_json TEXT NOT NULL,
    summary_json TEXT NOT NULL,
    reason TEXT NOT NULL,
    linked_at TEXT NOT NULL,
    PRIMARY KEY(job_id,version),
    FOREIGN KEY(job_id,version) REFERENCES job_versions(job_id,version) ON DELETE CASCADE,
    FOREIGN KEY(validation_run_id) REFERENCES validation_runs(run_id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_job_version_validations_run ON job_version_validations(validation_run_id);
CREATE INDEX IF NOT EXISTS ix_job_version_validations_accepted ON job_version_validations(job_id,accepted,linked_at DESC);
""";


    private const string RecipeParameterSchemaSql = """
CREATE TABLE IF NOT EXISTS product_parameter_values (
    product_id TEXT NOT NULL,
    key TEXT NOT NULL,
    value_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(product_id,key),
    FOREIGN KEY(product_id) REFERENCES products(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recipe_parameter_values (
    job_id TEXT NOT NULL,
    key TEXT NOT NULL,
    value_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(job_id,key),
    FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_product_parameter_values_product ON product_parameter_values(product_id,key);
CREATE INDEX IF NOT EXISTS ix_recipe_parameter_values_job ON recipe_parameter_values(job_id,key);
""";


    private const string WorkflowModuleSchemaSql = """
CREATE TABLE IF NOT EXISTS workflow_modules (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_workflow_modules_updated ON workflow_modules(updated_at DESC);

CREATE TABLE IF NOT EXISTS workflow_module_versions (
    module_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    module_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT NOT NULL,
    workflow_json TEXT NOT NULL,
    inputs_json TEXT NOT NULL,
    outputs_json TEXT NOT NULL,
    parameters_json TEXT NOT NULL,
    PRIMARY KEY(module_id,version),
    FOREIGN KEY(module_id) REFERENCES workflow_modules(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_workflow_module_versions_created ON workflow_module_versions(module_id,created_at DESC);
CREATE INDEX IF NOT EXISTS ix_workflow_module_versions_hash ON workflow_module_versions(module_hash);
""";

    private const string CameraSynchronizationCommissioningTestSchemaSql = """
CREATE TABLE IF NOT EXISTS camera_sync_commissioning_tests (
    test_id TEXT PRIMARY KEY,
    group_id TEXT NOT NULL,
    group_config_hash TEXT NOT NULL,
    scheduled INTEGER NOT NULL,
    requested_iterations INTEGER NOT NULL,
    completed_iterations INTEGER NOT NULL,
    frame_timeout_ms INTEGER NOT NULL,
    delay_ms INTEGER NOT NULL,
    minimum_samples INTEGER NOT NULL,
    status TEXT NOT NULL,
    started_at TEXT NOT NULL,
    completed_at TEXT NULL,
    error TEXT NULL,
    report_json TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_camera_sync_commissioning_group_started ON camera_sync_commissioning_tests(group_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_camera_sync_commissioning_status_started ON camera_sync_commissioning_tests(status, started_at DESC);
""";


    private const string PluginBenchmarkSchemaSql = """
CREATE TABLE IF NOT EXISTS plugin_benchmark_runs (
    run_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL,
    validation_run_id TEXT NOT NULL,
    plugin_id TEXT NOT NULL,
    plugin_version TEXT NOT NULL,
    plugin_assembly_sha256 TEXT NULL,
    workflow_hash TEXT NOT NULL,
    status TEXT NOT NULL,
    pool_sizes_json TEXT NOT NULL,
    requested_count INTEGER NOT NULL,
    warmup_count INTEGER NOT NULL,
    workload_concurrency INTEGER NOT NULL,
    baseline_run_id TEXT NULL,
    policy_json TEXT NOT NULL,
    started_at TEXT NOT NULL,
    completed_at TEXT NULL,
    cancel_requested INTEGER NOT NULL DEFAULT 0,
    error TEXT NULL,
    recommendation_json TEXT NULL,
    regression_json TEXT NULL,
    FOREIGN KEY(dataset_id) REFERENCES validation_datasets(id) ON DELETE RESTRICT,
    FOREIGN KEY(validation_run_id) REFERENCES validation_runs(run_id) ON DELETE RESTRICT,
    FOREIGN KEY(baseline_run_id) REFERENCES plugin_benchmark_runs(run_id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS ix_plugin_benchmark_plugin_started ON plugin_benchmark_runs(plugin_id,started_at DESC);
CREATE INDEX IF NOT EXISTS ix_plugin_benchmark_dataset_started ON plugin_benchmark_runs(dataset_id,started_at DESC);

CREATE TABLE IF NOT EXISTS plugin_benchmark_pool_results (
    run_id TEXT NOT NULL,
    pool_size INTEGER NOT NULL,
    item_count INTEGER NOT NULL,
    success_count INTEGER NOT NULL,
    failure_count INTEGER NOT NULL,
    failure_rate REAL NOT NULL,
    total_duration_ms REAL NOT NULL,
    throughput_per_second REAL NOT NULL,
    observed_working_set_bytes INTEGER NOT NULL,
    workflow_duration_json TEXT NOT NULL,
    worker_performance_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(run_id,pool_size),
    FOREIGN KEY(run_id) REFERENCES plugin_benchmark_runs(run_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_plugin_benchmark_pool_run ON plugin_benchmark_pool_results(run_id,pool_size);
""";

    private const string RunObservabilitySchemaSql = """
CREATE TABLE IF NOT EXISTS run_node_observations (
    run_id TEXT NOT NULL,
    execution_sequence INTEGER NOT NULL,
    node_id TEXT NOT NULL,
    node_type TEXT NOT NULL,
    phase TEXT NOT NULL,
    success INTEGER NOT NULL,
    start_offset_ms REAL NOT NULL,
    end_offset_ms REAL NOT NULL,
    duration_ms REAL NOT NULL,
    error TEXT NULL,
    PRIMARY KEY(run_id,execution_sequence),
    FOREIGN KEY(run_id) REFERENCES run_traces(run_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_run_node_observations_node ON run_node_observations(node_id,node_type);
CREATE INDEX IF NOT EXISTS ix_run_node_observations_run ON run_node_observations(run_id,execution_sequence);
""";

    private const string InvestigationCaseSchemaSql = """
CREATE TABLE IF NOT EXISTS investigation_cases (
    id TEXT PRIMARY KEY,
    case_key TEXT NOT NULL UNIQUE,
    signature_id TEXT NOT NULL,
    category TEXT NOT NULL,
    title TEXT NOT NULL,
    status TEXT NOT NULL,
    severity TEXT NOT NULL,
    workflow_id TEXT NOT NULL,
    workflow_hash TEXT NULL,
    source TEXT NOT NULL,
    job_id TEXT NULL,
    job_version INTEGER NULL,
    primary_node_id TEXT NULL,
    primary_node_type TEXT NULL,
    fingerprint TEXT NOT NULL,
    representative_run_id TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    occurrence_count INTEGER NOT NULL,
    reopen_count INTEGER NOT NULL DEFAULT 0,
    owner TEXT NULL,
    root_cause TEXT NULL,
    resolution_note TEXT NULL,
    verification_note TEXT NULL,
    verification_run_id TEXT NULL,
    resolved_at TEXT NULL,
    verified_at TEXT NULL,
    closed_at TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_investigation_cases_status ON investigation_cases(status,last_seen_at DESC);
CREATE INDEX IF NOT EXISTS ix_investigation_cases_signature ON investigation_cases(signature_id,last_seen_at DESC);
CREATE INDEX IF NOT EXISTS ix_investigation_cases_workflow ON investigation_cases(workflow_id,job_id,job_version,last_seen_at DESC);

CREATE TABLE IF NOT EXISTS investigation_case_runs (
    case_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    started_at TEXT NOT NULL,
    disposition TEXT NOT NULL,
    total_duration_ms REAL NOT NULL,
    node_duration_ms REAL NULL,
    linked_at TEXT NOT NULL,
    PRIMARY KEY(case_id,run_id),
    FOREIGN KEY(case_id) REFERENCES investigation_cases(id) ON DELETE CASCADE,
    FOREIGN KEY(run_id) REFERENCES run_traces(run_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_investigation_case_runs_started ON investigation_case_runs(started_at DESC,case_id);
CREATE INDEX IF NOT EXISTS ix_investigation_case_runs_run ON investigation_case_runs(run_id);
""";

    private const string InvestigationVerificationSchemaSql = """
CREATE TABLE IF NOT EXISTS investigation_case_verifications (
    id TEXT PRIMARY KEY,
    case_id TEXT NOT NULL,
    dataset_id TEXT NOT NULL,
    baseline_validation_run_id TEXT NOT NULL,
    candidate_validation_run_id TEXT NOT NULL,
    baseline_workflow_hash TEXT NOT NULL,
    candidate_workflow_hash TEXT NOT NULL,
    gate_status TEXT NOT NULL CHECK(gate_status IN ('Passed','Failed','Inconclusive')),
    baseline_signature_hits INTEGER NOT NULL,
    candidate_signature_hits INTEGER NOT NULL,
    baseline_inspectable_results INTEGER NOT NULL,
    candidate_inspectable_results INTEGER NOT NULL,
    baseline_summary_json TEXT NOT NULL,
    candidate_summary_json TEXT NOT NULL,
    reasons_json TEXT NOT NULL,
    warnings_json TEXT NOT NULL,
    note TEXT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY(case_id) REFERENCES investigation_cases(id) ON DELETE CASCADE,
    FOREIGN KEY(dataset_id) REFERENCES validation_datasets(id) ON DELETE RESTRICT,
    FOREIGN KEY(baseline_validation_run_id) REFERENCES validation_runs(run_id) ON DELETE RESTRICT,
    FOREIGN KEY(candidate_validation_run_id) REFERENCES validation_runs(run_id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_investigation_case_verifications_case ON investigation_case_verifications(case_id,created_at DESC);
CREATE INDEX IF NOT EXISTS ix_investigation_case_verifications_gate ON investigation_case_verifications(gate_status,created_at DESC);
CREATE INDEX IF NOT EXISTS ix_investigation_case_verifications_runs ON investigation_case_verifications(baseline_validation_run_id,candidate_validation_run_id);
""";


}
