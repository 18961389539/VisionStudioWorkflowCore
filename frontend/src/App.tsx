import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import {
  ReactFlowProvider, useEdgesState, useNodesState, useNodesInitialized, useReactFlow, type Edge, type Node
} from '@xyflow/react';
import { Button, message, Modal, type InputRef } from 'antd';
import Toolbox from './components/Toolbox';
import NodeConfigurationDialog, { type NodeConfigurationTab } from './components/NodeConfigurationDialog';
import ProductionPanel from './components/ProductionPanel';
import DiagnosticsPanel from './components/DiagnosticsPanel';
import SplitHandle from './components/SplitHandle';
import ErrorBoundary from './components/ErrorBoundary';
import EditorHeader from './components/EditorHeader';
import EditorBottomDock from './components/EditorBottomDock';
import EditorCanvasArea, { type EditorCanvasAreaProps } from './components/EditorCanvasArea';
import EditorPanels, { type EditorPanelsProps } from './components/EditorPanels';
import EditorOverlays, { type EditorOverlaysProps } from './components/EditorOverlays';
import type { PaletteCommand } from './components/CommandPalette';
import {
  HEADER_HEIGHT, LAYOUT_LIMITS, LAYOUT_PRESETS, MIN_CENTER_WIDTH, MIN_WORKSPACE_HEIGHT, automaticResultHeight,
  clampNumber, persistLayout, readLayout, type LayoutPreset, type LayoutState
} from './layout';
import { useI18n } from './i18n/provider';
import { useThemeMode } from './theme';
import { SecurityAccountBadge } from './security';
import { demos, type DemoKey } from './demos';
import { GraphHistory, captureGraph, restoreGraph, type GraphSnapshot } from './history';
import { clearDraft, readDraft, type WorkflowDraft } from './draft';
import type { WorkflowIssue } from './validation';
import type { DocumentBaseline } from './documentSync';
import { useWorkflowChanges } from './useWorkflowChanges';
import { useWorkflowSave } from './useWorkflowSave';
import { useWorkflowDraft } from './useWorkflowDraft';
import { useWorkflowInspection } from './useWorkflowInspection';
import { useWorkflowPicker } from './useWorkflowPicker';
import { useEditorShortcuts } from './useEditorShortcuts';
import { useEditorGraphOperations } from './useEditorGraphOperations';
import { useEditorPanels } from './useEditorPanels';
import { useEditorWorkflowDocument } from './useEditorWorkflowDocument';
import { useCatalogState } from './useCatalogState';
import { onConfigureNode, onShowNodeDetails } from './uiBridge';
import { useRunController, type BottomTab } from './useRunController';
import { cloneParameters, type NodeParameters } from './nodeConfigurationState';
import { applyReferenceParameters } from './roiReference';
import { withSwitchPorts } from './switchPorts';
import type { PluginLoadInfo, WorkflowPayload } from './types';
import { apiFetch } from './apiClient';
import { apiUrl } from './endpoints';
import {
  CANVAS_FIT_PADDING, COLLAPSED_RESULT_BAR_HEIGHT, buildWorkflowPayload, createInitial, moduleCatalogFromNode,
  persistNodeCompact, readNodeCompact, readRecentNodeTypes, type ConnectionPicker
} from './appEditorShared';
import {
  buildDebugMenuItems, buildDeviceMenuItems, buildMoreMenu, buildNodeContextMenuItems, buildPaletteTargets,
  buildProductionMenuItems, buildToolMenuItems, runPaletteCommand, type PanelOpeners
} from './appEditorMenus';

function Editor() {
  // 语言（2026-10-25 接线）：catalog 保存后端英文原文，显示用的本地化目录按语言派生——
  // 切换语言不再需要重新请求，也不会改写画布上已有节点的名称（节点名属于文档内容）。
  const { lang, toggleLang, dateLocale: locale } = useI18n();
  const langRef = useRef(lang);
  langRef.current = lang;

  const [messageApi, contextHolder] = message.useMessage();
  // A04：远端已存在同名文档时，覆盖必须由用户显式确认（绝不静默覆盖他人修改）。
  const [modalApi, modalContextHolder] = Modal.useModal();
  const [initialEditorState] = useState(() => createInitial(lang));
  const [nodes, setNodes, onNodesChange] = useNodesState(initialEditorState.nodes);
  const [edges, setEdges, onEdgesChange] = useEdgesState(initialEditorState.edges);
  const [plugins, setPlugins] = useState<PluginLoadInfo[]>([]);
  const { catalog, catalogSource, catalogSchemaVersion, catalogError, refreshCatalog } = useCatalogState({
    lang, langRef, messageApi, setNodes, setPlugins
  });
  useEffect(() => { refreshCatalog(); }, [refreshCatalog]);
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
  // 保存基线绑定文档生命周期，整块替换才换代（A→B→A 也不接受迟到响应）。
  const documentGenerationRef = useRef(0);
  const [designerWorkflowName, setDesignerWorkflowName] = useState('VisionStudio · 线性流程示例');

  useEffect(() => {
    apiFetch(apiUrl('host-mode')).then(r => r.ok ? r.json() : undefined).then(data => setRuntimeOnly(Boolean(data?.runtimeOnly))).catch(() => undefined);
  }, []);
  useEffect(() => { persistNodeCompact(compactNodes); }, [compactNodes]);
  const {
    pluginsOpen, setPluginsOpen, camerasOpen, setCamerasOpen, robotsOpen, setRobotsOpen, devicesOpen, setDevicesOpen,
    jobsOpen, setJobsOpen, tracesOpen, setTracesOpen, traceRunId, setTraceRunId, commandOpen, setCommandOpen,
    validationOpen, setValidationOpen, modelPackagesOpen, setModelPackagesOpen, modelAssetsOpen, setModelAssetsOpen,
    tuningOpen, setTuningOpen, modulesOpen, setModulesOpen, calibrationOpen, setCalibrationOpen, framesOpen, setFramesOpen,
    productionOpen, setProductionOpen, diagnosticsOpen, setDiagnosticsOpen, hardwareProvenanceOpen, setHardwareProvenanceOpen,
    aboutOpen, setAboutOpen, shortcutsOpen, setShortcutsOpen
  } = useEditorPanels();
  const { mode: themeMode, toggle: toggleTheme } = useThemeMode();
  const [runtimeOnly, setRuntimeOnly] = useState(false);
  const { screenToFlowPosition, getZoom, setCenter, fitView } = useReactFlow();
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
  const breakpoints = useMemo(
    () => nodes.filter((n) => Boolean(n.data.breakpoint)).map((n) => n.id),
    [nodes]
  );
  // 性能（2026-10-26）：拖拽时每个 pointermove 都会重建负载，原来每条边都 nodes.find + 端口 find，
  // 复杂度 O(E×N×P)。改成先建 Map，整体 O(N+E)。200 节点 / 300 边的实测见 docs 性能记录。
  const workflowPayload = useMemo<WorkflowPayload>(
    () => buildWorkflowPayload(designerWorkflowId, designerWorkflowName, nodes, edges),
    [designerWorkflowId, designerWorkflowName, nodes, edges]);

  // 性能（2026-10-26）：整份文档 JSON.stringify 实测 3.6ms / 268 KB（200 节点）。拖拽时每个
  // pointermove 都会改 position → 负载变化 → 每帧再序列化一次，16ms 的帧预算被吃掉近四分之一。
  // 位置变化不影响执行语义，只有落点才需要重新判定 dirty / 版本，所以拖拽期间冻结快照源，
  // 松手后再算一次（onNodeDragStop / onSelectionDragStop）。
  const [dragging, setDragging] = useState(false);
  const [snapshotSource, setSnapshotSource] = useState<WorkflowPayload>(workflowPayload);
  useEffect(() => {
    if (!dragging) setSnapshotSource(workflowPayload);
  }, [workflowPayload, dragging]);
  const workflowSnapshot = useMemo(() => JSON.stringify(snapshotSource), [snapshotSource]);
  // 拖拽时节点名不会变，从冻结快照派生可避免每帧重建 + 让 RunPanel 的 memo 生效
  const nodeLabels = useMemo(
    () => Object.fromEntries(snapshotSource.nodes.map((node) => [node.id, String(node.name ?? node.id)])),
    [snapshotSource]);

  // 底部结果区要按类型显示中文名：此前每次渲染都 Object.fromEntries 一遍 171 条目录。
  const nodeTypeLabels = useMemo(
    () => Object.fromEntries(catalog.map((item) => [item.type, item.displayName])),
    [catalog]
  );

  // 运行 / 调试控制 + 图像资源管理（独立模块）：anyBusy 是全局唯一执行闸门；
  // 结果与图像以 result.runId 为代际统一管理（详见 useRunController.ts / useRunImages.ts）
  const {
    running, runStartedAt, result, resultReceivedAt, clearResult, applyResult, resultStale,
    run, abortRun, runNode, runFromHere, previewNode,
    debugSession, debugBusy, allowSideEffects, setAllowSideEffects, anyBusy,
    hasSideEffectNodes, hasModuleCalls,
    startDebugSession, continueDebugSession, runSelectedNodeInSession, endDebugSession, debugStatus
  } = useRunController({
    workflowPayload, workflowFingerprint: workflowSnapshot, nodes, catalog, selectedId, nodeLabels, breakpoints,
    setNodes, setEdges, setBottomTab, messageApi
  });

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
  // 节点右键菜单：记录锚点（相对画布区）与目标节点，菜单动作就地实现可达
  const [nodeContextMenu, setNodeContextMenu] = useState<{ x: number; y: number; nodeId: string }>();
  // 触屏没有右键：长按 500ms 等价于 contextmenu（2026-10-25）。手指移动/抬起即取消，避免与拖拽冲突。
  const longPressRef = useRef<{ timer: number; startX: number; startY: number } | undefined>(undefined);
  const cancelLongPress = useCallback(() => {
    if (!longPressRef.current) return;
    window.clearTimeout(longPressRef.current.timer);
    longPressRef.current = undefined;
  }, []);
  const openNodeMenuAt = useCallback((nodeId: string, clientX: number, clientY: number) => {
    setSelectedId(nodeId);
    const bounds = flowAreaRef.current?.getBoundingClientRect();
    setNodeContextMenu({ x: clientX - (bounds?.left ?? 0), y: clientY - (bounds?.top ?? 0), nodeId });
  }, []);
  useEffect(() => cancelLongPress, [cancelLongPress]);
  const displayEdges = useMemo(() => highlightedEdgeId
    ? edges.map((edge) => edge.id === highlightedEdgeId
      ? { ...edge, animated: false, style: { ...edge.style, stroke: '#ff4d4f', strokeWidth: 2.5 } }
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
  const bottomMax = Math.max(LAYOUT_LIMITS.bottom.min, shellSize.height - HEADER_HEIGHT - MIN_WORKSPACE_HEIGHT);
  const normalBottom = layout.bottomSizing === 'manual' ? layout.bottom : automaticResultHeight(shellSize.height);
  const shownBottomBase = layout.bottomMaximized ? bottomMax : clampNumber(normalBottom, LAYOUT_LIMITS.bottom.min, bottomMax);
  const shownLeft = layout.leftCollapsed ? 0 : shownLeftBase;
  // 折叠态：保留细标签条（而非 0 高度）——运行/校验入口始终可见，点击标签即展开
  const shownBottom = layout.bottomCollapsed ? COLLAPSED_RESULT_BAR_HEIGHT : shownBottomBase;
  const presetSizes = LAYOUT_PRESETS[layout.preset === 'custom' ? 'edit' : layout.preset];


  const selectedNode = nodes.find((n) => n.id === selectedId);
  const configurationNode = nodes.find((node) => node.id === nodeConfigurationId);
  const configurationCatalog = catalog.find((item) => item.type === configurationNode?.data.typeKey) ?? moduleCatalogFromNode(configurationNode);
  const configurationPreviewBlockedReason = debugSession ? '请先结束当前调试会话，再测试暂存参数。'
    : configurationNode?.data.typeKey === 'module.call' ? '复用模块暂不支持单节点测试。'
    : configurationCatalog?.capabilities?.supportsRunNode === false ? '此节点不支持单节点测试，请应用后通过流程运行入口执行。' : undefined;









  // 运行结果点行定位：选中该节点并把画布居中过去
  // 性能（2026-10-26）：focusNode 原来依赖 nodes，拖拽每个 pointermove 都换引用，
  // 导致下游 memo 化组件（RunPanel）每帧失效。改用 nodesRef，引用保持稳定。

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
      graph.focusNode(issue.nodeId);
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

  // 画布图操作（连线 / 添加 / 删除 / 复制 / 对齐 / 定位）见 ./useEditorGraphOperations。
  const graph = useEditorGraphOperations({
    nodes, nodesRef, edgesRef, selectedRef, selectedId, selectedNode, selectedNodeIds, catalog,
    setNodes, setEdges, setSelectedId, setRecentNodeTypes, setConnectionPicker, setNodeContextMenu,
    messageApi, pushHistory, onNodesChange, onEdgesChange, flowAreaRef, getZoom,
    screenToFlowPosition, setCenter, clipboardRef, pasteIndexRef
  });

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
  // 示例应用 / 整篇恢复 / 模块插入与升级见 ./useEditorWorkflowDocument。
  const { applyDemo, restoreWorkflow, insertModule, upgradeSelectedModule } = useEditorWorkflowDocument({
    catalog, nodesRef, selectedRef, selectedId, documentGenerationRef, workflowRevisionRef, markCleanOnNextChange, history,
    setNodes, setEdges, setSelectedId, setHistoryVersion, setDesignerWorkflowId, setDesignerWorkflowName,
    setDemoKey, setLastSavedAt, setHighlightedEdgeId, pushHistory, resetInspection, clearResult,
    fitView, screenToFlowPosition, messageApi
  });
  const {
    workflowPickerOpen, setWorkflowPickerOpen, savedWorkflows, workflowToLoad, setWorkflowToLoad,
    workflowListLoading, workflowListError, workflowImport, openWorkflowPicker, loadSelectedSavedWorkflow
  } = useWorkflowPicker({ designerWorkflowId, messageApi, prepare: workflowChanges.prepare });
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

  // 面板/弹窗开关集中成一份，供命令面板、顶栏菜单与收尾的挂载点复用。
  const openers: PanelOpeners = {
    setCalibrationOpen, setTracesOpen, setJobsOpen, setValidationOpen, setTuningOpen, setModulesOpen,
    setCamerasOpen, setRobotsOpen, setDevicesOpen, setProductionOpen, setDiagnosticsOpen, setPluginsOpen,
    setModelPackagesOpen, setModelAssetsOpen, setFramesOpen, setHardwareProvenanceOpen, setShortcutsOpen, setAboutOpen
  };
  // 命令面板（Ctrl/Cmd+K，2026-10-25）：把散在顶栏菜单、工具箱和按钮上的入口收成一个键盘入口。
  const paletteTargets = useMemo<PaletteCommand[]>(() => buildPaletteTargets(focusMode), [focusMode]);
  const runCommand = (key: string) => runPaletteCommand(key, {
    ...openers, run, validate, save,
    focusMode,
    fitView: () => void fitView({ padding: CANVAS_FIT_PADDING, duration: 180 }),
    toggleFocusMode, toggleTheme, toggleLang
  });
  // 顶栏二级入口：高频动作（校验 / 保存 / 运行）常驻，其余按领域归入四个下拉菜单。
  // 动作名带上目标节点，让用户始终清楚“当前操作作用于谁”。
  const selectedLabel = selectedId ? (nodeLabels[selectedId] ?? selectedId) : '';
  const targetSuffix = selectedLabel ? `：${selectedLabel}` : '';
  const debugMenuItems = buildDebugMenuItems({
    debugSession, statusText: debugStatus.text, anyBusy, selectedId, selectedNode, targetSuffix,
    allowSideEffects, hasSideEffectNodes, hasModuleCalls, breakpointCount: breakpoints.length, compileBusy
  }, {
    toggleBreakpoint: graph.toggleBreakpoint, continueDebugSession, runSelectedNodeInSession, endDebugSession,
    runNode, runFromHere, setAllowSideEffects, startDebugSession, inspectDsl
  });
  const contextNode = nodeContextMenu ? nodes.find((node) => node.id === nodeContextMenu.nodeId) : undefined;
  const contextNodeLabel = contextNode ? (nodeLabels[contextNode.id] ?? contextNode.id) : '';
  const contextTargetSuffix = contextNodeLabel ? `：${contextNodeLabel}` : '';
  const nodeContextMenuItems = buildNodeContextMenuItems(
    { contextNode, contextTargetSuffix, anyBusy, debugSession, nodeContextMenu },
    { openNodeConfiguration, runNode, runFromHere, toggleBreakpoint: graph.toggleBreakpoint, copySelection: graph.copySelection, deleteElements: graph.deleteElements }
  );
  const deviceMenuItems = buildDeviceMenuItems(openers);
  const productionMenuItems = buildProductionMenuItems(openers);
  const toolMenuItems = buildToolMenuItems({
    catalogSource, catalogCount: catalog.length, catalogSchemaVersion, loadedPluginCount: plugins.filter((p) => p?.loaded).length,
    history, themeMode, undo, redo, toggleTheme, openWorkflowPicker
  }, openers);
  const moreMenu = buildMoreMenu(
    { showTemplates, openWorkflowPicker, applyLayoutPreset, layoutPreset: layout.preset },
    debugMenuItems, deviceMenuItems, productionMenuItems, toolMenuItems
  );


  // Ctrl/Cmd+K 是全局入口：面板打开、输入框聚焦时也要能用，所以不放进画布快捷键（那里会被 overlayOpen 挡住）。
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      if (event.key.toLowerCase() !== 'k') return;
      event.preventDefault();
      setCommandOpen((open) => !open);
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, []);

  // 切换示例 / 加载会整块替换流程图，存在未保存修改时先让用户选择
  const guardUnsaved = (label: string, run: () => void) => { void workflowChanges.request(label, run); };


  // 结果应用（applyResult）、运行/调试执行（run / runNode / runFromHere / 调试会话）
  // 与图像资产加载已迁至独立模块 useRunController / useRunImages；本组件只保留装配与输入派生。





  // 快捷键处理器见 ./useEditorShortcuts（巨型组件拆分，2026-10-23）：
  // Ctrl/Cmd+S 保存、Ctrl/Cmd+F 查找、Ctrl/Cmd+Z 撤销、Ctrl/Cmd+Shift+Z（或 Ctrl+Y）重做、
  // Ctrl/Cmd+C / V 复制粘贴选中集合、Delete/Backspace 删除选中的节点或连线。
  //
  // 2026-10-25：任一功能面板/弹窗打开时，画布快捷键整体让位——此前在追溯/配方面板里按 Delete
  // 会删掉画布上选中的节点。打开状态在这里显式汇总：antd 关闭弹窗后 DOM 仍残留，
  // 用 document.querySelector 判断会让快捷键在第一次打开过面板之后永久失效。
  const overlayOpen = Boolean(
    pendingGuard || templateOpen || imagePreviewOpen || workflowPickerOpen || pluginsOpen || camerasOpen
    || robotsOpen || devicesOpen || jobsOpen || tracesOpen || validationOpen || modelPackagesOpen || modelAssetsOpen
    || tuningOpen || modulesOpen || calibrationOpen || framesOpen || productionOpen || diagnosticsOpen
    || hardwareProvenanceOpen || aboutOpen || shortcutsOpen
  );
  useEditorShortcuts({
    nodeConfigurationId, overlayOpen, save, undo, redo, copySelection: graph.copySelection, deleteElements: graph.deleteElements, pushHistory,
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

  const canvas: EditorCanvasAreaProps = {
    layout, applyLayoutPreset, toggleFocusMode, focusMode, imagePreviewOpen, exitFocusMode, setImagePreviewOpen,
    compactNodes, setCompactNodes, selectedNode, selectedId, openNodeConfiguration, selectedNodeIds,
    alignSelection: graph.alignSelection, history, undo, redo, anyBusy, selectedEdgeIds,
    deleteElements: graph.deleteElements, searchInputRef, searchQuery, setSearchQuery, searchOpen, setSearchOpen,
    searchMatches, focusNode: graph.focusNode, fitView, nodes, edges, displayEdges, themeMode,
    isValidConnection: graph.isValidConnection, handleNodesChange: graph.handleNodesChange,
    handleEdgesChange: graph.handleEdgesChange, onConnect: graph.onConnect, setConnectionPicker,
    onConnectEnd: graph.onConnectEnd, setDragging, pushHistory, setSelectedId, flowAreaRef,
    onDrop: graph.onDrop, cancelLongPress, longPressRef, openNodeMenuAt, draftNotice, locale, restoreDraft,
    discardDraft, showTemplates, openWorkflowPicker, connectionPicker, connectionPickerRef, catalog,
    recentNodeTypes, addNode: graph.addNode, nodeContextMenu, setNodeContextMenu, nodeContextMenuItems,
    designerWorkflowId, nodeLabels, result, resultStale, running, debugBusy
  };

  const overlays: EditorOverlaysProps = {
    compiledDsl, closeDsl, workflowChanges, pendingGuard, guardingSave, commandOpen, setCommandOpen, catalog,
    paletteTargets, runPaletteCommand: runCommand, addNode: graph.addNode, templateOpen, setTemplateOpen,
    demoKey, guardUnsaved, applyDemo, openWorkflowPicker, workflowPickerOpen, setWorkflowPickerOpen,
    savedWorkflows, workflowListLoading, workflowListError, workflowToLoad, setWorkflowToLoad,
    loadSelectedSavedWorkflow, workflowImport, shortcutsOpen, setShortcutsOpen
  };

  const panels: EditorPanelsProps = {
    camerasOpen, setCamerasOpen, robotsOpen, setRobotsOpen, devicesOpen, setDevicesOpen,
    framesOpen, setFramesOpen, calibrationOpen, setCalibrationOpen, selectedNode, applyCalibrationParameters: graph.applyCalibrationParameters,
    jobsOpen, setJobsOpen, workflowPayload, workflowChanges, applyResult,
    tracesOpen, setTracesOpen, traceRunId, setTraceRunId, modelPackagesOpen, setModelPackagesOpen,
    modelAssetsOpen, setModelAssetsOpen, validationOpen, setValidationOpen, tuningOpen, setTuningOpen,
    catalog, selectedId, messageApi, modulesOpen, setModulesOpen, insertModule, upgradeSelectedModule,
    productionOpen, setProductionOpen, diagnosticsOpen, setDiagnosticsOpen, aboutOpen, setAboutOpen,
    hardwareProvenanceOpen, setHardwareProvenanceOpen, pluginsOpen, setPluginsOpen, refreshCatalog
  };

  return (
    <div
      className="app-shell"
      ref={shellRef}
      style={{
        '--vs-header-height': `${HEADER_HEIGHT}px`,
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
        run={{ running, debugBusy, onRun: run, onAbort: abortRun, startedAt: runStartedAt }}
        workflow={{ name: designerWorkflowName, id: designerWorkflowId, onRename: setDesignerWorkflowName, dirty, lastSavedAt }}
        validation={{ status: validationState.status, summary: validationState.summary, needsRevalidate, issueCount: validationIssues.length, busy: validationBusy, onValidate: validate }}
        catalogError={catalogError}
        save={{ busy: saving, onSave: save }}
      />

      <main className="workspace">
        <aside className="left-panel"><Toolbox catalog={catalog} recentTypes={recentNodeTypes} onAddNode={(type) => graph.addNode(type)} /></aside>
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

        <EditorCanvasArea {...canvas} />
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
        focusNode={graph.focusNode}
        onOpenTrace={(runId) => { setTraceRunId(runId); setTracesOpen(true); }}
        nodeLabels={nodeLabels}
        nodeTypeLabels={nodeTypeLabels}
      />

      <EditorOverlays {...overlays} />
      <EditorPanels {...panels} />
    </div>
  );
}

export default function App() {
  return <ReactFlowProvider><Editor /></ReactFlowProvider>;
}
