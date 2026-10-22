import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useProductionActions } from '../useProductionActions';

const response = (body: unknown = {}, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => body }) as Response;
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
const request = (action = 'save') => ({ url: `/api/production/${action}`, method: action === 'save' ? 'PUT' : 'POST',
  action, body: { jobId: 'A' }, successMessage: 'done', onSuccess: vi.fn() });
function mount() {
  const messages = { success: vi.fn(), error: vi.fn() };
  const refresh = vi.fn();
  return { ...renderHook(({ open }) => useProductionActions(open, messages, refresh), { initialProps: { open: true } }), messages, refresh };
}
beforeEach(() => { vi.useFakeTimers(); vi.stubGlobal('fetch', vi.fn(async () => response())); });
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('生产操作生命周期', () => {
  it('同一事件重复保存只提交一次，成功后再释放忙碌状态', async () => {
    const pending = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn(() => pending.promise));
    const { result, messages, refresh } = mount();
    const value = request();
    let first!: ReturnType<typeof result.current.call>;
    act(() => { first = result.current.call(value); });
    const duplicate = await result.current.call(value);
    expect(duplicate.ok).toBe(false);
    expect(fetch).toHaveBeenCalledOnce();
    expect(result.current.busy.has('save')).toBe(true);
    await act(async () => { pending.resolve(response({ saved: true })); await first; });
    expect(value.onSuccess).toHaveBeenCalledWith({ saved: true });
    expect(messages.success).toHaveBeenCalledOnce();
    expect(refresh).toHaveBeenCalledOnce();
    expect(result.current.busy.size).toBe(0);
  });

  it.each([['save', 'start'], ['start', 'save']])('%s 与 %s 同步互斥，停止入口仍可调用', async (first, blocked) => {
    const pending = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn(() => pending.promise));
    const { result } = mount();
    act(() => { void result.current.call(request(first)); });
    expect((await result.current.call(request(blocked))).ok).toBe(false);
    act(() => { void result.current.call(request('stop')); });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(result.current.busy.has('stop')).toBe(true);
    await act(async () => { pending.resolve(response()); });
  });

  it('停止后的迟到启动响应不恢复运行状态或显示启动成功', async () => {
    const startResponse = deferred<Response>();
    const stopResponse = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn((url: string) => url.endsWith('/start') ? startResponse.promise : stopResponse.promise));
    const { result, messages } = mount();
    const start = request('start');
    const stop = request('stop');
    act(() => { void result.current.call(start); void result.current.call(stop); });
    await act(async () => { stopResponse.resolve(response({ state: 'Stopped' })); });
    await act(async () => { startResponse.resolve(response({ state: 'Running' })); });
    expect(stop.onSuccess).toHaveBeenCalledWith({ state: 'Stopped' });
    expect(start.onSuccess).not.toHaveBeenCalled();
    expect(messages.success).toHaveBeenCalledTimes(1);
    expect(result.current.busy.size).toBe(0);
  });

  it('HTTP 拒绝保留详细错误和关联号，不执行成功回调', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ detail: 'stop first', code: 'locked', correlationId: 'run-trace' }, 409)));
    const { result, messages } = mount();
    const value = request();
    await act(async () => { await result.current.call(value); });
    expect(result.current.error).toMatch(/stop first.*locked.*run-trace/);
    expect(value.onSuccess).not.toHaveBeenCalled();
    expect(messages.success).not.toHaveBeenCalled();
    expect(result.current.busy.size).toBe(0);
  });

  it.each(['network', 'invalid-body', 'invalid-contract', 'server-error'])('%s 结果未确认时保留错误，不自动重试写入', async (kind) => {
    vi.stubGlobal('fetch', vi.fn(async () => {
      if (kind === 'network') throw new TypeError('offline');
      if (kind === 'invalid-body') return { ...response(), json: async () => { throw new SyntaxError('invalid JSON'); } };
      return response({}, kind === 'server-error' ? 503 : 200);
    }));
    const { result, refresh, messages } = mount();
    const value = request();
    if (kind === 'invalid-contract') value.onSuccess.mockImplementation(() => { throw new Error('invalid saved config'); });
    await act(async () => { await result.current.call(value); });
    expect(result.current.error).toContain('结果未确认');
    expect(fetch).toHaveBeenCalledOnce();
    expect(refresh).toHaveBeenCalledOnce();
    expect(messages.success).not.toHaveBeenCalled();
  });

  it('20 秒超时取消在途请求并恢复操作入口，提示不能确认是否提交', async () => {
    let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      signal = init!.signal as AbortSignal;
      signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    const { result } = mount();
    act(() => { void result.current.call(request()); });
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(signal.aborted).toBe(true);
    expect(result.current.busy.size).toBe(0);
    expect(result.current.error).toMatch(/结果未确认.*超时/);
  });

  it('关闭重开后旧写入正文与 finally 不影响新操作', async () => {
    const old = deferred<unknown>();
    const next = deferred<Response>();
    let calls = 0;
    let oldSignal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
      if (++calls === 1) { oldSignal = init!.signal as AbortSignal; return { ...response(), json: () => old.promise }; }
      return next.promise;
    }));
    const { result, rerender, messages } = mount();
    const value = request();
    await act(async () => { void result.current.call(value); });
    rerender({ open: false });
    rerender({ open: true });
    act(() => { void result.current.call(value); });
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { old.resolve({ saved: 'old' }); });
    expect(result.current.busy.has('save')).toBe(true);
    expect(value.onSuccess).not.toHaveBeenCalled();
    await act(async () => { next.resolve(response({ saved: 'new' })); });
    expect(value.onSuccess).toHaveBeenCalledWith({ saved: 'new' });
    expect(messages.success).toHaveBeenCalledOnce();
  });

  it('卸载取消在途操作，迟到成功不再通知或刷新', async () => {
    const pending = deferred<Response>();
    let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => { signal = init!.signal as AbortSignal; return pending.promise; }));
    const { result, unmount, messages, refresh } = mount();
    const value = request();
    act(() => { void result.current.call(value); });
    unmount();
    expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve(response()); });
    expect(value.onSuccess).not.toHaveBeenCalled();
    expect(messages.success).not.toHaveBeenCalled();
    expect(refresh).not.toHaveBeenCalled();
  });
});
