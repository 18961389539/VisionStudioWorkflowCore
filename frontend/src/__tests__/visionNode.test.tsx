import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Edge, Node, NodeConnection, NodeProps } from '@xyflow/react';
import type { CSSProperties } from 'react';
import VisionNode from '../components/VisionNode';
import { catalogNodeData, fallbackCatalogRaw } from '../catalog';

const state = vi.hoisted(() => ({
  connection: { inProgress: false } as Record<string, unknown>, outputConnections: [] as Record<string, unknown>[],
  inputConnections: [] as NodeConnection[], edges: [] as Edge[], nodes: new Map<string, Node>(),
  getNode: vi.fn<(id: string) => Node | undefined>(), getEdge: vi.fn<(id: string) => Edge | undefined>(),
  getEdges: vi.fn<() => Edge[]>(), internals: vi.fn(), update: vi.fn()
}));
vi.mock('@xyflow/react', () => ({
  Handle: ({ id, type, style }: { id: string; type: string; style?: CSSProperties }) => <span data-testid={`${type}-${id}`} style={style} />,
  Position: { Left: 'left', Right: 'right' },
  useConnection: () => state.connection,
  useNodeConnections: ({ handleType }: { handleType: string }) => handleType === 'source' ? state.outputConnections : state.inputConnections,
  useNodesData: (ids: string[]) => ids.map(id => state.nodes.get(id)).filter(Boolean),
  useReactFlow: () => ({ updateNodeData: state.update, getNode: state.getNode, getEdge: state.getEdge, getEdges: state.getEdges }),
  useUpdateNodeInternals: () => state.internals
}));

const outputs = [
  { name: 'next', dataType: 'Control' }, { name: 'image', dataType: 'Image' }, { name: 'count', dataType: 'Integer' },
  { name: 'detections', dataType: 'DetectionList' }, { name: 'x', dataType: 'Double' }, { name: 'y', dataType: 'Double' },
  { name: 'area', dataType: 'Double' }, { name: 'radius', dataType: 'Double' }
];
const base = { label: '斑点分析', typeKey: 'measure.blobs', inputs: [], outputs, status: 'ok', durationMs: 2.5, summary: { count: 1 }, disposition: 'OK' };
function component(data: Node['data'] = base) {
  state.nodes.set('node', graphNode('node', data));
  return <VisionNode {...({ id: 'node', data, selected: false } as unknown as NodeProps)} />;
}
const graphNode = (id: string, data: Node['data']): Node => ({ id, position: { x: 0, y: 0 }, data });
const requiredMark = (name: string) => screen.getByTestId(`target-${name}`).closest('.port-row')!.querySelector('.port-required');
function incoming(sourceType: string, kind?: string) {
  const edge: Edge = { id: 'incoming', source: 'upstream', sourceHandle: 'out', target: 'node', targetHandle: 'value',
    ...(kind ? { data: { kind } } : {}) };
  state.nodes.set('upstream', graphNode('upstream', { outputs: [{ name: 'out', dataType: sourceType }] }));
  state.edges = [edge];
  state.inputConnections = [{ source: edge.source, target: edge.target, sourceHandle: 'out', targetHandle: 'value', edgeId: edge.id }];
  return edge;
}
beforeEach(() => {
  state.connection = { inProgress: false }; state.outputConnections = []; state.inputConnections = []; state.edges = []; state.nodes.clear();
  state.internals.mockClear();
  state.getNode.mockImplementation(id => state.nodes.get(id));
  state.getEdge.mockImplementation(id => state.edges.find(edge => edge.id === id));
  state.getEdges.mockImplementation(() => state.edges);
});
afterEach(cleanup);

describe('canvas node presentation', () => {
  it.each([
    ['idle', '空闲'], ['pending', '等待'], ['running', '执行中'], ['ok', '成功'],
    ['error', '失败'], ['warmup', '预热'], ['breakpoint', '暂停'], ['skipped', '未走到']
  ])('exposes %s with a text label and a decorative icon', (status, label) => {
    const { container } = render(component({ ...base, status }));
    expect(screen.getByText(label)).toBeTruthy();
    expect(container.querySelector('.node-status svg[aria-hidden="true"]')).toBeTruthy();
  });

  describe('required input and connection feedback', () => {
    it.each([
      ['Control', 'Image', 'control'], ['Image', 'Control', 'data'],
      ['String', 'Image', 'data'], ['Image', 'Image', 'control']
    ])('does not satisfy %s -> %s with a %s edge', (sourceType, targetType, kind) => {
      incoming(sourceType, kind);
      render(component({ ...base, inputs: [{ name: 'value', dataType: targetType, required: true }] }));
      expect(requiredMark('value')).toBeTruthy();
      expect(screen.getByText('缺 1 个必需输入')).toBeTruthy();
      expect(screen.getByTitle(/必需 · 连线无效/)).toBeTruthy();
    });

    it.each(['Image', 'Control'])('accepts a valid legacy %s connection without an explicit kind', (dataType) => {
      incoming(dataType);
      render(component({ ...base, inputs: [{ name: 'value', dataType, required: true }] }));
      expect(requiredMark('value')).toBeNull();
      expect(screen.queryByText('缺 1 个必需输入')).toBeNull();
    });

    it.each([['Integer', 'Double'], ['Any', 'Image'], ['Image', 'Any']])('preserves compatible %s -> %s required inputs', (sourceType, targetType) => {
      incoming(sourceType, 'data');
      render(component({ ...base, inputs: [{ name: 'value', dataType: targetType, required: true }] }));
      expect(requiredMark('value')).toBeNull();
      expect(screen.queryByText('缺 1 个必需输入')).toBeNull();
    });

    it('rejects forward drags into an occupied data input too', () => {
      incoming('Image', 'data');
      const source = graphNode('new-source', { outputs: [{ name: 'image', dataType: 'Image' }] });
      state.nodes.set(source.id, source);
      state.connection = { inProgress: true, fromHandle: { type: 'source', id: 'image' }, fromNode: source };
      render(component({ ...base, inputs: [{ name: 'value', dataType: 'Image', required: true }] }));
      expect(screen.getByTestId('target-value').style.opacity).toBe('0.3');
    });

    it('keeps a self-connected required input unsatisfied', () => {
      const edge = incoming('Image', 'data');
      edge.source = 'node'; state.inputConnections[0].source = 'node';
      state.nodes.set('node', graphNode('node', { outputs: [{ name: 'out', dataType: 'Image' }] }));
      render(component({ ...base, inputs: [{ name: 'value', dataType: 'Image', required: true }] }));
      expect(requiredMark('value')).toBeTruthy();
    });

    it.each(['node', 'port'])('does not let a missing source %s hide a required input', (missing) => {
      incoming('Image', 'data');
      if (missing === 'node') state.nodes.delete('upstream');
      else state.nodes.set('upstream', graphNode('upstream', { outputs: [] }));
      render(component({ ...base, inputs: [{ name: 'value', dataType: 'Image', required: true }] }));
      expect(requiredMark('value')).toBeTruthy();
    });

    it('refreshes required inputs when upstream port metadata changes without reconnecting', () => {
      incoming('Image', 'data');
      const data = { ...base, inputs: [{ name: 'value', dataType: 'Image', required: true }] };
      const view = render(component(data));
      expect(requiredMark('value')).toBeNull();
      state.nodes.set('upstream', graphNode('upstream', { outputs: [] }));
      view.rerender(component(data));
      expect(requiredMark('value')).toBeTruthy();
      expect(screen.getByText('缺 1 个必需输入')).toBeTruthy();
    });

    it.each(['logic.boolean', 'value.arithmetic'])('resolves default conditional inputs in %s catalog node data', (type) => {
      const item = fallbackCatalogRaw.find(item => item.type === type)!;
      render(component({ ...base, typeKey: type, ...catalogNodeData(item), parameters: {} }));
      expect(requiredMark('b')).toBeTruthy();
    });

    it('updates conditional input hints when an omitted default is replaced and restored', () => {
      const item = fallbackCatalogRaw.find(item => item.type === 'logic.boolean')!;
      const data = { ...base, typeKey: item.type, ...catalogNodeData(item) };
      const view = render(component({ ...data, parameters: { op: 'not' } }));
      expect(requiredMark('b')).toBeNull();
      view.rerender(component({ ...data, parameters: {} }));
      expect(requiredMark('b')).toBeTruthy();
    });

    it('uses reusable-module parameter defaults for conditional input hints', () => {
      render(component({ ...base, typeKey: 'module.call', inputs: [{ name: 'value', dataType: 'Image',
        requiredWhen: { parameter: 'enabled', values: ['true'] } }],
        moduleParameters: [{ name: 'enabled', label: 'Enabled', type: 'boolean', defaultValue: true }], parameters: {} }));
      expect(requiredMark('value')).toBeTruthy();
    });

    it.each(['data', undefined])('rejects reverse drags from an occupied input (%s)', (kind) => {
      state.nodes.set('sink', graphNode('sink', { inputs: [{ name: 'image', dataType: 'Image' }] }));
      state.nodes.set('existing', graphNode('existing', { outputs: [{ name: 'image', dataType: 'Image' }] }));
      state.edges = [{ id: 'occupied', source: 'existing', sourceHandle: 'image', target: 'sink', targetHandle: 'image',
        ...(kind ? { data: { kind } } : {}) }];
      state.connection = { inProgress: true, fromHandle: { type: 'target', id: 'image' }, fromNode: state.nodes.get('sink') };
      render(component());
      expect(screen.getByTestId('source-image').style.opacity).toBe('0.3');
      expect(screen.getByTestId('source-image').closest('.port-row')?.querySelector('[title]')?.getAttribute('title'))
        .toContain('该数据输入已有来源');
    });

    it('still allows reverse drags to a free data input', () => {
      state.nodes.set('sink', graphNode('sink', { inputs: [{ name: 'image', dataType: 'Image' }] }));
      state.connection = { inProgress: true, fromHandle: { type: 'target', id: 'image' }, fromNode: state.nodes.get('sink') };
      render(component());
      expect(screen.getByTestId('source-image').style.boxShadow).toContain('0 0 0 3px');
    });

    it.each(['node', 'port'])('rejects a stale drag after its target %s disappears', (missing) => {
      const sink = graphNode('sink', { inputs: [{ name: 'image', dataType: 'Image' }] });
      state.nodes.set('sink', sink);
      state.connection = { inProgress: true, fromHandle: { type: 'target', id: 'image' }, fromNode: sink };
      if (missing === 'node') state.nodes.delete('sink');
      else state.nodes.set('sink', graphNode('sink', { inputs: [] }));
      render(component());
      expect(screen.getByTestId('source-image').style.opacity).toBe('0.3');
    });

    it('keeps reverse control connections multi-source', () => {
      const sink = graphNode('sink', { inputs: [{ name: 'exec', dataType: 'Control' }] });
      state.nodes.set('sink', sink);
      state.nodes.set('existing', graphNode('existing', { outputs: [{ name: 'next', dataType: 'Control' }] }));
      state.edges = [{ id: 'control', source: 'existing', sourceHandle: 'next', target: 'sink', targetHandle: 'exec', data: { kind: 'control' } }];
      state.connection = { inProgress: true, fromHandle: { type: 'target', id: 'exec' }, fromNode: sink };
      render(component());
      expect(screen.getByTestId('source-next').style.boxShadow).toContain('0 0 0 3px');
    });
  });

  it('distinguishes selection from execution success and breakpoints', () => {
    render(<VisionNode {...({ id: 'node', data: { ...base, breakpoint: true }, selected: true } as unknown as NodeProps)} />);
    expect(screen.getByRole('img', { name: '已选中节点' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '移除断点' })).toBeTruthy();
    expect(screen.getByText('成功')).toBeTruthy();
  });

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
