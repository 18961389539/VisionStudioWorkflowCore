import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { NodeProps } from '@xyflow/react';
import VisionNode from '../components/VisionNode';

const state = vi.hoisted(() => ({ connection: { inProgress: false } as Record<string, unknown>, outputConnections: [] as Record<string, unknown>[], internals: vi.fn(), update: vi.fn() }));
vi.mock('@xyflow/react', () => ({
  Handle: ({ id, type }: { id: string; type: string }) => <span data-testid={`${type}-${id}`} />,
  Position: { Left: 'left', Right: 'right' },
  useConnection: () => state.connection,
  useNodeConnections: ({ handleType }: { handleType: string }) => handleType === 'source' ? state.outputConnections : [],
  useReactFlow: () => ({ updateNodeData: state.update, getNode: () => undefined }),
  useUpdateNodeInternals: () => state.internals
}));

const outputs = [
  { name: 'next', dataType: 'Control' }, { name: 'image', dataType: 'Image' }, { name: 'count', dataType: 'Integer' },
  { name: 'detections', dataType: 'DetectionList' }, { name: 'x', dataType: 'Double' }, { name: 'y', dataType: 'Double' },
  { name: 'area', dataType: 'Double' }, { name: 'radius', dataType: 'Double' }
];
const base = { label: '斑点分析', typeKey: 'measure.blobs', inputs: [], outputs, status: 'ok', durationMs: 2.5, summary: { count: 1 }, disposition: 'OK' };
function component(data = base) { return <VisionNode {...({ id: 'node', data, selected: false } as unknown as NodeProps)} />; }
beforeEach(() => { state.connection = { inProgress: false }; state.outputConnections = []; state.internals.mockClear(); });
afterEach(cleanup);

describe('canvas node presentation', () => {
  it('starts with common ports, expands on request, and refreshes connection positions', () => {
    render(component());
    expect(screen.queryByTestId('source-radius')).toBeNull();
    expect(screen.getByTestId('source-image')).toBeTruthy();
    expect(screen.queryByText('DetectionList')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '展开其余端口（4）' }));
    expect(screen.getByTestId('source-radius')).toBeTruthy();
    expect(state.internals).toHaveBeenCalledWith('node');
    fireEvent.click(screen.getByRole('button', { name: '收起未连接端口' }));
    expect(screen.queryByTestId('source-radius')).toBeNull();
  });

  it('always retains an uncommon connected port and exposes all ports while connecting', () => {
    state.outputConnections = [{ sourceHandle: 'radius' }];
    const view = render(component());
    expect(screen.getByTestId('source-radius')).toBeTruthy();
    expect(screen.queryByTestId('source-area')).toBeNull();
    state.connection = { inProgress: true, fromHandle: { type: 'target', id: 'image' }, fromNode: { id: 'other', data: { inputs: [{ name: 'image', dataType: 'Image' }] } } };
    view.rerender(component());
    expect(screen.getByTestId('source-area')).toBeTruthy();
    state.connection = { inProgress: false };
    view.rerender(component());
    expect(screen.queryByTestId('source-area')).toBeNull();
    expect(screen.getByTestId('source-radius')).toBeTruthy();
  });

  it('keeps missing required inputs visible even when they are not common ports', () => {
    const inputs = outputs.map((port, index) => ({ ...port, required: index === 7 }));
    render(component({ ...base, inputs }));
    expect(screen.getByTestId('target-radius')).toBeTruthy();
    expect(screen.getByTitle('必需输入未连接')).toBeTruthy();
  });

  it('marks changed parameters as pending and treats success/OK as historical', () => {
    const { container } = render(component({ ...base, stale: true } as typeof base));
    expect(screen.getByText('参数已修改，待运行')).toBeTruthy();
    expect(screen.getByText('待运行')).toBeTruthy();
    expect(screen.queryByText('成功')).toBeNull();
    expect(screen.queryByText('OK')).toBeNull();
    expect(screen.getByText('旧结果 ·')).toBeTruthy();
    expect(container.querySelector('.status-ok')).toBeNull();
  });
});
