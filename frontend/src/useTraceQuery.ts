import { useCallback, useEffect, useRef, useState } from 'react';
import type { TracePage, TraceStats, TraceStorageStatus } from './types';
import { buildStatsParams, buildTraceParams, type TraceFilterState } from './traceQuery';

const REQUEST_TIMEOUT_MS = 20_000;

export type TracePageState = { current: number; pageSize: number };

export type TraceQueryResult = {
  data: TracePage | null;
  stats: TraceStats | null;
  storage: TraceStorageStatus | null;
  error: string | null;
  loading: boolean;
  /** 本轮数据落地时间：统计条上的“更新于”。 */
  loadedAt: Date | null;
  reload: () => void;
};

function describeFailure(response: Response): string {
  return `查询失败（HTTP ${response.status}）`;
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException
    ? error.name === 'AbortError' || error.name === 'TimeoutError'
    : error instanceof Error && (error.name === 'AbortError' || error.name === 'TimeoutError');
}

async function safeErrorDetail(response: Response): Promise<string> {
  try {
    const data = await response.json();
    return typeof data?.error === 'string' ? `：${data.error}` : '';
  } catch {
    return '';
  }
}

/**
 * 追溯列表数据源：代次保护 + 请求取消。
 * 快速切换筛选/翻页时旧请求被 abort；每个响应先核对代次再落状态 ——
 * 旧响应永远覆盖不了新结果（审查项“快速切换无旧数据覆盖”）。
 */
export function useTraceQuery(open: boolean, filter: TraceFilterState, page: TracePageState): TraceQueryResult {
  const [data, setData] = useState<TracePage | null>(null);
  const [stats, setStats] = useState<TraceStats | null>(null);
  const [storage, setStorage] = useState<TraceStorageStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadedAt, setLoadedAt] = useState<Date | null>(null);
  const [reloadTick, setReloadTick] = useState(0);
  const generationRef = useRef(0);
  const abortRef = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    const generation = ++generationRef.current;
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    setLoading(true);
    setError(null);
    try {
      const offset = Math.max(0, page.current - 1) * page.pageSize;
      const [listResponse, statsResponse, storageResponse] = await Promise.all([
        fetch(`/api/traces/page?${buildTraceParams(filter, offset, page.pageSize)}`, { signal: controller.signal }),
        fetch(`/api/traces/stats?${buildStatsParams(filter)}`, { signal: controller.signal }),
        fetch('/api/storage/status', { signal: controller.signal }),
      ]);
      if (generation !== generationRef.current) return;
      if (!listResponse.ok) {
        setError(`${describeFailure(listResponse)}${await safeErrorDetail(listResponse)}`);
        return;
      }

      const pageData: TracePage = await listResponse.json();
      if (statsResponse.ok) setStats(await statsResponse.json());
      if (storageResponse.ok) setStorage(await storageResponse.json());
      setData(pageData);
      setLoadedAt(new Date());
    } catch (error) {
      if (generation !== generationRef.current || isAbortError(error)) return;
      setError(error instanceof Error ? `查询失败：${error.message}` : '查询失败：未知错误');
    } finally {
      clearTimeout(timeout);
      if (generation === generationRef.current) setLoading(false);
    }
  }, [filter, page.current, page.pageSize]);

  useEffect(() => {
    if (open) void load();
    // open 关闭时让在途请求作废，避免抽屉重开后旧响应复活。
    if (!open) {
      generationRef.current += 1;
      abortRef.current?.abort();
    }
  }, [open, load, reloadTick]);

  const reload = useCallback(() => setReloadTick((tick) => tick + 1), []);

  return { data, stats, storage, error, loading, loadedAt, reload };
}
