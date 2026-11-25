/**
 * 编辑器状态按刷新频率分为四域：
 * - Graph     ：图数据与编辑动作（高频；拖动、连线、增删）
 * - Selection ：选中与定位（高频；点击、搜索）
 * - Layout    ：布局与外观（低频；预设、专注、紧凑、主题）
 * - Run       ：运行与工作流生命周期（低频；运行、草稿、结果）
 * 批注动作另有窄订阅，避免批注节点因其他节点拖拽而订阅整个 Graph。
 */
import { createContext, useContext, type MouseEvent as ReactMouseEvent, type ReactNode, type RefObject, type TouchEvent as ReactTouchEvent } from 'react';
import type { Connection, Edge, FitViewOptions, IsValidConnection, Node, OnConnectEnd, OnEdgesChange, OnNodesChange, OnReconnect } from '@xyflow/react';
import type { InputRef, MenuProps } from 'antd';
import type { LayoutPreset, LayoutState } from './layout';
import type { WorkflowDraft } from './draft';
import type { GraphHistory } from './history';
import type { NodeCatalogItem, RunResult } from './types';
import type { ConnectionPicker, NodeAttachment } from './appEditorShared';
import type { NodeConfigurationTab } from './components/NodeConfigurationDialog';

export type EditorGraphValue = {
  nodes: Node[];
  edges: Edge[];
  displayEdges: Edge[];
  isValidConnection: IsValidConnection;
  handleNodesChange: OnNodesChange;
  handleEdgesChange: OnEdgesChange;
  onConnect: (connection: Connection) => void;
  onReconnect?: OnReconnect<Edge>;
  onReconnectStart?: (event: ReactMouseEvent<Element> | ReactTouchEvent<Element>, edge: Edge) => void;
  onReconnectEnd?: () => void;
  onConnectEnd: OnConnectEnd;
  setDragging: (value: boolean) => void;
  pushHistory: (label: string, coalesceKey?: string) => void;
  history: GraphHistory;
  historyVersion: number;
  undo: () => void;
  redo: () => void;
  addNode: (typeKey: string, position?: { x: number; y: number }, attachment?: NodeAttachment) => void;
  deleteElements: (nodeIds: string[], edgeIds: string[], bridge?: boolean) => void;
  updateNodeParameter: (nodeId: string, key: string, value: unknown) => void;
  updateCanvasAnnotation: (nodeId: string, patch: Record<string, unknown>) => void;
  addCanvasNote: () => void;
  createGroup: () => void;
  alignSelection: (mode: 'left' | 'centerX' | 'right' | 'top' | 'centerY' | 'bottom') => void;
  autoLayout?: () => void;
  fitView: (options?: FitViewOptions) => Promise<boolean>;
  flowAreaRef: RefObject<HTMLDivElement | null>;
  onDrop: (event: React.DragEvent) => void;
  cancelLongPress: () => void;
  longPressRef: { current: { timer: number; startX: number; startY: number } | undefined };
  catalog: NodeCatalogItem[];
  recentNodeTypes: string[];
  nodeLabels: Record<string, string>;
};

export type EditorSelectionValue = {
  selectedNodeLabel: string | undefined;
  selectedNodeConfigurable: boolean;
  selectedId: string;
  setSelectedId: (value: string) => void;
  selectedNodeIds: string[];
  selectedEdgeIds: string[];
  openNodeConfiguration: (nodeId: string, tab?: NodeConfigurationTab) => void;
  focusNode: (nodeId: string) => void;
  searchInputRef: RefObject<InputRef | null>;
  searchQuery: string;
  setSearchQuery: (value: string) => void;
  searchOpen: boolean;
  setSearchOpen: (value: boolean) => void;
  searchMatches: { id: string; label: string }[];
  connectionPicker: ConnectionPicker | undefined;
  setConnectionPicker: (value: ConnectionPicker | undefined) => void;
  connectionPickerRef: RefObject<HTMLDivElement | null>;
  nodeContextMenu: { x: number; y: number; nodeId: string } | undefined;
  setNodeContextMenu: (value: { x: number; y: number; nodeId: string } | undefined) => void;
  nodeContextMenuItems: MenuProps['items'];
  openNodeMenuAt: (nodeId: string, clientX: number, clientY: number) => void;
};

export type EditorLayoutValue = {
  layout: LayoutState;
  applyLayoutPreset: (preset: LayoutPreset) => void;
  focusMode: boolean;
  toggleFocusMode: () => void;
  exitFocusMode: (restoreSnapshot: boolean) => void;
  compactNodes: boolean;
  setCompactNodes: (value: boolean) => void;
  imagePreviewOpen: boolean;
  setImagePreviewOpen: (value: boolean | ((open: boolean) => boolean)) => void;
  themeMode: 'light' | 'dark';
  locale: string;
};

export type EditorRunValue = {
  result: RunResult | undefined;
  resultStale: boolean;
  running: boolean;
  debugBusy: boolean;
  anyBusy: boolean;
  debugSession?: { status: 'halted' | 'completed' | 'faulted'; haltNodeId?: string; error?: string };
  onContinueDebugSession?: () => void;
  onRunSelectedNodeInSession?: () => void;
  onEndDebugSession?: () => void;
  draftNotice: WorkflowDraft | undefined;
  restoreDraft: () => void;
  discardDraft: () => void;
  showTemplates: () => void;
  openWorkflowPicker: () => Promise<void>;
  importWorkflowJson?: () => void;
  designerWorkflowId: string;
  /** 首次上手引导：仅在画布已有节点时显示——空画布已经有更聚焦的空态引导。 */
  onboarding?: {
    open: boolean;
    onClose: () => void;
    onOpenTemplates: () => void;
    onOpenCameras: () => void;
    onOpenCommand: () => void;
    onOpenValidation: () => void;
    onOpenJobs: () => void;
  };
};

const EditorGraphContext = createContext<EditorGraphValue | null>(null);
const EditorSelectionContext = createContext<EditorSelectionValue | null>(null);
const EditorLayoutContext = createContext<EditorLayoutValue | null>(null);
const EditorRunContext = createContext<EditorRunValue | null>(null);
const AnnotationActionContext = createContext<EditorGraphValue['updateCanvasAnnotation'] | null>(null);

function useRequired<T>(value: T | null, hookName: string): T {
  if (value === null) {
    throw new Error(`${hookName} 必须在 <EditorProviders> 内使用：缺少编辑器状态容器会让画布静默失效，因此直接报错。`);
  }
  return value;
}

export const useEditorGraph = (): EditorGraphValue =>
  useRequired(useContext(EditorGraphContext), 'useEditorGraph');

export const useEditorSelection = (): EditorSelectionValue =>
  useRequired(useContext(EditorSelectionContext), 'useEditorSelection');

export const useEditorLayout = (): EditorLayoutValue =>
  useRequired(useContext(EditorLayoutContext), 'useEditorLayout');

export const useEditorRun = (): EditorRunValue =>
  useRequired(useContext(EditorRunContext), 'useEditorRun');

export const useEditorAnnotationAction = (): EditorGraphValue['updateCanvasAnnotation'] =>
  useRequired(useContext(AnnotationActionContext), 'useEditorAnnotationAction');

export function EditorProviders({ graph, selection, layout, run, children }: {
  graph: EditorGraphValue;
  selection: EditorSelectionValue;
  layout: EditorLayoutValue;
  run: EditorRunValue;
  children: ReactNode;
}) {
  return (
    <EditorGraphContext.Provider value={graph}>
      <EditorSelectionContext.Provider value={selection}>
        <EditorLayoutContext.Provider value={layout}>
          <EditorRunContext.Provider value={run}>
            <AnnotationActionContext.Provider value={graph.updateCanvasAnnotation}>{children}</AnnotationActionContext.Provider>
          </EditorRunContext.Provider>
        </EditorLayoutContext.Provider>
      </EditorSelectionContext.Provider>
    </EditorGraphContext.Provider>
  );
}
