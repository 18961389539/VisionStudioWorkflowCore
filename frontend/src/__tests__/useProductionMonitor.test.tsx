import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useProductionMonitor } from '../useProductionMonitor';
import { defaultProductionConfig as defaults } from '../productionConfig';

const status = { state: 'Stopped', productionLocked: false, cycleCount: 1, okCount: 1, ngCount: 0,
  errorCount: 0, consecutiveFailures: 0, watchdogTrips: 0, lastDurationMs: 0 };
const config = { ...defaults, jobId: 'A' };
const pending = { runId: 'run-1', reason: 'unknown device action', deviceIds: ['device'] };
const ok = (data: unknown) => ({ ok: true, status: 200, json: async () => data }) as Response;
const failure = (detail = 'not available') => ({ ok: false, status: 503, json: async () => ({ detail, correlationId: 'test-trace' }) }) as Response;
const empty = () => ({ ok: true, status: 204 }) as Response;
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
const handler = async (url: string) => url.endsWith('/status') ? ok(status) : url.endsWith('/config') ? ok(config)
  : url.endsWith('/pending') ? empty() : ok([]);
const flush = async () => { await act(async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); }); };
const tick = async (ms = 1000) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };
const mount = (open = true) => renderHook(({ enabled }) => useProductionMonitor(enabled), { initialProps: { enabled: open } });

beforeEach(() => { vi.useFakeTimers(); vi.stubGlobal('fetch', vi.fn(handler)); });
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
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('/status'))).toHaveLength(3);
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
    await tick();
    expect(result.current.pending).toEqual(pending);
    expect(result.current.errors.pending).toMatch(/permission lost.*test-trace/);
    next = empty();
    await tick();
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
    expect(calls).toBeGreaterThanOrEqual(2);
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
    act(() => result.current.acceptConfig({ ...config, cycleDelayMs: 100 }));
    expect(signal.aborted).toBe(true);
    await act(async () => { old.resolve(config); });
    expect(result.current.config?.cycleDelayMs).toBe(100);
    expect(result.current.configReady).toBe(true);
  });

  it.each([
    { path: '/status', bad: { state: 'Imaginary', productionLocked: false }, resource: 'status' as const },
    { path: '/status', bad: { state: 'Stopped', productionLocked: false }, resource: 'status' as const },
    { path: '/status', bad: { ...status, lastDurationMs: 'broken' }, resource: 'status' as const },
    { path: '/config', bad: { ...config, cycleDelayMs: null }, resource: 'config' as const },
    { path: '/api/jobs', bad: {}, resource: 'jobs' as const },
    { path: '/api/jobs', bad: [null], resource: 'jobs' as const },
    // 报警列表自 2026-10-26 起显式带 take（服务端默认只返回最近 200 条）
    { path: '/api/alarms?activeOnly=false&take=200', bad: [null], resource: 'alarms' as const },
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
    await tick();
    expect(result.current.lastUpdatedAt).toBe(time);
    expect(result.current.statusFresh).toBe(false);
    next = ok({ ...status, cycleCount: 3 });
    await tick();
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
    expect(signals).toHaveLength(5);
    unmount();
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    await tick(10000);
    expect(signals).toHaveLength(5);
  });
});
