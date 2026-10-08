import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import {
  addEdge,
  Background,
  Controls,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  useEdgesState,
  useNodesState,
  useReactFlow,
  type Connection,
  type Edge,
  type Node,
  type OnEdgesChange,
  type OnConnectEnd,
  type OnNodesChange
} from '@xyflow/react';
import { Alert, Button, Checkbox, Dropdown, Empty, Input, Menu, message, Modal, Segmented, Select, Switch, Table, Tag, Tooltip, Typography, type InputRef, type MenuProps } from 'antd';
import Toolbox from './components/Toolbox';
import PropertyPanel from './components/PropertyPanel';
import RunPanel from './components/RunPanel';
import RunObservabilityPanel from './components/RunObservabilityPanel';
import ImageViewer from './components/ImageViewer';
import CameraPanel from './components/CameraPanel';
import RobotPanel from './components/RobotPanel';
import DevicePanel from './components/DevicePanel';
import JobPanel from './components/JobPanel';
import TracePanel from './components/TracePanel';
import DatasetValidationPanel from './components/DatasetValidationPanel';
import ParameterTuningPanel from './components/ParameterTuningPanel';
import WorkflowModulePanel from './components/WorkflowModulePanel';
import CalibrationPanel from './components/CalibrationPanel';
import FrameTreePanel from './components/FrameTreePanel';
import ProductionPanel from './components/ProductionPanel';
import DiagnosticsPanel from './components/DiagnosticsPanel';
import HardwareProvenancePanel from './components/HardwareProvenancePanel';
import PluginPackagePanel from './components/PluginPackagePanel';
import AboutPanel from './components/AboutPanel';
import SplitHandle from './components/SplitHandle';
import { LAYOUT_LIMITS, LAYOUT_PRESETS, MIN_CENTER_WIDTH, MIN_WORKSPACE_HEIGHT, clampNumber, persistLayout, readLayout, type LayoutPreset, type LayoutState } from './layout';
import VisionNode from './components/VisionNode';
import { fallbackCatalog, fallbackCatalogHash, fallbackCatalogSchemaVersion, makeDefaultParameters, validateCatalog } from './catalog';
import { dateLocale, getInitialLang, localizeCatalog } from './i18n';
import { APP_VERSION } from './version';
import { useThemeMode } from './theme';
import { demos, demoDescriptions, linearDemo, type DemoKey } from './demos';
import { GraphHistory, captureGraph, restoreGraph, type GraphSnapshot } from './history';
import { clearDraft, readDraft, writeDraft, type WorkflowDraft } from './draft';
import { connectionVerdict, serverIssueFromMessage, validateWorkflow, type WorkflowIssue } from './validation';
import { onShowNodeDetails } from './uiBridge';
import { useRunController, type BottomTab } from './useRunController';
import { useRunImages } from './useRunImages';
import { compatiblePorts, type PortRecommendation } from './toolboxModel';
import type { NodeCatalogItem, PluginLoadInfo, PluginSdkInfo, PortDescriptor, VisionRoi, WorkflowPayload, WorkflowModuleParameter, WorkflowModuleVersion } from './types';

const nodeTypes = { vision: VisionNode };
type NodeAttachment = PortRecommendation & { nodeId: string; portName: string; newPort: string };
type ConnectionPicker = PortRecommendation & { nodeId: string; portName: string; x: number; y: number; position: { x: number; y: number } };

// 节点紧凑模式：全局显示偏好（压缩标题 / 端口间距 / 页脚），持久化在 localStorage
const NODE_COMPACT_KEY = 'visionstudio.node-compact';
const readNodeCompact = () => {
  try { return window.localStorage.getItem(NODE_COMPACT_KEY) === '1'; } catch { return false; }
};
const persistNodeCompact = (value: boolean) => {
  try { window.localStorage.setItem(NODE_COMPACT_KEY, value ? '1' : '0'); } catch { /* 忽略存储失败 */ }
};

// 自动适配视图的内缩：顶部用固定像素为画布左上角的浮动工具条（查找/紧凑）留出空间。
// 分数制的实际内缩是 viewport*(1-1/(1+p))/2（250px 高的画布仅约 17px），会被固定高度的工具条压住节点标题。
// 类型对齐 @xyflow/react 的 PaddingWithUnit（number | `${number}px` | `${number}%`），避免任意 string 撞类型。
type FitPaddingUnit = number | `${number}px` | `${number}%`;
const CANVAS_FIT_PADDING: { top: FitPaddingUnit; right: FitPaddingUnit; bottom: FitPaddingUnit; left: FitPaddingUnit } =
  { top: '56px', right: 0.16, bottom: 0.16, left: 0.16 };

function hydrateNodesFromCatalog(nodes: Node[], catalog: NodeCatalogItem[]): Node[] {
  const byType = new Map(catalog.map((item) => [item.type.toLowerCase(), item]));
  return nodes.map((node) => {
    const typeKey = String(node.data.typeKey ?? '');
    const item = byType.get(typeKey.toLowerCase());
    if (!item) return node;

    const previousCatalogLabel = typeof node.data.catalogDisplayName === 'string' ? node.data.catalogDisplayName : undefined;
    const currentLabel = typeof node.data.label === 'string' ? node.data.label : typeKey;
    const label = currentLabel === typeKey || currentLabel === previousCatalogLabel ? item.displayName : currentLabel;
    return {
      ...node,
      data: {
        ...node.data,
        label,
        catalogDisplayName: item.displayName,
        parameters: { ...makeDefaultParameters(item), ...((node.data.parameters ?? {}) as Record<string, unknown>) },
        inputs: item.inputs,
        outputs: item.outputs
      }
    };
  });
}

const initialGraph = linearDemo();
const initial = { ...initialGraph, nodes: hydrateNodesFromCatalog(initialGraph.nodes, fallbackCatalog) };

function getPorts(node: Node | undefined, key: 'inputs' | 'outputs'): PortDescriptor[] {
  return ((node?.data?.[key] ?? []) as PortDescriptor[]);
}


function moduleMetadata(parameters: Record<string, unknown>) {
  const inputs = Array.isArray(parameters.__moduleInputs) ? parameters.__moduleInputs as PortDescriptor[] : [];
  const outputs = Array.isArray(parameters.__moduleOutputs) ? parameters.__moduleOutputs as PortDescriptor[] : [];
  const moduleParameters = Array.isArray(parameters.__moduleParameters) ? parameters.__moduleParameters as WorkflowModuleParameter[] : [];
  return { inputs, outputs, moduleParameters };
}

function moduleCatalogFromNode(node?: Node): NodeCatalogItem | undefined {
  if (!node || String(node.data.typeKey) !== 'module.call') return undefined;
  const parameters = (node.data.parameters ?? {}) as Record<string, unknown>;
  const meta = moduleMetadata(parameters);
  return {
    type: 'module.call',
    displayName: `可复用模块 · ${String(parameters.moduleId ?? node.data.label ?? node.id)} · V${String(parameters.moduleVersion ?? '?')}`,
    category: '模块',
    inputs: meta.inputs,
    outputs: meta.outputs,
    parameters: meta.moduleParameters.map((x) => ({
      name: x.name, label: x.label, type: x.type as any, defaultValue: x.defaultValue, min: x.min, max: x.max, step: x.step, options: x.options, unit: x.unit, group: x.group, description: x.description
    })),
    description: 'Immutable reusable Workflow Module call. The pinned module version is expanded before compile/execution.',
    pluginId: 'module',
    capabilities: { deterministic: true, supportsRunNode: false, supportsParallel: true, executionMode: 'Auto' }
  };
}

function edgePresentation(kind: 'control' | 'data') {
  return kind === 'control'
    ? { animated: true, data: { kind }, style: { strokeDasharray: '7 5' } }
    : { animated: false, data: { kind } };
}

const RECENT_NODES_KEY = 'visionstudio.recent-node-types';
const RECENT_NODES_LIMIT = 6;
// 双击添加时的占位尺寸与最小间距（流程坐标）；实际渲染尺寸在 measured 中回读
const NEW_NODE_WIDTH = 220;
const NEW_NODE_HEIGHT = 180;
const SPOT_GAP = 24;

function readRecentNodeTypes(): string[] {
  try {
    const raw = window.localStorage.getItem(RECENT_NODES_KEY);
    const parsed = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed)
      ? parsed.filter((value): value is string => typeof value === 'string').slice(0, RECENT_NODES_LIMIT)
      : [];
  } catch {
    return [];
  }
}

function Editor() {
  const [catalog, setCatalog] = useState<NodeCatalogItem[]>(fallbackCatalog);
  const [catalogSource, setCatalogSource] = useState<'runtime' | 'fallback'>('fallback');
  const [catalogHash, setCatalogHash] = useState(fallbackCatalogHash);
  const [catalogSchemaVersion, setCatalogSchemaVersion] = useState(fallbackCatalogSchemaVersion);
  const [catalogError, setCatalogError] = useState<string>();
  const [plugins, setPlugins] = useState<PluginLoadInfo[]>([]);
  const [pluginSdk, setPluginSdk] = useState<PluginSdkInfo>();
  const [pluginRescanning, setPluginRescanning] = useState(false);
  const [nodes, setNodes, onNodesChange] = useNodesState(initial.nodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState(initial.edges);
  const [selectedId, setSelectedId] = useState<string>('threshold-1');
  // 定位选区信号：属性面板点击“定位选区”时递增，图像查看器据此把视图适配到 ROI 区域
  const [roiFocusTick, setRoiFocusTick] = useState(0);
  const [recentNodeTypes, setRecentNodeTypes] = useState<string[]>(readRecentNodeTypes);
  const flowAreaRef = useRef<HTMLDivElement>(null);
  const [compactNodes, setCompactNodes] = useState(readNodeCompact);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchOpen, setSearchOpen] = useState(false);
  const searchInputRef = useRef<InputRef>(null);
  // 画布剪贴板：保存选中集合与集合内部连线；粘贴时生成新 id、整体偏移，并回填选中
  const clipboardRef = useRef<{ nodes: Node[]; edges: Edge[] } | null>(null);
  const pasteIndexRef = useRef(0);
  const [layout, setLayout] = useState<LayoutState>(readLayout);
  const [shellSize, setShellSize] = useState({ width: window.innerWidth, height: window.innerHeight });
  const shellRef = useRef<HTMLDivElement>(null);
  const [demoKey, setDemoKey] = useState<DemoKey>('linear');
  const [designerWorkflowId, setDesignerWorkflowId] = useState('demo-linear');
  const [designerWorkflowName, setDesignerWorkflowName] = useState('VisionStudio V0.51 linear demo');
  const [compiledDsl, setCompiledDsl] = useState<string>();

  useEffect(() => {
    fetch('/api/host-mode').then(r => r.ok ? r.json() : undefined).then(data => setRuntimeOnly(Boolean(data?.runtimeOnly))).catch(() => undefined);
  }, []);
  useEffect(() => { persistNodeCompact(compactNodes); }, [compactNodes]);
  const [pluginsOpen, setPluginsOpen] = useState(false);
  const [camerasOpen, setCamerasOpen] = useState(false);
  const [robotsOpen, setRobotsOpen] = useState(false);
  const [devicesOpen, setDevicesOpen] = useState(false);
  const [jobsOpen, setJobsOpen] = useState(false);
  const [tracesOpen, setTracesOpen] = useState(false);
  const [validationOpen, setValidationOpen] = useState(false);
  const [tuningOpen, setTuningOpen] = useState(false);
  const [modulesOpen, setModulesOpen] = useState(false);
  const [calibrationOpen, setCalibrationOpen] = useState(false);
  const [framesOpen, setFramesOpen] = useState(false);
  const [productionOpen, setProductionOpen] = useState(false);
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);
  const [hardwareProvenanceOpen, setHardwareProvenanceOpen] = useState(false);
  const [runtimeOnly, setRuntimeOnly] = useState(false);
  const [aboutOpen, setAboutOpen] = useState(false);
  const { mode: themeMode, toggle: toggleTheme } = useThemeMode();
  const { screenToFlowPosition, getZoom, getViewport, setCenter, fitView } = useReactFlow();
  const [messageApi, contextHolder] = message.useMessage();

  // 缩放分级（far/mid/near）：直接写画布容器 dataset，不经 React 状态——拖画布不会触发整树重渲；
  // CSS 按级别显隐端口文字/类型/摘要（Handle 恒在，连线落点不受影响）
  const syncZoomLevel = useCallback((viewport: { zoom: number }) => {
    const area = flowAreaRef.current;
    if (!area) return;
    const level = viewport.zoom >= 1.05 ? 'near' : viewport.zoom >= 0.6 ? 'mid' : 'far';
    if (area.dataset.zoomLevel !== level) area.dataset.zoomLevel = level;
  }, []);
  useEffect(() => {
    syncZoomLevel(getViewport());
  }, [getViewport, syncZoomLevel]);
  const [connectionPicker, setConnectionPicker] = useState<ConnectionPicker>();
  const connectionPickerRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!connectionPicker) return;
    const dismiss = (event: PointerEvent) => {
      const target = event.target as Element;
      if (!connectionPickerRef.current?.contains(target) && !target.closest('.ant-select-dropdown')) setConnectionPicker(undefined);
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented && !(event.target as Element).closest('.ant-select-dropdown')) setConnectionPicker(undefined);
    };
    const resize = () => setConnectionPicker(undefined);
    document.addEventListener('pointerdown', dismiss);
    document.addEventListener('keydown', escape);
    window.addEventListener('resize', resize);
    return () => {
      document.removeEventListener('pointerdown', dismiss);
      document.removeEventListener('keydown', escape);
      window.removeEventListener('resize', resize);
    };
  }, [connectionPicker]);

  // —— 运行/调试/图像域的输入：集中派生，供下方抽出的控制模块使用（useRunController / useRunImages）——
  const [bottomTab, setBottomTab] = useState<BottomTab>('result');
  const nodeLabels = useMemo(
    () => Object.fromEntries(nodes.map((node) => [node.id, String(node.data.label ?? node.id)])),
    [nodes]);
  const breakpoints = useMemo(
    () => nodes.filter((n) => Boolean(n.data.breakpoint)).map((n) => n.id),
    [nodes]
  );
  const workflowPayload = useMemo<WorkflowPayload>(() => ({
    id: designerWorkflowId,
    name: designerWorkflowName,
    nodes: nodes.map((node) => ({
      id: node.id,
      type: String(node.data.typeKey),
      name: String(node.data.label ?? node.id),
      position: node.position,
      parameters: (node.data.parameters ?? {}) as Record<string, unknown>
    })),
    edges: edges.map((edge) => {
      const sourceNode = nodes.find((n) => n.id === edge.source);
      const sourcePort = getPorts(sourceNode, 'outputs').find((p) => p.name === edge.sourceHandle);
      const kind = String(edge.data?.kind ?? (sourcePort?.dataType === 'Control' ? 'control' : 'data'));
      return {
        id: edge.id,
        sourceNodeId: edge.source,
        sourcePort: edge.sourceHandle ?? 'next',
        targetNodeId: edge.target,
        targetPort: edge.targetHandle ?? 'exec',
        kind
      };
    })
  }), [designerWorkflowId, designerWorkflowName, nodes, edges]);

  // 运行 / 调试控制 + 图像资源管理（独立模块）：anyBusy 是全局唯一执行闸门；
  // 结果与图像以 result.runId 为代际统一管理（详见 useRunController.ts / useRunImages.ts）
  const {
    running, result, clearResult, applyResult, resultStale,
    run, runNode, runFromHere,
    debugSession, debugBusy, allowSideEffects, setAllowSideEffects, anyBusy,
    hasSideEffectNodes, hasModuleCalls,
    startDebugSession, continueDebugSession, runSelectedNodeInSession, endDebugSession, debugStatus,
    endDebugSessionSilently
  } = useRunController({ workflowPayload, nodes, catalog, selectedId, nodeLabels, breakpoints, setNodes, setEdges, setBottomTab, messageApi });

  const { imageSource, setImageSource, imageViewerSource, viewerOverlays, selectedOutputAvailable, selectedInputAvailable } =
    useRunImages({ result, selectedId, nodes, edges, nodeLabels });

  // 校验问题列表 + 问题连线高亮（点击问题项定位节点或连线）
  const [validationIssues, setValidationIssues] = useState<WorkflowIssue[]>([]);
  const [highlightedEdgeId, setHighlightedEdgeId] = useState<string>();
  // 节点右键菜单：记录锚点（相对画布区）与目标节点，菜单动作就地可达
  const [nodeContextMenu, setNodeContextMenu] = useState<{ x: number; y: number; nodeId: string }>();
  const displayEdges = useMemo(() => highlightedEdgeId
    ? edges.map((edge) => edge.id === highlightedEdgeId
      ? { ...edge, animated: false, style: { ...(edge.style ?? {}), stroke: '#ff4d4f', strokeWidth: 2.5 } }
      : edge)
    : edges, [edges, highlightedEdgeId]);

  // 撤销/重做：整图快照栈，覆盖删除 / 连线 / 参数 / ROI / 移动；连续编辑（滑杆、输入、拖拽）在时间窗内合并为一步。
  // 引用镜像保证 pushHistory 总能取到最新图，回调无需依赖 nodes/edges。
  const history = useRef(new GraphHistory()).current;
  const [, setHistoryVersion] = useState(0);
  const nodesRef = useRef(nodes); nodesRef.current = nodes;
  const edgesRef = useRef(edges); edgesRef.current = edges;
  const selectedRef = useRef(selectedId); selectedRef.current = selectedId;
  const pushHistory = useCallback((label: string, coalesceKey?: string) => {
    history.push(label, captureGraph(nodesRef.current, edgesRef.current, selectedRef.current), coalesceKey);
    setHistoryVersion((version) => version + 1);
  }, [history]);

  const applySnapshot = useCallback((snapshot: GraphSnapshot) => {
    const restored = restoreGraph(snapshot);
    const nextSelected = restored.nodes.some((node) => node.id === restored.selectedId)
      ? restored.selectedId
      : (restored.nodes[0]?.id ?? '');
    setNodes(restored.nodes.map((node) => ({ ...node, selected: node.id === nextSelected })));
    setEdges(restored.edges);
    setSelectedId(nextSelected);
  }, [setEdges, setNodes]);

  const undo = useCallback(() => {
    if (anyBusy) return;
    const previous = history.undo(captureGraph(nodesRef.current, edgesRef.current, selectedRef.current));
    if (!previous) return;
    applySnapshot(previous);
    setHistoryVersion((version) => version + 1);
  }, [anyBusy, applySnapshot, history]);

  const redo = useCallback(() => {
    if (anyBusy) return;
    const next = history.redo(captureGraph(nodesRef.current, edgesRef.current, selectedRef.current));
    if (!next) return;
    applySnapshot(next);
    setHistoryVersion((version) => version + 1);
  }, [anyBusy, applySnapshot, history]);

  // 本地草稿：启动时读取一次作为恢复入口；恢复/丢弃前冻结草稿存储，避免覆盖待恢复内容
  const [draftNotice, setDraftNotice] = useState<WorkflowDraft | undefined>(readDraft);

  // 布局：三处分隔条 + 下栏最大化 + 两套预设。拖动中只写 CSS 变量，松手才落状态并持久化
  useEffect(() => { persistLayout(layout); }, [layout]);

  useEffect(() => {
    const element = shellRef.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => {
      const width = entry.contentRect.width;
      const height = entry.contentRect.height;
      setShellSize((current) => (Math.abs(current.width - width) < 1 && Math.abs(current.height - height) < 1 ? current : { width, height }));
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const updateLayout = (patch: Partial<LayoutState>) => setLayout((current) => ({ ...current, ...patch }));
  const applyLayoutPreset = (preset: LayoutPreset) => setLayout((current) => ({
    ...current, preset, ...LAYOUT_PRESETS[preset],
    leftCollapsed: false, rightCollapsed: false, bottomCollapsed: false, bottomMaximized: false
  }));
  const previewVar = (name: string, value: string) => shellRef.current?.style.setProperty(name, value);

  // 渲染时按当前窗口尺寸收窄尺寸，避免小窗口下三栏把画布挤没（存的值保持不变，窗口变回来即恢复）
  const rightMax = Math.max(LAYOUT_LIMITS.right.min, shellSize.width - layout.left - MIN_CENTER_WIDTH);
  const shownRightBase = clampNumber(layout.right, LAYOUT_LIMITS.right.min, rightMax);
  const leftMax = Math.max(LAYOUT_LIMITS.left.min, shellSize.width - shownRightBase - MIN_CENTER_WIDTH);
  const shownLeftBase = clampNumber(layout.left, LAYOUT_LIMITS.left.min, leftMax);
  const bottomMax = Math.max(LAYOUT_LIMITS.bottom.min, shellSize.height - 48 - MIN_WORKSPACE_HEIGHT);
  const shownBottomBase = layout.bottomMaximized ? bottomMax : clampNumber(layout.bottom, LAYOUT_LIMITS.bottom.min, bottomMax);
  const shownLeft = layout.leftCollapsed ? 0 : shownLeftBase;
  const shownRight = layout.rightCollapsed ? 0 : shownRightBase;
  const shownBottom = layout.bottomCollapsed ? 0 : shownBottomBase;
  const centerHeight = Math.max(MIN_WORKSPACE_HEIGHT, shellSize.height - 48 - shownBottom - 20);
  const presetSizes = LAYOUT_PRESETS[layout.preset === 'custom' ? 'edit' : layout.preset];

  const refreshCatalog = useCallback(async () => {
    try {
      const response = await fetch('/api/catalog');
      if (!response.ok) throw new Error(`Catalog request failed (${response.status})`);
      const data = localizeCatalog(validateCatalog(await response.json()));
      setCatalog(data);
      setNodes((current) => hydrateNodesFromCatalog(current, data));
      setCatalogSource('runtime');
      setCatalogHash(response.headers.get('X-VisionStudio-Catalog-Hash') ?? 'runtime');
      setCatalogSchemaVersion(Number(response.headers.get('X-VisionStudio-Catalog-Schema') ?? fallbackCatalogSchemaVersion));
      setCatalogError(undefined);
    } catch (error) {
      const reason = error instanceof Error ? error.message : '节点目录不可用';
      setCatalogError(reason);
      messageApi.warning('后端 Catalog 不可用；保留当前 Catalog（首次启动时使用自动生成 fallback）');
    }

    fetch('/api/plugins')
      .then((r) => (r.ok ? r.json() : []))
      .then((data) => setPlugins(data))
      .catch(() => setPlugins([]));
    fetch('/api/plugins/sdk')
      .then((r) => (r.ok ? r.json() : undefined))
      .then((data) => setPluginSdk(data))
      .catch(() => setPluginSdk(undefined));
  }, [messageApi, setNodes]);

  const rescanPlugins = useCallback(async () => {
    setPluginRescanning(true);
    try {
      const response = await fetch('/api/plugins/rescan', { method: 'POST' });
      const body = await response.json().catch(() => undefined);
      if (!response.ok) throw new Error(body?.detail ?? body?.error ?? `Plugin rescan failed (${response.status})`);
      setPlugins(body?.plugins ?? []);
      await refreshCatalog();
      const loadedNow = Number(body?.loadedNow ?? 0);
      const restartRequired = Number(body?.restartRequired ?? 0);
      messageApi.success(`插件重新扫描完成 · 已加载 ${loadedNow}${restartRequired ? ` · 需要重启 ${restartRequired}` : ''}`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '插件重新扫描失败');
    } finally { setPluginRescanning(false); }
  }, [messageApi, refreshCatalog]);

  useEffect(() => { refreshCatalog(); }, [refreshCatalog]);

  const selectedNode = nodes.find((n) => n.id === selectedId);
  const selectedCatalog = catalog.find((c) => c.type === selectedNode?.data.typeKey) ?? moduleCatalogFromNode(selectedNode);
  const selectedParameters = (selectedNode?.data.parameters ?? {}) as Record<string, unknown>;
  const selectedRoi = selectedParameters.roi as VisionRoi | undefined;
  const roiEnabled = selectedNode?.data.typeKey !== 'module.call' && Boolean(selectedCatalog?.inputs.some((p) => p.dataType === 'Image'));

  // 目标数据输入上已有的数据线数量（控制口可多入边；kind 缺失时按源端口类型回推）
  const dataEdgeCount = useCallback((targetNodeId: string, targetHandle?: string | null) =>
    edgesRef.current.filter((edge) => {
      if (edge.target !== targetNodeId || edge.targetHandle !== targetHandle) return false;
      const kind = String(edge.data?.kind ?? '');
      if (kind === 'control') return false;
      if (kind === 'data') return true;
      const sourcePort = getPorts(nodesRef.current.find((node) => node.id === edge.source), 'outputs')
        .find((port) => port.name === edge.sourceHandle);
      return sourcePort?.dataType !== 'Control';
    }).length, []);

  const onConnect = useCallback((connection: Connection) => {
    const sourceNode = nodes.find((n) => n.id === connection.source);
    const targetNode = nodes.find((n) => n.id === connection.target);
    const sourcePort = getPorts(sourceNode, 'outputs').find((p) => p.name === connection.sourceHandle);
    const targetPort = getPorts(targetNode, 'inputs').find((p) => p.name === connection.targetHandle);

    // 与 isValidConnection / 端口高亮 / 校验问题共用同一裁决（含控制数据隔离与单一数据源）
    const verdict = connectionVerdict({
      sourceType: sourcePort?.dataType,
      targetType: targetPort?.dataType,
      sameNode: connection.source === connection.target,
      targetDataSources: targetPort?.dataType === 'Control' ? 0 : dataEdgeCount(connection.target, connection.targetHandle)
    });
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
  }, [dataEdgeCount, messageApi, nodes, pushHistory, setEdges]);

  // React Flow 自带的删除通道（备用入口）：整批变更里出现 remove 时先记一步撤销
  const handleNodesChange = useCallback<OnNodesChange>((changes) => {
    if (changes.some((change) => change.type === 'remove')) pushHistory('删除节点');
    onNodesChange(changes);
  }, [onNodesChange, pushHistory]);

  const handleEdgesChange = useCallback<OnEdgesChange>((changes) => {
    if (changes.some((change) => change.type === 'remove')) pushHistory('删除连线');
    onEdgesChange(changes);
  }, [onEdgesChange, pushHistory]);

  // 连接过程中由 React Flow 统一裁决：不允许的拖放不会建立连线
  // （与端口高亮、onConnect 报错、validateWorkflow 共用 connectionVerdict，界面不会再“允许了又被校验拒”）
  const isValidConnection = useCallback((connection: Edge | Connection) => {
    if (!connection.source || !connection.target) return false;
    const sourceNode = nodes.find((node) => node.id === connection.source);
    const targetNode = nodes.find((node) => node.id === connection.target);
    if (!sourceNode || !targetNode) return false;
    const sourcePort = getPorts(sourceNode, 'outputs').find((port) => port.name === connection.sourceHandle);
    const targetPort = getPorts(targetNode, 'inputs').find((port) => port.name === connection.targetHandle);
    return connectionVerdict({
      sourceType: sourcePort?.dataType,
      targetType: targetPort?.dataType,
      sameNode: connection.source === connection.target,
      targetDataSources: targetPort?.dataType === 'Control' ? 0 : dataEdgeCount(connection.target, connection.targetHandle)
    }).allowed;
  }, [dataEdgeCount, nodes]);

  const rememberRecentNode = useCallback((typeKey: string) => {
    setRecentNodeTypes((current) => {
      const next = [typeKey, ...current.filter((x) => x !== typeKey)].slice(0, RECENT_NODES_LIMIT);
      try { window.localStorage.setItem(RECENT_NODES_KEY, JSON.stringify(next)); } catch { /* 忽略存储失败 */ }
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
    if (attachment) {
      const existing = nodesRef.current.find((node) => node.id === attachment.nodeId);
      const side = attachment.direction === 'source' ? 'outputs' : 'inputs';
      const port = getPorts(existing, side).find((port) => port.name === attachment.portName);
      if (!existing || port?.dataType !== attachment.dataType || !compatiblePorts(item, attachment).some((port) => port.name === attachment.newPort)) {
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
        parameters: makeDefaultParameters(item),
        inputs: item.inputs,
        outputs: item.outputs,
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

  // 运行结果点行定位：选中该节点并把画布居中过去
  const focusNode = useCallback((nodeId: string) => {
    const node = nodes.find((item) => item.id === nodeId);
    if (!node) return;
    setSelectedId(nodeId);
    setNodes((current) => current.map((item) => (item.selected === (item.id === nodeId) ? item : { ...item, selected: item.id === nodeId })));
    const width = node.measured?.width ?? 200;
    const height = node.measured?.height ?? 120;
    setCenter(node.position.x + width / 2, node.position.y + height / 2, { zoom: 1.05, duration: 300 });
  }, [nodes, setCenter, setNodes]);

  // 校验问题定位：节点直接选中并居中；连线高亮并居中到两端中点
  const locateIssue = (issue: WorkflowIssue) => {
    if (issue.edgeId) {
      const edge = edges.find((item) => item.id === issue.edgeId);
      const source = edge && nodes.find((node) => node.id === edge.source);
      const target = edge && nodes.find((node) => node.id === edge.target);
      if (edge && source && target) {
        setHighlightedEdgeId(edge.id);
        const sourceWidth = source.measured?.width ?? 200;
        const sourceHeight = source.measured?.height ?? 120;
        const targetWidth = target.measured?.width ?? 200;
        const targetHeight = target.measured?.height ?? 120;
        setCenter(
          (source.position.x + sourceWidth / 2 + target.position.x + targetWidth / 2) / 2,
          (source.position.y + sourceHeight / 2 + target.position.y + targetHeight / 2) / 2,
          { zoom: 1.1, duration: 300 }
        );
        return;
      }
    }
    if (issue.nodeId && nodes.some((node) => node.id === issue.nodeId)) {
      focusNode(issue.nodeId);
      setHighlightedEdgeId(undefined);
    }
  };

  // 当前选中集合：优先取 React Flow 维护的 selected 标记（框选 / Ctrl 加选），兼容仅设置 selectedId 的情况
  const selectedNodeIds = useMemo(() => {
    const flagged = nodes.filter((node) => node.selected).map((node) => node.id);
    if (flagged.length > 0) return flagged;
    return selectedId && nodes.some((node) => node.id === selectedId) ? [selectedId] : [];
  }, [nodes, selectedId]);

  // 节点查找：按显示名 / 类型 / id 匹配；高亮直接切 DOM 类名，避免大流程里每次输入都重建节点状态
  const searchMatches = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    if (!query) return [];
    return nodes.filter((node) => {
      const label = String(node.data.label ?? '');
      const typeKey = String(node.data.typeKey ?? '');
      return label.toLowerCase().includes(query) || typeKey.toLowerCase().includes(query) || node.id.toLowerCase().includes(query);
    });
  }, [nodes, searchQuery]);
  const searchMatchIds = useMemo(() => searchMatches.map((node) => node.id), [searchMatches]);

  useEffect(() => {
    const root = flowAreaRef.current;
    if (!root) return;
    root.querySelectorAll('.react-flow__node.search-hit').forEach((element) => element.classList.remove('search-hit'));
    if (!searchQuery.trim()) return;
    for (const id of searchMatchIds) {
      root.querySelector(`.react-flow__node[data-id="${CSS.escape(id)}"]`)?.classList.add('search-hit');
    }
  }, [searchMatchIds, searchQuery]);

  // 多选对齐：以选中集合的整体包围盒为基准（尺寸取 measured，未测量时按占位尺寸）
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

  const updateParameter = (key: string, value: unknown) => {
    if (!selectedId) return;
    pushHistory('修改参数', `param:${selectedId}`);
    // 参数改了：节点上残留的成功/失败与判定属于上一次运行 → 标过期，不再冒充当前结论
    setNodes((current) => current.map((node) =>
      node.id === selectedId
        ? { ...node, data: { ...node.data, stale: true, parameters: { ...(node.data.parameters as object), [key]: value } } }
        : node
    ));
  };

  const updateRoi = (roi?: VisionRoi) => {
    if (!selectedId || !roiEnabled) return;
    pushHistory('修改 ROI', `roi:${selectedId}`);
    setNodes((current) => current.map((node) => {
      if (node.id !== selectedId) return node;
      const parameters = { ...((node.data.parameters ?? {}) as Record<string, unknown>) };
      if (roi) parameters.roi = roi;
      else delete parameters.roi;
      return { ...node, data: { ...node.data, stale: true, parameters } };
    }));
  };

  // 已保存 / 未保存：用「当前负载快照 vs 上次保存/加载快照」比对，避免在每个改动入口手动打脏标记
  const workflowSnapshot = useMemo(() => JSON.stringify(workflowPayload), [workflowPayload]);
  const [savedSnapshot, setSavedSnapshot] = useState<string>();
  // 最近一次保存到后端的时间（仅保存成功时记录；批量加载/恢复草稿会清空）
  const [lastSavedAt, setLastSavedAt] = useState<Date>();
  const [pendingGuard, setPendingGuard] = useState<{ label: string; run: () => void }>();
  // 节点 footer 的“详情”：选中该节点并把底部面板切到运行结果（节点内部无法直接碰到外壳状态）
  useEffect(() => onShowNodeDetails((nodeId) => {
    setSelectedId(nodeId);
    setBottomTab('result');
    updateLayout({ bottomCollapsed: false });
  }), [updateLayout]);
  const markCleanOnNextChange = useRef(false);
  const dirty = savedSnapshot !== undefined && workflowSnapshot !== savedSnapshot;

  useEffect(() => {
    if (markCleanOnNextChange.current) {
      markCleanOnNextChange.current = false;
      setSavedSnapshot(workflowSnapshot);
      return;
    }
    setSavedSnapshot((current) => current ?? workflowSnapshot);
  }, [workflowSnapshot]);

  // 切换示例 / 加载会整块替换流程图，存在未保存修改时先让用户选择
  const guardUnsaved = (label: string, run: () => void) => {
    if (!dirty) { run(); return; }
    setPendingGuard({ label, run });
  };

  const applyDemo = (key: DemoKey) => {
    endDebugSessionSilently();
    const rawGraph = demos[key]();
    const graph = { ...rawGraph, nodes: hydrateNodesFromCatalog(rawGraph.nodes, catalog) };
    setDemoKey(key);
    setDesignerWorkflowId(`demo-${key}`);
    setDesignerWorkflowName(`VisionStudio V0.51 ${key} demo`);
    setNodes(graph.nodes.map((node) => ({ ...node, data: { ...node.data, breakpoint: false } })));
    setEdges(graph.edges);
    setSelectedId(key === 'roi' ? 'threshold-1' : (graph.nodes[0]?.id ?? ''));
    history.reset();
    setHistoryVersion((version) => version + 1);
    markCleanOnNextChange.current = true;
    clearResult(); // 结果与图像（预览/逐节点目录）随 runId 代际一并失效，由 useRunImages 接管
    setLastSavedAt(undefined);
    setValidationIssues([]);
    setHighlightedEdgeId(undefined);
    setTimeout(() => fitView({ padding: CANVAS_FIT_PADDING, duration: 250 }), 0);
  };

  // 结果应用（applyResult）、运行/调试执行（run / runNode / runFromHere / 调试会话）
  // 与图像资产加载已迁至独立模块 useRunController / useRunImages；本组件只保留装配与输入派生。

  const toggleBreakpoint = () => {
    if (!selectedId) return messageApi.warning('先选择一个节点');
    setNodes((current) => current.map((node) =>
      node.id === selectedId
        ? { ...node, data: { ...node.data, breakpoint: !Boolean(node.data.breakpoint) } }
        : node
    ));
  };

  // 校验 = 客户端预检（节点类型/参数/必需输入/连线）+ 服务端编译；结果进入可定位的问题列表
  const validate = async () => {
    const clientIssues = validateWorkflow(nodes, catalog, edges);
    let serverIssue: WorkflowIssue | undefined;
    let okSummary: string | undefined;
    try {
      const response = await fetch('/api/validate', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(workflowPayload)
      });
      const data = await response.json();
      if (response.ok && data.valid !== false) {
        okSummary = `流程校验通过 · ${data.orderedNodeIds?.length ?? nodes.length} 个节点 · ${data.pipelineSegmentCount ?? 0} 个流水线 · ${data.structuredRegionCount ?? 0} 个结构化区域`;
      } else {
        serverIssue = serverIssueFromMessage(String(data.error ?? `校验失败 (${response.status})`));
      }
    } catch (error) {
      serverIssue = serverIssueFromMessage(error instanceof Error ? error.message : '校验请求失败');
    }
    const issues = serverIssue ? [...clientIssues, serverIssue] : clientIssues;
    setValidationIssues(issues);
    setHighlightedEdgeId(undefined);
    // 校验结果进入底部“校验问题”标签页并展开结果区，不再用画布浮层遮挡
    setBottomTab('issues');
    updateLayout({ bottomCollapsed: false });
    if (issues.length > 0) messageApi.warning(`发现 ${issues.length} 个校验问题，点击列表项可定位`);
    else if (okSummary) messageApi.success(okSummary);
  };

  const inspectDsl = async () => {
    const response = await fetch('/api/compile', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(workflowPayload)
    });
    const data = await response.json();
    if (!response.ok) {
      messageApi.error(data.error ?? '编译失败');
      return;
    }
    try { setCompiledDsl(JSON.stringify(JSON.parse(data.dslJson), null, 2)); }
    catch { setCompiledDsl(data.dslJson); }
  };

  const restoreWorkflow = useCallback((workflow: WorkflowPayload, options?: { markClean?: boolean }) => {
    endDebugSessionSilently();
    setDesignerWorkflowId(workflow.id);
    setDesignerWorkflowName(workflow.name);
    const restoredNodes: Node[] = workflow.nodes.map((item) => {
      const meta = catalog.find((x) => x.type === item.type);
      const parameters = item.parameters ?? {};
      const module = item.type === 'module.call' ? moduleMetadata(parameters) : undefined;
      return {
        id: item.id,
        type: 'vision',
        position: item.position ?? { x: 100, y: 100 },
        data: {
          typeKey: item.type,
          label: item.name ?? meta?.displayName ?? (item.type === 'module.call' ? String(parameters.moduleId ?? 'Reusable Module') : item.type),
          parameters,
          inputs: module?.inputs ?? meta?.inputs ?? [],
          outputs: module?.outputs ?? meta?.outputs ?? [],
          moduleParameters: module?.moduleParameters ?? [],
          moduleVersion: parameters.moduleVersion,
          status: 'idle',
          breakpoint: false
        }
      };
    });
    const restoredEdges: Edge[] = workflow.edges.map((item) => ({
      id: item.id,
      source: item.sourceNodeId,
      sourceHandle: item.sourcePort,
      target: item.targetNodeId,
      targetHandle: item.targetPort,
      ...edgePresentation(item.kind === 'control' ? 'control' : 'data')
    }));
    setNodes(restoredNodes);
    setEdges(restoredEdges);
    setSelectedId(restoredNodes[0]?.id ?? '');
    // 整块加载会产生全新文档：清空撤销时间线；默认视为已保存基线（草稿恢复时显式关闭）
    history.reset();
    setHistoryVersion((version) => version + 1);
    if (options?.markClean !== false) markCleanOnNextChange.current = true;
    clearResult(); // 结果与图像（预览/逐节点目录）随 runId 代际一并失效，由 useRunImages 接管
    setLastSavedAt(undefined); // 整块加载后旧保存时间不再代表当前文档
    setValidationIssues([]);
    setHighlightedEdgeId(undefined);
    setTimeout(() => fitView({ padding: CANVAS_FIT_PADDING, duration: 250 }), 0);
  }, [catalog, fitView, history, setEdges, setNodes]);

  const insertModule = (version: WorkflowModuleVersion) => {
    const position = screenToFlowPosition({ x: window.innerWidth * 0.48, y: window.innerHeight * 0.38 });
    const parameters: Record<string, unknown> = {
      moduleId: version.moduleId, moduleVersion: version.version, moduleHash: version.moduleHash,
      __moduleInputs: version.inputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
      __moduleOutputs: version.outputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
      __moduleParameters: version.parameters
    };
    version.parameters.forEach((parameter) => { parameters[parameter.name] = parameter.defaultValue; });
    const newNode: Node = {
      id: `module-${version.moduleId.replace(/[^A-Za-z0-9_-]/g, '_')}-${crypto.randomUUID().slice(0, 8)}`, type: 'vision', position,
      data: { typeKey: 'module.call', label: version.moduleId, parameters, inputs: parameters.__moduleInputs, outputs: parameters.__moduleOutputs, moduleParameters: version.parameters, moduleVersion: version.version, status: 'idle', breakpoint: false }
    };
    pushHistory('插入模块');
    setNodes((current) => [...current, newNode]); setSelectedId(newNode.id); messageApi.success(`已插入模块 ${version.moduleId} V${version.version}`);
  };

  const upgradeSelectedModule = (version: WorkflowModuleVersion) => {
    if (!selectedId) return;
    pushHistory('升级模块版本');
    setNodes((current) => current.map((node) => {
      if (node.id !== selectedId || String(node.data.typeKey) !== 'module.call') return node;
      const old = (node.data.parameters ?? {}) as Record<string, unknown>;
      const parameters: Record<string, unknown> = {
        moduleId: version.moduleId, moduleVersion: version.version, moduleHash: version.moduleHash,
        __moduleInputs: version.inputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
        __moduleOutputs: version.outputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
        __moduleParameters: version.parameters
      };
      version.parameters.forEach((parameter) => { parameters[parameter.name] = old[parameter.name] ?? parameter.defaultValue; });
      return { ...node, data: { ...node.data, label: version.moduleId, parameters, inputs: parameters.__moduleInputs, outputs: parameters.__moduleOutputs, moduleParameters: version.parameters, moduleVersion: version.version } };
    }));
    messageApi.success(`已将选中的模块调用升级到 ${version.moduleId} V${version.version}`);
  };

  const save = async (): Promise<boolean> => {
    // 保存地址与显示的工作流 ID 单一事实源：不再绑定示例选择器，
    // 避免“编辑的是 A、保存进 B 的示例槽位”这类覆盖与后端改写 ID 的风险
    const response = await fetch(`/api/workflows/${encodeURIComponent(designerWorkflowId)}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(workflowPayload)
    });
    if (response.ok) {
      setSavedSnapshot(workflowSnapshot);
      setLastSavedAt(new Date());
      messageApi.success('工作流已保存为后端 JSON');
      return true;
    }
    messageApi.error('保存失败');
    return false;
  };

  const load = async () => {
    const response = await fetch(`/api/workflows/${encodeURIComponent(designerWorkflowId)}`);
    if (!response.ok) {
      messageApi.warning('后端尚未保存当前示例工作流');
      return;
    }
    const workflow: WorkflowPayload = await response.json();
    restoreWorkflow(workflow);
    messageApi.success('工作流已加载');
  };

  // 复制当前选中集合到画布剪贴板（快捷键 Ctrl+C 与节点右键菜单共用）；返回是否真的复制了内容
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

  // 删除指定节点（快捷键删除选中集合、右键菜单删除单个节点共用）；保留未受影响的选中
  const deleteNodes = useCallback((ids: string[]) => {
    if (ids.length === 0) return;
    const idSet = new Set(ids);
    pushHistory('删除节点');
    setNodes((current) => current.filter((node) => !idSet.has(node.id)));
    setEdges((current) => current.filter((edge) => !idSet.has(edge.source) && !idSet.has(edge.target)));
    setSelectedId((current) => (current && idSet.has(current) ? '' : current));
    void messageApi.success(ids.length > 1 ? `已删除 ${ids.length} 个节点` : '已删除选中节点');
  }, [messageApi, pushHistory, setEdges, setNodes]);

  // 快捷键：Ctrl/Cmd+S 保存、Ctrl/Cmd+F 查找节点、Ctrl/Cmd+Z 撤销、Ctrl/Cmd+Shift+Z（或 Ctrl+Y）重做、
  // Ctrl/Cmd+C / V 复制粘贴选中集合、Delete/Backspace 批量删除选中节点；
  // 焦点在输入控件内时只保留原生文本编辑行为（查找与保存除外）。捕获阶段监听，避免画布吞键。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const comboKey = event.key.toLowerCase();
      const mod = event.ctrlKey || event.metaKey;
      if (mod && comboKey === 's') {
        event.preventDefault();
        void save();
        return;
      }
      if (mod && comboKey === 'f') {
        event.preventDefault();
        searchInputRef.current?.focus();
        searchInputRef.current?.select();
        return;
      }
      const target = event.target as HTMLElement | null;
      if (target?.closest?.('input, textarea, select, [contenteditable="true"], .toolbox')) return;
      if (mod && !event.altKey) {
        if (comboKey === 'z' && !event.shiftKey) { event.preventDefault(); undo(); return; }
        if ((comboKey === 'z' && event.shiftKey) || comboKey === 'y') { event.preventDefault(); redo(); return; }
        if (comboKey === 'c' && !event.shiftKey) {
          if (!copySelection()) return;
          event.preventDefault();
          return;
        }
        if (comboKey === 'v' && !event.shiftKey) {
          const clip = clipboardRef.current;
          if (!clip || clip.nodes.length === 0) return;
          event.preventDefault();
          pasteIndexRef.current += 1;
          const offset = 40 * pasteIndexRef.current;
          const idMap = new Map<string, string>();
          const pastedNodes: Node[] = clip.nodes.map((node) => {
            const id = `${String(node.data.typeKey ?? 'node')}-${crypto.randomUUID().slice(0, 8)}`;
            idMap.set(node.id, id);
            return {
              ...node,
              id,
              selected: true,
              position: { x: node.position.x + offset, y: node.position.y + offset },
              measured: undefined,
              // 运行态字段不随粘贴继承
              data: { ...node.data, status: 'idle', breakpoint: false, durationMs: undefined, error: undefined, disposition: undefined, summary: undefined }
            };
          });
          const pastedEdges: Edge[] = clip.edges.map((edge) => ({
            ...edge,
            id: crypto.randomUUID(),
            source: idMap.get(edge.source) ?? edge.source,
            target: idMap.get(edge.target) ?? edge.target
          }));
          pushHistory('粘贴节点');
          setNodes((current) => [...current.map((node) => (node.selected ? { ...node, selected: false } : node)), ...pastedNodes]);
          setEdges((current) => [...current, ...pastedEdges]);
          setSelectedId(pastedNodes[0].id);
          void messageApi.success(`已粘贴 ${pastedNodes.length} 个节点`);
          return;
        }
      }
      if (event.key !== 'Delete' && event.key !== 'Backspace' && event.key !== 'Del' && event.code !== 'Delete' && event.code !== 'Backspace') return;
      if (selectedNodeIds.length === 0) return;
      event.preventDefault();
      deleteNodes(selectedNodeIds);
    };
    // 捕获阶段监听：避免画布内部对按键的 stopPropagation 把删除快捷键吃掉
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [copySelection, deleteNodes, messageApi, pushHistory, redo, save, selectedNodeIds, setEdges, setNodes, undo]);

  // 有未保存修改时拦截刷新 / 关闭页面
  useEffect(() => {
    if (!dirty) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [dirty]);

  // 本地草稿：有未保存修改时防抖写入；保存 / 恢复 / 丢弃后清除；恢复入口未决期间冻结存储，避免覆盖待恢复的草稿
  useEffect(() => {
    if (draftNotice) return;
    if (!dirty) { clearDraft(); return; }
    const timer = window.setTimeout(() => writeDraft({
      version: 1,
      savedAt: new Date().toISOString(),
      workflowId: designerWorkflowId,
      workflowName: designerWorkflowName,
      demoKey,
      payload: workflowPayload
    }), 600);
    return () => window.clearTimeout(timer);
  }, [demoKey, designerWorkflowId, designerWorkflowName, dirty, draftNotice, workflowPayload]);

  const restoreDraft = () => {
    const notice = draftNotice;
    if (!notice) return;
    try {
      restoreWorkflow(notice.payload, { markClean: false });
      // 同步工作流身份：草稿保存时的 ID/名称要跟着恢复，否则显示的 ID 与保存目标会脱钩
      if (notice.workflowId) setDesignerWorkflowId(notice.workflowId);
      if (notice.workflowName) setDesignerWorkflowName(notice.workflowName);
      if (notice.demoKey && notice.demoKey in demos) setDemoKey(notice.demoKey as DemoKey);
      clearDraft();
      setDraftNotice(undefined);
      messageApi.success('已恢复本地草稿（尚未保存到后端，Ctrl+S 保存）');
    } catch {
      clearDraft();
      setDraftNotice(undefined);
      messageApi.error('本地草稿数据无效，已丢弃');
    }
  };

  const discardDraft = () => {
    clearDraft();
    setDraftNotice(undefined);
    messageApi.info('已丢弃本地草稿');
  };

  const applyCalibrationParameters = (parameters: Record<string, unknown>) => {
    if (!selectedId || selectedNode?.data.typeKey !== 'calibration.planar') {
      messageApi.warning('请先选择“平面标定”节点');
      return;
    }
    setNodes((current) => current.map((node) =>
      node.id === selectedId
        ? { ...node, data: { ...node.data, parameters: { ...((node.data.parameters ?? {}) as Record<string, unknown>), ...parameters } } }
        : node
    ));
  };

  // 顶栏二级入口：高频动作（校验 / 保存 / 运行）常驻，其余按领域归入四个下拉菜单
  // 动作名带上目标节点，让用户始终清楚“当前操作作用于谁”
  const selectedLabel = selectedId ? (nodeLabels[selectedId] ?? selectedId) : '';
  const targetSuffix = selectedLabel ? `：${selectedLabel}` : '';
  const debugMenuItems: MenuProps['items'] = [
    { key: 'breakpoint', label: `切换断点${targetSuffix}`, disabled: !selectedId, onClick: toggleBreakpoint },
    { key: 'run-node', label: `运行节点${targetSuffix}`, disabled: anyBusy || !selectedId || selectedNode?.data.typeKey === 'module.call', onClick: runNode },
    { key: 'run-from-here', label: `从此处运行${targetSuffix}`, disabled: anyBusy || !selectedId || selectedNode?.data.typeKey === 'module.call', onClick: runFromHere },
    { type: 'divider' },
    { key: 'debug-breakpoints', label: `调试到断点${breakpoints.length ? `（${breakpoints.length}）` : ''}`, disabled: anyBusy || breakpoints.length === 0, onClick: () => void startDebugSession() },
    { key: 'core-dsl', label: '查看核心 DSL', onClick: inspectDsl }
  ];

  // 节点右键菜单：高频动作就地可达，不必再去找顶栏菜单；副行（extra）显示对应快捷键
  const contextNode = nodeContextMenu ? nodes.find((node) => node.id === nodeContextMenu.nodeId) : undefined;
  const contextNodeLabel = contextNode ? (nodeLabels[contextNode.id] ?? contextNode.id) : '';
  const contextTargetSuffix = contextNodeLabel ? `：${contextNodeLabel}` : '';
  const nodeContextMenuItems: MenuProps['items'] = [
    { key: 'run-node', label: `运行此节点${contextTargetSuffix}`, disabled: anyBusy || !contextNode || contextNode.data.typeKey === 'module.call', onClick: runNode },
    { key: 'run-from-here', label: `从此处运行${contextTargetSuffix}`, disabled: anyBusy || !contextNode || contextNode.data.typeKey === 'module.call', onClick: runFromHere },
    { key: 'breakpoint', label: `切换断点${contextTargetSuffix}`, disabled: !contextNode, onClick: toggleBreakpoint },
    { type: 'divider' },
    { key: 'copy', label: `复制节点${contextTargetSuffix}`, extra: 'Ctrl+C', onClick: copySelection },
    { key: 'delete', label: `删除节点${contextTargetSuffix}`, extra: 'Del', danger: true, disabled: !contextNode, onClick: () => { if (nodeContextMenu) deleteNodes([nodeContextMenu.nodeId]); } }
  ];

  const deviceMenuItems: MenuProps['items'] = [
    { key: 'cameras', label: '相机', onClick: () => setCamerasOpen(true) },
    { key: 'robots', label: '机器人', onClick: () => setRobotsOpen(true) },
    { key: 'devices', label: '设备 / PLC', onClick: () => setDevicesOpen(true) },
    { type: 'divider' },
    { key: 'calibration', label: '标定', onClick: () => setCalibrationOpen(true) },
    { key: 'frames', label: '坐标系', onClick: () => setFramesOpen(true) },
    { type: 'divider' },
    { key: 'hardware', label: '硬件信息', onClick: () => setHardwareProvenanceOpen(true) },
    { key: 'diagnostics', label: '诊断中心', onClick: () => setDiagnosticsOpen(true) }
  ];

  const productionMenuItems: MenuProps['items'] = [
    { key: 'jobs', label: '作业与配方版本', onClick: () => setJobsOpen(true) },
    { key: 'traces', label: '运行追溯', onClick: () => setTracesOpen(true) },
    { key: 'production', label: '生产运行', onClick: () => setProductionOpen(true) }
  ];

  const toolMenuItems: MenuProps['items'] = [
    // 目录/插件规模从顶栏收进菜单，降低顶栏视觉权重
    { type: 'group', label: `节点目录 · ${catalogSource === 'runtime' ? '运行时' : '本地'} ${catalog.length}（schema ${catalogSchemaVersion}）` },
    { type: 'group', label: `插件 ${plugins.filter((p) => p.loaded).length} 个已加载` },
    { type: 'divider' },
    { key: 'undo', label: `撤销${history.undoLabel ? ` · ${history.undoLabel}` : ''}（Ctrl+Z）`, disabled: !history.canUndo, onClick: undo },
    { key: 'redo', label: `重做${history.redoLabel ? ` · ${history.redoLabel}` : ''}（Ctrl+Shift+Z）`, disabled: !history.canRedo, onClick: redo },
    { type: 'divider' },
    { key: 'plugins', label: '插件', onClick: () => setPluginsOpen(true) },
    { key: 'modules', label: '可复用模块', onClick: () => setModulesOpen(true) },
    { key: 'tuning', label: '参数调优', onClick: () => setTuningOpen(true) },
    { key: 'validation', label: '数据集验证', onClick: () => setValidationOpen(true) },
    { type: 'divider' },
    { key: 'layout-edit', label: '布局：流程编辑' },
    { key: 'layout-image', label: '布局：图像调试' },
    { key: 'layout-reset', label: '布局：复位为预设尺寸' },
    { type: 'divider' },
    { key: 'load', label: '加载已保存工作流', onClick: () => guardUnsaved('加载已保存的工作流', () => void load()) },
    { key: 'theme', label: themeMode === 'dark' ? '切换到浅色主题' : '切换到深色主题', onClick: toggleTheme },
    { key: 'about', label: '关于 VisionStudio', onClick: () => setAboutOpen(true) }
  ];

  if (runtimeOnly) return <>
    <ProductionPanel open={true} onClose={() => undefined} embedded />
    <Button className="runtime-diagnostics-button" type="primary" ghost onClick={() => setDiagnosticsOpen(true)}>诊断</Button>
    <DiagnosticsPanel open={diagnosticsOpen} onClose={() => setDiagnosticsOpen(false)} />
  </>;

  return (
    <div
      className="app-shell"
      ref={shellRef}
      style={{
        '--vs-left-width': `${shownLeft}px`,
        '--vs-right-width': `${shownRight}px`,
        '--vs-bottom-height': `${shownBottom}px`,
        '--vs-image-height': `${layout.image}%`
      } as CSSProperties}
    >
      {contextHolder}
      <header className="topbar">
        <div className="topbar-left">
          <div className="brand">VisionStudio <span>V{APP_VERSION}</span></div>
          <span className="topbar-divider" />
          <div className="workflow-identity" title={`${designerWorkflowName} · ${designerWorkflowId}`}>
            {/* 名称可点击重命名；技术 ID 收进悬停详情，不再常驻占位 */}
            <Typography.Text
              className="workflow-name"
              editable={{
                onChange: (value) => {
                  const name = value.trim();
                  if (name && name !== designerWorkflowName) setDesignerWorkflowName(name);
                },
                tooltip: '点击重命名工作流',
                icon: <span className="workflow-rename-icon">✎</span>
              }}
            >{designerWorkflowName}</Typography.Text>
            <Tag
              className="workflow-dirty"
              color={dirty ? 'gold' : 'default'}
              title={dirty
                ? '有未保存的修改（Ctrl+S 保存）'
                : lastSavedAt
                  ? `已保存 · ${lastSavedAt.toLocaleTimeString(dateLocale(getInitialLang()))}`
                  : '自上次保存或加载以来没有修改'}
            >
              {dirty ? '未保存' : lastSavedAt ? `已保存 ${lastSavedAt.toLocaleTimeString(dateLocale(getInitialLang()), { hour: '2-digit', minute: '2-digit' })}` : '已保存'}
            </Tag>
          </div>
        </div>
        {/* 中段：模板创建 + 领域菜单（低频，收敛于此）；目录/插件数量入“工具”菜单 */}
        <div className="topbar-center">
          <span className="topbar-select-label">模板</span>
          <Tooltip title={demoDescriptions[demoKey]} placement="bottomLeft">
            <Select<DemoKey>
              value={demoKey}
              style={{ width: 118 }}
              popupMatchSelectWidth={false}
              onChange={(key) => guardUnsaved('切换模板', () => applyDemo(key))}
              optionRender={(option) => {
                const desc = demoDescriptions[option.value as DemoKey];
                return (
                  <div className="demo-option">
                    <div>{option.label}</div>
                    {desc && <div className="demo-option-desc">{desc}</div>}
                  </div>
                );
              }}
              options={[
              { value: 'camera', label: '相机示例' },
              { value: 'cameraTrigger', label: '触发式相机' },
              { value: 'frameSet', label: '同步帧组' },
              { value: 'measurement', label: '测量示例' },
              { value: 'calibration', label: '标定示例' },
              { value: 'robotEyeToHand', label: '眼在手外示例' },
              { value: 'robotEyeInHand', label: '眼在手上示例' },
              { value: 'robotRuntime', label: '机器人运行时' },
              { value: 'robotPlc', label: '机器人 PLC 握手' },
              { value: 'robotTcp', label: '机器人 TCP 模拟器' },
              { value: 'devicePlc', label: '设备 / PLC 运行时' },
              { value: 'linear', label: '线性流程示例' },
              { value: 'roi', label: '感兴趣区域示例' },
              { value: 'if', label: '条件分支示例' },
              { value: 'parallel', label: '并行流程示例' },
              { value: 'nested', label: '嵌套流程示例' }
            ]}
            />
          </Tooltip>
          <span className="topbar-divider" />
          <Dropdown menu={{ items: debugMenuItems }} trigger={['click']}>
            <Button>调试 <span className="menu-caret">▾</span></Button>
          </Dropdown>
          <Dropdown menu={{ items: deviceMenuItems }} trigger={['click']}>
            <Button>设备 <span className="menu-caret">▾</span></Button>
          </Dropdown>
          <Dropdown menu={{ items: productionMenuItems }} trigger={['click']}>
            <Button>生产 <span className="menu-caret">▾</span></Button>
          </Dropdown>
          <Dropdown
            trigger={['click']}
            menu={{
              items: toolMenuItems,
              selectable: true,
              selectedKeys: layout.preset === 'custom' ? [] : [layout.preset],
              onClick: ({ key }) => {
                if (key === 'layout-edit') applyLayoutPreset('edit');
                else if (key === 'layout-image') applyLayoutPreset('image');
                else if (key === 'layout-reset') applyLayoutPreset(layout.preset === 'custom' ? 'edit' : layout.preset);
              }
            }}
          >
            <Button>工具 <span className="menu-caret">▾</span></Button>
          </Dropdown>
        </div>
        {/* 右侧固定：高频动作常驻，账号入口（security-badge）经 CSS 融入本段最右 */}
        <div className="topbar-right">
          <Button onClick={validate}>校验</Button>
          <Button onClick={save}>保存</Button>
          <Button type="primary" onClick={run} loading={running} disabled={debugBusy}>运行</Button>
        </div>
      </header>

      <main className="workspace">
        <aside className="left-panel"><Toolbox catalog={catalog} recentTypes={recentNodeTypes} onAddNode={(type) => addNode(type)} /></aside>
        <SplitHandle
          axis="x"
          label="调整工具箱宽度"
          value={layout.left}
          min={LAYOUT_LIMITS.left.min}
          max={leftMax}
          collapsed={layout.leftCollapsed}
          collapseGlyph="‹"
          expandGlyph="›"
          preview={(value) => previewVar('--vs-left-width', `${value}px`)}
          commit={(value) => updateLayout({ left: value, leftCollapsed: false, preset: 'custom' })}
          reset={() => updateLayout({ left: presetSizes.left, leftCollapsed: false })}
          onToggleCollapse={() => updateLayout({ leftCollapsed: !layout.leftCollapsed })}
        />

        <section className="center-panel">
          <div className={`flow-area${compactNodes ? ' compact-nodes' : ''}`} style={{ position: 'relative' }} ref={flowAreaRef} onDrop={onDrop} onDragOver={(e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }}>
            {draftNotice && (
              <div className="draft-recovery" style={{ position: 'absolute', top: 10, left: '50%', transform: 'translateX(-50%)', zIndex: 50, width: 'min(640px, calc(100% - 24px))' }}>
                <Alert
                  type="warning"
                  showIcon
                  message="检测到未保存的本地草稿"
                  description={`${draftNotice.workflowName || draftNotice.workflowId || '未命名工作流'} · 保存于 ${new Date(draftNotice.savedAt).toLocaleString(dateLocale(getInitialLang()))}`}
                  action={(
                    <div style={{ display: 'flex', gap: 8 }}>
                      <Button size="small" type="primary" onClick={restoreDraft}>恢复草稿</Button>
                      <Button size="small" onClick={discardDraft}>丢弃</Button>
                    </div>
                  )}
                />
              </div>
            )}
            {/* 校验问题列表已迁至底部“校验问题”标签页：不再用浮层遮挡画布 */}
            <div className="canvas-toolbar" style={{ position: 'absolute', top: 10, left: 10, zIndex: 44 }}>
              <div className="canvas-search">
                <Input
                  ref={searchInputRef}
                  size="small"
                  allowClear
                  className="canvas-search-input"
                  placeholder="查找节点（Ctrl+F）"
                  value={searchQuery}
                  onChange={(event) => { setSearchQuery(event.target.value); setSearchOpen(true); }}
                  onFocus={() => setSearchOpen(true)}
                  onBlur={() => setSearchOpen(false)}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' && searchMatches.length > 0) {
                      focusNode(searchMatches[0].id);
                      setSearchOpen(false);
                    }
                    if (event.key === 'Escape') {
                      setSearchQuery('');
                      setSearchOpen(false);
                    }
                  }}
                />
                {searchOpen && searchQuery.trim() !== '' && (
                  // 用 mousedown 阻止默认行为，点结果时输入框不失焦，onClick 才能拿到这一次点击
                  <div className="canvas-search-results" onMouseDown={(event) => event.preventDefault()}>
                    <div className="canvas-search-count">
                      命中 {searchMatches.length} / {nodes.length}{searchMatches.length > 8 ? '（仅显示前 8 条）' : ''}
                    </div>
                    {searchMatches.slice(0, 8).map((node) => (
                      <button
                        key={node.id}
                        type="button"
                        className="canvas-search-item"
                        title="定位并选中该节点"
                        onClick={() => { focusNode(node.id); setSearchOpen(false); }}
                      >
                        <span className="canvas-search-label">{String(node.data.label ?? node.id)}</span>
                        <small>{node.id}</small>
                      </button>
                    ))}
                    {searchMatches.length === 0 && <div className="canvas-search-empty">无匹配节点</div>}
                  </div>
                )}
              </div>
              <span className="canvas-toolbar-divider" />
              <label className="canvas-compact-toggle" title="紧凑模式：压缩节点标题与端口间距，适合多端口的大型流程">
                <Switch size="small" checked={compactNodes} onChange={setCompactNodes} />
                <span>紧凑</span>
              </label>
            </div>

            {selectedNodeIds.length >= 2 && (
              <div className="canvas-align-toolbar" style={{ position: 'absolute', top: 10, left: '50%', transform: 'translateX(-50%)', zIndex: 44 }}>
                <Tag style={{ marginInlineEnd: 0 }}>已选 {selectedNodeIds.length}</Tag>
                <Button size="small" onClick={() => alignSelection('left')}>左对齐</Button>
                <Button size="small" onClick={() => alignSelection('centerX')}>水平居中</Button>
                <Button size="small" onClick={() => alignSelection('right')}>右对齐</Button>
                <Button size="small" onClick={() => alignSelection('top')}>顶对齐</Button>
                <Button size="small" onClick={() => alignSelection('centerY')}>垂直居中</Button>
                <Button size="small" onClick={() => alignSelection('bottom')}>底对齐</Button>
              </div>
            )}

            <ReactFlow
              nodes={nodes}
              edges={displayEdges}
              nodeTypes={nodeTypes}
              colorMode={themeMode}
              deleteKeyCode={null}
              selectionKeyCode="Shift"
              multiSelectionKeyCode={['Control', 'Meta', 'Shift']}
              isValidConnection={isValidConnection}
              onNodesChange={handleNodesChange}
              onEdgesChange={handleEdgesChange}
              onConnect={onConnect}
              // 连线进行中：临时恢复端口名（远缩放下也能确认落点）。写 dataset 而不是 className，避免与 React 的 className 互相覆盖
              onConnectStart={() => {
                setConnectionPicker(undefined);
                if (flowAreaRef.current) flowAreaRef.current.dataset.connecting = 'true';
              }}
              onConnectEnd={(event, state) => {
                if (flowAreaRef.current) delete flowAreaRef.current.dataset.connecting;
                onConnectEnd(event, state);
              }}
              onMoveStart={() => setConnectionPicker(undefined)}
              onNodeDragStart={() => pushHistory('移动节点')}
              onNodeClick={(_, node) => setSelectedId(node.id)}
              onMove={(_, viewport) => syncZoomLevel(viewport)}
              // 右键即选中：菜单里的“运行/断点”动作与用户所见目标一致
              onNodeContextMenu={(event, node) => {
                event.preventDefault();
                setSelectedId(node.id);
                const bounds = flowAreaRef.current?.getBoundingClientRect();
                setNodeContextMenu({
                  x: event.clientX - (bounds?.left ?? 0),
                  y: event.clientY - (bounds?.top ?? 0),
                  nodeId: node.id
                });
              }}
              fitView
              fitViewOptions={{ padding: CANVAS_FIT_PADDING }}
            >
              <Background gap={18} size={1} />
              <Controls />
              <MiniMap pannable zoomable />
            </ReactFlow>

            <div className="debug-toolbar" style={{ position: 'absolute', bottom: 10, left: '50%', transform: 'translateX(-50%)', zIndex: 40, display: 'flex', alignItems: 'center', gap: 8, padding: '6px 10px', borderRadius: 10, background: 'var(--vs-bg-card)', border: '1px solid var(--vs-border-frame)', boxShadow: '0 6px 18px rgba(0, 0, 0, 0.18)' }}>
              {/* 普通状态只保留调试入口；进入会话后展示完整操作条，减少画布常驻浮层 */}
              {!debugSession ? (
                <>
                  <Button
                    size="small"
                    disabled={anyBusy || hasModuleCalls}
                    title={hasModuleCalls
                      ? '含复用模块的流程暂不支持会话调试'
                      : breakpoints.length === 0
                        ? '先为节点设置断点（右键节点 → 切换断点），再从起点运行到第一个断点并保留现场'
                        : '从起点运行到第一个断点并保留现场'}
                    onClick={() => void startDebugSession()}
                  >
                    调试
                  </Button>
                  {hasSideEffectNodes && (
                    <span title="流程含 PLC 写入 / 机器人命令节点：勾选后调试会话才会真的写设备，否则这类节点将被跳过">
                      <Checkbox style={{ fontSize: 12 }} checked={allowSideEffects} onChange={(event) => setAllowSideEffects(event.target.checked)}>允许真实设备写入</Checkbox>
                    </span>
                  )}
                </>
              ) : (
                <>
                  <Tag color={debugStatus.color} style={{ marginInlineEnd: 0 }} title="会话基于启动时的流程快照">调试 · {debugStatus.text}</Tag>
                  <Button size="small" type="primary" disabled={anyBusy || debugSession.status !== 'halted'} onClick={() => void continueDebugSession()}>继续</Button>
                  <Button size="small" disabled={anyBusy || debugSession.status === 'faulted' || !selectedId} title={!selectedId ? '先在画布上选择一个节点' : '用会话缓存的输入执行选中节点，不重跑上游'} onClick={() => void runSelectedNodeInSession()}>运行选中节点</Button>
                  <Button size="small" danger disabled={anyBusy} onClick={() => void endDebugSession()}>结束会话</Button>
                </>
              )}
            </div>

            {/* 节点右键菜单：锚点为不可见占位，Dropdown 受控打开；点击菜单项或菜单外自动关闭 */}
            {connectionPicker && (
              <div className="connection-node-picker" ref={connectionPickerRef} style={{ left: connectionPicker.x, top: connectionPicker.y }} role="dialog" aria-label="添加兼容节点">
                <Toolbox catalog={catalog} recentTypes={recentNodeTypes} connection={connectionPicker}
                  onClose={() => setConnectionPicker(undefined)}
                  onAddNode={(type, port) => { if (port) addNode(type, connectionPicker.position, { ...connectionPicker, newPort: port }); }} />
              </div>
            )}
            {nodeContextMenu && (
              <Dropdown
                open
                trigger={['contextMenu']}
                menu={{ items: nodeContextMenuItems, onClick: () => setNodeContextMenu(undefined) }}
                onOpenChange={(open) => { if (!open) setNodeContextMenu(undefined); }}
              >
                <div style={{ position: 'absolute', top: nodeContextMenu.y, left: nodeContextMenu.x, width: 1, height: 1 }} />
              </Dropdown>
            )}
          </div>

          <SplitHandle
            axis="y"
            label="调整图像区高度"
            value={layout.image}
            min={LAYOUT_LIMITS.image.min}
            max={LAYOUT_LIMITS.image.max}
            scale={100 / centerHeight}
            invert
            collapsed={layout.imageCollapsed}
            collapsible
            collapseGlyph="▴"
            expandGlyph="▾"
            preview={(value) => previewVar('--vs-image-height', `${value}%`)}
            commit={(value) => updateLayout({ image: value, preset: 'custom' })}
            reset={() => updateLayout({ image: presetSizes.image })}
            onToggleCollapse={() => updateLayout({ imageCollapsed: !layout.imageCollapsed })}
          />

          {/* 折叠时画布独占中央区域；分隔条上的 ▾ 按钮可重新展开 */}
          <div className="preview-area" style={layout.imageCollapsed ? { display: 'none' } : undefined}>
            <div className="panel-title preview-title" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span>图像查看器 · 感兴趣区域 / 覆盖层</span>
              <Segmented
                size="small"
                value={imageSource}
                onChange={(value) => setImageSource(value as 'preview' | 'output' | 'input')}
                options={[
                  { label: '最终预览', value: 'preview' },
                  { label: '输出', value: 'output', disabled: !selectedOutputAvailable },
                  { label: '输入', value: 'input', disabled: !selectedInputAvailable }
                ]}
              />
              <Tag style={{ marginInlineEnd: 0 }} color={imageSource === 'preview' ? 'default' : 'blue'} title="当前图像来源（输出=所选节点结果，输入=其上游图像）">{imageViewerSource.label}</Tag>
            </div>
            <ImageViewer
              previewUrl={imageViewerSource.url}
              imageWidth={imageViewerSource.width}
              imageHeight={imageViewerSource.height}
              overlays={viewerOverlays}
              roi={selectedRoi}
              roiEnabled={roiEnabled}
              roiTargetLabel={roiEnabled ? String(selectedNode?.data.label ?? selectedNode?.id ?? '') : undefined}
              loading={anyBusy}
              onRoiChange={updateRoi}
              nodeLabels={nodeLabels}
              focusTick={roiFocusTick}
            />
          </div>
        </section>

        <SplitHandle
          axis="x"
          label="调整属性面板宽度"
          value={layout.right}
          min={LAYOUT_LIMITS.right.min}
          max={rightMax}
          invert
          collapsed={layout.rightCollapsed}
          collapseGlyph="›"
          expandGlyph="‹"
          preview={(value) => previewVar('--vs-right-width', `${value}px`)}
          commit={(value) => updateLayout({ right: value, rightCollapsed: false, preset: 'custom' })}
          reset={() => updateLayout({ right: presetSizes.right, rightCollapsed: false })}
          onToggleCollapse={() => updateLayout({ rightCollapsed: !layout.rightCollapsed })}
        />
        <aside className="right-panel">
          <PropertyPanel node={selectedNode} catalogItem={selectedCatalog} onChange={updateParameter} disabled={anyBusy} onLocateRoi={() => setRoiFocusTick((tick) => tick + 1)} />
        </aside>
      </main>

      <SplitHandle
        axis="y"
        label="调整结果区高度"
        value={layout.bottom}
        min={LAYOUT_LIMITS.bottom.min}
        max={bottomMax}
        invert
        collapsed={layout.bottomCollapsed}
        collapseGlyph="⌄"
        expandGlyph="⌃"
        maximize={{
          active: layout.bottomMaximized,
          title: layout.bottomMaximized ? '还原结果区高度' : '最大化结果区',
          onToggle: () => updateLayout({ bottomMaximized: !layout.bottomMaximized, bottomCollapsed: false })
        }}
        preview={(value) => previewVar('--vs-bottom-height', `${value}px`)}
        commit={(value) => updateLayout({ bottom: value, bottomMaximized: false, bottomCollapsed: false, preset: 'custom' })}
        reset={() => updateLayout({ bottom: presetSizes.bottom, bottomMaximized: false, bottomCollapsed: false })}
        onToggleCollapse={() => updateLayout({ bottomCollapsed: !layout.bottomCollapsed })}
      />

      <footer className="bottom-panel">
        {/* 底部统一标签页：运行结果 / 校验问题（带数量）/ 运行观测；右侧为可见的布局预设切换 */}
        <div className="bottom-tabs-bar">
          <Segmented
            size="small"
            value={bottomTab}
            onChange={(value) => setBottomTab(value as typeof bottomTab)}
            options={[
              { label: '运行结果', value: 'result' },
              { label: `校验问题${validationIssues.length > 0 ? ` · ${validationIssues.length}` : ''}`, value: 'issues' },
              { label: '运行观测', value: 'observability' }
            ]}
          />
          <span className="bottom-tabs-spacer" />
          <Segmented
            size="small"
            value={layout.preset}
            onChange={(value) => { if (value !== 'custom') applyLayoutPreset(value as LayoutPreset); }}
            options={[
              { label: '流程编辑', value: 'edit' },
              { label: '图像调试', value: 'image' },
              ...(layout.preset === 'custom' ? [{ label: '自定义', value: 'custom' }] : [])
            ]}
          />
        </div>
        {bottomTab === 'result' && (
          <RunPanel result={result} nodeLabels={nodeLabels} onSelectNode={focusNode} stale={resultStale} embedded />
        )}
        {bottomTab === 'issues' && (
          validationIssues.length === 0 ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="点击顶部“校验”检查流程；发现的问题将显示在这里，点击可定位到画布" />
          ) : (
            <div className="bottom-issues">
              {validationIssues.map((issue) => {
                const locatable = Boolean(issue.nodeId || issue.edgeId);
                return (
                  <div
                    key={issue.id}
                    onClick={() => locateIssue(issue)}
                    title={locatable ? '点击定位到节点/连线' : '该问题无法自动定位，请根据描述排查'}
                    style={{ display: 'flex', gap: 6, alignItems: 'flex-start', padding: '4px 6px', borderRadius: 6, cursor: locatable ? 'pointer' : 'default', fontSize: 12, lineHeight: 1.55 }}
                  >
                    <Tag color={issue.source === 'server' ? 'red' : 'orange'} style={{ marginInlineEnd: 0, flexShrink: 0 }}>
                      {issue.source === 'server' ? '编译器' : '预检'}
                    </Tag>
                    <span>
                      {issue.message}
                      {locatable && <small style={{ color: 'var(--vs-text-4)' }}>{issue.edgeId ? `（连线 ${issue.edgeId}）` : `（节点 ${issue.nodeId}）`}</small>}
                    </span>
                  </div>
                );
              })}
            </div>
          )
        )}
        {bottomTab === 'observability' && <RunObservabilityPanel runId={result?.runId} />}
      </footer>

      <Modal
        open={compiledDsl !== undefined}
        title="已编译的工作流 DSL"
        width={860}
        footer={null}
        onCancel={() => setCompiledDsl(undefined)}
      >
        <pre className="dsl-view">{compiledDsl}</pre>
      </Modal>

      <Modal
        open={pendingGuard !== undefined}
        title="存在未保存的修改"
        width={460}
        onCancel={() => setPendingGuard(undefined)}
        footer={[
          <Button key="cancel" onClick={() => setPendingGuard(undefined)}>取消</Button>,
          <Button key="discard" danger onClick={() => { const action = pendingGuard; setPendingGuard(undefined); action?.run(); }}>丢弃修改</Button>,
          <Button
            key="save"
            type="primary"
            onClick={() => { const action = pendingGuard; void save().then((ok) => { if (!ok) return; setPendingGuard(undefined); action?.run(); }); }}
          >
            保存并继续
          </Button>
        ]}
      >
        <p>当前工作流有未保存的修改，{pendingGuard?.label}前请选择如何处理：</p>
      </Modal>

      <CameraPanel open={camerasOpen} onClose={() => setCamerasOpen(false)} />
      <RobotPanel open={robotsOpen} onClose={() => setRobotsOpen(false)} />
      <DevicePanel open={devicesOpen} onClose={() => setDevicesOpen(false)} />
      <FrameTreePanel open={framesOpen} onClose={() => setFramesOpen(false)} />
      <CalibrationPanel
        open={calibrationOpen}
        onClose={() => setCalibrationOpen(false)}
        canApplyToSelectedNode={selectedNode?.data.typeKey === 'calibration.planar'}
        onApplyToSelectedNode={applyCalibrationParameters}
      />
      <JobPanel open={jobsOpen} onClose={() => setJobsOpen(false)} workflow={workflowPayload} onLoadWorkflow={restoreWorkflow} onRunResult={applyResult} />
      <TracePanel open={tracesOpen} onClose={() => setTracesOpen(false)} />
      <DatasetValidationPanel open={validationOpen} onClose={() => setValidationOpen(false)} currentWorkflow={workflowPayload} />
      <ParameterTuningPanel open={tuningOpen} onClose={() => setTuningOpen(false)} currentWorkflow={workflowPayload} catalog={catalog} initialNodeId={selectedId} onApplyWorkflow={(candidate) => { restoreWorkflow(candidate); setTuningOpen(false); messageApi.success('已将调优后的候选方案应用到设计器'); }} />
      <WorkflowModulePanel open={modulesOpen} onClose={() => setModulesOpen(false)} currentWorkflow={workflowPayload} selectedNodeId={selectedId} selectedNodeType={String(selectedNode?.data.typeKey ?? '')} onApplyWorkflow={(workflow) => { restoreWorkflow(workflow); messageApi.success('已将所选节点替换为固定版本的可复用模块'); }} onLoadWorkflow={(workflow) => { restoreWorkflow(workflow); messageApi.success('已将模块内部节点加载到设计器'); }} onInsertModule={insertModule} onUpgradeSelected={upgradeSelectedModule} />
      <ProductionPanel open={productionOpen} onClose={() => setProductionOpen(false)} />
      <DiagnosticsPanel open={diagnosticsOpen} onClose={() => setDiagnosticsOpen(false)} />
      <AboutPanel open={aboutOpen} onClose={() => setAboutOpen(false)} />
      <HardwareProvenancePanel open={hardwareProvenanceOpen} onClose={() => setHardwareProvenanceOpen(false)} />
      <PluginPackagePanel open={pluginsOpen} onClose={() => setPluginsOpen(false)} onCatalogRefresh={refreshCatalog} />
    </div>
  );
}

export default function App() {
  return <ReactFlowProvider><Editor /></ReactFlowProvider>;
}
