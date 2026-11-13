/**
 * 编辑器画布图操作（巨型组件拆分，2026-11-13）：连线 / 添加 / 删除 / 复制 / 对齐 / 定位。
 *
 * 逐行搬运自 App 的 Editor 组件；只把闭包依赖改成显式参数，行为与执行顺序不变。
 */
import { useCallback, type Dispatch, type SetStateAction } from 'react';
import { addEdge, type Connection, type Edge, type Node, type OnConnectEnd, type OnEdgesChange, type OnNodesChange } from '@xyflow/react';
import { message } from 'antd';
import { catalogNodeData, makeDefaultParameters } from './catalog';
import { graphConnectionVerdict } from './validation';
import { withSwitchPorts } from './switchPorts';
import { compatiblePorts } from './toolboxModel';
import {
  NEW_NODE_HEIGHT, NEW_NODE_WIDTH, RECENT_NODES_LIMIT, SPOT_GAP, edgePresentation, getPorts, persistRecentNodeTypes,
  type ConnectionPicker, type NodeAttachment
} from './appEditorShared';
import type { NodeCatalogItem } from './types';

type MessageApi = ReturnType<typeof message.useMessage>[0];

export type EditorGraphOptions = {
  nodes: Node[];
  nodesRef: { current: Node[] };
  edgesRef: { current: Edge[] };
  selectedRef: { current: string };
  selectedId: string;
  selectedNode: Node | undefined;
  selectedNodeIds: string[];
  catalog: NodeCatalogItem[];
  setNodes: Dispatch<SetStateAction<Node[]>>;
  setEdges: Dispatch<SetStateAction<Edge[]>>;
  setSelectedId: Dispatch<SetStateAction<string>>;
  setRecentNodeTypes: Dispatch<SetStateAction<string[]>>;
  setConnectionPicker: (value: ConnectionPicker | undefined) => void;
  setNodeContextMenu: (value: { x: number; y: number; nodeId: string } | undefined) => void;
  messageApi: MessageApi;
  pushHistory: (label: string, coalesceKey?: string) => void;
  onNodesChange: OnNodesChange;
  onEdgesChange: OnEdgesChange;
  flowAreaRef: { current: HTMLDivElement | null };
  getZoom: () => number;
  screenToFlowPosition: (position: { x: number; y: number }) => { x: number; y: number };
  setCenter: (x: number, y: number, options?: { zoom?: number; duration?: number }) => void;
  clipboardRef: { current: { nodes: Node[]; edges: Edge[] } | null };
  pasteIndexRef: { current: number };
};

export function useEditorGraphOperations(options: EditorGraphOptions) {
  const {
    nodes, nodesRef, edgesRef, selectedRef, selectedId, selectedNode, selectedNodeIds, catalog,
    setNodes, setEdges, setSelectedId, setRecentNodeTypes, setConnectionPicker, setNodeContextMenu,
    messageApi, pushHistory, onNodesChange, onEdgesChange, flowAreaRef, getZoom,
    screenToFlowPosition, setCenter, clipboardRef, pasteIndexRef
  } = options;
  const onConnect = useCallback((connection: Connection) => {
    const getNode = (id: string) => nodesRef.current.find(node => node.id === id);
    const sourceNode = getNode(connection.source);
    const sourcePort = getPorts(sourceNode, 'outputs').find((p) => p.name === connection.sourceHandle);
    const verdict = graphConnectionVerdict(connection, getNode, edgesRef.current);
    if (!verdict.allowed) {
      messageApi.error(verdict.reason ?? '不允许的连接');
      return;
    }

    const kind: 'control' | 'data' = sourcePort?.dataType === 'Control' ? 'control' : 'data';
    pushHistory('连线');
    setEdges((eds) => addEdge({
      ...connection,
      id: crypto.randomUUID(),
      ...edgePresentation(kind)
    }, eds));
  }, [messageApi, pushHistory, setEdges]);
  const handleNodesChange = useCallback<OnNodesChange>((changes) => {
    if (changes.some((change) => change.type === 'remove')) pushHistory('删除节点');
    onNodesChange(changes);
  }, [onNodesChange, pushHistory]);
  const handleEdgesChange = useCallback<OnEdgesChange>((changes) => {
    if (changes.some((change) => change.type === 'remove')) pushHistory('删除连线');
    onEdgesChange(changes);
  }, [onEdgesChange, pushHistory]);
  const isValidConnection = useCallback((connection: Edge | Connection) => {
    if (!connection.source || !connection.target) return false;
    return graphConnectionVerdict(connection, id => nodesRef.current.find(node => node.id === id), edgesRef.current).allowed;
  }, []);
  const rememberRecentNode = useCallback((typeKey: string) => {
    setRecentNodeTypes((current) => {
      const next = [typeKey, ...current.filter((x) => x !== typeKey)].slice(0, RECENT_NODES_LIMIT);
      persistRecentNodeTypes(next);
      return next;
    });
  }, []);
  // 双击 / 回车添加节点时的落点：在屏幕坐标下用画布节点的真实矩形（DOM rect）判断是否重叠，
  // 在可见区内按网格找既不与已有节点相交、又完整可见的空位（优先从选中节点右侧开始扫描），
  // 确实没有空位时按已有节点数量斜向错位，保证新节点始终可见且不会被完全压住
  const freeNodeSpot = useCallback((): { x: number; y: number } => {
    const area = flowAreaRef.current?.getBoundingClientRect();
    if (!area) return { x: 120, y: 120 };

    const zoom = getZoom();
    const width = NEW_NODE_WIDTH * zoom;
    const height = NEW_NODE_HEIGHT * zoom;
    const gap = SPOT_GAP * zoom;
    const occupied = nodes
      .map((node) => document.querySelector<HTMLElement>(`.react-flow__node[data-id="${node.id}"]`)?.getBoundingClientRect())
      .filter((box): box is DOMRect => Boolean(box));
    const free = (x: number, y: number) =>
      occupied.every((box) => x + width + gap <= box.left || x >= box.right + gap || y + height + gap <= box.top || y >= box.bottom + gap);
    const fits = (x: number, y: number) => x >= area.left && x + width <= area.right && y >= area.top && y + height <= area.bottom;

    const origins: { x: number; y: number }[] = [];
    const selected = nodes.find((node) => node.id === selectedId);
    if (selected) {
      const box = document.querySelector<HTMLElement>(`.react-flow__node[data-id="${selected.id}"]`)?.getBoundingClientRect();
      if (box) origins.push({ x: box.right + gap + 16, y: box.top });
    }
    origins.push({ x: area.left + gap, y: area.top + gap });

    const stepX = width + gap;
    const stepY = height + gap;
    const columns = Math.max(1, Math.floor(area.width / stepX));
    const rows = Math.max(1, Math.floor(area.height / stepY));

    for (const origin of origins) {
      for (let row = 0; row < rows; row += 1) {
        for (let column = 0; column < columns; column += 1) {
          const x = origin.x + column * stepX;
          const y = origin.y + row * stepY;
          if (fits(x, y) && free(x, y)) return screenToFlowPosition({ x, y });
        }
      }
    }

    const cascade = nodes.length % 6;
    const x = Math.max(area.left, Math.min(area.right - width, area.left + gap + cascade * 26));
    const y = Math.max(area.top, Math.min(area.bottom - height, area.top + gap + cascade * 20));
    return screenToFlowPosition({ x, y });
  }, [getZoom, nodes, screenToFlowPosition, selectedId]);
  const addNode = useCallback((typeKey: string, position?: { x: number; y: number }, attachment?: NodeAttachment) => {
    const item = catalog.find((x) => x.type === typeKey);
    if (!item) return;
    const defaults = makeDefaultParameters(item);
    const nodeItem = withSwitchPorts(item, defaults);
    if (attachment) {
      const existing = nodesRef.current.find((node) => node.id === attachment.nodeId);
      const side = attachment.direction === 'source' ? 'outputs' : 'inputs';
      const port = getPorts(existing, side).find((port) => port.name === attachment.portName);
      if (!existing || port?.dataType !== attachment.dataType || !compatiblePorts(nodeItem, attachment).some((port) => port.name === attachment.newPort)) {
        messageApi.warning('原节点或端口已变化，请重新连接');
        setConnectionPicker(undefined);
        return;
      }
      if (attachment.direction === 'target' && edgesRef.current.some((edge) => edge.target === attachment.nodeId && edge.targetHandle === attachment.portName && edge.data?.kind === 'data')) {
        messageApi.warning('该输入端口已有数据源，请先删除原连线');
        return;
      }
    }

    const newNode: Node = {
      id: `${typeKey}-${crypto.randomUUID().slice(0, 8)}`,
      type: 'vision',
      className: 'node-added',
      position: position ?? freeNodeSpot(),
      data: {
        typeKey,
        label: item.displayName,
        catalogDisplayName: item.displayName,
        parameters: defaults,
        ...catalogNodeData(nodeItem),
        status: 'idle',
        breakpoint: false
      }
    };
    pushHistory(attachment ? '添加并连接节点' : '添加节点');
    setNodes((current) => [
      ...current.map((node) => (node.selected ? { ...node, selected: false } : node)),
      { ...newNode, selected: true }
    ]);
    setSelectedId(newNode.id);
    rememberRecentNode(typeKey);
    if (attachment) {
      const forward = attachment.direction === 'source';
      setEdges((current) => addEdge({
        id: crypto.randomUUID(),
        source: forward ? attachment.nodeId : newNode.id,
        sourceHandle: forward ? attachment.portName : attachment.newPort,
        target: forward ? newNode.id : attachment.nodeId,
        targetHandle: forward ? attachment.newPort : attachment.portName,
        ...edgePresentation(attachment.dataType === 'Control' ? 'control' : 'data')
      }, current));
      setConnectionPicker(undefined);
    }
    void messageApi.success(`已添加「${item.displayName}」${attachment ? '并连接' : ''}`, 1.5);
  }, [catalog, freeNodeSpot, messageApi, pushHistory, rememberRecentNode, setEdges, setNodes]);
  const onConnectEnd = useCallback<OnConnectEnd>((event, state) => {
    if (state.isValid || state.toNode || !state.fromNode || !state.fromHandle) return;
    if (!(event.target as Element)?.closest('.react-flow__pane')) return;
    const area = flowAreaRef.current?.getBoundingClientRect();
    const point = 'changedTouches' in event ? event.changedTouches[0] : event;
    if (!area || !point) return;
    const direction = state.fromHandle.type;
    const port = getPorts(state.fromNode, direction === 'source' ? 'outputs' : 'inputs').find((port) => port.name === state.fromHandle.id);
    if (!port) return;
    setNodeContextMenu(undefined);
    setConnectionPicker({
      nodeId: state.fromNode.id, portName: port.name, dataType: port.dataType, direction,
      x: Math.max(8, Math.min(point.clientX - area.left, area.width - 308)),
      y: Math.max(8, Math.min(point.clientY - area.top, area.height - 468)),
      position: screenToFlowPosition({ x: point.clientX, y: point.clientY })
    });
  }, [screenToFlowPosition]);
  const onDrop = useCallback((event: React.DragEvent) => {
    event.preventDefault();
    addNode(event.dataTransfer.getData('application/vision-node'), screenToFlowPosition({ x: event.clientX, y: event.clientY }));
  }, [addNode, screenToFlowPosition]);
  const focusNode = useCallback((nodeId: string) => {
    const node = nodesRef.current.find((item) => item.id === nodeId);
    if (!node) return;
    setSelectedId(nodeId);
    setNodes((current) => current.map((item) => (item.selected === (item.id === nodeId) ? item : { ...item, selected: item.id === nodeId })));
    const width = node.measured?.width ?? 200;
    const height = node.measured?.height ?? 120;
    setCenter(node.position.x + width / 2, node.position.y + height / 2, { zoom: 1.05, duration: 300 });
  }, [setCenter, setNodes]);
  const alignSelection = useCallback((mode: 'left' | 'centerX' | 'right' | 'top' | 'centerY' | 'bottom') => {
    const ids = new Set(selectedNodeIds);
    if (ids.size < 2) return;
    const targets = nodesRef.current.filter((node) => ids.has(node.id));
    if (targets.length < 2) return;
    const sizeOf = (node: Node) => ({ width: node.measured?.width ?? 200, height: node.measured?.height ?? 120 });
    const left = Math.min(...targets.map((node) => node.position.x));
    const right = Math.max(...targets.map((node) => node.position.x + sizeOf(node).width));
    const top = Math.min(...targets.map((node) => node.position.y));
    const bottom = Math.max(...targets.map((node) => node.position.y + sizeOf(node).height));
    pushHistory('对齐节点');
    setNodes((current) => current.map((node) => {
      if (!ids.has(node.id)) return node;
      const { width, height } = sizeOf(node);
      const position = { ...node.position };
      if (mode === 'left') position.x = left;
      else if (mode === 'centerX') position.x = (left + right) / 2 - width / 2;
      else if (mode === 'right') position.x = right - width;
      else if (mode === 'top') position.y = top;
      else if (mode === 'centerY') position.y = (top + bottom) / 2 - height / 2;
      else if (mode === 'bottom') position.y = bottom - height;
      return { ...node, position };
    }));
  }, [pushHistory, selectedNodeIds, setNodes]);
  const toggleBreakpoint = () => {
    if (!selectedId) return messageApi.warning('先选择一个节点');
    setNodes((current) => current.map((node) =>
      node.id === selectedId
        ? { ...node, data: { ...node.data, breakpoint: !node.data.breakpoint } }
        : node
    ));
  };
  const copySelection = useCallback(() => {
    const ids = new Set(nodesRef.current.filter((node) => node.selected).map((node) => node.id));
    if (ids.size === 0 && selectedRef.current) ids.add(selectedRef.current);
    if (ids.size === 0) return false;
    clipboardRef.current = {
      nodes: nodesRef.current.filter((node) => ids.has(node.id)).map((node) => ({ ...node, data: { ...node.data } })),
      edges: edgesRef.current.filter((edge) => ids.has(edge.source) && ids.has(edge.target)).map((edge) => ({ ...edge }))
    };
    pasteIndexRef.current = 0;
    void messageApi.success(`已复制 ${ids.size} 个节点`);
    return true;
  }, [messageApi]);
  const deleteElements = useCallback((nodeIds: string[], edgeIds: string[]) => {
    if (nodeIds.length === 0 && edgeIds.length === 0) return;
    const nodeIdSet = new Set(nodeIds);
    const edgeIdSet = new Set(edgeIds);
    pushHistory(nodeIds.length > 0 && edgeIds.length > 0 ? '删除节点和连线' : nodeIds.length > 0 ? '删除节点' : '删除连线');
    if (nodeIdSet.size > 0) setNodes((current) => current.filter((node) => !nodeIdSet.has(node.id)));
    setEdges((current) => current.filter((edge) =>
      !edgeIdSet.has(edge.id) && !nodeIdSet.has(edge.source) && !nodeIdSet.has(edge.target)
    ));
    setSelectedId((current) => (current && nodeIdSet.has(current) ? '' : current));
    const deleted = [
      nodeIds.length > 0 ? `${nodeIds.length} 个节点` : '',
      edgeIds.length > 0 ? `${edgeIds.length} 条连线` : ''
    ].filter(Boolean).join('和');
    void messageApi.success(`已删除 ${deleted}`);
  }, [messageApi, pushHistory, setEdges, setNodes]);


  const applyCalibrationParameters = (parameters: Record<string, unknown>) => {
    const typeKey = String(selectedNode?.data.typeKey ?? '');
    const supported = ['calibration.planar', 'calibration.cameraIntrinsics', 'image.undistort', 'calibration.handEye2D', 'calibration.handEye3D'];
    if (!selectedId || !supported.includes(typeKey)) {
      messageApi.warning('请先选择受支持的标定节点');
      return;
    }
    setNodes((current) => current.map((node) =>
      node.id === selectedId
        ? { ...node, data: { ...node.data, parameters: { ...((node.data.parameters ?? {}) as Record<string, unknown>), ...parameters } } }
        : node
    ));
    messageApi.success(`标定参数已写入节点（${typeKey}）`);
  };
  return {
    onConnect, handleNodesChange, handleEdgesChange, isValidConnection, rememberRecentNode, freeNodeSpot,
    addNode, onConnectEnd, onDrop, focusNode, alignSelection, toggleBreakpoint, copySelection, deleteElements,
    applyCalibrationParameters
  };
}
