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
  pendingActions: apiUrl('production/device-actions/pending')
} as const;
