import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useTuningData } from '../useTuningData';
import { deferred, jsonResponse, tuningDataset, tuningItem, tuningRun } from './fixtures/tuning';

const datasets = [tuningDataset('A').dataset, tuningDataset('B').dataset];
const handler = async (url: string) => jsonResponse(url === '/api/validation/datasets' ? datasets
  : url.endsWith('/runs') ? [] : tuningDataset(decodeURIComponent(url.split('/').at(-1)!)));
const flush = async () => { await act(async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); }); };
const tick = async (ms = 1000) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };
const mount = () => renderHook(({ open }) => useTuningData(open), { initialProps: { open: true } });
beforeEach(() => { vi.useFakeTimers(); vi.stubGlobal('fetch', vi.fn(handler)); });
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('调优数据集读取', () => {
  it('详情慢或失败不阻塞验证记录，错误可见并可重新读取', async () => {
    const detail = deferred<Response>(); let broken = true;
    vi.stubGlobal('fetch', vi.fn((url: string) => url.endsWith('/A') ? broken ? detail.promise : Promise.resolve(jsonResponse(tuningDataset()))
      : url.endsWith('/A/runs') ? Promise.resolve(jsonResponse([tuningRun()])) : handler(url)));
    const { result } = mount(); await flush();
    expect(result.current.runs).toHaveLength(1);
    expect(result.current.ready).toBe(false);
    await act(async () => { detail.resolve(jsonResponse({ detail: 'detail unavailable', correlationId: 'detail-trace' }, 503)); });
    expect(result.current.errors.detail).toMatch(/detail unavailable.*detail-trace/);
    broken = false;
    await act(async () => { result.current.refresh(); });
    expect(result.current.ready).toBe(true);
    expect(result.current.errors.detail).toBeUndefined();
  });

  it('切换立即隐藏旧样本，旧正文及 finally 不覆盖新读取', async () => {
    const old = deferred<unknown>(); const next = deferred<Response>(); let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/A')) { signal = init!.signal as AbortSignal; return { ...jsonResponse({}), json: () => old.promise }; }
      return url.endsWith('/B') ? next.promise : handler(url);
    }));
    const { result } = mount(); await flush();
    act(() => result.current.chooseDataset('B'));
    expect(signal.aborted).toBe(true); expect(result.current.dataset).toBeUndefined();
    await act(async () => { old.resolve(tuningDataset('A')); });
    expect(result.current.loading.detail).toBe(true);
    expect(result.current.itemId).toBeUndefined();
    await act(async () => { next.resolve(jsonResponse(tuningDataset('B'))); });
    expect(result.current.sample?.datasetId).toBe('B');
  });

  it('同事件 A → B → A 仍会重新读取，不能接受原来的 A 响应', async () => {
    const old = deferred<unknown>(); let calls = 0;
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/A') && ++calls === 1
      ? { ...jsonResponse({}), json: () => old.promise } : handler(url)));
    const { result } = mount(); await flush();
    act(() => { result.current.chooseDataset('B'); result.current.chooseDataset('A'); });
    await flush();
    expect(calls).toBe(2); expect(result.current.ready).toBe(true);
    await act(async () => { old.resolve({ ...tuningDataset('A'), items: [tuningItem('A', 'old-item')] }); });
    expect(result.current.itemId).toBe('sample');
  });

  it('保留有效样本选择，重新读取后已移除的样本不再参与预览', async () => {
    let detail = { ...tuningDataset(), items: [tuningItem(), tuningItem('A', 'chosen')] };
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/A') ? jsonResponse(detail) : handler(url)));
    const { result } = mount(); await flush();
    act(() => result.current.chooseItem('chosen'));
    await act(async () => result.current.refresh());
    expect(result.current.itemId).toBe('chosen');
    detail = tuningDataset();
    await act(async () => result.current.refresh());
    expect(result.current.itemId).toBe('sample');
  });

  it('慢验证轮询不重叠，完成后停止轮询；详情不被每秒重复读取', async () => {
    const slow = deferred<Response>(); let runsCalls = 0; let detailCalls = 0;
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/A')) detailCalls++;
      if (url.endsWith('/A/runs')) return ++runsCalls === 1 ? jsonResponse([tuningRun('A', 'running', 'Running')]) : slow.promise;
      return handler(url);
    }));
    const { result } = mount(); await flush();
    await tick(3500);
    expect(runsCalls).toBe(2); expect(detailCalls).toBe(1);
    await act(async () => { slow.resolve(jsonResponse([tuningRun('A', 'running')])); });
    await tick(3000);
    expect(runsCalls).toBe(2); expect(result.current.runs[0].status).toBe('Completed');
  });

  it('接收刚创建的验证记录使之前的轮询失效，不会被旧正文抹掉', async () => {
    const old = deferred<unknown>(); let count = 0; let oldSignal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => url.endsWith('/A/runs') && ++count === 1
      ? (oldSignal = init!.signal as AbortSignal, { ...jsonResponse({}), json: () => old.promise }) : handler(url)));
    const { result } = mount(); await flush();
    act(() => result.current.acceptRun(tuningRun('A', 'new', 'Running')));
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { old.resolve([tuningRun('A', 'old')]); });
    expect(result.current.runs.some((run) => run.runId === 'old')).toBe(false);
    expect(result.current.runs.some((run) => run.runId === 'new')).toBe(true);
  });

  it.each(['list', 'detail', 'runs'])('拒绝 %s 的无效或混属数据，不用错误响应清空其它资源', async (resource) => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => resource === 'list' && url === '/api/validation/datasets' ? jsonResponse({})
      : resource === 'detail' && url.endsWith('/A') ? jsonResponse(tuningDataset('B'))
        : resource === 'runs' && url.endsWith('/A/runs') ? jsonResponse([tuningRun('B')]) : handler(url)));
    const { result } = mount(); await flush();
    expect(result.current.errors[resource]).toBeTruthy();
    if (resource === 'runs') expect(result.current.ready).toBe(true);
  });

  it('读取超时可重试，关闭重开立即发新请求且卸载取消全部读取', async () => {
    const signals: AbortSignal[] = [];
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      const signal = init!.signal as AbortSignal; signals.push(signal);
      signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    const { result, rerender, unmount } = mount();
    await tick(15_000);
    expect(result.current.errors.list).toContain('15 秒');
    act(() => result.current.refresh());
    rerender({ open: false }); rerender({ open: true });
    expect(signals).toHaveLength(3);
    expect(signals.slice(0, 2).every((signal) => signal.aborted)).toBe(true);
    unmount(); expect(signals[2].aborted).toBe(true);
  });

  it.each(['list', 'detail', 'runs'] as const)('%s 的请求不响应取消时仍结束加载，旧响应不能解锁重试', async (resource) => {
    const old = deferred<Response>(); const retry = deferred<Response>(); let calls = 0;
    const target = resource === 'list' ? '/api/validation/datasets' : `/api/validation/datasets/A${resource === 'runs' ? '/runs' : ''}`;
    const payload = resource === 'list' ? datasets : resource === 'detail' ? tuningDataset() : [tuningRun()];
    vi.stubGlobal('fetch', vi.fn((url: string) => url === target ? ++calls === 1 ? old.promise : retry.promise : handler(url)));
    const { result } = mount(); await flush();
    await tick(15_000);
    expect(result.current.loading[resource]).toBe(false);
    expect(result.current.errors[resource]).toContain('15 秒');
    act(() => result.current.refresh());
    await act(async () => { old.resolve(jsonResponse(payload)); });
    expect(result.current.loading[resource]).toBe(true);
    await act(async () => { retry.resolve(jsonResponse(payload)); });
    expect(result.current.loading[resource]).toBe(false);
    expect(result.current.errors[resource]).toBeUndefined();
    expect(result.current.ready).toBe(true);
  });

  it.each(['list', 'detail', 'runs'] as const)('%s 的正文不响应取消时仍允许重新读取，迟到正文不会落地', async (resource) => {
    const body = deferred<unknown>(); let calls = 0;
    const target = resource === 'list' ? '/api/validation/datasets' : `/api/validation/datasets/A${resource === 'runs' ? '/runs' : ''}`;
    const oldPayload = resource === 'list' ? [] : resource === 'detail'
      ? { ...tuningDataset(), items: [tuningItem('A', 'late-item')] } : [tuningRun('A', 'late-run')];
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url === target && ++calls === 1
      ? { ...jsonResponse({}), json: () => body.promise } : handler(url)));
    const { result } = mount(); await flush();
    await tick(15_000);
    expect(result.current.loading[resource]).toBe(false);
    expect(result.current.errors[resource]).toContain('15 秒');
    await act(async () => result.current.refresh());
    expect(result.current.errors[resource]).toBeUndefined();
    expect(result.current.ready).toBe(true);
    await act(async () => { body.resolve(oldPayload); });
    expect(result.current.datasetId).toBe('A');
    expect(result.current.itemId).toBe('sample');
    expect(result.current.runs.some((run) => run.runId === 'late-run')).toBe(false);
    expect(result.current.errors[resource]).toBeUndefined();
  });
});
