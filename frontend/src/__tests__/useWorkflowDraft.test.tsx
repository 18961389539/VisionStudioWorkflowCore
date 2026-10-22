import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useWorkflowDraft } from '../useWorkflowDraft';
import { DRAFT_STORAGE_KEY, readDraft } from '../draft';

const workflow = { id: 'A', name: 'Original', nodes: [], edges: [] };
const snapshot = JSON.stringify(workflow);
const changed = JSON.stringify({ ...workflow, name: 'Latest name' });
const priorDraft = { version: 1, savedAt: '2026-10-20T00:00:00Z', workflowId: 'old', workflowName: 'Old', demoKey: 'linear', payload: { ...workflow, id: 'old' } };

function mount(overrides: Partial<Parameters<typeof useWorkflowDraft>[0]> = {}) {
  const props = { snapshot, demoKey: 'linear', dirty: true, recoveryPending: false, onFailure: vi.fn(), ...overrides };
  return { ...renderHook((next) => useWorkflowDraft(next), { initialProps: props }), props };
}

beforeEach(() => { vi.useFakeTimers(); window.localStorage.clear(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.useRealTimers(); });

describe('工作流草稿生命周期', () => {
  it('连续编辑防抖，最终写入最新快照及一致的文档身份', () => {
    const { rerender, props } = mount();
    act(() => { vi.advanceTimersByTime(300); });
    expect(readDraft()).toBeUndefined();
    rerender({ ...props, snapshot: changed });
    act(() => { vi.advanceTimersByTime(599); });
    expect(readDraft()).toBeUndefined();
    act(() => { vi.advanceTimersByTime(1); });
    expect(readDraft()).toMatchObject({ workflowId: 'A', workflowName: 'Latest name', payload: { name: 'Latest name' } });
  });

  it.each(['beforeunload', 'pagehide'])('%s 在防抖结束前立即写入最后修改', (eventName) => {
    const { rerender, props } = mount();
    rerender({ ...props, snapshot: changed });
    const event = new Event(eventName, { cancelable: true });
    act(() => { window.dispatchEvent(event); });
    expect(readDraft()?.payload.name).toBe('Latest name');
    expect(vi.getTimerCount()).toBe(0);
    if (eventName === 'beforeunload') expect(event.defaultPrevented).toBe(true);
  });

  it('页面进入后台立即写入，前台切换不触发提前写入', () => {
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    mount();
    act(() => { document.dispatchEvent(new Event('visibilitychange')); });
    expect(readDraft()).toBeUndefined();
    visibility.mockReturnValue('hidden');
    act(() => { document.dispatchEvent(new Event('visibilitychange')); });
    expect(readDraft()?.payload).toEqual(workflow);
  });

  it('真正卸载时写入最新内容并取消防抖计时器', () => {
    const { unmount, rerender, props } = mount();
    rerender({ ...props, snapshot: changed });
    unmount();
    expect(readDraft()?.payload.name).toBe('Latest name');
    expect(vi.getTimerCount()).toBe(0);
  });

  it('恢复入口未决时，定时器、离开事件和卸载都不会覆盖待恢复草稿', () => {
    window.localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(priorDraft));
    const { unmount } = mount({ recoveryPending: true });
    act(() => { vi.advanceTimersByTime(1000); window.dispatchEvent(new Event('pagehide')); });
    unmount();
    expect(readDraft()?.workflowId).toBe('old');
  });

  it('恢复入口关闭后保留原草稿，直到新草稿写入成功', () => {
    window.localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(priorDraft));
    const { rerender, props } = mount({ recoveryPending: true });
    rerender({ ...props, recoveryPending: false });
    expect(readDraft()?.workflowId).toBe('old');
    act(() => { vi.advanceTimersByTime(600); });
    expect(readDraft()?.workflowId).toBe('A');
  });

  it('待恢复草稿存在时，当前文档有新编辑仍须提示关闭确认', () => {
    window.localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(priorDraft));
    mount({ recoveryPending: true });
    const event = new Event('beforeunload', { cancelable: true });
    act(() => { window.dispatchEvent(event); });
    expect(event.defaultPrevented).toBe(true);
    expect(readDraft()?.workflowId).toBe('old');
    expect(vi.getTimerCount()).toBe(0);
  });

  it('文档保存成功后清除草稿，旧定时器和卸载不能将它复活', () => {
    const { rerender, unmount, props } = mount();
    act(() => { vi.advanceTimersByTime(600); });
    expect(readDraft()).toBeDefined();
    rerender({ ...props, dirty: false });
    act(() => { vi.advanceTimersByTime(1000); window.dispatchEvent(new Event('pagehide')); });
    unmount();
    expect(readDraft()).toBeUndefined();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('存储失败每个失败周期只提示一次，恢复成功后可重新提示', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota'); });
    const { props, rerender } = mount();
    act(() => { vi.advanceTimersByTime(600); window.dispatchEvent(new Event('pagehide')); });
    expect(props.onFailure).toHaveBeenCalledOnce();
    setItem.mockRestore();
    act(() => { window.dispatchEvent(new Event('pagehide')); });
    expect(readDraft()).toBeDefined();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota'); });
    rerender({ ...props, snapshot: changed });
    act(() => { vi.advanceTimersByTime(600); });
    expect(props.onFailure).toHaveBeenCalledTimes(2);
  });

  it('与内容无关的重渲染不推迟草稿写入', () => {
    const { rerender, props } = mount();
    act(() => { vi.advanceTimersByTime(300); });
    rerender({ ...props, onFailure: vi.fn() });
    act(() => { vi.advanceTimersByTime(300); });
    expect(readDraft()?.payload).toEqual(workflow);
  });
});
