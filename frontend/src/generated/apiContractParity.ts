/**
 * 编译期契约对账：后端 OpenAPI 生成的类型必须能喂给前端手写类型。
 * 只关心**响应方向**（后端 DTO → 前端消费类型），所以断言 Backend extends Frontend。
 * 后端删字段/改类型时这里报错，提醒同步 src/types.ts 或后端 Produces<T>。
 * 该文件没有运行时导出，仅参与 `tsc -b` 类型检查。
 */
import type { components } from './api-types';
import type * as Frontend from '../types';
import type { NodeCatalogItem, NodeRunReport, ParameterDescriptor, RunResult, RunTraceRecord, WorkflowPayload } from '../types';

type Schema<K extends keyof components['schemas']> = components['schemas'][K];

/**
 * .NET 10 的 OpenAPI 把 double 生成成 `number | string`（JsonNumberHandling 兼容读字符串），
 * 实际响应是 JSON 数字。对账前把该联合收敛回 number，避免所有数值字段都误报。
 */
type Normalize<T> = [NonNullable<T>] extends [string]
  ? T
  : [NonNullable<T>] extends [number]
    ? T
    : [NonNullable<T>] extends [string | number]
      ? Exclude<T, string>
      : T extends readonly (infer U)[]
        ? Normalize<U>[]
        : T extends object
          ? { [K in keyof T]: Normalize<T[K]> }
          : T;

/** 目录参数 type 在 schema 里是宽 string；前端用封闭联合驱动表单/校验，这里只对比其余字段。 */
type NormalizeCatalog<T> = Omit<T, 'parameters'> & {
  parameters: Array<Omit<T extends { parameters: Array<infer P> } ? P : never, 'type'> & { type: ParameterDescriptor['type'] }>;
};

type BackendFitsFrontend<Frontend, Backend extends Frontend> = Backend;

export type WorkflowDefinitionParity = BackendFitsFrontend<WorkflowPayload, Normalize<Schema<'WorkflowDefinition'>>>;
export type NodeCatalogItemParity = BackendFitsFrontend<NodeCatalogItem, NormalizeCatalog<Normalize<Schema<'NodeCatalogItem'>>>>;
export type RunResultParity = BackendFitsFrontend<RunResult, Normalize<Schema<'WorkflowRunResult'>>>;
type NormalizeTrace<T> = Omit<T, 'disposition'> & { disposition: RunTraceRecord['disposition'] };
export type RunTraceRecordParity = BackendFitsFrontend<RunTraceRecord, NormalizeTrace<Normalize<Schema<'RunTraceRecord'>>>>;
export type NodeRunReportParity = BackendFitsFrontend<NodeRunReport, Normalize<Schema<'NodeRunReport'>>>;

/**
 * 其余与后端 schema 同名、结构已对齐的 DTO（45 个）。
 * 未列入的是已单独对账的 NodeCatalogItem / ParameterDescriptor / RunTraceRecord 三个；
 * required 表达已由 RequiredPropertiesSchemaTransformer 修正（带默认值/计算属性的非空字段），
 * 因此 CameraSettings / CameraCommissioningProfile / RunCounterPersistenceStatus / CalibrationWorkspaceRequest 也在此列。
 * TracePage 单独用 NormalizeTraceItems 覆盖（items 内的 disposition 与 RunTraceRecord 同样收窄）。
 */
export type Parity_AlarmRecord = BackendFitsFrontend<Frontend.AlarmRecord, Normalize<Schema<'AlarmRecord'>>>;
export type Parity_AssetKind = BackendFitsFrontend<Frontend.AssetKind, Normalize<Schema<'AssetKind'>>>;
export type Parity_CalibrationWorkspacePoint = BackendFitsFrontend<Frontend.CalibrationWorkspacePoint, Normalize<Schema<'CalibrationWorkspacePoint'>>>;
export type Parity_CameraOutputPixelFormat = BackendFitsFrontend<Frontend.CameraOutputPixelFormat, Normalize<Schema<'CameraOutputPixelFormat'>>>;
export type Parity_CameraTriggerMode = BackendFitsFrontend<Frontend.CameraTriggerMode, Normalize<Schema<'CameraTriggerMode'>>>;
export type Parity_ControlFlowDecision = BackendFitsFrontend<Frontend.ControlFlowDecision, Normalize<Schema<'ControlFlowDecision'>>>;
export type Parity_DeviceConnectionState = BackendFitsFrontend<Frontend.DeviceConnectionState, Normalize<Schema<'DeviceConnectionState'>>>;
export type Parity_DeviceDescriptor = BackendFitsFrontend<Frontend.DeviceDescriptor, Normalize<Schema<'DeviceDescriptor'>>>;
export type Parity_DeviceDriverCapabilities = BackendFitsFrontend<Frontend.DeviceDriverCapabilities, Normalize<Schema<'DeviceDriverCapabilities'>>>;
export type Parity_DeviceProtocolDiagnostics = BackendFitsFrontend<Frontend.DeviceProtocolDiagnostics, Normalize<Schema<'DeviceProtocolDiagnostics'>>>;
export type Parity_DeviceRuntimeSettings = BackendFitsFrontend<Frontend.DeviceRuntimeSettings, Normalize<Schema<'DeviceRuntimeSettings'>>>;
export type Parity_DeviceRuntimeStats = BackendFitsFrontend<Frontend.DeviceRuntimeStats, Normalize<Schema<'DeviceRuntimeStats'>>>;
export type Parity_DeviceTagDataType = BackendFitsFrontend<Frontend.DeviceTagDataType, Normalize<Schema<'DeviceTagDataType'>>>;
export type Parity_DeviceTagDefinition = BackendFitsFrontend<Frontend.DeviceTagDefinition, Normalize<Schema<'DeviceTagDefinition'>>>;
export type Parity_DeviceTagQuality = BackendFitsFrontend<Frontend.DeviceTagQuality, Normalize<Schema<'DeviceTagQuality'>>>;
export type Parity_DeviceTagSample = BackendFitsFrontend<Frontend.DeviceTagSample, Normalize<Schema<'DeviceTagSample'>>>;
export type Parity_HardwareProvenanceDeclaration = BackendFitsFrontend<Frontend.HardwareProvenanceDeclaration, Normalize<Schema<'HardwareProvenanceDeclaration'>>>;
export type Parity_JobDescriptor = BackendFitsFrontend<Frontend.JobDescriptor, Normalize<Schema<'JobDescriptor'>>>;
export type Parity_JobVersionSnapshot = BackendFitsFrontend<Frontend.JobVersionSnapshot, Normalize<Schema<'JobVersionSnapshot'>>>;
export type Parity_RuntimeDependencyValidation = BackendFitsFrontend<Frontend.RuntimeDependencyValidation, Normalize<Schema<'RuntimeDependencyValidation'>>>;
export type Parity_JobVersionInfo = BackendFitsFrontend<Frontend.JobVersionInfo, Normalize<Schema<'JobVersionInfo'>>>;
export type Parity_JobVersionValidationInfo = BackendFitsFrontend<Frontend.JobVersionValidationInfo, Normalize<Schema<'JobVersionValidationInfo'>>>;
export type Parity_NodeFrameRule = BackendFitsFrontend<Frontend.NodeFrameRule, Normalize<Schema<'NodeFrameRule'>>>;
export type Parity_NodeImageFramePolicy = BackendFitsFrontend<Frontend.NodeImageFramePolicy, Normalize<Schema<'NodeImageFramePolicy'>>>;
export type Parity_NodeMaturity = BackendFitsFrontend<Frontend.NodeMaturity, Normalize<Schema<'NodeMaturity'>>>;
export type Parity_NodeOutputFrameRule = BackendFitsFrontend<Frontend.NodeOutputFrameRule, Normalize<Schema<'NodeOutputFrameRule'>>>;
export type Parity_NodeOutputFrameSource = BackendFitsFrontend<Frontend.NodeOutputFrameSource, Normalize<Schema<'NodeOutputFrameSource'>>>;
export type Parity_NodeTransformFrameDeclaration = BackendFitsFrontend<Frontend.NodeTransformFrameDeclaration, Normalize<Schema<'NodeTransformFrameDeclaration'>>>;
export type Parity_NodeTransformFramePolicy = BackendFitsFrontend<Frontend.NodeTransformFramePolicy, Normalize<Schema<'NodeTransformFramePolicy'>>>;
export type Parity_ParameterOption = BackendFitsFrontend<Frontend.ParameterOption, Normalize<Schema<'ParameterOption'>>>;
export type Parity_PluginBenchmarkPoolResult = BackendFitsFrontend<Frontend.PluginBenchmarkPoolResult, Normalize<Schema<'PluginBenchmarkPoolResult'>>>;
export type Parity_PluginBenchmarkRecommendation = BackendFitsFrontend<Frontend.PluginBenchmarkRecommendation, Normalize<Schema<'PluginBenchmarkRecommendation'>>>;
export type Parity_PluginBenchmarkRegressionPolicy = BackendFitsFrontend<Frontend.PluginBenchmarkRegressionPolicy, Normalize<Schema<'PluginBenchmarkRegressionPolicy'>>>;
export type Parity_PluginWorkerLatencyDistribution = BackendFitsFrontend<Frontend.PluginWorkerLatencyDistribution, Normalize<Schema<'PluginWorkerLatencyDistribution'>>>;
export type Parity_PluginWorkerPerformanceProfile = BackendFitsFrontend<Frontend.PluginWorkerPerformanceProfile, Normalize<Schema<'PluginWorkerPerformanceProfile'>>>;
export type Parity_PluginWorkerToolPerformanceProfile = BackendFitsFrontend<Frontend.PluginWorkerToolPerformanceProfile, Normalize<Schema<'PluginWorkerToolPerformanceProfile'>>>;
export type Parity_PortDescriptor = BackendFitsFrontend<Frontend.PortDescriptor, Normalize<Schema<'PortDescriptor'>>>;
export type Parity_ProductionRuntimeConfig = BackendFitsFrontend<Frontend.ProductionRuntimeConfig, Normalize<Schema<'ProductionRuntimeConfig'>>>;
/** 生产状态在 schema 里是宽 string；前端用封闭联合驱动状态徽标。 */
type NormalizeProductionStatus<T> = Omit<Normalize<T>, 'state'> & { state: Frontend.ProductionRuntimeStatus['state'] };
export type Parity_ProductionRuntimeStatus = BackendFitsFrontend<Frontend.ProductionRuntimeStatus, NormalizeProductionStatus<Schema<'ProductionRuntimeStatus'>>>;
export type Parity_ProductionPtpGuardSnapshot = BackendFitsFrontend<Frontend.ProductionPtpGuardSnapshot, Normalize<Schema<'ProductionPtpGuardSnapshot'>>>;
export type Parity_ProductionSynchronizationGuardSnapshot = BackendFitsFrontend<Frontend.ProductionSynchronizationGuardSnapshot, Normalize<Schema<'ProductionSynchronizationGuardSnapshot'>>>;
export type Parity_PublicationEvent = BackendFitsFrontend<Frontend.PublicationEvent, Normalize<Schema<'PublicationEvent'>>>;
export type Parity_RobotCapabilities = BackendFitsFrontend<Frontend.RobotCapabilities, Normalize<Schema<'RobotCapabilities'>>>;
export type Parity_RobotHandshakeSignals = BackendFitsFrontend<Frontend.RobotHandshakeSignals, Normalize<Schema<'RobotHandshakeSignals'>>>;
export type Parity_RobotRuntimeSettings = BackendFitsFrontend<Frontend.RobotRuntimeSettings, Normalize<Schema<'RobotRuntimeSettings'>>>;
export type Parity_ValidationRunSummary = BackendFitsFrontend<Frontend.ValidationRunSummary, Normalize<Schema<'ValidationRunSummary'>>>;
export type Parity_VisionOverlay = BackendFitsFrontend<Frontend.VisionOverlay, Normalize<Schema<'VisionOverlay'>>>;
export type Parity_VisionToolCapabilities = BackendFitsFrontend<Frontend.VisionToolCapabilities, Normalize<Schema<'VisionToolCapabilities'>>>;
export type Parity_VisionValueSnapshot = BackendFitsFrontend<Frontend.VisionValueSnapshot, Normalize<Schema<'VisionValueSnapshot'>>>;
export type Parity_CameraSettings = BackendFitsFrontend<Frontend.CameraSettings, Normalize<Schema<'CameraSettings'>>>;
export type Parity_CameraCommissioningProfile = BackendFitsFrontend<Frontend.CameraCommissioningProfile, Normalize<Schema<'CameraCommissioningProfile'>>>;
/** 相机状态在 schema 里是宽 string；前端用封闭联合驱动 UI，仅收窄该字段。 */
type NormalizeCameraDescriptor<T> = Omit<Normalize<T>, 'state'> & { state: Frontend.CameraDescriptor['state'] };
export type Parity_CameraDescriptor = BackendFitsFrontend<Frontend.CameraDescriptor, NormalizeCameraDescriptor<Schema<'CameraDescriptor'>>>;
export type Parity_CameraTransportTelemetry = BackendFitsFrontend<Frontend.CameraTransportTelemetry, Normalize<Schema<'CameraTransportTelemetry'>>>;
export type Parity_CameraDiscoveredDevice = BackendFitsFrontend<Frontend.CameraDiscoveredDevice, Normalize<Schema<'CameraDiscoveredDevice'>>>;
export type Parity_MediaLibraryStatus = BackendFitsFrontend<Frontend.MediaLibraryStatus, Normalize<Schema<'MediaLibraryStatus'>>>;
export type Parity_MediaCollectionDescriptor = BackendFitsFrontend<Frontend.MediaCollectionDescriptor, Normalize<Schema<'MediaCollectionDescriptor'>>>;
/** label 在 schema 里是宽 string；前端用封闭联合驱动筛选。 */
type NormalizeMediaItem<T> = Omit<Normalize<T>, 'label'> & { label: Frontend.MediaItemDescriptor['label'] };
export type Parity_MediaItemDescriptor = BackendFitsFrontend<Frontend.MediaItemDescriptor, NormalizeMediaItem<Schema<'MediaItemDescriptor'>>>;
export type Parity_CameraSynchronizationGroup = BackendFitsFrontend<Frontend.CameraSynchronizationGroup, Normalize<Schema<'CameraSynchronizationGroup'>>>;
export type Parity_CameraSynchronizationGroupStatus = BackendFitsFrontend<Frontend.CameraSynchronizationGroupStatus, Normalize<Schema<'CameraSynchronizationGroupStatus'>>>;
export type Parity_CameraSynchronizationRunRecord = BackendFitsFrontend<Frontend.CameraSynchronizationRunRecord, Normalize<Schema<'CameraSynchronizationRunRecord'>>>;
export type Parity_CameraSynchronizationStatistics = BackendFitsFrontend<Frontend.CameraSynchronizationStatistics, Normalize<Schema<'CameraSynchronizationStatistics'>>>;
export type Parity_CameraSynchronizationPtpDiagnostics = BackendFitsFrontend<Frontend.CameraSynchronizationPtpDiagnostics, Normalize<Schema<'CameraSynchronizationPtpDiagnostics'>>>;
export type Parity_GigENetworkDiagnostics = BackendFitsFrontend<Frontend.GigENetworkDiagnostics, Normalize<Schema<'GigENetworkDiagnostics'>>>;
export type Parity_CameraSynchronizationCommissioningTest = BackendFitsFrontend<Frontend.CameraSynchronizationCommissioningTest, Normalize<Schema<'CameraSynchronizationCommissioningTest'>>>;
export type Parity_CameraSynchronizationCommissioningReport = BackendFitsFrontend<Frontend.CameraSynchronizationCommissioningReport, Normalize<Schema<'CameraSynchronizationCommissioningReport'>>>;
export type Parity_CameraCommissioningSnapshot = BackendFitsFrontend<Frontend.CameraCommissioningSnapshot, Normalize<Schema<'CameraCommissioningSnapshot'>>>;
export type Parity_CameraFeatureDescriptor = BackendFitsFrontend<Frontend.CameraFeatureDescriptor, Normalize<Schema<'CameraFeatureDescriptor'>>>;
export type Parity_CameraFeatureProfileRecord = BackendFitsFrontend<Frontend.CameraFeatureProfileRecord, Normalize<Schema<'CameraFeatureProfileRecord'>>>;
export type Parity_CameraSynchronizationCaptureResult = BackendFitsFrontend<Frontend.CameraSynchronizationCaptureResult, Normalize<Schema<'CameraSynchronizationCaptureResult'>>>;
export type Parity_GigENetworkCommissioningReport = BackendFitsFrontend<Frontend.GigENetworkCommissioningReport, Normalize<Schema<'GigENetworkCommissioningReport'>>>;
export type Parity_PluginPackageVersionInfo = BackendFitsFrontend<Frontend.PluginPackageVersionInfo, Normalize<Schema<'PluginPackageVersionInfo'>>>;
export type Parity_TrustedPluginPublisher = BackendFitsFrontend<Frontend.TrustedPluginPublisher, Normalize<Schema<'TrustedPluginPublisher'>>>;
export type Parity_PluginPackagePreflightResult = BackendFitsFrontend<Frontend.PluginPackagePreflightResult, Normalize<Schema<'PluginPackagePreflightResult'>>>;
/** 模块参数 type 在 schema 里是宽 string；前端复用节点参数表单的封闭联合。 */
type NormalizeWorkflowModuleVersion<T> = Omit<Normalize<T>, 'parameters'> & {
  parameters: Array<Omit<Normalize<T extends { parameters: Array<infer P> } ? P : never>, 'type'> & { type: ParameterDescriptor['type'] }>;
};
type NormalizeWorkflowModuleDescriptor<T> = Omit<Normalize<T>, 'versions'> & {
  versions: Array<NormalizeWorkflowModuleVersion<T extends { versions: Array<infer V> } ? V : never>>;
};
type NormalizeExtractWorkflowModuleResult<T> = Omit<Normalize<T>, 'module' | 'version'> & {
  module: NormalizeWorkflowModuleDescriptor<T extends { module: infer M } ? M : never>;
  version: NormalizeWorkflowModuleVersion<T extends { version: infer V } ? V : never>;
};
export type Parity_WorkflowModuleDescriptor = BackendFitsFrontend<Frontend.WorkflowModuleDescriptor, NormalizeWorkflowModuleDescriptor<Schema<'WorkflowModuleDescriptor'>>>;
export type Parity_WorkflowModuleVersion = BackendFitsFrontend<Frontend.WorkflowModuleVersion, NormalizeWorkflowModuleVersion<Schema<'WorkflowModuleVersion'>>>;
export type Parity_ExtractWorkflowModuleResult = BackendFitsFrontend<Frontend.ExtractWorkflowModuleResult, NormalizeExtractWorkflowModuleResult<Schema<'ExtractWorkflowModuleResult'>>>;
export type Parity_InvestigationCaseSummary = BackendFitsFrontend<Frontend.InvestigationCaseSummary, Normalize<Schema<'InvestigationCaseSummary'>>>;
export type Parity_InvestigationCaseDetail = BackendFitsFrontend<Frontend.InvestigationCaseDetail, Normalize<Schema<'InvestigationCaseDetail'>>>;
export type Parity_InvestigationVerificationEvidence = BackendFitsFrontend<Frontend.InvestigationVerificationEvidence, Normalize<Schema<'InvestigationVerificationEvidence'>>>;
export type Parity_InvestigationTrendDashboard = BackendFitsFrontend<Frontend.InvestigationTrendDashboard, Normalize<Schema<'InvestigationTrendDashboard'>>>;
export type Parity_JobVersionDiff = BackendFitsFrontend<Frontend.JobVersionDiff, Normalize<Schema<'JobVersionDiff'>>>;
export type Parity_ValidationCandidateInfo = BackendFitsFrontend<Frontend.ValidationCandidateInfo, Normalize<Schema<'ValidationCandidateInfo'>>>;
export type Parity_PluginBenchmarkRun = BackendFitsFrontend<Frontend.PluginBenchmarkRun, Normalize<Schema<'PluginBenchmarkRun'>>>;
export type Parity_PluginWorkerStatus = BackendFitsFrontend<Frontend.PluginWorkerStatus, Normalize<Schema<'PluginWorkerStatus'>>>;
/** scope 在 schema 里是宽 string；前端用 'recipe' 封闭联合。 */
type NormalizeParameterValueSet<T> = Omit<Normalize<T>, 'scope'> & { scope: Frontend.ParameterValueSet['scope'] };
export type Parity_ParameterValueSet = BackendFitsFrontend<Frontend.ParameterValueSet, NormalizeParameterValueSet<Schema<'ParameterValueSet'>>>;
export type Parity_ResolvedRecipeParameterization = BackendFitsFrontend<Frontend.ResolvedRecipeParameterization, Normalize<Schema<'ResolvedRecipeParameterization'>>>;
/** sourceKind 在 schema 里是宽 string；前端用 MEDIA/TRACE 联合。 */
type NormalizeParameterTuningPreview<T> = Omit<Normalize<T>, 'sourceKind' | 'expectedDisposition'> & {
  sourceKind: Frontend.ParameterTuningPreviewResult['sourceKind'];
  expectedDisposition: Frontend.ParameterTuningPreviewResult['expectedDisposition'];
};
export type Parity_ParameterTuningPreviewResult = BackendFitsFrontend<Frontend.ParameterTuningPreviewResult, NormalizeParameterTuningPreview<Schema<'ParameterTuningPreviewResult'>>>;
export type Parity_RunCounterPersistenceStatus = BackendFitsFrontend<Frontend.RunCounterPersistenceStatus, Normalize<Schema<'RunCounterPersistenceStatus'>>>;
export type Parity_CalibrationWorkspaceRequest = BackendFitsFrontend<Frontend.CalibrationWorkspaceRequest, Normalize<Schema<'CalibrationWorkspaceRequest'>>>;
export type Parity_CalibrationWorkspaceResult = BackendFitsFrontend<Frontend.CalibrationWorkspaceResult, Normalize<Schema<'CalibrationWorkspaceResult'>>>;
export type Parity_CalibrationAssetDescriptor = BackendFitsFrontend<Frontend.CalibrationAssetDescriptor, Normalize<Schema<'CalibrationAssetDescriptor'>>>;
export type Parity_CalibrationVersionSnapshot = BackendFitsFrontend<Frontend.CalibrationVersionSnapshot, Normalize<Schema<'CalibrationVersionSnapshot'>>>;
export type Parity_CameraIntrinsicAssetDescriptor = BackendFitsFrontend<Frontend.CameraIntrinsicAssetDescriptor, Normalize<Schema<'CameraIntrinsicAssetDescriptor'>>>;
export type Parity_CameraIntrinsicVersionSnapshot = BackendFitsFrontend<Frontend.CameraIntrinsicVersionSnapshot, Normalize<Schema<'CameraIntrinsicVersionSnapshot'>>>;
export type Parity_RobotDescriptor = BackendFitsFrontend<Frontend.RobotDescriptor, Normalize<Schema<'RobotDescriptor'>>>;
export type Parity_RobotCommandTraceRecord = BackendFitsFrontend<Frontend.RobotCommandTraceRecord, Normalize<Schema<'RobotCommandTraceRecord'>>>;
type NormalizeTraceItems<T> = Omit<Normalize<T>, 'items'> & { items: Array<NormalizeTrace<Normalize<T extends { items: Array<infer I> } ? I : never>>> };
export type Parity_TracePage = BackendFitsFrontend<Frontend.TracePage, NormalizeTraceItems<Schema<'TracePage'>>>;
export type Parity_ValidationDatasetSummary = BackendFitsFrontend<Frontend.ValidationDatasetSummary, Normalize<Schema<'ValidationDatasetSummary'>>>;

type NormalizeValidationItem<T extends { sourceKind: string; expectedDisposition: string }> =
  Omit<Normalize<T>, 'sourceKind' | 'expectedDisposition'> & {
    sourceKind: Frontend.ValidationDatasetItem['sourceKind'];
    expectedDisposition: Frontend.ValidationDatasetItem['expectedDisposition'];
  };
type BackendValidationDetail = Normalize<Schema<'ValidationDatasetDetail'>>;
type NormalizeValidationDetail = Omit<BackendValidationDetail, 'items'> & {
  items: Array<NormalizeValidationItem<BackendValidationDetail['items'][number]>>;
};
type BackendValidationRun = Normalize<Schema<'ValidationRunRecord'>>;
type NormalizeValidationRun = Omit<BackendValidationRun, 'results'> & {
  results?: Array<NormalizeValidationItem<NonNullable<BackendValidationRun['results']>[number]>> | null;
};
export type Parity_ValidationDatasetItem = BackendFitsFrontend<Frontend.ValidationDatasetItem, NormalizeValidationItem<Schema<'ValidationDatasetItem'>>>;
export type Parity_ValidationDatasetDetail = BackendFitsFrontend<Frontend.ValidationDatasetDetail, NormalizeValidationDetail>;
export type Parity_ValidationRunRecord = BackendFitsFrontend<Frontend.ValidationRunRecord, NormalizeValidationRun>;
export type Parity_ValidationResultRecord = BackendFitsFrontend<Frontend.ValidationResultRecord, NormalizeValidationItem<Schema<'ValidationResultRecord'>>>;
export type Parity_FailureSignatureAnalysis = BackendFitsFrontend<Frontend.FailureSignatureAnalysis, Normalize<Schema<'FailureSignatureAnalysis'>>>;
export type Parity_FailureSignatureOccurrence = BackendFitsFrontend<Frontend.FailureSignatureOccurrence, Normalize<Schema<'FailureSignatureOccurrence'>>>;
export type Parity_HardwareProvenanceData = BackendFitsFrontend<Frontend.HardwareProvenanceData, Normalize<Schema<'HardwareProvenanceData'>>>;
export type Parity_NodeReplayComparison = BackendFitsFrontend<Frontend.NodeReplayComparison, Normalize<Schema<'NodeReplayComparison'>>>;
export type Parity_OfflineReplayResult = BackendFitsFrontend<Frontend.OfflineReplayResult, Normalize<Schema<'OfflineReplayResult'>>>;
export type Parity_SchemaMigrationHistoryEntry = BackendFitsFrontend<Frontend.SchemaMigrationHistoryEntry, Normalize<Schema<'SchemaMigrationHistoryEntry'>>>;
export type Parity_SchemaMigrationStatus = BackendFitsFrontend<Frontend.SchemaMigrationStatus, Normalize<Schema<'SchemaMigrationStatus'>>>;
export type Parity_StorageBackupDescriptor = BackendFitsFrontend<Frontend.StorageBackupDescriptor, Normalize<Schema<'StorageBackupDescriptor'>>>;
export type Parity_StorageCapacityStatus = BackendFitsFrontend<Frontend.StorageCapacityStatus, Normalize<Schema<'StorageCapacityStatus'>>>;
export type Parity_StorageRestoreResult = BackendFitsFrontend<Frontend.StorageRestoreResult, Normalize<Schema<'StorageRestoreResult'>>>;
export type Parity_TraceNodeDelta = BackendFitsFrontend<Frontend.TraceNodeDelta, Normalize<Schema<'TraceNodeDelta'>>>;
export type Parity_TraceRetentionOptions = BackendFitsFrontend<Frontend.TraceRetentionOptions, Normalize<Schema<'TraceRetentionOptions'>>>;
export type Parity_TraceStats = BackendFitsFrontend<Frontend.TraceStats, Normalize<Schema<'TraceStats'>>>;
export type Parity_TraceStorageStatus = BackendFitsFrontend<Frontend.TraceStorageStatus, Normalize<Schema<'TraceStorageStatus'>>>;

// 追溯/存储/回放域（2026-11-10）：后端开放 string、前端收窄为联合的字段在此显式归一。
type NormalizeNodeTimeline<T> = Omit<Normalize<T>, 'phase'> & { phase: Frontend.NodeTimelineObservation['phase'] };
type NormalizeNodePerformance<T> = Omit<Normalize<T>, 'status'> & { status: Frontend.NodePerformanceBaseline['status'] };
type NormalizeHardwareSnapshot<T> = Omit<Normalize<T>, 'kind' | 'completeness'> & {
  kind: Frontend.HardwareProvenanceSnapshot['kind'];
  completeness: Frontend.HardwareProvenanceSnapshot['completeness'];
};
type NormalizeReplayContext<T> = Omit<Normalize<T>, 'trace'> & {
  trace: NormalizeTrace<Normalize<T extends { trace: infer R } ? R : never>>;
};
type NormalizeObservability<T> = Omit<Normalize<T>, 'timeline' | 'baselines'> & {
  timeline: Array<NormalizeNodeTimeline<Normalize<T extends { timeline: Array<infer I> } ? I : never>>>;
  baselines: Array<NormalizeNodePerformance<Normalize<T extends { baselines: Array<infer I> } ? I : never>>>;
};

export type Parity_HardwareProvenanceSnapshot = BackendFitsFrontend<Frontend.HardwareProvenanceSnapshot, NormalizeHardwareSnapshot<Schema<'HardwareProvenanceSnapshot'>>>;
export type Parity_NodePerformanceBaseline = BackendFitsFrontend<Frontend.NodePerformanceBaseline, NormalizeNodePerformance<Schema<'NodePerformanceBaseline'>>>;
export type Parity_NodeTimelineObservation = BackendFitsFrontend<Frontend.NodeTimelineObservation, NormalizeNodeTimeline<Schema<'NodeTimelineObservation'>>>;
export type Parity_OfflineReplayContext = BackendFitsFrontend<Frontend.OfflineReplayContext, NormalizeReplayContext<Schema<'OfflineReplayContext'>>>;
export type Parity_RunObservabilitySnapshot = BackendFitsFrontend<Frontend.RunObservabilitySnapshot, NormalizeObservability<Schema<'RunObservabilitySnapshot'>>>;
export type Parity_AssetEventEnvelope = BackendFitsFrontend<Frontend.AssetEventEnvelope, Normalize<Schema<'AssetEventEnvelope'>>>;
export type Parity_AssetEventSeverity = BackendFitsFrontend<Frontend.AssetEventSeverity, Normalize<Schema<'AssetEventSeverity'>>>;
export type Parity_AssetHealthLevel = BackendFitsFrontend<Frontend.AssetHealthLevel, Normalize<Schema<'AssetHealthLevel'>>>;
export type Parity_AssetHealthSnapshot = BackendFitsFrontend<Frontend.AssetHealthSnapshot, Normalize<Schema<'AssetHealthSnapshot'>>>;
export type Parity_DiagnosticsSummary = BackendFitsFrontend<Frontend.DiagnosticsSummary, Normalize<Schema<'DiagnosticsSummary'>>>;
export type Parity_RunTraceComparison = BackendFitsFrontend<Frontend.RunTraceComparison, Normalize<Schema<'RunTraceComparison'>>>;
export type Parity_PluginLoadInfo = BackendFitsFrontend<Frontend.PluginLoadInfo, Normalize<Schema<'PluginLoadInfo'>>>;
export type Parity_PluginSdkInfo = BackendFitsFrontend<Frontend.PluginSdkInfo, Normalize<Schema<'PluginSdkInfo'>>>;
export type Parity_PluginToolLoadInfo = BackendFitsFrontend<Frontend.PluginToolLoadInfo, Normalize<Schema<'PluginToolLoadInfo'>>>;
