import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useWorkflowChanges } from '../useWorkflowChanges';
import type { WorkflowPayload } from '../types';

const source: WorkflowPayload = { id: 'A', name: 'Original', nodes: [{ id: 'n', type: 'image.synthetic', parameters: { width: 320 } }], edges: [] };
const target = { ...source, id: 'B', name: 'Loaded' };
const snapshot = JSON.stringify(source);
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
function mount(dirty = true) {
  const props = { snapshot, dirty, generationRef: { current: 1 }, save: vi.fn(async () => true), apply: vi.fn(), warning: vi.fn() };
  return { ...renderHook((value) => useWorkflowChanges(value), { initialProps: props }), props };
}
afterEach(cleanup);

describe('流程切换确认', () => {
  it('无修改时直接执行，有修改时等待确认且取消会返回 false', async () => {
    const { result, props, rerender } = mount(false);
    const first = vi.fn();
    await act(async () => { expect(await result.current.request('first', first)).toBe(true); });
    expect(first).toHaveBeenCalledOnce();
    rerender({ ...props, dirty: true });
    let change!: Promise<boolean>;
    const next = vi.fn();
    act(() => { change = result.current.request('next', next); });
    expect(result.current.pending?.label).toBe('next');
    act(() => result.current.cancel());
    expect(await change).toBe(false);
    expect(next).not.toHaveBeenCalled();
  });

  it('只有明确丢弃才应用替换，候选内容在确认期间保持快照', async () => {
    const { result, props } = mount();
    const candidate = { ...target, name: 'Reviewed' };
    let completed!: Promise<boolean>;
    act(() => { completed = result.current.prepare('载入配方').apply(candidate); });
    candidate.name = 'changed behind confirmation';
    expect(props.apply).not.toHaveBeenCalled();
    act(() => result.current.discard());
    expect(await completed).toBe(true);
    expect(props.apply).toHaveBeenCalledWith(expect.objectContaining({ name: 'Reviewed' }), expect.objectContaining({ mode: 'replace' }));
    expect(result.current.pending).toBeUndefined();
  });

  it('保存失败保留确认与修改，成功后才继续', async () => {
    const { result, props } = mount();
    props.save.mockResolvedValueOnce(false);
    const action = vi.fn();
    let completed!: Promise<boolean>;
    act(() => { completed = result.current.request('载入', action); });
    await act(async () => { await result.current.saveAndContinue(); });
    expect(result.current.pending).toBeDefined();
    expect(action).not.toHaveBeenCalled();
    await act(async () => { await result.current.saveAndContinue(); });
    expect(await completed).toBe(true);
    expect(action).toHaveBeenCalledOnce();
  });

  it('保存等待期间同步阻止重复确认、取消、丢弃与替换确认对象', async () => {
    const saved = deferred<boolean>();
    const { result, props } = mount();
    props.save.mockReturnValue(saved.promise);
    let completed!: Promise<boolean>;
    act(() => { completed = result.current.prepare('original').apply(target); });
    let saving!: Promise<void>;
    act(() => { saving = result.current.saveAndContinue(); result.current.cancel(); result.current.discard(); void result.current.saveAndContinue(); });
    const blocked = result.current.prepare('blocked');
    expect(await blocked.apply(target)).toBe(false);
    blocked.cancel(); // 被拒绝的新入口不能取消原有确认。
    expect(result.current.pending?.label).toBe('original');
    expect(props.save).toHaveBeenCalledOnce();
    await act(async () => { saved.resolve(true); await saving; });
    expect(await completed).toBe(true);
    expect(props.apply).toHaveBeenCalledOnce();
  });

  it('保存期间又有编辑时放弃旧切换，不能覆盖新增内容', async () => {
    const saved = deferred<boolean>();
    const { result, props, rerender } = mount();
    props.save.mockReturnValue(saved.promise);
    let completed!: Promise<boolean>;
    act(() => { completed = result.current.prepare('load').apply(target); void result.current.saveAndContinue(); });
    rerender({ ...props, snapshot: JSON.stringify({ ...source, name: 'new edit' }) });
    await act(async () => { saved.resolve(false); });
    expect(await completed).toBe(false);
    expect(props.apply).not.toHaveBeenCalled();
    expect(result.current.pending).toBeUndefined();
  });

  it('新的确认取消先前确认，旧异步入口只能得到未应用', async () => {
    const { result, props } = mount();
    let first!: Promise<boolean>; let second!: Promise<boolean>;
    act(() => { first = result.current.prepare('first').apply(target); second = result.current.prepare('second').apply({ ...target, name: 'second' }); });
    expect(await first).toBe(false);
    act(() => result.current.discard());
    expect(await second).toBe(true);
    expect(props.apply).toHaveBeenCalledTimes(1);
    expect(props.apply.mock.calls[0][0].name).toBe('second');
  });

  it('关闭来源入口取消确认，保存回调稍后返回也不再切换', async () => {
    const saved = deferred<boolean>();
    const { result, props } = mount();
    props.save.mockReturnValue(saved.promise);
    const change = result.current.prepare('load');
    let completed!: Promise<boolean>;
    act(() => { completed = change.apply(target); void result.current.saveAndContinue(); });
    act(() => change.cancel());
    expect(await completed).toBe(false);
    await act(async () => { saved.resolve(true); });
    expect(props.apply).not.toHaveBeenCalled();
  });

  it('卸载终结等待并忽略保存成功', async () => {
    const saved = deferred<boolean>();
    const { result, props, unmount } = mount();
    props.save.mockReturnValue(saved.promise);
    let completed!: Promise<boolean>;
    act(() => { completed = result.current.prepare('load').apply(target); void result.current.saveAndContinue(); });
    unmount();
    expect(await completed).toBe(false);
    await act(async () => { saved.resolve(true); });
    expect(props.apply).not.toHaveBeenCalled();
  });
});

describe('准备流程与编辑归属', () => {
  it('同内容 A → B → A 也不能重新使用旧载入结果', async () => {
    const { result, props, rerender } = mount(false);
    const change = result.current.prepare('old');
    props.generationRef.current += 2;
    rerender({ ...props });
    expect(await change.apply(target)).toBe(false);
    expect(props.apply).not.toHaveBeenCalled();
  });

  it.each(['name', 'parameters', 'position'])('准备期间 %s 变化拒绝应用，内容无关重渲染仍允许', async (kind) => {
    const { result, props, rerender } = mount(false);
    const change = result.current.prepare('old');
    const updated = structuredClone(source);
    if (kind === 'name') updated.name = 'renamed';
    else if (kind === 'parameters') updated.nodes[0].parameters = { width: 640 };
    else updated.nodes[0].position = { x: 20, y: 30 };
    rerender({ ...props, snapshot: JSON.stringify(updated) });
    expect(change.isSameDocument()).toBe(true);
    expect(change.isCurrent()).toBe(false);
    expect(await change.apply(target)).toBe(false);
    const current = result.current.prepare('current');
    rerender({ ...props, snapshot: JSON.stringify(updated) });
    await act(async () => { expect(await current.apply(target)).toBe(true); });
  });

  it('调优/提取是同一文档的编辑，保留当前未保存内容且不声称已保存', async () => {
    const { result, props } = mount();
    const change = result.current.prepare('tuning', 'edit');
    await act(async () => { expect(await change.apply({ ...source, name: 'tuned' }, { markClean: true, revision: 'wrong' })).toBe(true); });
    expect(result.current.pending).toBeUndefined();
    expect(props.save).not.toHaveBeenCalled();
    expect(props.apply).toHaveBeenCalledWith(expect.objectContaining({ id: 'A' }), expect.objectContaining({ mode: 'edit', markClean: false, revision: undefined }));
    expect(await change.apply(source)).toBe(false);
  });

  it.each([null, { ...target, nodes: [null] }, { ...target, edges: [{}] }, { ...target, nodes: [{ id: 'n', type: 'x', position: { x: 'bad', y: 0 } }] }])('不完整文档不能触发部分应用：%j', async (value) => {
    const { result, props } = mount(false);
    expect(await result.current.prepare('bad').apply(value as WorkflowPayload)).toBe(false);
    expect(props.apply).not.toHaveBeenCalled();
    expect(props.generationRef.current).toBe(1);
  });

  it('编辑不能冒用其它文档身份，明确加载的后端修订仍可绑定', async () => {
    const { result, props } = mount(false);
    expect(await result.current.prepare('bad edit', 'edit').apply(target)).toBe(false);
    await act(async () => { await result.current.prepare('stored').apply(target, { markClean: true, revision: 'backend-r2' }); });
    expect(props.apply).toHaveBeenCalledWith(target, expect.objectContaining({ markClean: true, revision: 'backend-r2' }));
  });

  it('应用或保存异常可定位，确认不会永久忙碌或执行后续动作', async () => {
    const { result, props } = mount();
    props.save.mockRejectedValueOnce(new Error('save failure'));
    let completed!: Promise<boolean>;
    act(() => { completed = result.current.prepare('load').apply(target); });
    await act(async () => { await result.current.saveAndContinue(); });
    expect(result.current.busy).toBe(false);
    expect(result.current.pending).toBeDefined();
    props.apply.mockImplementationOnce(() => { throw new Error('bad module metadata'); });
    act(() => result.current.discard());
    expect(await completed).toBe(false);
    expect(props.warning).toHaveBeenCalledWith(expect.stringContaining('bad module metadata'));
  });
});
