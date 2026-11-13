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
  PluginLoadInfo,
  PluginBenchmarkRun,
  CameraTransportTelemetry,
  RobotDescriptor,
  JobVersionInfo,
  JobDescriptor,
  JobVersionSnapshot,
  ParameterValueSet,
  ResolvedRecipeParameterization,
  RuntimeDependencyValidation,
  RunTraceRecord,
  NodeTimelineObservation,
  NodePerformanceBaseline,
  RunObservabilitySnapshot,
  TracePage,
  OfflineReplayContext,
  ValidationDatasetItem,
  ValidationDatasetDetail,
  ValidationResultRecord,
  ValidationRunRecord,
  ParameterTuningPreviewResult,
  VisionTransform2D,
  CalibrationWorkspaceRequest,
  ProductionRuntimeStatus,
  ProductionSynchronizationGuardSnapshot,
  DiagnosticsSummary,
  MediaItemDescriptor,
  HardwareProvenanceSnapshot,
  CameraSynchronizationMemberStatus,
  CameraSynchronizationGroupStatus,
  CameraSynchronizationRunRecord,
  WorkflowModulePort,
  WorkflowModuleVersion,
  WorkflowModuleDescriptor,
  ExtractWorkflowModuleResult,
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
type B_PluginLoadInfo = components['schemas']['PluginLoadInfo'];
export type K_PluginLoadInfo = Assert<KeysOf<PluginLoadInfo> extends KeysOf<B_PluginLoadInfo> ? true : false>;
type B_PluginBenchmarkRun = components['schemas']['PluginBenchmarkRun'];
export type K_PluginBenchmarkRun = Assert<KeysOf<PluginBenchmarkRun> extends KeysOf<B_PluginBenchmarkRun> ? true : false>;
type B_CameraTransportTelemetry = components['schemas']['CameraTransportTelemetry'];
export type K_CameraTransportTelemetry = Assert<KeysOf<CameraTransportTelemetry> extends KeysOf<B_CameraTransportTelemetry> ? true : false>;
type B_RobotDescriptor = components['schemas']['RobotDescriptor'];
export type K_RobotDescriptor = Assert<KeysOf<RobotDescriptor> extends KeysOf<B_RobotDescriptor> ? true : false>;
type B_JobVersionInfo = components['schemas']['JobVersionInfo'];
export type K_JobVersionInfo = Assert<KeysOf<JobVersionInfo> extends KeysOf<B_JobVersionInfo> ? true : false>;
type B_JobDescriptor = components['schemas']['JobDescriptor'];
export type K_JobDescriptor = Assert<KeysOf<JobDescriptor> extends KeysOf<B_JobDescriptor> ? true : false>;
type B_JobVersionSnapshot = components['schemas']['JobVersionSnapshot'];
export type K_JobVersionSnapshot = Assert<KeysOf<JobVersionSnapshot> extends KeysOf<B_JobVersionSnapshot> ? true : false>;
type B_ParameterValueSet = components['schemas']['ParameterValueSet'];
export type K_ParameterValueSet = Assert<KeysOf<ParameterValueSet> extends KeysOf<B_ParameterValueSet> ? true : false>;
type B_ResolvedRecipeParameterization = components['schemas']['ResolvedRecipeParameterization'];
export type K_ResolvedRecipeParameterization = Assert<KeysOf<ResolvedRecipeParameterization> extends KeysOf<B_ResolvedRecipeParameterization> ? true : false>;
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
type B_TracePage = components['schemas']['TracePage'];
export type K_TracePage = Assert<KeysOf<TracePage> extends KeysOf<B_TracePage> ? true : false>;
type B_OfflineReplayContext = components['schemas']['OfflineReplayContext'];
export type K_OfflineReplayContext = Assert<KeysOf<OfflineReplayContext> extends KeysOf<B_OfflineReplayContext> ? true : false>;
type B_ValidationDatasetItem = components['schemas']['ValidationDatasetItem'];
export type K_ValidationDatasetItem = Assert<KeysOf<ValidationDatasetItem> extends KeysOf<B_ValidationDatasetItem> ? true : false>;
type B_ValidationDatasetDetail = components['schemas']['ValidationDatasetDetail'];
export type K_ValidationDatasetDetail = Assert<KeysOf<ValidationDatasetDetail> extends KeysOf<B_ValidationDatasetDetail> ? true : false>;
type B_ValidationResultRecord = components['schemas']['ValidationResultRecord'];
export type K_ValidationResultRecord = Assert<KeysOf<ValidationResultRecord> extends KeysOf<B_ValidationResultRecord> ? true : false>;
type B_ValidationRunRecord = components['schemas']['ValidationRunRecord'];
export type K_ValidationRunRecord = Assert<KeysOf<ValidationRunRecord> extends KeysOf<B_ValidationRunRecord> ? true : false>;
type B_ParameterTuningPreviewResult = components['schemas']['ParameterTuningPreviewResult'];
export type K_ParameterTuningPreviewResult = Assert<KeysOf<ParameterTuningPreviewResult> extends KeysOf<B_ParameterTuningPreviewResult> ? true : false>;
type B_VisionTransform2D = components['schemas']['VisionTransform2D'];
export type K_VisionTransform2D = Assert<KeysOf<VisionTransform2D> extends KeysOf<B_VisionTransform2D> ? true : false>;
type B_CalibrationWorkspaceRequest = components['schemas']['CalibrationWorkspaceRequest'];
export type K_CalibrationWorkspaceRequest = Assert<KeysOf<CalibrationWorkspaceRequest> extends KeysOf<B_CalibrationWorkspaceRequest> ? true : false>;
type B_ProductionRuntimeStatus = components['schemas']['ProductionRuntimeStatus'];
export type K_ProductionRuntimeStatus = Assert<KeysOf<ProductionRuntimeStatus> extends KeysOf<B_ProductionRuntimeStatus> ? true : false>;
type B_ProductionSynchronizationGuardSnapshot = components['schemas']['ProductionSynchronizationGuardSnapshot'];
export type K_ProductionSynchronizationGuardSnapshot = Assert<KeysOf<ProductionSynchronizationGuardSnapshot> extends KeysOf<B_ProductionSynchronizationGuardSnapshot> ? true : false>;
type B_DiagnosticsSummary = components['schemas']['DiagnosticsSummary'];
export type K_DiagnosticsSummary = Assert<KeysOf<DiagnosticsSummary> extends KeysOf<B_DiagnosticsSummary> ? true : false>;
type B_MediaItemDescriptor = components['schemas']['MediaItemDescriptor'];
export type K_MediaItemDescriptor = Assert<KeysOf<MediaItemDescriptor> extends KeysOf<B_MediaItemDescriptor> ? true : false>;
type B_HardwareProvenanceSnapshot = components['schemas']['HardwareProvenanceSnapshot'];
export type K_HardwareProvenanceSnapshot = Assert<KeysOf<HardwareProvenanceSnapshot> extends KeysOf<B_HardwareProvenanceSnapshot> ? true : false>;
type B_CameraSynchronizationMemberStatus = components['schemas']['CameraSynchronizationMemberStatus'];
export type K_CameraSynchronizationMemberStatus = Assert<KeysOf<CameraSynchronizationMemberStatus> extends KeysOf<B_CameraSynchronizationMemberStatus> ? true : false>;
type B_CameraSynchronizationGroupStatus = components['schemas']['CameraSynchronizationGroupStatus'];
export type K_CameraSynchronizationGroupStatus = Assert<KeysOf<CameraSynchronizationGroupStatus> extends KeysOf<B_CameraSynchronizationGroupStatus> ? true : false>;
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

// 以下类型刻意不绑定（键集不是后端子集），原因见 scripts/generate-type-bindings.mjs 的 EXEMPT：
//  - PortDescriptor: UI 注入 label（localizeCatalog 渲染时填显示名，后端不返回）
//  - OfflineReplayResult: 交叉类型 RunResult & {...}，继承的视图字段（errorCode/engine 等）不在该 schema 内
//  - WorkflowModuleParameter: 交叉类型 ParameterDescriptor & {...}，继承的视图字段（advanced/variants 等）不在该 schema 内
