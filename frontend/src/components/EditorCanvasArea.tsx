/**
 * 编辑器中央画布区（巨型组件拆分，2026-11-13）：画布工具栏（查找 / 撤销 / 对齐 / 布局）、
 * React Flow 画布、草稿恢复提示、端口连接选点与节点右键菜单、图像预览。
 * 纯展示：状态与回调由 App 注入，行为与拆分前一致。
 */
import { useCallback, useEffect, type RefObject } from 'react';
import { Alert, Button, Dropdown, Input, Segmented, Switch, Tag, type InputRef, type MenuProps } from 'antd';
import {
  Background, Controls, ReactFlow, useReactFlow, type Connection, type Edge, type FitViewOptions,
  type IsValidConnection, type Node, type OnConnectEnd, type OnEdgesChange, type OnNodesChange
} from '@xyflow/react';
import Toolbox from './Toolbox';
import CanvasMiniMap from './CanvasMiniMap';
import WorkflowImagePreview from './WorkflowImagePreview';
import type { LayoutPreset, LayoutState } from '../layout';
import type { WorkflowDraft } from '../draft';
import type { GraphHistory } from '../history';
import type { NodeCatalogItem, RunResult } from '../types';
import { CANVAS_FIT_PADDING, nodeTypes, type ConnectionPicker, type NodeAttachment } from '../appEditorShared';
import type { NodeConfigurationTab } from './NodeConfigurationDialog';

export type EditorCanvasAreaProps = {
  layout: LayoutState;
  applyLayoutPreset: (preset: LayoutPreset) => void;
  toggleFocusMode: () => void;
  focusMode: boolean;
  imagePreviewOpen: boolean;
  exitFocusMode: (restoreSnapshot: boolean) => void;
  setImagePreviewOpen: (value: boolean | ((open: boolean) => boolean)) => void;
  compactNodes: boolean;
  setCompactNodes: (value: boolean) => void;
  selectedNode: Node | undefined;
  selectedId: string;
  openNodeConfiguration: (nodeId: string, tab?: NodeConfigurationTab) => void;
  selectedNodeIds: string[];
  alignSelection: (mode: 'left' | 'centerX' | 'right' | 'top' | 'centerY' | 'bottom') => void;
  history: GraphHistory;
  undo: () => void;
  redo: () => void;
  anyBusy: boolean;
  selectedEdgeIds: string[];
  deleteElements: (nodeIds: string[], edgeIds: string[]) => void;
  searchInputRef: RefObject<InputRef | null>;
  searchQuery: string;
  setSearchQuery: (value: string) => void;
  searchOpen: boolean;
  setSearchOpen: (value: boolean) => void;
  searchMatches: Node[];
  focusNode: (nodeId: string) => void;
  fitView: (options?: FitViewOptions) => Promise<boolean>;
  nodes: Node[];
  edges: Edge[];
  displayEdges: Edge[];
  themeMode: 'light' | 'dark';
  isValidConnection: IsValidConnection;
  handleNodesChange: OnNodesChange;
  handleEdgesChange: OnEdgesChange;
  onConnect: (connection: Connection) => void;
  setConnectionPicker: (value: ConnectionPicker | undefined) => void;
  onConnectEnd: OnConnectEnd;
  setDragging: (value: boolean) => void;
  pushHistory: (label: string, coalesceKey?: string) => void;
  setSelectedId: (value: string) => void;
  flowAreaRef: RefObject<HTMLDivElement | null>;
  onDrop: (event: React.DragEvent) => void;
  cancelLongPress: () => void;
  longPressRef: { current: { timer: number; startX: number; startY: number } | undefined };
  openNodeMenuAt: (nodeId: string, clientX: number, clientY: number) => void;
  draftNotice: WorkflowDraft | undefined;
  locale: string;
  restoreDraft: () => void;
  discardDraft: () => void;
  showTemplates: () => void;
  openWorkflowPicker: () => Promise<void>;
  connectionPicker: ConnectionPicker | undefined;
  connectionPickerRef: RefObject<HTMLDivElement | null>;
  catalog: NodeCatalogItem[];
  recentNodeTypes: string[];
  addNode: (typeKey: string, position?: { x: number; y: number }, attachment?: NodeAttachment) => void;
  nodeContextMenu: { x: number; y: number; nodeId: string } | undefined;
  setNodeContextMenu: (value: { x: number; y: number; nodeId: string } | undefined) => void;
  nodeContextMenuItems: MenuProps['items'];
  designerWorkflowId: string;
  nodeLabels: Record<string, string>;
  result: RunResult | undefined;
  resultStale: boolean;
  running: boolean;
  debugBusy: boolean;
};

export default function EditorCanvasArea({
  layout, applyLayoutPreset, toggleFocusMode, focusMode, imagePreviewOpen, exitFocusMode, setImagePreviewOpen,
  compactNodes, setCompactNodes, selectedNode, selectedId, openNodeConfiguration, selectedNodeIds, alignSelection,
  history, undo, redo, anyBusy, selectedEdgeIds, deleteElements,
  searchInputRef, searchQuery, setSearchQuery, searchOpen, setSearchOpen, searchMatches, focusNode,
  fitView, nodes, edges, displayEdges, themeMode, isValidConnection, handleNodesChange, handleEdgesChange, onConnect,
  setConnectionPicker, onConnectEnd, setDragging, pushHistory, setSelectedId,
  flowAreaRef, onDrop, cancelLongPress, longPressRef, openNodeMenuAt,
  draftNotice, locale, restoreDraft, discardDraft, showTemplates, openWorkflowPicker,
  connectionPicker, connectionPickerRef, catalog, recentNodeTypes, addNode,
  nodeContextMenu, setNodeContextMenu, nodeContextMenuItems,
  designerWorkflowId, nodeLabels, result, resultStale, running, debugBusy
}: EditorCanvasAreaProps) {
  // 缩放分级（far/mid/near）：直接写画布容器 dataset，不经 React 状态——拖画布不会触发整树重渲；
  // CSS 按级别显隐端口文字/类型/摘要（Handle 恒在，连线落点不受影响）
  const { getViewport } = useReactFlow();
  const syncZoomLevel = useCallback((viewport: { zoom: number }) => {
    const area = flowAreaRef.current;
    if (!area) return;
    const level = viewport.zoom >= 1.05 ? 'near' : viewport.zoom >= 0.6 ? 'mid' : 'far';
    if (area.dataset.zoomLevel !== level) area.dataset.zoomLevel = level;
  }, []);
  useEffect(() => {
    syncZoomLevel(getViewport());
  }, [getViewport, syncZoomLevel]);

  return (
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
        {/* 触屏长按等价右键（2026-10-25）：React Flow 没有 onNodePointerDown，
            用容器上的指针事件做委托；移动超过 10px 或抬起即取消，避免与拖拽/平移打架。 */}
        <div className={`flow-area${compactNodes ? ' compact-nodes' : ''}`} style={{ position: 'relative' }} ref={flowAreaRef}
          onDrop={onDrop} onDragOver={(e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }}
          onPointerDown={(event) => {
            if (event.pointerType !== 'touch') return;
            const nodeElement = (event.target as HTMLElement).closest('.react-flow__node') as HTMLElement | null;
            const nodeId = nodeElement?.getAttribute('data-id');
            if (!nodeId) return;
            cancelLongPress();
            const { clientX, clientY } = event;
            const timer = window.setTimeout(() => { longPressRef.current = undefined; openNodeMenuAt(nodeId, clientX, clientY); }, 500);
            longPressRef.current = { timer, startX: clientX, startY: clientY };
          }}
          onPointerMove={(event) => {
            const pending = longPressRef.current;
            if (!pending) return;
            if (Math.hypot(event.clientX - pending.startX, event.clientY - pending.startY) > 10) cancelLongPress();
          }}
          onPointerUp={cancelLongPress} onPointerCancel={cancelLongPress} onPointerLeave={cancelLongPress}>
          {draftNotice && (
            <div className="draft-recovery" style={{ position: 'absolute', top: 10, left: '50%', transform: 'translateX(-50%)', zIndex: 50, width: 'min(640px, calc(100% - 24px))' }}>
              <Alert
                type="warning"
                showIcon
                title="检测到未保存的本地草稿"
                description={`${draftNotice.workflowName || draftNotice.workflowId || '未命名工作流'} · 保存于 ${new Date(draftNotice.savedAt).toLocaleString(locale)}`}
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
            onMoveStart={() => { setConnectionPicker(undefined); cancelLongPress(); }}
            onNodeDragStart={() => { cancelLongPress(); setDragging(true); pushHistory('移动节点'); }}
            onNodeDragStop={() => setDragging(false)}
            onSelectionDragStart={() => setDragging(true)}
            onSelectionDragStop={() => setDragging(false)}
            onNodeClick={(_, node) => setSelectedId(node.id)}
            onNodeDoubleClick={(_, node) => openNodeConfiguration(node.id)}
            onMove={(_, viewport) => syncZoomLevel(viewport)}
            // 右键即选中：菜单里的“运行/断点”动作与用户所见目标一致
            onNodeContextMenu={(event, node) => {
              event.preventDefault();
              cancelLongPress();
              openNodeMenuAt(node.id, event.clientX, event.clientY);
            }}
            fitView
            fitViewOptions={{ padding: CANVAS_FIT_PADDING }}
            // P2：右下角是画布唯一“空白处”——隐藏 React Flow 水印，避免与小地图按钮叠压
            //（@xyflow/react v12 免费支持 proOptions.hideAttribution）
            proOptions={{ hideAttribution: true }}
            // 性能（2026-10-26）：只渲染视口内的节点与连线。大流程（100+ 节点）平移到局部时，
            // 不再为视口外的节点维护 DOM 与 Konva/React 更新。
            onlyRenderVisibleElements
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
  );
}
