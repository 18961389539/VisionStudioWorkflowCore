from __future__ import annotations

import json
from .context import AuditContext

REQUIRED_ARTIFACTS = [
    "backend/tests/VisionStudio.Engine.Tests/Runtime/FaultInjectionTests.cs",
    "backend/tests/VisionStudio.Engine.Tests/Runtime/WorkflowPropertyTests.cs",
    "backend/tests/VisionStudio.Engine.Tests/Runtime/SoakStabilityTests.cs",
    "backend/tests/VisionStudio.Api.Tests/Storage/PersistenceConcurrencyTests.cs",
    "backend/tests/VisionStudio.Api.Tests/OpenApiContractTests.cs",
    "backend/tests/VisionStudio.Api.Tests/Production/ProductionRuntimeTests.cs",
    "backend/coverage.runsettings",
    "contracts/api-required-endpoints.json",
    "scripts/check-coverage.py",
    "scripts/soak-test.ps1",
    "scripts/soak-test.sh",
    ".github/workflows/soak-gate.yml",
    "backend/src/VisionStudio.Api/Production/ProductionRuntime.cs",
    "backend/src/VisionStudio.Api/AlarmStore.cs",
    "backend/src/VisionStudio.Engine/WorkflowFingerprint.cs",
    "backend/tests/VisionStudio.Engine.Tests/Runtime/WorkflowFingerprintTests.cs",
    "backend/tests/VisionStudio.Engine.Tests/Runtime/ParallelCapabilityTests.cs",
    "frontend/src/components/ProductionPanel.tsx",
    "scripts/publish-runtime.ps1",
    "scripts/publish-runtime.sh",
    "backend/src/VisionStudio.Api/Storage/SqliteMetadataDatabase.cs",
    "backend/src/VisionStudio.Api/Storage/LegacyStorageMigration.cs",
    "backend/tests/VisionStudio.Api.Tests/Storage/SqliteStorageTests.cs",
    "backend/src/VisionStudio.Engine/Runtime/VisionNodeRuntime.cs",
    "backend/src/VisionStudio.Engine/Runtime/VisionPipelineExecutor.cs",
    "backend/src/VisionStudio.Engine/Runtime/VisionPipelineStep.cs",
    "backend/src/VisionStudio.Engine/Runtime/VisionExecutionPolicy.cs",
    "backend/tests/VisionStudio.Engine.Tests/Runtime/HybridRuntimeTests.cs",
    "backend/src/VisionStudio.Abstractions/VisionStudio.Abstractions.csproj",
    "backend/src/VisionStudio.Abstractions/PluginContracts.cs",
    "backend/src/VisionStudio.Abstractions/ExecutionContracts.cs",
    "backend/tests/VisionStudio.Engine.Tests/PluginLifetimeTests.cs",
    "PLUGIN_ARCHITECTURE.md",
    "backend/src/VisionStudio.Api/Hosting/VisionStudioServiceRegistration.cs",
    "backend/src/VisionStudio.Api/Hosting/BootstrapHostedServices.cs",
    "backend/src/VisionStudio.Api/Infrastructure/ApiExceptionHandler.cs",
    "backend/src/VisionStudio.Api/Infrastructure/RuntimeOnlyMiddleware.cs",
    "backend/src/VisionStudio.Api/Endpoints/EndpointMapping.cs",
    "MIGRATION_V022_TO_V023.md",
    "HOST_ARCHITECTURE.md",
    "backend/src/VisionStudio.Api/Security/SecurityModels.cs",
    "backend/src/VisionStudio.Api/Security/SecurityStore.cs",
    "backend/src/VisionStudio.Api/Security/VisionStudioAuthenticationHandler.cs",
    "backend/src/VisionStudio.Api/Security/Audit.cs",
    "backend/src/VisionStudio.Api/Endpoints/SecurityEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Operations/SecurityTests.cs",
    "frontend/src/security.tsx",
    "SECURITY_ARCHITECTURE.md",
    "MIGRATION_V023_TO_V024.md",
    "backend/src/VisionStudio.Api/RuntimeDependencies.cs",
    "backend/src/VisionStudio.Api/RuntimeDependencies.Capture.cs",
    "backend/src/VisionStudio.Api/RuntimeDependencies.Hardware.cs",
    "backend/src/VisionStudio.Api/RuntimeDependencies.Calibrations.cs",
    "backend/src/VisionStudio.Api/RuntimeDependencies.Validation.cs",
    "backend/tests/VisionStudio.Api.Tests/RuntimeDependencyManifestTests.cs",
    "RUNTIME_DEPENDENCIES.md",
    "MIGRATION_V024_TO_V025.md",
    "backend/src/VisionStudio.Engine/Diagnostics/AssetHealth.cs",
    "backend/src/VisionStudio.Api/Diagnostics/AssetHealthProviders.cs",
    "backend/src/VisionStudio.Api/Diagnostics/DiagnosticsCenterService.cs",
    "backend/src/VisionStudio.Api/Diagnostics/DiagnosticsHub.cs",
    "backend/src/VisionStudio.Api/Endpoints/DiagnosticsEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Operations/DiagnosticsTests.cs",
    "frontend/src/components/DiagnosticsPanel.tsx",
    "DIAGNOSTICS_ARCHITECTURE.md",
    "MIGRATION_V025_TO_V026.md",
    "backend/src/VisionStudio.Engine/CatalogSnapshot.cs",
    "backend/src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj",
    "backend/src/VisionStudio.CatalogExporter/Program.cs",
    "backend/tests/VisionStudio.Engine.Tests/Contracts/CatalogSnapshotTests.cs",
    "frontend/src/generated/catalog.generated.json",
    "scripts/generate-frontend-catalog.ps1",
    "scripts/generate-frontend-catalog.sh",
    "CATALOG_ARCHITECTURE.md",
    "MIGRATION_V026_TO_V027.md",
    "backend/src/VisionStudio.Api/SchemaMigrations.cs",
    "backend/src/VisionStudio.Api/Storage/StorageMaintenance.cs",
    "backend/src/VisionStudio.Api/Storage/StorageBackupService.cs",
    "backend/tests/VisionStudio.Api.Tests/Storage/StorageMaintenanceTests.cs",
    "frontend/src/components/StoragePanel.tsx",
    "STORAGE_MAINTENANCE.md",
    "MIGRATION_V027_TO_V028.md",
    "backend/src/VisionStudio.Engine/Runtime/StructuredWorkflowIr.cs",
    "backend/tests/VisionStudio.Engine.Tests/Runtime/RecursiveStructuredWorkflowTests.cs",
    "samples/nested-structured-workflow.json",
    "STRUCTURED_WORKFLOW_IR.md",
    "MIGRATION_V028_TO_V029.md",
    "backend/src/VisionStudio.Api/Media/MediaLibraryService.cs",
    "backend/src/VisionStudio.Api/Endpoints/MediaEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Assets/MediaLibraryTests.cs",
    "MEDIA_LIBRARY.md",
    "MIGRATION_V029_TO_V030.md",
    "backend/src/VisionStudio.Hardware.Abstractions/HardwareProvenance.cs",
    "backend/src/VisionStudio.Api/Provenance/HardwareProvenanceStore.cs",
    "backend/src/VisionStudio.Api/Provenance/HardwareProvenanceService.cs",
    "backend/src/VisionStudio.Api/Endpoints/HardwareProvenanceEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Camera/HardwareProvenanceTests.cs",
    "frontend/src/components/HardwareProvenancePanel.tsx",
    "HARDWARE_PROVENANCE.md",
    "MIGRATION_V030_TO_V031.md",
    "backend/src/VisionStudio.Api/Provenance/VendorProvenance.cs",
    "backend/src/VisionStudio.Api/Provenance/VendorProbeBase.cs",
    "backend/src/VisionStudio.Api/Provenance/BaslerPylonProvenanceProbe.cs",
    "backend/src/VisionStudio.Api/Provenance/HikrobotMvsProvenanceProbe.cs",
    "backend/src/VisionStudio.Api/Provenance/AbbRwsProvenanceProbe.cs",
    "VENDOR_PROVENANCE.md",
    "MIGRATION_V031_TO_V032.md",
    "backend/src/VisionStudio.Camera.Basler/VisionStudio.Camera.Basler.csproj",
    "backend/src/VisionStudio.Camera.Basler/BaslerSdkReflection.cs",
    "backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraAdapterProvider.cs",
    "backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraDevice.cs",
    "backend/src/VisionStudio.Camera.Hikrobot/VisionStudio.Camera.Hikrobot.csproj",
    "backend/src/VisionStudio.Camera.Hikrobot/HikrobotSdkReflection.cs",
    "backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraAdapterProvider.cs",
    "backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraDevice.cs",
    "backend/src/VisionStudio.Api/Camera/CameraAdapterOptions.cs",
    "backend/tests/VisionStudio.Engine.Tests/Camera/CameraAcquisitionTests.cs",
    "CAMERA_VENDOR_ADAPTERS.md",
    "MIGRATION_V032_TO_V033.md",
    "backend/src/VisionStudio.Hardware.Abstractions/CameraFeatureProfiles.cs",
    "backend/src/VisionStudio.Camera.Basler/BaslerCameraFeatures.cs",
    "backend/src/VisionStudio.Camera.Hikrobot/HikrobotCameraFeatures.cs",
    "backend/src/VisionStudio.Api/Camera/CameraFeatureProfileStore.cs",
    "backend/src/VisionStudio.Api/Endpoints/CameraFeatureProfileEndpoints.cs",
    "backend/tests/VisionStudio.Engine.Tests/Camera/CameraFeatureProfileTests.cs",
    "backend/tests/VisionStudio.Api.Tests/Camera/CameraFeatureProfileStoreTests.cs",
    "CAMERA_COMMISSIONING.md",
    "MIGRATION_V033_TO_V034.md",
    "CAMERA_SYNCHRONIZATION.md",
    "MIGRATION_V034_TO_V035.md",
    "backend/src/VisionStudio.Api/Camera/CameraSynchronizationContracts.cs",
    "backend/src/VisionStudio.Api/Camera/CameraSynchronizationStores.cs",
    "backend/src/VisionStudio.Api/Camera/CameraSynchronizationService.cs",
    "backend/src/VisionStudio.Api/Endpoints/CameraSynchronizationEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Camera/CameraSynchronizationTests.cs",
    "backend/src/VisionStudio.Api/Operations/OfflineReplay.cs",
    "backend/src/VisionStudio.Api/Endpoints/ReplayEndpoints.cs",
    "frontend/src/components/ReplayDebuggerPanel.tsx",
    "OFFLINE_REPLAY_DEBUGGER.md",
    "MIGRATION_V046_TO_V047.md",
    "backend/src/VisionStudio.Api/Operations/DatasetValidation.cs",
    "backend/src/VisionStudio.Api/Endpoints/DatasetValidationEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Operations/DatasetValidationTests.cs",
    "frontend/src/components/DatasetValidationPanel.tsx",
    "DATASET_BATCH_VALIDATION.md",
    "MIGRATION_V047_TO_V048.md",
    "backend/src/VisionStudio.Api/Workflow/ParameterTuning.cs",
    "backend/src/VisionStudio.Api/Endpoints/ParameterTuningEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Recipe/ParameterTuningTests.cs",
    "frontend/src/components/ParameterTuningPanel.tsx",
    "PARAMETER_TUNING.md",
    "MIGRATION_V048_TO_V049.md",
    "backend/src/VisionStudio.Api/Workflow/ProductRecipeStore.cs",
    "backend/src/VisionStudio.Api/Endpoints/ProductRecipeEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Recipe/ProductRecipeLifecycleTests.cs",
    "PRODUCT_RECIPE_LIFECYCLE.md",
    "MIGRATION_V049_TO_V050.md",
    "backend/src/VisionStudio.Api/Workflow/RecipeParameterization.cs",
    "backend/src/VisionStudio.Api/Endpoints/RecipeParameterEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Recipe/RecipeParameterBindingTests.cs",
    "RECIPE_PARAMETER_BINDING.md",
    "MIGRATION_V050_TO_V051.md",
    "backend/src/VisionStudio.Api/Workflow/WorkflowModules.cs",
    "backend/src/VisionStudio.Api/Endpoints/WorkflowModuleEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Workflow/WorkflowModuleTests.cs",
    "frontend/src/components/WorkflowModulePanel.tsx",
    "REUSABLE_WORKFLOW_MODULES.md",
    "MIGRATION_V051_TO_V052.md",
    "backend/src/VisionStudio.Api/Endpoints/PluginEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Plugin/PluginSdkV2Tests.cs",
    "backend/src/VisionStudio.Plugin.Sample/plugin.json",
    "PLUGIN_SDK_2.md",
    "MIGRATION_V052_TO_V053.md",
    "PLUGIN_WORKER.md",
    "MIGRATION_V054_TO_V055.md",
    "backend/src/VisionStudio.Engine/PluginWorkerSharedMemoryStore.cs",
    "MIGRATION_V055_TO_V056.md",
    "backend/src/VisionStudio.Api/Plugin/PluginWorkerPerformance.cs",
    "PLUGIN_WORKER_PROFILING.md",
    "MIGRATION_V056_TO_V057.md",
    "backend/src/VisionStudio.Api/Plugin/PluginPerformanceBenchmark.cs",
    "backend/src/VisionStudio.Api/Endpoints/PluginBenchmarkEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Plugin/PluginPerformanceBenchmarkTests.cs",
    "PLUGIN_PERFORMANCE_BENCHMARK.md",
    "MIGRATION_V057_TO_V058.md",
    "backend/src/VisionStudio.Api/Plugin/PluginBenchmarkCi.cs",
    "backend/src/VisionStudio.Benchmark.Cli/VisionStudio.Benchmark.Cli.csproj",
    "backend/src/VisionStudio.Benchmark.Cli/Program.cs",
    "backend/tests/VisionStudio.Api.Tests/Plugin/PluginBenchmarkCiTests.cs",
    "PLUGIN_BENCHMARK_CI.md",
    "MIGRATION_V058_TO_V059.md",
    "scripts/plugin-benchmark-gate.ps1",
    "scripts/plugin-benchmark-gate.sh",
    ".github/workflows/plugin-performance-gate.yml",
    "backend/src/VisionStudio.Api/Operations/InvestigationCases.cs",
    "backend/src/VisionStudio.Api/Endpoints/InvestigationEndpoints.cs",
    "backend/tests/VisionStudio.Api.Tests/Operations/InvestigationCaseTests.cs",
    "frontend/src/components/InvestigationPanel.tsx",
    "INVESTIGATION_CASES.md",
    "MIGRATION_V061_TO_V062.md",
    "CASE_VERIFICATION_GATE.md",
    "MIGRATION_V062_TO_V063.md",
]


def audit_camera_and_hardware(ctx: AuditContext, catalog_ctx: dict, host_ctx: dict) -> dict:
    for rel in REQUIRED_ARTIFACTS:
        if rel.endswith(".md"):
            ctx.doc_present(rel)
        elif not (ctx.root / rel).exists():
            ctx.fail(f"Missing required architecture artifact: {rel}")

    host_registration = catalog_ctx["host_registration"]
    host_bootstrap = host_ctx["host_bootstrap"]
    api_sources = host_ctx["api_sources"]
    endpoint_sources = host_ctx["endpoint_sources"]
    schema_migration_source = host_ctx["schema_migration_source"]
    current_schema_token = host_ctx["current_schema_token"]
    plugin_contracts = host_ctx["plugin_contracts"]
    plugin_registry = host_ctx["plugin_registry"]
    plugin_manager = host_ctx["plugin_manager"]
    trace_store_dependency = host_ctx["trace_store_dependency"]
    dependency_source = host_ctx["dependency_source"]
    job_endpoints = host_ctx["job_endpoints"]
    trace_endpoints = host_ctx["trace_endpoints"]
    production_source = host_ctx["production_source"]
    api_smoke = host_ctx["api_smoke"]

    media_service = ctx.read_safe("backend/src/VisionStudio.Api/Media/MediaLibraryService.cs")
    media_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/MediaEndpoints.cs")
    file_camera = ctx.read_safe("backend/src/VisionStudio.Engine/Camera/FileCameraDevice.cs")
    media_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Assets/MediaLibraryTests.cs")
    camera_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/CameraEndpoints.cs")
    ctx.read_safe("frontend/src/components/CameraPanel.tsx")
    for token in ["media://", "ResolveSource", "NormalizeRelative", "EnsureNoReparseTraversal", "MaxImportBytes", "MaxBatchBytes", "MaxLibraryBytes", "EnsureImportCapacity", "ValidateImageSignature", "ImportAsync", "ListCollections", "ListItems"]:
        if token not in media_service:
            ctx.fail(f"V0.30 media sandbox token missing: {token}")
    for token in ["/api/media/status", "/api/media/collections", "/api/media/items", "/api/media/import", "/api/media/preview"]:
        if token not in media_endpoints:
            ctx.fail(f"V0.30 media endpoint missing: {token}")
    for token in ['RequireRateLimiting("media-import")', "MaxBatchBytes"]:
        if token not in media_endpoints:
            ctx.fail(f"V0.30 bounded media import endpoint token missing: {token}")
    for token in ["MultipartBodyLengthLimit", "KestrelServerOptions", "media-import"]:
        if token not in host_registration:
            ctx.fail(f"V0.30 media host upload guard missing: {token}")
    if "request.Path.Trim()" in camera_endpoints or "new FileCameraDevice(request" in camera_endpoints:
        ctx.fail("V0.30 File Camera endpoint still forwards arbitrary request filesystem paths")
    for token in ["ICameraSourceProvenanceProvider", "GetSourceProvenance", "IncrementalHash", "SearchOption.AllDirectories"]:
        if token not in file_camera:
            ctx.fail(f"V0.30 File Camera provenance/collection token missing: {token}")
    for token in ["SourceFingerprint", "sourceProvenance?.ContentSha256"]:
        if token not in api_sources:
            ctx.fail(f"V0.30 dependency provenance token missing: {token}")
    for token in ["ResolveSource_RejectsAbsoluteAndTraversalPaths", "Import_StoresOnlyLogicalMediaReferenceInsideRoot", "FileCameraSourceFingerprint_ChangesWhenMediaBytesChange", "RuntimeManifest_ReportsFileCameraMediaContentDrift", "Import_RejectsContentThatDoesNotMatchImageExtension"]:
        if token not in media_tests:
            ctx.fail(f"V0.30 media regression test missing: {token}")
    ctx.ok("V0.30 File Camera sandbox + Media Library + content provenance present")

    hardware_contracts = ctx.read_source("HardwareProvenance.cs")
    hardware_store = ctx.read_safe("backend/src/VisionStudio.Api/Provenance/HardwareProvenanceStore.cs")
    hardware_service = ctx.read_safe("backend/src/VisionStudio.Api/Provenance/HardwareProvenanceService.cs")
    hardware_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/HardwareProvenanceEndpoints.cs")
    hardware_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Camera/HardwareProvenanceTests.cs")
    hardware_frontend = ctx.read_safe("frontend/src/components/HardwareProvenancePanel.tsx")
    for token in ["HardwareProvenanceData", "IHardwareProvenanceProvider", "SerialNumber", "FirmwareVersion", "ControllerVersion", "ProgramHash"]:
        if token not in hardware_contracts:
            ctx.fail(f"V0.31 hardware contract token missing: {token}")
    for token in ["hardware-provenance.json", "UpsertAsync", "DeleteAsync"]:
        if token not in hardware_store:
            ctx.fail(f"V0.31 hardware store token missing: {token}")
    for token in ["adapter:", "+declared", "MissingRecommended", "Fingerprint", "fixtureRevision"]:
        if token not in hardware_service and token not in hardware_frontend:
            ctx.fail(f"V0.31 hardware provenance token missing: {token}")
    for token in ["/api/hardware-provenance", "EnsureDependencyMutationAllowed", "RequireEngineer"]:
        if token not in hardware_endpoints:
            ctx.fail(f"V0.31 hardware endpoint token missing: {token}")
    for token in ["manifestSchemaVersion", "CameraHardware", "DeviceHardware", "RobotHardware", "hardwareFingerprint"]:
        if token not in api_sources:
            ctx.fail(f"V0.31 RuntimeDependencyManifest hardware token missing: {token}")
    for token in ["VirtualCamera_ProvidesAdapterHardwareProvenance", "PlantDeclaration_MergesExternalLensAndFirmwareAndChangesFingerprint", "RuntimeManifest_DetectsHardwareDeclarationDrift", "LegacyManifestSchema_IsRejectedUntilRepublishedWithHardwareIdentity"]:
        if token not in hardware_tests:
            ctx.fail(f"V0.31 hardware regression test missing: {token}")
    ctx.ok("V0.31 adapter + declared hardware provenance and physical dependency drift guard present")

    vendor_contracts = ctx.read_safe("backend/src/VisionStudio.Api/Provenance/VendorProvenance.cs")
    vendor_base = ctx.read_safe("backend/src/VisionStudio.Api/Provenance/VendorProbeBase.cs")
    basler_probe = ctx.read_safe("backend/src/VisionStudio.Api/Provenance/BaslerPylonProvenanceProbe.cs")
    hik_probe = ctx.read_safe("backend/src/VisionStudio.Api/Provenance/HikrobotMvsProvenanceProbe.cs")
    abb_probe = ctx.read_safe("backend/src/VisionStudio.Api/Provenance/AbbRwsProvenanceProbe.cs")
    if "IAsyncHardwareProvenanceProvider" not in hardware_contracts:
        ctx.fail("V0.32 async hardware contract missing: IAsyncHardwareProvenanceProvider")
    for token in ["IVendorHardwareProvenanceProbe", "VendorProvenanceProbeRegistry", "RequiredForProduction", "BaslerPylonProbeOptions", "HikrobotMvsProbeOptions", "AbbRwsProbeOptions"]:
        if token not in vendor_contracts:
            ctx.fail(f"V0.32 vendor probe contract missing: {token}")
    for token in ["CachedVendorProvenanceProbe", "liveProbeState", "SemaphoreSlim"]:
        if token not in vendor_base:
            ctx.fail(f"V0.32 cached vendor probe behavior missing: {token}")
    for token in ["Basler.Pylon.CameraFinder", "DeviceFirmwareVersion", "SerialNumber", "ModelName"]:
        if token not in basler_probe:
            ctx.fail(f"V0.32 Basler pylon probe token missing: {token}")
    for token in ["MvCamCtrl.NET.MyCamera", "MV_CC_EnumDevices_NET", "MV_GIGE_DEVICE_INFO", "MV_USB3_DEVICE_INFO", "DeviceFirmwareVersion"]:
        if token not in hik_probe:
            ctx.fail(f"V0.32 Hikrobot MVS probe token missing: {token}")
    for token in ["ctrl/identity", "rw/system", "rw/rapid/modules", "HashRapidProgramAsync", "PasswordEnvironmentVariable"]:
        if token not in abb_probe:
            ctx.fail(f"V0.32 ABB RWS probe token missing: {token}")
    for token in ["CaptureForProductionAsync", "LiveProbeConfigured", "LiveProbeRequired", "live:"]:
        if token not in hardware_service:
            ctx.fail(f"V0.32 live hardware service integration missing: {token}")
    for token in ["VendorProvenanceBootstrapHostedService", "BaslerPylonProvenanceProbe", "HikrobotMvsProvenanceProbe", "AbbRwsProvenanceProbe", "AssetExists", "Required "]:
        if token not in host_bootstrap:
            ctx.fail(f"V0.32 vendor probe bootstrap missing: {token}")
    if "AddHostedService<VendorProvenanceBootstrapHostedService>()" not in host_registration:
        ctx.fail("V0.32 vendor provenance bootstrap is not ordered before Production AutoStart")
    if "CaptureForProductionAsync" not in dependency_source:
        ctx.fail("V0.32 required live probes do not guard RuntimeDependencyManifest capture")
    for token in ["RequiredLiveProbe_Unavailable_BlocksProductionCapture", "OptionalLiveProbe_Unavailable_AllowsProductionCaptureWithDescriptorFallback", "LiveProbeData_IsMergedIntoAutomaticChannel_AndCannotBeMaskedByDescriptor", "AbbRwsProbe_ReadsControllerRobotWareAndHashesRapidProgram", "RequiredVendorProbe_WithUnknownAsset_FailsBootstrap"]:
        if token not in hardware_tests:
            ctx.fail(f"V0.32 vendor provenance regression test missing: {token}")
    frontend_package = json.loads(ctx.read_safe("frontend/package.json"))
    vendor_appsettings = json.loads(ctx.read_safe("backend/src/VisionStudio.Api/appsettings.json"))
    if "VendorProvenance" not in vendor_appsettings or not all(k in vendor_appsettings["VendorProvenance"] for k in ["CacheSeconds", "Basler", "Hikrobot", "AbbRws"]):
        ctx.fail("V0.32 VendorProvenance configuration section missing")
    ctx.ok("V0.32 live Basler/Hikrobot/ABB provenance probes + required-production guard present")

    camera_contracts = ctx.read_source("CameraContracts.cs")  # 契约与帧类型分居 Abstractions / Hardware.Abstractions 两个程序集
    camera_worker = ctx.read_safe("backend/src/VisionStudio.Engine/Camera/CameraAcquisitionWorker.cs")
    basler_adapter = ctx.read_safe("backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraDevice.cs")
    basler_provider = ctx.read_safe("backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraAdapterProvider.cs")
    hik_adapter = ctx.read_safe("backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraDevice.cs")
    hik_provider = ctx.read_safe("backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraAdapterProvider.cs")
    camera_options = ctx.read_safe("backend/src/VisionStudio.Api/Camera/CameraAdapterOptions.cs")
    camera_ui = ctx.read_safe("frontend/src/components/CameraPanel.tsx")
    camera_tests = ctx.read_safe("backend/tests/VisionStudio.Engine.Tests/Camera/CameraAcquisitionTests.cs")
    for token in ["CameraOutputPixelFormat", "ICameraTelemetryProvider", "CameraDiscoveredDevice", "ICameraAdapterProvider", "ExternalTriggerSource"]:
        if token not in camera_contracts:
            ctx.fail(f"V0.33 camera contract missing: {token}")
    for token in ["CameraFrameTimeoutException", "FrameTimeouts", "DriverDroppedFrames", "ReconnectAsync"]:
        if token not in camera_worker and token not in camera_contracts:
            ctx.fail(f"V0.33 acquisition worker telemetry/reconnect token missing: {token}")
    for token in ["RetrieveResult", "PixelDataConverter", "SkippedImageCount", "IDisposable disposable", "TriggerSource", "ExternalTriggerSource"]:
        if token not in basler_adapter:
            ctx.fail(f"V0.33 Basler acquisition token missing: {token}")
    for token in ["CameraFinder", "DiscoverAsync", "BaslerPylonCameraDevice"]:
        if token not in basler_provider and token not in ctx.read_safe("backend/src/VisionStudio.Camera.Basler/BaslerSdkReflection.cs"):
            ctx.fail(f"V0.33 Basler discovery/provider token missing: {token}")
    for token in ["MV_CC_StartGrabbing_NET", "MV_CC_GetImageForBGR_NET", "nFrameNum", "DriverDroppedFrames", "TriggerSoftware", "ExternalTriggerSource"]:
        if token not in hik_adapter and token != "DriverDroppedFrames":
            ctx.fail(f"V0.33 Hikrobot acquisition token missing: {token}")
    for token in ["MV_CC_EnumDevices_NET", "DiscoverAsync", "HikrobotMvsCameraDevice"]:
        if token not in hik_provider and token not in ctx.read_safe("backend/src/VisionStudio.Camera.Hikrobot/HikrobotSdkReflection.cs"):
            ctx.fail(f"V0.33 Hikrobot discovery/provider token missing: {token}")
    for token in ["/api/cameras/adapters", "/api/cameras/discover", "/api/cameras/vendor", "ICameraAdapterProvider"]:
        if token not in camera_endpoints:
            ctx.fail(f"V0.33 camera endpoint missing: {token}")
    for token in ["CameraAdapters", "Configured vendor camera requires Driver and Id", "requires unavailable adapter"]:
        if token not in host_bootstrap and token != "CameraAdapters":
            ctx.fail(f"V0.33 fail-closed camera bootstrap missing: {token}")
    if "CameraAdapters" not in host_registration or "BaslerPylonCameraAdapterProvider" not in host_registration or "HikrobotMvsCameraAdapterProvider" not in host_registration:
        ctx.fail("V0.33 camera adapter DI/configuration missing")
    for token in ["FrameTimeout_IsCountedWithoutReconnect", "AdapterTelemetry_IsSurfacedSeparatelyFromRingOverwrite", "SoftwareTrigger_InvokesAdapterBeforePublishingFrame", "ExternalHardwareTrigger_CannotBeSynthesizedByHost"]:
        if token not in camera_tests:
            ctx.fail(f"V0.33 camera acquisition regression test missing: {token}")
    if "CameraAdapters" not in vendor_appsettings or not all(k in vendor_appsettings["CameraAdapters"] for k in ["BaslerAssemblyPath", "HikrobotAssemblyPath", "Cameras"]):
        ctx.fail("V0.33 CameraAdapters configuration section missing")
    self_adapters = {"visionstudio.camera.basler.dll", "visionstudio.camera.hikrobot.dll"}
    vendor_dlls = [
        x for x in ctx.root.rglob("*.dll")
        if any(k in x.name.lower() for k in ["basler", "pylon", "mvcam", "hikrobot", "mvs"])
        and x.name.lower() not in self_adapters
        and not any(part.lower() in ("bin", "obj", ".artifacts", "artifacts") for part in x.parts)
    ]
    if vendor_dlls:
        ctx.fail(f"V0.33 package contains vendor camera DLLs: {[str(x.relative_to(ctx.root)) for x in vendor_dlls]}")
    ctx.ok("V0.33 runtime-loaded Basler/Hikrobot acquisition + trigger/buffer/telemetry path present")

    camera_profile_hash = ctx.read_source("CameraFeatureProfiles.cs")
    basler_features = ctx.read_safe("backend/src/VisionStudio.Camera.Basler/BaslerCameraFeatures.cs")
    hik_features = ctx.read_safe("backend/src/VisionStudio.Camera.Hikrobot/HikrobotCameraFeatures.cs")
    profile_store = ctx.read_safe("backend/src/VisionStudio.Api/Camera/CameraFeatureProfileStore.cs")
    profile_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/CameraFeatureProfileEndpoints.cs")
    profile_tests = ctx.read_safe("backend/tests/VisionStudio.Engine.Tests/Camera/CameraFeatureProfileTests.cs")
    profile_store_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Camera/CameraFeatureProfileStoreTests.cs")
    for token in ["CameraFeatureDescriptor", "CameraCommissioningProfile", "CameraCommissioningCapabilities", "ICameraFeatureProvider", "CommissioningProfileHash"]:
        if token not in camera_contracts:
            ctx.fail(f"V0.34 commissioning contract missing: {token}")
    for token in ["SHA256.HashData", "AdvancedFeatures", "SchemaVersion != 1", "PacketSizeBytes"]:
        if token not in camera_profile_hash:
            ctx.fail(f"V0.34 profile canonicalization token missing: {token}")
    for token in ["GevSCPSPacketSize", "GevSCPD", "TriggerDelay", "LineDebouncerTime", "LineSource", "PtpEnable", "ActionDeviceKey"]:
        if token not in basler_features:
            ctx.fail(f"V0.34 Basler commissioning feature missing: {token}")
    for token in ["GevSCPSPacketSize", "GevSCPD", "TriggerDelay", "LineDebouncerTime", "StrobeEnable", "StrobeLineDuration", "GevIEEE1588", "ActionDeviceKey"]:
        if token not in hik_features:
            ctx.fail(f"V0.34 Hikrobot commissioning feature missing: {token}")
    for token in ["camera_feature_profiles", "profile_hash", "UpsertAsync", "DeleteAsync"]:
        if token not in profile_store and token not in schema_migration_source:
            ctx.fail(f"V0.34 camera profile store/schema token missing: {token}")
    for token in ["/api/cameras/{id}/commissioning", "/api/cameras/{id}/features", "/api/camera-feature-profiles/capture", "/api/camera-feature-profiles/import", "apply/{cameraId}", "EnsureDependencyMutationAllowed"]:
        if token not in profile_endpoints:
            ctx.fail(f"V0.34 commissioning endpoint missing: {token}")
    for token in ["commissioningProfileHash", "GetCommissioningProfileHash"]:
        if token not in dependency_source:
            ctx.fail(f"V0.34 dependency profile hash missing: {token}")
    for token in ["FeatureProfileId", "ApplyCommissioningProfileAsync", "CameraFeatureProfileStore"]:
        if token not in host_bootstrap and token not in camera_options:
            ctx.fail(f"V0.34 startup profile application missing: {token}")
    for token in ["CameraFeatureProfileHash_IsStableAcrossAdvancedFeatureOrdering", "CameraFeatureProfileNormalize_ClampsTransportAndTimingValues"]:
        if token not in profile_tests:
            ctx.fail(f"V0.34 profile regression test missing: {token}")
    for token in ["SchemaV5_CreatesCameraFeatureProfilesTable", "CameraFeatureProfileStore_RoundTripsPortableProfileAndHash"]:
        if token not in profile_store_tests:
            ctx.fail(f"V0.34 profile store regression test missing: {token}")
    if frontend_package.get("version") != "0.63.0" or "v63" not in frontend_package.get("name", ""):
        ctx.fail("Frontend package identity is not V0.63")
    ctx.ok("V0.34 normalized camera commissioning profile + feature browser + SQLite profile persistence present")

    sync_runtime = ctx.read_family("backend/src/VisionStudio.Api/Camera/CameraSynchronization*.cs")
    sync_endpoints = ctx.read_safe("backend/src/VisionStudio.Api/Endpoints/CameraSynchronizationEndpoints.cs")
    basler_sync = ctx.read_safe("backend/src/VisionStudio.Camera.Basler/BaslerCameraSynchronization.cs")
    hik_sync = ctx.read_safe("backend/src/VisionStudio.Camera.Hikrobot/HikrobotCameraSynchronization.cs")
    sync_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Camera/CameraSynchronizationTests.cs")
    for token in ["ICameraSynchronizationProvider", "ICameraActionCommandProvider", "CameraTimeSynchronizationStatus", "CameraActionCommandRequest", "DeviceTimestampNs", "DeviceTickFrequencyHz", "ScheduledRequiresTickFrequency"]:
        if token not in camera_contracts:
            ctx.fail(f"V0.35 camera synchronization contract missing: {token}")
    for token in ["camera_sync_groups", "CameraSynchronizationGroupStore", "CameraSynchronizationService", "MaxTriggerSkewUs", "ScheduledDeviceTimeNs", "device-ptp"]:
        if token not in sync_runtime and token not in schema_migration_source:
            ctx.fail(f"V0.35 sync runtime/schema token missing: {token}")
    for token in ["/api/camera-sync/groups", "/status", "/trigger", "/schedule", "EnsureDependencyMutationAllowed"]:
        if token not in sync_endpoints:
            ctx.fail(f"V0.35 sync endpoint token missing: {token}")
    for token in ["GevIEEE1588DataSetLatch", "GevIEEE1588OffsetFromMaster", "PtpServoStatus"]:
        if token not in basler_sync:
            ctx.fail(f"V0.35 Basler PTP diagnostic token missing: {token}")
    for token in ["ActionCommandTrigger", "Schedule", "Issue"]:
        if token not in basler_provider:
            ctx.fail(f"V0.35 Basler Action Command token missing: {token}")
    for token in ["MV_GIGE_IssueActionCommand_NET", "MV_ACTION_CMD_INFO", "bActionTimeEnable", "nActionTime", "DeviceTickFrequencyHz"]:
        if token not in hik_provider:
            ctx.fail(f"V0.35 Hikrobot Action Command token missing: {token}")
    for token in ["GevIEEE1588", "CameraPtpClockState"]:
        if token not in hik_sync:
            ctx.fail(f"V0.35 Hikrobot PTP token missing: {token}")
    for token in ["synchronizationGroupHashes", "manifestSchemaVersion"]:
        if token not in dependency_source:
            ctx.fail(f"V0.35 RuntimeDependencyManifest synchronization provenance missing: {token}")
    for token in ["SchemaV10_CreatesCameraSynchronizationCommissioningPtpAndTransportEvidence", "SynchronizationGroupHash_IsStableAcrossCameraOrdering", "CaptureResult_ComputesDeviceTimestampSkew"]:
        if token not in sync_tests:
            ctx.fail(f"V0.35 synchronization regression test missing: {token}")
    ctx.ok("V0.35 multi-camera PTP diagnostics + Action Command runtime + skew measurement retained")

    frameset_contract = ctx.read_safe("backend/src/VisionStudio.Abstractions/VisionData.cs")
    frameset_runtime = ctx.read_source("VisionFrameSet.cs")
    camera_nodes = ctx.read_safe("backend/src/VisionStudio.Engine/Nodes/CameraNodes.cs")
    frameset_tests = ctx.read_safe("backend/tests/VisionStudio.Engine.Tests/Camera/FrameSetWorkflowTests.cs")
    frameset_sample = json.loads(ctx.read_safe("samples/synchronized-frameset-workflow.json"))
    for token in ["FrameSet", "IVisionFrameSet", "VisionFrameSetFrameInfo", "VisionValue FrameSet"]:
        if token not in frameset_contract:
            ctx.fail(f"V0.36 FrameSet contract missing: {token}")
    for token in ["VisionFrameSet", "ISynchronizedFrameSetService", "SynchronizedFrameSetCaptureRequest", "IDisposable"]:
        if token not in frameset_runtime:
            ctx.fail(f"V0.36 FrameSet runtime ownership token missing: {token}")
    for token in ["camera.syncCapture", "frameset.image", "SynchronizedCaptureNode", "FrameSetImageNode", "TriggerSkewUs", "timestampBasis"]:
        if token not in camera_nodes:
            ctx.fail(f"V0.36 FrameSet node token missing: {token}")
    if "WaitForFrameLeaseAsync" not in ctx.read_safe("backend/src/VisionStudio.Engine/Camera/CameraManager.cs"):
        ctx.fail("V0.36 CameraManager cannot retain exact synchronized frame leases")
    for token in ["ISynchronizedFrameSetService", "Task<VisionFrameSet> CaptureAsync", "WaitForFrameLeaseAsync"]:
        if token not in sync_runtime:
            ctx.fail(f"V0.36 synchronization-to-FrameSet bridge missing: {token}")
    if (
        "CollectNodeResources(node, registration.Catalog, cameraIds, synchronizationGroupIds, deviceIds, robotIds)" not in dependency_source
        or "foreach (var cameraId in group.CameraIds) cameraIds.Add(cameraId)" not in dependency_source
        or 'case "camerasyncgroup": synchronizationGroupIds.Add(value); break;' not in dependency_source
    ):
        ctx.fail("V0.36 dependency manifest does not expand synchronization groupId to member cameras")
    for token in ["VisionDataType.FrameSet", "FrameSetImageNode_selects_requested_camera_without_clone", "VisionFrameSet_disposes_every_owned_image", "SynchronizedCaptureNode_emits_FrameSet_and_skew_metadata"]:
        if token not in frameset_tests:
            ctx.fail(f"V0.36 FrameSet regression test missing: {token}")
    if not any(n.get("type") == "camera.syncCapture" for n in frameset_sample.get("nodes", [])) or not any(n.get("type") == "frameset.image" for n in frameset_sample.get("nodes", [])):
        ctx.fail("V0.36 synchronized FrameSet sample does not exercise both built-in nodes")
    for rel in ["FRAMESET_WORKFLOW.md", "MIGRATION_V035_TO_V036.md"]:
        ctx.doc_present(rel)
    if (
        "frameSet: frameSetDemo" not in ctx.read_family("frontend/src/demos/*.ts")
        or "DEMO_GROUPS" not in ctx.read_safe("frontend/src/components/TemplateGalleryModal.tsx")
        or "TemplateGalleryModal" not in ctx.read_safe("frontend/src/App.tsx")
    ):
        ctx.fail("V0.36 frontend synchronized FrameSet demo is missing")
    ctx.ok("V0.36 first-class synchronized FrameSet workflow primitive + zero-copy extraction present")

    commissioning_source = ctx.read_safe("backend/src/VisionStudio.Api/Camera/CameraSynchronizationCommissioning.cs")
    for token in ["camera_sync_commissioning_tests", "CameraSynchronizationCommissioningService", "RequestedIterations", "CompletedIterations", "commissioning:{current.TestId}", "ApplicationStopping", "CancelAsync", "CameraSynchronizationCommissioningReport"]:
        if token not in commissioning_source and token not in schema_migration_source:
            ctx.fail(f"V0.40 commissioning token missing: {token}")
    for route in ["/api/camera-sync/groups/{id}/commissioning-tests", "/api/camera-sync/commissioning-tests/{testId}/cancel", "/api/camera-sync/commissioning-tests/{testId}/report"]:
        if route not in endpoint_sources:
            ctx.fail(f"V0.40 commissioning endpoint missing: {route}")
    if 'new(8, "V0.39 camera synchronization commissioning tests"' not in schema_migration_source:
        ctx.fail("V0.40 schema migration v8 missing")
    if "CommissioningStore_PersistsProgressAndReportMetadata" not in sync_tests:
        ctx.fail("V0.40 commissioning store regression test missing")
    ctx.doc_present("MIGRATION_V038_TO_V039.md")
    ctx.ok("V0.40 automatic synchronization commissioning test + persisted report workflow present")

    for token in ["ReportSchemaVersion", "HardwareFingerprint", "EvidenceHash", "WorstRuns", "GetReportHtmlAsync"]:
        if token not in commissioning_source:
            ctx.fail(f"V0.40 report evidence token missing: {token}")
    if "/api/camera-sync/commissioning-tests/{testId}/report.html" not in endpoint_sources:
        ctx.fail("V0.40 HTML report endpoint missing")
    if "exportSyncTestHtml" not in camera_ui:
        ctx.fail("V0.40 HTML report UI export missing")
    ctx.doc_present("CAMERA_COMMISSIONING_REPORT.md")
    ctx.doc_present("MIGRATION_V039_TO_V040.md")
    ctx.ok("V0.40 commissioning evidence report + HTML export present")

    for token in ["CameraSynchronizationCommissioningFrameEvidence", "DeltaFromFirstUs", "SvgPolyline", "SvgPtpOffsets", "Worst Samples & Timestamp Drill-down"]:
        if token not in commissioning_source:
            ctx.fail(f"V0.41 visual report token missing: {token}")
    ctx.doc_present("MIGRATION_V040_TO_V041.md")
    if not ctx.doc_has("CAMERA_COMMISSIONING_REPORT.md", "schema **v4**"):
        ctx.fail("Commissioning report guide does not describe current schema v4")
    ctx.ok("V0.41 visual commissioning report + per-camera worst-run timestamp drill-down present")

    for token in ["PtpSnapshots", "CameraSynchronizationPtpDiagnostics", "ComputePtpDiagnostics", "OffsetSkewPearsonCorrelation", "CapturePtpSnapshotsAsync"]:
        if token not in sync_runtime:
            ctx.fail(f"V0.42 PTP timing evidence token missing: {token}")
    if 'new(9, "V0.42 camera synchronization PTP timing evidence"' not in schema_migration_source or "ptp_json" not in schema_migration_source:
        ctx.fail("V0.42 schema migration v9 / ptp_json missing")
    if "/api/camera-sync/groups/{id}/ptp-diagnostics" not in endpoint_sources:
        ctx.fail("V0.42 PTP diagnostics endpoint missing")
    for token in ["SvgPtpTrend", "PtpDiagnostics", '"v4"']:
        if token not in commissioning_source:
            ctx.fail(f"V0.42 report v4 PTP trend token missing: {token}")
    if "PtpDiagnostics_ComputesReadyRateOffsetPercentileAndCorrelation" not in sync_tests:
        ctx.fail("V0.42 PTP diagnostics regression test missing")
    ctx.doc_present("MIGRATION_V041_TO_V042.md")
    ctx.ok("V0.42 per-run PTP evidence + timing diagnostics + report v4 trend present")

    ptp_guard = ctx.read_safe("backend/src/VisionStudio.Api/Production/ProductionPtpDriftGuard.cs")
    ptp_guard_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Production/ProductionPtpDriftGuardTests.cs")
    for token in ["PtpDriftGuardEnabled", "PtpGuardWindowRuns", "PtpGuardMinimumEvidenceRuns", "PtpGuardMinimumReadyRate", "PtpGuardCheckEveryCycles"]:
        if token not in production_source:
            ctx.fail(f"V0.43 production PTP guard config missing: {token}")
    for token in ["PROD-PTP-NOT-READY", "PROD-PTP-DRIFT", "PROD-PTP-MASTER-CLOCK", "ComputePtpDiagnostics", "ResolveRequirements"]:
        if token not in ptp_guard + production_source:
            ctx.fail(f"V0.43 production PTP guard token missing: {token}")
    if "/api/production/ptp-guard" not in endpoint_sources:
        ctx.fail("V0.43 production PTP guard endpoint missing")
    for token in ["ResolveRequirements_FindsScheduledSynchronizedCapture", "ResolveRequirements_MergesDuplicateGroupAndKeepsStrictestPolicy"]:
        if token not in ptp_guard_tests:
            ctx.fail(f"V0.43 PTP guard regression test missing: {token}")
    ctx.doc_present("MIGRATION_V042_TO_V043.md")
    ctx.ok("V0.43 Production PTP start gate + sliding-window drift/master-clock guard present")

    sync_guard = ctx.read_safe("backend/src/VisionStudio.Api/Production/ProductionSynchronizationHealthGuard.cs")
    sync_guard_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Production/ProductionSynchronizationHealthGuardTests.cs")
    for token in ["SynchronizationHealthGuardEnabled", "SynchronizationGuardWindowRuns", "SynchronizationGuardMaximumFailureRate", "SynchronizationGuardMaximumFrameTimeoutRate", "SynchronizationGuardMaxConsecutiveSkewViolations", "SynchronizationGuardUnhealthyChecksToFault", "SynchronizationGuardHealthyChecksToRecover"]:
        if token not in production_source:
            ctx.fail(f"V0.44 synchronization guard config missing: {token}")
    for token in ["PROD-SYNC-CAMERA-NOT-READY", "PROD-SYNC-ACTION", "PROD-SYNC-ACTION-ACK", "PROD-SYNC-FRAME-TIMEOUT", "PROD-SYNC-SKEW", "PROD-SYNC-FRAME-LOSS", "LiveRecoverableOnly", "EstimateSequenceGaps"]:
        if token not in sync_guard + production_source:
            ctx.fail(f"V0.44 synchronization guard token missing: {token}")
    if "ProductionSynchronizationHealthGuardService" not in host_registration:
        ctx.fail("V0.44 synchronization guard DI registration missing")
    if "/api/production/synchronization-guard" not in endpoint_sources:
        ctx.fail("V0.44 synchronization guard endpoint missing")
    for token in ["ResolveGroupIds_FindsImmediateAndScheduledSynchronizationGroups", "Evaluate_MissingLiveCameras_IsRecoverableTransportFault", "Evaluate_DisabledGuard_DoesNotInspectSynchronizationDependencies"]:
        if token not in sync_guard_tests:
            ctx.fail(f"V0.44 synchronization guard regression test missing: {token}")
    if "synchronizationHealthGuardEnabled" not in ctx.read_safe("frontend/src/types.ts") + ctx.read_safe("frontend/src/productionConfig.ts"):
        ctx.fail("V0.44 frontend sync guard type surface missing")
    ctx.doc_present("MIGRATION_V043_TO_V044.md")
    ctx.ok("V0.44 Production synchronization Action/frame/skew/sequence health guard + reconnect hysteresis present")

    basler_transport = ctx.read_safe("backend/src/VisionStudio.Camera.Basler/BaslerTransportTelemetry.cs")
    hik_transport = ctx.read_safe("backend/src/VisionStudio.Camera.Hikrobot/HikrobotTransportTelemetry.cs")
    frontend_types = ctx.read_safe("frontend/src/types.ts")
    for token in ["CameraTransportTelemetry", "HasNativeCounters", "Transport = null"]:
        if token not in camera_contracts:
            ctx.fail(f"V0.45 normalized camera transport contract missing: {token}")
    for token in ["Statistic_Buffer_Underrun_Count", "Statistic_Failed_Packet_Count", "Statistic_Resend_Request_Count", "Statistic_Total_Packet_Count", "basler-pylon-stream"]:
        if token not in basler_transport:
            ctx.fail(f"V0.45 Basler transport telemetry token missing: {token}")
    for token in ["MV_CC_GetAllMatchInfo_NET", "MV_MATCH_INFO_NET_DETECT", "nLostPacketCount", "nRequestResendPacketCount", "hikrobot-mvs-net-detect"]:
        if token not in hik_transport:
            ctx.fail(f"V0.45 Hikrobot transport telemetry token missing: {token}")
    ef_alarm_camera_config = ctx.read_safe("backend/src/VisionStudio.Api/Storage/EfCore/Configurations/AlarmAndCameraConfigurations.cs")
    for token in ["TransportJson", "TransportSnapshots", "CaptureTransportSnapshots"]:
        if token not in sync_runtime:
            ctx.fail(f"V0.45 synchronization transport evidence missing: {token}")
    if "transport_json" not in ef_alarm_camera_config:
        ctx.fail("V0.45 synchronization EF Core mapping missing: transport_json")
    for token in ["ApplyCameraTransportTelemetryEvidenceAsync", "camera_sync_runs.transport_json"]:
        if token not in schema_migration_source:
            ctx.fail(f"V0.45 schema v10 transport evidence migration missing: {token}")
    for token in ["SynchronizationGuardMaximumNativeFrameLossRate", "SynchronizationGuardMaximumBufferUnderruns", "SynchronizationGuardMaximumResynchronizations"]:
        if token not in production_source:
            ctx.fail(f"V0.45 native transport guard config missing: {token}")
    for token in ["PROD-SYNC-TRANSPORT", "ComputeNativeTransport", "vendor-native", "mixed-native+sequence"]:
        if token not in sync_guard:
            ctx.fail(f"V0.45 vendor transport production guard missing: {token}")
    if "/api/cameras/{id}/transport-telemetry" not in camera_endpoints:
        ctx.fail("V0.45 camera transport telemetry endpoint missing")
    camera_health = ctx.read_safe("backend/src/VisionStudio.Api/Diagnostics/AssetHealthProviders.cs")
    for token in ["transportLostFrames", "transportBufferUnderruns", "transportResendRequests", "transportThroughputMbps"]:
        if token not in camera_health:
            ctx.fail(f"V0.45 Diagnostics Center camera transport metric missing: {token}")
    for token in ["CameraTransportTelemetry", "transportSnapshots"]:
        if token not in frontend_types:
            ctx.fail(f"V0.45 frontend transport telemetry type missing: {token}")
    if "ComputeNativeTransport_PrefersVendorCountersAndCalculatesWindowDeltas" not in sync_guard_tests:
        ctx.fail("V0.45 vendor transport delta regression test missing")
    if "V044RuntimeConfig_UpgradesMissingNativeTransportGuardFieldsToV045Defaults" not in ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Production/ProductionRuntimeTests.cs"):
        ctx.fail("V0.45 V0.44 runtime config compatibility regression test missing")
    ctx.doc_present("MIGRATION_V044_TO_V045.md")
    ctx.ok("V0.45 Basler/Hikrobot vendor transport telemetry + persisted run evidence + native-first Production Guard present")

    gige_source = ctx.read_safe("backend/src/VisionStudio.Api/Camera/GigENetworkCommissioning.cs")
    gige_tests = ctx.read_safe("backend/tests/VisionStudio.Api.Tests/Camera/GigENetworkDiagnosticsTests.cs")
    for token in ["NetworkInterface.GetAllNetworkInterfaces", "GetIPStatistics", "SpeedMbps", "MtuBytes", "SameSubnet", "BuildTransportTrend", "PeakNicUtilization", "PacketSizeBytes", "InterPacketDelayTicks"]:
        if token not in gige_source:
            ctx.fail(f"V0.46 GigE network diagnostics token missing: {token}")
    for token in ["gige-network-diagnostics", "gige-network-tests"]:
        if token not in endpoint_sources:
            ctx.fail(f"V0.46 GigE endpoint missing: {token}")
    if "GigENetworkDiagnosticsService" not in host_registration:
        ctx.fail("V0.46 GigE diagnostics DI registration missing")
    for token in ["GigENetworkDiagnostics", "GigENetworkAssessment", "GigENetworkTrendPoint"]:
        if token not in frontend_types:
            ctx.fail(f"V0.46 frontend GigE type missing: {token}")
    for token in ["SameSubnet_UsesHostMaskAndConservativeSlash24Fallback", "BuildTransportTrend_ComputesVendorCounterDeltasAndResendRate", "BuildTransportTrend_TreatsCounterResetAsNewCounterEpoch"]:
        if token not in gige_tests:
            ctx.fail(f"V0.46 GigE regression test missing: {token}")
    if 'new(10, "V0.45 camera vendor transport telemetry evidence"' not in schema_migration_source:
        ctx.fail("V0.46 must retain the schema v10 transport migration")
    ctx.doc_present("MIGRATION_V045_TO_V046.md")
    ctx.ok("V0.46 host NIC mapping + bandwidth/resend diagnostics + one-click GigE commissioning test present")

    return vendor_appsettings
