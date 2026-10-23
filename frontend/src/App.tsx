import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import {
  addEdge,
  Background,
  Controls,
  ReactFlow,
  ReactFlowProvider,
  useEdgesState,
  useNodesState,
  useNodesInitialized,
  useReactFlow,
  type Connection,
  type Edge,
  type Node,
  type OnEdgesChange,
  type OnConnectEnd,
  type OnNodesChange
} from '@xyflow/react';
import { Alert, Button, Checkbox, Dropdown, Input, message, Modal, Segmented, Switch, Tag, type InputRef, type MenuProps } from 'antd';
import Toolbox from './components/Toolbox';
import NodeConfigurationDialog, { type NodeConfigurationTab } from './components/NodeConfigurationDialog';
import CameraPanel from './components/CameraPanel';
import RobotPanel from './components/RobotPanel';
import DevicePanel from './components/DevicePanel';
import ProductionPanel from './components/ProductionPanel';
import DiagnosticsPanel from './components/DiagnosticsPanel';
import SplitHandle from './components/SplitHandle';
import ErrorBoundary from './components/ErrorBoundary';
import { LAYOUT_LIMITS, LAYOUT_PRESETS, MIN_CENTER_WIDTH, MIN_WORKSPACE_HEIGHT, clampNumber, persistLayout, readLayout, type LayoutPreset, type LayoutState } from './layout';
import CanvasMiniMap from './components/CanvasMiniMap';
import WorkflowImagePreview from './components/WorkflowImagePreview';
import { automaticResultHeight } from './layout';
import { catalogNodeData, fallbackCatalog, fallbackCatalogHash, fallbackCatalogSchemaVersion, makeDefaultParameters, validateCatalog } from './catalog';
import { dateLocale, getInitialLang, localizeCatalog } from './i18n';
import { APP_VERSION } from './version';
import { useThemeMode } from './theme';
import { SecurityAccountBadge } from './security';
import { demos, type DemoKey } from './demos';
import { GraphHistory, captureGraph, restoreGraph, type GraphSnapshot } from './history';
import { clearDraft, readDraft, type WorkflowDraft } from './draft';
import { graphConnectionVerdict, type WorkflowIssue } from './validation';
import { responseErrorMessage } from './apiErrors';
import { type DocumentBaseline } from './documentSync';
import { isWorkflowDocument } from './workflowDocument';
import { useWorkflowChanges } from './useWorkflowChanges';
import { useWorkflowImport } from './useWorkflowImport';
import { useWorkflowSave } from './useWorkflowSave';
import { useWorkflowDraft } from './useWorkflowDraft';
import { useWorkflowInspection } from './useWorkflowInspection';
import { useEditorShortcuts } from './useEditorShortcuts';
import { onConfigureNode, onShowNodeDetails } from './uiBridge';
import { useRunController, type BottomTab } from './useRunController';
import { cloneParameters, type NodeParameters } from './nodeConfigurationState';
import { applyReferenceParameters } from './roiReference';
import { withSwitchPorts } from './switchPorts';
import { compatiblePorts, type PortRecommendation } from './toolboxModel';
import type { NodeCatalogItem, PluginLoadInfo, PluginSdkInfo, PortDescriptor, WorkflowPayload, WorkflowModuleParameter, WorkflowModuleVersion } from './types';
import { apiFetch } from './apiClient';
import { apiUrl, plugins as pluginEndpoints, workflows } from './endpoints';

// R12（2026-10-12 复审）：低频重面板按需加载（dynamic import + Suspense）——首屏不下载其代码，
// 首次打开时拉取并进入浏览器缓存。配对的渲染侧见 LazyPanel（条件挂载：关闭即卸载）。
const JobPanel = lazy(() => import('./components/JobPanel'));
const TracePanel = lazy(() => import('./components/TracePanel'));
const DatasetValidationPanel = lazy(() => import('./components/DatasetValidationPanel'));
const ModelPackagePanel = lazy(() => import('./components/ModelPackagePanel'));
const ParameterTuningPanel = lazy(() => import('./components/ParameterTuningPanel'));
const WorkflowModulePanel = lazy(() => import('./components/WorkflowModulePanel'));
const CalibrationPanel = lazy(() => import('./components/CalibrationPanel'));
const FrameTreePanel = lazy(() => import('./components/FrameTreePanel'));
const HardwareProvenancePanel = lazy(() => import('./components/HardwareProvenancePanel'));
const PluginPackagePanel = lazy(() => import('./components/PluginPackagePanel'));
const AboutPanel = lazy(() => import('./components/AboutPanel'));

// 模块级共享件（LazyPanel / 常量 / 类型 / 图数据纯函数）见 ./appEditorShared（巨型组件拆分，2026-10-23）。
import {
  LazyPanel, nodeTypes,
  CANVAS_FIT_PADDING, COLLAPSED_RESULT_BAR_HEIGHT, DEMO_GROUPS, DEMO_LABELS,
  NEW_NODE_HEIGHT, NEW_NODE_WIDTH, RECENT_NODES_LIMIT, SPOT_GAP, WORKFLOW_IO_TIMEOUT_MS,
  edgePresentation, getPorts, hydrateNodesFromCatalog, initial,
  moduleCatalogFromNode, moduleMetadata, persistNodeCompact, persistRecentNodeTypes, readNodeCompact, readRecentNodeTypes,
  type ConnectionPicker, type NodeAttachment, type WorkflowDocumentSummary
} from './appEditorShared';
import EditorHeader from './components/EditorHeader';
import EditorBottomDock from './components/EditorBottomDock';
import ShortcutHelpModal from './components/ShortcutHelpModal';
import TemplateGalleryModal from './components/TemplateGalleryModal';
import WorkflowPickerModal from './components/WorkflowPickerModal';

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
  const [nodeConfigurationId, setNodeConfigurationId] = useState<string>();
  const [configurationTab, setConfigurationTab] = useState<NodeConfigurationTab>('parameters');
  const openNodeConfiguration = useCallback((nodeId: string, tab: NodeConfigurationTab = 'parameters') => {
    setSelectedId(nodeId);
    setConfigurationTab(tab);
    setNodeConfigurationId(nodeId);
  }, []);
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
  // 模板入口 P0：顶栏下拉改为“从模板新建”弹窗——替换当前流程属于破坏性动作，必须显式确认。
  // 查询/分类/选中状态内聚于 TemplateGalleryModal（打开时重置），此处只保留开关。
  const [templateOpen, setTemplateOpen] = useState(false);
  const [imagePreviewOpen, setImagePreviewOpen] = useState(false);
  const showTemplates = () => { setTemplateOpen(true); };
  // 专注画布（P1）：一键收起全部面板，画布独占窗口；退出时还原进入前的布局
  const [focusMode, setFocusMode] = useState(false);
  const focusSnapshotRef = useRef<LayoutState | null>(null);
  // R07（2026-10-12 复审）/F05（2026-10-12 复核）：并发基线修订**必须绑定它所属的文档**——
  // 单一字符串在切换文档（模板/Job/模块/草稿）后会携带错误修订产生误报 409。
  // undefined = 未知基线（保存前先探测后端）；revision='' = 后端尚无修订记录。
  const workflowRevisionRef = useRef<DocumentBaseline>(undefined);
  const [designerWorkflowId, setDesignerWorkflowId] = useState('demo-linear');
  const [workflowPickerOpen, setWorkflowPickerOpen] = useState(false);
  const [savedWorkflows, setSavedWorkflows] = useState<WorkflowDocumentSummary[]>([]);
  const [workflowToLoad, setWorkflowToLoad] = useState<string>();
  const [workflowListLoading, setWorkflowListLoading] = useState(false);
  const [workflowListError, setWorkflowListError] = useState<string>();
  // 保存基线绑定文档生命周期，整块替换才换代（A→B→A 也不接受迟到响应）。
  const documentGenerationRef = useRef(0);
  const [designerWorkflowName, setDesignerWorkflowName] = useState('VisionStudio · 线性流程示例');

  useEffect(() => {
    apiFetch(apiUrl('host-mode')).then(r => r.ok ? r.json() : undefined).then(data => setRuntimeOnly(Boolean(data?.runtimeOnly))).catch(() => undefined);
  }, []);
  useEffect(() => { persistNodeCompact(compactNodes); }, [compactNodes]);
  const [pluginsOpen, setPluginsOpen] = useState(false);
  const [camerasOpen, setCamerasOpen] = useState(false);
  const [robotsOpen, setRobotsOpen] = useState(false);
  const [devicesOpen, setDevicesOpen] = useState(false);
  const [jobsOpen, setJobsOpen] = useState(false);
  const [tracesOpen, setTracesOpen] = useState(false);
  const [validationOpen, setValidationOpen] = useState(false);
  const [modelPackagesOpen, setModelPackagesOpen] = useState(false);
  const [tuningOpen, setTuningOpen] = useState(false);
  const [modulesOpen, setModulesOpen] = useState(false);
  const [calibrationOpen, setCalibrationOpen] = useState(false);
  const [framesOpen, setFramesOpen] = useState(false);
  const [productionOpen, setProductionOpen] = useState(false);
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);
  const [hardwareProvenanceOpen, setHardwareProvenanceOpen] = useState(false);
  const [runtimeOnly, setRuntimeOnly] = useState(false);
  const [aboutOpen, setAboutOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const { mode: themeMode, toggle: toggleTheme } = useThemeMode();
  const { screenToFlowPosition, getZoom, getViewport, setCenter, fitView } = useReactFlow();
  const nodesInitialized = useNodesInitialized();
  const initialFitDone = useRef(false);
  useEffect(() => {
    if (!nodesInitialized || initialFitDone.current) return;
    // 节点测量完成后再适配首屏，避免初次布局尚未稳定时放大到 200%。
    const frame = requestAnimationFrame(() => {
      initialFitDone.current = true;
      void fitView({ padding: CANVAS_FIT_PADDING });
    });
    return () => cancelAnimationFrame(frame);
  }, [nodesInitialized, fitView]);
  useEffect(() => {
    if (!imagePreviewOpen || focusMode) return;
    // 打开预览会压缩画布；仅在此显式布局操作时重新适配，日常选节点不改变视角。
    const frame = requestAnimationFrame(() => { void fitView({ padding: CANVAS_FIT_PADDING, duration: 180 }); });
    return () => cancelAnimationFrame(frame);
  }, [imagePreviewOpen, focusMode, fitView]);
  const [messageApi, contextHolder] = message.useMessage();
  // A04：远端已存在同名文档时，覆盖必须由用户显式确认（绝不静默覆盖他人修改）。
  const [modalApi, modalContextHolder] = Modal.useModal();

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
    running, result, resultReceivedAt, clearResult, applyResult, resultStale,
    run, abortRun, runNode, runFromHere, previewNode,
    debugSession, debugBusy, allowSideEffects, setAllowSideEffects, anyBusy,
    hasSideEffectNodes, hasModuleCalls,
    startDebugSession, continueDebugSession, runSelectedNodeInSession, endDebugSession, debugStatus
  } = useRunController({ workflowPayload, nodes, catalog, selectedId, nodeLabels, breakpoints, setNodes, setEdges, setBottomTab, messageApi });

  // 校验问题列表 + 问题连线高亮（点击问题项定位节点或连线）
  const [highlightedEdgeId, setHighlightedEdgeId] = useState<string>();
  const { validationIssues, validationState, needsRevalidate, compiledDsl, validationBusy, compileBusy,
    validate, inspectDsl, resetInspection, closeDsl } = useWorkflowInspection({
    workflow: workflowPayload, nodes, edges, catalog, generationRef: documentGenerationRef, messages: messageApi,
    onValidated: () => {
      setBottomTab('issues');
      updateLayout({ bottomCollapsed: false });
      setHighlightedEdgeId(undefined);
    },
  });
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

  // 退出专注的统一收口（P2）：restoreSnapshot=true 还原进入前的布局快照（“退出专注”按钮）；
  // false 表示布局改由调用方接管（切换布局预设），快照直接丢弃，避免残留快照覆盖用户的新选择
  const exitFocusMode = (restoreSnapshot: boolean) => {
    const snapshot = focusSnapshotRef.current;
    focusSnapshotRef.current = null;
    setFocusMode(false);
    if (restoreSnapshot && snapshot) updateLayout({ ...snapshot });
  };

  // 布局预设只调整面板；查看图像统一使用画布上的“查看图像”入口。
  const applyLayoutPreset = (preset: LayoutPreset) => {
    if (focusMode) exitFocusMode(false);
    setLayout((current) => ({
      ...current, preset, ...LAYOUT_PRESETS[preset], bottomSizing: 'auto',
      leftCollapsed: false, bottomCollapsed: false, bottomMaximized: false
    }));
  };
  const previewVar = (name: string, value: string) => shellRef.current?.style.setProperty(name, value);

  // 工具箱与结果区遵循主工作区最小尺寸；图像预览在中心区域内响应式分栏。
  const leftMax = Math.max(LAYOUT_LIMITS.left.min, shellSize.width - MIN_CENTER_WIDTH);
  const shownLeftBase = clampNumber(layout.left, LAYOUT_LIMITS.left.min, leftMax);
  const bottomMax = Math.max(LAYOUT_LIMITS.bottom.min, shellSize.height - 96 - MIN_WORKSPACE_HEIGHT);
  const normalBottom = layout.bottomSizing === 'manual' ? layout.bottom : automaticResultHeight(shellSize.height);
  const shownBottomBase = layout.bottomMaximized ? bottomMax : clampNumber(normalBottom, LAYOUT_LIMITS.bottom.min, bottomMax);
  const shownLeft = layout.leftCollapsed ? 0 : shownLeftBase;
  // 折叠态：保留细标签条（而非 0 高度）——运行/校验入口始终可见，点击标签即展开
  const shownBottom = layout.bottomCollapsed ? COLLAPSED_RESULT_BAR_HEIGHT : shownBottomBase;
  const presetSizes = LAYOUT_PRESETS[layout.preset === 'custom' ? 'edit' : layout.preset];

  const refreshCatalog = useCallback(async () => {
    try {
      const response = await apiFetch(apiUrl('catalog'));
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

    apiFetch(pluginEndpoints.list)
      .then((r) => (r.ok ? r.json() : []))
      // 契约漂移防御：网关错误体、代理返回的 JSON 对象都会走到这里。
      // 非数组一旦进入 plugins 状态，工具栏标签的 plugins.filter 就会抛错，
      // 而它在面板级错误边界之外——整棵编辑器会被顶层边界替换成错误页。
      .then((data) => setPlugins(Array.isArray(data) ? data : []))
      .catch(() => setPlugins([]));
    apiFetch(apiUrl('plugins/sdk'))
      .then((r) => (r.ok ? r.json() : undefined))
      .then((data) => setPluginSdk(data))
      .catch(() => setPluginSdk(undefined));
  }, [messageApi, setNodes]);

  const rescanPlugins = useCallback(async () => {
    setPluginRescanning(true);
    try {
      const response = await apiFetch(apiUrl('plugins/rescan'), { method: 'POST' });
      const body = await response.json().catch(() => undefined);
      if (!response.ok) throw new Error(body?.detail ?? body?.error ?? `Plugin rescan failed (${response.status})`);
      // 同上：body.plugins 可能是对象或缺省，不能让非数组进入状态。
      setPlugins(Array.isArray(body?.plugins) ? body.plugins : []);
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
  const configurationNode = nodes.find((node) => node.id === nodeConfigurationId);
  const configurationCatalog = catalog.find((item) => item.type === configurationNode?.data.typeKey) ?? moduleCatalogFromNode(configurationNode);
  const configurationPreviewBlockedReason = debugSession ? '请先结束当前调试会话，再测试暂存参数。'
    : configurationNode?.data.typeKey === 'module.call' ? '复用模块暂不支持单节点测试。'
    : configurationCatalog?.capabilities?.supportsRunNode === false ? '此节点不支持单节点测试，请应用后通过流程运行入口执行。' : undefined;

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
  const selectedEdgeIds = useMemo(() => edges.filter((edge) => edge.selected).map((edge) => edge.id), [edges]);

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

  const applyNodeParameters = (parameters: NodeParameters) => {
    if (!nodeConfigurationId || anyBusy || !nodesRef.current.some((node) => node.id === nodeConfigurationId)) return false;
    pushHistory('应用节点参数');
    const committed = cloneParameters(parameters);
    const targetId = nodeConfigurationId;
    setNodes((current) => applyReferenceParameters(current, edges, targetId, committed).map((node) => {
      if (node.id !== targetId) return node;
      const catalogItem = catalog.find((item) => item.type === node.data.typeKey);
      return catalogItem ? { ...node, data: { ...node.data, outputs: withSwitchPorts(catalogItem, committed).outputs } } : node;
    }));
    return true;
  };

  // 已保存 / 未保存：用「当前负载快照 vs 上次保存/加载快照」比对，避免在每个改动入口手动打脏标记
  const workflowSnapshot = useMemo(() => JSON.stringify(workflowPayload), [workflowPayload]);
  const [savedSnapshot, setSavedSnapshot] = useState<string>();
  // 最近一次保存到后端的时间（仅保存成功时记录；批量加载/恢复草稿会清空）
  const [lastSavedAt, setLastSavedAt] = useState<Date>();
  const { save, saving } = useWorkflowSave({
    workflowId: designerWorkflowId, snapshot: workflowSnapshot,
    generationRef: documentGenerationRef, baselineRef: workflowRevisionRef, messages: messageApi,
    onSaved: (snapshot) => { setSavedSnapshot(snapshot); setLastSavedAt(new Date()); },
    confirmOverwrite: (id) => new Promise<boolean>((resolve) => {
      modalApi.confirm({
        title: '后端已存在该工作流',
        content: `后端已保存工作流 '${id}'，而当前编辑器内容不是从该版本加载的。继续保存将覆盖后端版本；也可取消并“打开已有流程”加载核对后再保存。`,
        okText: '确认覆盖', cancelText: '取消保存',
        onOk: () => resolve(true), onCancel: () => resolve(false),
      });
    }),
  });
  // 节点详情打开配置弹窗并定位到统一结果区。
  useEffect(() => onShowNodeDetails((nodeId) => openNodeConfiguration(nodeId, 'result')), [openNodeConfiguration]);
  useEffect(() => onConfigureNode((nodeId) => openNodeConfiguration(nodeId)), [openNodeConfiguration]);
  useEffect(() => {
    if (nodeConfigurationId && (selectedId !== nodeConfigurationId || !nodes.some((node) => node.id === nodeConfigurationId))) {
      setNodeConfigurationId(undefined);
    }
  }, [nodeConfigurationId, selectedId, nodes]);
  const markCleanOnNextChange = useRef(false);
  const dirty = savedSnapshot !== undefined && workflowSnapshot !== savedSnapshot;
  const workflowChanges = useWorkflowChanges({
    snapshot: workflowSnapshot, dirty, generationRef: documentGenerationRef, save,
    warning: (text) => { void messageApi.warning(text); },
    apply: (workflow, options) => {
      restoreWorkflow(workflow, { markClean: options.mode === 'edit' ? false : options.markClean === true, preserveDocument: options.mode === 'edit', editLabel: options.label });
      if (options.revision !== undefined) workflowRevisionRef.current = { id: workflow.id, revision: options.revision };
    },
  });
  const pendingGuard = workflowChanges.pending;
  const guardingSave = saving || workflowChanges.busy;

  // 模板真实图数据只构建一次；目录变化时同步更新预览中的显示名。
  // 模板卡片/预览的派生数据内聚于 TemplateGalleryModal（巨型组件拆分，2026-10-23）。

  // P0-1：每次新的运行/调试结果落地时自动展开底部结果区（首次使用默认收起，结果出现即回位）
  const lastExpandedRunRef = useRef<string | undefined>(undefined);
  useEffect(() => {
    const runId = result?.runId;
    if (!runId || lastExpandedRunRef.current === runId) return;
    lastExpandedRunRef.current = runId;
    setBottomTab('result');
    updateLayout({ bottomCollapsed: false, bottomMaximized: false });
  }, [result?.runId, setBottomTab, updateLayout]);

  useEffect(() => {
    if (markCleanOnNextChange.current) {
      markCleanOnNextChange.current = false;
      setSavedSnapshot(workflowSnapshot);
      return;
    }
    setSavedSnapshot((current) => current ?? workflowSnapshot);
  }, [workflowSnapshot]);

  // 专注画布（P1）：一键收起全部面板让画布独占窗口；退出经 exitFocusMode 还原进入前的布局快照
  const toggleFocusMode = () => {
    if (focusMode) { exitFocusMode(true); return; }
    focusSnapshotRef.current = layout;
    setFocusMode(true);
    updateLayout({ leftCollapsed: true, bottomCollapsed: true, bottomMaximized: false });
  };

  // 切换示例 / 加载会整块替换流程图，存在未保存修改时先让用户选择
  const guardUnsaved = (label: string, run: () => void) => { void workflowChanges.request(label, run); };

  const applyDemo = (key: DemoKey) => {
    // A03/A04：整块替换文档即新代次——在途旧响应作废；并发基线失效（下次保存显式重建）。
    documentGenerationRef.current += 1;
    workflowRevisionRef.current = undefined;
    const rawGraph = demos[key]();
    const graph = { ...rawGraph, nodes: hydrateNodesFromCatalog(rawGraph.nodes, catalog) };
    setDemoKey(key);
    setDesignerWorkflowId(`demo-${key}`);
    // 文档名不携带版本号（避免与品牌 V{APP_VERSION} 同屏互相矛盾），并复用分组里的中文用途名
    setDesignerWorkflowName(`VisionStudio · ${DEMO_LABELS[key] ?? key}`);
    setNodes(graph.nodes.map((node) => ({ ...node, data: { ...node.data, breakpoint: false } })));
    setEdges(graph.edges);
    setSelectedId(key === 'roi' ? 'threshold-1' : (graph.nodes[0]?.id ?? ''));
    history.reset();
    setHistoryVersion((version) => version + 1);
    markCleanOnNextChange.current = true;
    clearResult(); // 结果与图像（预览/逐节点目录）随 runId 代际一并失效，由 useRunImages 接管
    setLastSavedAt(undefined);
    resetInspection();
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

  const restoreWorkflow = useCallback((workflow: WorkflowPayload, options?: { markClean?: boolean; preserveDocument?: boolean; editLabel?: string }) => {
    // A03/A04：整块替换文档即新代次——在途旧响应作废；并发基线失效。
    // 调用方如需绑定"本次实际加载"的修订基线，必须在 restore 之后重新设置（load 即如此）。
    if (!isWorkflowDocument(workflow)) throw new Error('工作流文档结构无效');
    const restoredNodes: Node[] = workflow.nodes.map((item) => {
      const meta = catalog.find((x) => x.type === item.type);
      const parameters = item.parameters ?? {};
      const nodeMeta = meta ? withSwitchPorts(meta, parameters) : undefined;
      const module = item.type === 'module.call' ? moduleMetadata(parameters) : undefined;
      return {
        id: item.id,
        type: 'vision',
        position: item.position ?? { x: 100, y: 100 },
        data: {
          typeKey: item.type,
          label: item.name ?? nodeMeta?.displayName ?? (item.type === 'module.call' ? String(parameters.moduleId ?? 'Reusable Module') : item.type),
          parameters,
          ...(module ? { inputs: module.inputs, outputs: module.outputs }
            : nodeMeta ? catalogNodeData(nodeMeta) : { inputs: [], outputs: [] }),
          moduleParameters: module?.moduleParameters ?? [],
          moduleVersion: parameters.moduleVersion,
          status: 'idle',
          breakpoint: options?.preserveDocument
            ? Boolean(nodesRef.current.find((node) => node.id === item.id && node.data.typeKey === item.type)?.data.breakpoint) : false
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
    // 先完成图数据构建；无效模块元数据不能造成身份已切换、画布未更新的半应用状态。
    if (options?.preserveDocument) pushHistory(options.editLabel ?? '应用流程编辑');
    if (!options?.preserveDocument) {
      documentGenerationRef.current += 1;
      workflowRevisionRef.current = undefined;
      setDesignerWorkflowId(workflow.id);
      setDesignerWorkflowName(workflow.name);
    }
    setNodes(restoredNodes);
    setEdges(restoredEdges);
    setSelectedId(options?.preserveDocument && restoredNodes.some((node) => node.id === selectedRef.current) ? selectedRef.current : restoredNodes[0]?.id ?? '');
    // 文档替换重建撤销时间线；调优/模块提取是同一文档的可撤销编辑。
    if (!options?.preserveDocument) history.reset();
    setHistoryVersion((version) => version + 1);
    markCleanOnNextChange.current = options?.markClean !== false;
    clearResult(); // 结果与图像（预览/逐节点目录）随 runId 代际一并失效，由 useRunImages 接管
    if (!options?.preserveDocument) setLastSavedAt(undefined); // 文档替换后旧保存时间不再代表当前文档
    resetInspection();
    setHighlightedEdgeId(undefined);
    if (!options?.preserveDocument) setTimeout(() => fitView({ padding: CANVAS_FIT_PADDING, duration: 250 }), 0);
  }, [catalog, fitView, history, pushHistory, resetInspection, setEdges, setNodes]);

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

  const workflowImport = useWorkflowImport({ active: workflowPickerOpen, contextKey: workflowToLoad,
    prepare: workflowChanges.prepare, error: (text) => { void messageApi.error(text); } });
  const load = (requestedId: string) => workflowImport.load({
    label: '加载已保存的工作流', url: workflows.byId(requestedId),
    select: (data) => {
      if (!isWorkflowDocument(data) || data.id !== requestedId) throw new Error('响应工作流与选中的文档不匹配');
      return data;
    },
    options: (response) => ({ markClean: true, revision: response.headers.get('X-Workflow-Revision') ?? '' }),
    onApplied: () => { setWorkflowPickerOpen(false); void messageApi.success('工作流已加载'); },
  });

  const openWorkflowPicker = async () => {
    setWorkflowPickerOpen(true);
    setWorkflowListLoading(true);
    setWorkflowListError(undefined);
    try {
      const response = await apiFetch(workflows.list, { signal: AbortSignal.timeout(WORKFLOW_IO_TIMEOUT_MS) });
      if (!response.ok) throw new Error(await responseErrorMessage(response, `HTTP ${response.status}`));
      const items = await response.json() as WorkflowDocumentSummary[];
      setSavedWorkflows(items);
      setWorkflowToLoad((current) => items.some((item) => item.id === current)
        ? current
        : items.some((item) => item.id === designerWorkflowId) ? designerWorkflowId : items[0]?.id);
    } catch (error) {
      setWorkflowListError(error instanceof Error ? error.message : '已保存工作流列表加载失败');
      setSavedWorkflows([]);
      setWorkflowToLoad(undefined);
    } finally { setWorkflowListLoading(false); }
  };

  const loadSelectedSavedWorkflow = () => { if (workflowToLoad) void load(workflowToLoad); };

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

  // 删除选中的节点与连线：连线可独立删除，节点删除时仍会一并清理其关联连线。
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

  // 快捷键处理器见 ./useEditorShortcuts（巨型组件拆分，2026-10-23）：
  // Ctrl/Cmd+S 保存、Ctrl/Cmd+F 查找、Ctrl/Cmd+Z 撤销、Ctrl/Cmd+Shift+Z（或 Ctrl+Y）重做、
  // Ctrl/Cmd+C / V 复制粘贴选中集合、Delete/Backspace 删除选中的节点或连线。
  useEditorShortcuts({
    nodeConfigurationId, save, undo, redo, copySelection, deleteElements, pushHistory,
    setNodes, setEdges, setSelectedId, messageApi, nodes, selectedNodeIds, selectedEdgeIds,
    searchInputRef, clipboardRef, pasteIndexRef
  });

  useWorkflowDraft({
    snapshot: workflowSnapshot, demoKey, dirty, recoveryPending: Boolean(draftNotice),
    onFailure: () => { void messageApi.warning('本地草稿保存失败（浏览器存储已满或被禁用），无法热恢复；请尽快 Ctrl+S 保存到后端'); },
  });

  const restoreDraft = () => guardUnsaved('恢复本地草稿', () => {
    const notice = draftNotice;
    if (!notice) return;
    try {
      restoreWorkflow(notice.payload, { markClean: false });
      // 同步工作流身份：草稿保存时的 ID/名称要跟着恢复，否则显示的 ID 与保存目标会脱钩
      if (notice.workflowId) setDesignerWorkflowId(notice.workflowId);
      if (notice.workflowName) setDesignerWorkflowName(notice.workflowName);
      if (notice.demoKey && notice.demoKey in demos) setDemoKey(notice.demoKey as DemoKey);
      setDraftNotice(undefined);
      messageApi.success('已恢复本地草稿（尚未保存到后端，Ctrl+S 保存）');
    } catch {
      clearDraft();
      setDraftNotice(undefined);
      messageApi.error('本地草稿数据无效，已丢弃');
    }
  });

  const discardDraft = () => {
    clearDraft();
    setDraftNotice(undefined);
    messageApi.info('已丢弃本地草稿');
  };

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

  // 顶栏二级入口：高频动作（校验 / 保存 / 运行）常驻，其余按领域归入四个下拉菜单
  // 动作名带上目标节点，让用户始终清楚“当前操作作用于谁”
  const selectedLabel = selectedId ? (nodeLabels[selectedId] ?? selectedId) : '';
  const targetSuffix = selectedLabel ? `：${selectedLabel}` : '';
  const debugMenuItems: MenuProps['items'] = [
    ...(debugSession ? [
      { key: 'debug-state', label: `当前会话：${debugStatus.text}`, disabled: true },
      { key: 'debug-continue', label: '继续调试', disabled: anyBusy || debugSession.status !== 'halted', onClick: () => void continueDebugSession() },
      { key: 'debug-cached-node', label: `用缓存输入运行节点${targetSuffix}`, disabled: anyBusy || debugSession.status === 'faulted' || !selectedId, onClick: () => void runSelectedNodeInSession() },
      { key: 'debug-end', label: '结束调试会话', danger: true, disabled: anyBusy, onClick: () => void endDebugSession() },
      { type: 'divider' as const }
    ] : []),
    { key: 'breakpoint', label: `切换断点${targetSuffix}`, disabled: !selectedId, onClick: toggleBreakpoint },
    { key: 'run-node', label: `运行节点${targetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !selectedId || selectedNode?.data.typeKey === 'module.call', onClick: runNode },
    { key: 'run-from-here', label: `从此处运行${targetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !selectedId || selectedNode?.data.typeKey === 'module.call', onClick: runFromHere },
    { type: 'divider' },
    ...(hasSideEffectNodes ? [{ key: 'debug-side-effects', label: <Checkbox checked={allowSideEffects} disabled={anyBusy || Boolean(debugSession)} onChange={(event) => setAllowSideEffects(event.target.checked)}>允许真实设备写入</Checkbox> }] : []),
    { key: 'debug-breakpoints', label: `开始断点调试${breakpoints.length ? `（${breakpoints.length}）` : ''}`, disabled: anyBusy || hasModuleCalls || Boolean(debugSession) || breakpoints.length === 0, onClick: () => void startDebugSession() },
    { key: 'core-dsl', label: '查看核心 DSL', disabled: compileBusy, onClick: inspectDsl }
  ];

  // 节点右键菜单：高频动作就地可达，不必再去找顶栏菜单；副行（extra）显示对应快捷键
  const contextNode = nodeContextMenu ? nodes.find((node) => node.id === nodeContextMenu.nodeId) : undefined;
  const contextNodeLabel = contextNode ? (nodeLabels[contextNode.id] ?? contextNode.id) : '';
  const contextTargetSuffix = contextNodeLabel ? `：${contextNodeLabel}` : '';
  const nodeContextMenuItems: MenuProps['items'] = [
    { key: 'configure', label: `配置节点${contextTargetSuffix}`, disabled: !contextNode, onClick: () => { if (contextNode) openNodeConfiguration(contextNode.id); } },
    { key: 'view-image', label: `查看图像${contextTargetSuffix}`, disabled: !contextNode, onClick: () => { if (contextNode) openNodeConfiguration(contextNode.id, 'image'); } },
    { key: 'run-node', label: `运行此节点${contextTargetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !contextNode || contextNode.data.typeKey === 'module.call', onClick: runNode },
    { key: 'run-from-here', label: `从此处运行${contextTargetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !contextNode || contextNode.data.typeKey === 'module.call', onClick: runFromHere },
    { key: 'breakpoint', label: `切换断点${contextTargetSuffix}`, disabled: !contextNode, onClick: toggleBreakpoint },
    { type: 'divider' },
    { key: 'copy', label: `复制节点${contextTargetSuffix}`, extra: 'Ctrl+C', onClick: copySelection },
    { key: 'delete', label: `删除节点${contextTargetSuffix}`, extra: 'Del', danger: true, disabled: !contextNode, onClick: () => { if (nodeContextMenu) deleteElements([nodeContextMenu.nodeId], []); } }
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
    { key: 'jobs', label: '配方管理', onClick: () => setJobsOpen(true) },
    { key: 'traces', label: '运行追溯', onClick: () => setTracesOpen(true) },
    { key: 'production', label: '生产运行', onClick: () => setProductionOpen(true) }
  ];

  const toolMenuItems: MenuProps['items'] = [
    // 目录/插件规模从顶栏收进菜单，降低顶栏视觉权重
    { type: 'group', label: `节点目录 · ${catalogSource === 'runtime' ? '运行时' : '本地'} ${catalog.length}（schema ${catalogSchemaVersion}）` },
    { type: 'group', label: `插件 ${plugins.filter((p) => p?.loaded).length} 个已加载` },
    { type: 'divider' },
    { key: 'undo', label: `撤销${history.undoLabel ? ` · ${history.undoLabel}` : ''}（Ctrl+Z）`, disabled: !history.canUndo, onClick: undo },
    { key: 'redo', label: `重做${history.redoLabel ? ` · ${history.redoLabel}` : ''}（Ctrl+Shift+Z）`, disabled: !history.canRedo, onClick: redo },
    { key: 'shortcuts', label: '键盘快捷键…', onClick: () => setShortcutsOpen(true) },
    { type: 'divider' },
    { key: 'plugins', label: '插件', onClick: () => setPluginsOpen(true) },
    { key: 'modules', label: '可复用模块', onClick: () => setModulesOpen(true) },
    { key: 'tuning', label: '参数调优', onClick: () => setTuningOpen(true) },
    { key: 'model-packages', label: '外部训练包', onClick: () => setModelPackagesOpen(true) },
    { key: 'validation', label: '数据集验证', onClick: () => setValidationOpen(true) },
    { type: 'divider' },
    { key: 'layout-edit', label: '布局：标准布局' },
    { key: 'layout-image', label: '布局：宽画布' },
    { key: 'layout-reset', label: '布局：复位为预设尺寸' },
    { type: 'divider' },
    { key: 'load', label: '加载已保存工作流', onClick: () => void openWorkflowPicker() },
    { key: 'theme', label: themeMode === 'dark' ? '切换到浅色主题' : '切换到深色主题', onClick: toggleTheme },
    { key: 'about', label: '关于 VisionStudio', onClick: () => setAboutOpen(true) }
  ];

  // 领域菜单先收进“更多”，更窄时再收起文件操作；流程名和执行动作独占第二行。
  const moreMenu: MenuProps = {
    items: [
      { key: 'more-template', label: '从模板新建', onClick: showTemplates },
      { key: 'more-open', label: '打开已有流程', onClick: () => void openWorkflowPicker() },
      { type: 'divider' },
      ...debugMenuItems,
      ...deviceMenuItems,
      ...productionMenuItems,
      ...toolMenuItems
    ],
    selectable: true,
    selectedKeys: layout.preset === 'custom' ? [] : [layout.preset],
    onClick: ({ key }) => {
      if (key === 'layout-edit') applyLayoutPreset('edit');
      else if (key === 'layout-image') applyLayoutPreset('image');
      else if (key === 'layout-reset') applyLayoutPreset(layout.preset === 'custom' ? 'edit' : layout.preset);
    }
  };

  if (runtimeOnly) return <>
    <ErrorBoundary variant="panel" scope="生产运行">
      <ProductionPanel open={true} onClose={() => undefined} embedded />
    </ErrorBoundary>
    <Button className="runtime-diagnostics-button" type="primary" ghost onClick={() => setDiagnosticsOpen(true)}>诊断</Button>
    {/* 操作员页无顶栏：账号入口固定在「诊断」按钮下方 */}
    <div className="runtime-account-slot"><SecurityAccountBadge /></div>
    <ErrorBoundary variant="panel" scope="诊断" onDismiss={() => setDiagnosticsOpen(false)}>
      <DiagnosticsPanel open={diagnosticsOpen} onClose={() => setDiagnosticsOpen(false)} />
    </ErrorBoundary>
  </>;

  return (
    <div
      className="app-shell"
      ref={shellRef}
      style={{
        '--vs-left-width': `${shownLeft}px`,
        '--vs-bottom-height': `${shownBottom}px`
      } as CSSProperties}
    >
      {contextHolder}{modalContextHolder}
      <EditorHeader
        menus={{ debug: debugMenuItems, device: deviceMenuItems, production: productionMenuItems, tools: toolMenuItems, more: moreMenu }}
        layoutPreset={layout.preset}
        onApplyLayoutPreset={applyLayoutPreset}
        templates={{ show: showTemplates, openSaved: openWorkflowPicker }}
        debug={{ inSession: Boolean(debugSession), statusText: debugStatus.text }}
        run={{ running, debugBusy, onRun: run, onAbort: abortRun }}
        workflow={{ name: designerWorkflowName, id: designerWorkflowId, onRename: setDesignerWorkflowName, dirty, lastSavedAt }}
        validation={{ status: validationState.status, summary: validationState.summary, needsRevalidate, issueCount: validationIssues.length, busy: validationBusy, onValidate: validate }}
        catalogError={catalogError}
        save={{ busy: saving, onSave: save }}
      />

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
          <div className="workspace-toolbar">
            <div className="canvas-toolbar" role="toolbar" aria-label="画布编辑工具">
              <div className="canvas-search">
                <Input
                  ref={searchInputRef}
                  size="small"
                  allowClear
                  className="canvas-search-input"
                  placeholder="画布中查找（Ctrl+F）"
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
              {/* 撤销/重做可见入口（P0）：高频编辑动作不再只藏在「工具」菜单与快捷键里；
                  tooltip 带历史标签（如“撤销 · 连线”），运行中也随 anyBusy 一起禁用 */}
              <Button size="small" className="canvas-history-button" disabled={!history.canUndo || anyBusy}
                title={`撤销${history.undoLabel ? ` · ${history.undoLabel}` : ''}（Ctrl+Z）`} aria-label="撤销" onClick={undo}>↶</Button>
              <Button size="small" className="canvas-history-button" disabled={!history.canRedo || anyBusy}
                title={`重做${history.redoLabel ? ` · ${history.redoLabel}` : ''}（Ctrl+Shift+Z）`} aria-label="重做" onClick={redo}>↷</Button>
              {selectedEdgeIds.length > 0 && <Button size="small" danger disabled={anyBusy}
                aria-label={`删除选中连线 ${selectedEdgeIds.length} 条`}
                title="删除选中的连线（Delete / Backspace）"
                onClick={() => deleteElements([], selectedEdgeIds)}>删除连线 · {selectedEdgeIds.length}</Button>}
              {selectedEdgeIds.length === 0 && <span className="canvas-edge-delete-hint" title="单击画布中的连线将其选中">连线：选中后按 Del 删除</span>}
              <span className="canvas-toolbar-divider" />
              <label className="canvas-compact-toggle" title="紧凑模式：压缩节点标题与端口间距，适合多端口的大型流程">
                <Switch size="small" checked={compactNodes} onChange={setCompactNodes} />
                <span>紧凑</span>
              </label>
              <Button size="small" disabled={!selectedNode} title="打开所选节点的参数、图像和详细说明（也可双击节点）" onClick={() => openNodeConfiguration(selectedId)}>节点配置</Button>
              <Button size="small" type={imagePreviewOpen && !focusMode ? 'primary' : 'default'} aria-pressed={imagePreviewOpen && !focusMode} onClick={() => { if (focusMode) exitFocusMode(true); setImagePreviewOpen((open) => focusMode || !open); }}>图像预览</Button>
            </div>

            {selectedNodeIds.length >= 2 && (
              <div className="canvas-align-toolbar" role="toolbar" aria-label="多选对齐工具">
                <Tag style={{ marginInlineEnd: 0 }}>已选 {selectedNodeIds.length}</Tag>
                <Button size="small" onClick={() => alignSelection('left')}>左对齐</Button>
                <Button size="small" onClick={() => alignSelection('centerX')}>水平居中</Button>
                <Button size="small" onClick={() => alignSelection('right')}>右对齐</Button>
                <Button size="small" onClick={() => alignSelection('top')}>顶对齐</Button>
                <Button size="small" onClick={() => alignSelection('centerY')}>垂直居中</Button>
                <Button size="small" onClick={() => alignSelection('bottom')}>底对齐</Button>
              </div>
            )}

            {/* 布局切换与编辑工具同处独立工具栏，结果区折叠后仍然可达。 */}
            <div className="canvas-layout-toolbar">
              <Button size="small" onClick={() => void fitView({ padding: CANVAS_FIT_PADDING, duration: 180 })}
                title="缩放并居中显示整个流程">适应画布</Button>
              <Segmented
                size="small"
                value={layout.preset}
                // 切预设即退出专注：由 applyLayoutPreset 统一处理（丢弃专注快照，布局完全由预设接管，P2）
                onChange={(value) => { if (value !== 'custom') applyLayoutPreset(value as LayoutPreset); }}
                options={[
                  { label: '标准布局', value: 'edit' },
                  { label: '宽画布', value: 'image' },
                  ...(layout.preset === 'custom' ? [{ label: '自定义', value: 'custom' }] : [])
                ]}
              />
              <Button
                size="small"
                type={focusMode ? 'primary' : 'default'}
                onClick={toggleFocusMode}
                title={focusMode ? '退出专注：还原进入前的面板布局' : '专注画布：收起工具箱与结果区'}
              >
                {focusMode ? '退出专注' : '专注'}
              </Button>
            </div>
          </div>
          <div className={`editor-content${imagePreviewOpen && !focusMode ? ' has-preview' : ''}`}>
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

              {/* 空画布引导（P1）：首屏/新流程时给出下一步动作 */}
              {nodes.length === 0 && (
                <div className="canvas-empty-state">
                  <div className="canvas-empty-title">从左侧工具箱拖入节点，搭建你的第一条视觉流程</div>
                  <div className="canvas-empty-actions">
                    <Button size="small" type="primary" onClick={showTemplates}>从模板新建</Button>
                    <Button size="small" onClick={() => void openWorkflowPicker()}>打开已有流程</Button>
                  </div>
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
                zoomOnDoubleClick={false}
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
                onNodeDoubleClick={(_, node) => openNodeConfiguration(node.id)}
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
                // P2：右下角是画布唯一“空白处”——隐藏 React Flow 水印，避免与小地图按钮叠压
                //（@xyflow/react v12 免费支持 proOptions.hideAttribution）
                proOptions={{ hideAttribution: true }}
              >
                <Background gap={18} size={1} />
                <Controls />
                <CanvasMiniMap nodeCount={nodes.length} />
              </ReactFlow>

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

            {imagePreviewOpen && !focusMode && <WorkflowImagePreview key={designerWorkflowId} nodes={nodes} edges={edges} selectedId={selectedId}
              nodeLabels={nodeLabels} result={result} stale={resultStale} running={running || debugBusy}
              onClose={() => setImagePreviewOpen(false)} />}
          </div>
        </section>

      </main>

      {nodeConfigurationId && configurationNode && configurationCatalog && <NodeConfigurationDialog key={nodeConfigurationId} initialTab={configurationTab} open
        node={configurationNode} catalogItem={configurationCatalog} catalogItems={catalog} nodes={nodes} edges={edges} disabled={anyBusy}
        result={result} resultReceivedAt={resultReceivedAt} resultStale={resultStale}
        onClose={() => setNodeConfigurationId(undefined)} onApply={applyNodeParameters}
        onPreview={(parameters) => previewNode(nodeConfigurationId, parameters)} previewBlockedReason={configurationPreviewBlockedReason} />}

      <EditorBottomDock
        layout={layout}
        updateLayout={updateLayout}
        bottomMax={bottomMax}
        presetBottom={presetSizes.bottom}
        shownBottomBase={shownBottomBase}
        bottomTab={bottomTab}
        onBottomTabChange={setBottomTab}
        onPreviewHeight={(value) => previewVar('--vs-bottom-height', `${value}px`)}
        validationIssues={validationIssues}
        locateIssue={locateIssue}
        result={result}
        resultStale={resultStale}
        focusNode={focusNode}
        nodeLabels={nodeLabels}
        nodeTypeLabels={Object.fromEntries(catalog.map((item) => [item.type, item.displayName]))}
      />

      <Modal
        open={compiledDsl !== undefined}
        title="已编译的工作流 DSL"
        width={860}
        footer={null}
        onCancel={closeDsl}
      >
        <pre className="dsl-view">{compiledDsl}</pre>
      </Modal>

      <Modal
        open={pendingGuard !== undefined}
        title="存在未保存的修改"
        width={460}
        closable={!guardingSave}
        keyboard={!guardingSave}
        maskClosable={!guardingSave}
        onCancel={() => { if (!guardingSave) workflowChanges.cancel(); }}
        footer={[
          <Button key="cancel" disabled={guardingSave} onClick={workflowChanges.cancel}>取消</Button>,
          <Button key="discard" danger disabled={guardingSave} onClick={workflowChanges.discard}>丢弃修改</Button>,
          <Button
            key="save"
            type="primary"
            loading={guardingSave}
            onClick={() => void workflowChanges.saveAndContinue()}
          >
            保存并继续
          </Button>
        ]}
      >
        <p>当前工作流有未保存的修改，{pendingGuard?.label}前请选择如何处理：</p>
      </Modal>

      {/* P0-2：模板入口弹窗——分组/用途/预览，替换当前流程前显式确认；“打开已有流程”就近放置 */}
      <TemplateGalleryModal open={templateOpen} onClose={() => setTemplateOpen(false)}
        currentDemoKey={demoKey} catalog={catalog}
        onApply={(key) => guardUnsaved('从模板新建', () => applyDemo(key))}
        onOpenSaved={() => void openWorkflowPicker()} />

      <WorkflowPickerModal open={workflowPickerOpen} onClose={() => setWorkflowPickerOpen(false)}
        workflows={savedWorkflows} loading={workflowListLoading} error={workflowListError}
        selected={workflowToLoad} onSelect={setWorkflowToLoad}
        onLoad={loadSelectedSavedWorkflow} applying={workflowImport.loading} />

      {/* 键盘快捷键一览（工具菜单）：快捷键此前只散落在悬停提示里，没有可查入口 */}
      <ShortcutHelpModal open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />

      <ErrorBoundary variant="panel" scope="相机" onDismiss={() => setCamerasOpen(false)}>
        <CameraPanel open={camerasOpen} onClose={() => setCamerasOpen(false)} />
      </ErrorBoundary>
      <ErrorBoundary variant="panel" scope="机器人" onDismiss={() => setRobotsOpen(false)}>
        <RobotPanel open={robotsOpen} onClose={() => setRobotsOpen(false)} />
      </ErrorBoundary>
      <ErrorBoundary variant="panel" scope="设备" onDismiss={() => setDevicesOpen(false)}>
        <DevicePanel open={devicesOpen} onClose={() => setDevicesOpen(false)} />
      </ErrorBoundary>
      <LazyPanel open={framesOpen} label="图像帧" onClose={() => setFramesOpen(false)}><FrameTreePanel open onClose={() => setFramesOpen(false)} /></LazyPanel>
      <LazyPanel open={calibrationOpen} label="标定" onClose={() => setCalibrationOpen(false)}><CalibrationPanel
        open
        onClose={() => setCalibrationOpen(false)}
        canApplyToSelectedNode={['calibration.planar', 'calibration.cameraIntrinsics', 'image.undistort', 'calibration.handEye2D', 'calibration.handEye3D'].includes(String(selectedNode?.data.typeKey ?? ''))}
        selectedNodeType={selectedNode ? String(selectedNode.data.typeKey) : undefined}
        onApplyToSelectedNode={applyCalibrationParameters}
      /></LazyPanel>
      <LazyPanel open={jobsOpen} label="作业" onClose={() => setJobsOpen(false)}><JobPanel open onClose={() => setJobsOpen(false)} workflow={workflowPayload} prepareWorkflowChange={workflowChanges.prepare} onRunResult={applyResult} /></LazyPanel>
      <LazyPanel open={tracesOpen} label="追溯" onClose={() => setTracesOpen(false)}><TracePanel open onClose={() => setTracesOpen(false)} /></LazyPanel>
      <LazyPanel open={modelPackagesOpen} label="模型包" onClose={() => setModelPackagesOpen(false)}><ModelPackagePanel open onClose={() => setModelPackagesOpen(false)} /></LazyPanel>
      <LazyPanel open={validationOpen} label="数据集验证" onClose={() => setValidationOpen(false)}><DatasetValidationPanel open onClose={() => setValidationOpen(false)} currentWorkflow={workflowPayload} /></LazyPanel>
      <LazyPanel open={tuningOpen} label="参数调优" onClose={() => setTuningOpen(false)}><ParameterTuningPanel open onClose={() => setTuningOpen(false)} currentWorkflow={workflowPayload} catalog={catalog} initialNodeId={selectedId} prepareWorkflowChange={workflowChanges.prepare} onApplied={() => { setTuningOpen(false); messageApi.success('已将调优后的候选方案应用到设计器'); }} /></LazyPanel>
      <LazyPanel open={modulesOpen} label="流程模块" onClose={() => setModulesOpen(false)}><WorkflowModulePanel open onClose={() => setModulesOpen(false)} currentWorkflow={workflowPayload} selectedNodeId={selectedId} selectedNodeType={String(selectedNode?.data.typeKey ?? '')} prepareWorkflowChange={workflowChanges.prepare} onInsertModule={insertModule} onUpgradeSelected={upgradeSelectedModule} /></LazyPanel>
      <ErrorBoundary variant="panel" scope="生产运行" onDismiss={() => setProductionOpen(false)}>
        <ProductionPanel open={productionOpen} onClose={() => setProductionOpen(false)} />
      </ErrorBoundary>
      <ErrorBoundary variant="panel" scope="诊断" onDismiss={() => setDiagnosticsOpen(false)}>
        <DiagnosticsPanel open={diagnosticsOpen} onClose={() => setDiagnosticsOpen(false)} />
      </ErrorBoundary>
      <LazyPanel open={aboutOpen} label="关于" onClose={() => setAboutOpen(false)}><AboutPanel open onClose={() => setAboutOpen(false)} /></LazyPanel>
      <LazyPanel open={hardwareProvenanceOpen} label="硬件溯源" onClose={() => setHardwareProvenanceOpen(false)}><HardwareProvenancePanel open onClose={() => setHardwareProvenanceOpen(false)} /></LazyPanel>
      <LazyPanel open={pluginsOpen} label="插件包" onClose={() => setPluginsOpen(false)}><PluginPackagePanel open onClose={() => setPluginsOpen(false)} onCatalogRefresh={refreshCatalog} /></LazyPanel>
    </div>
  );
}

export default function App() {
  return <ReactFlowProvider><Editor /></ReactFlowProvider>;
}
