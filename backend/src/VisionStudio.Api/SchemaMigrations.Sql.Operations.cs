using Microsoft.Data.Sqlite;

namespace VisionStudio.Api;

public sealed partial class SqliteSchemaMigrationRunner
{
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

    /// <summary>
    /// 结果落库表（output.database）。一条记录一行，payload 为 {type,value} JSON。
    /// payload 不建索引、不做全文检索：它可能很大，查询走 dataset/record_key/created_at。
    /// </summary>
    private const string ResultRecordSchemaSql = """
CREATE TABLE IF NOT EXISTS result_records (
    id TEXT PRIMARY KEY,
    dataset TEXT NOT NULL,
    record_key TEXT NULL,
    payload_json TEXT NOT NULL,
    run_id TEXT NOT NULL,
    workflow_scope TEXT NULL,
    node_id TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_result_records_dataset_created ON result_records(dataset, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_result_records_key ON result_records(record_key);
""";

    private static async Task ApplyResultRecordIdempotencyAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        await EnsureColumnAsync(connection, tx, "result_records", "idempotency_key", "TEXT NULL", ct);
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
CREATE UNIQUE INDEX IF NOT EXISTS ux_result_records_dataset_idempotency_key
ON result_records(dataset, idempotency_key);
""";
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string DurableCounterSchemaSql = """
CREATE TABLE IF NOT EXISTS durable_counter_events (
    seq INTEGER PRIMARY KEY,
    workflow_id TEXT NOT NULL,
    counter_id TEXT NOT NULL,
    event_id TEXT NOT NULL,
    count_value INTEGER NOT NULL,
    total INTEGER NOT NULL,
    pass_count INTEGER NOT NULL,
    fail_count INTEGER NOT NULL,
    UNIQUE(workflow_id, counter_id, event_id)
);
CREATE INDEX IF NOT EXISTS ix_durable_counter_events_latest
ON durable_counter_events(workflow_id, counter_id, seq DESC);
""";

    private const string CameraIntrinsicAssetSchemaSql = """
CREATE TABLE IF NOT EXISTS camera_intrinsic_assets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL,
    published_version INTEGER NULL
);
CREATE TABLE IF NOT EXISTS camera_intrinsic_versions (
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    snapshot_hash TEXT NOT NULL,
    model_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT NOT NULL,
    model_json TEXT NOT NULL,
    source_document_json TEXT NOT NULL,
    summary_json TEXT NOT NULL,
    PRIMARY KEY(asset_id, version),
    FOREIGN KEY(asset_id) REFERENCES camera_intrinsic_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_camera_intrinsic_versions_created ON camera_intrinsic_versions(asset_id, created_at DESC);
CREATE TABLE IF NOT EXISTS camera_intrinsic_publication_history (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    action TEXT NOT NULL,
    at TEXT NOT NULL,
    FOREIGN KEY(asset_id) REFERENCES camera_intrinsic_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_camera_intrinsic_publication_history_asset ON camera_intrinsic_publication_history(asset_id, seq);
""";

    private const string HandEyeCalibrationAssetSchemaSql = """
CREATE TABLE IF NOT EXISTS hand_eye_calibration_assets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL,
    published_version INTEGER NULL
);
CREATE TABLE IF NOT EXISTS hand_eye_calibration_versions (
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    snapshot_hash TEXT NOT NULL,
    model_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT NOT NULL,
    model_json TEXT NOT NULL,
    source_document_json TEXT NOT NULL,
    summary_json TEXT NOT NULL,
    PRIMARY KEY(asset_id, version),
    FOREIGN KEY(asset_id) REFERENCES hand_eye_calibration_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_hand_eye_calibration_versions_created ON hand_eye_calibration_versions(asset_id, created_at DESC);
CREATE TABLE IF NOT EXISTS hand_eye_calibration_publication_history (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    action TEXT NOT NULL,
    at TEXT NOT NULL,
    FOREIGN KEY(asset_id) REFERENCES hand_eye_calibration_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_hand_eye_calibration_publication_history_asset ON hand_eye_calibration_publication_history(asset_id, seq);
""";

    private const string HandEye3DCalibrationAssetSchemaSql = """
CREATE TABLE IF NOT EXISTS hand_eye3d_calibration_assets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL,
    published_version INTEGER NULL
);
CREATE TABLE IF NOT EXISTS hand_eye3d_calibration_versions (
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    snapshot_hash TEXT NOT NULL,
    model_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    note TEXT NOT NULL,
    model_json TEXT NOT NULL,
    source_document_json TEXT NOT NULL,
    summary_json TEXT NOT NULL,
    PRIMARY KEY(asset_id, version),
    FOREIGN KEY(asset_id) REFERENCES hand_eye3d_calibration_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_hand_eye3d_calibration_versions_created ON hand_eye3d_calibration_versions(asset_id, created_at DESC);
CREATE TABLE IF NOT EXISTS hand_eye3d_calibration_publication_history (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    action TEXT NOT NULL,
    at TEXT NOT NULL,
    FOREIGN KEY(asset_id) REFERENCES hand_eye3d_calibration_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_hand_eye3d_calibration_publication_history_asset ON hand_eye3d_calibration_publication_history(asset_id, seq);
""";

    /// <summary>
    /// v25：通用资产表（assets/asset_versions/asset_blobs），并把 v24 的 shape_model_* 数据原样搬迁。
    /// 搬迁是幂等的（INSERT OR IGNORE）；旧表保留一个版本用于回退，后续迁移再清。
    /// </summary>
    private const string GenericAssetSchemaSql = """
CREATE TABLE IF NOT EXISTS assets (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL,
    deleted_at TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_assets_kind_updated ON assets(kind, updated_at DESC);
CREATE TABLE IF NOT EXISTS asset_versions (
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    metadata_json TEXT NOT NULL,
    note TEXT NOT NULL,
    created_at TEXT NOT NULL,
    created_by TEXT NOT NULL,
    PRIMARY KEY(asset_id, version),
    FOREIGN KEY(asset_id) REFERENCES assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_asset_versions_created ON asset_versions(asset_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_asset_versions_sha ON asset_versions(sha256);
CREATE TABLE IF NOT EXISTS asset_blobs (
    sha256 TEXT PRIMARY KEY,
    size_bytes INTEGER NOT NULL,
    bytes BLOB NOT NULL,
    created_at TEXT NOT NULL
);

INSERT OR IGNORE INTO assets(id,kind,name,description,created_at,updated_at,latest_version,deleted_at)
SELECT id,'shape',name,description,created_at,updated_at,latest_version,deleted_at FROM shape_model_assets;
INSERT OR IGNORE INTO asset_versions(asset_id,version,sha256,size_bytes,metadata_json,note,created_at,created_by)
SELECT asset_id,version,sha256,size_bytes,
       json_object('trainingConfig',training_config,'sourceImageSha256',source_image_sha256),
       note,created_at,created_by
FROM shape_model_versions;
INSERT OR IGNORE INTO asset_blobs(sha256,size_bytes,bytes,created_at)
SELECT sha256,size_bytes,bytes,created_at FROM shape_model_blobs;
""";

    private const string ShapeModelAssetSchemaSql = """
CREATE TABLE IF NOT EXISTS shape_model_assets (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    latest_version INTEGER NOT NULL,
    deleted_at TEXT NULL
);
CREATE TABLE IF NOT EXISTS shape_model_versions (
    asset_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    training_config TEXT NOT NULL,
    source_image_sha256 TEXT NOT NULL,
    note TEXT NOT NULL,
    created_at TEXT NOT NULL,
    created_by TEXT NOT NULL,
    PRIMARY KEY(asset_id, version),
    FOREIGN KEY(asset_id) REFERENCES shape_model_assets(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_shape_model_versions_created ON shape_model_versions(asset_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_shape_model_versions_sha ON shape_model_versions(sha256);
CREATE TABLE IF NOT EXISTS shape_model_blobs (
    sha256 TEXT PRIMARY KEY,
    size_bytes INTEGER NOT NULL,
    bytes BLOB NOT NULL,
    created_at TEXT NOT NULL
);
""";
}
