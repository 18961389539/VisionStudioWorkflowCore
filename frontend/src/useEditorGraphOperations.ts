/**
 * 编辑器画布图操作（巨型组件拆分，2026-11-13）：连线 / 添加 / 删除 / 复制 / 对齐 / 定位。
 *
 * 逐行搬运自 App 的 Editor 组件；只把闭包依赖改成显式参数，行为与执行顺序不变。
 */
import { useCallback, useLayoutEffect, useRef, type Dispatch, type MouseEvent as ReactMouseEvent, type SetStateAction, type TouchEvent as ReactTouchEvent } from 'react';
import { addEdge, reconnectEdge, type Connection, type Edge, type FitViewOptions, type Node, type OnConnectEnd, type OnEdgesChange, type OnNodesChange, type OnReconnect } from '@xyflow/react';
import { message } from 'antd';
import { catalogNodeData, makeDefaultParameters } from './catalog';
import { graphConnectionVerdict } from './validation';
import { computeAutoLayoutPositions } from './graphLayout';
import { withSwitchPorts } from './switchPorts';
import { compatiblePorts } from './toolboxModel';
import { planControlFanout } from './smartBranching';
import {
  NEW_NODE_HEIGHT, NEW_NODE_WIDTH, RECENT_NODES_LIMIT, SPOT_GAP, edgePresentation, getPorts, persistRecentNodeTypes,
  type ConnectionPicker, type NodeAttachment
} from './appEditorShared';
import { absoluteNodePosition, createCanvasGroup, createCanvasNote, isCanvasAnnotation, isCanvasGroup } from './canvasAnnotations';
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
  fitView?: (options?: FitViewOptions) => Promise<boolean>;
  clipboardRef: { current: { nodes: Node[]; edges: Edge[] } | null };
  pasteIndexRef: { current: number };
};

export function useEditorGraphOperations(options: EditorGraphOptions) {
  const {
    nodes, selectedId, selectedNode, catalog,
    setNodes, setEdges, setSelectedId, setRecentNodeTypes, setConnectionPicker, setNodeContextMenu,
    messageApi, pushHistory, onNodesChange, onEdgesChange, getZoom,
    screenToFlowPosition, setCenter
  } = options;
  const refsRef = useRef(options);
  refsRef.current = options;
  const committedSelection = useRef({ nodes, selectedNodeIds: options.selectedNodeIds });
  useLayoutEffect(() => {
    committedSelection.current = { nodes, selectedNodeIds: options.selectedNodeIds };
  }, [nodes, options.selectedNodeIds]);
  const reconnectingEdgeIdRef = useRef<string | undefined>(undefined);

  const onConnect = useCallback((connection: Connection) => {
    const { nodesRef, edgesRef } = refsRef.current;
    const getNode = (id: string) => nodesRef.current.find(node => node.id === id);
    const sourceNode = getNode(connection.source);
    const sourcePort = getPorts(sourceNode, 'outputs').find((p) => p.name === connection.sourceHandle);
    const verdict = graphConnectionVerdict(connection, getNode, edgesRef.current);
    if (!verdict.allowed) {
      messageApi.error(verdict.reason ?? '不允许的连接');
      return;
    }

    const kind: 'control' | 'data' = sourcePort?.dataType === 'Control' ? 'control' : 'data';
    if (kind === 'control') {
      const plan = planControlFanout(connection, nodesRef.current, edgesRef.current);
      if (plan.kind === 'manual') {
        messageApi.warning(plan.reason);
        return;
      }
    }
    pushHistory('连线');
    setEdges((eds) => addEdge({
      ...connection,
      id: crypto.randomUUID(),
      ...edgePresentation(kind)
    }, eds));
  }, [messageApi, pushHistory, setEdges]);
  const onReconnect = useCallback<OnReconnect<Edge>>((oldEdge, connection) => {
    reconnectingEdgeIdRef.current = undefined;
    const { nodesRef, edgesRef } = refsRef.current;
    const getNode = (id: string) => nodesRef.current.find(node => node.id === id);
    const remainingEdges = edgesRef.current.filter((edge) => edge.id !== oldEdge.id);
    const verdict = graphConnectionVerdict(connection, getNode, remainingEdges);
    if (!verdict.allowed) {
      messageApi.error(verdict.reason ?? '不允许的连接');
      return;
    }

    const sourcePort = getPorts(getNode(connection.source), 'outputs').find((port) => port.name === connection.sourceHandle);
    const kind: 'control' | 'data' = sourcePort?.dataType === 'Control' ? 'control' : 'data';
    if (kind === 'control') {
      const plan = planControlFanout(connection, nodesRef.current, remainingEdges);
      if (plan.kind === 'manual') {
        messageApi.warning(plan.reason);
        return;
      }
    }
    pushHistory('重连');
    setEdges((current) => reconnectEdge(oldEdge, connection, current, { shouldReplaceId: false })
      .map((edge) => edge.id === oldEdge.id ? { ...edge, ...edgePresentation(kind) } : edge));
  }, [messageApi, pushHistory, setEdges]);
  const onReconnectStart = useCallback((_event: ReactMouseEvent<Element> | ReactTouchEvent<Element>, edge: Edge) => {
    reconnectingEdgeIdRef.current = edge.id;
  }, []);
  const onReconnectEnd = useCallback(() => {
    reconnectingEdgeIdRef.current = undefined;
  }, []);
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
    const { nodesRef, edgesRef } = refsRef.current;
    const edges = edgesRef.current.filter((edge) => edge.id !== reconnectingEdgeIdRef.current);
    return graphConnectionVerdict(connection, id => nodesRef.current.find(node => node.id === id), edges).allowed;
  }, []);
  const rememberRecentNode = useCallback((typeKey: string) => {
    setRecentNodeTypes((current) => {
      const next = [typeKey, ...current.filter((x) => x !== typeKey)].slice(0, RECENT_NODES_LIMIT);
      persistRecentNodeTypes(next);
      return next;
    });
  }, [setRecentNodeTypes]);
  // 双击 / 回车添加节点时的落点：在屏幕坐标下用画布节点的真实矩形（DOM rect）判断是否重叠，
  // 在可见区内按网格找既不与已有节点相交、又完整可见的空位（优先从选中节点右侧开始扫描），
  // 确实没有空位时按已有节点数量斜向错位，保证新节点始终可见且不会被完全压住
  const freeNodeSpot = useCallback((): { x: number; y: number } => {
    const area = refsRef.current.flowAreaRef.current?.getBoundingClientRect();
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
    const { nodesRef, edgesRef } = refsRef.current;
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
  }, [catalog, freeNodeSpot, messageApi, pushHistory, rememberRecentNode, setConnectionPicker, setEdges, setNodes, setSelectedId]);
  const insertNodeOnEdge = useCallback((typeKey: string, edgeId: string, position: { x: number; y: number}) => {
    const { nodesRef, edgesRef } = refsRef.current;
    const item = catalog.find((candidate) => candidate.type === typeKey);
    const oldEdge = edgesRef.current.find((edge) => edge.id === edgeId);
    if (!item || !oldEdge) return false;

    const defaults = makeDefaultParameters(item);
    const nodeItem = withSwitchPorts(item, defaults);
    const id = `${typeKey}-${crypto.randomUUID().slice(0, 8)}`;
    const newNode: Node = {
      id, type: 'vision', className: 'node-added', position,
      data: {
        typeKey, label: item.displayName, catalogDisplayName: item.displayName,
        parameters: defaults, ...catalogNodeData(nodeItem), status: 'idle', breakpoint: false
      }
    };
    const remainingEdges = edgesRef.current.filter((edge) => edge.id !== oldEdge.id);
    const getNode = (nodeId: string) => nodeId === id ? newNode : nodesRef.current.find((node) => node.id === nodeId);
    const sourcePort = getPorts(getNode(oldEdge.source), 'outputs').find((port) => port.name === oldEdge.sourceHandle);
    const inputPorts = getPorts(newNode, 'inputs');
    const outputPorts = getPorts(newNode, 'outputs');
    const compatibleRoutes: { input: string; output: string; kind: 'control' | 'data' }[] = [];

    for (const input of inputPorts) {
      const firstConnection: Connection = {
        source: oldEdge.source, sourceHandle: oldEdge.sourceHandle ?? null,
        target: newNode.id, targetHandle: input.name
      };
      if (!graphConnectionVerdict(firstConnection, getNode, remainingEdges).allowed) continue;
      const firstEdge: Edge = {
        ...firstConnection, id: 'insert-first-edge',
        data: { kind: input.dataType === 'Control' ? 'control' : 'data' }
      };
      for (const output of outputPorts) {
        const secondConnection: Connection = {
          source: newNode.id, sourceHandle: output.name,
          target: oldEdge.target, targetHandle: oldEdge.targetHandle ?? null
        };
        const secondVerdict = graphConnectionVerdict(secondConnection, getNode, [...remainingEdges, firstEdge]);
        if (secondVerdict.allowed) compatibleRoutes.push({
          input: input.name, output: output.name,
          kind: output.dataType === 'Control' ? 'control' : 'data'
        });
      }
    }
    if (compatibleRoutes.length !== 1) {
      messageApi.warning(compatibleRoutes.length === 0
        ? '此节点没有兼容的输入/输出端口，无法插入连线'
        : '此节点有多个兼容端口组合，请先添加节点后手动连线');
      return false;
    }

    const route = compatibleRoutes[0];
    if (!route) return false;
    const sourceKind = sourcePort?.dataType === 'Control' ? 'control' : 'data';
    pushHistory('插入节点并重连');
    setNodes((current) => [
      ...current.map((node) => node.selected ? { ...node, selected: false } : node),
      { ...newNode, selected: true }
    ]);
    setEdges((current) => [
      ...current.filter((edge) => edge.id !== oldEdge.id),
      {
        id: crypto.randomUUID(), source: oldEdge.source, sourceHandle: oldEdge.sourceHandle,
        target: newNode.id, targetHandle: route.input, ...edgePresentation(sourceKind)
      },
      {
        id: crypto.randomUUID(), source: newNode.id, sourceHandle: route.output,
        target: oldEdge.target, targetHandle: oldEdge.targetHandle, ...edgePresentation(route.kind)
      }
    ]);
    setSelectedId(newNode.id);
    rememberRecentNode(typeKey);
    void messageApi.success(`已将「${item.displayName}」插入连线`, 1.5);
    return true;
  }, [catalog, messageApi, pushHistory, rememberRecentNode, setEdges, setNodes, setSelectedId]);
  const onConnectEnd = useCallback<OnConnectEnd>((event, state) => {
    if (state.isValid || state.toNode || !state.fromNode || !state.fromHandle) return;
    if (!(event.target as Element)?.closest('.react-flow__pane')) return;
    const area = refsRef.current.flowAreaRef.current?.getBoundingClientRect();
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
  }, [screenToFlowPosition, setConnectionPicker, setNodeContextMenu]);
  const onDrop = useCallback((event: React.DragEvent) => {
    event.preventDefault();
    const typeKey = event.dataTransfer.getData('application/vision-node');
    const edgeElement = event.target instanceof Element ? event.target.closest('.react-flow__edge') : null;
    const edgeId = edgeElement?.getAttribute('data-id');
    const position = screenToFlowPosition({ x: event.clientX, y: event.clientY });
    if (edgeId && insertNodeOnEdge(typeKey, edgeId, position)) return;
    if (edgeId) return;
    addNode(typeKey, position);
  }, [addNode, insertNodeOnEdge, screenToFlowPosition]);
  const focusNode = useCallback((nodeId: string) => {
    const nodes = refsRef.current.nodesRef.current;
    const node = nodes.find((item) => item.id === nodeId);
    if (!node) return;
    setSelectedId(nodeId);
    setNodes((current) => current.map((item) => (item.selected === (item.id === nodeId) ? item : { ...item, selected: item.id === nodeId })));
    const width = node.measured?.width ?? 200;
    const height = node.measured?.height ?? 120;
    const position = absoluteNodePosition(node, new Map(nodes.map((item) => [item.id, item])));
    setCenter(position.x + width / 2, position.y + height / 2, { zoom: 1.05, duration: 300 });
  }, [setCenter, setNodes, setSelectedId]);
  const alignSelection = useCallback((mode: 'left' | 'centerX' | 'right' | 'top' | 'centerY' | 'bottom') => {
    const ids = new Set(committedSelection.current.selectedNodeIds);
    if (ids.size < 2) return;
    const targets = committedSelection.current.nodes.filter((node) => ids.has(node.id));
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
  }, [pushHistory, setNodes]);
  const toggleBreakpoint = () => {
    if (!selectedId) return messageApi.warning('先选择一个节点');
    setNodes((current) => current.map((node) =>
      node.id === selectedId
        ? { ...node, data: { ...node.data, breakpoint: !node.data.breakpoint } }
        : node
    ));
  };
  const updateNodeParameter = useCallback((nodeId: string, key: string, value: unknown) => {
    const node = refsRef.current.nodesRef.current.find((item) => item.id === nodeId);
    if (!node || isCanvasAnnotation(node) || Object.is((node.data.parameters as Record<string, unknown> | undefined)?.[key], value)) return;
    pushHistory('调整节点参数', `parameter:${nodeId}:${key}`);
    setNodes((current) => current.map((item) => item.id === nodeId
      ? { ...item, data: { ...item.data, parameters: { ...((item.data.parameters ?? {}) as Record<string, unknown>), [key]: value } } }
      : item));
  }, [pushHistory, setNodes]);
  const updateCanvasAnnotation = useCallback((nodeId: string, patch: Record<string, unknown>) => {
    const node = refsRef.current.nodesRef.current.find((item) => item.id === nodeId);
    if (!node || !isCanvasAnnotation(node)) return;
    const key = `annotation:${nodeId}:${Object.keys(patch).sort().join(',')}`;
    pushHistory('编辑画布说明', key);
    setNodes((current) => current.map((item) => item.id === nodeId
      ? { ...item, data: { ...item.data, ...patch } }
      : item));
  }, [pushHistory, setNodes]);
  const addCanvasNote = useCallback(() => {
    const { flowAreaRef, screenToFlowPosition } = refsRef.current;
    const bounds = flowAreaRef.current?.getBoundingClientRect();
    const position = bounds
      ? screenToFlowPosition({ x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 })
      : { x: 120, y: 120 };
    const note = createCanvasNote(position);
    pushHistory('添加画布便签');
    setNodes((current) => [...current.map((node) => ({ ...node, selected: false })), note]);
    setSelectedId(note.id);
  }, [pushHistory, setNodes, setSelectedId]);
  const createGroup = useCallback(() => {
    const { nodes: committedNodes, selectedNodeIds } = committedSelection.current;
    const result = createCanvasGroup(committedNodes, selectedNodeIds);
    if (!result.groupId) {
      messageApi.warning('先选择至少一个未分组的流程节点');
      return;
    }
    pushHistory('创建画布分组');
    setNodes(result.nodes);
    setSelectedId(result.groupId);
  }, [messageApi, pushHistory, setNodes, setSelectedId]);
  const copySelection = useCallback(() => {
    const { nodesRef, edgesRef, selectedRef, clipboardRef, pasteIndexRef } = refsRef.current;
    const ids = new Set(nodesRef.current.filter((node) => node.selected).map((node) => node.id));
    if (ids.size === 0 && selectedRef.current) ids.add(selectedRef.current);
    if (ids.size === 0) return false;
    for (const group of nodesRef.current.filter((node) => ids.has(node.id) && isCanvasGroup(node))) {
      if (Array.isArray(group.data.memberIds)) group.data.memberIds.forEach((id) => { if (typeof id === 'string') ids.add(id); });
    }
    const nodesById = new Map(nodesRef.current.map((node) => [node.id, node]));
    clipboardRef.current = {
      nodes: nodesRef.current.filter((node) => ids.has(node.id)).map((node) => {
        const includesParent = node.parentId ? ids.has(node.parentId) : false;
        const position = node.parentId && !includesParent ? absoluteNodePosition(node, nodesById) : node.position;
        return {
          ...node, ...(!includesParent && node.parentId ? { parentId: undefined, extent: undefined } : {}),
          position: { ...position }, data: { ...node.data }
        };
      }),
      edges: edgesRef.current.filter((edge) => ids.has(edge.source) && ids.has(edge.target)).map((edge) => ({ ...edge }))
    };
    pasteIndexRef.current = 0;
    void messageApi.success(`已复制 ${ids.size} 个节点`);
    return true;
  }, [messageApi]);
  const selectAllNodes = useCallback(() => {
    const currentNodes = refsRef.current.nodesRef.current;
    if (currentNodes.length === 0) return false;
    setNodes((current) => current.map((node) => (node.selected ? node : { ...node, selected: true })));
    if (!refsRef.current.selectedRef.current || !currentNodes.some((node) => node.id === refsRef.current.selectedRef.current)) {
      setSelectedId(currentNodes[0].id);
    }
    return true;
  }, [setNodes, setSelectedId]);
  const duplicateSelection = useCallback(() => {
    const { nodesRef, edgesRef, selectedRef } = refsRef.current;
    const ids = new Set(nodesRef.current.filter((node) => node.selected).map((node) => node.id));
    if (ids.size === 0 && selectedRef.current) ids.add(selectedRef.current);
    for (const group of nodesRef.current.filter((node) => ids.has(node.id) && isCanvasGroup(node))) {
      if (Array.isArray(group.data.memberIds)) group.data.memberIds.forEach((id) => { if (typeof id === 'string') ids.add(id); });
    }
    const sourceNodes = nodesRef.current.filter((node) => ids.has(node.id));
    if (sourceNodes.length === 0) return false;
    const idMap = new Map<string, string>();
    sourceNodes.forEach((node) => idMap.set(node.id, `${node.type ?? node.data.typeKey ?? 'node'}-${crypto.randomUUID().slice(0, 8)}`));
    const nodesById = new Map(nodesRef.current.map((node) => [node.id, node]));
    const duplicatedNodes: Node[] = sourceNodes.map((node) => {
      const id = idMap.get(node.id)!;
      const duplicateParentId = node.parentId ? idMap.get(node.parentId) : undefined;
      const position = node.parentId && !duplicateParentId ? absoluteNodePosition(node, nodesById) : node.position;
      const data = isCanvasGroup(node) && Array.isArray(node.data.memberIds)
        ? { ...node.data, memberIds: node.data.memberIds.map((memberId) => idMap.get(String(memberId))).filter((memberId): memberId is string => Boolean(memberId)) }
        : { ...node.data };
      return {
        ...node,
        id,
        ...(node.parentId ? { parentId: duplicateParentId, extent: duplicateParentId ? node.extent : undefined } : {}),
        selected: true,
        position: { x: position.x + (duplicateParentId ? 0 : 40), y: position.y + (duplicateParentId ? 0 : 40) },
        measured: undefined,
        data: { ...data, status: 'idle', breakpoint: false, durationMs: undefined, error: undefined, disposition: undefined, summary: undefined }
      };
    });
    const duplicatedEdges: Edge[] = edgesRef.current
      .filter((edge) => ids.has(edge.source) && ids.has(edge.target))
      .map((edge) => ({
        ...edge,
        id: crypto.randomUUID(),
        source: idMap.get(edge.source) ?? edge.source,
        target: idMap.get(edge.target) ?? edge.target
      }));
    pushHistory('创建节点副本');
    setNodes((current) => [...current.map((node) => (node.selected ? { ...node, selected: false } : node)), ...duplicatedNodes]);
    setEdges((current) => [...current, ...duplicatedEdges]);
    setSelectedId(duplicatedNodes[0].id);
    void messageApi.success(`已复制 ${duplicatedNodes.length} 个节点`);
    return true;
  }, [messageApi, pushHistory, setEdges, setNodes, setSelectedId]);
  const autoLayout = useCallback(() => {
    const { nodesRef, edgesRef, fitView: fitCanvas } = refsRef.current;
    const layoutNodes = nodesRef.current.filter((node) => !isCanvasAnnotation(node) && !node.parentId);
    if (layoutNodes.length === 0) return;
    const layoutNodeIds = new Set(layoutNodes.map((node) => node.id));
    const layoutEdges = edgesRef.current.filter((edge) => layoutNodeIds.has(edge.source) && layoutNodeIds.has(edge.target));
    const nextPositions = computeAutoLayoutPositions(layoutNodes, layoutEdges);
    pushHistory('自动整理布局');
    setNodes((current) => current.map((node) => {
      const position = nextPositions.get(node.id);
      return position ? { ...node, position } : node;
    }));
    if (fitCanvas) {
      window.setTimeout(() => { void fitCanvas({ padding: 0.16, duration: 200 }); }, 0);
    }
    void messageApi.success('已自动整理画布布局');
  }, [messageApi, pushHistory, setNodes]);
  const deleteElements = useCallback((nodeIds: string[], edgeIds: string[], bridge = false) => {
    if (nodeIds.length === 0 && edgeIds.length === 0) return;
    const nodeIdSet = new Set(nodeIds);
    const edgeIdSet = new Set(edgeIds);
    if (bridge && nodeIds.length === 1 && edgeIds.length === 0) {
      const nodeId = nodeIds[0];
      const { nodesRef, edgesRef } = refsRef.current;
      const incoming = edgesRef.current.filter((edge) => edge.target === nodeId);
      const outgoing = edgesRef.current.filter((edge) => edge.source === nodeId);
      if (incoming.length !== 1 || outgoing.length !== 1) {
        messageApi.warning('只能桥接恰有一条输入和一条输出连线的中间节点');
        return;
      }
      const upstream = incoming[0];
      const downstream = outgoing[0];
      const remainingEdges = edgesRef.current.filter((edge) => edge.id !== upstream.id && edge.id !== downstream.id);
      const connection: Connection = {
        source: upstream.source, sourceHandle: upstream.sourceHandle ?? null,
        target: downstream.target, targetHandle: downstream.targetHandle ?? null
      };
      const verdict = graphConnectionVerdict(connection,
        (id) => nodesRef.current.find((node) => node.id === id), remainingEdges);
      if (!verdict.allowed) {
        messageApi.warning(`无法安全桥接：${verdict.reason ?? '端口不兼容'}`);
        return;
      }
      const sourcePort = getPorts(nodesRef.current.find((node) => node.id === connection.source), 'outputs')
        .find((port) => port.name === connection.sourceHandle);
      const kind = sourcePort?.dataType === 'Control' ? 'control' : 'data';
      pushHistory('删除并桥接节点');
      setNodes((current) => current.filter((node) => node.id !== nodeId));
      setEdges((current) => [
        ...current.filter((edge) => edge.id !== upstream.id && edge.id !== downstream.id),
        { ...connection, id: crypto.randomUUID(), ...edgePresentation(kind) }
      ]);
      setSelectedId((current) => current === nodeId ? '' : current);
      void messageApi.success('已删除节点并桥接前后连线');
      return;
    }
    pushHistory(nodeIds.length > 0 && edgeIds.length > 0 ? '删除节点和连线' : nodeIds.length > 0 ? '删除节点' : '删除连线');
    if (nodeIdSet.size > 0) {
      const currentNodes = refsRef.current.nodesRef.current;
      const deletingGroupIds = new Set(currentNodes.filter((node) => nodeIdSet.has(node.id) && isCanvasGroup(node)).map((node) => node.id));
      const nodesById = new Map(currentNodes.map((node) => [node.id, node]));
      setNodes((current) => current.filter((node) => !nodeIdSet.has(node.id)).map((node) => {
        if (node.parentId && deletingGroupIds.has(node.parentId)) {
          return { ...node, position: absoluteNodePosition(node, nodesById), parentId: undefined, extent: undefined };
        }
        if (isCanvasGroup(node)) {
          const memberIds = Array.isArray(node.data.memberIds) ? node.data.memberIds as string[] : [];
          return { ...node, data: { ...node.data, memberIds: memberIds.filter((id) => !nodeIdSet.has(id)) } };
        }
        return node;
      }));
    }
    setEdges((current) => current.filter((edge) =>
      !edgeIdSet.has(edge.id) && !nodeIdSet.has(edge.source) && !nodeIdSet.has(edge.target)
    ));
    setSelectedId((current) => (current && nodeIdSet.has(current) ? '' : current));
    const deleted = [
      nodeIds.length > 0 ? `${nodeIds.length} 个节点` : '',
      edgeIds.length > 0 ? `${edgeIds.length} 条连线` : ''
    ].filter(Boolean).join('和');
    void messageApi.success(`已删除 ${deleted}`);
  }, [messageApi, pushHistory, setEdges, setNodes, setSelectedId]);


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
    onConnect, onReconnect, onReconnectStart, onReconnectEnd, handleNodesChange, handleEdgesChange, isValidConnection, rememberRecentNode, freeNodeSpot,
    addNode, onConnectEnd, onDrop, focusNode, alignSelection, autoLayout, toggleBreakpoint, copySelection,
    selectAllNodes, duplicateSelection, deleteElements, applyCalibrationParameters,
    updateNodeParameter, updateCanvasAnnotation, addCanvasNote, createGroup
  };
}
