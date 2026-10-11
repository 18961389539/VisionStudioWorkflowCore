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

const DAY_MS = 86_400_000;

function startOfDay(value: Date): Date {
  const copy = new Date(value);
  copy.setHours(0, 0, 0, 0);
  return copy;
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
        from: startOfDay(new Date(now.getTime() - DAY_MS)).toISOString(),
        to: new Date(startOfDay(now).getTime() - 1).toISOString(),
      };
    case 'week':
      return { from: startOfDay(new Date(now.getTime() - 6 * DAY_MS)).toISOString() };
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

/** 列表查询参数：分页 + 全部筛选。 */
export function buildTraceParams(filter: TraceFilterState, offset: number, limit: number): URLSearchParams {
  const params = new URLSearchParams();
  params.set('offset', String(Math.max(0, offset)));
  params.set('limit', String(Math.max(1, limit)));
  applyFilterParams(params, filter);
  return params;
}

/** 统计参数：与列表同一组筛选（不含分页）—— 数量与统计的口径保证。 */
export function buildStatsParams(filter: TraceFilterState): URLSearchParams {
  const params = new URLSearchParams();
  applyFilterParams(params, filter);
  return params;
}

function applyFilterParams(params: URLSearchParams, filter: TraceFilterState): void {
  const range = effectiveRange(filter);
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
