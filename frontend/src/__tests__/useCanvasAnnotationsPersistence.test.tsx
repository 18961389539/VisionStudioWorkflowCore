import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { StrictMode, type ReactNode } from 'react';
import type { Node } from '@xyflow/react';
import { restoreCanvasAnnotations } from '../canvasAnnotations';
import { useCanvasAnnotationsPersistence } from '../useCanvasAnnotationsPersistence';

const nodesAt = (x: number): Node[] => [
  { id: 'note', type: 'canvasNote', position: { x, y: 20 }, data: { content: 'note' } }
];
const savedX = (id: string) => restoreCanvasAnnotations(id, [])[0]?.position.x;

function mount() {
  const props = { workflowId: 'A', nodes: nodesAt(0), dragging: false, onFailure: vi.fn() };
  return { ...renderHook(useCanvasAnnotationsPersistence, { initialProps: props }), props };
}

beforeEach(() => { vi.useFakeTimers(); localStorage.clear(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.useRealTimers(); });

it('does no storage I/O for 120 drag frames or a held drag, then saves the final position once', () => {
  const getItem = vi.spyOn(Storage.prototype, 'getItem');
  const setItem = vi.spyOn(Storage.prototype, 'setItem');
  const { rerender, props, unmount } = mount();
  for (let x = 1; x <= 120; x++) {
    rerender({ ...props, nodes: nodesAt(x), dragging: true });
    act(() => { vi.advanceTimersByTime(16); });
  }
  act(() => { vi.advanceTimersByTime(1000); });
  expect(getItem).not.toHaveBeenCalled();
  expect(setItem).not.toHaveBeenCalled();
  rerender({ ...props, nodes: nodesAt(120) });
  act(() => { vi.advanceTimersByTime(599); });
  expect(setItem).not.toHaveBeenCalled();
  act(() => { vi.advanceTimersByTime(1); });
  expect(setItem).toHaveBeenCalledOnce();
  expect(savedX('A')).toBe(120);
  unmount();
  expect(setItem).toHaveBeenCalledOnce();
});

it('debounces ordinary annotation edits and ignores selection/status-only changes when saving', () => {
  const setItem = vi.spyOn(Storage.prototype, 'setItem');
  const { rerender, props } = mount();
  act(() => { vi.advanceTimersByTime(300); });
  rerender({ ...props, nodes: nodesAt(50) });
  act(() => { vi.advanceTimersByTime(599); });
  expect(setItem).not.toHaveBeenCalled();
  act(() => { vi.advanceTimersByTime(1); });
  expect(savedX('A')).toBe(50);
  rerender({ ...props, nodes: nodesAt(50).map((node) => ({ ...node, selected: true, data: { ...node.data, status: 'running' } })) });
  act(() => { vi.advanceTimersByTime(600); });
  expect(setItem).toHaveBeenCalledOnce();
});

it.each(['beforeunload', 'pagehide', 'hidden', 'unmount'])('flushes the latest held-drag position on %s', (event) => {
  const { rerender, props, unmount } = mount();
  rerender({ ...props, nodes: nodesAt(75), dragging: true });
  if (event === 'unmount') unmount();
  else act(() => {
    if (event === 'hidden') {
      vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
      document.dispatchEvent(new Event('visibilitychange'));
    } else window.dispatchEvent(new Event(event));
  });
  expect(savedX('A')).toBe(75);
  expect(vi.getTimerCount()).toBe(0);
});

it('flushes A under its original ID on A→B→A and cannot resurrect an old timer', () => {
  const { rerender, props, result } = mount();
  rerender({ ...props, nodes: nodesAt(11) });
  const b = result.current('B', []);
  expect(savedX('A')).toBe(11);
  expect(b).toEqual([]);
  rerender({ ...props, workflowId: 'B', nodes: nodesAt(22) });
  const a = result.current('A', []);
  expect(savedX('B')).toBe(22);
  expect(a[0].position.x).toBe(11);
  rerender({ ...props, nodes: a });
  act(() => { vi.advanceTimersByTime(1000); });
  expect(savedX('A')).toBe(11);
  expect(savedX('B')).toBe(22);
});

it('save-as copies the live annotations to the new ID without rehydrating another document', () => {
  const { rerender, props } = mount();
  rerender({ ...props, nodes: nodesAt(30) });
  rerender({ ...props, workflowId: 'B', nodes: nodesAt(30) });
  expect(savedX('A')).toBe(30);
  act(() => { vi.advanceTimersByTime(600); });
  expect(savedX('B')).toBe(30);
  rerender({ ...props, workflowId: 'B', nodes: [] });
  act(() => { vi.advanceTimersByTime(600); });
  expect(savedX('B')).toBeUndefined();
  expect(savedX('A')).toBe(30);
});

it('same-ID replacement uses current annotations, including deletion, before the debounce fires', () => {
  const { rerender, props, result } = mount();
  act(() => { vi.advanceTimersByTime(600); });
  rerender({ ...props, nodes: nodesAt(45) });
  expect(result.current('A', [])[0].position.x).toBe(45);
  rerender({ ...props, nodes: [] });
  expect(result.current('A', [])).toEqual([]);
});

it('retains failed document snapshots for retry and restores live annotations even if storage fails', () => {
  const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota'); });
  const { rerender, props, result } = mount();
  rerender({ ...props, nodes: nodesAt(60) });
  expect(result.current('A', [])[0].position.x).toBe(60);
  rerender({ ...props, workflowId: 'B', nodes: nodesAt(70) });
  act(() => { vi.advanceTimersByTime(600); window.dispatchEvent(new Event('pagehide')); });
  expect(props.onFailure).toHaveBeenCalledOnce();
  setItem.mockRestore();
  act(() => { window.dispatchEvent(new Event('pagehide')); });
  expect(savedX('A')).toBe(60);
  expect(savedX('B')).toBe(70);
});

it('StrictMode cleanup cannot duplicate writes or leave timers after real unmount', () => {
  const setItem = vi.spyOn(Storage.prototype, 'setItem');
  const { rerender, unmount } = renderHook(useCanvasAnnotationsPersistence, {
    initialProps: { workflowId: 'A', nodes: nodesAt(0), dragging: false, onFailure: vi.fn() },
    wrapper: ({ children }: { children: ReactNode }) => <StrictMode>{children}</StrictMode>,
  });
  setItem.mockClear();
  rerender({ workflowId: 'A', nodes: nodesAt(90), dragging: true, onFailure: vi.fn() });
  unmount();
  expect(savedX('A')).toBe(90);
  expect(setItem).toHaveBeenCalledOnce();
  expect(vi.getTimerCount()).toBe(0);
});
