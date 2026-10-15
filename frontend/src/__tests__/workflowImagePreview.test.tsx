import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import WorkflowImagePreview from '../components/WorkflowImagePreview';
import type { RunResult } from '../types';

// Keep canvas rendering outside this test; exercise real run-image resolution and selection.
vi.mock('../components/ImageViewer', () => ({
  default: ({ previewUrl }: { previewUrl?: string }) => <output data-testid="image-url">{previewUrl ?? 'empty'}</output>
}));

const nodes: Node[] = ['source', 'target'].map((id) => ({
  id, position: { x: 0, y: 0 }, data: { inputs: [{ name: 'image', dataType: 'Image' }] }
}));
const result: RunResult = { runId: 'run-1', success: true, totalDurationMs: 2, previewAvailable: true, nodeReports: [] };
const base = { nodes, edges: [{ id: 'edge', source: 'source', target: 'target', targetHandle: 'image' }],
  selectedId: 'target', nodeLabels: { source: '采集', target: '检测' }, result, stale: false, running: false, onClose: vi.fn() };

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function availableImages() {
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
    { nodeId: 'source', portName: 'image', width: 100, height: 100 },
    { nodeId: 'target', portName: 'image', width: 100, height: 100 }
  ] }) })));
}

describe('workflow image preview', () => {
  it('describes a first run without claiming there is a previous image', () => {
    render(<WorkflowImagePreview {...base} result={undefined} running />);
    expect(screen.getByText('正在运行，等待首张图像…')).toBeVisible();
    expect(screen.queryByText('正在运行，当前显示上次结果…')).toBeNull();
    expect(screen.getByRole('radio', { name: '节点输入' })).toBeDisabled();
    expect(screen.getByRole('radio', { name: '节点输出' })).toBeDisabled();
  });

  it('preserves the final preview on catalog failure and recovers when retried', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce({ ok: false, status: 503 })
      .mockResolvedValueOnce({ ok: true, json: async () => ({ images: [{ nodeId: 'target', width: 100, height: 100 }] }) });
    vi.stubGlobal('fetch', fetchMock);
    render(<WorkflowImagePreview {...base} />);
    expect(await screen.findByRole('alert')).toHaveTextContent('节点图像加载失败');
    expect(screen.getByTestId('image-url')).toHaveTextContent('/run-1/preview');
    fireEvent.click(screen.getByRole('button', { name: '重试加载' }));
    await waitFor(() => expect(screen.getByTestId('image-url')).toHaveTextContent('/run-1/nodes/target/image'));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ignores a delayed image catalog from the previous run and cancels its request', async () => {
    let resolveOld!: (response: unknown) => void;
    let resolveNew!: (response: unknown) => void;
    const fetchMock = vi.fn().mockImplementationOnce(() => new Promise((resolve) => { resolveOld = resolve; }))
      .mockImplementationOnce(() => new Promise((resolve) => { resolveNew = resolve; }));
    vi.stubGlobal('fetch', fetchMock);
    const view = render(<WorkflowImagePreview {...base} />);
    view.rerender(<WorkflowImagePreview {...base} result={{ ...result, runId: 'run-2' }} />);
    expect(fetchMock.mock.calls[0][1].signal.aborted).toBe(true);
    await act(async () => resolveNew({ ok: true, json: async () => ({ images: [{ nodeId: 'target', width: 200, height: 100 }] }) }));
    await waitFor(() => expect(screen.getByTestId('image-url')).toHaveTextContent('/run-2/nodes/target/image'));
    await act(async () => resolveOld({ ok: true, json: async () => ({ images: [{ nodeId: 'source', width: 100, height: 100 }] }) }));
    expect(screen.getByTestId('image-url')).toHaveTextContent('/run-2/nodes/target/image');
    expect(screen.getByRole('radio', { name: '节点输出' })).not.toBeDisabled();
  });

  it('follows selection, holds a pinned node, and resumes following when unpinned', async () => {
    availableImages();
    const view = render(<WorkflowImagePreview {...base} />);
    await waitFor(() => expect(screen.getByTestId('image-url')).toHaveTextContent('/nodes/target/image'));
    fireEvent.click(screen.getByRole('button', { name: '固定节点' }));
    view.rerender(<WorkflowImagePreview {...base} selectedId="source" />);
    expect(screen.getByTestId('image-url')).toHaveTextContent('/nodes/target/image');
    fireEvent.click(screen.getByRole('button', { name: '取消固定' }));
    await waitFor(() => expect(screen.getByTestId('image-url')).toHaveTextContent('/nodes/source/image'));
  });

  it('resolves the connected input and clears images when the run is cleared', async () => {
    availableImages();
    const view = render(<WorkflowImagePreview {...base} stale />);
    await waitFor(() => expect(screen.getByTestId('image-url')).toHaveTextContent('/nodes/target/image'));
    fireEvent.click(screen.getByRole('radio', { name: '节点输入' }));
    expect(screen.getByTestId('image-url')).toHaveTextContent('/nodes/source/image');
    expect(screen.getByText('流程已修改 · 上次结果')).toBeVisible();
    view.rerender(<WorkflowImagePreview {...base} result={undefined} />);
    expect(screen.getByTestId('image-url')).toHaveTextContent('empty');
    expect(screen.getByText('运行批次：尚未运行')).toBeVisible();
  });

  it('releases a removed pinned node and does not silently pin it again after undo', async () => {
    availableImages();
    const view = render(<WorkflowImagePreview {...base} />);
    await waitFor(() => expect(screen.getByTestId('image-url')).toHaveTextContent('/nodes/target/image'));
    fireEvent.click(screen.getByRole('button', { name: '固定节点' }));
    view.rerender(<WorkflowImagePreview {...base} nodes={[nodes[0]]} selectedId="source" />);
    expect(screen.getByRole('button', { name: '固定节点' })).toBeVisible();
    view.rerender(<WorkflowImagePreview {...base} selectedId="source" />);
    expect(screen.getByTestId('image-url')).toHaveTextContent('/nodes/source/image');
  });
});
