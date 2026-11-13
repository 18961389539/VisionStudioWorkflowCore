using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VisionStudio.Api;

public sealed partial class SqliteSchemaMigrationRunner
{
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
    /// v24：形状模型资产。模型字节按 SHA256 内容寻址单独存表（同一份字节被多个资产/版本引用时只存一份），
    /// 版本行只记哈希与训练元数据；P0 不含发布态（published_version 属 P1）。
    /// 软删除用 deleted_at，保证仍被工作流引用的资产可以保留并给出 409 引用清单。
    /// </summary>
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
