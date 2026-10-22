import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useTuningPreview } from '../useTuningPreview';
import { deferred, jsonResponse, tuningItem, tuningPreview, tuningWorkflow } from './fixtures/tuning';

const initial = { open: true, workflow: tuningWorkflow, nodeId: 'source', sample: tuningItem(), ready: true, autoPreview: false, revision: 0 };
const mount = (patch: Partial<typeof initial> = {}) => renderHook((props) => useTuningPreview(props), { initialProps: { ...initial, ...patch } });
const tick = async (ms: number) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };
beforeEach(() => { vi.useFakeTimers(); vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(tuningPreview()))); });
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('调优预览归属与节拍', () => {
  it('防抖期间旧响应也不能落地，编辑立即取消旧请求', async () => {
    const old = deferred<Response>(); const next = deferred<Response>(); let oldSignal!: AbortSignal;
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      if (++calls === 1) { oldSignal = init!.signal as AbortSignal; return old.promise; } return next.promise;
    }));
    const { result, rerender } = mount({ autoPreview: true });
    await tick(550);
    rerender({ ...initial, autoPreview: true, revision: 1 });
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { old.resolve(jsonResponse(tuningPreview())); });
    expect(result.current.preview).toBeUndefined();
    await tick(549); expect(calls).toBe(1);
    await tick(1); expect(calls).toBe(2);
    await act(async () => { next.resolve(jsonResponse(tuningPreview(false, tuningItem(), 'new-preview'))); });
    expect(result.current.preview?.replay.runId).toBe('new-preview');
  });

  it('自动关闭时修改参数仍立即移除旧指标，保留有明确过期标记的 ROI 背景', async () => {
    const { result, rerender } = mount();
    await act(async () => { await result.current.run(false); });
    expect(result.current.backgroundStale).toBe(false);
    rerender({ ...initial, revision: 1 });
    expect(result.current.preview).toBeUndefined();
    expect(result.current.previewUrl).toContain('preview-1');
    expect(result.current.backgroundStale).toBe(true);
    await tick(3000);
    expect(fetch).toHaveBeenCalledOnce();
  });

  it.each(['sample', 'dataset', 'node'])('切换 %s 不能沿用其它上下文的图像或指标', async (kind) => {
    const { result, rerender } = mount();
    await act(async () => { await result.current.run(false); });
    const sample = kind === 'dataset' ? tuningItem('B') : kind === 'sample' ? tuningItem('A', 'other') : tuningItem();
    rerender({ ...initial, sample, nodeId: kind === 'node' ? 'other-node' : 'source' });
    expect(result.current.preview).toBeUndefined();
    expect(result.current.previewUrl).toBeUndefined();
  });

  it('手动完整流程优先于在途自动节点预览，旧结果不能覆盖完整流程结果', async () => {
    const automatic = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => JSON.parse(String(init!.body)).fullWorkflow
      ? Promise.resolve(jsonResponse(tuningPreview(true))) : automatic.promise));
    const { result } = mount({ autoPreview: true });
    await tick(550);
    await act(async () => { await result.current.run(true); });
    await act(async () => { automatic.resolve(jsonResponse(tuningPreview(false))); });
    await tick(1000);
    expect(result.current.preview?.fullWorkflow).toBe(true);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('手动请求取消尚未到期的自动任务，同事件重复点击只发一次', async () => {
    const pending = deferred<Response>(); vi.stubGlobal('fetch', vi.fn(() => pending.promise));
    const { result } = mount({ autoPreview: true });
    act(() => { void result.current.run(false); void result.current.run(false); });
    await tick(1000); expect(fetch).toHaveBeenCalledOnce();
    await act(async () => { pending.resolve(jsonResponse(tuningPreview())); });
  });

  it('关闭自动预览取消自动请求，但保留手动请求', async () => {
    const pending = deferred<Response>(); let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => { signal = init!.signal as AbortSignal; return pending.promise; }));
    const { result, rerender } = mount({ autoPreview: true });
    act(() => { void result.current.run(true); });
    rerender(initial);
    expect(signal.aborted).toBe(false);
    await act(async () => { pending.resolve(jsonResponse(tuningPreview(true))); });
    expect(result.current.preview?.fullWorkflow).toBe(true);
  });

  it.each(['identity', 'node', 'duration', 'sample-source'])('不接受与请求不一致或不完整的 %s 成功响应', async (kind) => {
    const value = tuningPreview();
    if (kind === 'identity') value.itemId = 'other';
    if (kind === 'node') value.selectedNodeReport!.nodeId = 'other';
    if (kind === 'duration') value.replay.totalDurationMs = NaN;
    if (kind === 'sample-source') value.sourceRef = 'other-trace';
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(value)));
    const { result } = mount();
    await act(async () => { expect(await result.current.run(false)).toBe(false); });
    expect(result.current.preview).toBeUndefined();
    expect(result.current.error).toContain('不匹配');
  });

  it('算法失败报告仍作为当前结果展示，自动请求的 HTTP 错误也可见', async () => {
    const value = tuningPreview(); value.replay.success = false; value.replay.error = 'no feature found';
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(value)));
    const { result, rerender } = mount();
    await act(async () => { expect(await result.current.run(false)).toBe(true); });
    expect(result.current.preview?.replay.error).toBe('no feature found');
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ detail: 'offline unavailable', correlationId: 'preview-trace' }, 503)));
    rerender({ ...initial, autoPreview: true, revision: 1 });
    await tick(550);
    expect(result.current.error).toMatch(/offline unavailable.*preview-trace/);
  });

  it('同内容重置与关闭后重开都使旧正文及旧 finally 失效', async () => {
    const old = deferred<unknown>(); const next = deferred<Response>(); let count = 0;
    vi.stubGlobal('fetch', vi.fn(async () => ++count === 1 ? { ...jsonResponse({}), json: () => old.promise } : next.promise));
    const { result, rerender } = mount();
    await act(async () => { void result.current.run(false); });
    rerender({ ...initial, open: false }); rerender({ ...initial, revision: 2 });
    act(() => { void result.current.run(false); });
    await act(async () => { old.resolve(tuningPreview()); });
    expect(result.current.busy).toBe(true);
    expect(result.current.preview).toBeUndefined();
    await act(async () => { next.resolve(jsonResponse(tuningPreview())); });
    expect(result.current.busy).toBe(false);
  });

  it('60 秒超时和卸载都取消在途请求，超时显示明确反馈', async () => {
    let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      signal = init!.signal as AbortSignal; signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    const { result, unmount } = mount();
    act(() => { void result.current.run(false); });
    await tick(60_000);
    expect(result.current.error).toContain('60 秒');
    expect(result.current.busy).toBe(false);
    act(() => { void result.current.run(false); });
    unmount(); expect(signal.aborted).toBe(true);
  });

  it.each(['response', 'body', 'error-body'])('%s 不响应取消时，期限仍结束预览并隔离迟到内容', async (stage) => {
    const response = deferred<Response>(); const body = deferred<unknown>(); const retry = deferred<Response>();
    let signal!: AbortSignal; let calls = 0; let settled: boolean | undefined;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      if (++calls > 1) return retry.promise;
      signal = init!.signal as AbortSignal;
      return stage === 'response' ? response.promise : Promise.resolve({
        ...jsonResponse({}, stage === 'error-body' ? 503 : 200), json: () => body.promise,
      });
    }));
    const { result } = mount();
    await act(async () => { void result.current.run(false).then((value) => { settled = value; }); });
    await tick(60_000);
    expect(signal.aborted).toBe(true);
    expect(result.current.busy).toBe(false);
    expect(result.current.error).toContain('60 秒');
    expect(settled).toBe(false);

    act(() => { void result.current.run(false); });
    await act(async () => {
      response.resolve(jsonResponse(tuningPreview()));
      body.resolve(stage === 'error-body' ? { detail: 'late error' } : tuningPreview());
    });
    expect(result.current.busy).toBe(true);
    expect(result.current.preview).toBeUndefined();
    expect(result.current.error).toBeUndefined();
    await act(async () => { retry.resolve(jsonResponse(tuningPreview(false, tuningItem(), 'retry-preview'))); });
    expect(result.current.preview?.replay.runId).toBe('retry-preview');
    expect(result.current.busy).toBe(false);
  });
});
