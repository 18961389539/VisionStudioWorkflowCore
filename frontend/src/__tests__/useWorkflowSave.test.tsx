import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useWorkflowSave } from '../useWorkflowSave';
import type { DocumentBaseline } from '../documentSync';

const snapshot = JSON.stringify({ id: 'A', name: 'Original', nodes: [], edges: [] });
const response = (status = 204, revision = 'r2', body: unknown = {}) => ({
  ok: status >= 200 && status < 300, status,
  headers: new Headers({ 'X-Workflow-Revision': revision }), json: async () => body,
}) as Response;

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

function mount(baseline: DocumentBaseline = { id: 'A', revision: 'r1' }) {
  const generationRef = { current: 1 };
  const baselineRef = { current: baseline };
  const onSaved = vi.fn();
  const confirmOverwrite = vi.fn(async () => true);
  const messages = { info: vi.fn(), warning: vi.fn(), error: vi.fn(), success: vi.fn() };
  const props = { workflowId: 'A', snapshot, generationRef, baselineRef, onSaved, confirmOverwrite, messages };
  return { ...renderHook((next) => useWorkflowSave(next), { initialProps: props }), props };
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('工作流保存生命周期', () => {
  it('保存成功绑定提交快照和新修订，只有当前内容已保存才允许继续', async () => {
    const fetchMock = vi.fn(async () => response());
    vi.stubGlobal('fetch', fetchMock);
    const { result, props } = mount();
    let ok: boolean;
    await act(async () => { ok = await result.current.save(); });
    expect(ok!).toBe(true);
    expect(props.onSaved).toHaveBeenCalledWith(snapshot);
    expect(props.baselineRef.current).toEqual({ id: 'A', revision: 'r2' });
    expect(fetchMock.mock.calls[0]).toEqual(['/api/workflows/A?expectedRevision=r1', expect.objectContaining({ method: 'PUT', body: snapshot })]);
    expect(result.current.saving).toBe(false);
  });

  it('同一事件内重复保存也只发送一个请求', async () => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn(() => pending.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    const save = result.current.save;
    let first!: Promise<boolean>;
    act(() => { first = save(); });
    expect(result.current.saving).toBe(true);
    await expect(save()).resolves.toBe(false);
    expect(fetchMock).toHaveBeenCalledOnce();
    await act(async () => { pending.resolve(response()); await first; });
    expect(result.current.saving).toBe(false);
  });

  it.each(['', '   '])('已有流程缺少有效修订号时不允许覆盖写入：%j', async (revision) => {
    const fetchMock = vi.fn(async () => response(200, revision));
    vi.stubGlobal('fetch', fetchMock);
    const { result, props } = mount({ id: 'A', revision });
    await act(async () => { expect(await result.current.save()).toBe(false); });
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock.mock.calls[0]).toEqual(['/api/workflows/A', expect.anything()]);
    expect(props.confirmOverwrite).not.toHaveBeenCalled();
    expect(props.onSaved).not.toHaveBeenCalled();
    expect(props.messages.error).toHaveBeenCalledWith(expect.stringContaining('修订'));
  });

  it('保存成功但缺少修订头时提示基线未知，下次保存先重新探测并确认', async () => {
    const saved = response();
    saved.headers.delete('X-Workflow-Revision');
    const fetchMock = vi.fn().mockResolvedValueOnce(saved).mockResolvedValueOnce(response(200, 'r3')).mockResolvedValueOnce(response(204, 'r4'));
    vi.stubGlobal('fetch', fetchMock);
    const { result, props } = mount();
    await act(async () => { expect(await result.current.save()).toBe(true); });
    expect(props.onSaved).toHaveBeenCalledWith(snapshot);
    expect(props.baselineRef.current).toBeUndefined();
    expect(props.messages.warning).toHaveBeenCalledWith(expect.stringContaining('修订'));
    expect(props.messages.success).not.toHaveBeenCalled();
    await act(async () => { expect(await result.current.save()).toBe(true); });
    expect(fetchMock.mock.calls[1][0]).toBe('/api/workflows/A');
    expect(props.confirmOverwrite).toHaveBeenCalledWith('A');
    expect(fetchMock.mock.calls[2][0]).toBe('/api/workflows/A?expectedRevision=r3');
    expect(props.baselineRef.current).toEqual({ id: 'A', revision: 'r4' });
  });

  it.each(['重命名', '参数编辑'])('保存期间%s保留新增修改并阻止自动继续，后续保存沿用新基线', async (kind) => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(response(204, 'r3'));
    vi.stubGlobal('fetch', fetchMock);
    const { result, rerender, props } = mount();
    const continueAction = vi.fn();
    let first!: Promise<void>;
    act(() => { first = result.current.save().then((ok) => { if (ok) continueAction(); }); });
    const changed = kind === '重命名'
      ? JSON.stringify({ ...JSON.parse(snapshot), name: 'New name' })
      : JSON.stringify({ ...JSON.parse(snapshot), nodes: [{ id: 'n1', type: 'image.threshold', parameters: { threshold: 200 } }] });
    rerender({ ...props, snapshot: changed });
    await act(async () => { pending.resolve(response()); await first; });
    expect(continueAction).not.toHaveBeenCalled();
    expect(props.onSaved).toHaveBeenLastCalledWith(snapshot);
    expect(props.baselineRef.current?.revision).toBe('r2');
    expect(props.messages.warning).toHaveBeenCalledWith(expect.stringContaining('新增的修改仍未保存'));
    await act(async () => { expect(await result.current.save()).toBe(true); });
    expect(fetchMock.mock.calls[1]).toEqual(['/api/workflows/A?expectedRevision=r2', expect.objectContaining({ body: changed })]);
    expect(props.onSaved).toHaveBeenLastCalledWith(changed);
  });

  it('同 ID 文档重新加载也不能被旧保存响应污染', async () => {
    const pending = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn(() => pending.promise));
    const { result, props } = mount();
    let first!: Promise<boolean>;
    act(() => { first = result.current.save(); });
    props.generationRef.current += 2; // A → B → A
    props.baselineRef.current = { id: 'A', revision: 'loaded-r9' };
    await act(async () => { pending.resolve(response()); expect(await first).toBe(false); });
    expect(props.onSaved).not.toHaveBeenCalled();
    expect(props.baselineRef.current.revision).toBe('loaded-r9');
  });

  it('基线探测期间切换文档后停止，不再提交旧文档', async () => {
    const probe = deferred<Response>();
    const fetchMock = vi.fn(() => probe.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { result, rerender, props } = mount(undefined);
    props.baselineRef.current = undefined;
    let save!: Promise<boolean>;
    act(() => { save = result.current.save(); });
    props.generationRef.current += 1;
    rerender({ ...props, workflowId: 'B' });
    await act(async () => { probe.resolve(response(200)); expect(await save).toBe(false); });
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(props.confirmOverwrite).not.toHaveBeenCalled();
    expect(props.onSaved).not.toHaveBeenCalled();
  });

  it('覆盖确认期间切换文档，即使随后确认也不会发送旧写入', async () => {
    const confirmation = deferred<boolean>();
    const fetchMock = vi.fn(async () => response(200));
    vi.stubGlobal('fetch', fetchMock);
    const { result, rerender, props } = mount();
    props.baselineRef.current = undefined;
    props.confirmOverwrite.mockImplementation(() => confirmation.promise);
    let save!: Promise<boolean>;
    await act(async () => { save = result.current.save(); });
    expect(props.confirmOverwrite).toHaveBeenCalledWith('A');
    props.generationRef.current += 1;
    rerender({ ...props, workflowId: 'B' });
    await act(async () => { confirmation.resolve(true); expect(await save).toBe(false); });
    expect(fetchMock).toHaveBeenCalledOnce();
  });

  it('远端不存在时以明确的新建基线保存', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(404)).mockResolvedValueOnce(response());
    vi.stubGlobal('fetch', fetchMock);
    const { result, props } = mount();
    props.baselineRef.current = undefined;
    await act(async () => { expect(await result.current.save()).toBe(true); });
    expect(fetchMock.mock.calls[1][0]).toBe('/api/workflows/A?expectedRevision=');
    expect(props.confirmOverwrite).not.toHaveBeenCalled();
  });

  it('未知基线探测失败或取消覆盖时不写入', async () => {
    const fetchMock = vi.fn(async () => response(503));
    vi.stubGlobal('fetch', fetchMock);
    const { result, props } = mount();
    props.baselineRef.current = undefined;
    await act(async () => { expect(await result.current.save()).toBe(false); });
    expect(fetchMock).toHaveBeenCalledOnce();
    fetchMock.mockImplementation(async () => response(200));
    props.confirmOverwrite.mockResolvedValue(false);
    await act(async () => { expect(await result.current.save()).toBe(false); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(props.onSaved).not.toHaveBeenCalled();
  });

  it('版本冲突保留基线并呈现业务原因和关联号', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response(409, '', { detail: 'Revision mismatch', code: 'revision_conflict', correlationId: 'trace-123' })));
    const { result, props } = mount();
    await act(async () => { expect(await result.current.save()).toBe(false); });
    expect(props.baselineRef.current?.revision).toBe('r1');
    expect(props.onSaved).not.toHaveBeenCalled();
    expect(props.messages.error).toHaveBeenCalledWith(expect.stringContaining('trace-123'));
    expect(result.current.saving).toBe(false);
  });

  it('网络失败不会声称确定未提交，且释放保存闸门', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('offline')));
    const { result, props } = mount();
    await act(async () => { expect(await result.current.save()).toBe(false); });
    expect(props.messages.error).toHaveBeenCalledWith(expect.stringContaining('后端可能已收到'));
    expect(props.onSaved).not.toHaveBeenCalled();
    expect(result.current.saving).toBe(false);
  });

  it.each(['探测', '写入'])('卸载后到达的%s响应不会继续写入或更新状态', async (phase) => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn(() => pending.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { result, unmount, props } = mount();
    if (phase === '探测') props.baselineRef.current = undefined;
    let save!: Promise<boolean>;
    act(() => { save = result.current.save(); });
    unmount();
    pending.resolve(response(200));
    expect(await save).toBe(false);
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(props.onSaved).not.toHaveBeenCalled();
    expect(props.confirmOverwrite).not.toHaveBeenCalled();
  });

  it('修订延迟落定时仍承认保存成功，并告知版本基线状态', async () => {
    const saved = response();
    saved.headers.set('X-Workflow-Revision-Deferred', 'true');
    vi.stubGlobal('fetch', vi.fn(async () => saved));
    const { result, props } = mount();
    await act(async () => { expect(await result.current.save()).toBe(true); });
    expect(props.onSaved).toHaveBeenCalledWith(snapshot);
    expect(props.messages.warning).toHaveBeenCalledWith(expect.stringContaining('版本基线记录延迟落定'));
  });
});
