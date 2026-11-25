import { act, fireEvent, render, renderHook, within } from '@testing-library/react';
import { message } from 'antd';
import { startTransition, Suspense, useRef, useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, PortDescriptor } from '../types';
import { useEditorGraphOperations } from '../useEditorGraphOperations';

const imagePort: PortDescriptor = { name: 'image', label: '图像', dataType: 'Image' };
const passThrough: NodeCatalogItem = {
  type: 'image.pass', displayName: '图像处理', category: 'Preprocess', pluginId: 'builtin',
  inputs: [imagePort], outputs: [imagePort], parameters: []
};

function node(id: string, inputs: PortDescriptor[], outputs: PortDescriptor[]): Node {
  return {
    id, type: 'vision', position: { x: 0, y: 0 },
    data: { typeKey: id, label: id, inputs, outputs, parameters: {} }
  };
}

function useGraph(initialNodes: Node[], initialEdges: Edge[], catalog: NodeCatalogItem[] = [], selectedNodeIds: string[] = []) {
  const [nodes, setNodes] = useState(initialNodes);
  const [edges, setEdges] = useState(initialEdges);
  const [selectedId, setSelectedId] = useState('');
  const nodesRef = useRef(nodes);
  const edgesRef = useRef(edges);
  nodesRef.current = nodes;
  edgesRef.current = edges;
  const [messageApi] = message.useMessage();
  const pushHistory = useRef(vi.fn()).current;
  const graph = useEditorGraphOperations({
    nodes, nodesRef, edgesRef, selectedRef: { current: selectedId }, selectedId,
    selectedNode: nodes.find((item) => item.id === selectedId), selectedNodeIds, catalog,
    setNodes, setEdges, setSelectedId, setRecentNodeTypes: vi.fn(),
    setConnectionPicker: vi.fn(), setNodeContextMenu: vi.fn(), messageApi,
    pushHistory, onNodesChange: vi.fn(), onEdgesChange: vi.fn(),
    flowAreaRef: { current: null }, getZoom: () => 1,
    screenToFlowPosition: (position) => position, setCenter: vi.fn(),
    clipboardRef: { current: null }, pasteIndexRef: { current: 0 }
  });
  return { nodes, edges, graph, selectedId };
}

describe('graph editing operations', () => {
  it('keeps alignment and grouping actions stable while using the latest selection', () => {
    const first = { ...node('first', [], []), position: { x: 10, y: 0 } };
    const second = { ...node('second', [], []), position: { x: 100, y: 30 } };
    const third = { ...node('third', [], []), position: { x: 300, y: 60 } };
    const hook = renderHook(({ selected }) => useGraph([first, second, third], [], [], selected),
      { initialProps: { selected: ['first', 'second'] } });
    const { alignSelection, createGroup } = hook.result.current.graph;
    hook.rerender({ selected: ['second', 'third'] });
    expect(hook.result.current.graph.alignSelection).toBe(alignSelection);
    expect(hook.result.current.graph.createGroup).toBe(createGroup);
    act(() => alignSelection('left'));
    expect(hook.result.current.nodes.map((item) => item.position.x)).toEqual([10, 100, 100]);
    act(() => createGroup());
    const group = hook.result.current.nodes.find((item) => item.type === 'canvasGroup');
    expect(group?.data.memberIds).toEqual(['second', 'third']);
  });

  it.each(['align', 'group'] as const)('uses the committed selection for %s while a new selection is suspended', async (action) => {
    let release!: () => void;
    let ready = false;
    const pending = new Promise<void>((resolve) => { release = () => { ready = true; resolve(); }; });
    const initial = [
      { ...node('first', [], []), position: { x: 10, y: 0 } },
      { ...node('second', [], []), position: { x: 100, y: 0 } },
      { ...node('third', [], []), position: { x: 300, y: 0 } },
    ];
    function Block({ selected }: { selected: string[] }) {
      if (selected[0] === 'second' && !ready) throw pending;
      return null;
    }
    function Harness() {
      const [selected, setSelected] = useState(['first', 'second']);
      const { nodes, graph } = useGraph(initial, [], [], selected);
      const group = nodes.find((item) => item.type === 'canvasGroup');
      return <>
        <button onClick={() => startTransition(() => setSelected(['second', 'third']))}>select</button>
        <button onClick={() => action === 'align' ? graph.alignSelection('left') : graph.createGroup()}>apply</button>
        <span data-testid="result">{action === 'align'
          ? nodes.filter((item) => item.type === 'vision').map((item) => item.position.x).join(',')
          : ((group?.data.memberIds as string[] | undefined) ?? []).join(',')}</span>
        <Block selected={selected} />
      </>;
    }
    const view = render(<Suspense fallback={<div>loading</div>}><Harness /></Suspense>);
    const rendered = within(view.container);
    fireEvent.click(rendered.getByRole('button', { name: 'select' }));
    fireEvent.click(rendered.getByRole('button', { name: 'apply' }));
    expect(rendered.getByTestId('result').textContent).toBe(action === 'align' ? '10,10,300' : 'first,second');
    await act(async () => { release(); await pending; });
    view.unmount();
  });

  it('reconnects a data edge after excluding its old edge from input validation', () => {
    const sourceA = node('source-a', [], [imagePort]);
    const sourceB = node('source-b', [], [imagePort]);
    const target = node('target', [imagePort], []);
    const original: Edge = {
      id: 'existing', source: sourceA.id, sourceHandle: 'image',
      target: target.id, targetHandle: 'image', data: { kind: 'data' }
    };
    const hook = renderHook(() => useGraph([sourceA, sourceB, target], [original]));
    const connection = { source: sourceB.id, sourceHandle: 'image', target: target.id, targetHandle: 'image' };

    act(() => hook.result.current.graph.onReconnectStart({} as React.MouseEvent, original));
    expect(hook.result.current.graph.isValidConnection(connection)).toBe(true);
    act(() => hook.result.current.graph.onReconnect(original, connection));

    expect(hook.result.current.edges).toHaveLength(1);
    expect(hook.result.current.edges[0]).toMatchObject({ id: 'existing', source: 'source-b', target: 'target' });
  });

  it('inserts a uniquely compatible dropped node into an edge', () => {
    const source = node('source', [], [imagePort]);
    const target = node('target', [imagePort], []);
    const edge: Edge = {
      id: 'edge-to-insert', source: source.id, sourceHandle: 'image',
      target: target.id, targetHandle: 'image', data: { kind: 'data' }
    };
    const hook = renderHook(() => useGraph([source, target], [edge], [passThrough]));
    const edgeElement = document.createElement('div');
    edgeElement.className = 'react-flow__edge';
    edgeElement.dataset.id = edge.id;
    const event = {
      preventDefault: vi.fn(), target: edgeElement, clientX: 120, clientY: 80,
      dataTransfer: { getData: () => passThrough.type }
    } as unknown as React.DragEvent;

    act(() => hook.result.current.graph.onDrop(event));

    expect(hook.result.current.nodes).toHaveLength(3);
    expect(hook.result.current.nodes.find((item) => item.data.typeKey === passThrough.type)).toBeTruthy();
    expect(hook.result.current.edges).toHaveLength(2);
    expect(hook.result.current.edges.some((item) => item.id === edge.id)).toBe(false);
    expect(hook.result.current.edges.every((item) => item.data?.kind === 'data')).toBe(true);
  });

  it('bridges a compatible single-input single-output node when requested', () => {
    const source = node('source', [], [imagePort]);
    const middle = node('middle', [imagePort], [imagePort]);
    const target = node('target', [imagePort], []);
    const edges: Edge[] = [
      { id: 'in', source: source.id, sourceHandle: 'image', target: middle.id, targetHandle: 'image', data: { kind: 'data' } },
      { id: 'out', source: middle.id, sourceHandle: 'image', target: target.id, targetHandle: 'image', data: { kind: 'data' } }
    ];
    const hook = renderHook(() => useGraph([source, middle, target], edges));

    act(() => hook.result.current.graph.deleteElements(['middle'], [], true));

    expect(hook.result.current.nodes.map((item) => item.id)).toEqual(['source', 'target']);
    expect(hook.result.current.edges).toHaveLength(1);
    expect(hook.result.current.edges[0]).toMatchObject({ source: 'source', target: 'target', data: { kind: 'data' } });
  });
});
