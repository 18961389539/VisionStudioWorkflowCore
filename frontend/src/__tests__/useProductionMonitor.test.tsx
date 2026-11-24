import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useProductionMonitor } from '../useProductionMonitor';
import { defaultProductionConfig as defaults } from '../productionConfig';

const { useMonitorSnapshot } = vi.hoisted(() => ({ useMonitorSnapshot: vi.fn() }));
vi.mock('../useMonitorStream', () => ({ useMonitorSnapshot }));

const status = { state: 'Stopped', productionLocked: false, cycleCount: 1, okCount: 1, ngCount: 0,
  errorCount: 0, consecutiveFailures: 0, watchdogTrips: 0, lastDurationMs: 0 };
const config = { ...defaults, jobId: 'A' };
const pending = { runId: 'run-1', reason: 'unknown device action', deviceIds: ['device'] };
const ok = (data: unknown, total = 0) => ({
  ok: true, status: 200, headers: new Headers({ 'X-Total-Count': String(total), ETag: '"config-test"' }), json: async () => data
}) as Response;
const emptySummary = { activeCount: 0, criticalCount: 0, latestActiveMessage: null, pendingCount: 0 };
const critical = { id: 'critical', active: true, severity: 'Critical', message: 'old critical', raisedAt: '2026-10-26T08:00:00Z' };
const criticalSummary = { activeCount: 1, criticalCount: 1, latestActiveMessage: critical.message, pendingCount: 1 };
const failure = (detail = 'not available') => ({ ok: false, status: 503, json: async () => ({ detail, correlationId: 'test-trace' }) }) as Response;
const empty = () => ({ ok: true, status: 204 }) as Response;
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
const handler = async (url: string) => url.endsWith('/status') ? ok(status) : url.endsWith('/config') ? ok(config)
  : url.endsWith('/pending') ? empty() : url.endsWith('/alarms/summary') ? ok(emptySummary) : ok([]);
const flush = async () => { await act(async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); }); };
const tick = async (ms = 1000) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };
const mount = (open = true) => renderHook(({ enabled }) => useProductionMonitor(enabled), { initialProps: { enabled: open } });

beforeEach(() => { useMonitorSnapshot.mockReset(); vi.useFakeTimers(); vi.stubGlobal('fetch', vi.fn(handler)); });
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('生产数据独立轮询', () => {
  it('配置读取慢于多个轮询周期时，状态、告警和待核对动作仍立即可见', async () => {
    const slowConfig = deferred<Response>();
    const fetchMock = vi.fn((url: string) => url.endsWith('/config') ? slowConfig.promise
      : url.endsWith('/pending') ? Promise.resolve(ok(pending)) : handler(url));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await flush();
    expect(result.current.statusFresh).toBe(true);
    expect(result.current.configReady).toBe(false);
    expect(result.current.pending?.runId).toBe('run-1');
    await tick(2100);
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('/config'))).toHaveLength(1);
    // 2026-11-13：状态改由 /hubs/monitor 推送，轮询兜底周期由 1 s 放宽到 10 s。
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('/status'))).toHaveLength(1);
    await tick(8000);
    // 配置首请求在 5 s 超时被放弃，10 s 兜底周期再重试一次——慢配置不会拖住其它资源。
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('/config'))).toHaveLength(2);
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('/status'))).toHaveLength(2);
    await act(async () => { slowConfig.resolve(ok(config)); });
    expect(result.current.configReady).toBe(true);
  });

  it('配置正文损坏只降级配置，待核对动作不再被前一个解析错误阻断', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/config')
      ? { ...ok({}), json: async () => { throw new SyntaxError('invalid config body'); } }
      : url.endsWith('/pending') ? ok(pending) : handler(url)));
    const { result } = mount();
    await flush();
    expect(result.current.errors.config).toContain('invalid config body');
    expect(result.current.pending).toEqual(pending);
    expect(result.current.statusFresh).toBe(true);
  });

  it('主状态失败时仍可读取待核对动作，不能把主状态伪装为新鲜', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/status') ? failure('status offline')
      : url.endsWith('/pending') ? ok(pending) : handler(url)));
    const { result } = mount();
    await flush();
    expect(result.current.statusFresh).toBe(false);
    expect(result.current.lastUpdatedAt).toBeUndefined();
    expect(result.current.errors.status).toContain('status offline');
    expect(result.current.pending).toEqual(pending);
    expect(result.current.configReady).toBe(true);
  });

  it('待核对清单失败保留旧记录并明确标为不可用，204 才清除旧记录', async () => {
    let next = ok(pending);
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/pending') ? next : handler(url)));
    const { result } = mount();
    await flush();
    next = failure('permission lost');
    await tick(10_000);
    expect(result.current.pending).toEqual(pending);
    expect(result.current.errors.pending).toMatch(/permission lost.*test-trace/);
    next = empty();
    await tick(10_000);
    expect(result.current.pending).toBeUndefined();
    expect(result.current.errors.pending).toBeUndefined();
  });

  it('每个资源的超时覆盖正文阶段，迟到正文不能重新标成新鲜', async () => {
    const old = deferred<unknown>();
    let signal!: AbortSignal;
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/status')) {
        calls++;
        if (calls === 1) { signal = init!.signal as AbortSignal; return { ...ok({}), json: () => old.promise }; }
        return failure('still offline');
      }
      return handler(url);
    }));
    const { result } = mount();
    await flush();
    await tick(5000);
    expect(signal.aborted).toBe(true);
    // 超时清掉在途请求后，下一次 10 s 兜底轮询才会重试。
    expect(calls).toBe(1);
    await tick(5000);
    expect(calls).toBe(2);
    await act(async () => { old.resolve(status); });
    expect(result.current.statusFresh).toBe(false);
    expect(result.current.status).toBeUndefined();
    expect(result.current.lastUpdatedAt).toBeUndefined();
  });

  it('关闭重开立即发出新请求，旧 finally 不能解除新请求的互斥', async () => {
    const oldBody = deferred<unknown>();
    const newRequest = deferred<Response>();
    let oldSignal!: AbortSignal;
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (url.endsWith('/status')) {
        if (++calls === 1) { oldSignal = init!.signal as AbortSignal; return Promise.resolve({ ...ok({}), json: () => oldBody.promise }); }
        return newRequest.promise;
      }
      return handler(url);
    }));
    const { result, rerender } = mount();
    await flush();
    rerender({ enabled: false });
    rerender({ enabled: true });
    expect(oldSignal.aborted).toBe(true);
    expect(calls).toBe(2);
    await act(async () => { oldBody.resolve({ ...status, cycleCount: 99 }); });
    await tick();
    expect(calls).toBe(2);
    expect(result.current.status).toBeUndefined();
    await act(async () => { newRequest.resolve(ok({ ...status, cycleCount: 2 })); });
    expect(result.current.status?.cycleCount).toBe(2);
    expect(result.current.statusFresh).toBe(true);
  });

  it('配置写入确认取消旧读取，其迟到正文不能覆盖已保存值', async () => {
    const old = deferred<unknown>();
    let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/config')) { signal = init!.signal as AbortSignal; return { ...ok({}), json: () => old.promise }; }
      return handler(url);
    }));
    const { result } = mount();
    await flush();
    act(() => result.current.acceptConfig({ ...config, cycleDelayMs: 100 }, '"new-revision"'));
    expect(signal.aborted).toBe(true);
    await act(async () => { old.resolve(config); });
    expect(result.current.config?.cycleDelayMs).toBe(100);
    expect(result.current.configReady).toBe(true);
    expect(result.current.configRevision).toBe('"new-revision"');
  });

  it.each([
    { path: '/status', bad: { state: 'Imaginary', productionLocked: false }, resource: 'status' as const },
    { path: '/status', bad: { state: 'Stopped', productionLocked: false }, resource: 'status' as const },
    { path: '/status', bad: { ...status, lastDurationMs: 'broken' }, resource: 'status' as const },
    { path: '/config', bad: { ...config, cycleDelayMs: null }, resource: 'config' as const },
    { path: '/api/jobs', bad: {}, resource: 'jobs' as const },
    { path: '/api/jobs', bad: [null], resource: 'jobs' as const },
    { path: '/api/alarms?skip=0&take=8', bad: [null], resource: 'alarms' as const },
    { path: '/api/alarms/summary', bad: { activeCount: 1, criticalCount: 2 }, resource: 'alarmSummary' as const },
    { path: '/pending', bad: null, resource: 'pending' as const },
    { path: '/pending', bad: { deviceIds: 'device' }, resource: 'pending' as const },
  ])('无效成功响应只降级对应资源：$resource $path', async ({ path, bad, resource }) => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith(path) ? ok(bad) : handler(url)));
    const { result } = mount();
    await flush();
    expect(result.current.errors[resource]).toContain('响应无效');
    if (resource !== 'status') expect(result.current.statusFresh).toBe(true);
  });

  it('读取失败保留最后成功时间，恢复后清除错误并继续更新', async () => {
    let next = ok(status);
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/status') ? next : handler(url)));
    const { result } = mount();
    await flush();
    const time = result.current.lastUpdatedAt;
    next = failure('restart required');
    await tick(10_000);
    expect(result.current.lastUpdatedAt).toBe(time);
    expect(result.current.statusFresh).toBe(false);
    next = ok({ ...status, cycleCount: 3 });
    await tick(10_000);
    expect(result.current.statusFresh).toBe(true);
    expect(result.current.errors.status).toBeUndefined();
    expect(result.current.lastUpdatedAt!.getTime()).toBeGreaterThan(time!.getTime());
  });

  it('关闭和卸载取消所有在途读取，之后的 tick 不再请求', async () => {
    const signals: AbortSignal[] = [];
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      signals.push(init!.signal as AbortSignal);
      return new Promise<Response>(() => {});
    }));
    const { unmount } = mount();
    expect(signals).toHaveLength(6);
    unmount();
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    await tick(10000);
    expect(signals).toHaveLength(6);
  });

  it('在途告警请求结束后合并刷新最新推送，并且不等待轮询周期', async () => {
    const oldHistory = deferred<Response>();
    const oldSummary = deferred<Response>();
    let historyReads = 0;
    let summaryReads = 0;
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.includes('/alarms?')) return ++historyReads === 1 ? oldHistory.promise : ok([critical], 1);
      if (url.endsWith('/alarms/summary')) return ++summaryReads === 1 ? oldSummary.promise : ok(criticalSummary);
      return handler(url);
    }));
    const { result, rerender } = mount();

    for (const alarmRevision of ['1', '2']) {
      useMonitorSnapshot.mockReturnValue({ production: status, alarmRevision });
      rerender({ enabled: true });
    }
    expect([historyReads, summaryReads]).toEqual([1, 1]);
    await act(async () => { oldHistory.resolve(ok([])); oldSummary.resolve(ok(emptySummary)); });
    expect([historyReads, summaryReads]).toEqual([2, 2]);
    expect(result.current.alarms).toEqual([critical]);
    expect(result.current.alarmSummary).toEqual(criticalSummary);
  });

  it('在途告警请求超时后仍执行排队的刷新', async () => {
    let historyReads = 0;
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.includes('/alarms?')) return ++historyReads === 1 ? new Promise<Response>(() => {}) : ok([critical], 1);
      return handler(url);
    }));
    const { result, rerender } = mount();
    useMonitorSnapshot.mockReturnValue({ production: status, alarmRevision: '2' });
    rerender({ enabled: true });
    await tick(5000);
    expect(historyReads).toBe(2);
    expect(result.current.alarms).toEqual([critical]);
    expect(result.current.errors.alarms).toBeUndefined();
  });

  it('切页取消上一页请求并保留全库告警统计', async () => {
    const oldPage = deferred<Response>();
    let oldSignal: AbortSignal | undefined;
    let firstPageReads = 0;
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('?skip=0&take=8')) {
        if (++firstPageReads === 1) return ok([], 206);
        oldSignal = init?.signal ?? undefined;
        return oldPage.promise;
      }
      if (url.endsWith('?skip=200&take=8')) return ok([critical], 206);
      if (url.endsWith('/alarms/summary')) return ok(criticalSummary);
      return handler(url);
    });
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await flush();
    expect(result.current.alarmTotal).toBe(206);
    expect(result.current.alarms).toEqual([]);
    expect(result.current.alarmSummary).toEqual(criticalSummary);

    act(() => { result.current.refresh(); result.current.setAlarmPage(26); });
    expect(oldSignal?.aborted).toBe(true);
    await act(async () => { oldPage.resolve(ok([])); });
    expect(result.current.alarmPage).toBe(26);
    expect(result.current.alarmTotal).toBe(206);
    expect(result.current.alarms).toEqual([critical]);
  });

  it('历史被裁剪后回到最后一页并重新读取', async () => {
    let total = 9;
    const fetchMock = vi.fn(async (url: string) => {
      if (url.endsWith('?skip=0&take=8')) return ok([critical], total);
      if (url.endsWith('?skip=8&take=8')) return ok([], total);
      return handler(url);
    });
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await flush();
    act(() => result.current.setAlarmPage(2));
    await flush();
    expect(result.current.alarmPage).toBe(2);
    total = 1;

    act(() => result.current.refresh());
    await flush();
    expect(result.current.alarmPage).toBe(1);
    expect(result.current.alarmTotal).toBe(1);
    expect(result.current.alarms).toEqual([critical]);
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('?skip=0&take=8'))).toHaveLength(2);
  });

  it('切换待确认筛选时回到第一页，取消旧查询并忽略其迟到结果', async () => {
    const old = deferred<Response>();
    let oldSignal: AbortSignal | undefined;
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('?skip=0&take=8')) return ok([], 40);
      if (url.endsWith('?skip=8&take=8')) {
        oldSignal = init?.signal ?? undefined;
        return old.promise;
      }
      if (url.endsWith('?skip=0&take=8&pendingOnly=true')) return ok([critical], 1);
      return handler(url);
    });
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await flush();
    act(() => result.current.setAlarmPage(2));
    expect(result.current.alarmPage).toBe(2);
    act(() => result.current.setAlarmFilter('pending'));
    expect(oldSignal?.aborted).toBe(true);
    await act(async () => { old.resolve(ok([], 40)); });
    expect(result.current.alarmFilter).toBe('pending');
    expect(result.current.alarmPage).toBe(1);
    expect(result.current.alarmTotal).toBe(1);
    expect(result.current.alarms).toEqual([critical]);
    expect(result.current.alarmsLoading).toBe(false);
  });

  it('确认后立即重新读取告警和统计，忽略确认前尚未返回的旧结果', async () => {
    const previousPage = deferred<Response>();
    const previousSummary = deferred<Response>();
    const signals: AbortSignal[] = [];
    let pauseReads = false;
    let acknowledged = false;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.includes('/alarms?') || url.endsWith('/alarms/summary')) {
        const summary = url.endsWith('/summary');
        if (pauseReads) {
          signals.push(init?.signal as AbortSignal);
          return summary ? previousSummary.promise : previousPage.promise;
        }
        return summary ? ok({ ...criticalSummary, pendingCount: acknowledged ? 0 : 1 })
          : acknowledged ? ok([]) : ok([critical], 1);
      }
      return handler(url);
    }));
    const { result } = mount();
    await flush();
    act(() => result.current.setAlarmFilter('pending'));
    await flush();
    pauseReads = true;
    act(() => result.current.refresh());

    pauseReads = false;
    acknowledged = true;
    act(() => { result.current.invalidateAlarmReads(); result.current.refresh(); });
    expect(signals).toHaveLength(2);
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    await act(async () => { previousPage.resolve(ok([critical], 1)); previousSummary.resolve(ok(criticalSummary)); });
    expect(result.current.alarms).toEqual([]);
    expect(result.current.alarmTotal).toBe(0);
    expect(result.current.alarmSummary?.pendingCount).toBe(0);
  });

  it('重开面板时缓存的推送不能把状态标记为已确认', async () => {
    const { result, rerender } = mount();
    await flush();
    useMonitorSnapshot.mockReturnValue({ production: status, alarmRevision: '1' });
    rerender({ enabled: true });
    expect(result.current.statusFresh).toBe(true);
    rerender({ enabled: false });

    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/status') ? new Promise<Response>(() => {}) : handler(url)));
    rerender({ enabled: true });
    await flush();
    expect(result.current.statusFresh).toBe(false);
  });

  it('迟到的 HTTP 状态无法覆盖更新的推送', async () => {
    const oldStatus = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/status') ? oldStatus.promise : handler(url)));
    const { result, rerender } = mount();
    useMonitorSnapshot.mockReturnValue({
      production: { ...status, state: 'Running', productionLocked: true, statusEpoch: 'host-a', statusVersion: 2 },
      alarmRevision: '1',
    });
    rerender({ enabled: true });
    await act(async () => { oldStatus.resolve(ok({ ...status, statusEpoch: 'host-a', statusVersion: 1 })); });
    expect(result.current.status?.state).toBe('Running');
    expect(result.current.statusFresh).toBe(true);
  });
});
