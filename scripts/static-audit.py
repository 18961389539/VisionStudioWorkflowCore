#!/usr/bin/env python3
from __future__ import annotations
import json, pathlib, re, sys, xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
errors: list[str] = []
notes: list[str] = []

def fail(msg: str): errors.append(msg)
def ok(msg: str): notes.append(msg)

# JSON
json_files = [p for p in ROOT.rglob('*.json') if 'node_modules' not in p.parts]
for p in json_files:
    try: json.loads(p.read_text(encoding='utf-8'))
    except Exception as e: fail(f'JSON {p.relative_to(ROOT)}: {e}')
ok(f'JSON parsed: {len(json_files)}')

# XML-ish project/runsettings/solution files
xml_files = list(ROOT.rglob('*.csproj')) + list(ROOT.rglob('*.slnx')) + list(ROOT.rglob('*.runsettings'))
for p in xml_files:
    try: ET.parse(p)
    except Exception as e: fail(f'XML {p.relative_to(ROOT)}: {e}')
ok(f'XML parsed: {len(xml_files)}')

# Executor/catalog/DI closure
node_sources = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT/'backend/src/VisionStudio.Engine/Nodes').glob('*.cs'))
executor_classes = sorted(set(re.findall(r'public sealed class\s+(\w+)(?:\([^\n]*\))?\s*:\s*IVisionNodeExecutor', node_sources)))
executor_types = sorted(set(re.findall(r'public string Type\s*=>\s*"([^"]+)";', node_sources)))
if len(executor_classes) != len(executor_types): fail(f'Executor class/type count mismatch: {len(executor_classes)} vs {len(executor_types)}')
program = (ROOT/'backend/src/VisionStudio.Api/Program.cs').read_text(encoding='utf-8')
api_source_files = list((ROOT/'backend/src/VisionStudio.Api').rglob('*.cs'))
api_sources = '\n'.join(p.read_text(encoding='utf-8') for p in api_source_files)
host_registration = (ROOT/'backend/src/VisionStudio.Api/Hosting/VisionStudioServiceRegistration.cs').read_text(encoding='utf-8')
host_bootstrap = (ROOT/'backend/src/VisionStudio.Api/Hosting/BootstrapHostedServices.cs').read_text(encoding='utf-8')
endpoint_sources = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT/'backend/src/VisionStudio.Api/Endpoints').glob('*.cs'))
catalog = (ROOT/'backend/src/VisionStudio.Engine/NodeCatalog.cs').read_text(encoding='utf-8')
frontend_catalog_source = (ROOT/'frontend/src/catalog.ts').read_text(encoding='utf-8')
generated_catalog_doc = json.loads((ROOT/'frontend/src/generated/catalog.generated.json').read_text(encoding='utf-8'))
generated_catalog_items = generated_catalog_doc.get('items', [])
generated_catalog_types = {x.get('type') for x in generated_catalog_items}
for cls in executor_classes:
    if f'AddSingleton<{cls}>()' not in host_registration: fail(f'Missing DI registration: {cls}')
    if f'GetRequiredService<{cls}>()' not in host_bootstrap: fail(f'Missing builtIns registry entry: {cls}')
for typ in executor_types:
    if f'"{typ}"' not in catalog: fail(f'Missing backend catalog type: {typ}')
    if typ not in generated_catalog_types: fail(f'Missing generated frontend fallback catalog type: {typ}')
ok(f'Built-in executors closed: {len(executor_types)}')

# Workflow sample graph references and known node types
sample_files = sorted((ROOT/'samples').glob('*-workflow.json'))
known_types = set(executor_types) | {'math.offset'}
for p in sample_files:
    data = json.loads(p.read_text(encoding='utf-8'))
    nodes = data.get('nodes', [])
    ids = {n['id'] for n in nodes}
    if len(ids) != len(nodes): fail(f'{p.name}: duplicate node ids')
    for n in nodes:
        if n.get('type') not in known_types: fail(f'{p.name}: unknown node type {n.get("type")}')
    for e in data.get('edges', []):
        if e.get('sourceNodeId') not in ids or e.get('targetNodeId') not in ids:
            fail(f'{p.name}: edge {e.get("id")} references missing node')
ok(f'Workflow sample references: {len(sample_files)}')

# V0.28 storage migration/backup/capacity + retained catalog/diagnostics/dependency/security/modular/plugin/hybrid hardening artifacts
required = [
    'backend/tests/VisionStudio.Engine.Tests/FaultInjectionTests.cs',
    'backend/tests/VisionStudio.Engine.Tests/WorkflowPropertyTests.cs',
    'backend/tests/VisionStudio.Engine.Tests/SoakStabilityTests.cs',
    'backend/tests/VisionStudio.Api.Tests/PersistenceConcurrencyTests.cs',
    'backend/tests/VisionStudio.Api.Tests/OpenApiContractTests.cs',
    'backend/tests/VisionStudio.Api.Tests/ProductionRuntimeTests.cs',
    'backend/coverage.runsettings',
    'contracts/api-required-endpoints.json',
    'scripts/check-coverage.py',
    'scripts/soak-test.ps1',
    'scripts/soak-test.sh',
    '.github/workflows/soak-gate.yml',
    'backend/src/VisionStudio.Api/ProductionRuntime.cs',
    'backend/src/VisionStudio.Api/AlarmStore.cs',
    'backend/src/VisionStudio.Engine/WorkflowFingerprint.cs',
    'backend/tests/VisionStudio.Engine.Tests/WorkflowFingerprintTests.cs',
    'backend/tests/VisionStudio.Engine.Tests/ParallelCapabilityTests.cs',
    'frontend/src/components/ProductionPanel.tsx',
    'scripts/publish-runtime.ps1',
    'scripts/publish-runtime.sh',
    'backend/src/VisionStudio.Api/SqliteMetadataDatabase.cs',
    'backend/src/VisionStudio.Api/LegacyStorageMigration.cs',
    'backend/tests/VisionStudio.Api.Tests/SqliteStorageTests.cs',
    'backend/src/VisionStudio.Engine/Runtime/VisionNodeRuntime.cs',
    'backend/src/VisionStudio.Engine/Runtime/VisionPipelineExecutor.cs',
    'backend/src/VisionStudio.Engine/Runtime/VisionPipelineStep.cs',
    'backend/src/VisionStudio.Engine/Runtime/VisionExecutionPolicy.cs',
    'backend/tests/VisionStudio.Engine.Tests/HybridRuntimeTests.cs',
    'backend/src/VisionStudio.Abstractions/VisionStudio.Abstractions.csproj',
    'backend/src/VisionStudio.Abstractions/PluginContracts.cs',
    'backend/src/VisionStudio.Abstractions/ExecutionContracts.cs',
    'backend/tests/VisionStudio.Engine.Tests/PluginLifetimeTests.cs',
    'PLUGIN_ARCHITECTURE.md',
    'backend/src/VisionStudio.Api/Hosting/VisionStudioServiceRegistration.cs',
    'backend/src/VisionStudio.Api/Hosting/BootstrapHostedServices.cs',
    'backend/src/VisionStudio.Api/Infrastructure/ApiExceptionHandler.cs',
    'backend/src/VisionStudio.Api/Infrastructure/RuntimeOnlyMiddleware.cs',
    'backend/src/VisionStudio.Api/Endpoints/EndpointMapping.cs',
    'MIGRATION_V022_TO_V023.md',
    'HOST_ARCHITECTURE.md',
    'backend/src/VisionStudio.Api/Security/SecurityModels.cs',
    'backend/src/VisionStudio.Api/Security/SecurityStore.cs',
    'backend/src/VisionStudio.Api/Security/VisionStudioAuthenticationHandler.cs',
    'backend/src/VisionStudio.Api/Security/Audit.cs',
    'backend/src/VisionStudio.Api/Endpoints/SecurityEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/SecurityTests.cs',
    'frontend/src/security.tsx',
    'SECURITY_ARCHITECTURE.md',
    'MIGRATION_V023_TO_V024.md',
    'backend/src/VisionStudio.Api/RuntimeDependencies.cs',
    'backend/tests/VisionStudio.Api.Tests/RuntimeDependencyManifestTests.cs',
    'RUNTIME_DEPENDENCIES.md',
    'MIGRATION_V024_TO_V025.md',
    'backend/src/VisionStudio.Engine/Diagnostics/AssetHealth.cs',
    'backend/src/VisionStudio.Api/Diagnostics/AssetHealthProviders.cs',
    'backend/src/VisionStudio.Api/Diagnostics/DiagnosticsCenterService.cs',
    'backend/src/VisionStudio.Api/Diagnostics/DiagnosticsHub.cs',
    'backend/src/VisionStudio.Api/Endpoints/DiagnosticsEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/DiagnosticsTests.cs',
    'frontend/src/components/DiagnosticsPanel.tsx',
    'DIAGNOSTICS_ARCHITECTURE.md',
    'MIGRATION_V025_TO_V026.md',
    'backend/src/VisionStudio.Engine/CatalogSnapshot.cs',
    'backend/src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj',
    'backend/src/VisionStudio.CatalogExporter/Program.cs',
    'backend/tests/VisionStudio.Engine.Tests/CatalogSnapshotTests.cs',
    'frontend/src/generated/catalog.generated.json',
    'scripts/generate-frontend-catalog.ps1',
    'scripts/generate-frontend-catalog.sh',
    'CATALOG_ARCHITECTURE.md',
    'MIGRATION_V026_TO_V027.md',
    'backend/src/VisionStudio.Api/SchemaMigrations.cs',
    'backend/src/VisionStudio.Api/StorageMaintenance.cs',
    'backend/src/VisionStudio.Api/StorageBackupService.cs',
    'backend/tests/VisionStudio.Api.Tests/StorageMaintenanceTests.cs',
    'frontend/src/components/StoragePanel.tsx',
    'STORAGE_MAINTENANCE.md',
    'MIGRATION_V027_TO_V028.md',
    'backend/src/VisionStudio.Engine/Runtime/StructuredWorkflowIr.cs',
    'backend/tests/VisionStudio.Engine.Tests/RecursiveStructuredWorkflowTests.cs',
    'samples/nested-structured-workflow.json',
    'STRUCTURED_WORKFLOW_IR.md',
    'MIGRATION_V028_TO_V029.md',
    'backend/src/VisionStudio.Api/Media/MediaLibraryService.cs',
    'backend/src/VisionStudio.Api/Endpoints/MediaEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/MediaLibraryTests.cs',
    'MEDIA_LIBRARY.md',
    'MIGRATION_V029_TO_V030.md',
    'backend/src/VisionStudio.Engine/Provenance/HardwareProvenance.cs',
    'backend/src/VisionStudio.Api/Provenance/HardwareProvenanceStore.cs',
    'backend/src/VisionStudio.Api/Provenance/HardwareProvenanceService.cs',
    'backend/src/VisionStudio.Api/Endpoints/HardwareProvenanceEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/HardwareProvenanceTests.cs',
    'frontend/src/components/HardwareProvenancePanel.tsx',
    'HARDWARE_PROVENANCE.md',
    'MIGRATION_V030_TO_V031.md',
    'backend/src/VisionStudio.Api/Provenance/VendorProvenance.cs',
    'backend/src/VisionStudio.Api/Provenance/VendorProbeBase.cs',
    'backend/src/VisionStudio.Api/Provenance/BaslerPylonProvenanceProbe.cs',
    'backend/src/VisionStudio.Api/Provenance/HikrobotMvsProvenanceProbe.cs',
    'backend/src/VisionStudio.Api/Provenance/AbbRwsProvenanceProbe.cs',
    'VENDOR_PROVENANCE.md',
    'MIGRATION_V031_TO_V032.md',
    'backend/src/VisionStudio.Camera.Basler/VisionStudio.Camera.Basler.csproj',
    'backend/src/VisionStudio.Camera.Basler/BaslerSdkReflection.cs',
    'backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraAdapterProvider.cs',
    'backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraDevice.cs',
    'backend/src/VisionStudio.Camera.Hikrobot/VisionStudio.Camera.Hikrobot.csproj',
    'backend/src/VisionStudio.Camera.Hikrobot/HikrobotSdkReflection.cs',
    'backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraAdapterProvider.cs',
    'backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraDevice.cs',
    'backend/src/VisionStudio.Api/CameraAdapterOptions.cs',
    'backend/tests/VisionStudio.Engine.Tests/CameraAcquisitionTests.cs',
    'CAMERA_VENDOR_ADAPTERS.md',
    'MIGRATION_V032_TO_V033.md',
    'backend/src/VisionStudio.Engine/Camera/CameraFeatureProfiles.cs',
    'backend/src/VisionStudio.Camera.Basler/BaslerCameraFeatures.cs',
    'backend/src/VisionStudio.Camera.Hikrobot/HikrobotCameraFeatures.cs',
    'backend/src/VisionStudio.Api/CameraFeatureProfileStore.cs',
    'backend/src/VisionStudio.Api/Endpoints/CameraFeatureProfileEndpoints.cs',
    'backend/tests/VisionStudio.Engine.Tests/CameraFeatureProfileTests.cs',
    'backend/tests/VisionStudio.Api.Tests/CameraFeatureProfileStoreTests.cs',
    'CAMERA_COMMISSIONING.md',
    'MIGRATION_V033_TO_V034.md',
    'CAMERA_SYNCHRONIZATION.md',
    'MIGRATION_V034_TO_V035.md',
    'backend/src/VisionStudio.Api/CameraSynchronization.cs',
    'backend/src/VisionStudio.Api/Endpoints/CameraSynchronizationEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/CameraSynchronizationTests.cs',
    'backend/src/VisionStudio.Api/OfflineReplay.cs',
    'backend/src/VisionStudio.Api/Endpoints/ReplayEndpoints.cs',
    'frontend/src/components/ReplayDebuggerPanel.tsx',
    'OFFLINE_REPLAY_DEBUGGER.md',
    'MIGRATION_V046_TO_V047.md',
    'backend/src/VisionStudio.Api/DatasetValidation.cs',
    'backend/src/VisionStudio.Api/Endpoints/DatasetValidationEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/DatasetValidationTests.cs',
    'frontend/src/components/DatasetValidationPanel.tsx',
    'DATASET_BATCH_VALIDATION.md',
    'MIGRATION_V047_TO_V048.md',
    'backend/src/VisionStudio.Api/ParameterTuning.cs',
    'backend/src/VisionStudio.Api/Endpoints/ParameterTuningEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/ParameterTuningTests.cs',
    'frontend/src/components/ParameterTuningPanel.tsx',
    'PARAMETER_TUNING.md',
    'MIGRATION_V048_TO_V049.md',
    'backend/src/VisionStudio.Api/ProductRecipeStore.cs',
    'backend/src/VisionStudio.Api/Endpoints/ProductRecipeEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/ProductRecipeLifecycleTests.cs',
    'PRODUCT_RECIPE_LIFECYCLE.md',
    'MIGRATION_V049_TO_V050.md',
    'backend/src/VisionStudio.Api/RecipeParameterization.cs',
    'backend/src/VisionStudio.Api/Endpoints/RecipeParameterEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/RecipeParameterBindingTests.cs',
    'RECIPE_PARAMETER_BINDING.md',
    'MIGRATION_V050_TO_V051.md',
    'backend/src/VisionStudio.Api/WorkflowModules.cs',
    'backend/src/VisionStudio.Api/Endpoints/WorkflowModuleEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/WorkflowModuleTests.cs',
    'frontend/src/components/WorkflowModulePanel.tsx',
    'REUSABLE_WORKFLOW_MODULES.md',
    'MIGRATION_V051_TO_V052.md',
    'backend/src/VisionStudio.Api/Endpoints/PluginEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/PluginSdkV2Tests.cs',
    'backend/src/VisionStudio.Plugin.Sample/plugin.json',
    'PLUGIN_SDK_2.md',
    'MIGRATION_V052_TO_V053.md',
    'PLUGIN_WORKER.md',
    'MIGRATION_V054_TO_V055.md',
    'backend/src/VisionStudio.Engine/PluginWorkerSharedMemoryStore.cs',
    'MIGRATION_V055_TO_V056.md',
    'backend/src/VisionStudio.Api/PluginWorkerPerformance.cs',
    'PLUGIN_WORKER_PROFILING.md',
    'MIGRATION_V056_TO_V057.md',
    'backend/src/VisionStudio.Api/PluginPerformanceBenchmark.cs',
    'backend/src/VisionStudio.Api/Endpoints/PluginBenchmarkEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/PluginPerformanceBenchmarkTests.cs',
    'PLUGIN_PERFORMANCE_BENCHMARK.md',
    'MIGRATION_V057_TO_V058.md',
    'backend/src/VisionStudio.Api/PluginBenchmarkCi.cs',
    'backend/src/VisionStudio.Benchmark.Cli/VisionStudio.Benchmark.Cli.csproj',
    'backend/src/VisionStudio.Benchmark.Cli/Program.cs',
    'backend/tests/VisionStudio.Api.Tests/PluginBenchmarkCiTests.cs',
    'PLUGIN_BENCHMARK_CI.md',
    'MIGRATION_V058_TO_V059.md',
    'scripts/plugin-benchmark-gate.ps1',
    'scripts/plugin-benchmark-gate.sh',
    '.github/workflows/plugin-performance-gate.yml',
    'backend/src/VisionStudio.Api/InvestigationCases.cs',
    'backend/src/VisionStudio.Api/Endpoints/InvestigationEndpoints.cs',
    'backend/tests/VisionStudio.Api.Tests/InvestigationCaseTests.cs',
    'frontend/src/components/InvestigationPanel.tsx',
    'INVESTIGATION_CASES.md',
    'MIGRATION_V061_TO_V062.md',
    'CASE_VERIFICATION_GATE.md',
    'MIGRATION_V062_TO_V063.md',
]

for rel in required:
    if not (ROOT/rel).exists(): fail(f'Missing required architecture artifact: {rel}')

api_csproj=(ROOT/'backend/src/VisionStudio.Api/VisionStudio.Api.csproj').read_text(encoding='utf-8')
if 'Microsoft.AspNetCore.OpenApi' not in api_csproj: fail('OpenAPI package missing from API csproj')
if 'Microsoft.Data.Sqlite' not in api_csproj or '10.0.12' not in api_csproj: fail('Microsoft.Data.Sqlite 10.0.12 missing from API csproj')
if 'services.AddOpenApi();' not in host_registration or 'app.MapOpenApi()' not in program: fail('OpenAPI service/endpoint not wired')
if 'V0.63' not in endpoint_sources: fail('API health version not V0.63')
api_smoke=(ROOT/'backend/tests/VisionStudio.Api.Tests/ApiSmokeTests.cs').read_text(encoding='utf-8')
if 'v0.63' not in api_smoke.lower(): fail('API smoke test does not assert V0.63 health surface')
if 'RuntimeOnlyPolicy.IsMutationAllowed' not in api_sources: fail('Runtime-only explicit mutation allowlist is missing')
compiler=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionWorkflowCompiler.cs').read_text(encoding='utf-8')
if 'WorkflowFingerprint.ComputeShort' not in compiler: fail('Compiler does not use semantic WorkflowFingerprint')
if 'ValidateParallelCapabilities' not in compiler: fail('Compiler does not enforce SupportsParallel capability')
if 'CreatePipelineStep' not in compiler or 'PipelineSegments' not in compiler or 'VisionExecutionPolicy.IsPipelineEligible' not in compiler:
    fail('Hybrid compiler does not fuse/register VisionPipeline segments')
runner=(ROOT/'backend/src/VisionStudio.Engine/Runtime/WorkflowCoreVisionRunner.cs').read_text(encoding='utf-8')
pipeline=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionPipelineExecutor.cs').read_text(encoding='utf-8')
node_runtime=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionNodeRuntime.cs').read_text(encoding='utf-8')
for token in ['AddSingleton<VisionNodeRuntime>()','AddSingleton<VisionPipelineExecutor>()','AddTransient<VisionPipelineStep>()']:
    if token not in host_registration: fail(f'Hybrid runtime DI missing: {token}')
if 'data.PipelineSegments = compiled.PipelineSegments' not in runner: fail('Runner does not attach compiled pipeline segments to run data')
if 'VisionNodeRuntime' not in pipeline or 'foreach (var nodeId in nodeIds)' not in pipeline: fail('VisionPipelineExecutor does not execute node-level runtime semantics')
if 'data.AddReport' not in node_runtime or 'data.AddOverlays' not in node_runtime or 'data.BeforeNode' not in node_runtime:
    fail('VisionNodeRuntime is missing report/overlay/debug semantics')
ok('V0.21 hybrid Workflow Core + VisionPipelineExecutor semantics retained')

# V0.29 recursive structured workflow IR
structured_ir=(ROOT/'backend/src/VisionStudio.Engine/Runtime/StructuredWorkflowIr.cs').read_text(encoding='utf-8')
workflow_data=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionWorkflowData.cs').read_text(encoding='utf-8')
recursive_tests=(ROOT/'backend/tests/VisionStudio.Engine.Tests/RecursiveStructuredWorkflowTests.cs').read_text(encoding='utf-8')
frontend_app=(ROOT/'frontend/src/App.tsx').read_text(encoding='utf-8')
for token in ['StructuredWorkflowIrBuilder','FindNearestCommonJoin','excludedBoundaryJoinId','StructuredControlRegion','ValidateNoCrossBranchData']:
    if token not in structured_ir: fail(f'V0.29 structured IR token missing: {token}')
for token in ['new StructuredWorkflowIrBuilder','CompileSequence','GetBranchCondition','ControlRegions']:
    if token not in compiler: fail(f'V0.29 recursive compiler token missing: {token}')
if 'data.BranchCondition == false' in compiler or '["Condition"] = "data.BranchCondition"' in compiler:
    fail('V0.29 compiler still consumes global BranchCondition')
for token in ['SetBranchCondition','GetBranchCondition','SnapshotControlFlowDecisions','MarkParallelBranches']:
    if token not in workflow_data: fail(f'V0.29 keyed branch-state token missing: {token}')
for token in ['Compiler_CompilesNestedIfWithDistinctControlRegionsAndKeyedConditions','Compiler_CompilesParallelNestedInsideIf','WorkflowData_KeepsNestedBranchDecisionsIndependent']:
    if token not in recursive_tests: fail(f'V0.29 recursive regression test missing: {token}')
for token in ['controlFlowDecisions','branch-taken','branch-skipped','Nested Flow Demo']:
    if token not in frontend_app and token not in (ROOT/'frontend/src/styles.css').read_text(encoding='utf-8'):
        fail(f'V0.29 frontend branch visualization token missing: {token}')
ok('V0.29 recursive structured IR + keyed branch state + branch visualization present')


# V0.30 sandboxed File Camera + Media Library
media_service=(ROOT/'backend/src/VisionStudio.Api/Media/MediaLibraryService.cs').read_text(encoding='utf-8')
media_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/MediaEndpoints.cs').read_text(encoding='utf-8')
file_camera=(ROOT/'backend/src/VisionStudio.Engine/Camera/FileCameraDevice.cs').read_text(encoding='utf-8')
media_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/MediaLibraryTests.cs').read_text(encoding='utf-8')
camera_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/CameraEndpoints.cs').read_text(encoding='utf-8')
frontend_camera=(ROOT/'frontend/src/components/CameraPanel.tsx').read_text(encoding='utf-8')
for token in ['media://','ResolveSource','NormalizeRelative','EnsureNoReparseTraversal','MaxImportBytes','MaxBatchBytes','MaxLibraryBytes','EnsureImportCapacity','ValidateImageSignature','ImportAsync','ListCollections','ListItems']:
    if token not in media_service: fail(f'V0.30 media sandbox token missing: {token}')
for token in ['/api/media/status','/api/media/collections','/api/media/items','/api/media/import','/api/media/preview']:
    if token not in media_endpoints: fail(f'V0.30 media endpoint missing: {token}')
for token in ['RequireRateLimiting("media-import")','MaxBatchBytes']:
    if token not in media_endpoints: fail(f'V0.30 bounded media import endpoint token missing: {token}')
for token in ['MultipartBodyLengthLimit','KestrelServerOptions','media-import']:
    if token not in host_registration: fail(f'V0.30 media host upload guard missing: {token}')
if 'request.Path.Trim()' in camera_endpoints or 'new FileCameraDevice(request' in camera_endpoints:
    fail('V0.30 File Camera endpoint still forwards arbitrary request filesystem paths')
for token in ['ICameraSourceProvenanceProvider','GetSourceProvenance','IncrementalHash','SearchOption.AllDirectories']:
    if token not in file_camera: fail(f'V0.30 File Camera provenance/collection token missing: {token}')
for token in ['SourceFingerprint','sourceProvenance?.ContentSha256']:
    if token not in api_sources: fail(f'V0.30 dependency provenance token missing: {token}')
for token in ['ResolveSource_RejectsAbsoluteAndTraversalPaths','Import_StoresOnlyLogicalMediaReferenceInsideRoot','FileCameraSourceFingerprint_ChangesWhenMediaBytesChange','RuntimeManifest_ReportsFileCameraMediaContentDrift','Import_RejectsContentThatDoesNotMatchImageExtension']:
    if token not in media_tests: fail(f'V0.30 media regression test missing: {token}')
for token in ['Upload.Dragger','Media Library','media://','NG','Use as File Camera source']:
    if token not in frontend_camera: fail(f'V0.30 media UI token missing: {token}')
ok('V0.30 File Camera sandbox + Media Library + content provenance present')


# V0.31 adapter/manual hardware provenance + physical dependency lock
hardware_contracts=(ROOT/'backend/src/VisionStudio.Engine/Provenance/HardwareProvenance.cs').read_text(encoding='utf-8')
hardware_store=(ROOT/'backend/src/VisionStudio.Api/Provenance/HardwareProvenanceStore.cs').read_text(encoding='utf-8')
hardware_service=(ROOT/'backend/src/VisionStudio.Api/Provenance/HardwareProvenanceService.cs').read_text(encoding='utf-8')
hardware_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/HardwareProvenanceEndpoints.cs').read_text(encoding='utf-8')
hardware_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/HardwareProvenanceTests.cs').read_text(encoding='utf-8')
hardware_frontend=(ROOT/'frontend/src/components/HardwareProvenancePanel.tsx').read_text(encoding='utf-8')
for token in ['HardwareProvenanceData','IHardwareProvenanceProvider','SerialNumber','FirmwareVersion','ControllerVersion','ProgramHash']:
    if token not in hardware_contracts: fail(f'V0.31 hardware contract token missing: {token}')
for token in ['hardware-provenance.json','UpsertAsync','DeleteAsync']:
    if token not in hardware_store: fail(f'V0.31 hardware store token missing: {token}')
for token in ['adapter:','+declared','MissingRecommended','Fingerprint','fixtureRevision']:
    if token not in hardware_service and token not in hardware_frontend: fail(f'V0.31 hardware provenance token missing: {token}')
for token in ['/api/hardware-provenance','EnsureDependencyMutationAllowed','RequireEngineer']:
    if token not in hardware_endpoints: fail(f'V0.31 hardware endpoint token missing: {token}')
for token in ['manifestSchemaVersion','CameraHardware','DeviceHardware','RobotHardware','hardwareFingerprint']:
    if token not in api_sources: fail(f'V0.31 RuntimeDependencyManifest hardware token missing: {token}')
for token in ['VirtualCamera_ProvidesAdapterHardwareProvenance','PlantDeclaration_MergesExternalLensAndFirmwareAndChangesFingerprint','RuntimeManifest_DetectsHardwareDeclarationDrift','LegacyManifestSchema_IsRejectedUntilRepublishedWithHardwareIdentity']:
    if token not in hardware_tests: fail(f'V0.31 hardware regression test missing: {token}')
for token in ['Hardware Provenance','Plant declaration','firmwareVersion','programHash']:
    if token not in hardware_frontend: fail(f'V0.31 hardware UI token missing: {token}')
ok('V0.31 adapter + declared hardware provenance and physical dependency drift guard present')


# V0.32 live vendor provenance probes (Basler pylon / Hikrobot MVS / ABB RWS)
vendor_contracts=(ROOT/'backend/src/VisionStudio.Api/Provenance/VendorProvenance.cs').read_text(encoding='utf-8')
vendor_base=(ROOT/'backend/src/VisionStudio.Api/Provenance/VendorProbeBase.cs').read_text(encoding='utf-8')
basler_probe=(ROOT/'backend/src/VisionStudio.Api/Provenance/BaslerPylonProvenanceProbe.cs').read_text(encoding='utf-8')
hik_probe=(ROOT/'backend/src/VisionStudio.Api/Provenance/HikrobotMvsProvenanceProbe.cs').read_text(encoding='utf-8')
abb_probe=(ROOT/'backend/src/VisionStudio.Api/Provenance/AbbRwsProvenanceProbe.cs').read_text(encoding='utf-8')
for token in ['IAsyncHardwareProvenanceProvider']:
    if token not in hardware_contracts: fail(f'V0.32 async hardware contract missing: {token}')
for token in ['IVendorHardwareProvenanceProbe','VendorProvenanceProbeRegistry','RequiredForProduction','BaslerPylonProbeOptions','HikrobotMvsProbeOptions','AbbRwsProbeOptions']:
    if token not in vendor_contracts: fail(f'V0.32 vendor probe contract missing: {token}')
for token in ['CachedVendorProvenanceProbe','liveProbeState','SemaphoreSlim']:
    if token not in vendor_base: fail(f'V0.32 cached vendor probe behavior missing: {token}')
for token in ['Basler.Pylon.CameraFinder','DeviceFirmwareVersion','SerialNumber','ModelName']:
    if token not in basler_probe: fail(f'V0.32 Basler pylon probe token missing: {token}')
for token in ['MvCamCtrl.NET.MyCamera','MV_CC_EnumDevices_NET','MV_GIGE_DEVICE_INFO','MV_USB3_DEVICE_INFO','DeviceFirmwareVersion']:
    if token not in hik_probe: fail(f'V0.32 Hikrobot MVS probe token missing: {token}')
for token in ['ctrl/identity','rw/system','rw/rapid/modules','HashRapidProgramAsync','PasswordEnvironmentVariable']:
    if token not in abb_probe: fail(f'V0.32 ABB RWS probe token missing: {token}')
for token in ['CaptureForProductionAsync','LiveProbeConfigured','LiveProbeRequired','live:']:
    if token not in hardware_service: fail(f'V0.32 live hardware service integration missing: {token}')
for token in ['VendorProvenanceBootstrapHostedService','BaslerPylonProvenanceProbe','HikrobotMvsProvenanceProbe','AbbRwsProvenanceProbe','AssetExists','Required ']:
    if token not in host_bootstrap: fail(f'V0.32 vendor probe bootstrap missing: {token}')
if 'AddHostedService<VendorProvenanceBootstrapHostedService>()' not in host_registration:
    fail('V0.32 vendor provenance bootstrap is not ordered before Production AutoStart')
if 'CaptureForProductionAsync' not in (ROOT/'backend/src/VisionStudio.Api/RuntimeDependencies.cs').read_text(encoding='utf-8'):
    fail('V0.32 required live probes do not guard RuntimeDependencyManifest capture')
for token in ['RequiredLiveProbe_Unavailable_BlocksProductionCapture','OptionalLiveProbe_Unavailable_AllowsProductionCaptureWithDescriptorFallback','LiveProbeData_IsMergedIntoAutomaticChannel_AndCannotBeMaskedByDescriptor','AbbRwsProbe_ReadsControllerRobotWareAndHashesRapidProgram','RequiredVendorProbe_WithUnknownAsset_FailsBootstrap']:
    if token not in hardware_tests: fail(f'V0.32 vendor provenance regression test missing: {token}')
frontend_package = json.loads((ROOT/'frontend/package.json').read_text(encoding='utf-8'))
vendor_appsettings = json.loads((ROOT/'backend/src/VisionStudio.Api/appsettings.json').read_text(encoding='utf-8'))
if 'VendorProvenance' not in vendor_appsettings or not all(k in vendor_appsettings['VendorProvenance'] for k in ['CacheSeconds','Basler','Hikrobot','AbbRws']):
    fail('V0.32 VendorProvenance configuration section missing')
ok('V0.32 live Basler/Hikrobot/ABB provenance probes + required-production guard present')


# V0.33 real Basler + Hikrobot acquisition adapters
camera_contracts=(ROOT/'backend/src/VisionStudio.Engine/Camera/CameraContracts.cs').read_text(encoding='utf-8')
camera_worker=(ROOT/'backend/src/VisionStudio.Engine/Camera/CameraAcquisitionWorker.cs').read_text(encoding='utf-8')
basler_adapter=(ROOT/'backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraDevice.cs').read_text(encoding='utf-8')
basler_provider=(ROOT/'backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraAdapterProvider.cs').read_text(encoding='utf-8')
hik_adapter=(ROOT/'backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraDevice.cs').read_text(encoding='utf-8')
hik_provider=(ROOT/'backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraAdapterProvider.cs').read_text(encoding='utf-8')
camera_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/CameraEndpoints.cs').read_text(encoding='utf-8')
camera_options=(ROOT/'backend/src/VisionStudio.Api/CameraAdapterOptions.cs').read_text(encoding='utf-8')
camera_ui=(ROOT/'frontend/src/components/CameraPanel.tsx').read_text(encoding='utf-8')
camera_tests=(ROOT/'backend/tests/VisionStudio.Engine.Tests/CameraAcquisitionTests.cs').read_text(encoding='utf-8')
for token in ['CameraOutputPixelFormat','ICameraTelemetryProvider','CameraDiscoveredDevice','ICameraAdapterProvider','ExternalTriggerSource']:
    if token not in camera_contracts: fail(f'V0.33 camera contract missing: {token}')
for token in ['CameraFrameTimeoutException','FrameTimeouts','DriverDroppedFrames','ReconnectAsync']:
    if token not in camera_worker and token not in camera_contracts: fail(f'V0.33 acquisition worker telemetry/reconnect token missing: {token}')
for token in ['RetrieveResult','PixelDataConverter','SkippedImageCount','IDisposable disposable','TriggerSource','ExternalTriggerSource']:
    if token not in basler_adapter: fail(f'V0.33 Basler acquisition token missing: {token}')
for token in ['CameraFinder','DiscoverAsync','BaslerPylonCameraDevice']:
    if token not in basler_provider and token not in (ROOT/'backend/src/VisionStudio.Camera.Basler/BaslerSdkReflection.cs').read_text(encoding='utf-8'):
        fail(f'V0.33 Basler discovery/provider token missing: {token}')
for token in ['MV_CC_StartGrabbing_NET','MV_CC_GetImageForBGR_NET','nFrameNum','DriverDroppedFrames','TriggerSoftware','ExternalTriggerSource']:
    if token not in hik_adapter and token != 'DriverDroppedFrames': fail(f'V0.33 Hikrobot acquisition token missing: {token}')
for token in ['MV_CC_EnumDevices_NET','DiscoverAsync','HikrobotMvsCameraDevice']:
    if token not in hik_provider and token not in (ROOT/'backend/src/VisionStudio.Camera.Hikrobot/HikrobotSdkReflection.cs').read_text(encoding='utf-8'):
        fail(f'V0.33 Hikrobot discovery/provider token missing: {token}')
for token in ['/api/cameras/adapters','/api/cameras/discover','/api/cameras/vendor','ICameraAdapterProvider']:
    if token not in camera_endpoints: fail(f'V0.33 camera endpoint missing: {token}')
for token in ['CameraAdapters','Configured vendor camera requires Driver and Id','requires unavailable adapter']:
    if token not in host_bootstrap and token != 'CameraAdapters': fail(f'V0.33 fail-closed camera bootstrap missing: {token}')
if 'CameraAdapters' not in host_registration or 'BaslerPylonCameraAdapterProvider' not in host_registration or 'HikrobotMvsCameraAdapterProvider' not in host_registration:
    fail('V0.33 camera adapter DI/configuration missing')
for token in ['Discover','Register','Driver Drops','Ring Overwrites','Host Output','External Trigger Line']:
    if token not in camera_ui: fail(f'V0.33 camera UI token missing: {token}')
for token in ['FrameTimeout_IsCountedWithoutReconnect','AdapterTelemetry_IsSurfacedSeparatelyFromRingOverwrite','SoftwareTrigger_InvokesAdapterBeforePublishingFrame','ExternalHardwareTrigger_CannotBeSynthesizedByHost']:
    if token not in camera_tests: fail(f'V0.33 camera acquisition regression test missing: {token}')
camera_appsettings = json.loads((ROOT/'backend/src/VisionStudio.Api/appsettings.json').read_text(encoding='utf-8'))
if 'CameraAdapters' not in camera_appsettings or not all(k in camera_appsettings['CameraAdapters'] for k in ['BaslerAssemblyPath','HikrobotAssemblyPath','Cameras']):
    fail('V0.33 CameraAdapters configuration section missing')
# Vendor binaries are site-installed; source/runtime package must not include them.
vendor_dlls=[x for x in ROOT.rglob('*.dll') if any(k in x.name.lower() for k in ['basler','pylon','mvcam','hikrobot','mvs'])]
if vendor_dlls: fail(f'V0.33 package contains vendor camera DLLs: {[str(x.relative_to(ROOT)) for x in vendor_dlls]}')
ok('V0.33 runtime-loaded Basler/Hikrobot acquisition + trigger/buffer/telemetry path present')


# V0.34 camera commissioning profiles + normalized feature browser
camera_profile_contracts=(ROOT/'backend/src/VisionStudio.Engine/Camera/CameraContracts.cs').read_text(encoding='utf-8')
camera_profile_hash=(ROOT/'backend/src/VisionStudio.Engine/Camera/CameraFeatureProfiles.cs').read_text(encoding='utf-8')
basler_features=(ROOT/'backend/src/VisionStudio.Camera.Basler/BaslerCameraFeatures.cs').read_text(encoding='utf-8')
hik_features=(ROOT/'backend/src/VisionStudio.Camera.Hikrobot/HikrobotCameraFeatures.cs').read_text(encoding='utf-8')
profile_store=(ROOT/'backend/src/VisionStudio.Api/CameraFeatureProfileStore.cs').read_text(encoding='utf-8')
profile_schema=(ROOT/'backend/src/VisionStudio.Api/SchemaMigrations.cs').read_text(encoding='utf-8')
profile_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/CameraFeatureProfileEndpoints.cs').read_text(encoding='utf-8')
profile_tests=(ROOT/'backend/tests/VisionStudio.Engine.Tests/CameraFeatureProfileTests.cs').read_text(encoding='utf-8')
profile_store_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/CameraFeatureProfileStoreTests.cs').read_text(encoding='utf-8')
for token in ['CameraFeatureDescriptor','CameraCommissioningProfile','CameraCommissioningCapabilities','ICameraFeatureProvider','CommissioningProfileHash']:
    if token not in camera_profile_contracts: fail(f'V0.34 commissioning contract missing: {token}')
for token in ['SHA256.HashData','AdvancedFeatures','SchemaVersion != 1','PacketSizeBytes']:
    if token not in camera_profile_hash: fail(f'V0.34 profile canonicalization token missing: {token}')
for token in ['GevSCPSPacketSize','GevSCPD','TriggerDelay','LineDebouncerTime','LineSource','PtpEnable','ActionDeviceKey']:
    if token not in basler_features: fail(f'V0.34 Basler commissioning feature missing: {token}')
for token in ['GevSCPSPacketSize','GevSCPD','TriggerDelay','LineDebouncerTime','StrobeEnable','StrobeLineDuration','GevIEEE1588','ActionDeviceKey']:
    if token not in hik_features: fail(f'V0.34 Hikrobot commissioning feature missing: {token}')
for token in ['camera_feature_profiles','profile_hash','UpsertAsync','DeleteAsync']:
    if token not in profile_store and token not in profile_schema: fail(f'V0.34 camera profile store/schema token missing: {token}')
for token in ['/api/cameras/{id}/commissioning','/api/cameras/{id}/features','/api/camera-feature-profiles/capture','/api/camera-feature-profiles/import','apply/{cameraId}','EnsureDependencyMutationAllowed']:
    if token not in profile_endpoints: fail(f'V0.34 commissioning endpoint missing: {token}')
for token in ['commissioningProfileHash','GetCommissioningProfileHash']:
    if token not in (ROOT/'backend/src/VisionStudio.Api/RuntimeDependencies.cs').read_text(encoding='utf-8'): fail(f'V0.34 dependency profile hash missing: {token}')
for token in ['FeatureProfileId','ApplyCommissioningProfileAsync','CameraFeatureProfileStore']:
    if token not in host_bootstrap and token not in camera_options: fail(f'V0.34 startup profile application missing: {token}')
for token in ['Trigger / Strobe Commissioning','Packet Size','Inter-Packet Delay','Feature Browser','Save Current','Export JSON','Import JSON']:
    if token not in camera_ui: fail(f'V0.34 commissioning UI token missing: {token}')
for token in ['CameraFeatureProfileHash_IsStableAcrossAdvancedFeatureOrdering','CameraFeatureProfileNormalize_ClampsTransportAndTimingValues']:
    if token not in profile_tests: fail(f'V0.34 profile regression test missing: {token}')
for token in ['SchemaV5_CreatesCameraFeatureProfilesTable','CameraFeatureProfileStore_RoundTripsPortableProfileAndHash']:
    if token not in profile_store_tests: fail(f'V0.34 profile store regression test missing: {token}')
if frontend_package.get('version') != '0.63.0' or 'v63' not in frontend_package.get('name',''):
    fail('Frontend package identity is not V0.63')
ok('V0.34 normalized camera commissioning profile + feature browser + SQLite profile persistence present')

# V0.22 Abstractions-only plugin SDK + runtime lifetime/thread-safety enforcement
abstractions_csproj=(ROOT/'backend/src/VisionStudio.Abstractions/VisionStudio.Abstractions.csproj').read_text(encoding='utf-8')
engine_csproj=(ROOT/'backend/src/VisionStudio.Engine/VisionStudio.Engine.csproj').read_text(encoding='utf-8')
sample_csproj=(ROOT/'backend/src/VisionStudio.Plugin.Sample/VisionStudio.Plugin.Sample.csproj').read_text(encoding='utf-8')
plugin_contracts=(ROOT/'backend/src/VisionStudio.Abstractions/PluginContracts.cs').read_text(encoding='utf-8')
execution_contracts=(ROOT/'backend/src/VisionStudio.Abstractions/ExecutionContracts.cs').read_text(encoding='utf-8')
plugin_registry=(ROOT/'backend/src/VisionStudio.Engine/PluginSdk.cs').read_text(encoding='utf-8')
plugin_manager=(ROOT/'backend/src/VisionStudio.Api/PluginManager.cs').read_text(encoding='utf-8')
node_dispatcher=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionNodeDispatcher.cs').read_text(encoding='utf-8')
if '<PackageReference' in abstractions_csproj: fail('VisionStudio.Abstractions must not have third-party package references')
if 'VisionStudio.Abstractions' not in engine_csproj: fail('Engine does not reference VisionStudio.Abstractions')
if 'VisionStudio.Abstractions' not in sample_csproj or 'VisionStudio.Engine' in sample_csproj:
    fail('Sample plugin must reference Abstractions only, not Engine')
for token in ['VisionExecutorLifetime','VisionExecutorConcurrency','Func<IVisionNodeExecutor> Factory','IVisionPlugin']:
    if token not in plugin_contracts: fail(f'Missing plugin contract token: {token}')
for token in ['IVisionNodeExecutor','NodeExecutionContext','NodeExecutorResult']:
    if token not in execution_contracts: fail(f'Missing executor abstraction token: {token}')
for token in ['RegisterFactory','VisionExecutorConcurrency.Serialized','SemaphoreSlim','SupportsParallel == false']:
    if token not in plugin_registry: fail(f'Missing runtime plugin enforcement token: {token}')
if 'Transient' not in plugin_contracts or 'Singleton' not in plugin_contracts: fail('Plugin lifetime enum is incomplete')
if 'registration.ExecuteAsync' not in node_dispatcher: fail('VisionNodeDispatcher bypasses host-managed registration execution')
if 'typeof(VisionNodeRegistry)' in plugin_manager or 'VisionStudio.Engine.Runtime' in plugin_manager:
    fail('Plugin loader shares Engine/runtime types with plugins')
if 'typeof(IVisionPlugin)' not in plugin_manager or 'typeof(VisionPluginNode)' not in plugin_manager:
    fail('Plugin loader does not share Abstractions contract types')
sample_source=(ROOT/'backend/src/VisionStudio.Plugin.Sample/SampleMathPlugin.cs').read_text(encoding='utf-8')
if 'using VisionStudio.Engine' in sample_source: fail('Sample plugin source still imports Engine')
if 'VisionPluginNode.PerNode' not in sample_source: fail('Sample plugin does not demonstrate SDK 2.0 PerNode lifetime')
for p in (ROOT/'backend/src/VisionStudio.Abstractions').glob('*.cs'):
    txt=p.read_text(encoding='utf-8')
    for forbidden in ['using OpenCvSharp', 'using WorkflowCore', 'using NModbus', 'using S7.Net', 'using VisionStudio.Engine']:
        if forbidden in txt: fail(f'Abstractions leaks implementation dependency {forbidden}: {p.name}')
ok('V0.22 Abstractions-only plugin boundary + factory/lifetime/concurrency semantics present')
contract = json.loads((ROOT/'contracts/api-required-endpoints.json').read_text(encoding='utf-8'))
if contract.get('version') != 'v0.63': fail(f"API contract version is not v0.63: {contract.get('version')}")
seen_contract=set()
for item in contract.get('required', []):
    pair=(item.get('method','').lower(), item.get('path',''))
    if pair in seen_contract: fail(f'Duplicate API contract entry: {pair}')
    seen_contract.add(pair)
    method=pair[0].capitalize()
    if f'app.Map{method}(\"{pair[1]}\"' not in endpoint_sources:
        fail(f'Contract endpoint is not mapped in endpoint modules: {pair[0].upper()} {pair[1]}')
ok(f'API contract endpoints: {len(seen_contract)}')

# Full public route inventory. This protects auxiliary endpoints that are not part of the minimum production contract.
route_pairs = re.findall(r'\.Map(Get|Post|Put|Delete|Patch)\(\"([^\"]+)\"', endpoint_sources)
normalized_routes = [(method.lower(), path) for method, path in route_pairs]
if len(normalized_routes) != len(set(normalized_routes)):
    duplicates = sorted({x for x in normalized_routes if normalized_routes.count(x) > 1})
    fail(f'Duplicate endpoint mappings: {duplicates}')
if len(set(normalized_routes)) != 223:
    fail(f'Full route inventory changed unexpectedly: expected 223, got {len(set(normalized_routes))}')
ok(f'Full endpoint inventory: {len(set(normalized_routes))}')

# V0.23 host architecture checks retained
if len(program.splitlines()) > 80:
    fail(f'Program.cs remains a God file: {len(program.splitlines())} lines')
for token in ['AddProblemDetails()', 'AddExceptionHandler<ApiExceptionHandler>()']:
    if token not in host_registration: fail(f'ProblemDetails infrastructure missing: {token}')
if 'app.UseExceptionHandler();' not in program: fail('Global exception handler middleware is not enabled')
exception_handler=(ROOT/'backend/src/VisionStudio.Api/Infrastructure/ApiExceptionHandler.cs').read_text(encoding='utf-8')
for status in ['Status400BadRequest','Status404NotFound','Status409Conflict','Status503ServiceUnavailable','Status500InternalServerError']:
    if status not in exception_handler: fail(f'ProblemDetails mapper missing {status}')
for token in ['correlationId','X-Correlation-ID']:
    if token not in exception_handler: fail(f'ProblemDetails correlation token missing: {token}')
if 'catch (Exception ex) { return Results.BadRequest' in endpoint_sources:
    fail('Endpoint modules still contain catch-all Exception -> 400 mapping')
for token in ['StorageMigrationHostedService','RuntimeBootstrapHostedService','ProductionRuntimeHostedService','TraceRetentionHostedService']:
    if f'AddHostedService<{token}>()' not in host_registration: fail(f'Hosted service registration missing: {token}')
order=[host_registration.find(f'AddHostedService<{x}>()') for x in ['StorageMigrationHostedService','RuntimeBootstrapHostedService','ProductionRuntimeHostedService','TraceRetentionHostedService']]
if any(x < 0 for x in order) or order != sorted(order): fail('Hosted startup order is not migrate -> bootstrap -> production -> retention')
if 'Cors:AllowedOrigins' not in host_registration: fail('CORS origins are not configuration-backed')
if 'app.MapVisionStudioApi();' not in program: fail('Modular endpoint root mapping is missing')
ok(f'V0.23 modular host retained: Program.cs={len(program.splitlines())} lines, endpoint modules={len(list((ROOT/"backend/src/VisionStudio.Api/Endpoints").glob("*.cs")))}')

# V0.24 RBAC / sessions / audit
security_models=(ROOT/'backend/src/VisionStudio.Api/Security/SecurityModels.cs').read_text(encoding='utf-8')
security_store=(ROOT/'backend/src/VisionStudio.Api/Security/SecurityStore.cs').read_text(encoding='utf-8')
auth_handler=(ROOT/'backend/src/VisionStudio.Api/Security/VisionStudioAuthenticationHandler.cs').read_text(encoding='utf-8')
audit_source=(ROOT/'backend/src/VisionStudio.Api/Security/Audit.cs').read_text(encoding='utf-8')
security_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/SecurityEndpoints.cs').read_text(encoding='utf-8')
frontend_security=(ROOT/'frontend/src/security.tsx').read_text(encoding='utf-8')
for token in ['SecurityRoles.Operator','SecurityRoles.Engineer','SecurityRoles.Administrator','SetFallbackPolicy','AddAuthentication','AddRateLimiter','RateLimitPartition.GetFixedWindowLimiter']:
    if token not in host_registration: fail(f'V0.24 security registration missing: {token}')
# V0.28 moved DDL into an explicit ordered migration source.
schema_migration_source=(ROOT/'backend/src/VisionStudio.Api/SchemaMigrations.cs').read_text(encoding='utf-8')
security_schema=(ROOT/'backend/src/VisionStudio.Api/SqliteMetadataDatabase.cs').read_text(encoding='utf-8') + '\n' + schema_migration_source
for token in ['security_users','security_sessions','audit_events','CurrentVersion = 19']:
    if token not in security_schema: fail(f'V0.24 security schema missing: {token}')
for token in ['Rfc2898DeriveBytes.Pbkdf2','CryptographicOperations.FixedTimeEquals','SHA256.HashData','At least one enabled Administrator must remain']:
    if token not in security_store: fail(f'Credential/session hardening missing: {token}')
if 'password TEXT' in security_schema.lower() or 'password TEXT' in security_store:
    fail('Plaintext password persistence detected')
if 'CookieName' not in security_models or 'vs_session' not in security_models: fail('HttpOnly session cookie option is missing')
for token in ['Request.Cookies[_security.CookieName]','Bearer ','HandleChallengeAsync','HandleForbiddenAsync']:
    if token not in auth_handler: fail(f'Authentication handler missing: {token}')
for token in ['AuditActionMetadata','audit_events','FindFirstValue','RouteTarget']:
    if token not in audit_source: fail(f'Audit implementation missing: {token}')
for token in ['/api/auth/bootstrap','/api/auth/login','/api/auth/me','/api/security/users','/api/audit']:
    if token not in security_endpoints: fail(f'Security endpoint missing: {token}')
if security_endpoints.count('RequireRateLimiting("auth")') < 2: fail('Bootstrap/login rate limiting is not applied')
for token in ['RequireOperator(','RequireEngineer(','RequireAdministrator(']:
    if token not in endpoint_sources: fail(f'Role-protected mutation mappings missing: {token}')
if 'app.UseAuthentication();' not in program or 'app.UseAuthorization();' not in program or 'app.UseMiddleware<AuditMiddleware>();' not in program:
    fail('Authentication/authorization/audit middleware pipeline is incomplete')
if '/api/auth/' not in (ROOT/'backend/src/VisionStudio.Api/Infrastructure/RuntimeOnlyMiddleware.cs').read_text(encoding='utf-8'):
    fail('Runtime-only host blocks security/session administration')
for token in ['First-time security setup','HttpOnly cookie','/api/auth/me','Sign out']:
    if token not in frontend_security: fail(f'Frontend security gate missing: {token}')
security_settings=json.loads((ROOT/'backend/src/VisionStudio.Api/appsettings.json').read_text(encoding='utf-8'))
if not security_settings.get('Security', {}).get('Enabled', False): fail('Security is not enabled by default')
ok('V0.24 RBAC + PBKDF2 local users + revocable sessions + audit trail present')

# V0.25 RuntimeDependencyManifest / production provenance
trace_store_dependency=(ROOT/'backend/src/VisionStudio.Api/TraceabilityStore.cs').read_text(encoding='utf-8')
dependency_source=(ROOT/'backend/src/VisionStudio.Api/RuntimeDependencies.cs').read_text(encoding='utf-8')
job_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/JobEndpoints.cs').read_text(encoding='utf-8')
trace_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/TraceEndpoints.cs').read_text(encoding='utf-8')
production_source=(ROOT/'backend/src/VisionStudio.Api/ProductionRuntime.cs').read_text(encoding='utf-8')
job_store_source=(ROOT/'backend/src/VisionStudio.Api/JobStore.cs').read_text(encoding='utf-8')
for token in ['RuntimeDependencyManifest','EngineRuntimeDependency','PluginRuntimeDependency','CameraRuntimeDependency','DeviceRuntimeDependency','RobotRuntimeDependency','CalibrationRuntimeDependency','ManifestHash','CaptureAsync','ValidateAsync','ValidateOrThrowAsync']:
    if token not in dependency_source: fail(f'Runtime dependency manifest implementation missing: {token}')
for token in ['registration.Source','AssemblySha256','cameraId','deviceId','robotId','assetId','assetVersion','ConfigurationHash']:
    if token not in dependency_source + plugin_manager: fail(f'Runtime dependency capture token missing: {token}')
for token in ['runtime_dependency_manifests','published_dependency_manifest_hash','dependency_manifest_hash','CurrentVersion = 19']:
    if token not in security_schema: fail(f'V0.25 dependency schema missing: {token}')
for token in ['StoreManifestAsync','GetPublishedSnapshotAsync','dependency_manifest_hash','PublishedDependencyManifestHash']:
    if token not in job_store_source: fail(f'Job publication dependency persistence missing: {token}')
for token in ['GetPublishedSnapshotAsync','ValidateOrThrowAsync','PROD-DEPENDENCY-DRIFT','EnsureDependencyMutationAllowed','LockedDependencyManifestHash']:
    if token not in production_source: fail(f'Production dependency drift guard missing: {token}')
for token in ['/api/jobs/{id}/dependencies','/api/jobs/{id}/dependencies/validate']:
    if token not in job_endpoints: fail(f'Job dependency endpoint missing: {token}')
if '/api/traces/{runId}/dependencies' not in trace_endpoints: fail('Trace dependency provenance endpoint missing')
if 'GetDependencyManifestAsync' not in trace_store_dependency: fail('TraceabilityStore cannot resolve dependency manifest')
if 'AddSingleton<RuntimeDependencyManifestService>()' not in host_registration: fail('RuntimeDependencyManifestService is not registered')
for rel in ['backend/src/VisionStudio.Api/Endpoints/CameraEndpoints.cs','backend/src/VisionStudio.Api/Endpoints/DeviceEndpoints.cs','backend/src/VisionStudio.Api/Endpoints/RobotEndpoints.cs']:
    if 'EnsureDependencyMutationAllowed' not in (ROOT/rel).read_text(encoding='utf-8'): fail(f'Production dependency mutation guard missing from {rel}')
frontend_types=(ROOT/'frontend/src/types.ts').read_text(encoding='utf-8')
production_panel=(ROOT/'frontend/src/components/ProductionPanel.tsx').read_text(encoding='utf-8')
job_panel=(ROOT/'frontend/src/components/JobPanel.tsx').read_text(encoding='utf-8')
if 'lockedDependencyManifestHash' not in frontend_types or 'Dependency hash' not in production_panel: fail('Frontend production dependency lock status missing')
if 'Validate Dependencies' not in job_panel or 'publishedDependencyManifestHash' not in job_panel: fail('Frontend Job dependency validation UI missing')
ok('V0.25 RuntimeDependencyManifest + publication/trace provenance + production drift guard present')


# Retained V0.35 multi-camera PTP + Action Command synchronization runtime
sync_contracts=(ROOT/'backend/src/VisionStudio.Engine/Camera/CameraContracts.cs').read_text(encoding='utf-8')
sync_runtime=(ROOT/'backend/src/VisionStudio.Api/CameraSynchronization.cs').read_text(encoding='utf-8')
sync_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/CameraSynchronizationEndpoints.cs').read_text(encoding='utf-8')
basler_sync=(ROOT/'backend/src/VisionStudio.Camera.Basler/BaslerCameraSynchronization.cs').read_text(encoding='utf-8')
hik_sync=(ROOT/'backend/src/VisionStudio.Camera.Hikrobot/HikrobotCameraSynchronization.cs').read_text(encoding='utf-8')
basler_adapter=(ROOT/'backend/src/VisionStudio.Camera.Basler/BaslerPylonCameraAdapterProvider.cs').read_text(encoding='utf-8')
hik_adapter=(ROOT/'backend/src/VisionStudio.Camera.Hikrobot/HikrobotMvsCameraAdapterProvider.cs').read_text(encoding='utf-8')
sync_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/CameraSynchronizationTests.cs').read_text(encoding='utf-8')
for token in ['ICameraSynchronizationProvider','ICameraActionCommandProvider','CameraTimeSynchronizationStatus','CameraActionCommandRequest','DeviceTimestampNs','DeviceTickFrequencyHz','ScheduledRequiresTickFrequency']:
    if token not in sync_contracts: fail(f'V0.35 camera synchronization contract missing: {token}')
for token in ['camera_sync_groups','CameraSynchronizationGroupStore','CameraSynchronizationService','MaxTriggerSkewUs','ScheduledDeviceTimeNs','device-ptp']:
    if token not in sync_runtime and token not in schema_migration_source: fail(f'V0.35 sync runtime/schema token missing: {token}')
for token in ['/api/camera-sync/groups','/status','/trigger','/schedule','EnsureDependencyMutationAllowed']:
    if token not in sync_endpoints: fail(f'V0.35 sync endpoint token missing: {token}')
for token in ['GevIEEE1588DataSetLatch','GevIEEE1588OffsetFromMaster','PtpServoStatus']:
    if token not in basler_sync: fail(f'V0.35 Basler PTP diagnostic token missing: {token}')
for token in ['ActionCommandTrigger','Schedule','Issue']:
    if token not in basler_adapter: fail(f'V0.35 Basler Action Command token missing: {token}')
for token in ['MV_GIGE_IssueActionCommand_NET','MV_ACTION_CMD_INFO','bActionTimeEnable','nActionTime','DeviceTickFrequencyHz']:
    if token not in hik_adapter: fail(f'V0.35 Hikrobot Action Command token missing: {token}')
for token in ['GevIEEE1588','CameraPtpClockState']:
    if token not in hik_sync: fail(f'V0.35 Hikrobot PTP token missing: {token}')
for token in ['synchronizationGroupHashes','manifestSchemaVersion']:
    if token not in dependency_source: fail(f'V0.35 RuntimeDependencyManifest synchronization provenance missing: {token}')
for token in ['SchemaV10_CreatesCameraSynchronizationCommissioningPtpAndTransportEvidence','SynchronizationGroupHash_IsStableAcrossCameraOrdering','CaptureResult_ComputesDeviceTimestampSkew']:
    if token not in sync_tests: fail(f'V0.35 synchronization regression test missing: {token}')
for token in ['Multi-Camera Synchronization','Action Trigger','Scheduled Trigger','PTP']:
    if token not in frontend_camera: fail(f'V0.35 synchronization UI token missing: {token}')
ok('V0.35 multi-camera PTP diagnostics + Action Command runtime + skew measurement retained')


# V0.36 synchronized FrameSet workflow primitive
frameset_contract=(ROOT/'backend/src/VisionStudio.Abstractions/VisionData.cs').read_text(encoding='utf-8')
frameset_runtime=(ROOT/'backend/src/VisionStudio.Engine/Camera/VisionFrameSet.cs').read_text(encoding='utf-8')
camera_nodes=(ROOT/'backend/src/VisionStudio.Engine/Nodes/CameraNodes.cs').read_text(encoding='utf-8')
frameset_tests=(ROOT/'backend/tests/VisionStudio.Engine.Tests/FrameSetWorkflowTests.cs').read_text(encoding='utf-8')
frameset_sample=json.loads((ROOT/'samples/synchronized-frameset-workflow.json').read_text(encoding='utf-8'))
for token in ['FrameSet','IVisionFrameSet','VisionFrameSetFrameInfo','VisionValue FrameSet']:
    if token not in frameset_contract: fail(f'V0.36 FrameSet contract missing: {token}')
for token in ['VisionFrameSet','ISynchronizedFrameSetService','SynchronizedFrameSetCaptureRequest','IDisposable']:
    if token not in frameset_runtime: fail(f'V0.36 FrameSet runtime ownership token missing: {token}')
for token in ['camera.syncCapture','frameset.image','SynchronizedCaptureNode','FrameSetImageNode','TriggerSkewUs','timestampBasis']:
    if token not in camera_nodes: fail(f'V0.36 FrameSet node token missing: {token}')
if 'WaitForFrameLeaseAsync' not in (ROOT/'backend/src/VisionStudio.Engine/Camera/CameraManager.cs').read_text(encoding='utf-8'):
    fail('V0.36 CameraManager cannot retain exact synchronized frame leases')
for token in ['ISynchronizedFrameSetService','Task<VisionFrameSet> CaptureAsync','WaitForFrameLeaseAsync']:
    if token not in sync_runtime: fail(f'V0.36 synchronization-to-FrameSet bridge missing: {token}')
if 'CollectStringReference(node, registration.Catalog, "groupId", synchronizationGroupIds)' not in dependency_source or 'foreach (var cameraId in group.CameraIds) cameraIds.Add(cameraId)' not in dependency_source:
    fail('V0.36 dependency manifest does not expand synchronization groupId to member cameras')
for token in ['VisionDataType.FrameSet','FrameSetImageNode_selects_requested_camera_without_clone','VisionFrameSet_disposes_every_owned_image','SynchronizedCaptureNode_emits_FrameSet_and_skew_metadata']:
    if token not in frameset_tests: fail(f'V0.36 FrameSet regression test missing: {token}')
if not any(n.get('type') == 'camera.syncCapture' for n in frameset_sample.get('nodes', [])) or not any(n.get('type') == 'frameset.image' for n in frameset_sample.get('nodes', [])):
    fail('V0.36 synchronized FrameSet sample does not exercise both built-in nodes')
for rel in ['FRAMESET_WORKFLOW.md','MIGRATION_V035_TO_V036.md']:
    if not (ROOT/rel).exists(): fail(f'V0.36 documentation missing: {rel}')
if 'frameSet: frameSetDemo' not in (ROOT/'frontend/src/demos.ts').read_text(encoding='utf-8') or "value: 'frameSet'" not in (ROOT/'frontend/src/App.tsx').read_text(encoding='utf-8'):
    fail('V0.36 frontend synchronized FrameSet demo is missing')
ok('V0.36 first-class synchronized FrameSet workflow primitive + zero-copy extraction present')

# V0.40 automatic synchronization commissioning test
commissioning_source=(ROOT/'backend/src/VisionStudio.Api/CameraSynchronizationCommissioning.cs').read_text(encoding='utf-8')
for token in ['camera_sync_commissioning_tests','CameraSynchronizationCommissioningService','RequestedIterations','CompletedIterations','commissioning:{current.TestId}','ApplicationStopping','CancelAsync','CameraSynchronizationCommissioningReport']:
    if token not in commissioning_source and token not in schema_migration_source: fail(f'V0.40 commissioning token missing: {token}')
for route in ['/api/camera-sync/groups/{id}/commissioning-tests','/api/camera-sync/commissioning-tests/{testId}/cancel','/api/camera-sync/commissioning-tests/{testId}/report']:
    if route not in endpoint_sources: fail(f'V0.40 commissioning endpoint missing: {route}')
for token in ['Automatic Commissioning Test','20, 100, 500','Export JSON','Start Test']:
    if token not in camera_ui: fail(f'V0.40 commissioning UI token missing: {token}')
if 'new(8, "V0.39 camera synchronization commissioning tests"' not in schema_migration_source: fail('V0.40 schema migration v8 missing')
if 'CommissioningStore_PersistsProgressAndReportMetadata' not in sync_tests: fail('V0.40 commissioning store regression test missing')
if not (ROOT/'MIGRATION_V038_TO_V039.md').exists(): fail('V0.40 migration guide missing')
ok('V0.40 automatic synchronization commissioning test + persisted report workflow present')

# V0.40 commissioning evidence report
for token in ['ReportSchemaVersion','HardwareFingerprint','EvidenceHash','WorstRuns','GetReportHtmlAsync']:
    if token not in commissioning_source: fail(f'V0.40 report evidence token missing: {token}')
if '/api/camera-sync/commissioning-tests/{testId}/report.html' not in endpoint_sources: fail('V0.40 HTML report endpoint missing')
if 'exportSyncTestHtml' not in camera_ui: fail('V0.40 HTML report UI export missing')
if not (ROOT/'CAMERA_COMMISSIONING_REPORT.md').exists(): fail('V0.40 report guide missing')
if not (ROOT/'MIGRATION_V039_TO_V040.md').exists(): fail('V0.40 migration guide missing')
ok('V0.40 commissioning evidence report + HTML export present')


# V0.41 visual commissioning evidence and worst-run drill-down
for token in ['CameraSynchronizationCommissioningFrameEvidence','DeltaFromFirstUs','SvgPolyline','SvgPtpOffsets','Worst Samples & Timestamp Drill-down']:
    if token not in commissioning_source: fail(f'V0.41 visual report token missing: {token}')
for token in ['openSyncTestHtml','>View</Button>','>HTML</Button>']:
    if token not in camera_ui: fail(f'V0.41 visual report UI token missing: {token}')
if not (ROOT/'MIGRATION_V040_TO_V041.md').exists(): fail('V0.41 migration guide missing')
if 'schema **v4**' not in (ROOT/'CAMERA_COMMISSIONING_REPORT.md').read_text(encoding='utf-8'): fail('Commissioning report guide does not describe current schema v4')
ok('V0.41 visual commissioning report + per-camera worst-run timestamp drill-down present')


# V0.42 persisted PTP timing evidence + diagnostics
for token in ['PtpSnapshots','CameraSynchronizationPtpDiagnostics','ComputePtpDiagnostics','OffsetSkewPearsonCorrelation','CapturePtpSnapshotsAsync']:
    if token not in sync_runtime: fail(f'V0.42 PTP timing evidence token missing: {token}')
if 'new(9, "V0.42 camera synchronization PTP timing evidence"' not in schema_migration_source or 'ptp_json' not in schema_migration_source:
    fail('V0.42 schema migration v9 / ptp_json missing')
if '/api/camera-sync/groups/{id}/ptp-diagnostics' not in endpoint_sources:
    fail('V0.42 PTP diagnostics endpoint missing')
for token in ['PTP Timing Diagnostics','refreshSyncPtpDiagnostics','Offset ↔ Skew r']:
    if token not in camera_ui: fail(f'V0.42 PTP diagnostics UI token missing: {token}')
for token in ['SvgPtpTrend','PtpDiagnostics','"v4"']:
    if token not in commissioning_source: fail(f'V0.42 report v4 PTP trend token missing: {token}')
if 'PtpDiagnostics_ComputesReadyRateOffsetPercentileAndCorrelation' not in sync_tests:
    fail('V0.42 PTP diagnostics regression test missing')
if not (ROOT/'MIGRATION_V041_TO_V042.md').exists(): fail('V0.42 migration guide missing')
ok('V0.42 per-run PTP evidence + timing diagnostics + report v4 trend present')

# V0.43 Production PTP drift guard
ptp_guard=(ROOT/'backend/src/VisionStudio.Api/ProductionPtpDriftGuard.cs').read_text(encoding='utf-8')
production_runtime=(ROOT/'backend/src/VisionStudio.Api/ProductionRuntime.cs').read_text(encoding='utf-8')
production_ui=(ROOT/'frontend/src/components/ProductionPanel.tsx').read_text(encoding='utf-8')
ptp_guard_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/ProductionPtpDriftGuardTests.cs').read_text(encoding='utf-8')
for token in ['PtpDriftGuardEnabled','PtpGuardWindowRuns','PtpGuardMinimumEvidenceRuns','PtpGuardMinimumReadyRate','PtpGuardCheckEveryCycles']:
    if token not in production_runtime: fail(f'V0.43 production PTP guard config missing: {token}')
for token in ['PROD-PTP-NOT-READY','PROD-PTP-DRIFT','PROD-PTP-MASTER-CLOCK','ComputePtpDiagnostics','ResolveRequirements']:
    if token not in ptp_guard + production_runtime: fail(f'V0.43 production PTP guard token missing: {token}')
if '/api/production/ptp-guard' not in endpoint_sources: fail('V0.43 production PTP guard endpoint missing')
for token in ['PTP Production Guard','PTP Drift Guard','PTP PRODUCTION GUARD','Fault on master change']:
    if token not in production_ui: fail(f'V0.43 Production UI token missing: {token}')
for token in ['ResolveRequirements_FindsScheduledSynchronizedCapture','ResolveRequirements_MergesDuplicateGroupAndKeepsStrictestPolicy']:
    if token not in ptp_guard_tests: fail(f'V0.43 PTP guard regression test missing: {token}')
if not (ROOT/'MIGRATION_V042_TO_V043.md').exists(): fail('V0.43 migration guide missing')
ok('V0.43 Production PTP start gate + sliding-window drift/master-clock guard present')

# V0.44 Production synchronization health guard
sync_guard=(ROOT/'backend/src/VisionStudio.Api/ProductionSynchronizationHealthGuard.cs').read_text(encoding='utf-8')
sync_guard_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/ProductionSynchronizationHealthGuardTests.cs').read_text(encoding='utf-8')
for token in ['SynchronizationHealthGuardEnabled','SynchronizationGuardWindowRuns','SynchronizationGuardMaximumFailureRate','SynchronizationGuardMaximumFrameTimeoutRate','SynchronizationGuardMaxConsecutiveSkewViolations','SynchronizationGuardUnhealthyChecksToFault','SynchronizationGuardHealthyChecksToRecover']:
    if token not in production_runtime: fail(f'V0.44 synchronization guard config missing: {token}')
for token in ['PROD-SYNC-CAMERA-NOT-READY','PROD-SYNC-ACTION','PROD-SYNC-ACTION-ACK','PROD-SYNC-FRAME-TIMEOUT','PROD-SYNC-SKEW','PROD-SYNC-FRAME-LOSS','LiveRecoverableOnly','EstimateSequenceGaps']:
    if token not in sync_guard + production_runtime: fail(f'V0.44 synchronization guard token missing: {token}')
if 'ProductionSynchronizationHealthGuardService' not in host_registration: fail('V0.44 synchronization guard DI registration missing')
if '/api/production/synchronization-guard' not in endpoint_sources: fail('V0.44 synchronization guard endpoint missing')
for token in ['Synchronization Health Guard','SYNCHRONIZATION HEALTH GUARD','Fault hysteresis','Recover hysteresis']:
    if token not in production_ui: fail(f'V0.44 Production UI token missing: {token}')
for token in ['ResolveGroupIds_FindsImmediateAndScheduledSynchronizationGroups','Evaluate_MissingLiveCameras_IsRecoverableTransportFault','Evaluate_DisabledGuard_DoesNotInspectSynchronizationDependencies']:
    if token not in sync_guard_tests: fail(f'V0.44 synchronization guard regression test missing: {token}')
if 'synchronizationHealthGuardEnabled' not in (ROOT/'frontend/src/types.ts').read_text(encoding='utf-8'): fail('V0.44 frontend sync guard type surface missing')
if not (ROOT/'MIGRATION_V043_TO_V044.md').exists(): fail('V0.44 migration guide missing')
ok('V0.44 Production synchronization Action/frame/skew/sequence health guard + reconnect hysteresis present')

# V0.45 vendor-native transport telemetry + persisted evidence
camera_contracts=(ROOT/'backend/src/VisionStudio.Engine/Camera/CameraContracts.cs').read_text(encoding='utf-8')
basler_transport=(ROOT/'backend/src/VisionStudio.Camera.Basler/BaslerTransportTelemetry.cs').read_text(encoding='utf-8')
hik_transport=(ROOT/'backend/src/VisionStudio.Camera.Hikrobot/HikrobotTransportTelemetry.cs').read_text(encoding='utf-8')
camera_sync=(ROOT/'backend/src/VisionStudio.Api/CameraSynchronization.cs').read_text(encoding='utf-8')
camera_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/CameraEndpoints.cs').read_text(encoding='utf-8')
camera_ui=(ROOT/'frontend/src/components/CameraPanel.tsx').read_text(encoding='utf-8')
frontend_types=(ROOT/'frontend/src/types.ts').read_text(encoding='utf-8')
for token in ['CameraTransportTelemetry','HasNativeCounters','Transport = null']:
    if token not in camera_contracts: fail(f'V0.45 normalized camera transport contract missing: {token}')
for token in ['Statistic_Buffer_Underrun_Count','Statistic_Failed_Packet_Count','Statistic_Resend_Request_Count','Statistic_Total_Packet_Count','basler-pylon-stream']:
    if token not in basler_transport: fail(f'V0.45 Basler transport telemetry token missing: {token}')
for token in ['MV_CC_GetAllMatchInfo_NET','MV_MATCH_INFO_NET_DETECT','nLostPacketCount','nRequestResendPacketCount','hikrobot-mvs-net-detect']:
    if token not in hik_transport: fail(f'V0.45 Hikrobot transport telemetry token missing: {token}')
for token in ['transport_json','TransportSnapshots','CaptureTransportSnapshots']:
    if token not in camera_sync: fail(f'V0.45 synchronization transport evidence missing: {token}')
for token in ['ApplyCameraTransportTelemetryEvidenceAsync','camera_sync_runs.transport_json']:
    if token not in schema_migration_source: fail(f'V0.45 schema v10 transport evidence migration missing: {token}')
for token in ['SynchronizationGuardMaximumNativeFrameLossRate','SynchronizationGuardMaximumBufferUnderruns','SynchronizationGuardMaximumResynchronizations']:
    if token not in production_runtime: fail(f'V0.45 native transport guard config missing: {token}')
for token in ['PROD-SYNC-TRANSPORT','ComputeNativeTransport','vendor-native','mixed-native+sequence']:
    if token not in sync_guard: fail(f'V0.45 vendor transport production guard missing: {token}')
if '/api/cameras/{id}/transport-telemetry' not in camera_endpoints: fail('V0.45 camera transport telemetry endpoint missing')
camera_health=(ROOT/'backend/src/VisionStudio.Api/Diagnostics/AssetHealthProviders.cs').read_text(encoding='utf-8')
for token in ['transportLostFrames','transportBufferUnderruns','transportResendRequests','transportThroughputMbps']:
    if token not in camera_health: fail(f'V0.45 Diagnostics Center camera transport metric missing: {token}')
for token in ['Vendor Transport Telemetry','Lost frames','Buffer underrun','Resend requests']:
    if token not in camera_ui: fail(f'V0.45 Camera UI transport telemetry token missing: {token}')
for token in ['Native frame loss','Max underrun','Max resync','Transport evidence']:
    if token not in production_ui: fail(f'V0.45 Production UI transport guard token missing: {token}')
for token in ['CameraTransportTelemetry','transportSnapshots']:
    if token not in frontend_types: fail(f'V0.45 frontend transport telemetry type missing: {token}')
if 'ComputeNativeTransport_PrefersVendorCountersAndCalculatesWindowDeltas' not in sync_guard_tests:
    fail('V0.45 vendor transport delta regression test missing')
if 'V044RuntimeConfig_UpgradesMissingNativeTransportGuardFieldsToV045Defaults' not in (ROOT/'backend/tests/VisionStudio.Api.Tests/ProductionRuntimeTests.cs').read_text(encoding='utf-8'):
    fail('V0.45 V0.44 runtime config compatibility regression test missing')
if not (ROOT/'MIGRATION_V044_TO_V045.md').exists(): fail('V0.45 migration guide missing')
ok('V0.45 Basler/Hikrobot vendor transport telemetry + persisted run evidence + native-first Production Guard present')

# V0.46 GigE network commissioning + host NIC capacity diagnostics
gige_source=(ROOT/'backend/src/VisionStudio.Api/GigENetworkCommissioning.cs').read_text(encoding='utf-8')
gige_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/GigENetworkDiagnosticsTests.cs').read_text(encoding='utf-8')
for token in ['NetworkInterface.GetAllNetworkInterfaces','GetIPStatistics','SpeedMbps','MtuBytes','SameSubnet','BuildTransportTrend','PeakNicUtilization','PacketSizeBytes','InterPacketDelayTicks']:
    if token not in gige_source: fail(f'V0.46 GigE network diagnostics token missing: {token}')
for token in ['gige-network-diagnostics','gige-network-tests']:
    if token not in endpoint_sources: fail(f'V0.46 GigE endpoint missing: {token}')
if 'GigENetworkDiagnosticsService' not in host_registration: fail('V0.46 GigE diagnostics DI registration missing')
for token in ['GigE Network Commissioning','Peak NIC Load','Frame Issue Rate','Packet Issue Rate','Resend Request Rate','Run {syncTestIterations}-cycle GigE Test']:
    if token not in camera_ui: fail(f'V0.46 Camera UI GigE token missing: {token}')
for token in ['GigENetworkDiagnostics','GigENetworkAssessment','GigENetworkTrendPoint']:
    if token not in frontend_types: fail(f'V0.46 frontend GigE type missing: {token}')
for token in ['SameSubnet_UsesHostMaskAndConservativeSlash24Fallback','BuildTransportTrend_ComputesVendorCounterDeltasAndResendRate','BuildTransportTrend_TreatsCounterResetAsNewCounterEpoch']:
    if token not in gige_tests: fail(f'V0.46 GigE regression test missing: {token}')
if 'new(10, "V0.45 camera vendor transport telemetry evidence"' not in schema_migration_source: fail('V0.46 must retain the schema v10 transport migration')
if not (ROOT/'MIGRATION_V045_TO_V046.md').exists(): fail('V0.46 migration guide missing')
ok('V0.46 host NIC mapping + bandwidth/resend diagnostics + one-click GigE commissioning test present')

# V0.47 Offline Replay + Workflow Debugger
replay_source=(ROOT/'backend/src/VisionStudio.Api/OfflineReplay.cs').read_text(encoding='utf-8')
replay_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/ReplayEndpoints.cs').read_text(encoding='utf-8')
replay_ui=(ROOT/'frontend/src/components/ReplayDebuggerPanel.tsx').read_text(encoding='utf-8')
models_source=(ROOT/'backend/src/VisionStudio.Engine/Models.cs').read_text(encoding='utf-8')
workflow_data_source=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionWorkflowData.cs').read_text(encoding='utf-8')
for token in ['OfflineReplayService','EnsureReplayCompatible','PrepareExecutableWorkflow','__replayInputPath','Deterministic re-execution']:
    if token not in replay_source: fail(f'V0.47 replay service token missing: {token}')
for token in ['/api/traces/{runId}/replay/context','/api/traces/{runId}/replay/input','/api/traces/{runId}/replay']:
    if token not in replay_endpoints: fail(f'V0.47 replay endpoint missing: {token}')
for token in ['HasReplayInput','FindReplayInputPath','OkReplaySampleEvery','replay-input.png']:
    if token not in trace_store_dependency: fail(f'V0.47 trace replay artifact token missing: {token}')
for token in ['VisionValueSnapshot','Inputs = null','Outputs = null','ReplayInputArtifact']:
    if token not in models_source: fail(f'V0.47 node value snapshot model missing: {token}')
if 'BuildReplayInput' not in workflow_data_source: fail('V0.47 runtime does not capture replay input evidence')
for token in ['new(11, "V0.47 offline replay artifacts"','ApplyOfflineReplayArtifactsAsync','has_replay_input','replay_relative_path']:
    if token not in schema_migration_source: fail(f'V0.47 schema v11 replay migration missing: {token}')
for token in ['Offline Replay / Workflow Debugger','Step Selected','Run From Here','Run To Breakpoint','NODE INPUT / OUTPUT INSPECTOR','BASELINE vs REPLAY']:
    if token not in replay_ui: fail(f'V0.47 Replay Debugger UI token missing: {token}')
if 'OfflineReplay_ReexecutesSoftwareWorkflow_AndReturnsNodeSnapshots' not in api_smoke: fail('V0.47 replay API regression test missing')
sqlite_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/SqliteStorageTests.cs').read_text(encoding='utf-8')
if 'V047ReplayInput_IsPersistedAsDedicatedPngArtifact_AndSchemaIsV11' not in sqlite_tests: fail('V0.47 replay artifact persistence regression test missing')
v47_settings=json.loads((ROOT/'backend/src/VisionStudio.Api/appsettings.json').read_text(encoding='utf-8'))
if 'OkReplaySampleEvery' not in v47_settings.get('TraceRetention', {}): fail('V0.47 replay sampling configuration missing')
ok('V0.47 persisted replay inputs + deterministic offline debugger + node input/output snapshots present')


# V0.48 Dataset / Batch Validation
dataset_source=(ROOT/'backend/src/VisionStudio.Api/DatasetValidation.cs').read_text(encoding='utf-8')
dataset_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/DatasetValidationEndpoints.cs').read_text(encoding='utf-8')
dataset_ui=(ROOT/'frontend/src/components/DatasetValidationPanel.tsx').read_text(encoding='utf-8')
dataset_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/DatasetValidationTests.cs').read_text(encoding='utf-8')
for token in ['DatasetValidationStore','DatasetValidationService','ValidationRunSummary','FALSE_OK','FALSE_NG','P95DurationMs','TopologyHash','SourceKind','MEDIA','TRACE']:
    if token not in dataset_source: fail(f'V0.48 dataset validation token missing: {token}')
for token in ['/api/validation/datasets','/api/validation/datasets/{datasetId}/items','/api/validation/datasets/{datasetId}/runs','/api/validation/runs/{runId}','/api/validation/runs/{runId}/cancel']:
    if token not in dataset_endpoints: fail(f'V0.48 validation endpoint missing: {token}')
for token in ['CurrentVersion = 19','V0.48 dataset and batch validation','validation_datasets','validation_dataset_items','validation_runs','validation_results']:
    if token not in schema_migration_source: fail(f'V0.48 schema v12 dataset migration missing: {token}')
for token in ['Dataset / Batch Validation','ADD REPLAYABLE TRACES','ADD MEDIA LIBRARY IMAGES','CANDIDATE WORKFLOW','FALSE_OK','A/B VALIDATION COMPARISON']:
    if token not in dataset_ui: fail(f'V0.48 dataset validation UI token missing: {token}')
for token in ['ExecuteImageAsync','dataset-image-replay']:
    if token not in replay_source: fail(f'V0.48 Media Library replay integration missing: {token}')
for token in ['V048Summary_ComputesFalseOkFalseNgErrorsAndTiming','V048Schema12_CreatesDatasetBatchValidationTables']:
    if token not in dataset_tests: fail(f'V0.48 dataset regression test missing: {token}')
if 'DatasetValidation_DatasetCrudSurface_IsExposed' not in api_smoke: fail('V0.48 dataset API smoke test missing')
ok('V0.48 persistent labeled datasets + Trace/Media batch validation + false OK/NG + A/B comparison present')


# V0.49 Parameter Tuning Workspace
tuning_source=(ROOT/'backend/src/VisionStudio.Api/ParameterTuning.cs').read_text(encoding='utf-8')
tuning_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/ParameterTuningEndpoints.cs').read_text(encoding='utf-8')
tuning_ui=(ROOT/'frontend/src/components/ParameterTuningPanel.tsx').read_text(encoding='utf-8')
tuning_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/ParameterTuningTests.cs').read_text(encoding='utf-8')
for token in ['ParameterTuningService','ParameterTuningPreviewRequest','persistTrace: false','RunNode','FALSE_OK','FALSE_NG']:
    if token not in tuning_source: fail(f'V0.49 tuning service token missing: {token}')
for token in ['/api/tuning/preview']:
    if token not in tuning_endpoints: fail(f'V0.49 tuning endpoint missing: {token}')
for token in ['Parameter Tuning Workspace','LIVE OFFLINE PREVIEW','Validate Dataset','PERSISTED VALIDATION CANDIDATES','A/B REGRESSION','Apply to Designer']:
    if token not in tuning_ui: fail(f'V0.49 tuning UI token missing: {token}')
if '/api/validation/runs/{runId}/workflow' not in dataset_endpoints: fail('V0.49 validation candidate workflow endpoint missing')
if 'V049Classification_UsesIndustrialFalseOkFalseNgSemantics' not in tuning_tests: fail('V0.49 tuning regression test missing')
if 'ParameterTuning_TransientPreview_AndCandidateWorkflow_AreExposed' not in api_smoke: fail('V0.49 tuning API smoke test missing')
if 'persistTrace = true' not in replay_source or 'if (persistTrace)' not in replay_source: fail('V0.49 transient replay persistence guard missing')
ok('V0.49 transient single-image tuning + catalog-driven controls + ROI + persisted Dataset validation candidates present')

# V0.50 Product / Recipe lifecycle
product_recipe_source=(ROOT/'backend/src/VisionStudio.Api/ProductRecipeStore.cs').read_text(encoding='utf-8')
product_recipe_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/ProductRecipeEndpoints.cs').read_text(encoding='utf-8')
product_recipe_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/ProductRecipeLifecycleTests.cs').read_text(encoding='utf-8')
for token in ['ProductDescriptor','ValidationAcceptancePolicy','LinkValidationAsync','CloneRecipeAsync','DiffAsync','False OK']:
    if token not in product_recipe_source: fail(f'V0.50 product recipe token missing: {token}')
for token in ['/api/products','/api/products/{productId}/recipes','/api/jobs/{id}/clone','/validation-candidates','/validation']:
    if token not in product_recipe_endpoints: fail(f'V0.50 product recipe endpoint missing: {token}')
for token in ['V050ProductRecipe_PublishRequiresAcceptedValidationForExactWorkflowHash','V050ValidationPolicy_RejectsFalseOkEvenWhenAccuracyLooksHigh','V050CloneAndDiff_KeepRecipeTopologyTraceable']:
    if token not in product_recipe_tests: fail(f'V0.50 product recipe regression test missing: {token}')
for token in ['Validate Dependencies','Clone','Diff','Publication history']:
    if token not in job_panel: fail(f'V0.50 product recipe UI token missing: {token}')
for token in ['V0.50 product recipe lifecycle','products','job_version_validations','jobs.product_id','jobs.recipe_code','CurrentVersion = 19']:
    if token not in schema_migration_source and token not in endpoint_sources: fail(f'V0.50 product recipe schema/version token missing: {token}')
ok('V0.50 Product → Recipe → validated immutable version → publish lifecycle present')



# V0.51 Recipe Parameter Binding
parameter_source=(ROOT/'backend/src/VisionStudio.Api/RecipeParameterization.cs').read_text(encoding='utf-8')
parameter_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/RecipeParameterEndpoints.cs').read_text(encoding='utf-8')
parameter_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/RecipeParameterBindingTests.cs').read_text(encoding='utf-8')
for token in ['RecipeParameterStore','ResolvedRecipeParameterization','ResolveForJobAsync','product_parameter_values','recipe_parameter_values','IsCompatible']:
    if token not in parameter_source: fail(f'V0.51 parameter binding token missing: {token}')
for token in ['/api/products/{productId}/parameters','/api/jobs/{id}/parameters','/api/jobs/{id}/parameterization/resolve','/api/jobs/{id}/versions/{version:int}/parameterization']:
    if token not in parameter_endpoints: fail(f'V0.51 parameter endpoint missing: {token}')
for token in ['base_workflow_hash','base_workflow_json','parameter_bindings_json','parameter_snapshot_json','V0.51 recipe parameter binding','CurrentVersion = 19']:
    if token not in schema_migration_source: fail(f'V0.51 parameter schema token missing: {token}')
for token in ['V051Resolve_BindsProductAndRecipeValues_AndFreezesSnapshotIntoVersion','V051ImmutableVersion_DoesNotHotChangeWhenRecipeParameterDraftChanges','V051Resolver_RejectsMissingSourceAndTypeMismatch']:
    if token not in parameter_tests: fail(f'V0.51 parameter regression test missing: {token}')
for token in ['Product / Recipe Parameters · V0.51','Recipe Parameter Binding','Resolve Preview','Draft parameters never hot-edit Published versions']:
    if token not in job_panel: fail(f'V0.51 parameter UI token missing: {token}')
if 'snapshot.Workflow' not in (ROOT/'backend/src/VisionStudio.Api/Endpoints/JobEndpoints.cs').read_text(encoding='utf-8'):
    fail('V0.51 Production/Publish path no longer consumes immutable effective Workflow snapshot')
ok('V0.51 Product/Recipe parameter drafts → frozen binding snapshot → effective immutable Workflow present')


# V0.52 Reusable Workflow Modules / Subflow
module_source=(ROOT/'backend/src/VisionStudio.Api/WorkflowModules.cs').read_text(encoding='utf-8')
module_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/WorkflowModuleEndpoints.cs').read_text(encoding='utf-8')
module_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/WorkflowModuleTests.cs').read_text(encoding='utf-8')
module_ui=(ROOT/'frontend/src/components/WorkflowModulePanel.tsx').read_text(encoding='utf-8')
for token in ['WorkflowModuleStore','WorkflowModuleExpander','WorkflowModuleAuthoringService','ModuleAwareVisionWorkflowRunner','module.call','ModuleHash','ExpandAsync','DeleteAuthoringRollbackAsync']:
    if token not in module_source: fail(f'V0.52 reusable module token missing: {token}')
for token in ['/api/modules','/api/modules/extract','/api/modules/{id}/versions/{version:int}','/api/modules/{id}/versions','/api/modules/expand','/api/modules/validate']:
    if token not in module_endpoints: fail(f'V0.52 reusable module endpoint missing: {token}')
for token in ['workflow_modules','workflow_module_versions','V0.52 reusable workflow modules','CurrentVersion = 19']:
    if token not in schema_migration_source: fail(f'V0.52 module schema token missing: {token}')
for token in ['V052Extract_ProducesTypedPinnedCall_AndExpansionRestoresExecutableGraph','V052CallParameter_OverridesInternalParameter_ButKeepsPinnedModuleVersion','V052NewVersion_IsImmutable_AndBreakingInterfaceParameterRemovalIsRejected','V052SchemaV15_CreatesImmutableWorkflowModuleTables']:
    if token not in module_tests: fail(f'V0.52 reusable module regression test missing: {token}')
for token in ['Reusable Modules · V0.52','Extract Module + Replace Selection','Insert V','Open Internals in Designer','Upgrade Selected Call']:
    if token not in module_ui: fail(f'V0.52 reusable module UI token missing: {token}')
for token in ['WorkflowModuleRuntimeDependency','moduleExpander','WorkflowModule']:
    if token not in dependency_source: fail(f'V0.52 module runtime provenance missing: {token}')
if 'ModuleAwareVisionWorkflowRunner' not in host_registration or 'AddSingleton<WorkflowModuleStore>()' not in host_registration:
    fail('V0.52 module-aware runtime DI is incomplete')
if 'module.call' not in frontend_app or 'Reusable Modules' not in frontend_app:
    fail('V0.52 Designer module integration missing')
ok('V0.52 immutable typed Subflow/Reusable Module extraction + expansion + version pinning present')


# V0.53 Software Plugin SDK 2.0
plugin_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/PluginEndpoints.cs').read_text(encoding='utf-8')
plugin_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/PluginSdkV2Tests.cs').read_text(encoding='utf-8')
plugin_lifetime_tests=(ROOT/'backend/tests/VisionStudio.Engine.Tests/PluginLifetimeTests.cs').read_text(encoding='utf-8')
plugin_manifest=json.loads((ROOT/'backend/src/VisionStudio.Plugin.Sample/plugin.json').read_text(encoding='utf-8'))
for token in ['VisionPluginSdk','IVisionPluginV2','VisionPluginCompatibility','VisionPluginToolIdentity','PerNode','WithToolIdentity']:
    if token not in plugin_contracts: fail(f'V0.53 SDK 2.0 contract token missing: {token}')
for token in ['ConcurrentDictionary<string, Lazy<IVisionNodeExecutor>>','VisionExecutorLifetime.PerNode','UnregisterSource','ToolIdentity']:
    if token not in plugin_registry: fail(f'V0.53 PerNode/registry token missing: {token}')
for token in ['PluginPackageManifest','plugin.json','PackageManifestSha256','RestartRequired','Rescan','manifest-v2','ValidateNodeSchema','Assembly SHA-256 mismatch']:
    if token not in plugin_manager: fail(f'V0.53 package loader token missing: {token}')
for route in ['/api/plugins','/api/plugins/sdk','/api/plugins/rescan']:
    if route not in plugin_endpoints: fail(f'V0.53 plugin endpoint missing: {route}')
for token in ['V053ManifestPackage_LoadsWithSdk2ToolIdentityAndPerNodeLifetime','V053ManifestDescriptorMismatch_FailsBeforeNodeActivation','V053Rescan_ChangedActiveManifestRequiresRestartInsteadOfHotReplacement']:
    if token not in plugin_tests: fail(f'V0.53 plugin package regression test missing: {token}')
for token in ['PerNodeFactory_ReusesOneExecutorPerConfiguredNodeId','SamplePlugin_DeclaresSdk2PerNodeToolIdentityWithoutEngineTypes']:
    if token not in plugin_lifetime_tests: fail(f'V0.53 plugin lifetime regression test missing: {token}')
if plugin_manifest.get('schemaVersion') != 2 or plugin_manifest.get('minimumSdkApiVersion') != 2 or plugin_manifest.get('maximumSdkApiVersion') != 2:
    fail('V0.53 sample plugin manifest is not SDK API/package schema v2')
for token in ['PluginToolRuntimeDependency','SchemaVersion < 5','PackageManifestHash','PluginFingerprint','sdkApiVersion = x.SdkApiVersion']:
    if token not in dependency_source: fail(f'V0.53 plugin production provenance token missing: {token}')
plugin_package_ui=(ROOT/'frontend/src/components/PluginPackagePanel.tsx').read_text(encoding='utf-8')
if 'PluginPackagePanel' not in frontend_app or 'Runtime registry' not in plugin_package_ui or 'Restart Required' not in plugin_package_ui:
    fail('V0.53 plugin registry UI compatibility surface missing after V0.54 package-manager integration')
if 'schema v15' not in (ROOT/'MIGRATION_V052_TO_V053.md').read_text(encoding='utf-8').lower():
    fail('V0.53 migration does not explicitly preserve SQLite schema v15')
ok('V0.53 manifest-based SDK 2.0 + PerNode lifetime + safe runtime discovery + tool-version provenance present')


# V0.54 signed plugin packaging / install / rollback
plugin_package_source=(ROOT/'backend/src/VisionStudio.Api/PluginPackageManagement.cs').read_text(encoding='utf-8')
plugin_package_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/PluginPackageEndpoints.cs').read_text(encoding='utf-8')
plugin_package_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/PluginPackageManagementTests.cs').read_text(encoding='utf-8')
plugin_packager=(ROOT/'backend/src/VisionStudio.Plugin.Packager/Program.cs').read_text(encoding='utf-8')
plugin_package_ui=(ROOT/'frontend/src/components/PluginPackagePanel.tsx').read_text(encoding='utf-8')
for token in ['PluginPackageSignatureManifest','TrustedPluginPublisher','RequireTrustedSignature','ComputeContentHash','VerifySignature','PrepareRuntimePluginRoot','active-pointers.json','Pending','publisher.cer','signature.json']:
    if token not in plugin_package_source: fail(f'V0.54 package lifecycle token missing: {token}')
for route in ['/api/plugin-packages','/api/plugin-packages/preflight','/api/plugin-packages/install','/api/plugin-packages/{id}/versions/{version}/select','/api/plugin-packages/{id}/rollback','/api/plugin-publishers','/api/plugin-publishers/trust','/api/plugin-publishers/{thumbprint}']:
    if route not in plugin_package_endpoints: fail(f'V0.54 plugin package endpoint missing: {route}')
for token in ['V054SignedPackage_PreflightValidatesTrustedPublisherWithoutExecutingPluginCode','V054Install_KeepsMultipleVersionsAndStagesUpgradeUntilRestart','V054Install_RejectsUnsignedPackageWhenTrustedSignaturePolicyIsEnabled']:
    if token not in plugin_package_tests: fail(f'V0.54 plugin package regression test missing: {token}')
for token in ['RSA-SHA256','ECDSA-SHA256','publisher.cer','signature.json','SignHash']:
    if token not in plugin_packager: fail(f'V0.54 plugin packager token missing: {token}')
for token in ['Plugin Package Manager · V0.63','Select .vspkg','Preflight','Installed versions','Trusted publishers','ACTIVE','PENDING','Rollback']:
    if token not in plugin_package_ui: fail(f'V0.54 plugin package UI token missing: {token}')
if 'AddSingleton<PluginPackageService>()' not in host_registration or 'Configure<PluginPackageOptions>' not in host_registration:
    fail('V0.54 plugin package service/configuration is not registered')
if 'PrepareRuntimePluginRoot' not in host_bootstrap:
    fail('V0.54 pending plugin version is not applied before runtime PluginManager loading')
if 'schema v15' not in (ROOT/'MIGRATION_V053_TO_V054.md').read_text(encoding='utf-8').lower():
    fail('V0.54 migration does not explicitly preserve SQLite schema v15')
ok('V0.54 signed .vspkg preflight + trusted publisher + version repository + pending-restart rollback present')


# V0.55 out-of-process plugin worker
worker_contracts=(ROOT/'backend/src/VisionStudio.Abstractions/PluginWorkerContracts.cs').read_text(encoding='utf-8')
worker_wire=(ROOT/'backend/src/VisionStudio.Abstractions/PluginWorkerWire.cs').read_text(encoding='utf-8')
worker_supervisor=(ROOT/'backend/src/VisionStudio.Api/PluginWorkerSupervisor.cs').read_text(encoding='utf-8')
worker_program=(ROOT/'backend/src/VisionStudio.Plugin.Worker/Program.cs').read_text(encoding='utf-8')
worker_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/PluginWorkerEndpoints.cs').read_text(encoding='utf-8')
worker_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/PluginWorkerTests.cs').read_text(encoding='utf-8')
for token in ['VisionPluginIsolationMode','WorkerProcess','PluginWorkerDiscovery','PluginWorkerExecutionResult','ProtocolVersion']:
    if token not in worker_contracts: fail(f'V0.55 worker contract token missing: {token}')
for token in ['NamedPipeServerStream','ExecutionTimeoutMs','MaxWorkingSetMb','MaxRestartsPerMinute','Kill(entireProcessTree: true)','CircuitOpen']:
    if token not in worker_supervisor: fail(f'V0.55 worker supervisor token missing: {token}')
for token in ['NamedPipeClientStream','PluginWorkerWire.ReadAsync','registry.Register','PluginWorkerValueCodec']:
    if token not in worker_program: fail(f'V0.55 worker host token missing: {token}')
for route in ['/api/plugin-workers','/api/plugin-workers/{pluginId}/restart']:
    if route not in worker_endpoints: fail(f'V0.55 plugin worker endpoint missing: {route}')
for token in ['Worker_codec_round_trips_scalar_values','Sdk_surface_advertises_worker_process_isolation','Plugin_manifest_accepts_string_worker_isolation_mode']:
    if token not in worker_tests: fail(f'V0.55 plugin worker regression test missing: {token}')
for token in ['isolationMode = x.IsolationMode','SchemaVersion < 6','plugin-worker']:
    if token not in dependency_source: fail(f'V0.55 worker dependency provenance token missing: {token}')
if 'VisionStudio.Plugin.Worker' not in (ROOT/'backend/VisionStudio.slnx').read_text(encoding='utf-8'):
    fail('V0.55 worker project missing from solution')
if 'AddSingleton<PluginWorkerSupervisor>()' not in host_registration or 'Configure<PluginWorkerOptions>' not in host_registration:
    fail('V0.55 worker supervisor/configuration is not registered')
if 'Plugin worker pools' not in plugin_package_ui or 'WorkerProcess' not in plugin_package_ui:
    fail('V0.55 plugin worker UI compatibility surface missing after V0.56 pool upgrade')
if 'schema v15' not in (ROOT/'MIGRATION_V054_TO_V055.md').read_text(encoding='utf-8').lower():
    fail('V0.55 migration does not explicitly preserve SQLite schema v15')
ok('V0.55 out-of-process plugin worker + timeout/crash/memory isolation + runtime provenance present')


# V0.56 worker pool + shared-memory image transport
worker_store=(ROOT/'backend/src/VisionStudio.Engine/PluginWorkerSharedMemoryStore.cs').read_text(encoding='utf-8')
worker_settings=json.loads((ROOT/'backend/src/VisionStudio.Api/appsettings.json').read_text(encoding='utf-8')).get('PluginWorkers', {})
for token in ['PluginWorkerSharedImage','FileBackedSharedMemory','SupportedImageTransports']:
    if token not in worker_contracts: fail(f'V0.56 worker protocol/shared-image token missing: {token}')
for token in ['MemoryMappedFile.CreateFromFile','Marshal.Copy','CleanupStaleFiles','Path.GetFileName(fileName)','8UC1','16UC1','32FC1']:
    if token not in worker_store: fail(f'V0.56 shared-memory transport token missing: {token}')
for token in ['WorkerPool','DefaultPoolSize','MaxPoolSize','ConcurrentQueue<WorkerClient>','SharedMemoryThresholdBytes','SharedMemoryTransfers','PoolSize']:
    if token not in worker_supervisor: fail(f'V0.56 worker-pool supervisor token missing: {token}')
for token in ['--shared-memory-root','PluginWorkerSharedMemoryStore','FileBackedSharedMemory']:
    if token not in worker_program and token not in worker_contracts: fail(f'V0.56 worker shared-memory host token missing: {token}')
for token in ['WorkerPoolSize','WorkerSharedMemoryThresholdBytes','WorkerProtocolVersion','WorkerImageTransport']:
    if token not in plugin_manager: fail(f'V0.56 plugin manifest/load metadata missing: {token}')
for token in ['Worker_protocol_v3_advertises_shared_memory_transport_and_timings','Plugin_manifest_accepts_pool_and_shared_memory_threshold','Shared_memory_image_codec_round_trips_raw_pixels_without_png_payload']:
    if token not in worker_tests: fail(f'V0.56 worker regression test missing: {token}')
for token in ['schemaVersion = 7','workerProtocolVersion = x.WorkerProtocolVersion','workerPoolSize = x.WorkerPoolSize','workerImageTransport = x.WorkerImageTransport','workerSharedMemoryThresholdBytes = x.WorkerSharedMemoryThresholdBytes','plugin-worker-runtime']:
    if token not in dependency_source: fail(f'V0.56 worker runtime provenance token missing: {token}')
for key in ['DefaultPoolSize','MaxPoolSize','SharedMemoryEnabled','SharedMemoryThresholdBytes','SharedMemoryRootPath','SharedMemoryStaleMinutes']:
    if key not in worker_settings: fail(f'V0.56 PluginWorkers configuration missing: {key}')
if worker_settings.get('MaxPoolSize') != 4 or worker_settings.get('SharedMemoryThresholdBytes') != 1048576:
    fail('V0.56 default pool/shared-memory thresholds drifted')
if plugin_manifest.get('workerPoolSize') != 2 or plugin_manifest.get('workerSharedMemoryThresholdBytes') != 262144:
    fail('V0.56 sample worker plugin does not demonstrate pool/shared-memory configuration')
for token in ['Plugin worker pools','sharedMemoryTransfers','Restart pool','FileBackedSharedMemory']:
    if token not in plugin_package_ui and token not in worker_contracts: fail(f'V0.56 worker pool UI token missing: {token}')
if 'schema v15' not in (ROOT/'MIGRATION_V055_TO_V056.md').read_text(encoding='utf-8').lower():
    fail('V0.56 migration does not explicitly preserve SQLite schema v15')
ok('V0.56 pooled worker execution + file-backed shared-memory image transport + schema-v7 worker runtime provenance present')


# V0.57 Worker performance profiler + advisory adaptive pool recommendation
worker_performance=(ROOT/'backend/src/VisionStudio.Api/PluginWorkerPerformance.cs').read_text(encoding='utf-8')
worker_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/PluginWorkerEndpoints.cs').read_text(encoding='utf-8')
for token in ['Version = 3','PluginWorkerTiming','Timing { get; init; }']:
    if token not in worker_contracts: fail(f'V0.57 worker timing protocol token missing: {token}')
for token in ['QueueWaitMs','IpcRoundTripMs','PluginExecuteMs','P50Ms','P95Ms','P99Ms','RecommendedPoolSize','RecommendationReady','PluginWorkerPerformanceProfiler']:
    if token not in worker_performance: fail(f'V0.57 profiler token missing: {token}')
for token in ['PerformanceWindowSize','PerformanceMinimumSamples','AdaptiveTargetUtilization','AdaptiveQueuePressureRatio','RecordPerformance','ResetPerformance']:
    if token not in worker_supervisor: fail(f'V0.57 supervisor profiling token missing: {token}')
for token in ['/api/plugin-workers/performance','/api/plugin-workers/{pluginId}/performance/reset']:
    if token not in worker_endpoints: fail(f'V0.57 profiler endpoint missing: {token}')
for token in ['Performance_profiler_separates_stages_and_recommends_more_workers_under_queue_pressure','Performance_profiler_reset_clears_rolling_window','Worker_timing_contract_carries_decode_execute_and_encode_segments']:
    if token not in worker_tests: fail(f'V0.57 profiler regression test missing: {token}')
for key in ['PerformanceWindowSize','PerformanceMinimumSamples','AdaptiveTargetUtilization','AdaptiveQueuePressureRatio']:
    if key not in worker_settings: fail(f'V0.57 PluginWorkers profiling configuration missing: {key}')
if worker_settings.get('PerformanceWindowSize') != 512 or worker_settings.get('PerformanceMinimumSamples') != 30:
    fail('V0.57 performance-window defaults drifted')
for token in ['Worker performance profiler','recommendedPoolSize','queueWait','pluginExecute','Reset']:
    if token not in plugin_package_ui: fail(f'V0.57 worker performance UI token missing: {token}')
if 'schema v15' not in (ROOT/'MIGRATION_V056_TO_V057.md').read_text(encoding='utf-8').lower():
    fail('V0.57 migration does not explicitly preserve SQLite schema v15')
if '**v2 to v3**' not in (ROOT/'MIGRATION_V056_TO_V057.md').read_text(encoding='utf-8'):
    fail('V0.57 migration does not document worker protocol v3')
ok('V0.57 stage-level worker performance profiler + advisory 1/2/4 pool recommendation present')


# V0.58 repeatable Dataset-backed worker benchmark + regression gate
benchmark_source=(ROOT/'backend/src/VisionStudio.Api/PluginPerformanceBenchmark.cs').read_text(encoding='utf-8')
benchmark_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/PluginBenchmarkEndpoints.cs').read_text(encoding='utf-8')
benchmark_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/PluginPerformanceBenchmarkTests.cs').read_text(encoding='utf-8')
for token in ['PluginBenchmarkRegressionPolicy','PluginBenchmarkPoolResult','PluginBenchmarkRecommendation','PluginBenchmarkRegressionGate','RunBenchmarkAsync','DatasetId','ValidationRunId','BaselineRunId','ThroughputPerSecond','ObservedWorkingSetBytes','MaximumP95RegressionPercent','MinimumThroughputRatio']:
    if token not in benchmark_source and token not in worker_supervisor: fail(f'V0.58 benchmark token missing: {token}')
for route in ['/api/plugin-benchmarks','/api/plugin-benchmarks/{runId}','/api/plugin-benchmarks/{runId}/cancel']:
    if route not in benchmark_endpoints: fail(f'V0.58 benchmark endpoint missing: {route}')
for token in ['Recommend_PrefersSmallestPoolWithinFivePercentOfMaximumThroughput','RegressionGate_FailsWhenP95BudgetIsExceeded','RegressionGate_UsesBaselineRecommendedPoolInsteadOfHidingRegressionWithLargerPool']:
    if token not in benchmark_tests: fail(f'V0.58 benchmark regression test missing: {token}')
for token in ['plugin_benchmark_runs','plugin_benchmark_pool_results','V0.58 plugin performance benchmark history','CurrentVersion = 19']:
    if token not in schema_migration_source: fail(f'V0.58 benchmark schema token missing: {token}')
for token in ['Performance benchmark + regression gate','Run Pool 1 / 2 / 4','Optional baseline benchmark','Regression gate']:
    if token not in plugin_package_ui: fail(f'V0.58 benchmark UI token missing: {token}')
if 'schema v16' not in (ROOT/'MIGRATION_V057_TO_V058.md').read_text(encoding='utf-8').lower():
    fail('V0.58 migration does not document SQLite schema v16')
if 'temporary benchmark' not in (ROOT/'PLUGIN_PERFORMANCE_BENCHMARK.md').read_text(encoding='utf-8').lower():
    fail('V0.58 benchmark documentation does not describe temporary benchmark pools')
ok('V0.58 repeatable Dataset benchmark + temporary 1/2/4 pools + persisted regression gate present')


# V0.59 portable CI benchmark spec + headless runner + deterministic exit codes
benchmark_ci=(ROOT/'backend/src/VisionStudio.Api/PluginBenchmarkCi.cs').read_text(encoding='utf-8')
benchmark_cli=(ROOT/'backend/src/VisionStudio.Benchmark.Cli/Program.cs').read_text(encoding='utf-8')
benchmark_ci_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/PluginBenchmarkCiTests.cs').read_text(encoding='utf-8')
benchmark_ci_doc=(ROOT/'PLUGIN_BENCHMARK_CI.md').read_text(encoding='utf-8')
for token in ['PluginBenchmarkBaselineSnapshot','BaselineSnapshot','portableBaseline','BaselinePluginVersion','BaselinePluginAssemblySha256']:
    if token not in benchmark_source: fail(f'V0.59 portable baseline token missing: {token}')
for token in ['PluginBenchmarkCiSpec','CurrentSchemaVersion = 1','FromBaseline','RequireRegressionPass']:
    if token not in benchmark_ci: fail(f'V0.59 CI spec token missing: {token}')
if '/api/plugin-benchmarks/{runId}/ci-spec' not in benchmark_endpoints:
    fail('V0.59 CI spec export endpoint missing')
for token in ['ExitRegressionGateFailed = 10','ExitBenchmarkFailed = 11','ExitInfrastructureError = 12','ExitTimedOut = 13','GITHUB_STEP_SUMMARY','plugin-benchmark-junit.xml','VISIONSTUDIO_BENCH_TOKEN','api/plugin-benchmarks']:
    if token not in benchmark_cli: fail(f'V0.59 headless runner token missing: {token}')
for token in ['CiSpec_EmbedsPortableBaselineAndRemovesDatabaseRunDependency','RegressionGate_AcceptsPortableBaselineSnapshot']:
    if token not in (benchmark_ci_tests + benchmark_tests): fail(f'V0.59 CI regression test missing: {token}')
for rel in ['scripts/plugin-benchmark-gate.ps1','scripts/plugin-benchmark-gate.sh','.github/workflows/plugin-performance-gate.yml']:
    if not (ROOT/rel).exists(): fail(f'V0.59 CI wrapper missing: {rel}')
for token in ['Portable CI specification','Headless runner','Exit codes','JUnit','same-pool comparison']:
    if token.lower() not in benchmark_ci_doc.lower(): fail(f'V0.59 CI documentation token missing: {token}')
if 'schema v16' not in (ROOT/'MIGRATION_V058_TO_V059.md').read_text(encoding='utf-8').lower():
    fail('V0.59 migration does not explicitly preserve SQLite schema v16')
for token in ['CI-enforced performance regressions','CI Spec']:
    if token not in plugin_package_ui: fail(f'V0.59 CI benchmark UI token missing: {token}')
ok('V0.59 portable baseline CI spec + headless benchmark runner + deterministic regression exit codes present')


# V0.60 exact run timeline + node latency baseline observability
observability_source=(ROOT/'backend/src/VisionStudio.Api/RunObservability.cs').read_text(encoding='utf-8')
trace_store=(ROOT/'backend/src/VisionStudio.Api/TraceabilityStore.cs').read_text(encoding='utf-8')
models=(ROOT/'backend/src/VisionStudio.Engine/Models.cs').read_text(encoding='utf-8')
workflow_data=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionWorkflowData.cs').read_text(encoding='utf-8')
node_runtime=(ROOT/'backend/src/VisionStudio.Engine/Runtime/VisionNodeRuntime.cs').read_text(encoding='utf-8')
schema=(ROOT/'backend/src/VisionStudio.Api/SchemaMigrations.cs').read_text(encoding='utf-8')
trace_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/TraceEndpoints.cs').read_text(encoding='utf-8')
observability_ui=(ROOT/'frontend/src/components/RunObservabilityPanel.tsx').read_text(encoding='utf-8')
observability_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/RunObservabilityTests.cs').read_text(encoding='utf-8')
for token in ['ExecutionSequence','StartOffsetMs','EndOffsetMs']:
    if token not in models: fail(f'V0.60 node timeline field missing: {token}')
for token in ['BeginExecutionTimeline','BeginNodeObservation','CurrentExecutionOffsetMs']:
    if token not in workflow_data: fail(f'V0.60 monotonic timeline primitive missing: {token}')
if 'run_node_observations' not in schema or 'CurrentVersion = 19' not in schema:
    fail('V0.60 SQLite schema v17 run_node_observations migration missing')
if 'InsertNodeObservationsAsync' not in trace_store:
    fail('V0.60 trace persistence does not write node observations')
for token in ['NodeTimelineObservation','NodePerformanceBaseline','PeakConcurrency','Percentile','VerySlow']:
    if token not in observability_source: fail(f'V0.60 observability read model missing: {token}')
if '/api/traces/{runId}/observability' not in trace_endpoints:
    fail('V0.60 observability endpoint missing')
for token in ['Run Observatory','Timeline span','Peak concurrency','Baseline runs','P95']:
    if token not in observability_ui: fail(f'V0.60 observability UI token missing: {token}')
for token in ['V060Trace_PersistsExactNodeTimeline_InSchemaV17','V060Observability_UsesPriorSameWorkflowRuns_ForLatencyBaseline']:
    if token not in observability_tests: fail(f'V0.60 observability regression test missing: {token}')
if 'schema v17' not in (ROOT/'MIGRATION_V059_TO_V060.md').read_text(encoding='utf-8').lower():
    fail('V0.60 migration does not document SQLite schema v17')
ok('V0.60 exact run timeline + historical node-latency observability present')


# V0.61 trace comparison + deterministic failure signatures
trace_analysis_source=(ROOT/'backend/src/VisionStudio.Api/TraceAnalysis.cs').read_text(encoding='utf-8')
trace_analysis_ui=(ROOT/'frontend/src/components/TraceAnalysisPanel.tsx').read_text(encoding='utf-8')
trace_analysis_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/TraceAnalysisTests.cs').read_text(encoding='utf-8')
replay_debugger_ui=(ROOT/'frontend/src/components/ReplayDebuggerPanel.tsx').read_text(encoding='utf-8')
for token in ['RunTraceComparison','TraceNodeDelta','FailureSignatureAnalysis','AutoPreviousOk','ExecutionFailure','QualityNG','PerformanceRegression','HashFingerprint']:
    if token not in trace_analysis_source: fail(f'V0.61 trace analysis token missing: {token}')
for route in ['/api/traces/{runId}/compare','/api/traces/{runId}/failure-signature']:
    if route not in trace_endpoints: fail(f'V0.61 trace analysis endpoint missing: {route}')
for token in ['Trace Compare + Failure Signature','Replay suspect node','Recent matching run','Perf regressions']:
    if token not in trace_analysis_ui: fail(f'V0.61 trace analysis UI token missing: {token}')
if 'initialNodeId' not in replay_debugger_ui:
    fail('V0.61 Replay/Debugger suspect-node handoff missing')
for token in ['V061Compare_AutoSelectsNearestPriorOkRun_AndReportsNodeDiff','V061FailureSignature_ClustersSameExecutionError_WhenVolatileNumbersDiffer']:
    if token not in trace_analysis_tests: fail(f'V0.61 trace analysis regression test missing: {token}')
if 'schema v17' not in (ROOT/'MIGRATION_V060_TO_V061.md').read_text(encoding='utf-8').lower():
    fail('V0.61 migration does not explicitly preserve SQLite schema v17')
if not (ROOT/'TRACE_ANALYSIS.md').exists(): fail('V0.61 trace analysis architecture document missing')
ok('V0.61 nearest-OK trace comparison + deterministic recurring failure signatures present')


# V0.62 persistent investigation cases + signature trend dashboard
investigation_source=(ROOT/'backend/src/VisionStudio.Api/InvestigationCases.cs').read_text(encoding='utf-8')
investigation_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/InvestigationEndpoints.cs').read_text(encoding='utf-8')
investigation_ui=(ROOT/'frontend/src/components/InvestigationPanel.tsx').read_text(encoding='utf-8')
investigation_tests=(ROOT/'backend/tests/VisionStudio.Api.Tests/InvestigationCaseTests.cs').read_text(encoding='utf-8')
for token in ['InvestigationCaseService','Open", "Investigating", "Resolved", "Verified", "Closed','CaseKey','reopenCount','Resolution note is required','Verification note is required','InvestigationTrendDashboard']:
    if token not in investigation_source: fail(f'V0.62 investigation domain token missing: {token}')
for route in ['/api/investigations','/api/investigations/trends','/api/investigations/for-trace/{runId}','/api/investigations/from-trace/{runId}','/api/investigations/{id}']:
    if route not in investigation_endpoints: fail(f'V0.62 investigation endpoint missing: {route}')
for token in ['Track this signature','Replay evidence','Root cause','Verify fix','Top signature · 30d']:
    if token not in investigation_ui: fail(f'V0.62 investigation UI token missing: {token}')
for token in ['new(18, "V0.62 investigation cases and signature evidence"','investigation_cases','investigation_case_runs','CurrentVersion = 19']:
    if token not in schema_migration_source: fail(f'V0.62 schema v18 token missing: {token}')
for token in ['V063TrackFromTrace_DeduplicatesSignatureAndPersistsSchemaV19','V063TrendDashboard_AggregatesTrackedSignatureEvidence']:
    if token not in investigation_tests: fail(f'V0.62 investigation regression test missing: {token}')
if 'InvestigationCaseService' not in host_registration or 'MapInvestigationEndpoints' not in (ROOT/'backend/src/VisionStudio.Api/Endpoints/EndpointMapping.cs').read_text(encoding='utf-8'):
    fail('V0.62 investigation DI/endpoint mapping missing')
if not (ROOT/'INVESTIGATION_CASES.md').exists() or not (ROOT/'MIGRATION_V061_TO_V062.md').exists():
    fail('V0.62 investigation architecture/migration documentation missing')
ok('V0.62 deduplicated investigation lifecycle + linked trace evidence + signature trend dashboard present')


# V0.63 resolved-case verification gate + dataset A/B regression evidence
for token in ['InvestigationVerificationEvidence','EvaluateVerificationAsync','GetLatestPassedVerificationEvidenceAsync','BaselineSignatureHits','CandidateSignatureHits','fresh Passed dataset regression gate']:
    if token not in investigation_source: fail(f'V0.63 verification gate token missing: {token}')
for route in ['/api/investigations/{id}/verification-evidence']:
    if route not in investigation_endpoints: fail(f'V0.63 verification endpoint missing: {route}')
for token in ['Dataset Regression Verification Gate','Evaluate verification gate','Passed','Inconclusive','False OK','False NG','fresh Passed gate']:
    if token not in investigation_ui: fail(f'V0.63 verification UI token missing: {token}')
for token in ['new(19, "V0.63 case verification gate and dataset regression evidence"','investigation_case_verifications','gate_status','baseline_validation_run_id','candidate_validation_run_id','CurrentVersion = 19']:
    if token not in schema_migration_source: fail(f'V0.63 schema v19 token missing: {token}')
for token in ['V063Verified_RequiresFreshPassedDatasetRegressionGate','EvaluateVerificationAsync','Assert.Equal("Passed", gate.GateStatus)']:
    if token not in investigation_tests: fail(f'V0.63 verification regression test missing: {token}')
if not (ROOT/'CASE_VERIFICATION_GATE.md').exists() or not (ROOT/'MIGRATION_V062_TO_V063.md').exists():
    fail('V0.63 verification architecture/migration documentation missing')
ok('V0.63 resolved-case A/B dataset regression gate + signature disappearance evidence present')


qsh=(ROOT/'scripts/quality-gate.sh').read_text(encoding='utf-8')
if 'Category!=Soak' not in qsh or 'check-coverage.py' not in qsh: fail('Quality gate missing fast/coverage split')


# Retained V0.20 storage semantics
trace_store=(ROOT/'backend/src/VisionStudio.Api/TraceabilityStore.cs').read_text(encoding='utf-8')
job_store=(ROOT/'backend/src/VisionStudio.Api/JobStore.cs').read_text(encoding='utf-8')
cal_store=(ROOT/'backend/src/VisionStudio.Api/CalibrationWorkspace.cs').read_text(encoding='utf-8')
sqlite_db=(ROOT/'backend/src/VisionStudio.Api/SqliteMetadataDatabase.cs').read_text(encoding='utf-8')
if 'SearchOption.AllDirectories' in trace_store: fail('TraceabilityStore still performs recursive directory scans')
for token in ['run_traces','job_versions','calibration_versions','journal_mode=WAL','foreign_keys=ON']:
    if token not in (sqlite_db + trace_store + job_store + cal_store): fail(f'Missing SQLite storage token: {token}')
if 'OkPreviewSampleEvery' not in trace_store or 'TraceRetentionHostedService' not in trace_store: fail('Trace retention/sampling not wired')
if 'workflow_json TEXT NULL' not in schema_migration_source or 'COALESCE(t.workflow_json, j.workflow_json)' not in trace_store or 'embeddedWorkflow' not in trace_store:
    fail('Production trace workflow de-duplication / immutable Job snapshot resolution is not wired')
if 'StorageMigrationHostedService' not in host_registration or 'LegacyStorageMigrationService' not in host_bootstrap:
    fail('Legacy file migration is not wired through hosted startup')
appsettings=json.loads((ROOT/'backend/src/VisionStudio.Api/appsettings.json').read_text(encoding='utf-8'))
if 'TraceRetention' not in appsettings: fail('TraceRetention configuration missing')
ok('V0.20 SQLite storage semantics retained')



# V0.28 explicit schema migrations + local backup/restore + capacity guard
schema_migrations=(ROOT/'backend/src/VisionStudio.Api/SchemaMigrations.cs').read_text(encoding='utf-8')
storage_maintenance=(ROOT/'backend/src/VisionStudio.Api/StorageMaintenance.cs').read_text(encoding='utf-8')
storage_backup=(ROOT/'backend/src/VisionStudio.Api/StorageBackupService.cs').read_text(encoding='utf-8')
storage_panel=(ROOT/'frontend/src/components/StoragePanel.tsx').read_text(encoding='utf-8')
for token in ['CurrentVersion = 19','schema_migration_history','BaselineHistoryAsync','VerifyHistoryChecksumsAsync','SHA256.HashData','BeginTransaction(deferred: false)']:
    if token not in schema_migrations: fail(f'V0.28 migration runner missing: {token}')
if 'EnsureColumnAsync' in sqlite_db: fail('SqliteMetadataDatabase still owns ad-hoc EnsureColumn migration logic')
for token in ['CreateSnapshotAsync','RestoreSnapshotAsync','BackupDatabase','ValidateSnapshotAsync']:
    if token not in sqlite_db: fail(f'V0.28 SQLite online backup/restore primitive missing: {token}')
for token in ['StorageMaintenanceCoordinator','BeginExclusiveAsync','StorageCapacityService','WarningFreeBytes','CriticalFreeBytes','STORAGE-DISK-CRITICAL','AutoCleanupOnCritical']:
    if token not in storage_maintenance: fail(f'V0.28 storage guard missing: {token}')
for token in ['BackupFormatVersion','DatabaseSha256','CreateAsync','RestoreAsync','ProductionRuntimeState.Stopped','pre-restore.db','RestartRecommended','SafeChildPath']:
    if token not in storage_backup: fail(f'V0.28 backup/restore safety missing: {token}')
for token in ['/api/storage/schema','/api/storage/capacity','/api/storage/backups','/api/storage/restore']:
    if token not in trace_endpoints: fail(f'V0.28 storage endpoint missing: {token}')
if 'EnsureProductionStartAllowedAsync' not in production_source: fail('Production start is not protected by the storage capacity guard')
if 'CanPersistArtifact' not in trace_store: fail('Trace artifact persistence is not protected by the storage capacity guard')
for token in ['StorageMaintenanceMiddleware','StorageCapacityHostedService','StorageBackupService','StorageCapacityService']:
    if token not in (program + host_registration): fail(f'V0.28 storage service/middleware wiring missing: {token}')
for token in ['Create backup','Restore backup','/api/storage/backups','/api/storage/restore']:
    if token not in storage_panel: fail(f'V0.28 storage maintenance UI missing: {token}')
storage_settings=appsettings.get('Storage', {})
for key in ['WarningFreeBytes','CriticalFreeBytes','MaxArtifactBytes','MaxBackupBytes','BackupRetentionCount']:
    if key not in storage_settings: fail(f'V0.28 Storage configuration missing: {key}')
ok('V0.28 schema migration runner + online backup/restore + disk capacity guard present')

# V0.26 shared Asset Health + SignalR diagnostics center
asset_health=(ROOT/'backend/src/VisionStudio.Engine/Diagnostics/AssetHealth.cs').read_text(encoding='utf-8')
diagnostics_service=(ROOT/'backend/src/VisionStudio.Api/Diagnostics/DiagnosticsCenterService.cs').read_text(encoding='utf-8')
diagnostics_providers=(ROOT/'backend/src/VisionStudio.Api/Diagnostics/AssetHealthProviders.cs').read_text(encoding='utf-8')
diagnostics_panel=(ROOT/'frontend/src/components/DiagnosticsPanel.tsx').read_text(encoding='utf-8')
for token in ['IAssetHealthProvider','AssetHealthSnapshot','AssetEventEnvelope','Healthy','Degraded','Faulted','Offline']:
    if token not in asset_health: fail(f'V0.26 Asset Health contract missing: {token}')
for token in ['CameraAssetHealthProvider','DeviceAssetHealthProvider','RobotAssetHealthProvider']:
    if token not in diagnostics_providers: fail(f'V0.26 normalized provider missing: {token}')
for token in ['assetEvent','assetHealth','diagnosticsSummary','PeriodicTimer']:
    if token not in diagnostics_service: fail(f'V0.26 Diagnostics Center missing: {token}')
if 'services.AddSignalR();' not in host_registration or 'MapHub<DiagnosticsHub>' not in program:
    fail('V0.26 SignalR hub is not fully wired')
if '@microsoft/signalr' not in (ROOT/'frontend/package.json').read_text(encoding='utf-8') or 'HubConnectionBuilder' not in diagnostics_panel:
    fail('V0.26 frontend SignalR client is missing')
ok('V0.26 unified Asset Health/Event model + SignalR Diagnostics Center present')

# V0.27 backend-authoritative Catalog + generated frontend fallback
catalog_snapshot=(ROOT/'backend/src/VisionStudio.Engine/CatalogSnapshot.cs').read_text(encoding='utf-8')
catalog_exporter=(ROOT/'backend/src/VisionStudio.CatalogExporter/Program.cs').read_text(encoding='utf-8')
demos_source=(ROOT/'frontend/src/demos.ts').read_text(encoding='utf-8')
system_endpoints=(ROOT/'backend/src/VisionStudio.Api/Endpoints/SystemEndpoints.cs').read_text(encoding='utf-8')
if generated_catalog_doc.get('schemaVersion') != 1: fail('Generated catalog schemaVersion must be 1')
generated_hash=generated_catalog_doc.get('hash','')
if not re.fullmatch(r'[0-9a-f]{64}', generated_hash): fail('Generated catalog hash is not a lowercase SHA-256')
if len(generated_catalog_items) != len(executor_types):
    fail(f'Generated catalog node count drift: expected {len(executor_types)}, got {len(generated_catalog_items)}')
if len(generated_catalog_types) != len(generated_catalog_items): fail('Generated catalog contains duplicate node types')
if generated_catalog_types != set(executor_types):
    fail(f'Generated catalog type set differs from built-ins: missing={sorted(set(executor_types)-generated_catalog_types)}, extra={sorted(generated_catalog_types-set(executor_types))}')
for token in ['ComputeHash','SchemaVersion = 1','DisplayOrder','CanonicalOrder','SHA256.HashData']:
    if token not in catalog_snapshot: fail(f'Catalog snapshot implementation missing: {token}')
for token in ['BuiltInNodeCatalog.Items','--check','VisionCatalogSnapshot.Create','VisionCatalogSnapshot.ComputeHash(actual.Items)']:
    if token not in catalog_exporter: fail(f'Catalog exporter/check missing: {token}')
if "generated/catalog.generated.json" not in frontend_catalog_source or 'fallbackCatalog: NodeCatalogItem[] = document.items' not in frontend_catalog_source:
    fail('Frontend fallback is not loaded from generated backend catalog artifact')
if "fallbackCatalog" in demos_source or "./catalog" in demos_source:
    fail('Demo definitions still depend on frontend fallback catalog instead of being vendor-neutral graph specs')
if 'X-VisionStudio-Catalog-Hash' not in system_endpoints or 'X-VisionStudio-Catalog-Schema' not in system_endpoints or '/api/catalog/meta' not in system_endpoints:
    fail('Runtime catalog authority metadata/headers are missing')
if 'validateCatalog(await response.json())' not in (ROOT/'frontend/src/App.tsx').read_text(encoding='utf-8'):
    fail('Frontend does not validate/hydrate the authoritative runtime catalog')
if '--check' not in qsh or 'VisionStudio.CatalogExporter' not in qsh:
    fail('Quality gate does not reject stale generated frontend catalog')
publish_sh=(ROOT/'scripts/publish-runtime.sh').read_text(encoding='utf-8')
publish_ps=(ROOT/'scripts/publish-runtime.ps1').read_text(encoding='utf-8')
if '--check' not in publish_sh or 'VisionStudio.CatalogExporter' not in publish_sh or '--check' not in publish_ps or 'VisionStudio.CatalogExporter' not in publish_ps:
    fail('Runtime publish path can bypass generated catalog staleness check')
ok('V0.27 backend-authoritative runtime Catalog + generated offline fallback present')

# Test inventory
cs_tests = list((ROOT/'backend/tests').rglob('*.cs'))
text='\n'.join(p.read_text(encoding='utf-8') for p in cs_tests)
facts=len(re.findall(r'\[Fact\]',text)); theories=len(re.findall(r'\[Theory\]',text)); soaks=len(re.findall(r'Trait\("Category",\s*"Soak"\)',text))
ok(f'Test source: Fact={facts}, Theory={theories}, SoakTraits={soaks}')

if errors:
    print('STATIC AUDIT FAILED')
    for e in errors: print(' -',e)
    sys.exit(1)
print('STATIC AUDIT PASSED')
for n in notes: print(' -',n)
