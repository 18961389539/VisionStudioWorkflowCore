import type { TracePage, TraceStats, TraceStorageStatus } from './types';

export type TraceTimePreset = 'hour' | 'today' | 'yesterday' | 'week' | 'custom';

export type TraceFilterState = {
  preset: TraceTimePreset;
  customFrom?: string;
  customTo?: string;
  jobId?: string;
  jobVersion?: number | null;
  workflow?: string;
  executionStatus?: string;
  disposition?: string;
  source?: string;
  runIdPrefix?: string;
  errorCode?: string;
};

export const TRACE_TIME_PRESETS: { key: TraceTimePreset; label: string }[] = [
  { key: 'hour', label: '最近一小时' },
  { key: 'today', label: '今天' },
  { key: 'yesterday', label: '昨天' },
  { key: 'week', label: '最近 7 天' },
  { key: 'custom', label: '自定义' },
];

export const EXECUTION_STATUS_OPTIONS = ['Running', 'Completed', 'Failed', 'Canceled', 'Unknown'];
export const DISPOSITION_OPTIONS = ['OK', 'NG', 'REVIEW', 'ERROR'];

function startOfDay(value: Date): Date {
  const copy = new Date(value);
  copy.setHours(0, 0, 0, 0);
  return copy;
}

function daysAgo(value: Date, days: number): Date {
  const copy = new Date(value);
  copy.setDate(copy.getDate() - days);
  return startOfDay(copy);
}

/** datetime-local 必须显示本地日历字段，不能截取 UTC ISO 字符串。 */
export function toLocalDateTimeInput(iso?: string): string {
  if (!iso) return '';
  const value = new Date(iso);
  if (!Number.isFinite(value.getTime())) return '';
  const pad = (part: number) => String(part).padStart(2, '0');
  return `${String(value.getFullYear()).padStart(4, '0')}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}T${pad(value.getHours())}:${pad(value.getMinutes())}`;
}

export function localDateTimeToIso(value: string): string | undefined {
  if (!value) return undefined;
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toISOString() : undefined;
}

/** 单侧时间窗合法；按运行 ID 直查时自定义时间不参与查询。 */
export function validateTraceFilter(filter: TraceFilterState): string | null {
  if (filter.preset !== 'custom' || filter.runIdPrefix?.trim()) return null;
  const { customFrom: from, customTo: to } = filter;
  if ((from && !Number.isFinite(Date.parse(from))) || (to && !Number.isFinite(Date.parse(to)))) {
    return '请输入有效的自定义时间';
  }
  if (from && to && Date.parse(from) > Date.parse(to)) return '开始时间不能晚于结束时间';
  return null;
}

/** 时间预设 → UTC ISO 起止（后端 started_at 以 UTC ISO "O" 落库，字符串比较即时间比较）。 */
export function presetRange(filter: TraceFilterState, now = new Date()): { from?: string; to?: string } {
  if (filter.preset === 'custom') {
    return { from: filter.customFrom, to: filter.customTo };
  }

  switch (filter.preset) {
    case 'hour':
      return { from: new Date(now.getTime() - 3_600_000).toISOString() };
    case 'today':
      return { from: startOfDay(now).toISOString() };
    case 'yesterday':
      return {
        from: daysAgo(now, 1).toISOString(),
        to: new Date(startOfDay(now).getTime() - 1).toISOString(),
      };
    case 'week':
      return { from: daysAgo(now, 6).toISOString() };
  }
}

/**
 * 生效的时间窗：输入运行 ID 前缀（含完整 ID）时直接查找，
 * 不受默认时间窗口约束 —— 全历史里按 ID 定位一条记录不该被“今天”挡住。
 */
export function effectiveRange(filter: TraceFilterState, now = new Date()): { from?: string; to?: string } {
  if (filter.runIdPrefix && filter.runIdPrefix.trim()) return {};
  return presetRange(filter, now);
}

function put(params: URLSearchParams, key: string, value: string | number | null | undefined): void {
  if (value === null || value === undefined) return;
  const text = String(value).trim();
  if (text) params.set(key, text);
}

/** 列表查询参数：分页 + 全部筛选。now 可注入以获得确定性结果（测试 / 跨日边界）；生产调用走默认当前时间。 */
export function buildTraceParams(
  filter: TraceFilterState, offset: number, limit: number, now = new Date(),
  /**
   * 列表是否携带逐节点报告（2026-10-26 性能）。默认 false：
   * 一页 50 条记录各带几十份报告是列表响应体的大头，而界面只渲染选中那一条，
   * 选中后再按 runId 取详情。传 true 可恢复旧行为。
   */
  includeReports = false
): URLSearchParams {
  const params = new URLSearchParams();
  params.set('offset', String(Math.max(0, offset)));
  params.set('limit', String(Math.max(1, limit)));
  applyFilterParams(params, filter, now);
  params.set('includeReports', includeReports ? 'true' : 'false');
  return params;
}

/** 统计参数：与列表同一组筛选（不含分页）—— 数量与统计的口径保证。now 语义同 buildTraceParams。 */
export function buildStatsParams(filter: TraceFilterState, now = new Date()): URLSearchParams {
  const params = new URLSearchParams();
  applyFilterParams(params, filter, now);
  // 空筛选代表全部历史；显式区分旧版无参数请求的“近 7 天”默认口径。
  if (params.size === 0) params.set('allTime', 'true');
  return params;
}

function applyFilterParams(params: URLSearchParams, filter: TraceFilterState, now: Date): void {
  const range = effectiveRange(filter, now);
  put(params, 'jobId', filter.jobId);
  put(params, 'jobVersion', filter.jobVersion ?? undefined);
  put(params, 'workflow', filter.workflow);
  put(params, 'executionStatus', filter.executionStatus);
  put(params, 'disposition', filter.disposition);
  put(params, 'source', filter.source);
  put(params, 'runIdPrefix', filter.runIdPrefix);
  put(params, 'errorCode', filter.errorCode);
  if (range.from) params.set('from', range.from);
  if (range.to) params.set('to', range.to);
}

/** 统计范围的人类可读描述（与列表筛选一致，展示在统计条上）。 */
export function describeRange(filter: TraceFilterState, now = new Date()): string {
  const prefix = filter.runIdPrefix?.trim();
  if (prefix) return `运行 ID 前缀 “${prefix}”（不限时间）`;
  const range = presetRange(filter, now);
  const format = (iso?: string) => (iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '不限');
  if (filter.preset === 'custom') return `${format(range.from)} ~ ${format(range.to)}`;
  const label = TRACE_TIME_PRESETS.find((item) => item.key === filter.preset)?.label ?? filter.preset;
  return `${label}（${format(range.from)} ~ ${format(range.to)}）`;
}

export type { TracePage, TraceStats, TraceStorageStatus };
