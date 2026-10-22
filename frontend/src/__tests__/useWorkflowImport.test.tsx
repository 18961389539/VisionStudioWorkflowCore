import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useWorkflowImport } from '../useWorkflowImport';
import { useWorkflowChanges } from '../useWorkflowChanges';

const original = { id: 'A', name: 'Original', nodes: [], edges: [] };
const loaded = { id: 'B', name: 'Loaded', nodes: [], edges: [] };
const response = (data: unknown = loaded, status = 200) => ({ ok: status >= 200 && status < 300, status,
  headers: new Headers({ 'X-Workflow-Revision': 'r2' }), json: async () => data }) as Response;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
function mount(dirty = false) {
  const props = { snapshot: JSON.stringify(original), dirty, generationRef: { current: 1 }, save: vi.fn(async () => true),
    apply: vi.fn(), warning: vi.fn(), error: vi.fn(), active: true, contextKey: 'recipe-A' };
  return { ...renderHook((value) => {
    const changes = useWorkflowChanges(value);
    const importer = useWorkflowImport({ active: value.active, contextKey: value.contextKey, prepare: changes.prepare, error: value.error });
    return { changes, ...importer };
  }, { initialProps: props }), props };
}
const flush = async () => { await act(async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); }); };
beforeEach(() => vi.stubGlobal('fetch', vi.fn(async () => response())));
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('流程载入生命周期', () => {
  it('读取正文完成才确认，取消不应用或发出成功反馈', async () => {
    const body = deferred<unknown>();
    vi.stubGlobal('fetch', vi.fn(async () => ({ ...response(), json: () => body.promise })));
    const { result, props } = mount(true);
    const onApplied = vi.fn();
    let operation!: Promise<boolean>;
    act(() => { operation = result.current.load({ url: '/api/workflows/B', label: 'load B', onApplied }); });
    expect(result.current.changes.pending).toBeUndefined();
    await act(async () => { body.resolve(loaded); });
    expect(result.current.changes.pending?.label).toBe('load B');
    act(() => result.current.changes.cancel());
    await act(async () => { expect(await operation).toBe(false); });
    expect(props.apply).not.toHaveBeenCalled();
    expect(onApplied).not.toHaveBeenCalled();
    expect(result.current.loading).toBe(false);
  });

  it('明确确认后应用文档和后端基线，最后通知调用者成功', async () => {
    const { result, props } = mount(true);
    const onApplied = vi.fn();
    let operation!: Promise<boolean>;
    act(() => { operation = result.current.load({ url: '/api/workflows/B', label: 'load B',
      options: (value) => ({ markClean: true, revision: value.headers.get('X-Workflow-Revision')! }), onApplied }); });
    await flush();
    act(() => result.current.changes.discard());
    await act(async () => { expect(await operation).toBe(true); });
    expect(props.apply).toHaveBeenCalledWith(loaded, expect.objectContaining({ revision: 'r2', markClean: true }));
    expect(onApplied).toHaveBeenCalledWith(loaded);
    expect(result.current.loading).toBe(false);
  });

  it('同事件连续点击不重叠读取', async () => {
    const pending = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn(() => pending.promise));
    const { result } = mount();
    let operation!: Promise<boolean>;
    act(() => { operation = result.current.load({ url: '/A', label: 'load' }); });
    expect(await result.current.load({ url: '/A', label: 'duplicate' })).toBe(false);
    expect(fetch).toHaveBeenCalledOnce();
    await act(async () => { pending.resolve(response()); await operation; });
  });

  it('载入后回调异常仍返回已应用，不误报设计器保持不变', async () => {
    const { result, props } = mount();
    await act(async () => {
      expect(await result.current.load({ workflow: loaded, label: 'load',
        onApplied: () => { throw new Error('notification failed'); } })).toBe(true);
    });
    expect(props.apply).toHaveBeenCalledOnce();
    expect(props.error).toHaveBeenCalledWith(expect.stringContaining('流程已载入'));
    expect(props.error).toHaveBeenCalledWith(expect.stringContaining('notification failed'));
    expect(props.error).not.toHaveBeenCalledWith(expect.stringContaining('当前设计器保持不变'));
    expect(result.current.loading).toBe(false);
  });

  it('重复节点标识在确认前拒绝，不覆盖当前文档', async () => {
    const { result, props } = mount(true);
    const onApplied = vi.fn();
    const node = { id: 'duplicate', type: 'image.synthetic' };
    await act(async () => {
      expect(await result.current.load({ workflow: { ...loaded, nodes: [node, node] }, label: 'load', onApplied })).toBe(false);
    });
    expect(result.current.changes.pending).toBeUndefined();
    expect(props.apply).not.toHaveBeenCalled();
    expect(onApplied).not.toHaveBeenCalled();
    expect(props.error).toHaveBeenCalledWith(expect.stringContaining('有效的工作流文档'));
  });

  it('读取期间重命名或编辑不会被迟到文档覆盖', async () => {
    const body = deferred<unknown>();
    vi.stubGlobal('fetch', vi.fn(async () => ({ ...response(), json: () => body.promise })));
    const { result, props, rerender } = mount();
    const onApplied = vi.fn();
    let operation!: Promise<boolean>;
    act(() => { operation = result.current.load({ url: '/B', label: 'load', onApplied }); });
    rerender({ ...props, snapshot: JSON.stringify({ ...original, name: 'new edit' }) });
    await act(async () => { body.resolve(loaded); await operation; });
    expect(props.apply).not.toHaveBeenCalled();
    expect(onApplied).not.toHaveBeenCalled();
    expect(props.warning).toHaveBeenCalledWith(expect.stringContaining('设计器已改变'));
  });

  it('切换来源取消旧正文，旧 finally 不影响新请求', async () => {
    const old = deferred<unknown>(); const next = deferred<Response>();
    let calls = 0; let oldSignal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
      if (++calls === 1) { oldSignal = init!.signal as AbortSignal; return { ...response(), json: () => old.promise }; }
      return next.promise;
    }));
    const { result, props, rerender } = mount();
    act(() => { void result.current.load({ url: '/first', label: 'first' }); });
    await flush();
    rerender({ ...props, contextKey: 'recipe-B' });
    act(() => { void result.current.load({ url: '/next', label: 'next' }); });
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { old.resolve(loaded); });
    expect(result.current.loading).toBe(true);
    expect(props.apply).not.toHaveBeenCalled();
    await act(async () => { next.resolve(response()); });
    expect(props.apply).toHaveBeenCalledOnce();
    expect(result.current.loading).toBe(false);
  });

  it('关闭来源面板会同时取消读取和未保存确认', async () => {
    const { result, props, rerender } = mount(true);
    const onApplied = vi.fn();
    let operation!: Promise<boolean>;
    act(() => { operation = result.current.load({ url: '/B', label: 'load', onApplied }); });
    await flush();
    expect(result.current.changes.pending).toBeDefined();
    rerender({ ...props, active: false });
    expect(await operation).toBe(false);
    expect(result.current.changes.pending).toBeUndefined();
    expect(onApplied).not.toHaveBeenCalled();
  });

  it.each(['HTTP', 'JSON', 'schema', 'identity'])('载入 %s 失败不改变画布或提示成功', async (kind) => {
    vi.stubGlobal('fetch', vi.fn(async () => kind === 'HTTP' ? response({ detail: 'unavailable', correlationId: 'trace-load' }, 503)
      : kind === 'JSON' ? { ...response(), json: async () => { throw new SyntaxError('broken JSON'); } }
        : kind === 'schema' ? response({ id: 'B', name: 'B', nodes: [null], edges: [] }) : response(loaded)));
    const { result, props } = mount();
    const onApplied = vi.fn();
    await act(async () => {
      expect(await result.current.load({ url: '/A', label: 'load', onApplied,
        select: kind === 'identity' ? () => { throw new Error('mismatched identity'); } : undefined })).toBe(false);
    });
    expect(props.apply).not.toHaveBeenCalled();
    expect(onApplied).not.toHaveBeenCalled();
    expect(props.error).toHaveBeenCalledWith(expect.stringContaining('当前设计器保持不变'));
    if (kind === 'HTTP') expect(props.error).toHaveBeenCalledWith(expect.stringContaining('trace-load'));
  });

  it('人工确认等待不受网络超时限制，已解析候选仍由关闭动作取消', async () => {
    vi.useFakeTimers();
    const { result, props } = mount(true);
    let operation!: Promise<boolean>;
    act(() => { operation = result.current.load({ workflow: loaded, label: 'resolved preview' }); });
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(30_000); });
    expect(result.current.changes.pending).toBeDefined();
    act(() => result.current.changes.discard());
    await act(async () => { expect(await operation).toBe(true); });
    expect(props.apply).toHaveBeenCalledOnce();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('读取超时给出错误并释放入口', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init!.signal!.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    const { result, props } = mount();
    act(() => { void result.current.load({ url: '/B', label: 'load' }); });
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000); });
    expect(result.current.loading).toBe(false);
    expect(props.error).toHaveBeenCalledWith(expect.stringContaining('15 秒'));
  });
});
