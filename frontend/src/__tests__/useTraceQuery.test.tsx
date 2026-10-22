import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TraceFilterState } from '../traceQuery';
import { useTraceQuery, type TracePageState } from '../useTraceQuery';
import type { TracePage } from '../types';
import * as traceQuery from '../traceQuery';

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
const HOUR_FILTER: TraceFilterState = { preset: 'hour' };

describe('useTraceQuery', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => fetchByUrl(String(input))));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
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

  it('旧列表正文在新筛选完成后才解码，也不能覆盖新结果', async () => {
    let releaseBody!: (value: TracePage) => void;
    let calls = 0;
    const oldBody = vi.fn(() => new Promise<TracePage>((resolve) => { releaseBody = resolve; }));
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).startsWith('/api/traces/page') && ++calls === 1) {
        return { ok: true, json: oldBody } as unknown as Response;
      }
      if (String(input).startsWith('/api/traces/page')) return jsonResponse({ ...PAGE, total: 99 });
      return fetchByUrl(String(input));
    }));
    const { result, rerender } = renderHook(
      ({ filter }) => useTraceQuery(true, filter, PAGE_STATE),
      { initialProps: { filter: FILTER as TraceFilterState } },
    );
    await waitFor(() => expect(oldBody).toHaveBeenCalled());
    rerender({ filter: { preset: 'week' } });
    await waitFor(() => expect(result.current.data?.total).toBe(99));
    const loadedAt = result.current.loadedAt;
    await act(async () => { releaseBody(PAGE); });
    expect(result.current.data?.total).toBe(99);
    expect(result.current.loadedAt).toBe(loadedAt);
    expect(result.current.rangeDescription).toContain('最近 7 天');
  });

  it('旧统计正文和错误正文不能污染新查询', async () => {
    let releaseStats!: (value: unknown) => void;
    let releaseError!: (value: unknown) => void;
    let first = true;
    const statsBody = vi.fn(() => new Promise((resolve) => { releaseStats = resolve; }));
    const errorBody = vi.fn(() => new Promise((resolve) => { releaseError = resolve; }));
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (first && url.startsWith('/api/traces/page')) return { ok: false, status: 503, json: errorBody } as unknown as Response;
      if (first && url.startsWith('/api/traces/stats')) return { ok: true, json: statsBody } as unknown as Response;
      return fetchByUrl(url);
    }));
    const { result, rerender } = renderHook(
      ({ filter }) => useTraceQuery(true, filter, PAGE_STATE),
      { initialProps: { filter: FILTER as TraceFilterState } },
    );
    await waitFor(() => { expect(statsBody).toHaveBeenCalled(); expect(errorBody).toHaveBeenCalled(); });
    first = false;
    rerender({ filter: { preset: 'week' } });
    await waitFor(() => expect(result.current.stats?.total).toBe(2));
    await act(async () => { releaseStats({ ...STATS, total: 900 }); releaseError({ error: 'old error' }); });
    expect(result.current.stats?.total).toBe(2);
    expect(result.current.error).toBeNull();
  });

  it('关闭或卸载面板会取消请求并清理定时器', async () => {
    vi.useFakeTimers();
    const signals: AbortSignal[] = [];
    vi.stubGlobal('fetch', vi.fn((_input: RequestInfo | URL, options?: RequestInit) => {
      signals.push(options!.signal as AbortSignal);
      return new Promise<Response>(() => {});
    }));
    const { result, rerender, unmount } = renderHook(
      ({ open }) => useTraceQuery(open, FILTER, PAGE_STATE), { initialProps: { open: true } },
    );
    expect(vi.getTimerCount()).toBe(1);
    rerender({ open: false });
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
    expect(vi.getTimerCount()).toBe(0);
    rerender({ open: true });
    expect(signals.at(-1)?.aborted).toBe(false);
    unmount();
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('超时明确报错，重试成功后清除错误', async () => {
    vi.useFakeTimers();
    let pending = true;
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, options?: RequestInit) => {
      if (!pending) return Promise.resolve(fetchByUrl(String(input)));
      return new Promise<Response>((_resolve, reject) => {
        options?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      });
    }));
    const { result } = renderHook(() => useTraceQuery(true, FILTER, PAGE_STATE));
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(result.current.error).toContain('超时');
    expect(result.current.loading).toBe(false);
    expect(result.current.warnings).toHaveLength(2);
    pending = false;
    await act(async () => { result.current.reload(); });
    expect(result.current.data?.total).toBe(2);
    expect(result.current.error).toBeNull();
    expect(result.current.warnings).toEqual([]);
    expect(result.current.stale).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('统计或存储接口的网络故障不阻断列表', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (!String(input).startsWith('/api/traces/page')) throw new TypeError('offline');
      return jsonResponse(PAGE);
    }));
    const { result } = renderHook(() => useTraceQuery(true, FILTER, PAGE_STATE));
    await waitFor(() => expect(result.current.data?.total).toBe(2));
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
    expect(result.current.stats).toBeNull();
    expect(result.current.storage).toBeNull();
    expect(result.current.warnings).toEqual(expect.arrayContaining([
      expect.stringContaining('统计查询失败'), expect.stringContaining('存储状态查询失败'),
    ]));
  });

  it('统计挂起时列表先显示，超时后单独提示统计不可用', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, options?: RequestInit) => {
      if (!String(input).startsWith('/api/traces/stats')) return Promise.resolve(fetchByUrl(String(input)));
      return new Promise<Response>((_resolve, reject) => {
        options?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      });
    }));
    const { result } = renderHook(() => useTraceQuery(true, FILTER, PAGE_STATE));
    await act(async () => { await Promise.resolve(); });
    expect(result.current.data?.total).toBe(2);
    expect(result.current.loading).toBe(false);
    expect(result.current.stats).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(result.current.error).toBeNull();
    expect(result.current.warnings).toEqual([expect.stringContaining('统计查询超时')]);
  });

  it('HTTP 或 JSON 统计错误不会复用上一次筛选的统计', async () => {
    let fail = false;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (fail && url.startsWith('/api/traces/stats')) return jsonResponse({ error: 'stats unavailable' }, false, 503);
      if (fail && url.startsWith('/api/storage/status')) return { ok: true, json: async () => { throw new SyntaxError('bad JSON'); } } as unknown as Response;
      return fetchByUrl(url);
    }));
    const { result, rerender } = renderHook(
      ({ filter }) => useTraceQuery(true, filter, PAGE_STATE),
      { initialProps: { filter: FILTER as TraceFilterState } },
    );
    await waitFor(() => expect(result.current.stats?.total).toBe(2));
    fail = true;
    rerender({ filter: { preset: 'week' } });
    await waitFor(() => expect(result.current.warnings).toHaveLength(2));
    expect(result.current.stats).toBeNull();
    expect(result.current.storage).toBeNull();
    expect(result.current.stale).toBe(false);
    expect(result.current.warnings.join()).toContain('503');
  });

  it('新查询失败保留旧列表及原范围，并明确标为过期', async () => {
    let fail = false;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (fail && String(input).startsWith('/api/traces/page')) return jsonResponse({}, false, 500);
      return fetchByUrl(String(input));
    }));
    const { result, rerender } = renderHook(
      ({ filter }) => useTraceQuery(true, filter, PAGE_STATE),
      { initialProps: { filter: FILTER as TraceFilterState } },
    );
    await waitFor(() => expect(result.current.data).toBe(PAGE));
    const description = result.current.rangeDescription;
    const loadedAt = result.current.loadedAt;
    fail = true;
    rerender({ filter: { preset: 'week' } });
    await waitFor(() => expect(result.current.error).toContain('500'));
    expect(result.current.data).toBe(PAGE);
    expect(result.current.rangeDescription).toBe(description);
    expect(result.current.loadedAt).toBe(loadedAt);
    expect(result.current.stale).toBe(true);
    expect(result.current.stats).toBeNull();
  });

  it('列表与统计共用同一个时间边界，且说明不随渲染时钟移动', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-20T12:00:00Z'));
    const buildList = traceQuery.buildTraceParams;
    vi.spyOn(traceQuery, 'buildTraceParams').mockImplementation((...args) => {
      const params = buildList(...args);
      vi.setSystemTime(new Date('2026-10-21T12:00:00Z'));
      return params;
    });
    const { result, rerender } = renderHook(() => useTraceQuery(true, HOUR_FILTER, PAGE_STATE));
    await act(async () => { await Promise.resolve(); });
    const calls = vi.mocked(fetch).mock.calls.map(([input]) => new URL(String(input), 'http://localhost'));
    const list = calls.find((url) => url.pathname === '/api/traces/page')!;
    const stats = calls.find((url) => url.pathname === '/api/traces/stats')!;
    expect(list.searchParams.get('from')).toBe('2026-10-20T11:00:00.000Z');
    expect(stats.searchParams.get('from')).toBe(list.searchParams.get('from'));
    const description = result.current.rangeDescription;
    rerender();
    expect(result.current.rangeDescription).toBe(description);
  });

  it('拒绝倒置的自定义时间窗，不向后端发送无效查询', () => {
    const { result } = renderHook(() => useTraceQuery(true, {
      preset: 'custom', customFrom: '2026-10-21T00:00:00Z', customTo: '2026-10-20T00:00:00Z',
    }, PAGE_STATE));
    expect(fetch).not.toHaveBeenCalled();
    expect(result.current.error).toContain('开始时间');
    expect(result.current.loading).toBe(false);
  });

  it('等值筛选对象和分页对象不会重复触发查询', async () => {
    const { result, rerender } = renderHook(() => useTraceQuery(true, { preset: 'today' }, { current: 1, pageSize: 50 }));
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(fetch).toHaveBeenCalledTimes(3);
    rerender();
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('关闭面板后才解码的正文不会更新列表或状态', async () => {
    let releaseBody!: (value: unknown) => void;
    const body = vi.fn(() => new Promise((resolve) => { releaseBody = resolve; }));
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => String(input).startsWith('/api/traces/page')
      ? { ok: true, json: body } as unknown as Response : fetchByUrl(String(input))));
    const { result, rerender } = renderHook(
      ({ open }) => useTraceQuery(open, FILTER, PAGE_STATE), { initialProps: { open: true } },
    );
    await waitFor(() => expect(body).toHaveBeenCalled());
    rerender({ open: false });
    await act(async () => { releaseBody(PAGE); });
    expect(result.current.data).toBeNull();
    expect(result.current.loadedAt).toBeNull();
    expect(result.current.loading).toBe(false);
    expect(result.current.error).toBeNull();
  });
});
