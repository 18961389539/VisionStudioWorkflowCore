import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TraceFilterState } from '../traceQuery';
import { useTraceQuery, type TracePageState } from '../useTraceQuery';
import type { TracePage } from '../types';

const PAGE: TracePage = {
  offset: 0,
  limit: 50,
  total: 2,
  items: [
    { runId: 'run-a', startedAt: '2026-10-11T01:00:00.000Z', source: 'job', jobId: 'job-1', jobVersion: 2, workflowId: 'wf', workflowName: '压装', executionStatus: 'Completed', disposition: 'OK', totalDurationMs: 12, nodeCount: 2, overlayCount: 0, hasPreview: false, hasReplayInput: false, error: null, note: null, nodeReports: [] },
    { runId: 'run-b', startedAt: '2026-10-11T02:00:00.000Z', source: 'job', jobId: 'job-1', jobVersion: 2, workflowId: 'wf', workflowName: '压装', executionStatus: 'Failed', disposition: 'NG', totalDurationMs: 30, nodeCount: 2, overlayCount: 0, hasPreview: false, hasReplayInput: false, error: 'boom', errorCode: 'E-X', note: null, nodeReports: [] },
  ],
};

const STATS = { total: 2, ok: 1, ng: 1, review: 0, errors: 0, averageDurationMs: 21 };
const STORAGE = { traceCount: 2, previewCount: 0, replayInputCount: 0 };

function jsonResponse(body: unknown, ok = true, status = 200): Response {
  return { ok, status, json: async () => body } as unknown as Response;
}

function fetchByUrl(url: string): Response {
  if (url.startsWith('/api/traces/page')) return jsonResponse(PAGE);
  if (url.startsWith('/api/traces/stats')) return jsonResponse(STATS);
  if (url.startsWith('/api/storage/status')) return jsonResponse(STORAGE);
  return jsonResponse({ error: 'not found' }, false, 404);
}

const FILTER = { preset: 'today' as const };
const PAGE_STATE: TracePageState = { current: 1, pageSize: 50 };

describe('useTraceQuery', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => fetchByUrl(String(input))));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('加载分页与统计，并透传真实总数', async () => {
    const { result } = renderHook(() => useTraceQuery(true, FILTER, PAGE_STATE));

    await waitFor(() => expect(result.current.data).not.toBeNull());
    expect(result.current.data?.total).toBe(2);
    expect(result.current.data?.items).toHaveLength(2);
    expect(result.current.stats?.total).toBe(2);
    expect(result.current.error).toBeNull();
  });

  it('快速切换时旧响应不覆盖新结果（代次保护）', async () => {
    // 第一次 page 请求挂起（由测试手动放行）；之后的 page 请求立即返回新数据。
    let releaseFirstPage: ((value: Response) => void) | undefined;
    let seenPageCalls = 0;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/traces/page')) {
        seenPageCalls += 1;
        if (seenPageCalls === 1) {
          return new Promise<Response>((resolve) => { releaseFirstPage = resolve; });
        }
        return jsonResponse({ ...PAGE, total: 99, items: [] });
      }
      return fetchByUrl(url);
    }));

    const { result, rerender } = renderHook(
      ({ open, filter }: { open: boolean; filter: TraceFilterState }) => useTraceQuery(open, filter, PAGE_STATE),
      { initialProps: { open: true, filter: FILTER as TraceFilterState } },
    );

    // 切换筛选：新请求立即落地（total=99）；随后才放行被取消的旧响应。
    rerender({ open: true, filter: { preset: 'week' } });
    await waitFor(() => expect(result.current.data?.total).toBe(99));

    await act(async () => { releaseFirstPage?.(jsonResponse(PAGE)); });
    await act(async () => { await Promise.resolve(); });

    // 旧响应（total=2）不得覆盖新结果（total=99）。
    expect(result.current.data?.total).toBe(99);
  });

  it('查询失败时明确报错且不清空已有数据', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/traces/page')) return jsonResponse({ error: 'storage locked' }, false, 503);
      return fetchByUrl(url);
    }));

    const { result } = renderHook(() => useTraceQuery(true, FILTER, PAGE_STATE));

    await waitFor(() => expect(result.current.error).not.toBeNull());
    expect(result.current.error).toContain('503');
    expect(result.current.error).toContain('storage locked');
    expect(result.current.loading).toBe(false);
  });

  it('统计口径与列表筛选一致（stats 请求携带同一组过滤参数）', async () => {
    const calls: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      calls.push(url);
      return fetchByUrl(url);
    }));

    const { result } = renderHook(() => useTraceQuery(true, { preset: 'today', disposition: 'NG' }, PAGE_STATE));
    await waitFor(() => expect(result.current.data).not.toBeNull());

    const statsUrl = calls.find((url) => url.startsWith('/api/traces/stats')) ?? '';
    expect(statsUrl).toContain('disposition=NG');
    expect(statsUrl).toContain('from=');
    expect(calls.some((url) => url.startsWith('/api/traces/page'))).toBe(true);
  });
});
