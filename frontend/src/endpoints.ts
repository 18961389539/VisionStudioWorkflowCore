/**
 * 端点常量：统一所有 /api/ 路径的根与资源域。
 *
 * 当前运行时代码仍有约 300 处硬编码 '/api/...'，由 `apiUsage.test.ts` 的 fetch 棘轮兜底。
 * 本模块按资源域提供常量与 builder，新代码必须从这里取路径；迁移中的面板逐步改写。
 * 当 `API_BASE` 需要变化（如部署到子路径）时，只需改一处。
 */
export const API_BASE = '/api';

/** 拼接 API 基路径与相对路径；`apiUrl('jobs')` → '/api/jobs'。 */
export function apiUrl(path: string): string {
  return `${API_BASE}/${path.replace(/^\//, '')}`;
}

// ── jobs ──────────────────────────────────────────────────────────────────────
export const jobs = {
  list: apiUrl('jobs'),
  byId: (id: string) => apiUrl(`jobs/${encodeURIComponent(id)}`),
  parameters: (id: string) => apiUrl(`jobs/${encodeURIComponent(id)}/parameters`),
  versions: (id: string) => apiUrl(`jobs/${encodeURIComponent(id)}/versions`),
  version: (id: string, version: number) => apiUrl(`jobs/${encodeURIComponent(id)}/versions/${version}`),
  run: (id: string) => apiUrl(`jobs/${encodeURIComponent(id)}/run`),
  clone: (id: string) => apiUrl(`jobs/${encodeURIComponent(id)}/clone`),
  resolve: (id: string) => apiUrl(`jobs/${encodeURIComponent(id)}/parameterization/resolve`),
  validationCandidates: (id: string, version: number) => apiUrl(`jobs/${encodeURIComponent(id)}/versions/${version}/validation-candidates`),
  validate: (id: string, version: number) => apiUrl(`jobs/${encodeURIComponent(id)}/versions/${version}/validation`),
  dependencies: (id: string) => apiUrl(`jobs/${encodeURIComponent(id)}/dependencies/validate`),
  diff: (id: string, from: number, to: number) => apiUrl(`jobs/${encodeURIComponent(id)}/versions/${from}/diff/${to}`)
} as const;

// ── traces ────────────────────────────────────────────────────────────────────
export const traces = {
  page: apiUrl('traces/page'),
  stats: apiUrl('traces/stats')
} as const;

// ── validation ────────────────────────────────────────────────────────────────
export const validation = {
  datasets: apiUrl('validation/datasets'),
  dataset: (id: string) => apiUrl(`validation/datasets/${encodeURIComponent(id)}`),
  datasetRuns: (id: string) => apiUrl(`validation/datasets/${encodeURIComponent(id)}/runs`),
  datasetItems: (id: string) => apiUrl(`validation/datasets/${encodeURIComponent(id)}/items`),
  datasetItem: (id: string, itemId: string) => apiUrl(`validation/datasets/${encodeURIComponent(id)}/items/${encodeURIComponent(itemId)}`),
  run: (runId: string) => apiUrl(`validation/runs/${encodeURIComponent(runId)}`),
  runCancel: (runId: string) => apiUrl(`validation/runs/${encodeURIComponent(runId)}/cancel`),
  runWorkflow: (runId: string) => apiUrl(`validation/runs/${encodeURIComponent(runId)}/workflow`)
} as const;

// ── runs ──────────────────────────────────────────────────────────────────────
export const runs = {
  images: (runId: string) => apiUrl(`runs/${encodeURIComponent(runId)}/images`),
  nodeImage: (runId: string, nodeId: string, portName?: string) => {
    const base = apiUrl(`runs/${encodeURIComponent(runId)}/nodes/${encodeURIComponent(nodeId)}/image`);
    return portName ? `${base}?portName=${encodeURIComponent(portName)}` : base;
  },
  preview: (runId: string) => apiUrl(`runs/${encodeURIComponent(runId)}/preview`)
} as const;

// ── cameras ───────────────────────────────────────────────────────────────────
export const cameras = {
  list: apiUrl('cameras'),
  adapters: apiUrl('cameras/adapters'),
  byId: (id: string) => apiUrl(`cameras/${encodeURIComponent(id)}`),
  action: (id: string, action: string) => apiUrl(`cameras/${encodeURIComponent(id)}/${action}`),
  commissioning: (id: string) => apiUrl(`cameras/${encodeURIComponent(id)}/commissioning`)
} as const;

// ── auth ──────────────────────────────────────────────────────────────────────
export const auth = {
  status: apiUrl('auth/status'),
  me: apiUrl('auth/me'),
  login: apiUrl('auth/login'),
  logout: apiUrl('auth/logout'),
  autoLogin: apiUrl('auth/auto-login'),
  bootstrap: apiUrl('auth/bootstrap')
} as const;

// ── plugins ───────────────────────────────────────────────────────────────────
export const plugins = {
  list: apiUrl('plugins'),
  packages: apiUrl('plugin-packages'),
  install: (mode: string) => apiUrl(`plugin-packages/${mode}`),
  publishers: apiUrl('plugin-publishers'),
  workers: apiUrl('plugin-workers'),
  benchmarks: apiUrl('plugin-benchmarks')
} as const;

// ── workflows ─────────────────────────────────────────────────────────────────
export const workflows = {
  list: apiUrl('workflows'),
  byId: (id: string) => apiUrl(`workflows/${encodeURIComponent(id)}`),
  withExpectedRevision: (id: string, revision: string) => apiUrl(`workflows/${encodeURIComponent(id)}?expectedRevision=${encodeURIComponent(revision)}`)
} as const;

// ── debug sessions ────────────────────────────────────────────────────────────
export const debug = {
  run: apiUrl('debug/run'),
  sessions: apiUrl('debug/sessions'),
  session: (id: string) => apiUrl(`debug/sessions/${encodeURIComponent(id)}`),
  continue: (id: string) => apiUrl(`debug/sessions/${encodeURIComponent(id)}/continue`),
  runNode: (id: string) => apiUrl(`debug/sessions/${encodeURIComponent(id)}/run-node`)
} as const;

// ── production ────────────────────────────────────────────────────────────────
export const production = {
  status: apiUrl('production/status'),
  config: apiUrl('production/config'),
  start: apiUrl('production/start'),
  stop: apiUrl('production/stop'),
  recover: apiUrl('production/recover'),
  pendingActions: apiUrl('production/device-actions/pending'),
  resolveAction: apiUrl('production/device-actions/resolve')
} as const;

// ── camera-sync ───────────────────────────────────────────────────────────────
export const cameraSync = {
  groups: apiUrl('camera-sync/groups'),
  group: (id: string) => apiUrl(`camera-sync/groups/${encodeURIComponent(id)}`),
  groupStatus: (id: string) => apiUrl(`camera-sync/groups/${encodeURIComponent(id)}/status`),
  groupStatistics: (id: string) => apiUrl(`camera-sync/groups/${encodeURIComponent(id)}/statistics?limit=100&minimumSamples=20`),
  groupCommissioningTests: (id: string) => apiUrl(`camera-sync/groups/${encodeURIComponent(id)}/commissioning-tests`),
  groupPtpDiagnostics: (id: string) => apiUrl(`camera-sync/groups/${encodeURIComponent(id)}/ptp-diagnostics?limit=100`),
  groupGigeDiagnostics: (id: string) => apiUrl(`camera-sync/groups/${encodeURIComponent(id)}/gige-network-diagnostics?limit=100`),
  groupGigeTests: (id: string) => apiUrl(`camera-sync/groups/${encodeURIComponent(id)}/gige-network-tests`),
  runs: (groupId: string) => apiUrl(`camera-sync/runs?groupId=${encodeURIComponent(groupId)}&limit=20`),
  commissioningTest: (testId: string) => apiUrl(`camera-sync/commissioning-tests/${encodeURIComponent(testId)}`),
  commissioningTestCancel: (testId: string) => apiUrl(`camera-sync/commissioning-tests/${encodeURIComponent(testId)}/cancel`),
  commissioningTestReport: (testId: string) => apiUrl(`camera-sync/commissioning-tests/${encodeURIComponent(testId)}/report`),
  commissioningTestReportHtml: (testId: string) => apiUrl(`camera-sync/commissioning-tests/${encodeURIComponent(testId)}/report.html`),
  gigeTestReport: (testId: string) => apiUrl(`camera-sync/gige-network-tests/${encodeURIComponent(testId)}/report`)
} as const;

// ── calibration ───────────────────────────────────────────────────────────────
export const calibration = {
  solve: apiUrl('calibration/solve'),
  captureGrid: apiUrl('calibration/capture-grid'),
  wizardSessions: apiUrl('calibration/wizard/sessions'),
  wizardSessionImport: apiUrl('calibration/wizard/sessions/import'),
  wizardSession: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}`),
  wizardSessionViews: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/views`),
  wizardSessionView: (sessionId: string, index: number) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/views/${index}`),
  wizardSessionViewEnabled: (sessionId: string, index: number) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/views/${index}/enabled`),
  wizardSessionViewFromImage: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/views/from-image`),
  wizardSessionHints: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/hints`),
  wizardSessionPreview: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/preview`),
  wizardSessionCalibrate: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/calibrate`),
  wizardSessionExport: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/export`),
  wizardSessionDisableOutliers: (sessionId: string) => apiUrl(`calibration/wizard/sessions/${encodeURIComponent(sessionId)}/disable-outliers`),
  wizardHandEyeSolve: apiUrl('calibration/wizard/handeye/solve'),
  wizardHandEye3DSolve: apiUrl('calibration/wizard/handeye3d/solve')
} as const;

// ── calibrations (assets) ────────────────────────────────────────────────────
export const calibrations = {
  list: apiUrl('calibrations'),
  byId: (id: string) => apiUrl(`calibrations/${encodeURIComponent(id)}`),
  versions: (id: string) => apiUrl(`calibrations/${encodeURIComponent(id)}/versions`),
  version: (id: string, version: string) => apiUrl(`calibrations/${encodeURIComponent(id)}/versions/${encodeURIComponent(version)}`),
  publish: (id: string, version: string) => apiUrl(`calibrations/${encodeURIComponent(id)}/versions/${encodeURIComponent(version)}/publish`),
  rollback: (id: string, version: string) => apiUrl(`calibrations/${encodeURIComponent(id)}/rollback/${version}`)
} as const;

// ── camera-intrinsics ────────────────────────────────────────────────────────
export const cameraIntrinsics = {
  list: apiUrl('camera-intrinsics'),
  byId: (id: string) => apiUrl(`camera-intrinsics/${encodeURIComponent(id)}`),
  version: (id: string, version: string) => apiUrl(`camera-intrinsics/${encodeURIComponent(id)}/versions/${encodeURIComponent(version)}`)
} as const;

// ── camera-feature-profiles ──────────────────────────────────────────────────
export const cameraFeatureProfiles = {
  capture: apiUrl('camera-feature-profiles/capture'),
  import: apiUrl('camera-feature-profiles/import')
} as const;

// ── devices ───────────────────────────────────────────────────────────────────
export const devices = {
  list: apiUrl('devices'),
  registerModbusTcp: apiUrl('devices/register/modbus-tcp'),
  registerS7: apiUrl('devices/register/s7'),
  byId: (id: string) => apiUrl(`devices/${encodeURIComponent(id)}`),
  action: (id: string, action: string) => apiUrl(`devices/${encodeURIComponent(id)}/${action}`),
  readAll: (id: string) => apiUrl(`devices/${encodeURIComponent(id)}/read-all`),
  settings: (id: string) => apiUrl(`devices/${encodeURIComponent(id)}/settings`),
  tag: (id: string, tagId: string) => apiUrl(`devices/${encodeURIComponent(id)}/tags/${encodeURIComponent(tagId)}`)
} as const;

// ── robots ────────────────────────────────────────────────────────────────────
export const robots = {
  list: apiUrl('robots'),
  byId: (id: string) => apiUrl(`robots/${encodeURIComponent(id)}`),
  action: (id: string, action: string) => apiUrl(`robots/${encodeURIComponent(id)}/${action}`),
  ack: (id: string, commandId: string) => apiUrl(`robots/${encodeURIComponent(id)}/ack/${commandId}`),
  settings: (id: string) => apiUrl(`robots/${encodeURIComponent(id)}/settings`),
  target: (id: string) => apiUrl(`robots/${encodeURIComponent(id)}/target`)
} as const;

// ── investigations ────────────────────────────────────────────────────────────
export const investigations = {
  trends: apiUrl('investigations/trends?days=30&top=8'),
  forTrace: (runId: string) => apiUrl(`investigations/for-trace/${encodeURIComponent(runId)}`),
  fromTrace: (runId: string) => apiUrl(`investigations/from-trace/${encodeURIComponent(runId)}`),
  byId: (id: string) => apiUrl(`investigations/${encodeURIComponent(id)}`),
  verificationEvidence: (id: string) => apiUrl(`investigations/${encodeURIComponent(id)}/verification-evidence`)
} as const;

// ── media ─────────────────────────────────────────────────────────────────────
export const media = {
  status: apiUrl('media/status'),
  collections: apiUrl('media/collections'),
  items: apiUrl('media/items'),
  import: apiUrl('media/import'),
  preview: (path: string) => apiUrl(`media/preview?path=${encodeURIComponent(path)}`),
  itemByPath: (path: string) => apiUrl(`media/items?path=${encodeURIComponent(path)}`)
} as const;

// ── storage ───────────────────────────────────────────────────────────────────
export const storage = {
  status: apiUrl('storage/status'),
  schema: apiUrl('storage/schema'),
  capacity: apiUrl('storage/capacity'),
  cleanup: apiUrl('storage/cleanup'),
  restore: apiUrl('storage/restore'),
  backups: apiUrl('storage/backups')
} as const;

// ── alarms ────────────────────────────────────────────────────────────────────
export const alarms = {
  list: apiUrl('alarms'),
  active: apiUrl('alarms?activeOnly=false')
} as const;

// ── model-packages ────────────────────────────────────────────────────────────
export const modelPackages = {
  list: apiUrl('model-packages'),
  preflight: apiUrl('model-packages/preflight'),
  import: apiUrl('model-packages/import')
} as const;

// ── modules ───────────────────────────────────────────────────────────────────
export const modules = {
  list: apiUrl('modules'),
  extract: apiUrl('modules/extract')
} as const;

// ── diagnostics ───────────────────────────────────────────────────────────────
export const diagnostics = {
  summary: apiUrl('diagnostics/summary'),
  events: apiUrl('diagnostics/events?take=250'),
  assets: apiUrl('diagnostics/assets')
} as const;

// ── hardware-provenance ──────────────────────────────────────────────────────
export const hardwareProvenance = {
  byKindAndId: (kind: string, id: string) => apiUrl(`hardware-provenance/${encodeURIComponent(kind)}/${encodeURIComponent(id)}`)
} as const;
