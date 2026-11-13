namespace VisionStudio.Api;

/// <summary>
/// 有序迁移清单。与执行器分文件，避免每加一个版本就撑大 <c>SchemaMigrations.cs</c>。
/// 只允许追加版本号连续的新条目；已发布条目的 SQL / checksumSeed 不可修改，
/// 否则 <c>baseline</c> 与历史库的 checksum 校验会直接失败。
/// </summary>
public sealed partial class SqliteSchemaMigrationRunner
{
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
            "run_traces.error_code"),
        new(21, "Independent recipe validation policy", "", ApplyIndependentRecipeValidationAsync,
            "jobs.requires_validation"),
        new(22, "Versioned camera intrinsic calibration assets", CameraIntrinsicAssetSchemaSql),
        new(23, "Run result records for database persistence", ResultRecordSchemaSql),
        new(24, "Versioned shape model assets (content-addressed blobs)", ShapeModelAssetSchemaSql),
        new(25, "Generic content-addressed assets (kind + metadata), shape data migrated", GenericAssetSchemaSql),
        new(26, "Versioned planar hand-eye calibration assets", HandEyeCalibrationAssetSchemaSql),
        new(27, "Versioned SE(3) hand-eye calibration assets", HandEye3DCalibrationAssetSchemaSql),
        // 2026-11-12：登录失败计数与锁定窗口。此前只有 10 次/分钟限流，没有"这个账号已经
        // 被连续试了 N 次"的状态——厂内共享账号的口令被慢速枚举时，日志里只有一串 401。
        // 用 custom apply 而不是裸 ALTER：v3 基线库（早于安全域）没有 security_users，
        // 恢复的旧快照也可能已经带列，裸 DDL 会以 "no such table" / "duplicate column" 失败。
        new(28, "Login failure counter and lockout window", "", ApplyLoginLockoutColumnsAsync,
            "security_users.failed_attempts INTEGER NOT NULL DEFAULT 0 + security_users.locked_until TEXT NULL"),
        // 2026-11-13：报警从 production/alarms.json 迁到 SQLite。旧实现每次 Raise/Recover/Ack
        // 都要读全量、改一条、重写整个文件；报警风暴时写放大与 GC 一起放大。表结构保持与
        // AlarmRecord 一一对应，旧文件由 AlarmStore 在首次使用时导入并归档。
        new(29, "Alarm history in SQLite", AlarmHistorySchemaSql,
            checksumSeed: "alarms + ix_alarms_raised_at + ux_alarms_active_code_source")
    ];
}
