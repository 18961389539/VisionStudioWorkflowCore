from __future__ import annotations

import json
from .context import AuditContext


def audit_workflow_and_plugins(ctx: AuditContext, catalog_ctx: dict, host_ctx: dict, vendor_appsettings: dict) -> None:
    host_registration = catalog_ctx['host_registration']
    host_bootstrap = host_ctx['host_bootstrap']
    endpoint_sources = host_ctx['endpoint_sources']
    schema_migration_source = host_ctx['schema_migration_source']
    current_schema_token = host_ctx['current_schema_token']
    plugin_contracts = host_ctx['plugin_contracts']
    plugin_registry = host_ctx['plugin_registry']
    plugin_manager = host_ctx['plugin_manager']
    trace_store_dependency = host_ctx['trace_store_dependency']
    dependency_source = host_ctx['dependency_source']
    job_endpoints = host_ctx['job_endpoints']
    trace_endpoints = host_ctx['trace_endpoints']
    api_smoke = host_ctx['api_smoke']

    replay_source = ctx.read_safe("backend/src/VisionStudio.Api/Operations/OfflineReplay.cs")
    replay_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/ReplayEndpoints.cs")
    replay_ui = ctx.read_safe("frontend/src/components/ReplayDebuggerPanel.tsx")
    models_source = ctx.read_safe("backend/src/VisionStudio.Engine/Models.cs")
    workflow_data_source = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/VisionWorkflowData.cs")
    for token in ["OfflineReplayService", "EnsureReplayCompatible", "PrepareExecutableWorkflow", "__replayInputPath", "Deterministic re-execution"]:
        if token not in replay_source:
            ctx.fail(f"V0.47 replay service token missing: {token}")
    for token in ["/api/traces/{runId}/replay/context", "/api/traces/{runId}/replay/input", "/api/traces/{runId}/replay"]:
        if token not in replay_endpoints:
            ctx.fail(f"V0.47 replay endpoint missing: {token}")
    for token in ["HasReplayInput", "FindReplayInputPath", "OkReplaySampleEvery", "replay-input.png"]:
        if token not in trace_store_dependency:
            ctx.fail(f"V0.47 trace replay artifact token missing: {token}")
    for token in ["VisionValueSnapshot", "Inputs = null", "Outputs = null", "ReplayInputArtifact"]:
        if token not in models_source:
            ctx.fail(f"V0.47 node value snapshot model missing: {token}")
    if "BuildReplayInput" not in workflow_data_source:
        ctx.fail("V0.47 runtime does not capture replay input evidence")
    for token in ['new(11, "V0.47 offline replay artifacts"', "ApplyOfflineReplayArtifactsAsync", "has_replay_input", "replay_relative_path"]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.47 schema v11 replay migration missing: {token}")
    if "OfflineReplay_ReexecutesSoftwareWorkflow_AndReturnsNodeSnapshots" not in api_smoke:
        ctx.fail("V0.47 replay API regression test missing")
    sqlite_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Storage/SqliteStorageTests.cs")
    if "V047ReplayInput_IsPersistedAsDedicatedPngArtifact_AndSchemaIsV11" not in sqlite_tests:
        ctx.fail("V0.47 replay artifact persistence regression test missing")
    if "OkReplaySampleEvery" not in vendor_appsettings.get("TraceRetention", {}):
        ctx.fail("V0.47 replay sampling configuration missing")
    ctx.ok("V0.47 persisted replay inputs + deterministic offline debugger + node input/output snapshots present")

    dataset_source = ctx.read_safe("backend/src/VisionStudio.Api/Operations/DatasetValidation.cs")
    dataset_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/DatasetValidationEndpoints.cs")
    ctx.read_safe("frontend/src/components/DatasetValidationPanel.tsx")
    dataset_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Operations/DatasetValidationTests.cs")
    for token in ["DatasetValidationStore", "DatasetValidationService", "ValidationRunSummary", "FALSE_OK", "FALSE_NG", "P95DurationMs", "TopologyHash", "SourceKind", "MEDIA", "TRACE"]:
        if token not in dataset_source:
            ctx.fail(f"V0.48 dataset validation token missing: {token}")
    for token in ["/api/validation/datasets", "/api/validation/datasets/{datasetId}/items", "/api/validation/datasets/{datasetId}/runs", "/api/validation/runs/{runId}", "/api/validation/runs/{runId}/cancel"]:
        if token not in dataset_endpoints:
            ctx.fail(f"V0.48 validation endpoint missing: {token}")
    for token in [current_schema_token, "V0.48 dataset and batch validation", "validation_datasets", "validation_dataset_items", "validation_runs", "validation_results"]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.48 schema v12 dataset migration missing: {token}")
    for token in ["ExecuteImageAsync", "dataset-image-replay"]:
        if token not in replay_source:
            ctx.fail(f"V0.48 Media Library replay integration missing: {token}")
    for token in ["V048Summary_ComputesFalseOkFalseNgErrorsAndTiming", "V048Schema12_CreatesDatasetBatchValidationTables"]:
        if token not in dataset_tests:
            ctx.fail(f"V0.48 dataset regression test missing: {token}")
    if "DatasetValidation_DatasetCrudSurface_IsExposed" not in api_smoke:
        ctx.fail("V0.48 dataset API smoke test missing")
    ctx.ok("V0.48 persistent labeled datasets + Trace/Media batch validation + false OK/NG + A/B comparison present")

    tuning_source = ctx.read_safe("backend/src/VisionStudio.Api/Workflow/ParameterTuning.cs")
    tuning_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/ParameterTuningEndpoints.cs")
    ctx.read_safe("frontend/src/components/ParameterTuningPanel.tsx")
    tuning_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Recipe/ParameterTuningTests.cs")
    for token in ["ParameterTuningService", "ParameterTuningPreviewRequest", "persistTrace: false", "RunNode", "FALSE_OK", "FALSE_NG"]:
        if token not in tuning_source:
            ctx.fail(f"V0.49 tuning service token missing: {token}")
    if "/api/tuning/preview" not in tuning_endpoints:
        ctx.fail("V0.49 tuning endpoint missing: /api/tuning/preview")
    if "/api/validation/runs/{runId}/workflow" not in dataset_endpoints:
        ctx.fail("V0.49 validation candidate workflow endpoint missing")
    if "V049Classification_UsesIndustrialFalseOkFalseNgSemantics" not in tuning_tests:
        ctx.fail("V0.49 tuning regression test missing")
    if "ParameterTuning_TransientPreview_AndCandidateWorkflow_AreExposed" not in api_smoke:
        ctx.fail("V0.49 tuning API smoke test missing")
    if "persistTrace = true" not in replay_source or "if (persistTrace)" not in replay_source:
        ctx.fail("V0.49 transient replay persistence guard missing")
    ctx.ok("V0.49 transient single-image tuning + catalog-driven controls + ROI + persisted Dataset validation candidates present")

    product_recipe_source = ctx.read_safe("backend/src/VisionStudio.Api/Workflow/ProductRecipeStore.cs")
    product_recipe_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/ProductRecipeEndpoints.cs")
    product_recipe_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Recipe/ProductRecipeLifecycleTests.cs")
    for token in ["ProductDescriptor", "ValidationAcceptancePolicy", "LinkValidationAsync", "CloneRecipeAsync", "DiffAsync", "False OK"]:
        if token not in product_recipe_source:
            ctx.fail(f"V0.50 product recipe token missing: {token}")
    for token in ["/api/recipes", "/api/jobs/{id}/clone", "/validation-candidates", "/validation"]:
        if token not in product_recipe_endpoints:
            ctx.fail(f"V0.50 product recipe endpoint missing: {token}")
    for token in ["V050ProductRecipe_PublishRequiresAcceptedValidationForExactWorkflowHash", "V050ValidationPolicy_RejectsFalseOkEvenWhenAccuracyLooksHigh", "V050CloneAndDiff_KeepRecipeTopologyTraceable"]:
        if token not in product_recipe_tests:
            ctx.fail(f"V0.50 product recipe regression test missing: {token}")
    for token in ["V0.50 product recipe lifecycle", "products", "job_version_validations", "jobs.product_id", "jobs.recipe_code", current_schema_token]:
        if token not in schema_migration_source and token not in endpoint_sources:
            ctx.fail(f"V0.50 product recipe schema/version token missing: {token}")
    ctx.ok("V0.50 Product → Recipe → validated immutable version → publish lifecycle present")

    parameter_source = ctx.read_safe("backend/src/VisionStudio.Api/Workflow/RecipeParameterization.cs")
    parameter_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/RecipeParameterEndpoints.cs")
    parameter_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Recipe/RecipeParameterBindingTests.cs")
    for token in ["RecipeParameterStore", "ResolvedRecipeParameterization", "ResolveForJobAsync", "product_parameter_values", "recipe_parameter_values", "IsCompatible"]:
        if token not in parameter_source:
            ctx.fail(f"V0.51 parameter binding token missing: {token}")
    for token in ["/api/jobs/{id}/parameters", "/api/jobs/{id}/parameterization/resolve", "/api/jobs/{id}/versions/{version:int}/parameterization"]:
        if token not in parameter_endpoints:
            ctx.fail(f"V0.51 parameter endpoint missing: {token}")
    for token in ["base_workflow_hash", "base_workflow_json", "parameter_bindings_json", "parameter_snapshot_json", "V0.51 recipe parameter binding", current_schema_token]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.51 parameter schema token missing: {token}")
    for token in ["V051Resolve_BindsProductAndRecipeValues_AndFreezesSnapshotIntoVersion", "V051ImmutableVersion_DoesNotHotChangeWhenRecipeParameterDraftChanges", "V051Resolver_RejectsMissingSourceAndTypeMismatch"]:
        if token not in parameter_tests:
            ctx.fail(f"V0.51 parameter regression test missing: {token}")
    if "snapshot.Workflow" not in job_endpoints:
        ctx.fail("V0.51 Production/Publish path no longer consumes immutable effective Workflow snapshot")
    ctx.ok("V0.51 Product/Recipe parameter drafts → frozen binding snapshot → effective immutable Workflow present")

    module_source = ctx.read_safe("backend/src/VisionStudio.Api/Workflow/WorkflowModules.cs")
    module_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/WorkflowModuleEndpoints.cs")
    module_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Workflow/WorkflowModuleTests.cs")
    ctx.read_safe("frontend/src/components/WorkflowModulePanel.tsx")
    for token in ["WorkflowModuleStore", "WorkflowModuleExpander", "WorkflowModuleAuthoringService", "ModuleAwareVisionWorkflowRunner", "module.call", "ModuleHash", "ExpandAsync", "DeleteAuthoringRollbackAsync"]:
        if token not in module_source:
            ctx.fail(f"V0.52 reusable module token missing: {token}")
    for token in ["/api/modules", "/api/modules/extract", "/api/modules/{id}/versions/{version:int}", "/api/modules/{id}/versions", "/api/modules/expand", "/api/modules/validate"]:
        if token not in module_endpoints:
            ctx.fail(f"V0.52 reusable module endpoint missing: {token}")
    for token in ["workflow_modules", "workflow_module_versions", "V0.52 reusable workflow modules", current_schema_token]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.52 module schema token missing: {token}")
    for token in ["V052Extract_ProducesTypedPinnedCall_AndExpansionRestoresExecutableGraph", "V052CallParameter_OverridesInternalParameter_ButKeepsPinnedModuleVersion", "V052NewVersion_IsImmutable_AndBreakingInterfaceParameterRemovalIsRejected", "V052SchemaV15_CreatesImmutableWorkflowModuleTables"]:
        if token not in module_tests:
            ctx.fail(f"V0.52 reusable module regression test missing: {token}")
    for token in ["WorkflowModuleRuntimeDependency", "moduleExpander", "WorkflowModule"]:
        if token not in dependency_source:
            ctx.fail(f"V0.52 module runtime provenance missing: {token}")
    if "ModuleAwareVisionWorkflowRunner" not in host_registration or "AddSingleton<WorkflowModuleStore>()" not in host_registration:
        ctx.fail("V0.52 module-aware runtime DI is incomplete")
    ctx.ok("V0.52 immutable typed Subflow/Reusable Module extraction + expansion + version pinning present")

    plugin_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/PluginEndpoints.cs")
    plugin_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Plugin/PluginSdkV2Tests.cs")
    plugin_lifetime_tests = ctx.read_safe("backend/tests/VisionStudio.Engine.Tests/PluginLifetimeTests.cs")
    plugin_manifest = json.loads(ctx.read_safe("backend/src/VisionStudio.Plugin.Sample/plugin.json"))
    for token in ["VisionPluginSdk", "IVisionPluginV2", "VisionPluginCompatibility", "VisionPluginToolIdentity", "PerNode", "WithToolIdentity"]:
        if token not in plugin_contracts:
            ctx.fail(f"V0.53 SDK 2.0 contract token missing: {token}")
    for token in ["ConcurrentDictionary<string, Lazy<IVisionNodeExecutor>>", "VisionExecutorLifetime.PerNode", "UnregisterSource", "ToolIdentity"]:
        if token not in plugin_registry:
            ctx.fail(f"V0.53 PerNode/registry token missing: {token}")
    for token in ["PluginPackageManifest", "plugin.json", "PackageManifestSha256", "RestartRequired", "Rescan", "manifest-v2", "ValidateNodeSchema", "Assembly SHA-256 mismatch"]:
        if token not in plugin_manager:
            ctx.fail(f"V0.53 package loader token missing: {token}")
    for route in ["/api/plugins", "/api/plugins/sdk", "/api/plugins/rescan"]:
        if route not in plugin_endpoints:
            ctx.fail(f"V0.53 plugin endpoint missing: {route}")
    for token in ["V053ManifestPackage_LoadsWithSdk2ToolIdentityAndPerNodeLifetime", "V053ManifestDescriptorMismatch_FailsBeforeNodeActivation", "V053Rescan_ChangedActiveManifestRequiresRestartInsteadOfHotReplacement"]:
        if token not in plugin_tests:
            ctx.fail(f"V0.53 plugin package regression test missing: {token}")
    for token in ["PerNodeFactory_ReusesOneExecutorPerConfiguredNodeId", "SamplePlugin_DeclaresSdk2PerNodeToolIdentityWithoutEngineTypes"]:
        if token not in plugin_lifetime_tests:
            ctx.fail(f"V0.53 plugin lifetime regression test missing: {token}")
    if plugin_manifest.get("schemaVersion") != 2 or plugin_manifest.get("minimumSdkApiVersion") != 2 or plugin_manifest.get("maximumSdkApiVersion") != 2:
        ctx.fail("V0.53 sample plugin manifest is not SDK API/package schema v2")
    for token in ["PluginToolRuntimeDependency", "SchemaVersion < 5", "PackageManifestHash", "PluginFingerprint", "sdkApiVersion = x.SdkApiVersion"]:
        if token not in dependency_source:
            ctx.fail(f"V0.53 plugin production provenance token missing: {token}")
    if not ctx.doc_has("MIGRATION_V052_TO_V053.md", "schema v15"):
        ctx.fail("V0.53 migration does not explicitly preserve SQLite schema v15")
    ctx.ok("V0.53 manifest-based SDK 2.0 + PerNode lifetime + safe runtime discovery + tool-version provenance present")

    plugin_package_source = ctx.read_safe("backend/src/VisionStudio.Api/Plugin/PluginPackageManagement.cs")
    plugin_package_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/PluginPackageEndpoints.cs")
    plugin_package_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Plugin/PluginPackageManagementTests.cs")
    plugin_packager = ctx.read_safe("backend/src/VisionStudio.Plugin.Packager/Program.cs")
    ctx.read_safe("frontend/src/components/PluginPackagePanel.tsx")
    for token in ["PluginPackageSignatureManifest", "TrustedPluginPublisher", "RequireTrustedSignature", "ComputeContentHash", "VerifySignature", "PrepareRuntimePluginRoot", "active-pointers.json", "Pending", "publisher.cer", "signature.json"]:
        if token not in plugin_package_source:
            ctx.fail(f"V0.54 package lifecycle token missing: {token}")
    for route in ["/api/plugin-packages", "/api/plugin-packages/preflight", "/api/plugin-packages/install", "/api/plugin-packages/{id}/versions/{version}/select", "/api/plugin-packages/{id}/rollback", "/api/plugin-publishers", "/api/plugin-publishers/trust", "/api/plugin-publishers/{thumbprint}"]:
        if route not in plugin_package_endpoints:
            ctx.fail(f"V0.54 plugin package endpoint missing: {route}")
    for token in ["V054SignedPackage_PreflightValidatesTrustedPublisherWithoutExecutingPluginCode", "V054Install_KeepsMultipleVersionsAndStagesUpgradeUntilRestart", "V054Install_RejectsUnsignedPackageWhenTrustedSignaturePolicyIsEnabled"]:
        if token not in plugin_package_tests:
            ctx.fail(f"V0.54 plugin package regression test missing: {token}")
    for token in ["RSA-SHA256", "ECDSA-SHA256", "publisher.cer", "signature.json", "SignHash"]:
        if token not in plugin_packager:
            ctx.fail(f"V0.54 plugin packager token missing: {token}")
    if "AddSingleton<PluginPackageService>()" not in host_registration or "Configure<PluginPackageOptions>" not in host_registration:
        ctx.fail("V0.54 plugin package service/configuration is not registered")
    if "PrepareRuntimePluginRoot" not in host_bootstrap:
        ctx.fail("V0.54 pending plugin version is not applied before runtime PluginManager loading")
    if not ctx.doc_has("MIGRATION_V053_TO_V054.md", "schema v15"):
        ctx.fail("V0.54 migration does not explicitly preserve SQLite schema v15")
    ctx.ok("V0.54 signed .vspkg preflight + trusted publisher + version repository + pending-restart rollback present")

    worker_contracts = ctx.read_safe("backend/src/VisionStudio.Abstractions/PluginWorkerContracts.cs")
    ctx.read_safe("backend/src/VisionStudio.Abstractions/PluginWorkerWire.cs")
    worker_supervisor = ctx.read_safe("backend/src/VisionStudio.Api/Plugin/PluginWorkerSupervisor.cs")
    worker_pool = ctx.read_safe("backend/src/VisionStudio.Api/Workers/WorkerProcessPool.cs")
    worker_runtime = worker_supervisor + "\n" + worker_pool
    worker_program = ctx.read_safe("backend/src/VisionStudio.Plugin.Worker/Program.cs") + "\n" + ctx.read_safe("backend/src/VisionStudio.Engine/PluginWorkerHost.cs")
    worker_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/PluginWorkerEndpoints.cs")
    worker_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Plugin/PluginWorkerTests.cs")
    for token in ["VisionPluginIsolationMode", "WorkerProcess", "PluginWorkerDiscovery", "PluginWorkerExecutionResult", "ProtocolVersion"]:
        if token not in worker_contracts:
            ctx.fail(f"V0.55 worker contract token missing: {token}")
    for token in ["NamedPipeServerStream", "ExecutionTimeoutMs", "MaxWorkingSetMb", "MaxRestartsPerMinute", "Kill(entireProcessTree: true)", "CircuitOpen"]:
        if token not in worker_runtime:
            ctx.fail(f"V0.55 worker supervisor token missing: {token}")
    for token in ["NamedPipeClientStream", "PluginWorkerWire.ReadAsync", "registry.Register", "PluginWorkerValueCodec"]:
        if token not in worker_program:
            ctx.fail(f"V0.55 worker host token missing: {token}")
    for route in ["/api/plugin-workers", "/api/plugin-workers/{pluginId}/restart"]:
        if route not in worker_endpoints:
            ctx.fail(f"V0.55 plugin worker endpoint missing: {route}")
    for token in ["Worker_codec_round_trips_scalar_values", "Sdk_surface_advertises_worker_process_isolation", "Plugin_manifest_accepts_string_worker_isolation_mode"]:
        if token not in worker_tests:
            ctx.fail(f"V0.55 plugin worker regression test missing: {token}")
    for token in ["isolationMode = x.IsolationMode", "SchemaVersion < 6", "plugin-worker"]:
        if token not in dependency_source:
            ctx.fail(f"V0.55 worker dependency provenance token missing: {token}")
    if "VisionStudio.Plugin.Worker" not in ctx.read_safe("backend/VisionStudio.slnx"):
        ctx.fail("V0.55 worker project missing from solution")
    if "AddSingleton<PluginWorkerSupervisor>()" not in host_registration or "Configure<PluginWorkerOptions>" not in host_registration:
        ctx.fail("V0.55 worker supervisor/configuration is not registered")
    if not ctx.doc_has("MIGRATION_V054_TO_V055.md", "schema v15"):
        ctx.fail("V0.55 migration does not explicitly preserve SQLite schema v15")
    ctx.ok("V0.55 out-of-process plugin worker + timeout/crash/memory isolation + runtime provenance present")

    worker_store = ctx.read_safe("backend/src/VisionStudio.Engine/PluginWorkerSharedMemoryStore.cs")
    worker_settings = vendor_appsettings.get("PluginWorkers", {})
    for token in ["PluginWorkerSharedImage", "FileBackedSharedMemory", "SupportedImageTransports"]:
        if token not in worker_contracts:
            ctx.fail(f"V0.56 worker protocol/shared-image token missing: {token}")
    for token in ["MemoryMappedFile.CreateFromFile", "Marshal.Copy", "CleanupStaleFiles", "Path.GetFileName(fileName)", "8UC1", "16UC1", "32FC1"]:
        if token not in worker_store:
            ctx.fail(f"V0.56 shared-memory transport token missing: {token}")
    for token in ["WorkerPool", "DefaultPoolSize", "MaxPoolSize", "ConcurrentQueue<WorkerClient>", "SharedMemoryThresholdBytes", "SharedMemoryTransfers", "PoolSize"]:
        if token not in worker_runtime:
            ctx.fail(f"V0.56 worker-pool supervisor token missing: {token}")
    for token in ["--shared-memory-root", "PluginWorkerSharedMemoryStore", "FileBackedSharedMemory"]:
        if token not in worker_program and token not in worker_contracts:
            ctx.fail(f"V0.56 worker shared-memory host token missing: {token}")
    for token in ["WorkerPoolSize", "WorkerSharedMemoryThresholdBytes", "WorkerProtocolVersion", "WorkerImageTransport"]:
        if token not in plugin_manager:
            ctx.fail(f"V0.56 plugin manifest/load metadata missing: {token}")
    for token in ["Worker_protocol_v3_advertises_shared_memory_transport_and_timings", "Plugin_manifest_accepts_pool_and_shared_memory_threshold", "Shared_memory_image_codec_round_trips_raw_pixels_without_png_payload"]:
        if token not in worker_tests:
            ctx.fail(f"V0.56 worker regression test missing: {token}")
    for token in ["schemaVersion = 7", "workerProtocolVersion = x.WorkerProtocolVersion", "workerPoolSize = x.WorkerPoolSize", "workerImageTransport = x.WorkerImageTransport", "workerSharedMemoryThresholdBytes = x.WorkerSharedMemoryThresholdBytes", "plugin-worker-runtime"]:
        if token not in dependency_source:
            ctx.fail(f"V0.56 worker runtime provenance token missing: {token}")
    for key in ["DefaultPoolSize", "MaxPoolSize", "SharedMemoryEnabled", "SharedMemoryThresholdBytes", "SharedMemoryRootPath", "SharedMemoryStaleMinutes"]:
        if key not in worker_settings:
            ctx.fail(f"V0.56 PluginWorkers configuration missing: {key}")
    if worker_settings.get("MaxPoolSize") != 4 or worker_settings.get("SharedMemoryThresholdBytes") != 1048576:
        ctx.fail("V0.56 default pool/shared-memory thresholds drifted")
    if plugin_manifest.get("workerPoolSize") != 2 or plugin_manifest.get("workerSharedMemoryThresholdBytes") != 262144:
        ctx.fail("V0.56 sample worker plugin does not demonstrate pool/shared-memory configuration")
    if not ctx.doc_has("MIGRATION_V055_TO_V056.md", "schema v15"):
        ctx.fail("V0.56 migration does not explicitly preserve SQLite schema v15")
    ctx.ok("V0.56 pooled worker execution + file-backed shared-memory image transport + schema-v7 worker runtime provenance present")

    system_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/SystemEndpoints.cs")
    script_worker_program = ctx.read_safe("backend/src/VisionStudio.Script.Worker/Program.cs") + "\n" + ctx.read_safe("backend/src/VisionStudio.Engine/PluginWorkerHost.cs")
    for token in ["script.csharp", "PluginWorkerWire", "PluginWorkerValueCodec", "VisionNodeRegistry", "NamedPipeClientStream", "CSharpScriptNode"]:
        if token not in script_worker_program:
            ctx.fail(f"script worker host token missing: {token}")
    if "VisionStudio.Script.Worker" not in ctx.read_safe("backend/VisionStudio.slnx"):
        ctx.fail("script worker project missing from solution")
    if "VisionStudio.Script.Worker" not in ctx.read_safe("backend/src/VisionStudio.Api/VisionStudio.Api.csproj"):
        ctx.fail("API host does not build/copy the script worker")
    for rel in ["scripts/publish-runtime.ps1", "scripts/publish-runtime.sh"]:
        if "VisionStudio.Script.Worker.dll" not in ctx.read_safe(rel):
            ctx.fail(f"script worker is not part of the published runtime assets: {rel}")
    ctx.ok("Phase 1 script worker host present (pipe protocol + script.csharp execution + publish assets)")

    script_node_source = ctx.read_safe("backend/src/VisionStudio.Engine/Nodes/CSharpScriptNode.cs")
    for token in ["IScriptExecutionBackend", "InProcessScriptExecutionBackend", "_backend.RunAsync"]:
        if token not in script_node_source:
            ctx.fail(f"Phase 2 script node backend abstraction missing: {token}")
    script_backends = ctx.read_safe("backend/src/VisionStudio.Engine/Scripting/ScriptExecutionBackends.cs")
    for token in ["InProcessScriptExecutionBackend", "CSharpScriptRunner.RunAsync", "ScriptExecutionOutcome"]:
        if token not in script_backends:
            ctx.fail(f"Phase 2 in-process script backend missing: {token}")
    script_supervisor = ctx.read_safe("backend/src/VisionStudio.Api/Plugin/ScriptWorkerSupervisor.cs")
    for token in ["ScriptIsolationMode", "WorkerProcess", "WorkerPool", "PluginWorkerValueCodec", "ExecutionTimeoutMs"]:
        if token not in script_supervisor:
            ctx.fail(f"Phase 2 script worker supervisor missing: {token}")
    if "Configure<ScriptWorkerOptions>" not in host_registration or "IScriptExecutionBackend" not in host_registration:
        ctx.fail("Phase 2 script worker configuration/backend is not registered")
    script_worker_settings = vendor_appsettings.get("ScriptWorkers", {})
    for key in ["IsolationMode", "PoolSize", "ExecutionTimeoutMs", "MaxWorkingSetMb", "SharedMemoryThresholdBytes"]:
        if key not in script_worker_settings:
            ctx.fail(f"Phase 2 ScriptWorkers configuration missing: {key}")
    if script_worker_settings.get("IsolationMode") != "InProcess":
        ctx.fail("Phase 2 must keep InProcess as the default script isolation mode")
    if "/api/script-workers" not in ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/ScriptWorkerEndpoints.cs"):
        ctx.fail("Phase 2 script worker status endpoint missing")
    if "MapScriptWorkerEndpoints" not in ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/EndpointMapping.cs"):
        ctx.fail("Phase 2 script worker endpoints are not mapped")
    if "scriptWorker" not in system_endpoints:
        ctx.fail("Phase 2 script worker status is not exposed by /api/health")
    ctx.ok("Phase 2 script execution backend switch (InProcess default / WorkerProcess opt-in) present")

    production_script_settings = json.loads(ctx.read_safe("backend/src/VisionStudio.Api/appsettings.Production.json")).get("ScriptWorkers", {})
    if production_script_settings.get("IsolationMode") != "WorkerProcess":
        ctx.fail("Phase 3 Production must default script execution to WorkerProcess")
    if int(production_script_settings.get("JobObjectMemoryLimitMb") or 0) <= 0:
        ctx.fail("Phase 3 Production must configure a positive JobObjectMemoryLimitMb")
    job_object_source = ctx.read_safe("backend/src/VisionStudio.Api/Workers/WindowsJobObject.cs")
    for token in ["CreateJobObject", "AssignProcessToJobObject", "JobObjectLimitKillOnJobClose", "ProcessMemoryLimit", "ActiveProcessLimit"]:
        if token not in job_object_source:
            ctx.fail(f"Phase 3 Windows Job Object token missing: {token}")
    for token in ["WindowsJobObject", "JobObjectMemoryLimitMb", "_job?.Dispose()"]:
        if token not in worker_pool:
            ctx.fail(f"Phase 3 worker pool hardening token missing: {token}")
    if "IsolationMode=WorkerProcess but the worker assembly was not found" not in script_supervisor:
        ctx.fail("Phase 3 WorkerProcess mode must fail fast when the script worker assembly is missing")
    ctx.ok("Phase 3 Production worker default + Windows Job Object memory/process/kill-on-close hardening present")

    worker_performance = ctx.read_safe("backend/src/VisionStudio.Api/Plugin/PluginWorkerPerformance.cs")
    for token in ["Version = 3", "PluginWorkerTiming", "Timing { get; init; }"]:
        if token not in worker_contracts:
            ctx.fail(f"V0.57 worker timing protocol token missing: {token}")
    for token in ["QueueWaitMs", "IpcRoundTripMs", "PluginExecuteMs", "P50Ms", "P95Ms", "P99Ms", "RecommendedPoolSize", "RecommendationReady", "PluginWorkerPerformanceProfiler"]:
        if token not in worker_performance:
            ctx.fail(f"V0.57 profiler token missing: {token}")
    for token in ["PerformanceWindowSize", "PerformanceMinimumSamples", "AdaptiveTargetUtilization", "AdaptiveQueuePressureRatio", "RecordPerformance", "ResetPerformance"]:
        if token not in worker_supervisor:
            ctx.fail(f"V0.57 supervisor profiling token missing: {token}")
    for token in ["/api/plugin-workers/performance", "/api/plugin-workers/{pluginId}/performance/reset"]:
        if token not in worker_endpoints:
            ctx.fail(f"V0.57 profiler endpoint missing: {token}")
    for token in ["Performance_profiler_separates_stages_and_recommends_more_workers_under_queue_pressure", "Performance_profiler_reset_clears_rolling_window", "Worker_timing_contract_carries_decode_execute_and_encode_segments"]:
        if token not in worker_tests:
            ctx.fail(f"V0.57 profiler regression test missing: {token}")
    for key in ["PerformanceWindowSize", "PerformanceMinimumSamples", "AdaptiveTargetUtilization", "AdaptiveQueuePressureRatio"]:
        if key not in worker_settings:
            ctx.fail(f"V0.57 PluginWorkers profiling configuration missing: {key}")
    if worker_settings.get("PerformanceWindowSize") != 512 or worker_settings.get("PerformanceMinimumSamples") != 30:
        ctx.fail("V0.57 performance-window defaults drifted")
    if not ctx.doc_has("MIGRATION_V056_TO_V057.md", "schema v15"):
        ctx.fail("V0.57 migration does not explicitly preserve SQLite schema v15")
    if not ctx.doc_has("MIGRATION_V056_TO_V057.md", "**v2 to v3**"):
        ctx.fail("V0.57 migration does not document worker protocol v3")
    ctx.ok("V0.57 stage-level worker performance profiler + advisory 1/2/4 pool recommendation present")

    benchmark_source = ctx.read_safe("backend/src/VisionStudio.Api/Plugin/PluginPerformanceBenchmark.cs")
    benchmark_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/PluginBenchmarkEndpoints.cs")
    benchmark_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Plugin/PluginPerformanceBenchmarkTests.cs")
    for token in ["PluginBenchmarkRegressionPolicy", "PluginBenchmarkPoolResult", "PluginBenchmarkRecommendation", "PluginBenchmarkRegressionGate", "RunBenchmarkAsync", "DatasetId", "ValidationRunId", "BaselineRunId", "ThroughputPerSecond", "ObservedWorkingSetBytes", "MaximumP95RegressionPercent", "MinimumThroughputRatio"]:
        if token not in benchmark_source and token not in worker_supervisor:
            ctx.fail(f"V0.58 benchmark token missing: {token}")
    for route in ["/api/plugin-benchmarks", "/api/plugin-benchmarks/{runId}", "/api/plugin-benchmarks/{runId}/cancel"]:
        if route not in benchmark_endpoints:
            ctx.fail(f"V0.58 benchmark endpoint missing: {route}")
    for token in ["Recommend_PrefersSmallestPoolWithinFivePercentOfMaximumThroughput", "RegressionGate_FailsWhenP95BudgetIsExceeded", "RegressionGate_UsesBaselineRecommendedPoolInsteadOfHidingRegressionWithLargerPool"]:
        if token not in benchmark_tests:
            ctx.fail(f"V0.58 benchmark regression test missing: {token}")
    for token in ["plugin_benchmark_runs", "plugin_benchmark_pool_results", "V0.58 plugin performance benchmark history", current_schema_token]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.58 benchmark schema token missing: {token}")
    if not ctx.doc_has("MIGRATION_V057_TO_V058.md", "schema v16"):
        ctx.fail("V0.58 migration does not document SQLite schema v16")
    if not ctx.doc_has("PLUGIN_PERFORMANCE_BENCHMARK.md", "temporary benchmark"):
        ctx.fail("V0.58 benchmark documentation does not describe temporary benchmark pools")
    ctx.ok("V0.58 repeatable Dataset benchmark + temporary 1/2/4 pools + persisted regression gate present")

    benchmark_ci = ctx.read_safe("backend/src/VisionStudio.Api/Plugin/PluginBenchmarkCi.cs")
    benchmark_cli = ctx.read_safe("backend/src/VisionStudio.Benchmark.Cli/Program.cs")
    benchmark_ci_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Plugin/PluginBenchmarkCiTests.cs")
    benchmark_ci_doc = (ctx.root / "PLUGIN_BENCHMARK_CI.md").read_text(encoding="utf-8") if ctx.doc_present("PLUGIN_BENCHMARK_CI.md") else ""
    for token in ["PluginBenchmarkBaselineSnapshot", "BaselineSnapshot", "portableBaseline", "BaselinePluginVersion", "BaselinePluginAssemblySha256"]:
        if token not in benchmark_source:
            ctx.fail(f"V0.59 portable baseline token missing: {token}")
    for token in ["PluginBenchmarkCiSpec", "CurrentSchemaVersion = 1", "FromBaseline", "RequireRegressionPass"]:
        if token not in benchmark_ci:
            ctx.fail(f"V0.59 CI spec token missing: {token}")
    if "/api/plugin-benchmarks/{runId}/ci-spec" not in benchmark_endpoints:
        ctx.fail("V0.59 CI spec export endpoint missing")
    for token in ["ExitRegressionGateFailed = 10", "ExitBenchmarkFailed = 11", "ExitInfrastructureError = 12", "ExitTimedOut = 13", "GITHUB_STEP_SUMMARY", "plugin-benchmark-junit.xml", "VISIONSTUDIO_BENCH_TOKEN", "api/plugin-benchmarks"]:
        if token not in benchmark_cli:
            ctx.fail(f"V0.59 headless runner token missing: {token}")
    for token in ["CiSpec_EmbedsPortableBaselineAndRemovesDatabaseRunDependency", "RegressionGate_AcceptsPortableBaselineSnapshot"]:
        if token not in (benchmark_ci_tests + benchmark_tests):
            ctx.fail(f"V0.59 CI regression test missing: {token}")
    for rel in ["scripts/plugin-benchmark-gate.ps1", "scripts/plugin-benchmark-gate.sh", ".github/workflows/plugin-performance-gate.yml"]:
        if not (ctx.root / rel).exists():
            ctx.fail(f"V0.59 CI wrapper missing: {rel}")
    for token in ["Portable CI specification", "Headless runner", "Exit codes", "JUnit", "same-pool comparison"]:
        if benchmark_ci_doc and token.lower() not in benchmark_ci_doc.lower():
            ctx.fail(f"V0.59 CI documentation token missing: {token}")
    if not ctx.doc_has("MIGRATION_V058_TO_V059.md", "schema v16"):
        ctx.fail("V0.59 migration does not explicitly preserve SQLite schema v16")
    ctx.ok("V0.59 portable baseline CI spec + headless benchmark runner + deterministic regression exit codes present")

    observability_source = ctx.read_safe("backend/src/VisionStudio.Api/Production/RunObservability.cs")
    trace_store = ctx.read_family("backend/src/VisionStudio.Api/Trace/TraceabilityStore*.cs") + "\n" + ctx.read_safe("backend/src/VisionStudio.Api/Trace/TraceRetentionHostedService.cs")
    workflow_data = ctx.read_safe("backend/src/VisionStudio.Engine/Runtime/VisionWorkflowData.cs")
    observability_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Production/RunObservabilityTests.cs")
    ctx.read_safe("frontend/src/components/RunObservabilityPanel.tsx")
    for token in ["ExecutionSequence", "StartOffsetMs", "EndOffsetMs"]:
        if token not in models_source:
            ctx.fail(f"V0.60 node timeline field missing: {token}")
    for token in ["BeginExecutionTimeline", "BeginNodeObservation", "CurrentExecutionOffsetMs"]:
        if token not in workflow_data:
            ctx.fail(f"V0.60 monotonic timeline primitive missing: {token}")
    if "run_node_observations" not in schema_migration_source or current_schema_token not in schema_migration_source:
        ctx.fail("V0.60 SQLite schema v17 run_node_observations migration missing")
    if "InsertNodeObservationsAsync" not in trace_store:
        ctx.fail("V0.60 trace persistence does not write node observations")
    for token in ["NodeTimelineObservation", "NodePerformanceBaseline", "PeakConcurrency", "Percentile", "VerySlow"]:
        if token not in observability_source:
            ctx.fail(f"V0.60 observability read model missing: {token}")
    if "/api/traces/{runId}/observability" not in trace_endpoints:
        ctx.fail("V0.60 observability endpoint missing")
    for token in ["V060Trace_PersistsExactNodeTimeline_InSchemaV17", "V060Observability_UsesPriorSameWorkflowRuns_ForLatencyBaseline"]:
        if token not in observability_tests:
            ctx.fail(f"V0.60 observability regression test missing: {token}")
    if not ctx.doc_has("MIGRATION_V059_TO_V060.md", "schema v17"):
        ctx.fail("V0.60 migration does not document SQLite schema v17")
    ctx.ok("V0.60 exact run timeline + historical node-latency observability present")

    trace_analysis_source = ctx.read_safe("backend/src/VisionStudio.Api/Trace/TraceAnalysis.cs")
    trace_analysis_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Trace/TraceAnalysisTests.cs")
    ctx.read_safe("frontend/src/components/TraceAnalysisPanel.tsx")
    for token in ["RunTraceComparison", "TraceNodeDelta", "FailureSignatureAnalysis", "AutoPreviousOk", "ExecutionFailure", "QualityNG", "PerformanceRegression", "HashFingerprint"]:
        if token not in trace_analysis_source:
            ctx.fail(f"V0.61 trace analysis token missing: {token}")
    for route in ["/api/traces/{runId}/compare", "/api/traces/{runId}/failure-signature"]:
        if route not in trace_endpoints:
            ctx.fail(f"V0.61 trace analysis endpoint missing: {route}")
    if "initialNodeId" not in replay_ui:
        ctx.fail("V0.61 Replay/Debugger suspect-node handoff missing")
    for token in ["V061Compare_AutoSelectsNearestPriorOkRun_AndReportsNodeDiff", "V061FailureSignature_ClustersSameExecutionError_WhenVolatileNumbersDiffer"]:
        if token not in trace_analysis_tests:
            ctx.fail(f"V0.61 trace analysis regression test missing: {token}")
    if not ctx.doc_has("MIGRATION_V060_TO_V061.md", "schema v17"):
        ctx.fail("V0.61 migration does not explicitly preserve SQLite schema v17")
    ctx.doc_present("TRACE_ANALYSIS.md")
    ctx.ok("V0.61 nearest-OK trace comparison + deterministic recurring failure signatures present")

    investigation_source = ctx.read_safe("backend/src/VisionStudio.Api/Operations/InvestigationCases.cs")
    investigation_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/InvestigationEndpoints.cs")
    ctx.read_safe("frontend/src/components/InvestigationPanel.tsx")
    investigation_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Operations/InvestigationCaseTests.cs")
    for token in ["InvestigationCaseService", 'Open", "Investigating", "Resolved", "Verified", "Closed', "CaseKey", "reopenCount", "Resolution note is required", "Verification note is required", "InvestigationTrendDashboard"]:
        if token not in investigation_source:
            ctx.fail(f"V0.62 investigation domain token missing: {token}")
    for route in ["/api/investigations", "/api/investigations/trends", "/api/investigations/for-trace/{runId}", "/api/investigations/from-trace/{runId}", "/api/investigations/{id}"]:
        if route not in investigation_endpoints:
            ctx.fail(f"V0.62 investigation endpoint missing: {route}")
    for token in ['new(18, "V0.62 investigation cases and signature evidence"', "investigation_cases", "investigation_case_runs", current_schema_token]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.62 schema v18 token missing: {token}")
    for token in ["V063TrackFromTrace_DeduplicatesSignatureAndPersistsSchemaV19", "V063TrendDashboard_AggregatesTrackedSignatureEvidence"]:
        if token not in investigation_tests:
            ctx.fail(f"V0.62 investigation regression test missing: {token}")
    if "InvestigationCaseService" not in host_registration or "MapInvestigationEndpoints" not in ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/EndpointMapping.cs"):
        ctx.fail("V0.62 investigation DI/endpoint mapping missing")
    ctx.doc_present("INVESTIGATION_CASES.md")
    ctx.doc_present("MIGRATION_V061_TO_V062.md")
    ctx.ok("V0.62 deduplicated investigation lifecycle + linked trace evidence + signature trend dashboard present")

    for token in ["InvestigationVerificationEvidence", "EvaluateVerificationAsync", "GetLatestPassedVerificationEvidenceAsync", "BaselineSignatureHits", "CandidateSignatureHits", "fresh Passed dataset regression gate"]:
        if token not in investigation_source:
            ctx.fail(f"V0.63 verification gate token missing: {token}")
    if "/api/investigations/{id}/verification-evidence" not in investigation_endpoints:
        ctx.fail("V0.63 verification endpoint missing: /api/investigations/{id}/verification-evidence")
    for token in ['new(19, "V0.63 case verification gate and dataset regression evidence"', "investigation_case_verifications", "gate_status", "baseline_validation_run_id", "candidate_validation_run_id", current_schema_token]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.63 schema v19 token missing: {token}")
    for token in ["V063Verified_RequiresFreshPassedDatasetRegressionGate", "EvaluateVerificationAsync", 'Assert.Equal("Passed", gate.GateStatus)']:
        if token not in investigation_tests:
            ctx.fail(f"V0.63 verification regression test missing: {token}")
    ctx.doc_present("CASE_VERIFICATION_GATE.md")
    ctx.doc_present("MIGRATION_V062_TO_V063.md")
    ctx.ok("V0.63 resolved-case A/B dataset regression gate + signature disappearance evidence present")
