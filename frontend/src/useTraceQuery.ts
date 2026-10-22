import { useCallback, useEffect, useMemo, useState } from 'react';
import type { TracePage, TraceStats, TraceStorageStatus } from './types';
import { buildStatsParams, buildTraceParams, describeRange, validateTraceFilter, type TraceFilterState } from './traceQuery';

const REQUEST_TIMEOUT_MS = 20_000;

export type TracePageState = { current: number; pageSize: number };

export type TraceQueryResult = {
  data: TracePage | null;
  stats: TraceStats | null;
  storage: TraceStorageStatus | null;
  error: string | null;
  warnings: string[];
  loading: boolean;
  /** 保留的列表属于上一次查询，不能冒充当前筛选结果。 */
  stale: boolean;
  rangeDescription: string | null;
  /** 本轮数据落地时间：统计条上的“更新于”。 */
  loadedAt: Date | null;
  reload: () => void;
};

async function responseData<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal });
  if (!response.ok) {
    let detail = '';
    try {
      const data = await response.json();
      if (typeof data?.error === 'string') detail = `：${data.error}`;
    } catch { /* 非 JSON 错误页仍保留 HTTP 状态码。 */ }
    throw new Error(`HTTP ${response.status}${detail}`);
  }
  return response.json();
}

/** 列表、统计与存储独立加载；所有正文读取结束后仍须核对请求生命周期。 */
export function useTraceQuery(open: boolean, filter: TraceFilterState, page: TracePageState): TraceQueryResult {
  const [data, setData] = useState<TracePage | null>(null);
  const [stats, setStats] = useState<TraceStats | null>(null);
  const [storage, setStorage] = useState<TraceStorageStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [resourceErrors, setResourceErrors] = useState<{ stats?: string; storage?: string }>({});
  const [loading, setLoading] = useState(false);
  const [stale, setStale] = useState(false);
  const [rangeDescription, setRangeDescription] = useState<string | null>(null);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [reloadTick, setReloadTick] = useState(0);
  // 调用方传入等值的新对象时，不应触发重复请求和状态更新循环。
  const queryFilter = useMemo(() => ({ ...filter }), [
    filter.preset, filter.customFrom, filter.customTo, filter.jobId, filter.jobVersion,
    filter.workflow, filter.executionStatus, filter.disposition, filter.source, filter.runIdPrefix, filter.errorCode,
  ]);

  useEffect(() => {
    const filter = queryFilter;
    if (!open) {
      setLoading(false);
      return;
    }

    setStale(true);
    setStats(null);
    setStorage(null);
    setResourceErrors({});
    const validationError = validateTraceFilter(filter);
    setError(validationError);
    if (validationError) {
      setLoading(false);
      return;
    }

    let active = true;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    // 同次查询共用时钟，避免滚动时间窗漂移或跨午夜时列表与统计日期不同。
    const queryTime = new Date();
    const description = describeRange(filter, queryTime);
    const offset = Math.max(0, page.current - 1) * page.pageSize;
    setLoading(true);

    async function load<T>(url: string, label: string, onSuccess: (value: T) => void, onError: (message: string) => void) {
      try {
        const value = await responseData<T>(url, controller.signal);
        if (active && !controller.signal.aborted) onSuccess(value);
      } catch (reason) {
        if (!active) return;
        onError(controller.signal.aborted
          ? `${label}超时（20 秒），请重试`
          : `${label}失败：${reason instanceof Error ? reason.message : '未知错误'}`);
      }
    }

    const listRequest = load<TracePage>(
      `/api/traces/page?${buildTraceParams(filter, offset, page.pageSize, queryTime)}`,
      '查询',
      (value) => {
        setData(value);
        setLoadedAt(new Date());
        setRangeDescription(description);
        setStale(false);
      },
      setError,
    ).finally(() => { if (active) setLoading(false); });
    const statsRequest = load<TraceStats>(
      `/api/traces/stats?${buildStatsParams(filter, queryTime)}`,
      '统计查询', setStats,
      (message) => setResourceErrors((current) => ({ ...current, stats: message })),
    );
    const storageRequest = load<TraceStorageStatus>(
      '/api/storage/status', '存储状态查询', setStorage,
      (message) => setResourceErrors((current) => ({ ...current, storage: message })),
    );
    void Promise.all([listRequest, statsRequest, storageRequest]).finally(() => clearTimeout(timeout));

    return () => {
      active = false;
      controller.abort();
      clearTimeout(timeout);
    };
  }, [open, queryFilter, page.current, page.pageSize, reloadTick]);

  const reload = useCallback(() => setReloadTick((tick) => tick + 1), []);

  // 列表保留旧数据期间，不混入新筛选的统计或存储快照。
  return {
    data, stats: stale ? null : stats, storage: stale ? null : storage,
    error, warnings: Object.values(resourceErrors).filter((value): value is string => Boolean(value)),
    loading, stale, rangeDescription, loadedAt, reload,
  };
}
