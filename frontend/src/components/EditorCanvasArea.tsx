/**
 * 编辑器中央画布区（巨型组件拆分，2026-11-13）：画布工具栏（查找 / 撤销 / 对齐 / 布局）、
 * React Flow 画布、草稿恢复提示、端口连接选点与节点右键菜单、图像预览。
 * 纯展示：状态与回调由 4 个域 Context 注入（见 ../editorContexts），行为与拆分前一致。
 */
import { lazy, memo, Suspense, useCallback, useEffect } from 'react';
import { Button, Dropdown, Tag } from 'antd';
import { Background, Controls, ReactFlow, useReactFlow } from '@xyflow/react';
import Toolbox from './Toolbox';
import CanvasMiniMap from './CanvasMiniMap';
import CanvasLayoutToolbar from './CanvasLayoutToolbar';
import CanvasEditingToolbar from './CanvasEditingToolbar';
import CanvasDraftRecovery from './CanvasDraftRecovery';
import { useEditorGraph, useEditorLayout, useEditorRun, useEditorSelection } from '../editorContexts';
import { useI18n } from '../i18n/provider';
import { isCanvasAnnotation } from '../canvasAnnotations';

// 首次引导只在未关闭时出现一次，不占首屏体积（主包 JS 预算已接近上限）
const OnboardingCard = lazy(() => import('./OnboardingCard'));
import WorkflowImagePreview from './WorkflowImagePreview';
import { CANVAS_FIT_PADDING, nodeTypes } from '../appEditorShared';


function EditorCanvasArea() {
  const { lang } = useI18n();
  const text = (zh: string, en: string) => (lang === 'en' ? en : zh);
  const {
    nodes, edges, displayEdges, isValidConnection, handleNodesChange, handleEdgesChange, onConnect, onReconnect,
    onReconnectStart, onReconnectEnd, onConnectEnd,
    setDragging, pushHistory, history, undo, redo, addNode, deleteElements, updateNodeParameter, addCanvasNote, createGroup, alignSelection, autoLayout, fitView,
    flowAreaRef, onDrop, cancelLongPress, longPressRef, catalog, recentNodeTypes, nodeLabels
  } = useEditorGraph();
  const {
    selectedNodeLabel, selectedNodeConfigurable, selectedId, setSelectedId, selectedNodeIds, selectedEdgeIds, openNodeConfiguration, focusNode,
    searchInputRef, searchQuery, setSearchQuery, searchOpen, setSearchOpen, searchMatches,
    connectionPicker, setConnectionPicker, connectionPickerRef, nodeContextMenu, setNodeContextMenu,
    nodeContextMenuItems, openNodeMenuAt
  } = useEditorSelection();
  const {
    focusMode, exitFocusMode, compactNodes, setCompactNodes,
    imagePreviewOpen, setImagePreviewOpen, themeMode
  } = useEditorLayout();
  const {
    result, resultStale, running, debugBusy, anyBusy,
    showTemplates, openWorkflowPicker, importWorkflowJson, designerWorkflowId, onboarding,
    debugSession, onContinueDebugSession, onRunSelectedNodeInSession, onEndDebugSession
  } = useEditorRun();
  // 缩放分级（far/mid/near）：直接写画布容器 dataset，不经 React 状态——拖画布不会触发整树重渲；
  // CSS 按级别显隐端口文字/类型/摘要（Handle 恒在，连线落点不受影响）
  const { getViewport } = useReactFlow();
  const groupableSelectionCount = nodes.filter((node) => selectedNodeIds.includes(node.id) && !node.parentId && !isCanvasAnnotation(node)).length;
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
        <CanvasEditingToolbar nodeCount={nodes.length} selectedId={selectedId} configurableSelected={selectedNodeConfigurable}
          selectedCount={selectedNodeIds.length} groupableSelectionCount={groupableSelectionCount} selectedEdgeIds={selectedEdgeIds}
          anyBusy={anyBusy} canUndo={history.canUndo} canRedo={history.canRedo} undoLabel={history.undoLabel} redoLabel={history.redoLabel}
          undo={undo} redo={redo} deleteElements={deleteElements} compactNodes={compactNodes} setCompactNodes={setCompactNodes}
          openNodeConfiguration={openNodeConfiguration} imagePreviewOpen={imagePreviewOpen} focusMode={focusMode}
          exitFocusMode={exitFocusMode} setImagePreviewOpen={setImagePreviewOpen} addCanvasNote={addCanvasNote}
          createGroup={createGroup} alignSelection={alignSelection} searchInputRef={searchInputRef} searchQuery={searchQuery}
          setSearchQuery={setSearchQuery} searchOpen={searchOpen} setSearchOpen={setSearchOpen} searchMatches={searchMatches} focusNode={focusNode} />
        <CanvasLayoutToolbar hasNodes={nodes.length > 0} anyBusy={anyBusy} autoLayout={autoLayout} fitView={fitView} />
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
          <CanvasDraftRecovery />

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
            <Button size="small" disabled={debugBusy || debugSession.status === 'faulted' || !selectedNodeConfigurable}
              aria-keyshortcuts="F10" title={selectedNodeConfigurable ? `${text('使用缓存输入运行', 'Run with cached inputs')}: ${selectedNodeLabel}` : text('先选择要运行的节点', 'Select a node to run')}
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

export default memo(EditorCanvasArea);
