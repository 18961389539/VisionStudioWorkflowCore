/**
 * 编辑器中央画布区（巨型组件拆分，2026-11-13）：画布工具栏（查找 / 撤销 / 对齐 / 布局）、
 * React Flow 画布、草稿恢复提示、端口连接选点与节点右键菜单、图像预览。
 * 纯展示：状态与回调由 4 个域 Context 注入（见 ../editorContexts），行为与拆分前一致。
 */
import { lazy, Suspense, useCallback, useEffect, useState } from 'react';
import { Alert, Button, Dropdown, Input, Segmented, Switch, Tag } from 'antd';
import { Background, Controls, ReactFlow, useReactFlow } from '@xyflow/react';
import Toolbox from './Toolbox';
import CanvasMiniMap from './CanvasMiniMap';
import { useEditorGraph, useEditorLayout, useEditorRun, useEditorSelection } from '../editorContexts';
import { useI18n } from '../i18n/provider';
import { isCanvasAnnotation } from '../canvasAnnotations';

// 首次引导只在未关闭时出现一次，不占首屏体积（主包 JS 预算已接近上限）
const OnboardingCard = lazy(() => import('./OnboardingCard'));
import WorkflowImagePreview from './WorkflowImagePreview';
import type { LayoutPreset } from '../layout';
import { CANVAS_FIT_PADDING, nodeTypes } from '../appEditorShared';


export default function EditorCanvasArea() {
  const { lang } = useI18n();
  const text = (zh: string, en: string) => (lang === 'en' ? en : zh);
  // 状态改由 4 个域 Context 提供（2026-11-25）：此前是 67 个平铺 props。
  // 解构保留原变量名，画布 JSX 与交互逻辑一行未改。
  const {
    nodes, edges, displayEdges, isValidConnection, handleNodesChange, handleEdgesChange, onConnect, onReconnect,
    onReconnectStart, onReconnectEnd, onConnectEnd,
    setDragging, pushHistory, history, undo, redo, addNode, deleteElements, updateNodeParameter, addCanvasNote, createGroup, alignSelection, autoLayout, fitView,
    flowAreaRef, onDrop, cancelLongPress, longPressRef, catalog, recentNodeTypes, nodeLabels
  } = useEditorGraph();
  const {
    selectedNode, selectedId, setSelectedId, selectedNodeIds, selectedEdgeIds, openNodeConfiguration, focusNode,
    searchInputRef, searchQuery, setSearchQuery, searchOpen, setSearchOpen, searchMatches,
    connectionPicker, setConnectionPicker, connectionPickerRef, nodeContextMenu, setNodeContextMenu,
    nodeContextMenuItems, openNodeMenuAt
  } = useEditorSelection();
  const configurableSelectedNode = selectedNode && !isCanvasAnnotation(selectedNode) ? selectedNode : undefined;
  const {
    layout, applyLayoutPreset, focusMode, toggleFocusMode, exitFocusMode, compactNodes, setCompactNodes,
    imagePreviewOpen, setImagePreviewOpen, themeMode, locale
  } = useEditorLayout();
  const {
    result, resultStale, running, debugBusy, anyBusy, draftNotice, restoreDraft, discardDraft,
    showTemplates, openWorkflowPicker, importWorkflowJson, designerWorkflowId, onboarding,
    debugSession, onContinueDebugSession, onRunSelectedNodeInSession, onEndDebugSession
  } = useEditorRun();
  // 缩放分级（far/mid/near）：直接写画布容器 dataset，不经 React 状态——拖画布不会触发整树重渲；
  // CSS 按级别显隐端口文字/类型/摘要（Handle 恒在，连线落点不受影响）
  const { getViewport } = useReactFlow();
  const [searchActiveIndex, setSearchActiveIndex] = useState(0);
  const visibleSearchMatches = searchMatches.slice(0, 8);
  const groupableSelectionCount = nodes.filter((node) => selectedNodeIds.includes(node.id) && !node.parentId && !isCanvasAnnotation(node)).length;
  const clampedSearchIndex = visibleSearchMatches.length > 0
    ? Math.min(searchActiveIndex, visibleSearchMatches.length - 1)
    : -1;
  const syncZoomLevel = useCallback((viewport: { zoom: number }) => {
    const area = flowAreaRef.current;
    if (!area) return;
    const level = viewport.zoom >= 1.05 ? 'near' : viewport.zoom >= 0.6 ? 'mid' : 'far';
    if (area.dataset.zoomLevel !== level) area.dataset.zoomLevel = level;
  }, [flowAreaRef]);
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
              role="combobox"
              aria-autocomplete="list"
              aria-expanded={searchOpen && searchQuery.trim() !== ''}
              aria-controls="canvas-search-listbox"
              aria-activedescendant={searchOpen && searchQuery.trim() !== '' && clampedSearchIndex >= 0
                ? `canvas-search-option-${clampedSearchIndex}`
                : undefined}
              className="canvas-search-input"
              placeholder={text('画布中查找（Ctrl+F）', 'Find on canvas (Ctrl+F)')}
              value={searchQuery}
              onChange={(event) => { setSearchQuery(event.target.value); setSearchActiveIndex(0); setSearchOpen(true); }}
              onFocus={() => setSearchOpen(true)}
              onBlur={() => setSearchOpen(false)}
              onKeyDown={(event) => {
                if (event.key === 'ArrowDown') {
                  event.preventDefault();
                  setSearchOpen(true);
                  if (visibleSearchMatches.length > 0) {
                    setSearchActiveIndex((index) => (index + 1) % visibleSearchMatches.length);
                  }
                  return;
                }
                if (event.key === 'ArrowUp') {
                  event.preventDefault();
                  setSearchOpen(true);
                  if (visibleSearchMatches.length > 0) {
                    setSearchActiveIndex((index) => (index - 1 + visibleSearchMatches.length) % visibleSearchMatches.length);
                  }
                  return;
                }
                if (event.key === 'Enter' && visibleSearchMatches.length > 0) {
                  const target = visibleSearchMatches[Math.max(0, clampedSearchIndex)] ?? visibleSearchMatches[0];
                  focusNode(target.id);
                  setSearchOpen(false);
                }
                if (event.key === 'Escape') {
                  setSearchQuery('');
                  setSearchActiveIndex(0);
                  setSearchOpen(false);
                }
              }}
            />
            {searchOpen && searchQuery.trim() !== '' && (
              // 用 mousedown 阻止默认行为，点结果时输入框不失焦，onClick 才能拿到这一次点击
              <div id="canvas-search-listbox" role="listbox" aria-label="画布节点搜索结果" className="canvas-search-results" onMouseDown={(event) => event.preventDefault()}>
                <div className="canvas-search-count">
                  命中 {searchMatches.length} / {nodes.length}{searchMatches.length > 8 ? '（仅显示前 8 条）' : ''}
                </div>
                {visibleSearchMatches.map((node, index) => (
                  <button
                    key={node.id}
                    id={`canvas-search-option-${index}`}
                    type="button"
                    role="option"
                    aria-selected={index === clampedSearchIndex}
                    className={`canvas-search-item${index === clampedSearchIndex ? ' is-active' : ''}`}
                    title="定位并选中该节点"
                    onMouseEnter={() => setSearchActiveIndex(index)}
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
            title={`${text('撤销', 'Undo')}${history.undoLabel ? ` · ${history.undoLabel}` : ''}（Ctrl+Z）`} aria-label={text('撤销', 'Undo')} onClick={undo}>↶</Button>
          <Button size="small" className="canvas-history-button" disabled={!history.canRedo || anyBusy}
            title={`${text('重做', 'Redo')}${history.redoLabel ? ` · ${history.redoLabel}` : ''}（Ctrl+Shift+Z）`} aria-label={text('重做', 'Redo')} onClick={redo}>↷</Button>
          {selectedEdgeIds.length > 0 && <Button size="small" danger disabled={anyBusy}
            aria-label={text(`删除选中连线 ${selectedEdgeIds.length} 条`, `Delete ${selectedEdgeIds.length} selected edges`)}
            title={text('删除选中的连线（Delete / Backspace）', 'Delete selected edges (Delete / Backspace)')}
            onClick={() => deleteElements([], selectedEdgeIds)}>{text(`删除连线 · ${selectedEdgeIds.length}`, `Delete edges · ${selectedEdgeIds.length}`)}</Button>}
          {selectedEdgeIds.length === 0 && <span className="canvas-edge-delete-hint" title={text('单击画布中的连线将其选中', 'Click an edge on canvas to select')}>{text('连线：选中后按 Del 删除', 'Edge: select & press Del')}</span>}
          <span className="canvas-toolbar-divider" />
          <label className="canvas-compact-toggle" title={text('紧凑模式：压缩节点标题与端口间距，适合多端口的大型流程', 'Compact mode: tighten node header & port spacing')}>
            <Switch size="small" checked={compactNodes} onChange={setCompactNodes} />
            <span>{text('紧凑', 'Compact')}</span>
          </label>
          <Button size="small" disabled={!configurableSelectedNode} title={text('打开所选节点的参数、图像和详细说明（也可双击节点）', 'Configure selected node (or double-click)')} onClick={() => openNodeConfiguration(selectedId)}>{text('节点配置', 'Configure')}</Button>
          <Button size="small" type={imagePreviewOpen && !focusMode ? 'primary' : 'default'} aria-pressed={imagePreviewOpen && !focusMode} onClick={() => { if (focusMode) exitFocusMode(true); setImagePreviewOpen((open) => focusMode || !open); }}>{text('图像预览', 'Preview')}</Button>
          <Button size="small" disabled={anyBusy} title={text('添加只保存在本机画布中的流程说明', 'Add a canvas-only note')} onClick={addCanvasNote}>{text('便签', 'Note')}</Button>
          <Button size="small" disabled={anyBusy || groupableSelectionCount === 0}
            title={text('将选中的未分组节点收纳为可移动的画布分组', 'Group selected ungrouped nodes on the canvas')}
            onClick={createGroup}>{text('分组', 'Group')}</Button>
        </div>

        {selectedNodeIds.length >= 2 && (
          <div className="canvas-align-toolbar" role="toolbar" aria-label="多选对齐工具">
            <Tag style={{ marginInlineEnd: 0 }}>{text(`已选 ${selectedNodeIds.length}`, `${selectedNodeIds.length} selected`)}</Tag>
            <Button size="small" onClick={() => alignSelection('left')}>{text('左对齐', 'Left')}</Button>
            <Button size="small" onClick={() => alignSelection('centerX')}>{text('水平居中', 'Center X')}</Button>
            <Button size="small" onClick={() => alignSelection('right')}>{text('右对齐', 'Right')}</Button>
            <Button size="small" onClick={() => alignSelection('top')}>{text('顶对齐', 'Top')}</Button>
            <Button size="small" onClick={() => alignSelection('centerY')}>{text('垂直居中', 'Center Y')}</Button>
            <Button size="small" onClick={() => alignSelection('bottom')}>{text('底对齐', 'Bottom')}</Button>
          </div>
        )}

        {/* 布局切换与编辑工具同处独立工具栏，结果区折叠后仍然可达。 */}
        <div className="canvas-layout-toolbar">
          {autoLayout && (
            <Button size="small" disabled={nodes.length === 0 || anyBusy} onClick={autoLayout}
              title={text('按节点连线关系自动整理布局（可撤销）', 'Auto-arrange nodes by topology (undoable)')}>{text('自动排布', 'Auto layout')}</Button>
          )}
          <Button size="small" onClick={() => void fitView({ padding: CANVAS_FIT_PADDING, duration: 180 })}
            title={text('缩放并居中显示整个流程（Ctrl+0）', 'Fit workflow to view (Ctrl+0)')}>{text('适应画布', 'Fit view')}</Button>
          <Segmented
            size="small"
            value={layout.preset}
            // 切预设即退出专注：由 applyLayoutPreset 统一处理（丢弃专注快照，布局完全由预设接管，P2）
            onChange={(value) => { if (value !== 'custom') applyLayoutPreset(value as LayoutPreset); }}
            options={[
              { label: text('标准布局', 'Standard'), value: 'edit' },
              { label: text('宽画布', 'Wide'), value: 'image' },
              ...(layout.preset === 'custom' ? [{ label: text('自定义', 'Custom'), value: 'custom' }] : [])
            ]}
          />
          <Button
            size="small"
            type={focusMode ? 'primary' : 'default'}
            onClick={toggleFocusMode}
            title={focusMode ? text('退出专注：还原进入前的面板布局', 'Exit focus mode') : text('专注画布：收起工具箱与结果区', 'Focus canvas')}
          >
            {focusMode ? text('退出专注', 'Exit focus') : text('专注', 'Focus')}
          </Button>
        </div>
      </div>
      <div className={`editor-content${imagePreviewOpen && !focusMode ? ' has-preview' : ''}`}>
        {/* 触屏长按等价右键（2026-10-25）：React Flow 没有 onNodePointerDown，
            用容器上的指针事件做委托；移动超过 10px 或抬起即取消，避免与拖拽/平移打架。 */}
        <div className={`flow-area${compactNodes ? ' compact-nodes' : ''}`} style={{ position: 'relative' }} ref={flowAreaRef}
          onDrop={(event) => {
            flowAreaRef.current?.querySelectorAll('.edge-drop-target').forEach((edge) => edge.classList.remove('edge-drop-target'));
            onDrop(event);
          }}
          onDragOver={(event) => {
            event.preventDefault();
            event.dataTransfer.dropEffect = 'copy';
            const target = event.target instanceof Element ? event.target.closest('.react-flow__edge') : null;
            flowAreaRef.current?.querySelectorAll('.edge-drop-target').forEach((edge) => {
              if (edge !== target) edge.classList.remove('edge-drop-target');
            });
            target?.classList.add('edge-drop-target');
          }}
          onDragLeave={(event) => {
            if (!event.currentTarget.contains(event.relatedTarget as Node | null))
              flowAreaRef.current?.querySelectorAll('.edge-drop-target').forEach((edge) => edge.classList.remove('edge-drop-target'));
          }}
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
                title={text('检测到未保存的本地草稿', 'Unsaved local draft detected')}
                description={`${draftNotice.workflowName || draftNotice.workflowId || text('未命名工作流', 'Untitled workflow')} · ${text('保存于', 'Saved')} ${new Date(draftNotice.savedAt).toLocaleString(locale)}`}
                action={(
                  <div style={{ display: 'flex', gap: 8 }}>
                    <Button size="small" type="primary" onClick={restoreDraft}>{text('恢复草稿', 'Restore')}</Button>
                    <Button size="small" onClick={discardDraft}>{text('丢弃', 'Discard')}</Button>
                  </div>
                )}
              />
            </div>
          )}

          {/* 空画布引导（P1）：首屏/新流程时给出下一步动作 */}
          {nodes.length === 0 && (
            <div className="canvas-empty-state">
              <div className="canvas-empty-title">{text('从左侧工具箱拖入节点，搭建你的第一条视觉流程', 'Drag nodes from the left toolbox to build your vision workflow')}</div>
              <div className="canvas-empty-actions">
                <Button size="small" type="primary" onClick={showTemplates}>{text('从模板新建', 'New from template')}</Button>
                <Button size="small" onClick={() => void openWorkflowPicker()}>{text('打开已有流程', 'Open workflow')}</Button>
                {importWorkflowJson && <Button size="small" onClick={importWorkflowJson}>{text('导入 JSON…', 'Import JSON…')}</Button>}
              </div>
            </div>
          )}

          {debugSession && <div className="debug-session-toolbar" role="toolbar" aria-label={text('断点调试控制', 'Breakpoint debugging controls')}>
            <Tag color={debugSession.status === 'faulted' ? 'red' : debugSession.status === 'halted' ? 'gold' : 'green'}>
              {debugSession.status === 'halted' ? text('已暂停', 'Paused')
                : debugSession.status === 'faulted' ? text('会话中断', 'Session faulted') : text('会话完成', 'Session completed')}
            </Tag>
            {debugSession.haltNodeId && <Button size="small" type="link" onClick={() => focusNode(debugSession.haltNodeId!)}>
              {text('暂停于', 'Paused at')} {nodeLabels[debugSession.haltNodeId] ?? debugSession.haltNodeId}
            </Button>}
            <Button size="small" disabled={debugBusy || debugSession.status !== 'halted'}
              aria-keyshortcuts="F5" onClick={onContinueDebugSession}>{text('继续', 'Continue')} <kbd>F5</kbd></Button>
            <Button size="small" disabled={debugBusy || debugSession.status === 'faulted' || !configurableSelectedNode}
              aria-keyshortcuts="F10" title={configurableSelectedNode ? `${text('使用缓存输入运行', 'Run with cached inputs')}: ${String(configurableSelectedNode.data.label ?? configurableSelectedNode.id)}` : text('先选择要运行的节点', 'Select a node to run')}
              onClick={onRunSelectedNodeInSession}>{text('运行选中节点', 'Run selected')} <kbd>F10</kbd></Button>
            <Button size="small" danger disabled={debugBusy} aria-keyshortcuts="Shift+F5"
              onClick={onEndDebugSession}>{text('结束会话', 'End session')} <kbd>Shift+F5</kbd></Button>
          </div>}

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
            onReconnect={onReconnect}
            onReconnectStart={onReconnectStart}
            onReconnectEnd={onReconnectEnd}
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
            onNodeDoubleClick={(_, node) => { if (!isCanvasAnnotation(node)) openNodeConfiguration(node.id); }}
            onMove={(_, viewport) => syncZoomLevel(viewport)}
            // 右键即选中：菜单里的“运行/断点”动作与用户所见目标一致
            onNodeContextMenu={(event, node) => {
              event.preventDefault();
              cancelLongPress();
              if (!isCanvasAnnotation(node)) openNodeMenuAt(node.id, event.clientX, event.clientY);
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

          {/* 首次上手：画布已有节点时给一条五步主线；空画布交给更聚焦的空态引导 */}
          {onboarding?.open && nodes.length > 0 && <Suspense fallback={null}>
            <OnboardingCard
              onClose={onboarding.onClose}
              onOpenTemplates={onboarding.onOpenTemplates}
              onOpenCameras={onboarding.onOpenCameras}
              onOpenCommand={onboarding.onOpenCommand}
              onOpenValidation={onboarding.onOpenValidation}
              onOpenJobs={onboarding.onOpenJobs} />
          </Suspense>}

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
          nodeLabels={nodeLabels} catalog={catalog} result={result} stale={resultStale} running={running || debugBusy}
          onParameterChange={updateNodeParameter} onOpenConfiguration={openNodeConfiguration}
          onClose={() => setImagePreviewOpen(false)} />}
      </div>
    </section>
  );
}
