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
}
