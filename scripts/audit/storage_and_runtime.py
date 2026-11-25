from __future__ import annotations

import json
import re
from .context import AuditContext


def audit_storage_and_runtime(ctx: AuditContext, catalog_ctx: dict, host_ctx: dict) -> None:
    program = host_ctx["program"]
    host_registration = catalog_ctx["host_registration"]
    host_bootstrap = host_ctx["host_bootstrap"]
    schema_migration_source = host_ctx["schema_migration_source"]
    current_schema_token = host_ctx["current_schema_token"]
    trace_endpoints = host_ctx["trace_endpoints"]
    production_source = host_ctx["production_source"]

    compiler = ctx.read_family("backend/src/VisionStudio.Engine/Runtime/VisionWorkflowCompiler*.cs")
    if "WorkflowFingerprint.Compute" not in compiler:
        ctx.fail("Compiler does not use semantic WorkflowFingerprint")
    if "ValidateParallelCapabilities" not in compiler:
        ctx.fail("Compiler does not enforce SupportsParallel capability")
    if "CreatePipelineStep" not in compiler or "PipelineSegments" not in compiler or "VisionExecutionPolicy.IsPipelineEligible" not in compiler:
        ctx.fail("Hybrid compiler does not fuse/register VisionPipeline segments")
    runner = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/WorkflowCoreVisionRunner.cs")
    pipeline = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/VisionPipelineExecutor.cs")
    node_runtime = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/VisionNodeRuntime.cs")
    for token in ["AddSingleton<VisionNodeRuntime>()", "AddSingleton<VisionPipelineExecutor>()", "AddTransient<VisionPipelineStep>()"]:
        if token not in host_registration:
            ctx.fail(f"Hybrid runtime DI missing: {token}")
    if "data.PipelineSegments = compiled.PipelineSegments" not in runner:
        ctx.fail("Runner does not attach compiled pipeline segments to run data")
    if "VisionNodeRuntime" not in pipeline or "foreach (var nodeId in nodeIds)" not in pipeline:
        ctx.fail("VisionPipelineExecutor does not execute node-level runtime semantics")
    if "data.AddReport" not in node_runtime or "data.AddOverlays" not in node_runtime or "data.BeforeNode" not in node_runtime:
        ctx.fail("VisionNodeRuntime is missing report/overlay/debug semantics")
    ctx.ok("V0.21 hybrid Workflow Core + VisionPipelineExecutor semantics retained")

    structured_ir = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/StructuredWorkflowIr.cs")
    workflow_data = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/VisionWorkflowData.cs")
    recursive_tests = ctx.read_safe("backend/tests/VisionStudio.Engine.Tests/Runtime/RecursiveStructuredWorkflowTests.cs")
    for token in ["StructuredWorkflowIrBuilder", "FindNearestCommonJoin", "excludedBoundaryJoinId", "StructuredControlRegion", "ValidateNoCrossBranchData"]:
        if token not in structured_ir:
            ctx.fail(f"V0.29 structured IR token missing: {token}")
    for token in ["new StructuredWorkflowIrBuilder", "CompileSequence", "BranchConditions", "ControlRegions"]:
        if token not in compiler:
            ctx.fail(f"V0.29 recursive compiler token missing: {token}")
    if "data.BranchCondition == false" in compiler or '["Condition"] = "data.BranchCondition"' in compiler:
        ctx.fail("V0.29 compiler still consumes global BranchCondition")
    for token in ["SetBranchCondition", "GetBranchCondition", "SnapshotControlFlowDecisions", "MarkParallelBranches"]:
        if token not in workflow_data:
            ctx.fail(f"V0.29 keyed branch-state token missing: {token}")
    for token in ["Compiler_CompilesNestedIfWithDistinctControlRegionsAndKeyedConditions", "Compiler_CompilesParallelNestedInsideIf", "WorkflowData_KeepsNestedBranchDecisionsIndependent"]:
        if token not in recursive_tests:
            ctx.fail(f"V0.29 recursive regression test missing: {token}")
    ctx.ok("V0.29 recursive structured IR + keyed branch state + branch visualization present")

    qsh = ctx.read_safe("scripts/quality-gate.sh")
    if "Category!=Soak" not in qsh or "check-coverage.py" not in qsh:
        ctx.fail("Quality gate missing fast/coverage split")

    trace_store = ctx.read_family("backend/src/VisionStudio.Api/Trace/TraceabilityStore*.cs") + "\n" + ctx.read_safe("backend/src/VisionStudio.Api/Trace/TraceRetentionHostedService.cs")
    job_store = ctx.read_safe("backend/src/VisionStudio.Api/Operations/JobStore.cs")
    cal_store = ctx.read_safe("backend/src/VisionStudio.Api/Calibration/CalibrationWorkspace.cs")
    sqlite_db = ctx.read_safe("backend/src/VisionStudio.Api/Storage/SqliteMetadataDatabase.cs")
    if "SearchOption.AllDirectories" in trace_store:
        ctx.fail("TraceabilityStore still performs recursive directory scans")
    for token in ["run_traces", "job_versions", "calibration_versions", "journal_mode=WAL", "foreign_keys=ON"]:
        if token not in (sqlite_db + trace_store + job_store + cal_store):
            ctx.fail(f"Missing SQLite storage token: {token}")
    if "OkPreviewSampleEvery" not in trace_store or "TraceRetentionHostedService" not in trace_store:
        ctx.fail("Trace retention/sampling not wired")
    if "workflow_json TEXT NULL" not in schema_migration_source or "COALESCE(t.workflow_json, j.workflow_json)" not in trace_store or "embeddedWorkflow" not in trace_store:
        ctx.fail("Production trace workflow de-duplication / immutable Job snapshot resolution is not wired")
    if "StorageMigrationHostedService" not in host_registration or "LegacyStorageMigrationService" not in host_bootstrap:
        ctx.fail("Legacy file migration is not wired through hosted startup")
    appsettings = json.loads(ctx.read_safe("backend/src/VisionStudio.Api/appsettings.json"))
    if "TraceRetention" not in appsettings:
        ctx.fail("TraceRetention configuration missing")
    ctx.ok("V0.20 SQLite storage semantics retained")

    schema_migrations = ctx.read_family("backend/src/VisionStudio.Api/SchemaMigrations*.cs")
    storage_maintenance = ctx.read_family("backend/src/VisionStudio.Api/Storage/StorageMaintenance*.cs") + "\n" + ctx.read_family("backend/src/VisionStudio.Api/Storage/StorageCapacity*.cs")
    storage_backup = ctx.read_family("backend/src/VisionStudio.Api/Storage/StorageBackupService*.cs")
    ctx.read_safe("frontend/src/components/StoragePanel.tsx")
    for token in [current_schema_token, "schema_migration_history", "BaselineHistoryAsync", "VerifyHistoryChecksumsAsync", "SHA256.HashData", "BeginTransaction(deferred: false)"]:
        if token not in schema_migrations:
            ctx.fail(f"V0.28 migration runner missing: {token}")
    if "EnsureColumnAsync" in sqlite_db:
        ctx.fail("SqliteMetadataDatabase still owns ad-hoc EnsureColumn migration logic")
    for token in ["CreateSnapshotAsync", "RestoreSnapshotAsync", "BackupDatabase", "ValidateSnapshotAsync"]:
        if token not in sqlite_db:
            ctx.fail(f"V0.28 SQLite online backup/restore primitive missing: {token}")
    for token in ["StorageMaintenanceCoordinator", "BeginExclusiveAsync", "StorageCapacityService", "WarningFreeBytes", "CriticalFreeBytes", "STORAGE-DISK-CRITICAL", "AutoCleanupOnCritical"]:
        if token not in storage_maintenance:
            ctx.fail(f"V0.28 storage guard missing: {token}")
    for token in ["BackupFormatVersion", "DatabaseSha256", "CreateAsync", "RestoreAsync", "ProductionRuntimeState.Stopped", "pre-restore.db", "RestartRecommended", "SafeChildPath"]:
        if token not in storage_backup:
            ctx.fail(f"V0.28 backup/restore safety missing: {token}")
    for token in ["/api/storage/schema", "/api/storage/capacity", "/api/storage/backups", "/api/storage/restore"]:
        if token not in trace_endpoints:
            ctx.fail(f"V0.28 storage endpoint missing: {token}")
    if "EnsureProductionStartAllowedAsync" not in production_source:
        ctx.fail("Production start is not protected by the storage capacity guard")
    if "CanPersistArtifact" not in trace_store:
        ctx.fail("Trace artifact persistence is not protected by the storage capacity guard")
    for token in ["StorageMaintenanceMiddleware", "StorageCapacityHostedService", "StorageBackupService", "StorageCapacityService"]:
        if token not in (program + host_registration):
            ctx.fail(f"V0.28 storage service/middleware wiring missing: {token}")
    frontend_endpoints_source = ctx.read_safe("frontend/src/endpoints.ts")
    for token in ["apiUrl('storage/backups')", "apiUrl('storage/restore')"]:
        if token not in frontend_endpoints_source:
            ctx.fail(f"V0.28 storage maintenance endpoints missing: {token}")
    storage_settings = appsettings.get("Storage", {})
    for key in ["WarningFreeBytes", "CriticalFreeBytes", "MaxArtifactBytes", "MaxBackupBytes", "BackupRetentionCount"]:
        if key not in storage_settings:
            ctx.fail(f"V0.28 Storage configuration missing: {key}")
    ctx.ok("V0.28 schema migration runner + online backup/restore + disk capacity guard present")

    auto_backup = ctx.read_family("backend/src/VisionStudio.Api/Storage/AutoBackup*.cs")
    auto_backup_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Storage/AutoBackupTests.cs")
    auto_backup_doc = ctx.read_safe("docs/auto-backup.md")
    for token in ["AutoBackupOptions", "CreateAsync", "ListAsync", "IsDue", "IsStale", "BackgroundService"]:
        if token not in auto_backup:
            ctx.fail(f"P1 automatic backup missing: {token}")
    if "services.AddSingleton<AutoBackupService>();" not in host_registration or "GetRequiredService<AutoBackupService>()" not in host_registration:
        ctx.fail("P1 automatic backup service is not registered as a hosted service")
    early_health = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/SystemEndpoints.cs")
    for token in ["AutoBackupService", "GetStatusAsync", "lastBackupAt", "lastBackupAgeHours", "stale", "lastError", "listError"]:
        if token not in early_health:
            ctx.fail(f"P1 /api/health backup freshness disclosure missing: {token}")
    auto_backup_settings = storage_settings.get("AutoBackup", {})
    for key in ["Enabled", "IntervalHours", "StaleAfterHours", "IncludeArtifacts"]:
        if key not in auto_backup_settings:
            ctx.fail(f"P1 Storage:AutoBackup configuration missing: {key}")
    if "AutomaticBackup_IsRestorable_Drill" not in auto_backup_tests:
        ctx.fail("P1 automatic backup restore drill test missing")
    if "stale" not in auto_backup_doc or "degradedReasons" not in auto_backup_doc:
        ctx.fail("P1 auto backup documentation must state that a stale backup does not change readiness")
    ctx.ok("P1 automatic backup + freshness disclosure present")

    security_headers = ctx.read_safe("backend/src/VisionStudio.Api/Security/SecurityHeaders.cs")
    security_header_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/SecurityHeadersTests.cs")
    security_header_doc = ctx.read_safe("docs/security-headers.md")
    for token in ["ContentSecurityPolicy", "XContentTypeOptions", "XFrameOptions", "Referrer-Policy", "StrictTransportSecurity", "frame-ancestors", "IsHttps", "OnStarting"]:
        if token not in security_headers:
            ctx.fail(f"P1 security response headers missing: {token}")
    if "services.Configure<SecurityHeadersOptions>" not in host_registration:
        ctx.fail("P1 SecurityHeaders options are not bound")
    if "app.UseMiddleware<SecurityHeadersMiddleware>();" not in program:
        ctx.fail("P1 SecurityHeadersMiddleware is not wired into the pipeline")
    else:
        forwarded_at = program.index("app.UseForwardedHeaders();")
        security_at = program.index("app.UseMiddleware<SecurityHeadersMiddleware>();")
        static_at = program.index("app.UseStaticFiles();")
        exception_at = program.index("app.UseExceptionHandler();")
        if not (forwarded_at < security_at < static_at) or security_at > exception_at:
            ctx.fail("P1 SecurityHeadersMiddleware must run after forwarded headers and before static files/exception handling")
    security_headers_settings = appsettings.get("SecurityHeaders", {})
    for key in ["Enabled", "ContentSecurityPolicy", "Hsts", "ReferrerPolicy", "FrameOptions"]:
        if key not in security_headers_settings:
            ctx.fail(f"P1 SecurityHeaders configuration missing: {key}")
    for token in ["ProblemDetailsErrorResponse_AlsoCarriesSecurityHeaders", "HttpsRequest_CarriesHsts", "DisabledConfiguration_EmitsNoSecurityHeaders"]:
        if token not in security_header_tests:
            ctx.fail(f"P1 security header regression test missing: {token}")
    if "ws:" not in security_header_doc or "unsafe-inline" not in security_header_doc:
        ctx.fail("P1 security headers documentation must explain the SignalR/antd CSP allowances")
    ctx.ok("P1 security response headers present")

    api_csproj = ctx.read_safe("backend/src/VisionStudio.Api/VisionStudio.Api.csproj")
    package_props = ctx.read_safe("backend/Directory.Packages.props")
    install_service_script = ctx.read_safe("scripts/install-windows-service.ps1")
    uninstall_service_script = ctx.read_safe("scripts/uninstall-windows-service.ps1")
    crash_dump_script = ctx.read_safe("scripts/configure-crash-dumps.ps1")
    windows_service_doc = ctx.read_safe("docs/windows-service.md")
    if "Microsoft.Extensions.Hosting.WindowsServices" not in api_csproj or "Microsoft.Extensions.Hosting.WindowsServices" not in package_props:
        ctx.fail("P1 Windows service hosting package is not referenced/version-pinned")
    if "builder.Host.UseWindowsService();" not in program:
        ctx.fail("P1 UseWindowsService is not wired into the host builder")
    for token in ["New-Service", "sc.exe failure", "delayed-auto", "DataRoot must live outside", "WaitForStatus"]:
        if token not in install_service_script:
            ctx.fail(f"P1 Windows service install script missing: {token}")
    if "sc.exe delete" not in uninstall_service_script or "WaitForStatus" not in uninstall_service_script:
        ctx.fail("P1 Windows service uninstall script does not stop gracefully before deletion")
    for token in ["LocalDumps", "DumpFolder", "DumpType", "DumpCount", "Remove"]:
        if token not in crash_dump_script:
            ctx.fail(f"P1 crash dump script missing: {token}")
    for token in ["UseWindowsService", "LocalDumps", "DOTNET_DbgEnableMiniDump", "WindowsServiceLifetime", "DataRoot"]:
        if token not in windows_service_doc:
            ctx.fail(f"P1 Windows service documentation missing: {token}")
    if "windows-service.md" not in ctx.read_safe("docs/README.md"):
        ctx.fail("P1 Windows service documentation is not indexed")
    ctx.ok("P1 Windows service hosting + crash dump configuration present")

    engine_bench_program = ctx.read_safe("backend/src/VisionStudio.Benchmark.Cli/Program.cs")
    engine_bench_source = ctx.read_safe("backend/src/VisionStudio.Benchmark.Cli/EngineBenchmark.cs")
    engine_bench_project = ctx.read_safe("backend/src/VisionStudio.Benchmark.Cli/VisionStudio.Benchmark.Cli.csproj")
    engine_bench_gate = ctx.read_safe("scripts/engine-benchmark-gate.ps1")
    engine_bench_workflow = ctx.read_safe(".github/workflows/engine-performance-gate.yml")
    engine_bench_doc = ctx.read_safe("docs/engine-benchmark.md")
    if "VisionStudio.Engine.csproj" not in engine_bench_project:
        ctx.fail("P2 engine benchmark CLI does not reference the engine project")
    if "EngineBenchmark.Run" not in engine_bench_program:
        ctx.fail("P2 --engine-bench dispatch is not wired into the benchmark CLI")
    elif "CliOptions.Parse(args)" in engine_bench_program and engine_bench_program.index("EngineBenchmark.Run") > engine_bench_program.index("CliOptions.Parse(args)"):
        ctx.fail("P2 --engine-bench must dispatch before CliOptions.Parse (the plugin parser rejects unknown flags)")
    for token in ["--engine-bench", "--update-baseline", "MACHINE_MISMATCH", "TolerancePercent", "ExitRegression", "ExitBenchmarkFailed", "Stopwatch"]:
        if token not in engine_bench_source:
            ctx.fail(f"P2 engine benchmark implementation missing: {token}")
    for token in ["preprocess.gaussian.640x480", "preprocess.threshold.640x480", "preprocess.gaussian.1280x960", "match.shape.managedChamfer.640x480"]:
        if token not in engine_bench_source:
            ctx.fail(f"P2 engine benchmark case missing: {token}")
    for token in ["--engine-bench", "UpdateBaseline", "TolerancePercent", "$LASTEXITCODE"]:
        if token not in engine_bench_gate:
            ctx.fail(f"P2 engine benchmark gate script missing: {token}")
    if "runs-on: self-hosted" not in engine_bench_workflow or "cron:" not in engine_bench_workflow:
        ctx.fail("P2 engine performance gate must run nightly on a fixed self-hosted machine")
    for token in ["engine-benchmark-gate.ps1", "update_baseline", "ci/engine-benchmark-baseline.json"]:
        if token not in engine_bench_workflow:
            ctx.fail(f"P2 engine performance workflow missing: {token}")
    engine_bench_baseline = json.loads(ctx.read_safe("ci/engine-benchmark-baseline.json"))
    if engine_bench_baseline.get("schemaVersion") != 1:
        ctx.fail("P2 engine benchmark baseline schemaVersion must be 1")
    if not engine_bench_baseline.get("machine"):
        ctx.fail("P2 engine benchmark baseline must record the machine identity")
    baseline_cases = engine_bench_baseline.get("cases", {})
    for case_id in ["preprocess.gaussian.640x480", "preprocess.threshold.640x480", "preprocess.gaussian.1280x960", "match.shape.managedChamfer.640x480"]:
        entry = baseline_cases.get(case_id)
        if not isinstance(entry, dict) or float(entry.get("medianMs", 0)) <= 0:
            ctx.fail(f"P2 engine benchmark baseline median missing or non-positive: {case_id}")
    if "25%" not in engine_bench_doc or "MACHINE_MISMATCH" not in engine_bench_doc:
        ctx.fail("P2 engine benchmark documentation must state the 25% tolerance and machine-mismatch behaviour")
    if "engine-benchmark.md" not in ctx.read_safe("docs/README.md"):
        ctx.fail("P2 engine benchmark documentation is not indexed")
    if "engine-performance-gate.yml" not in ctx.read_safe("docs/quality-gates.md"):
        ctx.fail("P2 engine performance gate is not documented in quality-gates.md")
    ctx.ok("P2 engine algorithm performance regression gate present")

    asset_health = ctx.read_safe("backend/src/VisionStudio.Engine/Diagnostics/AssetHealth.cs")
    diagnostics_service = ctx.read_safe("backend/src/VisionStudio.Api/Diagnostics/DiagnosticsCenterService.cs")
    diagnostics_providers = ctx.read_safe("backend/src/VisionStudio.Api/Diagnostics/AssetHealthProviders.cs")
    diagnostics_panel = ctx.read_safe("frontend/src/components/DiagnosticsPanel.tsx")
    for token in ["IAssetHealthProvider", "AssetHealthSnapshot", "AssetEventEnvelope", "Healthy", "Degraded", "Faulted", "Offline"]:
        if token not in asset_health:
            ctx.fail(f"V0.26 Asset Health contract missing: {token}")
    for token in ["CameraAssetHealthProvider", "DeviceAssetHealthProvider", "RobotAssetHealthProvider"]:
        if token not in diagnostics_providers:
            ctx.fail(f"V0.26 normalized provider missing: {token}")
    for token in ["assetEvent", "assetHealth", "diagnosticsSummary", "PeriodicTimer"]:
        if token not in diagnostics_service:
            ctx.fail(f"V0.26 Diagnostics Center missing: {token}")
    if "services.AddSignalR();" not in host_registration or "MapHub<DiagnosticsHub>" not in program:
        ctx.fail("V0.26 SignalR hub is not fully wired")
    if "@microsoft/signalr" not in ctx.read_safe("frontend/package.json") or "HubConnectionBuilder" not in diagnostics_panel:
        ctx.fail("V0.26 frontend SignalR client is missing")
    ctx.ok("V0.26 unified Asset Health/Event model + SignalR Diagnostics Center present")

    catalog_snapshot = ctx.read_safe("backend/src/VisionStudio.Engine/CatalogSnapshot.cs")
    catalog_exporter = ctx.read_safe("backend/src/VisionStudio.CatalogExporter/Program.cs")
    demos_source = ctx.read_family("frontend/src/demos/*.ts")
    system_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/SystemEndpoints.cs")
    generated_catalog_doc = catalog_ctx["generated_catalog_doc"]
    generated_catalog_items = catalog_ctx["generated_catalog_items"]
    generated_catalog_types = catalog_ctx["generated_catalog_types"]
    canonical_executor_types = set(catalog_ctx["executor_types"]) - catalog_ctx["migrated_types"]
    if generated_catalog_doc.get("schemaVersion") != 1:
        ctx.fail("Generated catalog schemaVersion must be 1")
    generated_hash = generated_catalog_doc.get("hash", "")
    if not re.fullmatch(r"[0-9a-f]{64}", generated_hash):
        ctx.fail("Generated catalog hash is not a lowercase SHA-256")
    if len(generated_catalog_items) != len(canonical_executor_types):
        ctx.fail(f"Generated catalog node count drift: expected {len(canonical_executor_types)}, got {len(generated_catalog_items)}")
    if len(generated_catalog_types) != len(generated_catalog_items):
        ctx.fail("Generated catalog contains duplicate node types")
    if generated_catalog_types != canonical_executor_types:
        ctx.fail(
            f"Generated catalog type set differs from built-ins: "
            f"missing={sorted(canonical_executor_types - generated_catalog_types)}, "
            f"extra={sorted(generated_catalog_types - canonical_executor_types)}"
        )
    for token in ["ComputeHash", "SchemaVersion = 1", "DisplayOrder", "CanonicalOrder", "SHA256.HashData"]:
        if token not in catalog_snapshot:
            ctx.fail(f"Catalog snapshot implementation missing: {token}")
    for token in ["BuiltInNodeCatalog.Items", "--check", "VisionCatalogSnapshot.Create", "VisionCatalogSnapshot.ComputeHash(actual.Items)"]:
        if token not in catalog_exporter:
            ctx.fail(f"Catalog exporter/check missing: {token}")
    frontend_catalog_source = catalog_ctx["frontend_catalog_source"]
    if (
        "generated/catalog.generated.json" not in frontend_catalog_source
        or "loadFallbackCatalog" not in frontend_catalog_source
        or "import('./generated/catalog.generated.json')" not in frontend_catalog_source
    ):
        ctx.fail("Frontend fallback is not lazily loaded from generated backend catalog artifact")
    if "import generatedCatalog from './generated/catalog.generated.json'" in frontend_catalog_source:
        ctx.fail("Frontend fallback catalog must not be statically imported into the main bundle")
    if "fallbackCatalog" in demos_source or "./catalog" in demos_source:
        ctx.fail("Demo definitions still depend on frontend fallback catalog instead of being vendor-neutral graph specs")
    if "X-VisionStudio-Catalog-Hash" not in system_endpoints or "X-VisionStudio-Catalog-Schema" not in system_endpoints or "/api/catalog/meta" not in system_endpoints:
        ctx.fail("Runtime catalog authority metadata/headers are missing")
    if "validateCatalog(await response.json())" not in ctx.read_safe("frontend/src/useCatalogState.ts"):
        ctx.fail("Frontend does not validate/hydrate the authoritative runtime catalog")
    if "--check" not in qsh or "VisionStudio.CatalogExporter" not in qsh:
        ctx.fail("Quality gate does not reject stale generated frontend catalog")
    publish_sh = ctx.read_safe("scripts/publish-runtime.sh")
    publish_ps = ctx.read_safe("scripts/publish-runtime.ps1")
    if "--check" not in publish_sh or "VisionStudio.CatalogExporter" not in publish_sh or "--check" not in publish_ps or "VisionStudio.CatalogExporter" not in publish_ps:
        ctx.fail("Runtime publish path can bypass generated catalog staleness check")
    ctx.ok("V0.27 backend-authoritative runtime Catalog + generated offline fallback present")

    for fixture in ("scratch-640x480.png", "edge-640x480.png", "ring-640x480.png"):
        if not (ctx.root / "backend/tests/VisionStudio.Engine.Tests/Fixtures" / fixture).is_file():
            ctx.fail(f"Golden regression fixture missing: {fixture} (regenerate with scripts/generate-golden-fixtures.py)")
    golden = ctx.read_safe("backend/tests/VisionStudio.Engine.Tests/Runtime/GoldenWorkflowRegressionTests.cs")
    for token in ["WorkflowCoreVisionRunner", "image.threshold", "measure.caliper", "measure.caliperCircle"]:
        if token not in golden:
            ctx.fail(f"Golden regression no longer runs the real pipeline path: {token}")
    ctx.ok("Golden regression fixtures + pipeline-level cases present")

    bundle = ctx.read_safe("backend/src/VisionStudio.Api/Diagnostics/DiagnosticBundleService.cs")
    bundle_code = "\n".join(line for line in bundle.splitlines() if not line.strip().startswith("//"))
    if "appsettings" in bundle_code:
        ctx.fail("Diagnostic bundle must not zip configuration files (appsettings may contain secrets)")
    if "RequireAdministrator" not in ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/DiagnosticsEndpoints.cs"):
        ctx.fail("Diagnostic bundle endpoint is not administrator-only (audit tail + logs contain user names and paths)")
    ctx.ok("Diagnostic bundle stays administrator-only and excludes configuration files")

    security_store = ctx.read_safe("backend/src/VisionStudio.Api/Security/SecurityStore.cs")
    ef_security_config = ctx.read_safe("backend/src/VisionStudio.Api/Storage/EfCore/Configurations/SecurityConfigurations.cs")
    for token in ["FailedAttempts", "LockedUntil", "RecordFailedLoginAsync", "UnlockAsync"]:
        if token not in security_store:
            ctx.fail(f"Login lockout implementation missing: {token}")
    for token in ["failed_attempts", "locked_until"]:
        if token not in ef_security_config:
            ctx.fail(f"Login lockout EF Core mapping missing: {token}")
    if "account_locked" not in ctx.read_safe("backend/src/VisionStudio.Api/Infrastructure/ApiExceptions.cs"):
        ctx.fail("Locked accounts must answer 429 account_locked (401 makes clients retry and extends the lockout)")
    if "users/{id}/unlock" not in ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/SecurityEndpoints.cs"):
        ctx.fail("Administrator unlock endpoint is missing (a locked account would need a database edit)")
    ctx.ok("Login failure counter + lockout window + administrator unlock present")

    if 'SourceName = "VisionStudio.Engine"' not in ctx.read_safe("backend/src/VisionStudio.Engine/Diagnostics/EngineTelemetry.cs"):
        ctx.fail("Engine telemetry ActivitySource name changed; exporters registered by name will stop receiving spans")
    dispatcher = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/VisionNodeDispatcher.cs")
    for token in ['StartActivity("node.execute")', "vision.node.duration_ms", "vision.node.timed_out"]:
        if token not in dispatcher:
            ctx.fail(f"Node execution telemetry missing: {token}")
    ctx.ok("Node execution emits a standard Activity per run (duration + timeout tags)")

    if "FrameMatPool.Return" not in ctx.read_safe("backend/src/VisionStudio.Engine/Camera/CameraFrameHub.cs"):
        ctx.fail("Camera frame release no longer returns the Mat to the pool (per-frame LOH allocation returns)")
    for driver in ("VisionStudio.Camera.Hikrobot/HikrobotMvsCameraDevice.cs", "VisionStudio.Camera.Basler/BaslerPylonCameraDevice.cs"):
        if "FrameMatPool.Rent" not in ctx.read_safe(f"backend/src/{driver}"):
            ctx.fail(f"Camera driver allocates a Mat per frame instead of renting from the pool: {driver}")
    if "framePool" not in system_endpoints:
        ctx.fail("/api/health does not expose frame pool counters (the pool becomes an invisible second memory ledger)")
    ctx.ok("Camera frames are pooled and the pool is observable through /api/health")

    for doc in ("docs/golden-regression.md", "docs/diagnostic-bundle.md", "docs/login-lockout.md", "docs/engine-telemetry.md", "docs/frame-mat-pool.md"):
        ctx.doc_present(doc)
    ctx.doc_has("docs/login-lockout.md", "account_locked")
    if not (ctx.root / "frontend/e2e/recipe-publication.spec.ts").is_file():
        ctx.fail("Publication/rollback e2e spec is missing (maturity acknowledgement is only verified in jsdom)")
    ctx.ok("2026-11-12 capabilities keep their design notes and the publication e2e spec")

    for ps1 in (ctx.root / "scripts").rglob("*.ps1"):
        raw = ps1.read_bytes()
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            text = ""
        if any(ord(ch) > 127 for ch in text) and not raw.startswith(b"\xef\xbb\xbf"):
            ctx.fail(f"non-ASCII PowerShell script must carry a UTF-8 BOM: {ps1.relative_to(ctx.root).as_posix()}")
    ctx.ok("PowerShell scripts keep the PS 5.1 encoding contract (non-ASCII => UTF-8 BOM)")

    for sh in (ctx.root / "scripts").rglob("*.sh"):
        if b"\r\n" in sh.read_bytes():
            ctx.fail(f"Shell script must use LF line endings (not CRLF): {sh.relative_to(ctx.root).as_posix()}")
    ctx.ok("Shell scripts keep LF line endings (Git Bash / Linux compatible)")

    cs_tests = list((ctx.root / "backend/tests").rglob("*.cs"))
    text = "\n".join(p.read_text(encoding="utf-8") for p in cs_tests)
    facts = len(re.findall(r"\[Fact\]", text))
    theories = len(re.findall(r"\[Theory\]", text))
    soaks = len(re.findall(r'Trait\("Category",\s*"Soak"\)', text))
    ctx.ok(f"Test source: Fact={facts}, Theory={theories}, SoakTraits={soaks}")
