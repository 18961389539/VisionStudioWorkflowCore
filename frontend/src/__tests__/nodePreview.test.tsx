import { act, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import { useRunController } from '../useRunController';
import type { NodeCatalogItem, RunResult, WorkflowPayload } from '../types';

const workflow: WorkflowPayload = { id: 'wf', name: 'test', nodes: [{ id: 'threshold', type: 'image.threshold', parameters: { threshold: 128 } }], edges: [] };
const nodes: Node[] = [{ id: 'threshold', position: { x: 0, y: 0 }, data: { typeKey: 'image.threshold', parameters: { threshold: 128 } } }];
const catalog: NodeCatalogItem = { type: 'image.threshold', displayName: '阈值', category: '图像', parameters: [], inputs: [], outputs: [] };
const baseline: RunResult = { runId: 'baseline', success: true, totalDurationMs: 1, previewAvailable: false, nodeReports: [] };
const previewResult: RunResult = { ...baseline, runId: 'preview' };
const response = (data: unknown, ok = true, status = 200) => ({ ok, status, json: async () => data }) as Response;

function controller(metadata = catalog) {
  const setNodes = vi.fn(); const setEdges = vi.fn();
  const props: Parameters<typeof useRunController>[0] = {
    workflowPayload: workflow, nodes, catalog: [metadata], selectedId: 'threshold', nodeLabels: {}, breakpoints: [],
    setNodes, setEdges, setBottomTab: vi.fn(),
    messageApi: { warning: vi.fn(), error: vi.fn(), success: vi.fn() } as unknown as Parameters<typeof useRunController>[0]['messageApi']
  };
  return { ...renderHook(() => useRunController(props)), setNodes, setEdges };
}
afterEach(() => vi.unstubAllGlobals());

describe('isolated node previews', () => {
  it('submits temporary parameters in RunNode mode and leaves the workflow and global result intact', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => response(previewResult));
    vi.stubGlobal('fetch', fetchMock);
    const { result, setNodes, setEdges } = controller();
    act(() => result.current.applyResult(baseline, JSON.stringify(workflow)));
    setNodes.mockClear(); setEdges.mockClear();
    const receivedAt = result.current.resultReceivedAt;
    let returned: RunResult | undefined;
    await act(async () => { returned = await result.current.previewNode('threshold', { threshold: 200 }); });
    const body = JSON.parse(fetchMock.mock.calls[0][1]!.body as string);
    expect(body.options).toEqual({ mode: 'RunNode', targetNodeId: 'threshold', breakpoints: [] });
    expect(body.workflow.nodes[0].parameters.threshold).toBe(200);
    expect(workflow.nodes[0].parameters?.threshold).toBe(128);
    expect(returned?.runId).toBe('preview');
    expect(result.current.result?.runId).toBe('baseline');
    expect(result.current.resultReceivedAt).toBe(receivedAt);
    expect(result.current.resultStale).toBe(false);
    expect(setNodes).not.toHaveBeenCalled();
    expect(setEdges).not.toHaveBeenCalled();
  });

  it('rejects duplicate in-flight requests and releases the busy gate afterwards', async () => {
    let release!: (value: Response) => void;
    const fetchMock = vi.fn(() => new Promise<Response>((resolve) => { release = resolve; }));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = controller();
    const preview = result.current.previewNode;
    let first!: Promise<RunResult>;
    act(() => { first = preview('threshold', { threshold: 100 }); });
    await expect(preview('threshold', { threshold: 101 })).rejects.toThrow('正在进行');
    await act(async () => { release(response(previewResult)); await first; });
    expect(fetchMock).toHaveBeenCalledOnce();
    expect(result.current.anyBusy).toBe(false);
  });

  it('returns algorithm failure reports but rejects request-level errors without overwriting results', async () => {
    const fetchMock = vi.fn(async () => response({ ...previewResult, success: false, error: '算法失败' }, false, 400));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = controller();
    await act(async () => expect((await result.current.previewNode('threshold', {})).success).toBe(false));
    fetchMock.mockImplementation(async () => response({ detail: '运行租约冲突' }, false, 409));
    await act(async () => { await expect(result.current.previewNode('threshold', {})).rejects.toThrow('运行租约冲突'); });
    expect(result.current.result).toBeUndefined();
    expect(result.current.anyBusy).toBe(false);
  });

  it('does not execute nodes that prohibit standalone testing', async () => {
    const fetchMock = vi.fn(); vi.stubGlobal('fetch', fetchMock);
    const { result } = controller({ ...catalog, capabilities: { supportsRunNode: false } });
    await expect(result.current.previewNode('threshold', {})).rejects.toThrow('不支持单节点测试');
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
