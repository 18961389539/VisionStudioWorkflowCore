// 由 scripts/generate-type-bindings.mjs 生成，不要手改。
// 重新生成：npm run generate:type-bindings（改了 openapi.json 或 types.ts 之后）。
//
// 手写 UI 视图与后端契约的编译期绑定：每个断言要求「手写类型的键集是后端键集的子集」。
// 后端新增字段不会红；后端删除/改名字段而手写还在用会红——这正是镜像漂移最贵的形态。
import type { components } from './api-types';
import type {
  ParameterDescriptor,
  VisionToolCapabilities,
  NodeFrameRule,
  NodeTransformFrameDeclaration,
  NodeCatalogItem,
  NodeRunReport,
  VisionOverlay,
  PluginToolLoadInfo,
  PluginLoadInfo,
  PluginPackageVersionInfo,
  PluginPackagePreflightResult,
  TrustedPluginPublisher,
  PluginSdkInfo,
  PluginWorkerInstanceStatus,
  PluginWorkerStatus,
  PluginWorkerLatencyDistribution,
  PluginWorkerToolPerformanceProfile,
  PluginWorkerPerformanceProfile,
  PluginBenchmarkRegressionPolicy,
  PluginBenchmarkPoolResult,
  PluginBenchmarkRecommendation,
  PluginBenchmarkRegressionGate,
  PluginBenchmarkRun,
  CameraSettings,
  CameraCapabilities,
  CameraTransportTelemetry,
  CameraAcquisitionStats,
  CameraDescriptor,
  CameraDiscoveredDevice,
  RobotRuntimeSettings,
  RobotCapabilities,
  RobotHandshakeSignals,
  RobotDescriptor,
  RobotCommandTraceEvent,
  RobotCommandTraceRecord,
  JobVersionValidationInfo,
  JobVersionInfo,
  PublicationEvent,
  JobDescriptor,
  ValidationCandidateInfo,
  JobVersionDiff,
  JobVersionSnapshot,
  ParameterValueSet,
  ResolvedRecipeParameterization,
  RuntimeDependencyDrift,
  RuntimeDependencyValidation,
  RunTraceRecord,
  NodeTimelineObservation,
  NodePerformanceBaseline,
  RunObservabilitySnapshot,
  TraceNodeDelta,
  RunTraceComparison,
  FailureSignatureOccurrence,
  FailureSignatureAnalysis,
  TraceStats,
  TracePage,
  TraceRetentionOptions,
  StorageCapacityStatus,
  SchemaMigrationHistoryEntry,
  SchemaMigrationStatus,
  StorageBackupDescriptor,
  StorageRestoreResult,
  TraceStorageStatus,
  OfflineReplayContext,
  NodeReplayComparison,
  ValidationDatasetSummary,
  ValidationDatasetItem,
  ValidationDatasetDetail,
  ValidationRunSummary,
  ValidationResultRecord,
  ValidationRunRecord,
  ParameterTuningPreviewResult,
  CalibrationWorkspacePoint,
  CalibrationResidual,
  CalibrationHeatCell,
  VisionTransform2D,
  CalibrationWorkspaceRequest,
  CalibrationWorkspaceResult,
  CalibrationVersionInfo,
  CalibrationAssetDescriptor,
  CalibrationVersionSnapshot,
  CameraIntrinsicVersionInfo,
  CameraIntrinsicAssetDescriptor,
  CameraIntrinsicVersionSnapshot,
  HandEyeWizardSessionDocument,
  HandEyeCalibrationAssetDescriptor,
  HandEyeCalibrationVersionSnapshot,
  HandEye3DWizardSessionDocument,
  HandEye3DCalibrationAssetDescriptor,
  HandEye3DCalibrationVersionSnapshot,
  DeviceRuntimeSettings,
  DeviceRuntimeStats,
  DeviceProtocolDiagnostics,
  DeviceDescriptor,
  ProductionRuntimeConfig,
  ProductionRuntimeStatus,
  ProductionPtpGuardGroupStatus,
  ProductionPtpGuardSnapshot,
  ProductionSynchronizationTransportWindow,
  ProductionSynchronizationGuardGroupStatus,
  ProductionSynchronizationGuardSnapshot,
  AssetHealthSnapshot,
  DiagnosticsSummary,
  RunCounterPersistenceStatus,
  MediaLibraryStatus,
  MediaCollectionDescriptor,
  MediaItemDescriptor,
  HardwareProvenanceSnapshot,
  CameraCommissioningProfile,
  CameraCommissioningSnapshot,
  CameraFeatureDescriptor,
  CameraFeatureProfileRecord,
  CameraTimeSynchronizationStatus,
  CameraSynchronizationGroup,
  CameraSynchronizationMemberStatus,
  CameraSynchronizationGroupStatus,
  CameraSynchronizationCaptureResult,
  CameraSynchronizationCommissioningAssessment,
  CameraSynchronizationStatistics,
  CameraSynchronizationPtpCameraDiagnostics,
  CameraSynchronizationPtpTrendPoint,
  CameraSynchronizationPtpDiagnostics,
  GigEHostAddress,
  GigEHostNetworkInterface,
  GigECameraNetworkDiagnostic,
  GigENicLoadDiagnostic,
  GigENetworkTrendPoint,
  GigENetworkAssessment,
  GigENetworkDiagnostics,
  GigENetworkCommissioningReport,
  CameraSynchronizationCommissioningCameraEvidence,
  CameraSynchronizationCommissioningReport,
  CameraSynchronizationCommissioningTest,
  CameraSynchronizationRunRecord,
  WorkflowModulePort,
  WorkflowModuleVersion,
  WorkflowModuleDescriptor,
  ExtractWorkflowModuleResult,
  InvestigationCaseSummary,
  InvestigationCaseRun,
  InvestigationVerificationEvidence,
  InvestigationCaseDetail,
  InvestigationTrendPoint,
  InvestigationSignatureTrend,
  InvestigationTrendDashboard,
  NodeSpanRecord,
  NodeSpanSnapshot,
} from '../types';

type Assert<T extends true> = T;
type KeysOf<T> = keyof T;

type B_ParameterDescriptor = components['schemas']['ParameterDescriptor'];
export type K_ParameterDescriptor = Assert<KeysOf<ParameterDescriptor> extends KeysOf<B_ParameterDescriptor> ? true : false>;
type B_VisionToolCapabilities = components['schemas']['VisionToolCapabilities'];
export type K_VisionToolCapabilities = Assert<KeysOf<VisionToolCapabilities> extends KeysOf<B_VisionToolCapabilities> ? true : false>;
type B_NodeFrameRule = components['schemas']['NodeFrameRule'];
export type K_NodeFrameRule = Assert<KeysOf<NodeFrameRule> extends KeysOf<B_NodeFrameRule> ? true : false>;
type B_NodeTransformFrameDeclaration = components['schemas']['NodeTransformFrameDeclaration'];
export type K_NodeTransformFrameDeclaration = Assert<KeysOf<NodeTransformFrameDeclaration> extends KeysOf<B_NodeTransformFrameDeclaration> ? true : false>;
type B_NodeCatalogItem = components['schemas']['NodeCatalogItem'];
export type K_NodeCatalogItem = Assert<KeysOf<NodeCatalogItem> extends KeysOf<B_NodeCatalogItem> ? true : false>;
type B_NodeRunReport = components['schemas']['NodeRunReport'];
export type K_NodeRunReport = Assert<KeysOf<NodeRunReport> extends KeysOf<B_NodeRunReport> ? true : false>;
type B_VisionOverlay = components['schemas']['VisionOverlay'];
export type K_VisionOverlay = Assert<KeysOf<VisionOverlay> extends KeysOf<B_VisionOverlay> ? true : false>;
type B_PluginToolLoadInfo = components['schemas']['PluginToolLoadInfo'];
export type K_PluginToolLoadInfo = Assert<KeysOf<PluginToolLoadInfo> extends KeysOf<B_PluginToolLoadInfo> ? true : false>;
type B_PluginLoadInfo = components['schemas']['PluginLoadInfo'];
export type K_PluginLoadInfo = Assert<KeysOf<PluginLoadInfo> extends KeysOf<B_PluginLoadInfo> ? true : false>;
type B_PluginPackageVersionInfo = components['schemas']['PluginPackageVersionInfo'];
export type K_PluginPackageVersionInfo = Assert<KeysOf<PluginPackageVersionInfo> extends KeysOf<B_PluginPackageVersionInfo> ? true : false>;
type B_PluginPackagePreflightResult = components['schemas']['PluginPackagePreflightResult'];
export type K_PluginPackagePreflightResult = Assert<KeysOf<PluginPackagePreflightResult> extends KeysOf<B_PluginPackagePreflightResult> ? true : false>;
type B_TrustedPluginPublisher = components['schemas']['TrustedPluginPublisher'];
export type K_TrustedPluginPublisher = Assert<KeysOf<TrustedPluginPublisher> extends KeysOf<B_TrustedPluginPublisher> ? true : false>;
type B_PluginSdkInfo = components['schemas']['PluginSdkInfo'];
export type K_PluginSdkInfo = Assert<KeysOf<PluginSdkInfo> extends KeysOf<B_PluginSdkInfo> ? true : false>;
type B_PluginWorkerInstanceStatus = components['schemas']['PluginWorkerInstanceStatus'];
export type K_PluginWorkerInstanceStatus = Assert<KeysOf<PluginWorkerInstanceStatus> extends KeysOf<B_PluginWorkerInstanceStatus> ? true : false>;
type B_PluginWorkerStatus = components['schemas']['PluginWorkerStatus'];
export type K_PluginWorkerStatus = Assert<KeysOf<PluginWorkerStatus> extends KeysOf<B_PluginWorkerStatus> ? true : false>;
type B_PluginWorkerLatencyDistribution = components['schemas']['PluginWorkerLatencyDistribution'];
export type K_PluginWorkerLatencyDistribution = Assert<KeysOf<PluginWorkerLatencyDistribution> extends KeysOf<B_PluginWorkerLatencyDistribution> ? true : false>;
type B_PluginWorkerToolPerformanceProfile = components['schemas']['PluginWorkerToolPerformanceProfile'];
export type K_PluginWorkerToolPerformanceProfile = Assert<KeysOf<PluginWorkerToolPerformanceProfile> extends KeysOf<B_PluginWorkerToolPerformanceProfile> ? true : false>;
type B_PluginWorkerPerformanceProfile = components['schemas']['PluginWorkerPerformanceProfile'];
export type K_PluginWorkerPerformanceProfile = Assert<KeysOf<PluginWorkerPerformanceProfile> extends KeysOf<B_PluginWorkerPerformanceProfile> ? true : false>;
type B_PluginBenchmarkRegressionPolicy = components['schemas']['PluginBenchmarkRegressionPolicy'];
export type K_PluginBenchmarkRegressionPolicy = Assert<KeysOf<PluginBenchmarkRegressionPolicy> extends KeysOf<B_PluginBenchmarkRegressionPolicy> ? true : false>;
type B_PluginBenchmarkPoolResult = components['schemas']['PluginBenchmarkPoolResult'];
export type K_PluginBenchmarkPoolResult = Assert<KeysOf<PluginBenchmarkPoolResult> extends KeysOf<B_PluginBenchmarkPoolResult> ? true : false>;
type B_PluginBenchmarkRecommendation = components['schemas']['PluginBenchmarkRecommendation'];
export type K_PluginBenchmarkRecommendation = Assert<KeysOf<PluginBenchmarkRecommendation> extends KeysOf<B_PluginBenchmarkRecommendation> ? true : false>;
type B_PluginBenchmarkRegressionGate = components['schemas']['PluginBenchmarkRegressionGate'];
export type K_PluginBenchmarkRegressionGate = Assert<KeysOf<PluginBenchmarkRegressionGate> extends KeysOf<B_PluginBenchmarkRegressionGate> ? true : false>;
type B_PluginBenchmarkRun = components['schemas']['PluginBenchmarkRun'];
export type K_PluginBenchmarkRun = Assert<KeysOf<PluginBenchmarkRun> extends KeysOf<B_PluginBenchmarkRun> ? true : false>;
type B_CameraSettings = components['schemas']['CameraSettings'];
export type K_CameraSettings = Assert<KeysOf<CameraSettings> extends KeysOf<B_CameraSettings> ? true : false>;
type B_CameraCapabilities = components['schemas']['CameraCapabilities'];
export type K_CameraCapabilities = Assert<KeysOf<CameraCapabilities> extends KeysOf<B_CameraCapabilities> ? true : false>;
type B_CameraTransportTelemetry = components['schemas']['CameraTransportTelemetry'];
export type K_CameraTransportTelemetry = Assert<KeysOf<CameraTransportTelemetry> extends KeysOf<B_CameraTransportTelemetry> ? true : false>;
type B_CameraAcquisitionStats = components['schemas']['CameraAcquisitionStats'];
export type K_CameraAcquisitionStats = Assert<KeysOf<CameraAcquisitionStats> extends KeysOf<B_CameraAcquisitionStats> ? true : false>;
type B_CameraDescriptor = components['schemas']['CameraDescriptor'];
export type K_CameraDescriptor = Assert<KeysOf<CameraDescriptor> extends KeysOf<B_CameraDescriptor> ? true : false>;
type B_CameraDiscoveredDevice = components['schemas']['CameraDiscoveredDevice'];
export type K_CameraDiscoveredDevice = Assert<KeysOf<CameraDiscoveredDevice> extends KeysOf<B_CameraDiscoveredDevice> ? true : false>;
type B_RobotRuntimeSettings = components['schemas']['RobotRuntimeSettings'];
export type K_RobotRuntimeSettings = Assert<KeysOf<RobotRuntimeSettings> extends KeysOf<B_RobotRuntimeSettings> ? true : false>;
type B_RobotCapabilities = components['schemas']['RobotCapabilities'];
export type K_RobotCapabilities = Assert<KeysOf<RobotCapabilities> extends KeysOf<B_RobotCapabilities> ? true : false>;
type B_RobotHandshakeSignals = components['schemas']['RobotHandshakeSignals'];
export type K_RobotHandshakeSignals = Assert<KeysOf<RobotHandshakeSignals> extends KeysOf<B_RobotHandshakeSignals> ? true : false>;
type B_RobotDescriptor = components['schemas']['RobotDescriptor'];
export type K_RobotDescriptor = Assert<KeysOf<RobotDescriptor> extends KeysOf<B_RobotDescriptor> ? true : false>;
type B_RobotCommandTraceEvent = components['schemas']['RobotCommandTraceEvent'];
export type K_RobotCommandTraceEvent = Assert<KeysOf<RobotCommandTraceEvent> extends KeysOf<B_RobotCommandTraceEvent> ? true : false>;
type B_RobotCommandTraceRecord = components['schemas']['RobotCommandTraceRecord'];
export type K_RobotCommandTraceRecord = Assert<KeysOf<RobotCommandTraceRecord> extends KeysOf<B_RobotCommandTraceRecord> ? true : false>;
type B_JobVersionValidationInfo = components['schemas']['JobVersionValidationInfo'];
export type K_JobVersionValidationInfo = Assert<KeysOf<JobVersionValidationInfo> extends KeysOf<B_JobVersionValidationInfo> ? true : false>;
type B_JobVersionInfo = components['schemas']['JobVersionInfo'];
export type K_JobVersionInfo = Assert<KeysOf<JobVersionInfo> extends KeysOf<B_JobVersionInfo> ? true : false>;
type B_PublicationEvent = components['schemas']['PublicationEvent'];
export type K_PublicationEvent = Assert<KeysOf<PublicationEvent> extends KeysOf<B_PublicationEvent> ? true : false>;
type B_JobDescriptor = components['schemas']['JobDescriptor'];
export type K_JobDescriptor = Assert<KeysOf<JobDescriptor> extends KeysOf<B_JobDescriptor> ? true : false>;
type B_ValidationCandidateInfo = components['schemas']['ValidationCandidateInfo'];
export type K_ValidationCandidateInfo = Assert<KeysOf<ValidationCandidateInfo> extends KeysOf<B_ValidationCandidateInfo> ? true : false>;
type B_JobVersionDiff = components['schemas']['JobVersionDiff'];
export type K_JobVersionDiff = Assert<KeysOf<JobVersionDiff> extends KeysOf<B_JobVersionDiff> ? true : false>;
type B_JobVersionSnapshot = components['schemas']['JobVersionSnapshot'];
export type K_JobVersionSnapshot = Assert<KeysOf<JobVersionSnapshot> extends KeysOf<B_JobVersionSnapshot> ? true : false>;
type B_ParameterValueSet = components['schemas']['ParameterValueSet'];
export type K_ParameterValueSet = Assert<KeysOf<ParameterValueSet> extends KeysOf<B_ParameterValueSet> ? true : false>;
type B_ResolvedRecipeParameterization = components['schemas']['ResolvedRecipeParameterization'];
export type K_ResolvedRecipeParameterization = Assert<KeysOf<ResolvedRecipeParameterization> extends KeysOf<B_ResolvedRecipeParameterization> ? true : false>;
type B_RuntimeDependencyDrift = components['schemas']['RuntimeDependencyDrift'];
export type K_RuntimeDependencyDrift = Assert<KeysOf<RuntimeDependencyDrift> extends KeysOf<B_RuntimeDependencyDrift> ? true : false>;
type B_RuntimeDependencyValidation = components['schemas']['RuntimeDependencyValidation'];
export type K_RuntimeDependencyValidation = Assert<KeysOf<RuntimeDependencyValidation> extends KeysOf<B_RuntimeDependencyValidation> ? true : false>;
type B_RunTraceRecord = components['schemas']['RunTraceRecord'];
export type K_RunTraceRecord = Assert<KeysOf<RunTraceRecord> extends KeysOf<B_RunTraceRecord> ? true : false>;
type B_NodeTimelineObservation = components['schemas']['NodeTimelineObservation'];
export type K_NodeTimelineObservation = Assert<KeysOf<NodeTimelineObservation> extends KeysOf<B_NodeTimelineObservation> ? true : false>;
type B_NodePerformanceBaseline = components['schemas']['NodePerformanceBaseline'];
export type K_NodePerformanceBaseline = Assert<KeysOf<NodePerformanceBaseline> extends KeysOf<B_NodePerformanceBaseline> ? true : false>;
type B_RunObservabilitySnapshot = components['schemas']['RunObservabilitySnapshot'];
export type K_RunObservabilitySnapshot = Assert<KeysOf<RunObservabilitySnapshot> extends KeysOf<B_RunObservabilitySnapshot> ? true : false>;
type B_TraceNodeDelta = components['schemas']['TraceNodeDelta'];
export type K_TraceNodeDelta = Assert<KeysOf<TraceNodeDelta> extends KeysOf<B_TraceNodeDelta> ? true : false>;
type B_RunTraceComparison = components['schemas']['RunTraceComparison'];
export type K_RunTraceComparison = Assert<KeysOf<RunTraceComparison> extends KeysOf<B_RunTraceComparison> ? true : false>;
type B_FailureSignatureOccurrence = components['schemas']['FailureSignatureOccurrence'];
export type K_FailureSignatureOccurrence = Assert<KeysOf<FailureSignatureOccurrence> extends KeysOf<B_FailureSignatureOccurrence> ? true : false>;
type B_FailureSignatureAnalysis = components['schemas']['FailureSignatureAnalysis'];
export type K_FailureSignatureAnalysis = Assert<KeysOf<FailureSignatureAnalysis> extends KeysOf<B_FailureSignatureAnalysis> ? true : false>;
type B_TraceStats = components['schemas']['TraceStats'];
export type K_TraceStats = Assert<KeysOf<TraceStats> extends KeysOf<B_TraceStats> ? true : false>;
type B_TracePage = components['schemas']['TracePage'];
export type K_TracePage = Assert<KeysOf<TracePage> extends KeysOf<B_TracePage> ? true : false>;
type B_TraceRetentionOptions = components['schemas']['TraceRetentionOptions'];
export type K_TraceRetentionOptions = Assert<KeysOf<TraceRetentionOptions> extends KeysOf<B_TraceRetentionOptions> ? true : false>;
type B_StorageCapacityStatus = components['schemas']['StorageCapacityStatus'];
export type K_StorageCapacityStatus = Assert<KeysOf<StorageCapacityStatus> extends KeysOf<B_StorageCapacityStatus> ? true : false>;
type B_SchemaMigrationHistoryEntry = components['schemas']['SchemaMigrationHistoryEntry'];
export type K_SchemaMigrationHistoryEntry = Assert<KeysOf<SchemaMigrationHistoryEntry> extends KeysOf<B_SchemaMigrationHistoryEntry> ? true : false>;
type B_SchemaMigrationStatus = components['schemas']['SchemaMigrationStatus'];
export type K_SchemaMigrationStatus = Assert<KeysOf<SchemaMigrationStatus> extends KeysOf<B_SchemaMigrationStatus> ? true : false>;
type B_StorageBackupDescriptor = components['schemas']['StorageBackupDescriptor'];
export type K_StorageBackupDescriptor = Assert<KeysOf<StorageBackupDescriptor> extends KeysOf<B_StorageBackupDescriptor> ? true : false>;
type B_StorageRestoreResult = components['schemas']['StorageRestoreResult'];
export type K_StorageRestoreResult = Assert<KeysOf<StorageRestoreResult> extends KeysOf<B_StorageRestoreResult> ? true : false>;
type B_TraceStorageStatus = components['schemas']['TraceStorageStatus'];
export type K_TraceStorageStatus = Assert<KeysOf<TraceStorageStatus> extends KeysOf<B_TraceStorageStatus> ? true : false>;
type B_OfflineReplayContext = components['schemas']['OfflineReplayContext'];
export type K_OfflineReplayContext = Assert<KeysOf<OfflineReplayContext> extends KeysOf<B_OfflineReplayContext> ? true : false>;
type B_NodeReplayComparison = components['schemas']['NodeReplayComparison'];
export type K_NodeReplayComparison = Assert<KeysOf<NodeReplayComparison> extends KeysOf<B_NodeReplayComparison> ? true : false>;
type B_ValidationDatasetSummary = components['schemas']['ValidationDatasetSummary'];
export type K_ValidationDatasetSummary = Assert<KeysOf<ValidationDatasetSummary> extends KeysOf<B_ValidationDatasetSummary> ? true : false>;
type B_ValidationDatasetItem = components['schemas']['ValidationDatasetItem'];
export type K_ValidationDatasetItem = Assert<KeysOf<ValidationDatasetItem> extends KeysOf<B_ValidationDatasetItem> ? true : false>;
type B_ValidationDatasetDetail = components['schemas']['ValidationDatasetDetail'];
export type K_ValidationDatasetDetail = Assert<KeysOf<ValidationDatasetDetail> extends KeysOf<B_ValidationDatasetDetail> ? true : false>;
type B_ValidationRunSummary = components['schemas']['ValidationRunSummary'];
export type K_ValidationRunSummary = Assert<KeysOf<ValidationRunSummary> extends KeysOf<B_ValidationRunSummary> ? true : false>;
type B_ValidationResultRecord = components['schemas']['ValidationResultRecord'];
export type K_ValidationResultRecord = Assert<KeysOf<ValidationResultRecord> extends KeysOf<B_ValidationResultRecord> ? true : false>;
type B_ValidationRunRecord = components['schemas']['ValidationRunRecord'];
export type K_ValidationRunRecord = Assert<KeysOf<ValidationRunRecord> extends KeysOf<B_ValidationRunRecord> ? true : false>;
type B_ParameterTuningPreviewResult = components['schemas']['ParameterTuningPreviewResult'];
export type K_ParameterTuningPreviewResult = Assert<KeysOf<ParameterTuningPreviewResult> extends KeysOf<B_ParameterTuningPreviewResult> ? true : false>;
type B_CalibrationWorkspacePoint = components['schemas']['CalibrationWorkspacePoint'];
export type K_CalibrationWorkspacePoint = Assert<KeysOf<CalibrationWorkspacePoint> extends KeysOf<B_CalibrationWorkspacePoint> ? true : false>;
type B_CalibrationResidual = components['schemas']['CalibrationResidual'];
export type K_CalibrationResidual = Assert<KeysOf<CalibrationResidual> extends KeysOf<B_CalibrationResidual> ? true : false>;
type B_CalibrationHeatCell = components['schemas']['CalibrationHeatCell'];
export type K_CalibrationHeatCell = Assert<KeysOf<CalibrationHeatCell> extends KeysOf<B_CalibrationHeatCell> ? true : false>;
type B_VisionTransform2D = components['schemas']['VisionTransform2D'];
export type K_VisionTransform2D = Assert<KeysOf<VisionTransform2D> extends KeysOf<B_VisionTransform2D> ? true : false>;
type B_CalibrationWorkspaceRequest = components['schemas']['CalibrationWorkspaceRequest'];
export type K_CalibrationWorkspaceRequest = Assert<KeysOf<CalibrationWorkspaceRequest> extends KeysOf<B_CalibrationWorkspaceRequest> ? true : false>;
type B_CalibrationWorkspaceResult = components['schemas']['CalibrationWorkspaceResult'];
export type K_CalibrationWorkspaceResult = Assert<KeysOf<CalibrationWorkspaceResult> extends KeysOf<B_CalibrationWorkspaceResult> ? true : false>;
type B_CalibrationVersionInfo = components['schemas']['CalibrationVersionInfo'];
export type K_CalibrationVersionInfo = Assert<KeysOf<CalibrationVersionInfo> extends KeysOf<B_CalibrationVersionInfo> ? true : false>;
type B_CalibrationAssetDescriptor = components['schemas']['CalibrationAssetDescriptor'];
export type K_CalibrationAssetDescriptor = Assert<KeysOf<CalibrationAssetDescriptor> extends KeysOf<B_CalibrationAssetDescriptor> ? true : false>;
type B_CalibrationVersionSnapshot = components['schemas']['CalibrationVersionSnapshot'];
export type K_CalibrationVersionSnapshot = Assert<KeysOf<CalibrationVersionSnapshot> extends KeysOf<B_CalibrationVersionSnapshot> ? true : false>;
type B_CameraIntrinsicVersionInfo = components['schemas']['CameraIntrinsicVersionInfo'];
export type K_CameraIntrinsicVersionInfo = Assert<KeysOf<CameraIntrinsicVersionInfo> extends KeysOf<B_CameraIntrinsicVersionInfo> ? true : false>;
type B_CameraIntrinsicAssetDescriptor = components['schemas']['CameraIntrinsicAssetDescriptor'];
export type K_CameraIntrinsicAssetDescriptor = Assert<KeysOf<CameraIntrinsicAssetDescriptor> extends KeysOf<B_CameraIntrinsicAssetDescriptor> ? true : false>;
type B_CameraIntrinsicVersionSnapshot = components['schemas']['CameraIntrinsicVersionSnapshot'];
export type K_CameraIntrinsicVersionSnapshot = Assert<KeysOf<CameraIntrinsicVersionSnapshot> extends KeysOf<B_CameraIntrinsicVersionSnapshot> ? true : false>;
type B_HandEyeWizardSessionDocument = components['schemas']['HandEyeWizardSessionDocument'];
export type K_HandEyeWizardSessionDocument = Assert<KeysOf<HandEyeWizardSessionDocument> extends KeysOf<B_HandEyeWizardSessionDocument> ? true : false>;
type B_HandEyeCalibrationAssetDescriptor = components['schemas']['HandEyeCalibrationAssetDescriptor'];
export type K_HandEyeCalibrationAssetDescriptor = Assert<KeysOf<HandEyeCalibrationAssetDescriptor> extends KeysOf<B_HandEyeCalibrationAssetDescriptor> ? true : false>;
type B_HandEyeCalibrationVersionSnapshot = components['schemas']['HandEyeCalibrationVersionSnapshot'];
export type K_HandEyeCalibrationVersionSnapshot = Assert<KeysOf<HandEyeCalibrationVersionSnapshot> extends KeysOf<B_HandEyeCalibrationVersionSnapshot> ? true : false>;
type B_HandEye3DWizardSessionDocument = components['schemas']['HandEye3DWizardSessionDocument'];
export type K_HandEye3DWizardSessionDocument = Assert<KeysOf<HandEye3DWizardSessionDocument> extends KeysOf<B_HandEye3DWizardSessionDocument> ? true : false>;
type B_HandEye3DCalibrationAssetDescriptor = components['schemas']['HandEye3DCalibrationAssetDescriptor'];
export type K_HandEye3DCalibrationAssetDescriptor = Assert<KeysOf<HandEye3DCalibrationAssetDescriptor> extends KeysOf<B_HandEye3DCalibrationAssetDescriptor> ? true : false>;
type B_HandEye3DCalibrationVersionSnapshot = components['schemas']['HandEye3DCalibrationVersionSnapshot'];
export type K_HandEye3DCalibrationVersionSnapshot = Assert<KeysOf<HandEye3DCalibrationVersionSnapshot> extends KeysOf<B_HandEye3DCalibrationVersionSnapshot> ? true : false>;
type B_DeviceRuntimeSettings = components['schemas']['DeviceRuntimeSettings'];
export type K_DeviceRuntimeSettings = Assert<KeysOf<DeviceRuntimeSettings> extends KeysOf<B_DeviceRuntimeSettings> ? true : false>;
type B_DeviceRuntimeStats = components['schemas']['DeviceRuntimeStats'];
export type K_DeviceRuntimeStats = Assert<KeysOf<DeviceRuntimeStats> extends KeysOf<B_DeviceRuntimeStats> ? true : false>;
type B_DeviceProtocolDiagnostics = components['schemas']['DeviceProtocolDiagnostics'];
export type K_DeviceProtocolDiagnostics = Assert<KeysOf<DeviceProtocolDiagnostics> extends KeysOf<B_DeviceProtocolDiagnostics> ? true : false>;
type B_DeviceDescriptor = components['schemas']['DeviceDescriptor'];
export type K_DeviceDescriptor = Assert<KeysOf<DeviceDescriptor> extends KeysOf<B_DeviceDescriptor> ? true : false>;
type B_ProductionRuntimeConfig = components['schemas']['ProductionRuntimeConfig'];
export type K_ProductionRuntimeConfig = Assert<KeysOf<ProductionRuntimeConfig> extends KeysOf<B_ProductionRuntimeConfig> ? true : false>;
type B_ProductionRuntimeStatus = components['schemas']['ProductionRuntimeStatus'];
export type K_ProductionRuntimeStatus = Assert<KeysOf<ProductionRuntimeStatus> extends KeysOf<B_ProductionRuntimeStatus> ? true : false>;
type B_ProductionPtpGuardGroupStatus = components['schemas']['ProductionPtpGuardGroupStatus'];
export type K_ProductionPtpGuardGroupStatus = Assert<KeysOf<ProductionPtpGuardGroupStatus> extends KeysOf<B_ProductionPtpGuardGroupStatus> ? true : false>;
type B_ProductionPtpGuardSnapshot = components['schemas']['ProductionPtpGuardSnapshot'];
export type K_ProductionPtpGuardSnapshot = Assert<KeysOf<ProductionPtpGuardSnapshot> extends KeysOf<B_ProductionPtpGuardSnapshot> ? true : false>;
type B_ProductionSynchronizationTransportWindow = components['schemas']['ProductionSynchronizationTransportWindow'];
export type K_ProductionSynchronizationTransportWindow = Assert<KeysOf<ProductionSynchronizationTransportWindow> extends KeysOf<B_ProductionSynchronizationTransportWindow> ? true : false>;
type B_ProductionSynchronizationGuardGroupStatus = components['schemas']['ProductionSynchronizationGuardGroupStatus'];
export type K_ProductionSynchronizationGuardGroupStatus = Assert<KeysOf<ProductionSynchronizationGuardGroupStatus> extends KeysOf<B_ProductionSynchronizationGuardGroupStatus> ? true : false>;
type B_ProductionSynchronizationGuardSnapshot = components['schemas']['ProductionSynchronizationGuardSnapshot'];
export type K_ProductionSynchronizationGuardSnapshot = Assert<KeysOf<ProductionSynchronizationGuardSnapshot> extends KeysOf<B_ProductionSynchronizationGuardSnapshot> ? true : false>;
type B_AssetHealthSnapshot = components['schemas']['AssetHealthSnapshot'];
export type K_AssetHealthSnapshot = Assert<KeysOf<AssetHealthSnapshot> extends KeysOf<B_AssetHealthSnapshot> ? true : false>;
type B_DiagnosticsSummary = components['schemas']['DiagnosticsSummary'];
export type K_DiagnosticsSummary = Assert<KeysOf<DiagnosticsSummary> extends KeysOf<B_DiagnosticsSummary> ? true : false>;
type B_RunCounterPersistenceStatus = components['schemas']['RunCounterPersistenceStatus'];
export type K_RunCounterPersistenceStatus = Assert<KeysOf<RunCounterPersistenceStatus> extends KeysOf<B_RunCounterPersistenceStatus> ? true : false>;
type B_MediaLibraryStatus = components['schemas']['MediaLibraryStatus'];
export type K_MediaLibraryStatus = Assert<KeysOf<MediaLibraryStatus> extends KeysOf<B_MediaLibraryStatus> ? true : false>;
type B_MediaCollectionDescriptor = components['schemas']['MediaCollectionDescriptor'];
export type K_MediaCollectionDescriptor = Assert<KeysOf<MediaCollectionDescriptor> extends KeysOf<B_MediaCollectionDescriptor> ? true : false>;
type B_MediaItemDescriptor = components['schemas']['MediaItemDescriptor'];
export type K_MediaItemDescriptor = Assert<KeysOf<MediaItemDescriptor> extends KeysOf<B_MediaItemDescriptor> ? true : false>;
type B_HardwareProvenanceSnapshot = components['schemas']['HardwareProvenanceSnapshot'];
export type K_HardwareProvenanceSnapshot = Assert<KeysOf<HardwareProvenanceSnapshot> extends KeysOf<B_HardwareProvenanceSnapshot> ? true : false>;
type B_CameraCommissioningProfile = components['schemas']['CameraCommissioningProfile'];
export type K_CameraCommissioningProfile = Assert<KeysOf<CameraCommissioningProfile> extends KeysOf<B_CameraCommissioningProfile> ? true : false>;
type B_CameraCommissioningSnapshot = components['schemas']['CameraCommissioningSnapshot'];
export type K_CameraCommissioningSnapshot = Assert<KeysOf<CameraCommissioningSnapshot> extends KeysOf<B_CameraCommissioningSnapshot> ? true : false>;
type B_CameraFeatureDescriptor = components['schemas']['CameraFeatureDescriptor'];
export type K_CameraFeatureDescriptor = Assert<KeysOf<CameraFeatureDescriptor> extends KeysOf<B_CameraFeatureDescriptor> ? true : false>;
type B_CameraFeatureProfileRecord = components['schemas']['CameraFeatureProfileRecord'];
export type K_CameraFeatureProfileRecord = Assert<KeysOf<CameraFeatureProfileRecord> extends KeysOf<B_CameraFeatureProfileRecord> ? true : false>;
type B_CameraTimeSynchronizationStatus = components['schemas']['CameraTimeSynchronizationStatus'];
export type K_CameraTimeSynchronizationStatus = Assert<KeysOf<CameraTimeSynchronizationStatus> extends KeysOf<B_CameraTimeSynchronizationStatus> ? true : false>;
type B_CameraSynchronizationGroup = components['schemas']['CameraSynchronizationGroup'];
export type K_CameraSynchronizationGroup = Assert<KeysOf<CameraSynchronizationGroup> extends KeysOf<B_CameraSynchronizationGroup> ? true : false>;
type B_CameraSynchronizationMemberStatus = components['schemas']['CameraSynchronizationMemberStatus'];
export type K_CameraSynchronizationMemberStatus = Assert<KeysOf<CameraSynchronizationMemberStatus> extends KeysOf<B_CameraSynchronizationMemberStatus> ? true : false>;
type B_CameraSynchronizationGroupStatus = components['schemas']['CameraSynchronizationGroupStatus'];
export type K_CameraSynchronizationGroupStatus = Assert<KeysOf<CameraSynchronizationGroupStatus> extends KeysOf<B_CameraSynchronizationGroupStatus> ? true : false>;
type B_CameraSynchronizationCaptureResult = components['schemas']['CameraSynchronizationCaptureResult'];
export type K_CameraSynchronizationCaptureResult = Assert<KeysOf<CameraSynchronizationCaptureResult> extends KeysOf<B_CameraSynchronizationCaptureResult> ? true : false>;
type B_CameraSynchronizationCommissioningAssessment = components['schemas']['CameraSynchronizationCommissioningAssessment'];
export type K_CameraSynchronizationCommissioningAssessment = Assert<KeysOf<CameraSynchronizationCommissioningAssessment> extends KeysOf<B_CameraSynchronizationCommissioningAssessment> ? true : false>;
type B_CameraSynchronizationStatistics = components['schemas']['CameraSynchronizationStatistics'];
export type K_CameraSynchronizationStatistics = Assert<KeysOf<CameraSynchronizationStatistics> extends KeysOf<B_CameraSynchronizationStatistics> ? true : false>;
type B_CameraSynchronizationPtpCameraDiagnostics = components['schemas']['CameraSynchronizationPtpCameraDiagnostics'];
export type K_CameraSynchronizationPtpCameraDiagnostics = Assert<KeysOf<CameraSynchronizationPtpCameraDiagnostics> extends KeysOf<B_CameraSynchronizationPtpCameraDiagnostics> ? true : false>;
type B_CameraSynchronizationPtpTrendPoint = components['schemas']['CameraSynchronizationPtpTrendPoint'];
export type K_CameraSynchronizationPtpTrendPoint = Assert<KeysOf<CameraSynchronizationPtpTrendPoint> extends KeysOf<B_CameraSynchronizationPtpTrendPoint> ? true : false>;
type B_CameraSynchronizationPtpDiagnostics = components['schemas']['CameraSynchronizationPtpDiagnostics'];
export type K_CameraSynchronizationPtpDiagnostics = Assert<KeysOf<CameraSynchronizationPtpDiagnostics> extends KeysOf<B_CameraSynchronizationPtpDiagnostics> ? true : false>;
type B_GigEHostAddress = components['schemas']['GigEHostAddress'];
export type K_GigEHostAddress = Assert<KeysOf<GigEHostAddress> extends KeysOf<B_GigEHostAddress> ? true : false>;
type B_GigEHostNetworkInterface = components['schemas']['GigEHostNetworkInterface'];
export type K_GigEHostNetworkInterface = Assert<KeysOf<GigEHostNetworkInterface> extends KeysOf<B_GigEHostNetworkInterface> ? true : false>;
type B_GigECameraNetworkDiagnostic = components['schemas']['GigECameraNetworkDiagnostic'];
export type K_GigECameraNetworkDiagnostic = Assert<KeysOf<GigECameraNetworkDiagnostic> extends KeysOf<B_GigECameraNetworkDiagnostic> ? true : false>;
type B_GigENicLoadDiagnostic = components['schemas']['GigENicLoadDiagnostic'];
export type K_GigENicLoadDiagnostic = Assert<KeysOf<GigENicLoadDiagnostic> extends KeysOf<B_GigENicLoadDiagnostic> ? true : false>;
type B_GigENetworkTrendPoint = components['schemas']['GigENetworkTrendPoint'];
export type K_GigENetworkTrendPoint = Assert<KeysOf<GigENetworkTrendPoint> extends KeysOf<B_GigENetworkTrendPoint> ? true : false>;
type B_GigENetworkAssessment = components['schemas']['GigENetworkAssessment'];
export type K_GigENetworkAssessment = Assert<KeysOf<GigENetworkAssessment> extends KeysOf<B_GigENetworkAssessment> ? true : false>;
type B_GigENetworkDiagnostics = components['schemas']['GigENetworkDiagnostics'];
export type K_GigENetworkDiagnostics = Assert<KeysOf<GigENetworkDiagnostics> extends KeysOf<B_GigENetworkDiagnostics> ? true : false>;
type B_GigENetworkCommissioningReport = components['schemas']['GigENetworkCommissioningReport'];
export type K_GigENetworkCommissioningReport = Assert<KeysOf<GigENetworkCommissioningReport> extends KeysOf<B_GigENetworkCommissioningReport> ? true : false>;
type B_CameraSynchronizationCommissioningCameraEvidence = components['schemas']['CameraSynchronizationCommissioningCameraEvidence'];
export type K_CameraSynchronizationCommissioningCameraEvidence = Assert<KeysOf<CameraSynchronizationCommissioningCameraEvidence> extends KeysOf<B_CameraSynchronizationCommissioningCameraEvidence> ? true : false>;
type B_CameraSynchronizationCommissioningReport = components['schemas']['CameraSynchronizationCommissioningReport'];
export type K_CameraSynchronizationCommissioningReport = Assert<KeysOf<CameraSynchronizationCommissioningReport> extends KeysOf<B_CameraSynchronizationCommissioningReport> ? true : false>;
type B_CameraSynchronizationCommissioningTest = components['schemas']['CameraSynchronizationCommissioningTest'];
export type K_CameraSynchronizationCommissioningTest = Assert<KeysOf<CameraSynchronizationCommissioningTest> extends KeysOf<B_CameraSynchronizationCommissioningTest> ? true : false>;
type B_CameraSynchronizationRunRecord = components['schemas']['CameraSynchronizationRunRecord'];
export type K_CameraSynchronizationRunRecord = Assert<KeysOf<CameraSynchronizationRunRecord> extends KeysOf<B_CameraSynchronizationRunRecord> ? true : false>;
type B_WorkflowModulePort = components['schemas']['WorkflowModulePort'];
export type K_WorkflowModulePort = Assert<KeysOf<WorkflowModulePort> extends KeysOf<B_WorkflowModulePort> ? true : false>;
type B_WorkflowModuleVersion = components['schemas']['WorkflowModuleVersion'];
export type K_WorkflowModuleVersion = Assert<KeysOf<WorkflowModuleVersion> extends KeysOf<B_WorkflowModuleVersion> ? true : false>;
type B_WorkflowModuleDescriptor = components['schemas']['WorkflowModuleDescriptor'];
export type K_WorkflowModuleDescriptor = Assert<KeysOf<WorkflowModuleDescriptor> extends KeysOf<B_WorkflowModuleDescriptor> ? true : false>;
type B_ExtractWorkflowModuleResult = components['schemas']['ExtractWorkflowModuleResult'];
export type K_ExtractWorkflowModuleResult = Assert<KeysOf<ExtractWorkflowModuleResult> extends KeysOf<B_ExtractWorkflowModuleResult> ? true : false>;
type B_InvestigationCaseSummary = components['schemas']['InvestigationCaseSummary'];
export type K_InvestigationCaseSummary = Assert<KeysOf<InvestigationCaseSummary> extends KeysOf<B_InvestigationCaseSummary> ? true : false>;
type B_InvestigationCaseRun = components['schemas']['InvestigationCaseRun'];
export type K_InvestigationCaseRun = Assert<KeysOf<InvestigationCaseRun> extends KeysOf<B_InvestigationCaseRun> ? true : false>;
type B_InvestigationVerificationEvidence = components['schemas']['InvestigationVerificationEvidence'];
export type K_InvestigationVerificationEvidence = Assert<KeysOf<InvestigationVerificationEvidence> extends KeysOf<B_InvestigationVerificationEvidence> ? true : false>;
type B_InvestigationCaseDetail = components['schemas']['InvestigationCaseDetail'];
export type K_InvestigationCaseDetail = Assert<KeysOf<InvestigationCaseDetail> extends KeysOf<B_InvestigationCaseDetail> ? true : false>;
type B_InvestigationTrendPoint = components['schemas']['InvestigationTrendPoint'];
export type K_InvestigationTrendPoint = Assert<KeysOf<InvestigationTrendPoint> extends KeysOf<B_InvestigationTrendPoint> ? true : false>;
type B_InvestigationSignatureTrend = components['schemas']['InvestigationSignatureTrend'];
export type K_InvestigationSignatureTrend = Assert<KeysOf<InvestigationSignatureTrend> extends KeysOf<B_InvestigationSignatureTrend> ? true : false>;
type B_InvestigationTrendDashboard = components['schemas']['InvestigationTrendDashboard'];
export type K_InvestigationTrendDashboard = Assert<KeysOf<InvestigationTrendDashboard> extends KeysOf<B_InvestigationTrendDashboard> ? true : false>;
type B_NodeSpanRecord = components['schemas']['NodeSpanRecord'];
export type K_NodeSpanRecord = Assert<KeysOf<NodeSpanRecord> extends KeysOf<B_NodeSpanRecord> ? true : false>;
type B_NodeSpanSnapshot = components['schemas']['NodeSpanSnapshot'];
export type K_NodeSpanSnapshot = Assert<KeysOf<NodeSpanSnapshot> extends KeysOf<B_NodeSpanSnapshot> ? true : false>;

// 以下类型刻意不绑定（键集不是后端子集），原因见 scripts/generate-type-bindings.mjs 的 EXEMPT：
//  - PortDescriptor: UI 注入 label（localizeCatalog 渲染时填显示名，后端不返回）
//  - OfflineReplayResult: 交叉类型 RunResult & {...}，继承的视图字段（errorCode/engine 等）不在该 schema 内
//  - WorkflowModuleParameter: 交叉类型 ParameterDescriptor & {...}，继承的视图字段（advanced/variants 等）不在该 schema 内
