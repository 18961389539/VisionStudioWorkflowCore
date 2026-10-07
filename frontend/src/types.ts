export type PortDescriptor = {
  name: string;
  dataType: string;
  required?: boolean;
  /** 显示名（zh 模式由 localizeCatalog 注入端口名映射；en 直通英文原名） */
  label?: string;
};

export type ParameterOption = {
  label: string;
  value: string;
};

export type ParameterDescriptor = {
  name: string;
  label: string;
  type: 'number' | 'text' | 'textarea' | 'boolean' | 'select';
  defaultValue?: unknown;
  min?: number;
  max?: number;
  step?: number;
  options?: ParameterOption[];
  unit?: string | null;
  group?: string | null;
  description?: string | null;
};

export type VisionToolCapabilities = {
  supportsRoi?: boolean;
  emitsOverlay?: boolean;
  deterministic?: boolean;
  supportsRunNode?: boolean;
  supportsParallel?: boolean;
  executionMode?: 'Auto' | 'WorkflowCore' | 'Pipeline';
};

export type NodeCatalogItem = {
  type: string;
  displayName: string;
  category: string;
  inputs: PortDescriptor[];
  outputs: PortDescriptor[];
  parameters: ParameterDescriptor[];
  description?: string | null;
  pluginId?: string | null;
  capabilities?: VisionToolCapabilities | null;
};

export type VisionValueSnapshot = {
  type: string;
  display: string;
  value?: unknown;
};

export type NodeRunReport = {
  nodeId: string;
  nodeType: string;
  success: boolean;
  durationMs: number;
  summary: Record<string, unknown>;
  error?: string | null;
  phase?: 'Warmup' | 'Run';
  inputs?: Record<string, VisionValueSnapshot> | null;
  outputs?: Record<string, VisionValueSnapshot> | null;
  executionSequence?: number;
  startOffsetMs?: number;
  endOffsetMs?: number;
};

export type OverlayPoint = { x: number; y: number };

export type VisionOverlay = {
  id: string;
  type: 'Point' | 'Line' | 'Circle' | 'Rectangle' | 'Polygon' | 'Contour' | 'Text';
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
  | { type: 'Polygon'; points: OverlayPoint[] };

export type ControlFlowDecision = {
  nodeId: string;
  nodeType: string;
  activeBranches: string[];
  selectedBranch?: string | null;
  condition?: boolean | null;
};

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
  controlFlowDecisions?: ControlFlowDecision[];
};

export type PluginToolLoadInfo = {
  type: string;
  displayName: string;
  toolVersion?: string | null;
  lifetime: 'Singleton' | 'Transient' | 'PerNode';
  concurrency: 'ThreadSafe' | 'Serialized';
  experimental: boolean;
};

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


export type PluginPackageVersionInfo = {
  id: string; name: string; version: string; vendor: string;
  packageSha256: string; contentSha256: string; signed: boolean; signatureValid: boolean; publisherTrusted: boolean;
  publisherThumbprint?: string | null; publisherSubject?: string | null; installedAt: string;
  active: boolean; pending: boolean; restartRequired: boolean; packageFormat: string;
};

export type PluginPackagePreflightResult = {
  valid: boolean; error?: string | null; id?: string | null; name?: string | null; version?: string | null; vendor?: string | null; entryAssembly?: string | null;
  packageSha256: string; contentSha256: string; signed: boolean; signatureValid: boolean; publisherTrusted: boolean;
  publisherThumbprint?: string | null; publisherSubject?: string | null; warnings: string[];
};

export type TrustedPluginPublisher = {
  thumbprint: string; subject: string; displayName?: string | null; enabled: boolean; addedAt: string; notBefore?: string | null; notAfter?: string | null;
};

export type PluginSdkInfo = {
  apiVersion: number;
  packageManifestSchemaVersion: number;
  supportedLifetimes: string[];
  supportedConcurrencyModes: string[];
  supportedIsolationModes: string[];
  runtimeDiscovery: boolean;
  runtimeReplacement: boolean;
  isolationBoundary: string;
};


export type PluginWorkerInstanceStatus = {
  workerIndex: number; state: string; processId?: number | null; startedAt?: string | null; lastRequestAt?: string | null;
  restartCount: number; workingSetBytes: number; lastError?: string | null; circuitOpen: boolean; busy: boolean;
};

export type PluginWorkerStatus = {
  pluginId: string; state: string; processId?: number | null; startedAt?: string | null; lastRequestAt?: string | null;
  restartCount: number; workingSetBytes: number; assemblyPath: string; lastError?: string | null; circuitOpen: boolean;
  poolSize: number; runningWorkers: number; busyWorkers: number; requestCount: number; sharedMemoryTransfers: number;
  imageTransport: string; sharedMemoryThresholdBytes: number; workers: PluginWorkerInstanceStatus[];
};

export type PluginWorkerLatencyDistribution = {
  averageMs: number; p50Ms: number; p95Ms: number; p99Ms: number; maxMs: number;
};

export type PluginWorkerToolPerformanceProfile = {
  nodeType: string; sampleCount: number; failureCount: number;
  queueWait: PluginWorkerLatencyDistribution; pluginExecute: PluginWorkerLatencyDistribution; total: PluginWorkerLatencyDistribution;
};

export type PluginWorkerPerformanceProfile = {
  pluginId: string; poolSize: number; windowCapacity: number; sampleCount: number; successCount: number; failureCount: number;
  windowStartedAt?: string | null; windowEndedAt?: string | null; requestsPerSecond: number; estimatedPoolUtilization: number; queuePressureRatio: number;
  dominantStage: string; recommendedPoolSize: number; recommendationReady: boolean; recommendation: string;
  hostInputEncode: PluginWorkerLatencyDistribution; queueWait: PluginWorkerLatencyDistribution; ipcRoundTrip: PluginWorkerLatencyDistribution;
  ipcOverhead: PluginWorkerLatencyDistribution; workerInputDecode: PluginWorkerLatencyDistribution; pluginExecute: PluginWorkerLatencyDistribution;
  workerOutputEncode: PluginWorkerLatencyDistribution; hostOutputDecode: PluginWorkerLatencyDistribution; total: PluginWorkerLatencyDistribution;
  tools: PluginWorkerToolPerformanceProfile[];
};


export type PluginBenchmarkRegressionPolicy = {
  maximumP95RegressionPercent: number;
  maximumP99RegressionPercent: number;
  minimumThroughputRatio: number;
  maximumWorkingSetRegressionPercent: number;
  maximumFailureRate: number;
};

export type PluginBenchmarkPoolResult = {
  poolSize: number;
  itemCount: number;
  successCount: number;
  failureCount: number;
  failureRate: number;
  totalDurationMs: number;
  throughputPerSecond: number;
  observedWorkingSetBytes: number;
  workflowDuration: PluginWorkerLatencyDistribution;
  workerPerformance: PluginWorkerPerformanceProfile;
};

export type PluginBenchmarkRecommendation = {
  recommendedPoolSize: number;
  reason: string;
  maximumThroughputPerSecond: number;
  recommendedThroughputPerSecond: number;
  recommendedP95Ms: number;
};

export type PluginBenchmarkRegressionGate = {
  status: 'PASS' | 'FAIL' | 'NO_BASELINE' | 'NOT_COMPARABLE' | string;
  baselineRunId?: string | null;
  comparedPoolSize?: number | null;
  p95DeltaPercent?: number | null;
  p99DeltaPercent?: number | null;
  throughputRatio?: number | null;
  workingSetDeltaPercent?: number | null;
  candidateFailureRate?: number | null;
  reasons: string[];
  baselinePluginVersion?: string | null;
  baselinePluginAssemblySha256?: string | null;
};

export type PluginBenchmarkRun = {
  runId: string;
  datasetId: string;
  validationRunId: string;
  pluginId: string;
  pluginVersion: string;
  pluginAssemblySha256?: string | null;
  workflowHash: string;
  status: string;
  poolSizes: number[];
  requestedCount: number;
  warmupCount: number;
  workloadConcurrency: number;
  baselineRunId?: string | null;
  regressionPolicy: PluginBenchmarkRegressionPolicy;
  startedAt: string;
  completedAt?: string | null;
  cancelRequested: boolean;
  error?: string | null;
  recommendation?: PluginBenchmarkRecommendation | null;
  regressionGate?: PluginBenchmarkRegressionGate | null;
  results?: PluginBenchmarkPoolResult[] | null;
};

export type CameraTriggerMode = 'Continuous' | 'Software' | 'External';
export type CameraOutputPixelFormat = 'Auto' | 'Mono8' | 'Bgr8';

export type CameraSettings = {
  exposureUs: number;
  gainDb: number;
  targetFps: number;
  triggerMode: CameraTriggerMode;
  outputPixelFormat: CameraOutputPixelFormat;
  externalTriggerSource: string;
};

export type CameraCapabilities = {
  exposure: boolean;
  gain: boolean;
  frameRate: boolean;
  softwareTrigger: boolean;
  externalTrigger: boolean;
  hostSimulatedExternalTrigger: boolean;
  minExposureUs: number;
  maxExposureUs: number;
  minGainDb: number;
  maxGainDb: number;
  maxFps: number;
  outputPixelFormats?: CameraOutputPixelFormat[] | null;
};

export type CameraTransportTelemetry = {
  cameraId: string;
  driver: string;
  native: boolean;
  source: string;
  capturedAt: string;
  receivedBytes?: number | null;
  receivedFrames?: number | null;
  lostFrames?: number | null;
  failedFrames?: number | null;
  bufferUnderruns?: number | null;
  receivedPackets?: number | null;
  lostPackets?: number | null;
  failedPackets?: number | null;
  resendRequests?: number | null;
  resentPackets?: number | null;
  resynchronizations?: number | null;
  throughputMbps?: number | null;
  error?: string | null;
  hasNativeCounters?: boolean;
};

export type CameraAcquisitionStats = {
  acquisitionState: 'Stopped' | 'Starting' | 'Running' | 'WaitingTrigger' | 'Reconnecting' | 'Faulted';
  framesPublished: number;
  ringOverwrites: number;
  acquisitionErrors: number;
  reconnectCount: number;
  lastSequence: number;
  actualFps: number;
  lastFrameAt?: string | null;
  runtimeError?: string | null;
  frameTimeouts: number;
  driverDroppedFrames: number;
  nativePixelFormat?: string | null;
  transport?: CameraTransportTelemetry | null;
};

export type CameraDescriptor = {
  id: string;
  name: string;
  driver: string;
  state: 'Closed' | 'Open' | 'Streaming' | 'Faulted';
  framesCaptured: number;
  source?: string | null;
  error?: string | null;
  settings: CameraSettings;
  capabilities: CameraCapabilities;
  acquisition: CameraAcquisitionStats;
};

export type CameraAdapterDescriptor = {
  driver: string;
  vendor: string;
  isSdkAvailable: boolean;
  sdkError?: string | null;
};

export type CameraDiscoveredDevice = {
  driver: string;
  vendor: string;
  model: string;
  serialNumber: string;
  userDefinedName?: string | null;
  ipAddress?: string | null;
  transport?: string | null;
  deviceKey: string;
  firmwareVersion?: string | null;
};

export type RobotRuntimeSettings = {
  linearSpeedMmPerSec: number;
  angularSpeedDegPerSec: number;
  positionToleranceMm: number;
  angleToleranceDeg: number;
};

export type RobotCapabilities = {
  readCurrentPose: boolean;
  sendTarget: boolean;
  move2D: boolean;
  stop: boolean;
  resetFault: boolean;
  acknowledge: boolean;
  industrialHandshake: boolean;
  maxLinearSpeedMmPerSec: number;
  maxAngularSpeedDegPerSec: number;
};

export type RobotHandshakeSignals = {
  targetReady: boolean;
  execute: boolean;
  busy: boolean;
  complete: boolean;
  error: boolean;
  ack: boolean;
  commandId: number;
  errorCode?: string | null;
  updatedAt: string;
};

export type CoordinatePose2D = {
  x: number;
  y: number;
  thetaDeg: number;
  frame: string;
  unit: string;
};

export type RobotTarget2D = {
  x: number;
  y: number;
  rDeg: number;
  frame: string;
  unit: string;
  robot: string;
  guidanceMode: string;
};

export type RobotDescriptor = {
  id: string;
  name: string;
  vendor: string;
  model: string;
  driver: string;
  baseFrame: string;
  unit: string;
  connectionState: 'Disconnected' | 'Connecting' | 'Connected' | 'Faulted';
  handshakeState: 'Disconnected' | 'Ready' | 'TargetAccepted' | 'Executing' | 'InPosition' | 'Stopped' | 'Faulted';
  handshake: RobotHandshakeSignals;
  currentPose: CoordinatePose2D;
  activeTarget?: RobotTarget2D | null;
  lastCommandId: number;
  busy: boolean;
  inPosition: boolean;
  error?: string | null;
  settings: RobotRuntimeSettings;
  capabilities: RobotCapabilities;
  updatedAt: string;
};

export type RobotCommandTraceEvent = {
  traceId: string;
  robotId: string;
  commandId: number;
  attempt: number;
  stage: string;
  timestamp: string;
  message: string;
  handshake?: RobotHandshakeSignals | null;
  target?: RobotTarget2D | null;
  error?: string | null;
};

export type RobotCommandTraceRecord = {
  traceId: string;
  robotId: string;
  startedAt: string;
  updatedAt: string;
  commandId: number;
  maxAttempt: number;
  lastStage: string;
  status: string;
  target?: RobotTarget2D | null;
  error?: string | null;
  events: RobotCommandTraceEvent[];
};

export type WorkflowPayload = {
  id: string;
  name: string;
  nodes: Array<{
    id: string;
    type: string;
    name?: string | null;
    position?: { x: number; y: number } | null;
    parameters?: Record<string, unknown>;
  }>;
  edges: Array<{
    id: string;
    sourceNodeId: string;
    sourcePort: string;
    targetNodeId: string;
    targetPort: string;
    kind?: string | null;
  }>;
};

export type JobVersionValidationInfo = {
  validationRunId: string; datasetId: string; accepted: boolean; linkedAt: string; reason: string;
  summary?: ValidationRunSummary | null; policy?: { minimumAccuracy: number; maximumFalseOkRate: number; maximumFalseNgRate: number; maximumErrors: number } | null;
};

export type JobVersionInfo = {
  version: number;
  workflowHash: string;
  createdAt: string;
  note: string;
  published: boolean;
  validation?: JobVersionValidationInfo | null;
  bindingCount?: number;
};

export type PublicationEvent = {
  version: number;
  action: string;
  at: string;
  dependencyManifestHash?: string | null;
};

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
  productId?: string | null;
  recipeCode?: string | null;
};


export type ProductRecipeSummary = { id: string; recipeCode: string; name: string; description: string; latestVersion: number; activeVersion?: number | null; lifecycleState: 'Draft' | 'Validated' | 'Published'; updatedAt: string; };
export type ProductDescriptor = { id: string; name: string; description: string; createdAt: string; updatedAt: string; recipes: ProductRecipeSummary[]; };
export type ValidationCandidateInfo = { validationRunId: string; datasetId: string; startedAt: string; completedAt?: string | null; summary: ValidationRunSummary; };
export type JobVersionDiff = { jobId: string; fromVersion: number; toVersion: number; fromHash: string; toHash: string; changed: boolean; changes: Array<{kind:string;path:string;before?:string|null;after?:string|null}>; };
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

export type ParameterValueSet = { scope: 'product'|'recipe'; ownerId: string; values: Record<string,unknown>; updatedAt?: string|null; };
export type ResolvedRecipeParameterization = {
  baseWorkflow: WorkflowPayload; effectiveWorkflow: WorkflowPayload; baseWorkflowHash: string; effectiveWorkflowHash: string;
  bindings: Record<string,string>; snapshot: Record<string,unknown>; bindingCount: number;
};

export type RuntimeDependencyDrift = {
  kind: string;
  id: string;
  expected: string;
  actual?: string | null;
  message: string;
};

export type RuntimeDependencyValidation = {
  compatible: boolean;
  expectedManifestHash: string;
  currentManifestHash: string;
  drifts: RuntimeDependencyDrift[];
};

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

export type TraceNodeDelta = {
  nodeId: string;
  nodeType: string;
  baselineSuccess?: boolean | null;
  currentSuccess?: boolean | null;
  baselineDurationMs?: number | null;
  currentDurationMs?: number | null;
  durationDeltaMs?: number | null;
  durationRatio?: number | null;
  summaryChanged: boolean;
  summaryChangedKeys: string[];
  errorChanged: boolean;
  performanceStatus: 'Insufficient' | 'Normal' | 'Slow' | 'VerySlow' | string;
  status: 'Same' | 'Added' | 'Missing' | 'TypeChanged' | 'StatusChanged' | 'OutputChanged' | 'PerformanceRegression' | string;
};

export type RunTraceComparison = {
  runId: string;
  baselineRunId?: string | null;
  baselineSelection: 'AutoPreviousOk' | 'Explicit' | string;
  baselineAvailable: boolean;
  sameWorkflowIdentity: boolean;
  workflowHashChanged: boolean;
  currentDisposition: string;
  baselineDisposition?: string | null;
  currentDurationMs: number;
  baselineDurationMs?: number | null;
  durationDeltaMs?: number | null;
  durationRatio?: number | null;
  changedNodeCount: number;
  statusChangedNodeCount: number;
  performanceRegressionNodeCount: number;
  nodes: TraceNodeDelta[];
};

export type FailureSignatureOccurrence = {
  runId: string;
  startedAt: string;
  disposition: string;
  totalDurationMs: number;
  nodeDurationMs?: number | null;
};

export type FailureSignatureAnalysis = {
  runId: string;
  hasSignature: boolean;
  signatureId?: string | null;
  category: 'None' | 'ExecutionFailure' | 'QualityNG' | 'PerformanceRegression' | string;
  primaryNodeId?: string | null;
  primaryNodeType?: string | null;
  fingerprint: string;
  occurrenceCount: number;
  recurring: boolean;
  windowDays: number;
  recentOccurrences: FailureSignatureOccurrence[];
  recommendedAction?: string | null;
  replayNodeId?: string | null;
};

export type TraceStats = {
  total: number;
  ok: number;
  ng: number;
  review: number;
  errors: number;
  averageDurationMs: number;
};

export type TraceRetentionOptions = {
  metadataRetentionDays: number;
  okArtifactRetentionDays: number;
  ngArtifactRetentionDays: number;
  errorArtifactRetentionDays: number;
  reviewArtifactRetentionDays: number;
  okPreviewSampleEvery: number;
  okReplaySampleEvery: number;
  cleanupIntervalMinutes: number;
};

export type StorageCapacityStatus = {
  level: 'Normal' | 'Warning' | 'Critical';
  availableFreeBytes: number;
  totalBytes: number;
  databaseBytes: number;
  artifactBytes: number;
  backupBytes: number;
  artifactQuotaExceeded: boolean;
  backupQuotaExceeded: boolean;
  productionStartAllowed: boolean;
  artifactWritesAllowed: boolean;
  sampledAt: string;
};

export type SchemaMigrationHistoryEntry = {
  version: number;
  name: string;
  checksum: string;
  appliedAt: string;
  durationMs: number;
  baselined: boolean;
};

export type SchemaMigrationStatus = {
  currentVersion: number;
  targetVersion: number;
  upToDate: boolean;
  history: SchemaMigrationHistoryEntry[];
};

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

export type NodeReplayComparison = {
  nodeId: string;
  nodeType: string;
  baselineSuccess?: boolean | null;
  replaySuccess?: boolean | null;
  baselineDurationMs?: number | null;
  replayDurationMs?: number | null;
  durationDeltaMs?: number | null;
  summaryChanged: boolean;
  status: 'Same' | 'Changed' | 'StatusChanged' | 'NotExecuted' | 'New' | string;
};

export type OfflineReplayResult = RunResult & {
  sourceRunId: string;
  comparison: NodeReplayComparison[];
  baselineDisposition?: string | null;
  dispositionChanged: boolean;
  executionModel: string;
};

export type ValidationDatasetSummary = {
  id: string; name: string; description: string; topologyHash?: string | null; itemCount: number; createdAt: string; updatedAt: string;
};
export type ValidationDatasetItem = {
  itemId: string; datasetId: string; sourceKind: 'TRACE' | 'MEDIA'; sourceRef: string; sourceRunId?: string | null; expectedDisposition: 'OK' | 'NG'; note?: string | null; sortOrder: number; addedAt: string; sourceDisposition: string; displayName: string; replayReady: boolean;
};
export type ValidationDatasetDetail = { dataset: ValidationDatasetSummary; items: ValidationDatasetItem[] };
export type ValidationRunSummary = {
  total: number; completed: number; trueOk: number; trueNg: number; falseOk: number; falseNg: number; errors: number; accuracy: number; falseOkRate: number; falseNgRate: number; p50DurationMs: number; p95DurationMs: number; p99DurationMs: number; maxDurationMs: number;
};
export type ValidationResultRecord = {
  runId: string; itemId: string; sourceKind: 'TRACE' | 'MEDIA'; sourceRef: string; expectedDisposition: string; actualDisposition: string; classification: 'TRUE_OK' | 'TRUE_NG' | 'FALSE_OK' | 'FALSE_NG' | 'ERROR' | string; replayRunId?: string | null; success: boolean; durationMs: number; failedNodeIds: string[]; error?: string | null; completedAt: string;
};
export type ValidationRunRecord = {
  runId: string; datasetId: string; status: 'Running' | 'Completed' | 'Cancelled' | 'Failed' | string; sourceWorkflowRunId: string; workflowHash: string; requestedCount: number; completedCount: number; startedAt: string; completedAt?: string | null; cancelRequested: boolean; error?: string | null; summary?: ValidationRunSummary | null; results?: ValidationResultRecord[] | null;
};

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

export type CalibrationWorkspacePoint = {
  index: number;
  imageX: number;
  imageY: number;
  worldX: number;
  worldY: number;
  enabled: boolean;
  source: string;
};

export type CalibrationResidual = {
  index: number;
  imageX: number;
  imageY: number;
  worldX: number;
  worldY: number;
  predictedX: number;
  predictedY: number;
  dx: number;
  dy: number;
  error: number;
  source: string;
};

export type CalibrationHeatCell = {
  column: number;
  row: number;
  x: number;
  y: number;
  error: number;
};

export type VisionTransform2D = {
  sourceFrame: string;
  targetFrame: string;
  targetUnit: string;
  h00: number; h01: number; h02: number;
  h10: number; h11: number; h12: number;
  h20: number; h21: number; h22: number;
  matrix?: number[];
};

export type CalibrationWorkspaceRequest = {
  sourceFrame: string;
  targetFrame: string;
  targetUnit: string;
  points: CalibrationWorkspacePoint[];
  verificationPoints: CalibrationWorkspacePoint[];
  heatmapColumns: number;
  heatmapRows: number;
};

export type CalibrationWorkspaceResult = {
  transform: VisionTransform2D;
  rmse: number;
  maxError: number;
  residuals: CalibrationResidual[];
  verificationRmse?: number | null;
  verificationMaxError?: number | null;
  verificationResiduals: CalibrationResidual[];
  heatmap: CalibrationHeatCell[];
  usedPointCount: number;
  verificationPointCount: number;
};

export type CalibrationVersionInfo = {
  version: number;
  snapshotHash: string;
  createdAt: string;
  note: string;
  published: boolean;
};

export type CalibrationAssetDescriptor = {
  id: string;
  name: string;
  description: string;
  createdAt: string;
  updatedAt: string;
  latestVersion: number;
  publishedVersion?: number | null;
  versions: CalibrationVersionInfo[];
  publicationHistory: Array<{ version: number; action: string; at: string }>;
};

export type CalibrationVersionSnapshot = {
  assetId: string;
  version: number;
  snapshotHash: string;
  createdAt: string;
  note: string;
  workspace: CalibrationWorkspaceRequest;
  result: CalibrationWorkspaceResult;
};

export type DeviceConnectionState = 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting' | 'Faulted';
export type DeviceTagQuality = 'Good' | 'Uncertain' | 'Bad' | 'Disconnected';
export type DeviceTagDataType = 'Boolean' | 'Integer' | 'Double' | 'String';

export type DeviceTagDefinition = {
  id: string;
  name: string;
  address: string;
  dataType: DeviceTagDataType;
  writable: boolean;
  unit?: string | null;
  description?: string | null;
};

export type DeviceTagSample = {
  deviceId: string;
  tagId: string;
  dataType: DeviceTagDataType;
  value: unknown;
  quality: DeviceTagQuality;
  timestamp: string;
  error?: string | null;
};

export type DeviceRuntimeSettings = {
  pollIntervalMs: number;
  reconnectDelayMs: number;
  heartbeatIntervalMs: number;
  hostHeartbeatTagId: string;
  deviceHeartbeatTagId: string;
};

export type DeviceRuntimeStats = {
  pollCycles: number;
  batchReadCycles: number;
  readErrors: number;
  writeErrors: number;
  reconnectCount: number;
  actualPollHz: number;
  lastPollAt?: string | null;
  lastGoodReadAt?: string | null;
  lastDeviceHeartbeatAt?: string | null;
  hostHeartbeat: boolean;
  deviceHeartbeat: boolean;
  runtimeError?: string | null;
};

export type DeviceDriverCapabilities = {
  supportsBatchRead: boolean;
  supportsBatchWrite: boolean;
  supportsDiagnostics: boolean;
  realIo: boolean;
  supportsString: boolean;
};

export type DeviceProtocolDiagnostics = {
  transactions: number;
  batchReads: number;
  batchWrites: number;
  lastRoundTripMs: number;
  averageRoundTripMs: number;
  lastTransactionAt?: string | null;
  lastProtocolError?: string | null;
};

export type DeviceDescriptor = {
  id: string;
  name: string;
  vendor: string;
  model: string;
  driver: string;
  protocol: string;
  endpoint: string;
  connectionState: DeviceConnectionState;
  settings: DeviceRuntimeSettings;
  stats: DeviceRuntimeStats;
  capabilities: DeviceDriverCapabilities;
  diagnostics: DeviceProtocolDiagnostics;
  tags: DeviceTagDefinition[];
  values: Record<string, DeviceTagSample>;
  error?: string | null;
  updatedAt: string;
};

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

export type ProductionPtpGuardIssue = { code: string; groupId: string; message: string };
export type ProductionPtpGuardGroupStatus = {
  groupId: string; groupName: string; requiredByScheduledCapture: boolean; requiredByGroupPolicy: boolean;
  liveReady: boolean; windowRuns: number; evidenceRuns: number; ptpReadyRate?: number | null; p95MaxAbsOffsetNs?: number | null;
  masterClockChanges: number; inconsistentMasterClockRuns: number; currentMasterClockId?: string | null; healthy: boolean; reason?: string | null;
};
export type ProductionPtpGuardSnapshot = {
  enabled: boolean; healthy: boolean; evaluatedAt: string; groups: ProductionPtpGuardGroupStatus[]; issues: ProductionPtpGuardIssue[]; summary?: string | null;
};

export type ProductionSynchronizationTransportWindow = {
  evidenceMode: string; nativeCameras: number; nativeEvidenceRuns: number; receivedFrames: number; lostFrames: number; failedFrames: number; bufferUnderruns: number;
  nativeFrameLossRate?: number | null; lostPackets: number; failedPackets: number; resendRequests: number; resentPackets: number; resynchronizations: number; averageThroughputMbps?: number | null;
};
export type ProductionSynchronizationGuardIssue = { code: string; groupId: string; message: string; liveRecoverable: boolean };
export type ProductionSynchronizationGuardGroupStatus = {
  groupId: string; groupName: string; liveReady: boolean; windowRuns: number; evidenceRuns: number; completedRuns: number; failedRuns: number; cancelledRuns: number;
  completionRate: number; frameTimeoutRuns: number; frameTimeoutRate: number; actionCommandFailureRuns: number; actionAckShortfallRuns: number; skewViolationRuns: number;
  consecutiveFailures: number; consecutiveSkewViolations: number; estimatedSequenceGapFrames: number; estimatedSequenceGapRate: number; healthy: boolean; reason?: string | null; transport?: ProductionSynchronizationTransportWindow | null;
};
export type ProductionSynchronizationGuardSnapshot = {
  enabled: boolean; healthy: boolean; evaluatedAt: string; groups: ProductionSynchronizationGuardGroupStatus[]; issues: ProductionSynchronizationGuardIssue[]; summary?: string | null; liveRecoverableOnly?: boolean;
};

export type AlarmRecord = {
  id: string;
  code: string;
  severity: 'Info' | 'Warning' | 'Error' | 'Critical';
  source: string;
  message: string;
  active: boolean;
  acknowledged: boolean;
  raisedAt: string;
  recoveredAt?: string | null;
  acknowledgedAt?: string | null;
  runId?: string | null;
};

export type AssetKind = 'Camera' | 'Device' | 'Robot';
export type AssetHealthLevel = 'Unknown' | 'Healthy' | 'Degraded' | 'Faulted' | 'Offline';
export type AssetEventSeverity = 'Info' | 'Warning' | 'Error' | 'Critical';

export type AssetHealthSnapshot = {
  kind: AssetKind;
  id: string;
  name: string;
  driver: string;
  level: AssetHealthLevel;
  state: string;
  connected: boolean;
  activity: string;
  error?: string;
  metrics: Record<string, number>;
  updatedAt: string;
  key: string;
};

export type AssetEventEnvelope = {
  eventId: string;
  timestamp: string;
  kind: AssetKind;
  assetId: string;
  assetName: string;
  type: string;
  severity: AssetEventSeverity;
  level: AssetHealthLevel;
  state: string;
  message: string;
  previousState?: string;
  metadata?: Record<string, string>;
};

export type DiagnosticsSummary = {
  total: number;
  healthy: number;
  degraded: number;
  faulted: number;
  offline: number;
  updatedAt: string;
};

export type MediaLibraryStatus = {
  root: string;
  collectionCount: number;
  itemCount: number;
  ngItemCount: number;
  totalBytes: number;
  maxImportBytes: number;
  maxLibraryBytes: number;
  allowedExtensions: string[];
};

export type MediaCollectionDescriptor = {
  name: string;
  source: string;
  itemCount: number;
  ngItemCount: number;
  totalBytes: number;
  lastModifiedAt?: string | null;
};

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


export type HardwareProvenanceData = {
  manufacturer?: string | null;
  productName?: string | null;
  model?: string | null;
  serialNumber?: string | null;
  hardwareRevision?: string | null;
  firmwareVersion?: string | null;
  softwareVersion?: string | null;
  controllerVersion?: string | null;
  programName?: string | null;
  programHash?: string | null;
  attributes?: Record<string, string> | null;
};

export type HardwareProvenanceDeclaration = HardwareProvenanceData;

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

export type CameraCommissioningCapabilities = {
  featureBrowser: boolean;
  gigENetworkTuning: boolean;
  triggerDelay: boolean;
  lineDebouncer: boolean;
  strobeOutput: boolean;
  ptp: boolean;
  actionCommand: boolean;
  inputLines?: string[] | null;
  outputLines?: string[] | null;
  outputSources?: string[] | null;
};

export type CameraCommissioningProfile = {
  schemaVersion: number;
  acquisition?: CameraSettings | null;
  packetSizeBytes?: number | null;
  interPacketDelayTicks?: number | null;
  triggerDelayUs?: number | null;
  triggerInputLine?: string | null;
  lineDebouncerUs?: number | null;
  outputLine?: string | null;
  outputSource?: string | null;
  outputInverted?: boolean | null;
  strobeEnabled?: boolean | null;
  strobeDelayUs?: number | null;
  strobeDurationUs?: number | null;
  strobePreDelayUs?: number | null;
  ptpEnabled?: boolean | null;
  actionDeviceKey?: number | null;
  actionGroupKey?: number | null;
  actionGroupMask?: number | null;
  actionSelector?: number | null;
  advancedFeatures?: Record<string, string> | null;
};

export type CameraCommissioningSnapshot = {
  cameraId: string;
  driver: string;
  capabilities: CameraCommissioningCapabilities;
  profileHash?: string | null;
  profile: CameraCommissioningProfile;
};

export type CameraFeatureDescriptor = {
  key: string;
  displayName: string;
  category: string;
  valueKind: 'Boolean' | 'Integer' | 'Float' | 'Enum' | 'String';
  value?: string | null;
  readable: boolean;
  writable: boolean;
  profileEligible: boolean;
  unit?: string | null;
  min?: number | null;
  max?: number | null;
  increment?: number | null;
  options?: string[] | null;
  description?: string | null;
};

export type CameraFeatureProfileRecord = {
  id: string;
  name: string;
  driver: string;
  sourceCameraId?: string | null;
  profileHash: string;
  profile: CameraCommissioningProfile;
  createdAt: string;
  updatedAt: string;
};

export type CameraPtpClockState = 'Unsupported' | 'Disabled' | 'Initializing' | 'Listening' | 'Master' | 'Slave' | 'Locked' | 'Faulted' | 'Unknown';
export type CameraTimeSynchronizationStatus = {
  cameraId: string;
  driver: string;
  ptpSupported: boolean;
  ptpEnabled: boolean;
  state: CameraPtpClockState;
  offsetFromMasterNs?: number | null;
  deviceTimestampNs?: number | null;
  masterClockId?: string | null;
  deviceTickFrequencyHz?: number | null;
  error?: string | null;
  capturedAt?: string | null;
};
export type CameraSynchronizationGroup = {
  id: string;
  name: string;
  driver: string;
  cameraIds: string[];
  deviceKey: number;
  groupKey: number;
  groupMask: number;
  broadcastAddress: string;
  requirePtpLocked: boolean;
  maxPtpOffsetNs: number;
  maxTriggerSkewUs: number;
  scheduledLeadTimeMs: number;
  configurationHash: string;
  createdAt: string;
  updatedAt: string;
};
export type CameraSynchronizationMemberStatus = {
  cameraId: string;
  driver: string;
  cameraState: string;
  acquisitionState: string;
  timeSync: CameraTimeSynchronizationStatus;
  latestFrame?: { cameraId: string; sequence: number; hostTimestamp: string; deviceTimestampNs?: number | null; triggerId?: number | null } | null;
};
export type CameraSynchronizationGroupStatus = {
  group: CameraSynchronizationGroup;
  ready: boolean;
  ptpReady: boolean;
  error?: string | null;
  members: CameraSynchronizationMemberStatus[];
  latestSkewUs?: number | null;
  timestampBasis: string;
};
export type CameraSynchronizationCaptureResult = {
  groupId: string;
  scheduled: boolean;
  scheduledDeviceTimeNs?: number | null;
  command: { driver: string; scheduled: boolean; scheduledDeviceTimeNs?: number | null; issuedAt: string; acknowledgedDevices: number; messages: string[] };
  frames: Array<{ cameraId: string; sequence: number; hostTimestamp: string; deviceTimestampNs?: number | null; triggerId?: number | null }>;
  triggerSkewUs: number;
  timestampBasis: string;
  withinTolerance: boolean;
  maxAllowedSkewUs: number;
  completedAt: string;
};
export type CameraSynchronizationCommissioningAssessment = {
  status: 'InsufficientData' | 'Pass' | 'Fail' | string;
  passed: boolean;
  minimumSamples: number;
  requiredCompletionRate: number;
  requiredTolerancePassRate: number;
  reasons: string[];
};

export type CameraSynchronizationStatistics = {
  groupId: string;
  sampleLimit: number;
  totalRuns: number;
  completedRuns: number;
  failedRuns: number;
  cancelledRuns: number;
  measuredRuns: number;
  withinToleranceRuns: number;
  completionRate: number;
  tolerancePassRate: number;
  averageSkewUs?: number | null;
  p50SkewUs?: number | null;
  p95SkewUs?: number | null;
  p99SkewUs?: number | null;
  maxSkewUs?: number | null;
  averageDurationMs?: number | null;
  p95DurationMs?: number | null;
  devicePtpRuns: number;
  hostArrivalRuns: number;
  firstCompletedAt?: string | null;
  lastCompletedAt?: string | null;
  commissioning: CameraSynchronizationCommissioningAssessment;
};

export type CameraSynchronizationPtpCameraDiagnostics = { cameraId: string; samples: number; readySamples: number; readyRate: number; offsetSamples: number; averageAbsOffsetNs?: number | null; p95AbsOffsetNs?: number | null; maxAbsOffsetNs?: number | null; masterClockChanges: number; masterClockIds: string[] };
export type CameraSynchronizationPtpTrendPoint = { runId: string; completedAt: string; outcome: string; triggerSkewUs?: number | null; maxAbsOffsetNs?: number | null; allPtpReady: boolean; masterClockConsistent: boolean; masterClockId?: string | null };
export type CameraSynchronizationPtpDiagnostics = { groupId: string; sampleLimit: number; totalRuns: number; runsWithPtpEvidence: number; ptpReadyRuns: number; ptpNotReadyRuns: number; inconsistentMasterClockRuns: number; offsetSkewPearsonCorrelation?: number | null; maxAbsOffsetNs?: number | null; p95MaxAbsOffsetNs?: number | null; cameras: CameraSynchronizationPtpCameraDiagnostics[]; trend: CameraSynchronizationPtpTrendPoint[] };

export type GigEHostAddress = { address: string; mask?: string | null };
export type GigEHostNetworkInterface = { id: string; name: string; description: string; operationalStatus: string; interfaceType: string; speedMbps: number; mtuBytes?: number | null; ipv4Addresses: GigEHostAddress[]; bytesReceived?: number | null; bytesSent?: number | null; incomingPacketErrors?: number | null; incomingPacketDiscards?: number | null; outgoingPacketErrors?: number | null; outgoingPacketDiscards?: number | null };
export type GigECameraNetworkDiagnostic = { cameraId: string; driver: string; cameraIpAddress?: string | null; nicId?: string | null; nicName?: string | null; nicSpeedMbps?: number | null; nicMtuBytes?: number | null; packetSizeBytes?: number | null; interPacketDelayTicks?: number | null; currentThroughputMbps?: number | null; currentNicUtilization?: number | null; nativeTransportTelemetry: boolean; transportSource: string; recommendations: string[] };
export type GigENicLoadDiagnostic = { nicId: string; nicName: string; speedMbps: number; mtuBytes?: number | null; cameraIds: string[]; currentCameraThroughputMbps: number; currentUtilization: number; level: string };
export type GigENetworkTrendPoint = { runId: string; completedAt: string; nativeCameraSamples: number; throughputByCameraMbps: Record<string, number>; totalThroughputMbps: number; receivedFramesDelta: number; lostFramesDelta: number; failedFramesDelta: number; bufferUnderrunsDelta: number; receivedPacketsDelta: number; lostPacketsDelta: number; failedPacketsDelta: number; resendRequestsDelta: number; resentPacketsDelta: number; resynchronizationsDelta: number; frameIssueRate?: number | null; packetIssueRate?: number | null; resendRequestRate?: number | null };
export type GigENetworkAssessment = { status: 'Pass' | 'Fail' | 'InsufficientData' | string; passed: boolean; minimumEvidenceRuns: number; evidenceRuns: number; maximumRecommendedNicUtilization: number; maximumFrameIssueRate: number; maximumPacketIssueRate: number; maximumResendRequestRate: number; peakNicUtilization?: number | null; aggregateFrameIssueRate?: number | null; aggregatePacketIssueRate?: number | null; aggregateResendRequestRate?: number | null; reasons: string[]; recommendations: string[] };
export type GigENetworkDiagnostics = { groupId: string; groupName: string; groupConfigurationHash: string; capturedAt: string; historyRuns: number; hostInterfaces: GigEHostNetworkInterface[]; cameras: GigECameraNetworkDiagnostic[]; nicLoads: GigENicLoadDiagnostic[]; trend: GigENetworkTrendPoint[]; assessment: GigENetworkAssessment };
export type GigENetworkCommissioningReport = { reportSchemaVersion: string; testId: string; groupId: string; status: string; requestedIterations: number; completedIterations: number; startedAt: string; completedAt?: string | null; diagnostics: GigENetworkDiagnostics; evidenceHash: string; notes: string[] };

export type CameraSynchronizationCommissioningCameraEvidence = { cameraId: string; provider: string; hardware: { manufacturer?: string | null; productName?: string | null; model?: string | null; serialNumber?: string | null; hardwareRevision?: string | null; firmwareVersion?: string | null; softwareVersion?: string | null; controllerVersion?: string | null; programName?: string | null; programHash?: string | null; attributes?: Record<string,string> | null }; hardwareFingerprint: string; completeness: string; liveProbeConfigured: boolean; liveProbeSucceeded: boolean; liveProbeError?: string | null; ptpState: string; ptpSupported: boolean; ptpEnabled: boolean; offsetFromMasterNs?: number | null; masterClockId?: string | null; commissioningProfileHash?: string | null };
export type CameraSynchronizationCommissioningReport = {
  reportSchemaVersion: string; testId: string; groupId: string; groupName: string; groupDriver: string; groupConfigurationHash: string; cameraIds: string[]; scheduled: boolean; requestedIterations: number; completedIterations: number; startedAt: string; completedAt: string; maxAllowedSkewUs: number; requirePtpLocked: boolean; maxPtpOffsetNs: number;
  statistics: CameraSynchronizationStatistics; cameras: CameraSynchronizationCommissioningCameraEvidence[]; trend: { index: number; runId: string; completedAt: string; outcome: string; triggerSkewUs?: number | null; withinTolerance?: boolean | null; timestampBasis: string }[]; worstRuns: { runId: string; completedAt: string; outcome: string; triggerSkewUs?: number | null; withinTolerance?: boolean | null; timestampBasis: string; error?: string | null; frames: { cameraId: string; sequence: number; hostTimestamp: string; deviceTimestampNs?: number | null; triggerId?: number | null; deltaFromFirstUs: number }[] }[]; signoff: { preparedBy: string; reviewedBy: string; approvedBy: string; notes: string; signedAt?: string | null }; evidenceHash: string; notes: string[]; ptpDiagnostics?: CameraSynchronizationPtpDiagnostics | null;
};
export type CameraSynchronizationCommissioningTest = {
  testId: string; groupId: string; groupConfigurationHash: string; scheduled: boolean; requestedIterations: number; completedIterations: number; frameTimeoutMs: number; delayMs: number; minimumSamples: number;
  status: 'Queued' | 'Running' | 'Completed' | 'Cancelled' | 'Failed' | string; startedAt: string; completedAt?: string | null; error?: string | null; report?: CameraSynchronizationCommissioningReport | null;
};

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


export type WorkflowModulePort = {
  name: string;
  dataType: string;
  required: boolean;
  internalNodeId: string;
  internalPort: string;
};

export type WorkflowModuleParameter = ParameterDescriptor & {
  defaultValue: unknown;
  internalNodeId: string;
  internalParameter: string;
};

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

export type WorkflowModuleDescriptor = {
  id: string;
  name: string;
  description: string;
  createdAt: string;
  updatedAt: string;
  latestVersion: number;
  versions: WorkflowModuleVersion[];
};

export type ExtractWorkflowModuleResult = {
  module: WorkflowModuleDescriptor;
  version: WorkflowModuleVersion;
  replacementWorkflow: WorkflowPayload;
};

export type InvestigationCaseSummary = {
  id: string;
  signatureId: string;
  category: string;
  title: string;
  status: 'Open' | 'Investigating' | 'Resolved' | 'Verified' | 'Closed' | string;
  severity: 'Low' | 'Medium' | 'High' | 'Critical' | string;
  workflowId: string;
  workflowHash?: string | null;
  source: string;
  jobId?: string | null;
  jobVersion?: number | null;
  primaryNodeId?: string | null;
  primaryNodeType?: string | null;
  fingerprint: string;
  representativeRunId: string;
  firstSeenAt: string;
  lastSeenAt: string;
  occurrenceCount: number;
  reopenCount: number;
  owner?: string | null;
  rootCause?: string | null;
  resolutionNote?: string | null;
  verificationNote?: string | null;
  verificationRunId?: string | null;
  resolvedAt?: string | null;
  verifiedAt?: string | null;
  closedAt?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type InvestigationCaseRun = {
  runId: string;
  startedAt: string;
  disposition: string;
  totalDurationMs: number;
  nodeDurationMs?: number | null;
};

export type InvestigationVerificationEvidence = {
  id: string;
  caseId: string;
  datasetId: string;
  datasetName: string;
  baselineValidationRunId: string;
  candidateValidationRunId: string;
  baselineWorkflowHash: string;
  candidateWorkflowHash: string;
  gateStatus: 'Passed' | 'Failed' | 'Inconclusive' | string;
  baselineSignatureHits: number;
  candidateSignatureHits: number;
  baselineInspectableResults: number;
  candidateInspectableResults: number;
  baselineSummary: ValidationRunSummary;
  candidateSummary: ValidationRunSummary;
  reasons: string[];
  warnings: string[];
  note?: string | null;
  createdAt: string;
};

export type InvestigationCaseDetail = {
  case: InvestigationCaseSummary;
  evidenceRuns: InvestigationCaseRun[];
  verificationEvidence: InvestigationVerificationEvidence[];
};

export type InvestigationTrendPoint = {
  day: string;
  total: number;
  executionFailure: number;
  qualityNg: number;
  performanceRegression: number;
};

export type InvestigationSignatureTrend = {
  caseId: string;
  signatureId: string;
  title: string;
  category: string;
  status: string;
  severity: string;
  primaryNodeType?: string | null;
  occurrences: number;
  lastSeenAt: string;
};

export type InvestigationTrendDashboard = {
  windowDays: number;
  trackedCases: number;
  activeCases: number;
  verifiedOrClosedCases: number;
  evidenceOccurrences: number;
  timeline: InvestigationTrendPoint[];
  topSignatures: InvestigationSignatureTrend[];
};
