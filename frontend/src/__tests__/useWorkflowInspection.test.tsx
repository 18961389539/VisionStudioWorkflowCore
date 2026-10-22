import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import { useWorkflowInspection } from '../useWorkflowInspection';
import type { NodeCatalogItem, WorkflowPayload } from '../types';
import { deferred, jsonResponse } from './fixtures/tuning';

const catalog: NodeCatalogItem[] = [{ type: 'test.source', displayName: 'Source', category: 'Test',
  inputs: [], outputs: [{ name: 'image', dataType: 'Image' }], parameters: [] }];
const nodes: Node[] = [{ id: 'source', position: { x: 0, y: 0 }, data: { typeKey: 'test.source', inputs: [], outputs: catalog[0].outputs } }];
const workflow: WorkflowPayload = { id: 'inspection', name: 'Inspection', nodes: [{ id: 'source', type: 'test.source' }], edges: [] };
const valid = { valid: true, orderedNodeIds: ['source'], pipelineSegmentCount: 1, structuredRegionCount: 0 };
const dsl = { Id: 'compiled-inspection', Version: 1, DataType: 'VisionWorkflowData', Steps: [] };
const compiled = { dslJson: JSON.stringify(dsl) };
type Action = 'validate' | 'compile';
const invoke = (value: ReturnType<typeof useWorkflowInspection>, action: Action) => action === 'validate' ? value.validate() : value.inspectDsl();
const responseFor = (action: Action) => action === 'validate' ? valid : compiled;
const tick = async (ms: number) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };
function mount() {
  const props = { workflow, nodes, edges: [], catalog, generationRef: { current: 0 }, onValidated: vi.fn(),
    messages: { warning: vi.fn(), success: vi.fn(), error: vi.fn() } };
  return { ...renderHook((value) => useWorkflowInspection(value), { initialProps: props }), props };
}
beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('fetch', vi.fn(async (url: string) => jsonResponse(url.endsWith('/validate') ? valid : compiled)));
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('workflow validation and DSL ownership', () => {
  it('keeps validation feedback, summary and formatted core DSL', async () => {
    const { result, props } = mount();
    await act(async () => { expect(await result.current.validate()).toBe(true); });
    expect(result.current.validationState).toMatchObject({ status: 'ok', summary: expect.stringContaining('1 个节点') });
    expect(result.current.needsRevalidate).toBe(false);
    expect(props.onValidated).toHaveBeenCalledOnce();
    await act(async () => { expect(await result.current.inspectDsl()).toBe(true); });
    expect(result.current.compiledDsl).toBe(JSON.stringify(dsl, null, 2));
  });

  it('keeps client parameter issues even when the server validates successfully', async () => {
    const { result, props, rerender } = mount();
    rerender({ ...props, catalog: [{ ...catalog[0], parameters: [{ name: 'size', label: 'Size', type: 'number', min: 1 }] }],
      nodes: [{ ...nodes[0], data: { ...nodes[0].data, parameters: { size: -1 } } }] });
    await act(async () => { await result.current.validate(); });
    expect(result.current.validationState.status).toBe('issues');
    expect(result.current.validationIssues.some(issue => issue.source === 'client')).toBe(true);
    expect(props.messages.success).not.toHaveBeenCalled();
  });

  it('old callbacks submit the current document instead of captured parameters', async () => {
    const { result, props, rerender } = mount();
    const validate = result.current.validate;
    rerender({ ...props, workflow: { ...workflow, name: 'Current edit' } });
    await act(async () => { await validate(); });
    expect(JSON.parse(String(vi.mocked(fetch).mock.calls[0][1]!.body)).name).toBe('Current edit');
  });

  describe.each(['validate', 'compile'] as const)('%s requests', (action) => {
    it.each(['edit', 'reload', 'catalog'])('rejects an old body after %s without feedback or opening results', async (change) => {
      const body = deferred<unknown>(); let signal: AbortSignal | null | undefined;
      vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
        signal = init?.signal; return { ...jsonResponse({}), json: () => body.promise };
      }));
      const { result, props, rerender } = mount();
      await act(async () => { void invoke(result.current, action); });
      if (change === 'reload') props.generationRef.current += 1;
      rerender({ ...props, workflow: change === 'edit' ? { ...workflow, name: 'Changed' } : workflow,
        catalog: change === 'catalog' ? [...catalog] : catalog });
      expect(signal?.aborted).toBe(true);
      await act(async () => { body.resolve(responseFor(action)); });
      expect(result.current.validationState.status).toBe('unvalidated');
      expect(result.current.compiledDsl).toBeUndefined();
      expect(props.onValidated).not.toHaveBeenCalled();
      expect(props.messages.success).not.toHaveBeenCalled();
    });

    it('old finally cannot release a newer request after editing', async () => {
      const old = deferred<unknown>(); const next = deferred<Response>(); let calls = 0;
      vi.stubGlobal('fetch', vi.fn(async () => ++calls === 1 ? { ...jsonResponse({}), json: () => old.promise } : next.promise));
      const { result, props, rerender } = mount();
      await act(async () => { void invoke(result.current, action); });
      rerender({ ...props, workflow: { ...workflow, name: 'New edit' } });
      act(() => { void invoke(result.current, action); });
      await act(async () => { old.resolve(responseFor(action)); });
      expect(action === 'validate' ? result.current.validationBusy : result.current.compileBusy).toBe(true);
      await act(async () => { next.resolve(jsonResponse(responseFor(action))); });
      expect(action === 'validate' ? result.current.validationBusy : result.current.compileBusy).toBe(false);
      expect(action === 'validate' ? result.current.validationState.status === 'ok' : Boolean(result.current.compiledDsl)).toBe(true);
    });

    it('preserves backend ProblemDetails instead of hiding its reason and correlation ID', async () => {
      vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ detail: 'compiler unavailable', code: 'compile-unavailable', correlationId: 'compile-trace' }, 503)));
      const { result, props } = mount();
      await act(async () => { await invoke(result.current, action); });
      if (action === 'validate') expect(result.current.validationIssues[0]?.message).toMatch(/compiler unavailable.*compile-unavailable.*compile-trace/);
      else expect(props.messages.error).toHaveBeenCalledWith(expect.stringMatching(/compiler unavailable.*compile-unavailable.*compile-trace/));
    });

    it.each(['response', 'body'])('settles at 15 seconds when %s ignores abort and allows retry', async (stage) => {
      const response = deferred<Response>(); const body = deferred<unknown>(); let calls = 0; let settled: boolean | undefined;
      vi.stubGlobal('fetch', vi.fn(async () => ++calls > 1 ? jsonResponse(responseFor(action)) : stage === 'response'
        ? response.promise : { ...jsonResponse({}), json: () => body.promise }));
      const { result, props } = mount();
      await act(async () => { void invoke(result.current, action).then(value => { settled = value; }); });
      await tick(14_999);
      expect(action === 'validate' ? result.current.validationBusy : result.current.compileBusy).toBe(true);
      expect(settled).toBeUndefined();
      await tick(1);
      expect(action === 'validate' ? result.current.validationBusy : result.current.compileBusy).toBe(false);
      expect(settled).toBe(false);
      if (action === 'validate') expect(result.current.validationIssues[0]?.message).toContain('15 秒');
      else expect(props.messages.error).toHaveBeenCalledWith(expect.stringContaining('15 秒'));
      await act(async () => { await invoke(result.current, action); });
      const notifications = props.onValidated.mock.calls.length;
      await act(async () => { response.resolve(jsonResponse(responseFor(action))); body.resolve(responseFor(action)); });
      expect(props.onValidated).toHaveBeenCalledTimes(notifications);
      expect(action === 'validate' ? result.current.validationState.status === 'ok' : Boolean(result.current.compiledDsl)).toBe(true);
    });

    it('ignores late results after reset and unmount', async () => {
      const pending = deferred<Response>();
      vi.stubGlobal('fetch', vi.fn(() => pending.promise));
      const { result, props, unmount } = mount();
      act(() => { void invoke(result.current, action); result.current.resetInspection(); });
      unmount();
      await act(async () => { pending.resolve(jsonResponse(responseFor(action))); });
      expect(props.onValidated).not.toHaveBeenCalled();
      expect(props.messages.success).not.toHaveBeenCalled();
      expect(props.messages.error).not.toHaveBeenCalled();
    });

    it('does not resurrect a request after A to B to A edits', async () => {
      const pending = deferred<Response>();
      vi.stubGlobal('fetch', vi.fn(() => pending.promise));
      const { result, props, rerender } = mount();
      let settled: boolean | undefined;
      act(() => { void invoke(result.current, action).then(value => { settled = value; }); });
      rerender({ ...props, workflow: { ...workflow, name: 'B' } });
      rerender({ ...props });
      await act(async () => { pending.resolve(jsonResponse(responseFor(action))); });
      expect(settled).toBe(false);
      expect(result.current.validationState.status).toBe('unvalidated');
      expect(result.current.compiledDsl).toBeUndefined();
      expect(props.onValidated).not.toHaveBeenCalled();
    });

    it('does not cancel a current request for an equivalent workflow object', async () => {
      const pending = deferred<Response>();
      vi.stubGlobal('fetch', vi.fn(() => pending.promise));
      const { result, props, rerender } = mount();
      act(() => { void invoke(result.current, action); });
      rerender({ ...props, workflow: { ...workflow, nodes: [...workflow.nodes] } });
      expect(action === 'validate' ? result.current.validationBusy : result.current.compileBusy).toBe(true);
      await act(async () => { pending.resolve(jsonResponse(responseFor(action))); });
      expect(action === 'validate' ? result.current.validationState.status === 'ok' : Boolean(result.current.compiledDsl)).toBe(true);
    });

    it.each(['network', 'json'])('surfaces %s failures and releases the action for retry', async (stage) => {
      vi.stubGlobal('fetch', vi.fn(async () => {
        if (stage === 'network') throw new Error('offline');
        return { ...jsonResponse({}), json: async () => { throw new SyntaxError('bad json'); } };
      }));
      const { result, props } = mount();
      await act(async () => { expect(await invoke(result.current, action)).toBe(false); });
      expect(action === 'validate' ? result.current.validationBusy : result.current.compileBusy).toBe(false);
      const text = stage === 'network' ? 'offline' : '响应无效：无法解析 JSON';
      if (action === 'validate') expect(result.current.validationIssues[0]?.message).toContain(text);
      else expect(props.messages.error).toHaveBeenCalledWith(expect.stringContaining(text));
      vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(responseFor(action))));
      await act(async () => { expect(await invoke(result.current, action)).toBe(true); });
    });
  });

  it('deduplicates both actions synchronously without blocking one another', async () => {
    const pending = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn(() => pending.promise));
    const { result } = mount();
    act(() => {
      void result.current.validate(); void result.current.validate();
      void result.current.inspectDsl(); void result.current.inspectDsl();
    });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(result.current.validationBusy).toBe(true);
    expect(result.current.compileBusy).toBe(true);
    act(() => result.current.resetInspection());
  });

  it.each([null, {}, { valid: 'yes' }, { valid: true, orderedNodeIds: 'source' },
    { ...valid, pipelineSegmentCount: -1 }, { ...valid, structuredRegionCount: 0.5 }])('rejects malformed validation success: %j', async (data) => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(data)));
    const { result, props } = mount();
    await act(async () => { await result.current.validate(); });
    expect(result.current.validationState.status).toBe('issues');
    expect(result.current.validationIssues[0]?.message).toContain('校验响应无效');
    expect(props.messages.success).not.toHaveBeenCalled();
  });

  it.each([{}, { dslJson: 'not-json' }, { dslJson: 'null' }, { dslJson: '[]' }, { dslJson: '{}' }])('does not display malformed DSL as a compiled artifact: %j', async (data) => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(data)));
    const { result, props } = mount();
    await act(async () => { await result.current.inspectDsl(); });
    expect(result.current.compiledDsl).toBeUndefined();
    expect(props.messages.error).toHaveBeenCalledWith(expect.stringContaining('编译响应无效'));
  });

  it('preserves result-style validation failures and location hints', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ valid: false, error: "Input 'source.image' has multiple data sources." }, 400)));
    const { result } = mount();
    await act(async () => { await result.current.validate(); });
    expect(result.current.validationIssues[0]).toMatchObject({ source: 'server', nodeId: 'source' });
  });

  it('handles valid=false in a successful HTTP response without reporting success', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ valid: false, error: 'compiler rejected the graph' })));
    const { result, props } = mount();
    await act(async () => { expect(await result.current.validate()).toBe(false); });
    expect(result.current.validationIssues[0]?.message).toContain('compiler rejected the graph');
    expect(props.messages.success).not.toHaveBeenCalled();
  });

  it('allows module-expanded node counts and omits unavailable optional statistics', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ valid: true, orderedNodeIds: ['source', 'module:inner'] })));
    const { result } = mount();
    await act(async () => { await result.current.validate(); });
    expect(result.current.validationState).toEqual({ status: 'ok', summary: '流程校验通过 · 2 个节点' });
  });

  it('closing DSL invalidates a pending compile so it cannot reopen the dialog', async () => {
    const body = deferred<unknown>();
    vi.stubGlobal('fetch', vi.fn(async () => ({ ...jsonResponse({}), json: () => body.promise })));
    const { result } = mount();
    await act(async () => { void result.current.inspectDsl(); });
    act(() => result.current.closeDsl());
    await act(async () => { body.resolve(compiled); });
    expect(result.current.compiledDsl).toBeUndefined();
    expect(result.current.compileBusy).toBe(false);
  });

  it('marks existing validation stale on edit and hides DSL from a different snapshot', async () => {
    const { result, props, rerender } = mount();
    await act(async () => { await result.current.validate(); await result.current.inspectDsl(); });
    rerender({ ...props, workflow: { ...workflow, name: 'New edit' } });
    expect(result.current.needsRevalidate).toBe(true);
    expect(result.current.compiledDsl).toBeUndefined();
    props.generationRef.current += 1;
    rerender({ ...props });
    expect(result.current.validationState.status).toBe('unvalidated');
    expect(result.current.validationIssues).toEqual([]);
  });
});
