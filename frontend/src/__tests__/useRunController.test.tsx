import { useState } from 'react';
import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { useRunController } from '../useRunController';
import type { NodeCatalogItem, RunResult, WorkflowPayload } from '../types';

const nodes: Node[] = [
  { id: 'source', position: { x: 0, y: 0 }, data: { typeKey: 'image.synthetic', status: 'idle' } },
  { id: 'threshold', position: { x: 200, y: 0 }, data: { typeKey: 'image.threshold', status: 'idle', parameters: { threshold: 128 } } },
];
const edges: Edge[] = [{ id: 'control', source: 'source', target: 'threshold', sourceHandle: 'next', targetHandle: 'exec', data: { kind: 'control' } }];
const workflow: WorkflowPayload = {
  id: 'A', name: 'Workflow A',
  nodes: nodes.map((node) => ({ id: node.id, type: String(node.data.typeKey), parameters: node.data.parameters as Record<string, unknown> })),
  edges: [{ id: 'control', sourceNodeId: 'source', sourcePort: 'next', targetNodeId: 'threshold', targetPort: 'exec', kind: 'control' }],
};
const metadata: NodeCatalogItem = { type: 'image.threshold', displayName: '阈值', category: '图像', parameters: [], inputs: [], outputs: [] };
const completed: RunResult = {
  runId: 'run-1', success: true, totalDurationMs: 2, previewAvailable: false,
  nodeReports: [{ nodeId: 'threshold', nodeType: 'image.threshold', success: true, durationMs: 1, summary: { count: 1 } }],
};
const sessionResult = { ...completed, runId: 'session-run', nodeReports: [], sessionId: 's1', debugState: 'Breakpoint', haltNodeId: 'threshold' };
const response = (data: unknown = {}, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => data }) as Response;
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
function mount() {
  const messages = { info: vi.fn(), error: vi.fn(), warning: vi.fn(), success: vi.fn() };
  const props = { workflowPayload: workflow, selectedId: 'threshold', breakpoints: ['threshold'], nodeLabels: {} };
  const hook = renderHook((next) => {
    const [canvasNodes, setNodes] = useState(nodes);
    const [canvasEdges, setEdges] = useState(edges);
    const controller = useRunController({ ...next, nodes: canvasNodes, catalog: [metadata], setNodes, setEdges, setBottomTab: vi.fn(), messageApi: messages as unknown as Parameters<typeof useRunController>[0]['messageApi'] });
    return { ...controller, nodes: canvasNodes, edges: canvasEdges, setCanvasNodes: setNodes };
  }, { initialProps: props });
  return { ...hook, props, messages };
}

afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('运行入口互斥', () => {
  it.each(['run', 'previewNode', 'startDebugSession'] as const)('%s 在同一个事件内阻断其它运行和调试请求', async (entry) => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn(() => pending.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    const controller = result.current;
    let first!: Promise<unknown>;
    act(() => { first = entry === 'previewNode' ? controller.previewNode('threshold', {}) : controller[entry](); });
    act(() => { void controller.run(); void controller.runNode(); void controller.runFromHere(); void controller.startDebugSession(); });
    await expect(controller.previewNode('threshold', {})).rejects.toThrow('正在进行');
    expect(fetchMock).toHaveBeenCalledOnce();
    await act(async () => { pending.resolve(response(entry === 'startDebugSession' ? sessionResult : completed)); await first; });
    expect(result.current.anyBusy).toBe(false);
  });

  it.each(['continueDebugSession', 'runSelectedNodeInSession', 'endDebugSession'] as const)('%s 与其它会话动作同步互斥', async (entry) => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockReturnValue(pending.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    const controller = result.current;
    let first!: Promise<unknown>;
    act(() => { first = controller[entry](); });
    act(() => { void controller.continueDebugSession(); void controller.runSelectedNodeInSession(); void controller.endDebugSession(); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(async () => { pending.resolve(response(completed)); await first; });
    expect(result.current.anyBusy).toBe(false);
  });

  it('已有会话时禁止普通运行和重新创建，不抢先删除现有现场', async () => {
    const fetchMock = vi.fn(async () => response(sessionResult));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    await act(async () => { await result.current.run(); await result.current.startDebugSession(); });
    await expect(result.current.previewNode('threshold', {})).rejects.toThrow('先结束');
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(result.current.debugSession?.sessionId).toBe('s1');
  });

  it('同一批事件里建立会话后，旧回调也读取最新会话 ID', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockResolvedValue(response(completed));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    const controller = result.current;
    await act(async () => { await controller.startDebugSession(); await controller.continueDebugSession(); });
    expect(fetchMock.mock.calls[1][0]).toBe('/api/debug/sessions/s1/continue');
  });
});

describe('会话释放', () => {
  it.each(['network', 'http'] as const)('%s 失败时保留会话供重试，确认释放前禁止继续执行', async (failure) => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult));
    if (failure === 'network') fetchMock.mockRejectedValueOnce(new TypeError('offline'));
    else fetchMock.mockResolvedValueOnce(response({ detail: 'worker unavailable', code: 'busy', correlationId: 'trace-1' }, 503));
    fetchMock.mockResolvedValue(response({}, 204));
    vi.stubGlobal('fetch', fetchMock);
    const { result, messages } = mount();
    await act(async () => { await result.current.startDebugSession(); await result.current.endDebugSession(); });
    expect(result.current.debugSession).toMatchObject({ sessionId: 's1', status: 'faulted' });
    expect(result.current.anyBusy).toBe(false);
    expect(messages.error).toHaveBeenCalledWith(expect.stringContaining('释放未确认'));
    if (failure === 'http') expect(messages.error).toHaveBeenCalledWith(expect.stringContaining('trace-1'));
    await act(async () => { await result.current.continueDebugSession(); await result.current.run(); await result.current.startDebugSession(); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(async () => { await result.current.endDebugSession(); });
    expect(fetchMock.mock.calls[2][0]).toBe('/api/debug/sessions/s1');
    expect(result.current.debugSession).toBeUndefined();
  });

  it('404 表示会话已不存在，允许清除本地句柄', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockResolvedValueOnce(response({}, 404));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); await result.current.endDebugSession(); });
    expect(result.current.debugSession).toBeUndefined();
    expect(result.current.anyBusy).toBe(false);
  });

  it('切换文档时释放失败仍保留旧会话，只提供结束重试', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockResolvedValue(response({}, 503));
    vi.stubGlobal('fetch', fetchMock);
    const { result, rerender, props } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    await act(async () => { result.current.clearResult(); });
    rerender({ ...props, workflowPayload: { ...workflow, id: 'B' } });
    expect(result.current.result).toBeUndefined();
    expect(result.current.debugSession?.status).toBe('faulted');
    await act(async () => { await result.current.continueDebugSession(); await result.current.runSelectedNodeInSession(); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('重复清空结果不会并发删除，也不在删除失败后自动重试', async () => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockReturnValue(pending.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    act(() => { result.current.clearResult(); result.current.clearResult(); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(async () => { pending.resolve(response({}, 503)); });
    expect(result.current.debugSession?.sessionId).toBe('s1');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

describe('结果归属与响应契约', () => {
  it.each([null, {}, { runId: 'invalid', success: true }, { ...completed, nodeReports: [null] },
    { ...completed, nodeReports: [{ ...completed.nodeReports[0], summary: null }] },
    { ...completed, overlays: {} },
  ])('无效成功响应不覆盖已有结果：%j', async (invalid) => {
    vi.stubGlobal('fetch', vi.fn(async () => response(invalid)));
    const { result, messages } = mount();
    act(() => { result.current.applyResult(completed, JSON.stringify(workflow)); });
    await act(async () => { await result.current.run(); });
    expect(result.current.result?.runId).toBe('run-1');
    expect(result.current.anyBusy).toBe(false);
    expect(result.current.edges[0].animated).toBe(false);
    expect(messages.error).toHaveBeenCalledWith(expect.stringContaining('有效运行结果'));
  });

  it('完整的算法失败结果仍落地，而不是作为请求拒绝丢弃', async () => {
    const failed = { ...completed, runId: 'failed', success: false, error: 'algorithm failure', nodeReports: [{ ...completed.nodeReports[0], success: false }] };
    vi.stubGlobal('fetch', vi.fn(async () => response(failed, 400)));
    const { result } = mount();
    await act(async () => { await result.current.run(); });
    expect(result.current.result?.runId).toBe('failed');
    expect(result.current.nodes.find((node) => node.id === 'threshold')?.data.status).toBe('error');
  });

  it('执行期间编辑参数，结果及节点报告均标记为旧版本', async () => {
    const pending = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn(() => pending.promise));
    const { result, rerender, props } = mount();
    let run!: Promise<void>;
    act(() => { run = result.current.run(); });
    const edited = { ...workflow, nodes: workflow.nodes.map((node) => node.id === 'threshold'
      ? { ...node, parameters: { threshold: 200 } } : node) };
    rerender({ ...props, workflowPayload: edited });
    await act(async () => { pending.resolve(response(completed)); await run; });
    expect(result.current.resultStale).toBe(true);
    expect(result.current.nodes.find((node) => node.id === 'threshold')?.data.stale).toBe(true);
  });

  it('来源未提供流程快照的结果不借用最近指纹，也不染色当前画布', () => {
    vi.stubGlobal('fetch', vi.fn());
    const { result } = mount();
    act(() => { result.current.applyResult(completed); });
    expect(result.current.result?.runId).toBe('run-1');
    expect(result.current.resultStale).toBe(true);
    expect(result.current.nodes.every((node) => node.data.status === 'idle')).toBe(true);
  });

  it('旧正文在切换后才完成，不得恢复旧结果或改变新画布', async () => {
    const body = deferred<RunResult>();
    let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, options?: RequestInit) => {
      signal = options!.signal as AbortSignal;
      return { ...response(), json: () => body.promise } as Response;
    }));
    const { result, rerender, props } = mount();
    let run!: Promise<void>;
    await act(async () => { run = result.current.run(); });
    act(() => { result.current.clearResult(); result.current.setCanvasNodes(nodes); });
    rerender({ ...props, workflowPayload: { ...workflow, id: 'B' } });
    expect(signal.aborted).toBe(true);
    await act(async () => { body.resolve(completed); await run; });
    expect(result.current.result).toBeUndefined();
    expect(result.current.nodes.every((node) => node.data.status === 'idle')).toBe(true);
    expect(result.current.anyBusy).toBe(false);
  });

  it('切换流程后才创建成功的调试会话被释放，不复活旧结果', async () => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(response({}, 204));
    vi.stubGlobal('fetch', fetchMock);
    const { result, rerender, props } = mount();
    let start!: Promise<void>;
    act(() => { start = result.current.startDebugSession(); result.current.clearResult(); });
    rerender({ ...props, workflowPayload: { ...workflow, id: 'B' } });
    await act(async () => { pending.resolve(response(sessionResult)); await start; });
    expect(fetchMock.mock.calls[1]).toEqual(['/api/debug/sessions/s1', expect.objectContaining({ method: 'DELETE' })]);
    expect(result.current.result).toBeUndefined();
    expect(result.current.debugSession).toBeUndefined();
    expect(result.current.anyBusy).toBe(false);
  });

  it('旧会话继续响应晚到时只释放旧现场，不更新新流程结果', async () => {
    const body = deferred<RunResult>();
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult))
      .mockResolvedValueOnce({ ...response(), json: () => body.promise })
      .mockResolvedValue(response({}, 204));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    let continuation!: Promise<void>;
    await act(async () => { continuation = result.current.continueDebugSession(); });
    act(() => { result.current.clearResult(); });
    await act(async () => { body.resolve(completed); await continuation; });
    expect(result.current.result).toBeUndefined();
    expect(result.current.debugSession).toBeUndefined();
    expect(fetchMock.mock.calls[2][0]).toBe('/api/debug/sessions/s1');
  });

  it('无效创建响应若包含会话 ID，保留释放入口并清除等待标记', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ sessionId: 's1' })));
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    expect(result.current.debugSession).toMatchObject({ sessionId: 's1', status: 'faulted' });
    expect(result.current.result).toBeUndefined();
    expect(result.current.nodes.every((node) => node.data.status === 'idle')).toBe(true);
  });

  it('创建会话返回算法失败时，仍保留实际报告和可释放的会话', async () => {
    const failed = { ...sessionResult, success: false, debugState: 'Faulted', haltNodeId: undefined, error: 'algorithm failed', nodeReports: [{ ...completed.nodeReports[0], success: false }] };
    vi.stubGlobal('fetch', vi.fn(async () => response(failed, 400)));
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    expect(result.current.result?.success).toBe(false);
    expect(result.current.nodes.find((node) => node.id === 'threshold')?.data.status).toBe('error');
    expect(result.current.debugSession?.sessionId).toBe('s1');
  });

  it.each(['continueDebugSession', 'runSelectedNodeInSession'] as const)('%s 请求结果无法确认时保留会话并阻止再次执行', async (entry) => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockResolvedValue(response({}));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); await result.current[entry](); });
    expect(result.current.debugSession).toMatchObject({ sessionId: 's1', status: 'faulted' });
    await act(async () => { await result.current.continueDebugSession(); await result.current.runSelectedNodeInSession(); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(result.current.anyBusy).toBe(false);
  });

  it.each([null, {}, { sessionId: 'wrong', state: 'Halted' }])('刷新响应无效时不能将未知状态冒充已完成：%j', async (invalid) => {
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult))
      .mockResolvedValueOnce(response({ detail: 'execution rejected' }, 409)).mockResolvedValue(response(invalid));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); await result.current.continueDebugSession(); });
    expect(result.current.debugSession).toMatchObject({ sessionId: 's1', status: 'faulted' });
    expect(result.current.anyBusy).toBe(false);
  });

  it('继续会话执行失败时展示实际报告，并使用后端确认的故障状态', async () => {
    const failed = { ...completed, runId: 'continue-failed', success: false, error: 'algorithm failed', nodeReports: [{ ...completed.nodeReports[0], success: false }] };
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockResolvedValueOnce(response(failed, 400))
      .mockResolvedValue(response({ sessionId: 's1', state: 'Faulted' }));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = mount();
    await act(async () => { await result.current.startDebugSession(); await result.current.continueDebugSession(); });
    expect(result.current.result?.runId).toBe('continue-failed');
    expect(result.current.nodes.find((node) => node.id === 'threshold')?.data.status).toBe('error');
    expect(result.current.debugSession?.status).toBe('faulted');
  });
});

describe('取消和卸载', () => {
  it.each(['abort', 'timeout'] as const)('节点测试 %s 正确区分手动取消与超时，并释放执行锁', async (mode) => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((_input: RequestInfo | URL, options?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      options?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    const { result } = mount();
    let outcome!: Promise<string>;
    act(() => { outcome = result.current.previewNode('threshold', {}).then(() => '', (error: Error) => error.message); });
    if (mode === 'abort') await act(async () => { result.current.abortRun(); });
    else await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });
    expect(await outcome).toContain(mode === 'abort' ? '已取消' : '超时');
    expect(result.current.anyBusy).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('卸载时取消运行请求，迟到结果不能更新界面或发出成功提示', async () => {
    const pending = deferred<Response>();
    let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn((_input: RequestInfo | URL, options?: RequestInit) => { signal = options!.signal as AbortSignal; return pending.promise; }));
    const { result, unmount, messages } = mount();
    let run!: Promise<void>;
    act(() => { run = result.current.run(); });
    unmount();
    expect(signal.aborted).toBe(true);
    pending.resolve(response(completed));
    await run;
    expect(messages.success).not.toHaveBeenCalled();
  });

  it('卸载后才返回的会话 ID 仍尝试释放', async () => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(response({}, 204));
    vi.stubGlobal('fetch', fetchMock);
    const { result, unmount } = mount();
    let start!: Promise<void>;
    act(() => { start = result.current.startDebugSession(); });
    unmount();
    pending.resolve(response(sessionResult));
    await start;
    expect(fetchMock.mock.calls[1]).toEqual(['/api/debug/sessions/s1', expect.objectContaining({ method: 'DELETE', keepalive: true })]);
  });

  it('卸载不打断已经发出的会话删除，让释放请求完成', async () => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn().mockResolvedValueOnce(response(sessionResult)).mockReturnValue(pending.promise);
    vi.stubGlobal('fetch', fetchMock);
    const { result, unmount, messages } = mount();
    await act(async () => { await result.current.startDebugSession(); });
    let closing!: Promise<void>;
    act(() => { closing = result.current.endDebugSession(); });
    const signal = fetchMock.mock.calls[1][1].signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(false);
    pending.resolve(response({}, 204));
    await closing;
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(messages.info).not.toHaveBeenCalled();
  });
});
