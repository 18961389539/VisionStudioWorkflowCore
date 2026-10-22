import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import type { RunResult } from '../types';
import { useRunImages } from '../useRunImages';

const image = { nodeId: 'target', portName: 'image', width: 100, height: 80 };
const nodes: Node[] = ['source', 'target'].map((id) => ({
  id, position: { x: 0, y: 0 }, data: { inputs: [{ name: 'image', dataType: 'Image' }] },
}));
const edges: Edge[] = [{ id: 'edge', source: 'source', sourceHandle: 'image', target: 'target', targetHandle: 'image' }];
const result: RunResult = { runId: 'run-1', success: true, totalDurationMs: 2, previewAvailable: true, nodeReports: [] };
const props = { result, selectedId: 'target', nodes, edges, nodeLabels: { source: 'Source', target: 'Target' } };

function response(data: unknown, status = 200) {
  const value = new Response(undefined, { status });
  vi.spyOn(value, 'json').mockResolvedValue(data);
  return value;
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('run image catalog ownership', () => {
  it.each([
    ['null response', null],
    ['missing image list', {}],
    ['null image list', { images: null }],
    ['non-array image list', { images: {} }],
    ['null image', { images: [null] }],
    ['blank node ID', { images: [{ ...image, nodeId: ' ' }] }],
    ['invalid node ID', { images: [{ ...image, nodeId: 1 }] }],
    ['blank port name', { images: [{ ...image, portName: ' ' }] }],
    ['invalid port name', { images: [{ ...image, portName: 1 }] }],
    ['zero width', { images: [{ ...image, width: 0 }] }],
    ['negative height', { images: [{ ...image, height: -1 }] }],
    ['string width', { images: [{ ...image, width: '100' }] }],
    ['missing height', { images: [{ nodeId: 'target', width: 100 }] }],
    ['non-finite width', { images: [{ ...image, width: Infinity }] }],
    ['non-finite height', { images: [{ ...image, height: NaN }] }],
    ['fractional width', { images: [{ ...image, width: 1.5 }] }],
    ['mismatched run', { runId: 'another-run', images: [image] }],
    ['invalid run ID', { runId: 1, images: [image] }],
    ['duplicate output', { images: [image, { ...image, portName: 'IMAGE' }] }],
  ])('reports %s instead of an empty or usable catalog', async (_label, data) => {
    vi.stubGlobal('fetch', vi.fn(async () => response(data)));
    const hook = renderHook(() => useRunImages(props));
    await act(async () => {});
    expect(hook.result.current.imageCatalogLoading).toBe(false);
    expect(hook.result.current.imageCatalogError).toBeTruthy();
    expect(hook.result.current.runImageCatalog).toBeUndefined();
    expect(hook.result.current.selectedOutputAvailable).toBe(false);
    expect(hook.result.current.imageViewerSource.url).toContain('/run-1/preview');
  });

  it('accepts a genuinely empty catalog without an error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ runId: 'run-1', images: [] })));
    const hook = renderHook(() => useRunImages(props));
    await act(async () => {});
    expect(hook.result.current.runImageCatalog?.images).toEqual({});
    expect(hook.result.current.imageCatalogError).toBeUndefined();
    expect(hook.result.current.selectedOutputAvailable).toBe(false);
  });

  it('keeps legacy omitted run ID and default image port compatible', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ images: [{ nodeId: 'target', width: 100, height: 80 }] })));
    const hook = renderHook(() => useRunImages(props));
    await act(async () => {});
    expect(hook.result.current.imageCatalogError).toBeUndefined();
    expect(hook.result.current.imageOutputPortName).toBe('image');
    expect(hook.result.current.imageViewerSource.url).toContain('/nodes/target/image?portName=image');
  });

  it('includes ProblemDetails reason and correlation ID in catalog errors', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ detail: 'Capture unavailable', correlationId: 'trace-images' }, 503)));
    const hook = renderHook(() => useRunImages(props));
    await act(async () => {});
    expect(hook.result.current.imageCatalogError).toContain('Capture unavailable');
    expect(hook.result.current.imageCatalogError).toContain('trace-images');
  });

  it('ends loading on timeout and rejects a late body even if it ignores abort', async () => {
    vi.useFakeTimers();
    const body = deferred<unknown>();
    const value = response({});
    vi.mocked(value.json).mockReturnValue(body.promise);
    const fetchMock = vi.fn<typeof fetch>(async () => value);
    vi.stubGlobal('fetch', fetchMock);
    const hook = renderHook(() => useRunImages(props));
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000); });
    expect(hook.result.current.imageCatalogLoading).toBe(false);
    expect(hook.result.current.imageCatalogError).toBeTruthy();
    await act(async () => { body.resolve({ runId: 'run-1', images: [image] }); });
    expect(hook.result.current.imageCatalogError).toBeTruthy();
    expect(hook.result.current.runImageCatalog).toBeUndefined();
    expect(hook.result.current.selectedOutputAvailable).toBe(false);
  });

  it('retry owns its result while the timed-out request finishes late', async () => {
    vi.useFakeTimers();
    const old = deferred<Response>();
    const fetchMock = vi.fn().mockReturnValueOnce(old.promise).mockResolvedValueOnce(response({ runId: 'run-1', images: [image] }));
    vi.stubGlobal('fetch', fetchMock);
    const hook = renderHook(() => useRunImages(props));
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000); });
    expect(fetchMock.mock.calls[0][1].signal.aborted).toBe(true);
    expect(hook.result.current.imageCatalogLoading).toBe(false);
    act(() => hook.result.current.retryImageCatalog());
    await act(async () => {});
    expect(hook.result.current.selectedOutputAvailable).toBe(true);
    await act(async () => { old.resolve(response({ runId: 'run-1', images: [] })); });
    expect(hook.result.current.selectedOutputAvailable).toBe(true);
    expect(hook.result.current.imageCatalogError).toBeUndefined();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('withdraws a catalog during retry and preserves final preview on failure', async () => {
    const retry = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(response({ runId: 'run-1', images: [image] })).mockReturnValueOnce(retry.promise));
    const hook = renderHook(() => useRunImages(props));
    await act(async () => {});
    expect(hook.result.current.selectedOutputAvailable).toBe(true);
    act(() => hook.result.current.retryImageCatalog());
    expect(hook.result.current.imageCatalogLoading).toBe(true);
    expect(hook.result.current.runImageCatalog).toBeUndefined();
    expect(hook.result.current.selectedOutputAvailable).toBe(false);
    expect(hook.result.current.imageViewerSource.url).toContain('/run-1/preview');
    await act(async () => { retry.resolve(response({ detail: 'Expired images' }, 404)); });
    expect(hook.result.current.imageCatalogError).toContain('Expired images');
    expect(hook.result.current.runImageCatalog).toBeUndefined();
    expect(hook.result.current.imageViewerSource.url).toContain('/run-1/preview');
  });

  it('ignores a previous run body and clears resources on document reset', async () => {
    const oldBody = deferred<unknown>();
    const oldResponse = response({});
    vi.mocked(oldResponse.json).mockReturnValue(oldBody.promise);
    const fetchMock = vi.fn().mockResolvedValueOnce(oldResponse)
      .mockResolvedValueOnce(response({ runId: 'run-2', images: [image] }));
    vi.stubGlobal('fetch', fetchMock);
    const initial: Parameters<typeof useRunImages>[0] = props;
    const hook = renderHook((value) => useRunImages(value), { initialProps: initial });
    await act(async () => {});
    hook.rerender({ ...props, result: { ...result, runId: 'run-2' } });
    await act(async () => {});
    expect(fetchMock.mock.calls[0][1].signal.aborted).toBe(true);
    await act(async () => { oldBody.resolve({ runId: 'run-1', images: [] }); });
    expect(hook.result.current.runImageCatalog?.runId).toBe('run-2');
    expect(hook.result.current.selectedOutputAvailable).toBe(true);
    hook.rerender({ ...props, result: undefined });
    expect(hook.result.current.runImageCatalog).toBeUndefined();
    expect(hook.result.current.imageCatalogLoading).toBe(false);
    expect(hook.result.current.imageCatalogError).toBeUndefined();
    expect(hook.result.current.imageViewerSource.url).toBeUndefined();
  });

  it('aborts on unmount and ignores a late body without rendering again', async () => {
    const body = deferred<unknown>();
    const value = response({});
    vi.mocked(value.json).mockReturnValue(body.promise);
    const fetchMock = vi.fn<typeof fetch>(async () => value);
    const rendered = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const hook = renderHook(() => { rendered(); return useRunImages(props); });
    await act(async () => {});
    hook.unmount();
    const renderCount = rendered.mock.calls.length;
    expect(fetchMock.mock.calls[0][1].signal.aborted).toBe(true);
    await act(async () => { body.resolve({ runId: 'run-1', images: [image] }); });
    expect(rendered).toHaveBeenCalledTimes(renderCount);
  });

  it('encodes run and node path segments as well as port query values', async () => {
    const runId = 'run #?%/1';
    const nodeId = 'node #?%/1';
    const portName = 'mask&kind=image';
    const fetchMock = vi.fn<typeof fetch>(async () => response({ runId, images: [{ ...image, nodeId, portName }] }));
    vi.stubGlobal('fetch', fetchMock);
    const hook = renderHook(() => useRunImages({ ...props, result: { ...result, runId }, selectedId: nodeId,
      nodes: [{ ...nodes[1], id: nodeId }] }));
    await act(async () => {});
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/runs/${encodeURIComponent(runId)}/images`);
    expect(hook.result.current.imageViewerSource.url).toBe(`/api/runs/${encodeURIComponent(runId)}/nodes/${encodeURIComponent(nodeId)}/image?portName=${encodeURIComponent(portName)}`);
    act(() => hook.result.current.setImageSource('preview'));
    expect(hook.result.current.imageViewerSource.url).toContain(`/api/runs/${encodeURIComponent(runId)}/preview?`);
  });

  it('preserves a manually chosen preview on equivalent node and edge rerenders', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ runId: 'run-1', images: [image, { ...image, nodeId: 'source' }] })));
    const hook = renderHook((value) => useRunImages(value), { initialProps: props });
    await act(async () => {});
    expect(hook.result.current.imageSource).toBe('output');
    act(() => hook.result.current.setImageSource('preview'));
    hook.rerender({ ...props, nodes: nodes.map((node) => ({ ...node, data: { ...node.data, status: 'idle' } })),
      edges: edges.map((edge) => ({ ...edge })) });
    expect(hook.result.current.imageSource).toBe('preview');
    expect(hook.result.current.imageViewerSource.url).toContain('/run-1/preview');
    hook.rerender({ ...props, selectedId: 'source' });
    expect(hook.result.current.imageSource).toBe('output');
    expect(hook.result.current.imageViewerSource.url).toContain('/nodes/source/image');
  });
});
