import { act, renderHook } from '@testing-library/react';
import { message } from 'antd';
import { useRef, useState } from 'react';
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

function useGraph(initialNodes: Node[], initialEdges: Edge[], catalog: NodeCatalogItem[] = []) {
  const [nodes, setNodes] = useState(initialNodes);
  const [edges, setEdges] = useState(initialEdges);
  const [selectedId, setSelectedId] = useState('');
  const nodesRef = useRef(nodes);
  const edgesRef = useRef(edges);
  nodesRef.current = nodes;
  edgesRef.current = edges;
  const [messageApi] = message.useMessage();
  const graph = useEditorGraphOperations({
    nodes, nodesRef, edgesRef, selectedRef: { current: selectedId }, selectedId,
    selectedNode: nodes.find((item) => item.id === selectedId), selectedNodeIds: [], catalog,
    setNodes, setEdges, setSelectedId, setRecentNodeTypes: vi.fn(),
    setConnectionPicker: vi.fn(), setNodeContextMenu: vi.fn(), messageApi,
    pushHistory: vi.fn(), onNodesChange: vi.fn(), onEdgesChange: vi.fn(),
    flowAreaRef: { current: null }, getZoom: () => 1,
    screenToFlowPosition: (position) => position, setCenter: vi.fn(),
    clipboardRef: { current: null }, pasteIndexRef: { current: 0 }
  });
  return { nodes, edges, graph, selectedId };
}

describe('graph editing operations', () => {
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
