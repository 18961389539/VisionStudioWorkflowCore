import type { components } from './generated/api-types';

/** 后端 OpenAPI schema 别名：枚举与 DTO 以生成类型为准，手写侧只保留 UI 需要的收窄。 */
type ContractSchema<K extends keyof components['schemas']> = components['schemas'][K];

/**
 * 响应 DTO 别名：在生成 schema 上应用两条前端消费约定。
 *  - number|string → number：.NET 10 OpenAPI 为数值字段生成字符串兼容分支，实际 JSON 是数字；
 *  - required-but-nullable → optional：后端必发 null，前端沿用历史上"可省略"的消费方式。
 * 仅用于与后端 schema 完全一致、没有前端收窄/额外 UI 字段的 DTO；需要收窄时用 Omit 包装。
 */
type NormalizeContractNumber<T> = [NonNullable<T>] extends [string] ? T
  : [NonNullable<T>] extends [number] ? T
  : [NonNullable<T>] extends [string | number] ? Exclude<T, string>
  : T extends readonly (infer U)[] ? NormalizeContractNumber<U>[]
  : T extends object ? { [K in keyof T]: NormalizeContractNumber<T[K]> }
  : T;

type OptionalContractKeys<T> = {
  [K in keyof T]-?: [null] extends [T[K]] ? K : [undefined] extends [T[K]] ? K : never;
}[keyof T];

type RelaxContractNullables<T> = T extends readonly (infer U)[] ? RelaxContractNullables<U>[]
  : T extends object
    ? { [K in Exclude<keyof T, OptionalContractKeys<T>>]: RelaxContractNullables<T[K]> }
      & { [K in OptionalContractKeys<T>]?: RelaxContractNullables<T[K]> }
    : T;

export type ContractDto<K extends keyof components['schemas']> = RelaxContractNullables<NormalizeContractNumber<components['schemas'][K]>>;

export type VisionRegionRun = { row: number; startColumn: number; endColumn: number };
export type VisionRegion = { width: number; height: number; components: { runs: VisionRegionRun[] }[] };

/** UI 视图：required 允许缺省（目录夹具不必逐项声明），label 由 localizeCatalog 注入。 */
export type PortDescriptor = {
  name: string;
  dataType: string;
  required?: boolean;
  requiredWhen?: { parameter: string; values: string[] } | null;
  /** 显示名（zh 模式由 localizeCatalog 注入端口名映射；en 直通英文原名） */
  label?: string;
};

export type ParameterOption = ContractSchema<'ParameterOption'>;

/** UI 视图：type 收窄为表单封闭联合；边界/默认值等按面板需要允许 null 或省略。 */
export type ParameterDescriptor = {
  name: string;
  label: string;
  type: 'number' | 'text' | 'textarea' | 'boolean' | 'select';
  defaultValue?: unknown;
  /** 留空或 null 时使用执行器根据当前输入确定的默认值。 */
  optional?: boolean;
  integer?: boolean;
  min?: number | null;
  max?: number | null;
  step?: number | null;
  options?: ParameterOption[] | null;
  unit?: string | null;
  group?: string | null;
  description?: string | null;
  visibleWhen?: { parameter: string; values: string[] } | null;
  visibleWhenAll?: Array<{ parameter: string; values: string[] }> | null;
  /**
   * 宿主注入/画布资产参数（ROI 对象、基准图像、离线回放路径等）：
   * 目录必须声明以便执行器读取，但不渲染到配置面板、不写入新节点默认值。
   */
  internal?: boolean;
  /**
   * 首屏折叠参数：配置页默认只展示核心参数，把这些参数折叠到“显示全部参数”。
   * 只影响展示，不影响执行器读取或校验。
   */
  advanced?: boolean;
  variantParameter?: string | null;
  /** 后端以 nullable 字典声明：null 与缺省等价，表示该参数没有变体。 */
  variants?: Record<string, { label?: string | null; description?: string | null; suggestions?: ParameterOption[] | null; min?: number | null; max?: number | null; step?: number | null; defaultValue?: unknown; unit?: string | null }> | null;
  /** 连接这些输入端口时忽略本参数（端口优先）。 */
  overriddenByPorts?: string[] | null;
};

/** UI 视图：能力位均可缺省（未声明按 false 处理），executionMode 沿用后端枚举。 */
export type VisionToolCapabilities = {
  supportsRoi?: boolean;
  emitsOverlay?: boolean;
  deterministic?: boolean;
  supportsRunNode?: boolean;
  supportsParallel?: boolean;
  executionMode?: ContractSchema<'VisionExecutionMode'>;
};

export type NodeImageFramePolicy = ContractSchema<'NodeImageFramePolicy'>;
export type NodeTransformFramePolicy = ContractSchema<'NodeTransformFramePolicy'>;

/** 目录声明的图像坐标帧语义（与后端 NodeFrameRule 一一对应）。 */
export type NodeFrameRule = {
  policy: NodeImageFramePolicy;
  inputPort?: string;
  frameSuffix?: string | null;
  frameLabel?: string | null;
  fallbackFrame?: string | null;
  transform?: NodeTransformFramePolicy;
  referencePort?: string | null;
  transformPort?: string | null;
  whenParameter?: string | null;
  whenValues?: string[] | null;
};

export type NodeOutputFrameSource = ContractSchema<'NodeOutputFrameSource'>;

/** 非图像输出（ROI/几何/检测框）的帧来源声明。 */
export type NodeOutputFrameRule = ContractSchema<'NodeOutputFrameRule'>;

/** 只输出 transform 的节点的帧声明（模板→图像、组合、求逆）。 */
export type NodeTransformFrameDeclaration = {
  policy: NodeTransformFramePolicy;
  primaryPort?: string;
  secondaryPort?: string | null;
  imagePort?: string;
  templatePort?: string;
};

/** 节点成熟度（后端 enum 字符串）。目录里 Stable 不写该字段，缺省一律按 Stable 处理。 */
export type NodeMaturity = ContractSchema<'NodeMaturity'>;

type BackendNodeCatalogItem = ContractSchema<'NodeCatalogItem'>;

/**
 * 节点目录：标识/分组/描述等字段来自后端 schema；参数、端口与能力位保留前端更窄的类型
 * （表单渲染与校验依赖封闭联合），其余声明字段（frames / actions / resources / declares*）直接继承。
 */
export type NodeCatalogItem =
  Omit<BackendNodeCatalogItem, 'inputs' | 'outputs' | 'parameters' | 'capabilities'
    | 'frames' | 'transformFrames' | 'outputFrames' | 'roiImageBindings' | 'maturity'
    | 'declaresActions' | 'declaresFrames' | 'declaresTransformFrames' | 'declaresOutputFrames' | 'declaresRoiImageBindings'> & {
    inputs: PortDescriptor[];
    outputs: PortDescriptor[];
    parameters: ParameterDescriptor[];
    capabilities?: VisionToolCapabilities | null;
    /** 后端计算属性；前端目录夹具不必逐项填写，消费方缺省按 false/未声明处理。 */
    declaresActions?: boolean;
    declaresFrames?: boolean;
    declaresTransformFrames?: boolean;
    declaresOutputFrames?: boolean;
    declaresRoiImageBindings?: boolean;
    /** 坐标帧声明；未声明时输出沿用输入帧。插件可据此参与帧跟踪。 */
    frames?: NodeFrameRule[] | null;
    /** 纯 transform 节点的帧声明（match.shape / compose / inverse）。 */
    transformFrames?: NodeTransformFrameDeclaration | null;
    /** ROI / 几何 / 检测等非图像输出的帧来源声明。 */
    outputFrames?: NodeOutputFrameRule[] | null;
    /** ROI 输入绑定的图像输入端口（ROI 帧校验用）。 */
    roiImageBindings?: { roiPort: string; imagePort: string }[] | null;
    /** 节点成熟度：Stable 不写入 JSON，缺省即 Stable。见 src/nodeMaturity.ts。 */
    maturity?: NodeMaturity | null;
  };

export type VisionValueSnapshot = ContractSchema<'VisionValueSnapshot'>;

type BackendNodeRunReport = ContractSchema<'NodeRunReport'>;

/** 节点运行报告：结构来自后端 schema；耗时/序号收窄为 number 并允许省略（旧记录没有时间线字段）。 */
export type NodeRunReport =
  Omit<BackendNodeRunReport, 'durationMs' | 'executionSequence' | 'startOffsetMs' | 'endOffsetMs' | 'phase'> & {
    durationMs: number;
    executionSequence?: number;
    startOffsetMs?: number;
    endOffsetMs?: number;
    /** 后端默认 Run；历史离线记录与前端夹具可以省略。 */
    phase?: ContractSchema<'NodeExecutionPhase'>;
  };

export type OverlayPoint = ContractDto<'VisionOverlayPoint'>;

/** UI 视图：type 收窄为封闭图元联合；按图元类型只有部分坐标字段有值，故全部可空。 */
export type VisionOverlay = {
  id: string;
  type: 'Point' | 'Line' | 'Circle' | 'Rectangle' | 'Polygon' | 'Contour' | 'Text' | 'Polyline';
  nodeId?: string | null;
  label?: string | null;
  x?: number | null;
  y?: number | null;
  x2?: number | null;
  y2?: number | null;
  radius?: number | null;
  width?: number | null;
  height?: number | null;
  angleDeg?: number | null;
  points?: OverlayPoint[] | null;
  stroke?: string;
  strokeWidth?: number;
  fill?: string | null;
};

export type VisionRoi =
  | { type: 'Rectangle'; x: number; y: number; width: number; height: number }
  | { type: 'Circle'; x: number; y: number; radius: number }
  | { type: 'Polygon'; points: OverlayPoint[] }
  | { type: 'Brush'; strokes: VisionRoiBrushStroke[] };

export type VisionRoiBrushStroke = {
  mode: 'Add' | 'Erase';
  width: number;
  points: OverlayPoint[];
};

export type ControlFlowDecision = ContractSchema<'ControlFlowDecision'>;

export type RunResult = {
  runId: string;
  success: boolean;
  totalDurationMs: number;
  previewAvailable: boolean;
  previewWidth?: number;
  previewHeight?: number;
  overlays?: VisionOverlay[];
  nodeReports: NodeRunReport[];
  error?: string | null;
  errorCode?: string | null;
  engine?: string;
  debugState?: string;
  haltNodeId?: string | null;
  haltReason?: string | null;
  qualityDisposition?: string | null;
  controlFlowDecisions?: ControlFlowDecision[] | null;
};

export type PluginToolLoadInfo = ContractDto<'PluginToolLoadInfo'>;

/** UI 视图：isolationMode 收窄为封闭联合；后端必发 null 的字段按历史习惯允许省略。 */
export type PluginLoadInfo = {
  id: string;
  name: string;
  version: string;
  assemblyPath: string;
  nodeCount: number;
  loaded: boolean;
  error?: string | null;
  assemblySha256?: string | null;
  packageFormat?: string;
  packageManifestSha256?: string | null;
  sdkApiVersion?: number;
  minimumSdkApiVersion?: number;
  maximumSdkApiVersion?: number;
  vendor?: string | null;
  enabled?: boolean;
  restartRequired?: boolean;
  warnings?: string[] | null;
  tools?: PluginToolLoadInfo[] | null;
  isolationMode?: 'InProcess' | 'WorkerProcess';
  workerProcessId?: number | null;
  workerState?: string | null;
  workerPoolSize?: number | null;
  workerImageTransport?: string | null;
  workerSharedMemoryThresholdBytes?: number | null;
  workerProtocolVersion?: number | null;
};


export type PluginPackageVersionInfo = ContractDto<'PluginPackageVersionInfo'>;

export type PluginPackagePreflightResult = ContractDto<'PluginPackagePreflightResult'>;

export type TrustedPluginPublisher = ContractDto<'TrustedPluginPublisher'>;

export type PluginSdkInfo = ContractDto<'PluginSdkInfo'>;


export type PluginWorkerInstanceStatus = ContractDto<'PluginWorkerInstanceStatus'>;

export type PluginWorkerStatus = ContractDto<'PluginWorkerStatus'>;

export type PluginWorkerLatencyDistribution = ContractDto<'PluginWorkerLatencyDistribution'>;

export type PluginWorkerToolPerformanceProfile = ContractDto<'PluginWorkerToolPerformanceProfile'>;

export type PluginWorkerPerformanceProfile = ContractDto<'PluginWorkerPerformanceProfile'>;


export type PluginBenchmarkRegressionPolicy = ContractDto<'PluginBenchmarkRegressionPolicy'>;

export type PluginBenchmarkPoolResult = ContractDto<'PluginBenchmarkPoolResult'>;

export type PluginBenchmarkRecommendation = ContractDto<'PluginBenchmarkRecommendation'>;

export type PluginBenchmarkRegressionGate = ContractDto<'PluginBenchmarkRegressionGate'>;

export type PluginBenchmarkRun =
  Omit<ContractDto<'PluginBenchmarkRun'>, 'recommendation' | 'regressionGate'> & {
    recommendation?: PluginBenchmarkRecommendation | null;
    regressionGate?: PluginBenchmarkRegressionGate | null;
  };

export type CameraTriggerMode = ContractSchema<'CameraTriggerMode'>;
export type CameraOutputPixelFormat = ContractSchema<'CameraOutputPixelFormat'>;

export type CameraSettings = ContractDto<'CameraSettings'>;

export type CameraCapabilities = ContractDto<'CameraCapabilities'>;

export type CameraTransportTelemetry =
  Omit<ContractDto<'CameraTransportTelemetry'>, 'hasNativeCounters'> & { hasNativeCounters?: boolean };

export type CameraAcquisitionStats = ContractDto<'CameraAcquisitionStats'>;

export type CameraDescriptor = ContractDto<'CameraDescriptor'>;

/** 相机适配器探测结果：无同名后端 schema（响应为内联对象），字段与该端点保持一致。 */
export type CameraAdapterDescriptor = {
  driver: string;
  vendor: string;
  isSdkAvailable: boolean;
  sdkError?: string | null;
};

export type CameraDiscoveredDevice = ContractDto<'CameraDiscoveredDevice'>;

export type RobotRuntimeSettings = ContractDto<'RobotRuntimeSettings'>;

export type RobotCapabilities = ContractDto<'RobotCapabilities'>;

export type RobotHandshakeSignals = ContractDto<'RobotHandshakeSignals'>;

export type CoordinatePose2D = ContractDto<'VisionCoordinatePose2D'>;

export type RobotTarget2D = ContractDto<'VisionRobotTarget2D'>;

export type RobotDescriptor =
  Omit<ContractDto<'RobotDescriptor'>, 'activeTarget' | 'simulated'> & {
    activeTarget?: RobotTarget2D | null;
    /**
     * 后端标记的虚拟/仿真适配器（2026-11-12）。现场最危险的误判是"以为在驱动真机，
     * 其实连的是内置模拟器"：命令全部成功、状态一路正常，产线却没有任何动作。
     */
    simulated?: boolean;
  };

export type RobotCommandTraceEvent = ContractDto<'RobotCommandTraceEvent'>;

export type RobotCommandTraceRecord = ContractDto<'RobotCommandTraceRecord'>;

/**
 * 工作流文档：字段名/基本类型来自后端 WorkflowDefinition / NodeDefinition / EdgeDefinition，
 * 只覆盖 UI 需要更宽的几处（数值坐标生成类型是 number|string；parameters 允许省略）。
 */
type BackendNodeDefinition = ContractSchema<'NodeDefinition'>;
type BackendEdgeDefinition = ContractSchema<'EdgeDefinition'>;

export type WorkflowPayload =
  Omit<ContractSchema<'WorkflowDefinition'>, 'nodes' | 'edges' | 'schemaVersion'> & {
    schemaVersion?: number;
    nodes: Array<Omit<BackendNodeDefinition, 'name' | 'position' | 'parameters'> & {
      name?: string | null;
      position?: { x: number; y: number } | null;
      parameters?: Record<string, unknown> | null;
    }>;
    edges: Array<Omit<BackendEdgeDefinition, 'kind' | 'isControl'> & {
      kind?: string | null;
    }>;
  };

export type JobVersionValidationInfo = ContractDto<'JobVersionValidationInfo'>;

/** UI 视图：bindingCount 允许省略（旧版本记录没有该字段），validation 可空。 */
export type JobVersionInfo = {
  version: number;
  workflowHash: string;
  createdAt: string;
  note: string;
  published: boolean;
  validation?: JobVersionValidationInfo | null;
  bindingCount?: number;
};

export type PublicationEvent = ContractDto<'PublicationEvent'>;

/** UI 视图：versions/publicationHistory 复用上面的视图类型；后端新增 productId 暂未消费。 */
export type JobDescriptor = {
  id: string;
  name: string;
  description: string;
  createdAt: string;
  updatedAt: string;
  latestVersion: number;
  publishedVersion?: number | null;
  publishedDependencyManifestHash?: string | null;
  versions: JobVersionInfo[];
  publicationHistory: PublicationEvent[];
  recipeCode?: string | null;
};


export type ValidationCandidateInfo = ContractDto<'ValidationCandidateInfo'>;
export type JobVersionDiff = ContractDto<'JobVersionDiff'>;
/** UI 视图：workflow 复用宽松的 WorkflowPayload（画布坐标/参数按前端消费方式放宽）。 */
export type JobVersionSnapshot = {
  jobId: string;
  version: number;
  workflowHash: string;
  createdAt: string;
  note: string;
  workflow: WorkflowPayload;
  baseWorkflowHash?: string | null;
  baseWorkflow?: WorkflowPayload | null;
  parameterBindings?: Record<string,string> | null;
  parameterSnapshot?: Record<string,unknown> | null;
};

/** UI 视图：scope 收窄为 'recipe'，updatedAt 允许省略。 */
export type ParameterValueSet = { scope: 'recipe'; ownerId: string; values: Record<string,unknown>; updatedAt?: string|null; };
/** UI 视图：两个 workflow 复用宽松的 WorkflowPayload，绑定/快照保留原始字典。 */
export type ResolvedRecipeParameterization = {
  baseWorkflow: WorkflowPayload; effectiveWorkflow: WorkflowPayload; baseWorkflowHash: string; effectiveWorkflowHash: string;
  bindings: Record<string,string>; snapshot: Record<string,unknown>; bindingCount: number;
};

export type RuntimeDependencyDrift = ContractDto<'RuntimeDependencyDrift'>;

/** UI 视图：drifts 复用 ContractDto；后端 currentManifest 暂未消费。 */
export type RuntimeDependencyValidation = {
  compatible: boolean;
  expectedManifestHash: string;
  currentManifestHash: string;
  drifts: RuntimeDependencyDrift[];
};

/** UI 视图：disposition 收窄为封闭联合驱动筛选/徽标，其余字段沿用后端。 */
export type RunTraceRecord = {
  runId: string;
  startedAt: string;
  source: string;
  jobId?: string | null;
  jobVersion?: number | null;
  workflowId: string;
  workflowName: string;
  workflowHash?: string | null;
  dependencyManifestHash?: string | null;
  executionStatus: string;
  disposition: 'OK' | 'NG' | 'REVIEW' | 'ERROR' | 'PENDING';
  totalDurationMs: number;
  nodeCount: number;
  overlayCount: number;
  hasPreview: boolean;
  hasReplayInput: boolean;
  replaySourceNodeId?: string | null;
  error?: string | null;
  errorCode?: string | null;
  note?: string | null;
  nodeReports: NodeRunReport[];
};

/** UI 视图：phase 收窄为 Warmup/Run 封闭联合。 */
export type NodeTimelineObservation = {
  runId: string;
  executionSequence: number;
  nodeId: string;
  nodeType: string;
  phase: 'Warmup' | 'Run';
  success: boolean;
  startOffsetMs: number;
  endOffsetMs: number;
  durationMs: number;
  error?: string | null;
};

/** UI 视图：status 收窄为封闭联合驱动颜色分级。 */
export type NodePerformanceBaseline = {
  nodeId: string;
  nodeType: string;
  sampleCount: number;
  currentDurationMs: number;
  p50Ms: number;
  p95Ms: number;
  maxMs: number;
  ratioToMedian?: number | null;
  status: 'Insufficient' | 'Normal' | 'Slow' | 'VerySlow';
};

/** UI 视图：timeline/baselines 使用收窄了 phase/status 的节点视图。 */
export type RunObservabilitySnapshot = {
  runId: string;
  timelineAvailable: boolean;
  timelineSpanMs: number;
  runtimeOverheadMs: number;
  peakConcurrency: number;
  slowestNodeId?: string | null;
  slowNodeCount: number;
  baselineRunCount: number;
  timeline: NodeTimelineObservation[];
  baselines: NodePerformanceBaseline[];
};

export type TraceNodeDelta = ContractDto<'TraceNodeDelta'>;

export type RunTraceComparison = ContractDto<'RunTraceComparison'>;

export type FailureSignatureOccurrence = ContractDto<'FailureSignatureOccurrence'>;

export type FailureSignatureAnalysis = ContractDto<'FailureSignatureAnalysis'>;

export type TraceStats = ContractDto<'TraceStats'>;

/** UI 视图：items 复用收窄后的 RunTraceRecord。 */
export type TracePage = {
  offset: number;
  limit: number;
  total: number;
  items: RunTraceRecord[];
};

export type TraceRetentionOptions = ContractDto<'TraceRetentionOptions'>;

export type StorageCapacityStatus = ContractDto<'StorageCapacityStatus'>;

export type SchemaMigrationHistoryEntry = ContractDto<'SchemaMigrationHistoryEntry'>;

export type SchemaMigrationStatus = ContractDto<'SchemaMigrationStatus'>;

/** UI 视图：数值收窄为 number；后端新增的 systemAssets 相关字段暂未消费。 */
export type StorageBackupDescriptor = {
  backupId: string;
  createdAt: string;
  schemaVersion: number;
  archiveBytes: number;
  includesArtifacts: boolean;
  artifactCount: number;
  artifactBytes: number;
};

export type StorageRestoreResult = {
  backupId: string;
  sourceSchemaVersion: number;
  currentSchemaVersion: number;
  artifactsRestored: boolean;
  restartRecommended: boolean;
};

/** UI 视图：数值收窄为 number；后端新增的 snapshotSkipped* 计数暂未展示。 */
export type TraceStorageStatus = {
  databaseFile: string;
  databaseBytes: number;
  traceCount: number;
  previewCount: number;
  replayInputCount: number;
  artifactBytes: number;
  retention: TraceRetentionOptions;
  capacity?: StorageCapacityStatus | null;
  schema?: SchemaMigrationStatus | null;
};

/** UI 视图：trace/workflow 复用前端视图，可空字段允许省略。 */
export type OfflineReplayContext = {
  sourceRunId: string;
  trace: RunTraceRecord;
  workflow: WorkflowPayload;
  replayable: boolean;
  blockReason?: string | null;
  hasReplayInput: boolean;
  replaySourceNodeId?: string | null;
  orderedNodeIds: string[];
  executionModel: string;
};

export type NodeReplayComparison = ContractDto<'NodeReplayComparison'>;

/** 回放结果：交叉 RunResult 以复用预览/覆盖层等 UI 字段（该 schema 本身不含这些字段）。 */
export type OfflineReplayResult = RunResult & {
  sourceRunId: string;
  comparison: NodeReplayComparison[];
  baselineDisposition?: string | null;
  dispositionChanged: boolean;
  executionModel: string;
};

export type ValidationDatasetSummary = ContractDto<'ValidationDatasetSummary'>;
/** UI 视图：sourceKind/expectedDisposition 收窄为 UI 联合，其余按后端原样。 */
export type ValidationDatasetItem = {
  itemId: string; datasetId: string; sourceKind: 'TRACE' | 'MEDIA'; sourceRef: string; sourceRunId?: string | null; expectedDisposition: 'OK' | 'NG'; note?: string | null; sortOrder: number; addedAt: string; sourceDisposition: string; displayName: string; replayReady: boolean;
};
export type ValidationDatasetDetail = { dataset: ValidationDatasetSummary; items: ValidationDatasetItem[] };
export type ValidationRunSummary = ContractDto<'ValidationRunSummary'>;
/** UI 视图：sourceKind 收窄，classification 保留宽 string（后端为开放分类）。 */
export type ValidationResultRecord = {
  runId: string; itemId: string; sourceKind: 'TRACE' | 'MEDIA'; sourceRef: string; expectedDisposition: string; actualDisposition: string; classification: 'TRUE_OK' | 'TRUE_NG' | 'FALSE_OK' | 'FALSE_NG' | 'ERROR' | string; replayRunId?: string | null; success: boolean; durationMs: number; failedNodeIds: string[]; error?: string | null; completedAt: string;
};
/** UI 视图：status 收窄，results 复用上面的结果视图。 */
export type ValidationRunRecord = {
  runId: string; datasetId: string; status: 'Running' | 'Completed' | 'Cancelled' | 'Failed' | string; sourceWorkflowRunId: string; workflowHash: string; requestedCount: number; completedCount: number; startedAt: string; completedAt?: string | null; cancelRequested: boolean; error?: string | null; summary?: ValidationRunSummary | null; results?: ValidationResultRecord[] | null;
};

/** UI 视图：sourceKind/expectedDisposition 收窄，replay 复用 OfflineReplayResult。 */
export type ParameterTuningPreviewResult = {
  datasetId: string;
  itemId: string;
  sourceKind: 'TRACE' | 'MEDIA';
  sourceRef: string;
  expectedDisposition: 'OK' | 'NG';
  actualDisposition?: string | null;
  classification?: string | null;
  fullWorkflow: boolean;
  selectedNodeReport?: NodeRunReport | null;
  replay: OfflineReplayResult;
};

export type CalibrationWorkspacePoint = ContractDto<'CalibrationWorkspacePoint'>;

export type CalibrationResidual = ContractDto<'CalibrationResidual'>;

export type CalibrationHeatCell = ContractDto<'CalibrationHeatCell'>;

/** UI 视图：数值收窄为 number；后端 sourceUnit 暂未消费，matrix 可空。 */
export type VisionTransform2D = {
  sourceFrame: string;
  targetFrame: string;
  targetUnit: string;
  h00: number; h01: number; h02: number;
  h10: number; h11: number; h12: number;
  h20: number; h21: number; h22: number;
  matrix?: number[] | null;
};

export type VisionTransform3D = {
  sourceFrame: string;
  targetFrame: string;
  unit: string;
  /** Row-major 4x4 homogeneous matrix. */
  matrix: number[];
};

export type VisionCoordinatePoint3D = {
  x: number;
  y: number;
  z: number;
  frame: string;
  unit: string;
};

/** 请求视图：maxRmse/maxPointError 允许省略（后端有默认值），数值按表单输入收敛为 number。 */
export type CalibrationWorkspaceRequest = {
  sourceFrame: string;
  targetFrame: string;
  targetUnit: string;
  points: CalibrationWorkspacePoint[];
  verificationPoints?: CalibrationWorkspacePoint[] | null;
  heatmapColumns: number;
  heatmapRows: number;
  maxRmse?: number;
  maxPointError?: number;
};

export type CalibrationWorkspaceResult = ContractDto<'CalibrationWorkspaceResult'>;

export type CalibrationVersionInfo = ContractDto<'CalibrationVersionInfo'>;

export type CalibrationAssetDescriptor = ContractDto<'CalibrationAssetDescriptor'>;

export type CalibrationVersionSnapshot = ContractDto<'CalibrationVersionSnapshot'>;

export type CameraIntrinsicVersionInfo = ContractDto<'CameraIntrinsicVersionInfo'>;

export type CameraIntrinsicAssetDescriptor = ContractDto<'CameraIntrinsicAssetDescriptor'>;

export type CameraIntrinsicVersionSnapshot = ContractDto<'CameraIntrinsicVersionSnapshot'>;


export type CalibrationWizardQuality = {
  cornerCount: number;
  expectedCorners: number;
  coverageX: number;
  coverageY: number;
  blurVariance: number;
  verdict: 'Fail' | 'Warn' | 'Pass';
  reason: string;
  guidance: string;
};

export type CalibrationWizardView = {
  index: number;
  enabled: boolean;
  label: string;
  quality: CalibrationWizardQuality;
};

export type CalibrationWizardSession = {
  sessionId: string;
  board: {
    pattern: string;
    columns: number;
    rows: number;
    spacingMm: number;
    minBlurVariance: number;
    minCoverage: number;
    maxGridError: number;
    saddleThreshold: number;
  };
  imageWidth?: number | null;
  imageHeight?: number | null;
  views: CalibrationWizardView[];
  enabledCount: number;
  cameraModel: 'pinhole' | 'fisheye' | 'telecentric';
  telecentricMagnification: number;
  telecentricPixelSizeUm: number;
  model?: unknown;
  lastSolveSummary?: Record<string, unknown> | null;
};

export type CalibrationWizardHint = {
  code: string;
  message: string;
  region: string;
};

export type CalibrationHandEyeResidual = {
  index: number;
  translationError: number;
  angleErrorDeg: number;
  robot: { x: number; y: number; thetaDeg: number };
  vision: { x: number; y: number; thetaDeg: number };
};

export type HandEyeWizardSessionDocument = ContractDto<'HandEyeWizardSessionDocument'>;

export type HandEyeWizardSessionSummary = {
  sessionId: string;
  name: string;
  updatedAt: string;
  mode: string;
  sampleCount: number;
  hasSolve: boolean;
};

export type HandEyeCalibrationAssetDescriptor = ContractDto<'HandEyeCalibrationAssetDescriptor'>;

export type HandEyeCalibrationVersionSnapshot = ContractDto<'HandEyeCalibrationVersionSnapshot'>;

export type HandEye3DWizardSessionDocument = ContractDto<'HandEye3DWizardSessionDocument'>;

export type HandEye3DWizardSessionSummary = {
  sessionId: string;
  name: string;
  updatedAt: string;
  mode: string;
  method: string;
  sampleCount: number;
  hasSolve: boolean;
};

export type HandEye3DCalibrationAssetDescriptor = ContractDto<'HandEye3DCalibrationAssetDescriptor'>;

export type HandEye3DCalibrationVersionSnapshot = ContractDto<'HandEye3DCalibrationVersionSnapshot'>;

export type DeviceConnectionState = ContractSchema<'DeviceConnectionState'>;
export type DeviceTagQuality = ContractSchema<'DeviceTagQuality'>;
export type DeviceTagDataType = ContractSchema<'DeviceTagDataType'>;

export type DeviceTagDefinition = ContractSchema<'DeviceTagDefinition'>;

export type DeviceTagSample = ContractSchema<'DeviceTagSample'>;

export type DeviceRuntimeSettings = ContractDto<'DeviceRuntimeSettings'>;

export type DeviceRuntimeStats = ContractDto<'DeviceRuntimeStats'>;

export type DeviceDriverCapabilities = ContractSchema<'DeviceDriverCapabilities'>;

export type DeviceProtocolDiagnostics = ContractDto<'DeviceProtocolDiagnostics'>;

export type DeviceDescriptor = ContractDto<'DeviceDescriptor'>;

/** UI 视图：数值收窄为 number；后端 maxRecoveryAttempts 暂未在配置面板暴露。 */
export type ProductionRuntimeConfig = {
  jobId?: string | null;
  autoStart: boolean;
  cycleDelayMs: number;
  maxCycleMs: number;
  maxConsecutiveFailures: number;
  autoRecover: boolean;
  recoveryDelayMs: number;
  stopTimeoutMs: number;
  ptpDriftGuardEnabled: boolean;
  ptpGuardWindowRuns: number;
  ptpGuardMinimumEvidenceRuns: number;
  ptpGuardMinimumReadyRate: number;
  ptpGuardCheckEveryCycles: number;
  ptpGuardFaultOnMasterClockChange: boolean;
  synchronizationHealthGuardEnabled: boolean;
  synchronizationGuardWindowRuns: number;
  synchronizationGuardMinimumEvidenceRuns: number;
  synchronizationGuardMaximumFailureRate: number;
  synchronizationGuardMaximumFrameTimeoutRate: number;
  synchronizationGuardMaxConsecutiveFailures: number;
  synchronizationGuardMaxConsecutiveSkewViolations: number;
  synchronizationGuardMaximumNativeFrameLossRate: number;
  synchronizationGuardMaximumBufferUnderruns: number;
  synchronizationGuardMaximumResynchronizations: number;
  synchronizationGuardMaximumSequenceGapRate: number;
  synchronizationGuardCheckEveryCycles: number;
  synchronizationGuardUnhealthyChecksToFault: number;
  synchronizationGuardHealthyChecksToRecover: number;
  synchronizationGuardRecoveryTimeoutMs: number;
};

/** UI 视图：state 收窄为封闭联合；后端 trace 队列/副作用字段暂未消费。 */
export type ProductionRuntimeStatus = {
  state: 'Stopped' | 'Starting' | 'Running' | 'Recovering' | 'Stopping' | 'Faulted';
  jobId?: string | null;
  lockedJobVersion?: number | null;
  lockedWorkflowHash?: string | null;
  lockedDependencyManifestHash?: string | null;
  startedAt?: string | null;
  lastCycleAt?: string | null;
  currentRunId?: string | null;
  cycleCount: number;
  okCount: number;
  ngCount: number;
  errorCount: number;
  consecutiveFailures: number;
  watchdogTrips: number;
  lastDurationMs: number;
  lastDisposition?: string | null;
  lastError?: string | null;
  productionLocked: boolean;
  ptpGuard?: ProductionPtpGuardSnapshot | null;
  synchronizationGuard?: ProductionSynchronizationGuardSnapshot | null;
};

export type ProductionPtpGuardIssue = ContractSchema<'ProductionPtpGuardIssue'>;
export type ProductionPtpGuardGroupStatus = ContractDto<'ProductionPtpGuardGroupStatus'>;
export type ProductionPtpGuardSnapshot = ContractDto<'ProductionPtpGuardSnapshot'>;

export type ProductionSynchronizationTransportWindow = ContractDto<'ProductionSynchronizationTransportWindow'>;
export type ProductionSynchronizationGuardIssue = ContractSchema<'ProductionSynchronizationGuardIssue'>;
export type ProductionSynchronizationGuardGroupStatus = ContractDto<'ProductionSynchronizationGuardGroupStatus'>;
export type ProductionSynchronizationGuardSnapshot =
  Omit<ContractDto<'ProductionSynchronizationGuardSnapshot'>, 'liveRecoverableOnly'> & { liveRecoverableOnly?: boolean };

export type AlarmRecord = ContractSchema<'AlarmRecord'>;

export type AssetKind = ContractSchema<'AssetKind'>;
export type AssetHealthLevel = ContractSchema<'AssetHealthLevel'>;
export type AssetEventSeverity = ContractSchema<'AssetEventSeverity'>;

export type AssetHealthSnapshot = ContractDto<'AssetHealthSnapshot'>;

export type AssetEventEnvelope = ContractSchema<'AssetEventEnvelope'>;

export type DiagnosticsSummary =
  Omit<ContractDto<'DiagnosticsSummary'>, 'staleProviders'> & {
    /** 采样失败的 provider：其资产为保留的上次快照，数据可能过期 */
    staleProviders?: string[];
  };

/**
 * 跨轮计数器落盘状态（GET /api/diagnostics/run-counters）。
 *
 * `degraded` 为真表示计数仍在内存累加、但磁盘副本已落后——重启后
 * "本班次良率 / 连续 NG"会倒退，这是现场最容易被误判成"视觉算错"的一类问题。
 */
export type RunCounterPersistenceStatus = ContractDto<'RunCounterPersistenceStatus'>;

export type MediaLibraryStatus = ContractDto<'MediaLibraryStatus'>;

export type MediaCollectionDescriptor = ContractDto<'MediaCollectionDescriptor'>;

/** UI 视图：label 收窄为封闭联合驱动筛选，bytes 收窄为 number。 */
export type MediaItemDescriptor = {
  relativePath: string;
  source: string;
  collection: string;
  label: 'Unlabeled' | 'OK' | 'NG' | 'Review';
  name: string;
  extension: string;
  bytes: number;
  modifiedAt: string;
};


export type HardwareProvenanceData = ContractSchema<'HardwareProvenanceData'>;

export type HardwareProvenanceDeclaration = ContractSchema<'HardwareProvenanceDeclaration'>;

/** UI 视图：kind/completeness 收窄为封闭联合；探针字段允许省略。 */
export type HardwareProvenanceSnapshot = {
  kind: 'camera' | 'device' | 'robot';
  id: string;
  provider: string;
  data: HardwareProvenanceData;
  fingerprint: string;
  completeness: 'Complete' | 'Partial' | 'Unavailable';
  missingRecommendedFields: string[];
  capturedAt: string;
  hasManualDeclaration: boolean;
  liveProbeConfigured?: boolean;
  liveProbeSucceeded?: boolean;
  liveProbeRequired?: boolean;
  liveProbeProvider?: string | null;
  liveProbeError?: string | null;
};

export type CameraCommissioningCapabilities = ContractSchema<'CameraCommissioningCapabilities'>;

export type CameraCommissioningProfile = ContractDto<'CameraCommissioningProfile'>;

export type CameraCommissioningSnapshot = ContractDto<'CameraCommissioningSnapshot'>;

export type CameraFeatureDescriptor = ContractDto<'CameraFeatureDescriptor'>;

export type CameraFeatureProfileRecord = ContractDto<'CameraFeatureProfileRecord'>;

export type CameraPtpClockState = ContractSchema<'CameraPtpClockState'>;
export type CameraTimeSynchronizationStatus = ContractDto<'CameraTimeSynchronizationStatus'>;
export type CameraSynchronizationGroup = ContractDto<'CameraSynchronizationGroup'>;
/** UI 视图：相机/采集状态保留宽 string（跨驱动展示），latestFrame 可空。 */
export type CameraSynchronizationMemberStatus = {
  cameraId: string;
  driver: string;
  cameraState: string;
  acquisitionState: string;
  timeSync: CameraTimeSynchronizationStatus;
  latestFrame?: { cameraId: string; sequence: number; hostTimestamp: string; deviceTimestampNs?: number | null; triggerId?: number | null } | null;
};
/** UI 视图：group/members 复用前端视图，可空字段允许省略。 */
export type CameraSynchronizationGroupStatus = {
  group: CameraSynchronizationGroup;
  ready: boolean;
  ptpReady: boolean;
  error?: string | null;
  members: CameraSynchronizationMemberStatus[];
  latestSkewUs?: number | null;
  timestampBasis: string;
};
export type CameraSynchronizationCaptureResult = ContractDto<'CameraSynchronizationCaptureResult'>;
export type CameraSynchronizationCommissioningAssessment = ContractDto<'CameraSynchronizationCommissioningAssessment'>;

export type CameraSynchronizationStatistics = ContractDto<'CameraSynchronizationStatistics'>;

export type CameraSynchronizationPtpCameraDiagnostics = ContractDto<'CameraSynchronizationPtpCameraDiagnostics'>;
export type CameraSynchronizationPtpTrendPoint = ContractDto<'CameraSynchronizationPtpTrendPoint'>;
export type CameraSynchronizationPtpDiagnostics = ContractDto<'CameraSynchronizationPtpDiagnostics'>;

export type GigEHostAddress = ContractDto<'GigEHostAddress'>;
export type GigEHostNetworkInterface = ContractDto<'GigEHostNetworkInterface'>;
export type GigECameraNetworkDiagnostic = ContractDto<'GigECameraNetworkDiagnostic'>;
export type GigENicLoadDiagnostic = ContractDto<'GigENicLoadDiagnostic'>;
export type GigENetworkTrendPoint = ContractDto<'GigENetworkTrendPoint'>;
export type GigENetworkAssessment = ContractDto<'GigENetworkAssessment'>;
export type GigENetworkDiagnostics = ContractDto<'GigENetworkDiagnostics'>;
export type GigENetworkCommissioningReport = ContractDto<'GigENetworkCommissioningReport'>;

export type CameraSynchronizationCommissioningCameraEvidence = ContractDto<'CameraSynchronizationCommissioningCameraEvidence'>;
export type CameraSynchronizationCommissioningReport = ContractDto<'CameraSynchronizationCommissioningReport'>;
export type CameraSynchronizationCommissioningTest = ContractDto<'CameraSynchronizationCommissioningTest'>;

/** UI 视图：command/frames 复用 CaptureResult 的字段，transportSnapshots 复用收窄后的遥测视图。 */
export type CameraSynchronizationRunRecord = {
  runId: string;
  groupId: string;
  groupConfigurationHash: string;
  source: string;
  scheduled: boolean;
  requestedAt: string;
  completedAt: string;
  durationMs: number;
  scheduledDeviceTimeNs?: number | null;
  timestampBasis: string;
  triggerSkewUs?: number | null;
  maxAllowedSkewUs: number;
  withinTolerance?: boolean | null;
  outcome: string;
  error?: string | null;
  command?: CameraSynchronizationCaptureResult['command'] | null;
  frames: CameraSynchronizationCaptureResult['frames'];
  ptpSnapshots: CameraTimeSynchronizationStatus[];
  transportSnapshots?: CameraTransportTelemetry[] | null;
};


/** UI 视图：dataType 保留宽 string（模块端口允许任意数据类型）。 */
export type WorkflowModulePort = {
  name: string;
  dataType: string;
  required: boolean;
  internalNodeId: string;
  internalPort: string;
};

/** UI 视图：交叉 ParameterDescriptor 以复用节点参数渲染字段（type 为封闭联合）。 */
export type WorkflowModuleParameter = ParameterDescriptor & {
  defaultValue: unknown;
  internalNodeId: string;
  internalParameter: string;
};

/** UI 视图：workflow/parameters 复用 WorkflowPayload 与模块参数视图。 */
export type WorkflowModuleVersion = {
  moduleId: string;
  version: number;
  moduleHash: string;
  createdAt: string;
  note: string;
  workflow: WorkflowPayload;
  inputs: WorkflowModulePort[];
  outputs: WorkflowModulePort[];
  parameters: WorkflowModuleParameter[];
};

/** UI 视图：versions 复用上面的模块版本视图。 */
export type WorkflowModuleDescriptor = {
  id: string;
  name: string;
  description: string;
  createdAt: string;
  updatedAt: string;
  latestVersion: number;
  versions: WorkflowModuleVersion[];
};

/** UI 视图：module/version/replacementWorkflow 均复用前端视图。 */
export type ExtractWorkflowModuleResult = {
  module: WorkflowModuleDescriptor;
  version: WorkflowModuleVersion;
  replacementWorkflow: WorkflowPayload;
};

export type InvestigationCaseSummary = ContractDto<'InvestigationCaseSummary'>;

export type InvestigationCaseRun = ContractDto<'InvestigationCaseRun'>;

export type InvestigationVerificationEvidence = ContractDto<'InvestigationVerificationEvidence'>;

export type InvestigationCaseDetail = ContractDto<'InvestigationCaseDetail'>;

export type InvestigationTrendPoint = ContractDto<'InvestigationTrendPoint'>;

export type InvestigationSignatureTrend = ContractDto<'InvestigationSignatureTrend'>;

export type InvestigationTrendDashboard = ContractDto<'InvestigationTrendDashboard'>;

/**
 * 节点级 span 的单次记录（2026-11-12）。
 * 来源 `diagnostics/node-spans`，与 `EngineTelemetry` 的标签口径一一对应：
 * `nodeId`/`nodeType` 对齐画布节点，`runId`/`scope` 用于串链，`durationMs` 与
 * `vision.node.duration_ms` 同值。`sequence` 是进程内自增序号，环形覆盖后仍可判先后。
 */
export type NodeSpanRecord = ContractDto<'NodeSpanRecord'>;

/**
 * 最近执行快照。`overwritten > 0` 表示缓冲已回卷，`recent` 只是尾部而不是全量——
 * 展示时必须让这一点可见，否则会把"最近 64 条"误读成"全部都很慢"。
 */
export type NodeSpanSnapshot = ContractDto<'NodeSpanSnapshot'>;
