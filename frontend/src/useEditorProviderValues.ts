import { useLayoutEffect, useMemo, useRef } from 'react';
import type { EditorGraphValue, EditorLayoutValue, EditorRunValue, EditorSelectionValue } from './editorContexts';

export function useEditorProviderValues(input: {
  graph: EditorGraphValue; selection: EditorSelectionValue; layout: EditorLayoutValue; run: EditorRunValue;
}) {
  const { graph: g, selection: s, layout: l, run: r } = input;
  const graph = useMemo<EditorGraphValue>(() => ({
    nodes: g.nodes, edges: g.edges, displayEdges: g.displayEdges, isValidConnection: g.isValidConnection,
    handleNodesChange: g.handleNodesChange, handleEdgesChange: g.handleEdgesChange, onConnect: g.onConnect,
    onReconnect: g.onReconnect, onReconnectStart: g.onReconnectStart, onReconnectEnd: g.onReconnectEnd,
    onConnectEnd: g.onConnectEnd, setDragging: g.setDragging, pushHistory: g.pushHistory,
    history: g.history, historyVersion: g.historyVersion, undo: g.undo, redo: g.redo, addNode: g.addNode,
    deleteElements: g.deleteElements, updateNodeParameter: g.updateNodeParameter, updateCanvasAnnotation: g.updateCanvasAnnotation,
    addCanvasNote: g.addCanvasNote, createGroup: g.createGroup, alignSelection: g.alignSelection, autoLayout: g.autoLayout,
    fitView: g.fitView, flowAreaRef: g.flowAreaRef, onDrop: g.onDrop, cancelLongPress: g.cancelLongPress,
    longPressRef: g.longPressRef, catalog: g.catalog, recentNodeTypes: g.recentNodeTypes, nodeLabels: g.nodeLabels,
  }), [g.nodes, g.edges, g.displayEdges, g.isValidConnection, g.handleNodesChange, g.handleEdgesChange,
    g.onConnect, g.onReconnect, g.onReconnectStart, g.onReconnectEnd, g.onConnectEnd, g.setDragging, g.pushHistory,
    g.history, g.historyVersion, g.undo, g.redo, g.addNode, g.deleteElements, g.updateNodeParameter, g.updateCanvasAnnotation,
    g.addCanvasNote, g.createGroup, g.alignSelection, g.autoLayout, g.fitView, g.flowAreaRef, g.onDrop,
    g.cancelLongPress, g.longPressRef, g.catalog, g.recentNodeTypes, g.nodeLabels]);
  const selection = useMemo<EditorSelectionValue>(() => ({
    selectedNodeLabel: s.selectedNodeLabel, selectedNodeConfigurable: s.selectedNodeConfigurable,
    selectedId: s.selectedId, setSelectedId: s.setSelectedId,
    selectedNodeIds: s.selectedNodeIds, selectedEdgeIds: s.selectedEdgeIds, openNodeConfiguration: s.openNodeConfiguration,
    focusNode: s.focusNode, searchInputRef: s.searchInputRef, searchQuery: s.searchQuery, setSearchQuery: s.setSearchQuery,
    searchOpen: s.searchOpen, setSearchOpen: s.setSearchOpen, searchMatches: s.searchMatches,
    connectionPicker: s.connectionPicker, setConnectionPicker: s.setConnectionPicker, connectionPickerRef: s.connectionPickerRef,
    nodeContextMenu: s.nodeContextMenu, setNodeContextMenu: s.setNodeContextMenu,
    nodeContextMenuItems: s.nodeContextMenuItems, openNodeMenuAt: s.openNodeMenuAt,
  }), [s.selectedNodeLabel, s.selectedNodeConfigurable, s.selectedId, s.setSelectedId, s.selectedNodeIds, s.selectedEdgeIds, s.openNodeConfiguration,
    s.focusNode, s.searchInputRef, s.searchQuery, s.setSearchQuery, s.searchOpen, s.setSearchOpen, s.searchMatches,
    s.connectionPicker, s.setConnectionPicker, s.connectionPickerRef, s.nodeContextMenu, s.setNodeContextMenu,
    s.nodeContextMenuItems, s.openNodeMenuAt]);
  const layout = useMemo<EditorLayoutValue>(() => ({
    layout: l.layout, applyLayoutPreset: l.applyLayoutPreset, focusMode: l.focusMode, toggleFocusMode: l.toggleFocusMode,
    exitFocusMode: l.exitFocusMode, compactNodes: l.compactNodes, setCompactNodes: l.setCompactNodes,
    imagePreviewOpen: l.imagePreviewOpen, setImagePreviewOpen: l.setImagePreviewOpen, themeMode: l.themeMode, locale: l.locale,
  }), [l.layout, l.applyLayoutPreset, l.focusMode, l.toggleFocusMode, l.exitFocusMode, l.compactNodes,
    l.setCompactNodes, l.imagePreviewOpen, l.setImagePreviewOpen, l.themeMode, l.locale]);

  // Event handlers always read the last committed render, not an aborted concurrent render.
  const committedRun = useRef(r);
  useLayoutEffect(() => { committedRun.current = r; }, [r]);
  const actions = useMemo(() => ({
    restoreDraft: () => committedRun.current.restoreDraft(),
    discardDraft: () => committedRun.current.discardDraft(),
    showTemplates: () => committedRun.current.showTemplates(),
    openWorkflowPicker: () => committedRun.current.openWorkflowPicker(),
    importWorkflowJson: () => committedRun.current.importWorkflowJson?.(),
    continueDebug: () => committedRun.current.onContinueDebugSession?.(),
    runSelected: () => committedRun.current.onRunSelectedNodeInSession?.(),
    endDebug: () => committedRun.current.onEndDebugSession?.(),
    closeOnboarding: () => committedRun.current.onboarding?.onClose(),
    onboardingTemplates: () => committedRun.current.onboarding?.onOpenTemplates(),
    onboardingCameras: () => committedRun.current.onboarding?.onOpenCameras(),
    onboardingCommand: () => committedRun.current.onboarding?.onOpenCommand(),
    onboardingValidation: () => committedRun.current.onboarding?.onOpenValidation(),
    onboardingJobs: () => committedRun.current.onboarding?.onOpenJobs(),
  }), []);
  const hasOnboarding = Boolean(r.onboarding);
  const onboardingOpen = r.onboarding?.open;
  const onboarding = useMemo(() => hasOnboarding ? {
    open: Boolean(onboardingOpen), onClose: actions.closeOnboarding, onOpenTemplates: actions.onboardingTemplates,
    onOpenCameras: actions.onboardingCameras, onOpenCommand: actions.onboardingCommand, onOpenValidation: actions.onboardingValidation,
    onOpenJobs: actions.onboardingJobs,
  } : undefined, [hasOnboarding, onboardingOpen, actions]);
  const canImport = Boolean(r.importWorkflowJson);
  const canContinue = Boolean(r.onContinueDebugSession);
  const canRunSelected = Boolean(r.onRunSelectedNodeInSession);
  const canEnd = Boolean(r.onEndDebugSession);
  const run = useMemo<EditorRunValue>(() => ({
    result: r.result, resultStale: r.resultStale, running: r.running, debugBusy: r.debugBusy, anyBusy: r.anyBusy,
    debugSession: r.debugSession, onContinueDebugSession: canContinue ? actions.continueDebug : undefined,
    onRunSelectedNodeInSession: canRunSelected ? actions.runSelected : undefined, onEndDebugSession: canEnd ? actions.endDebug : undefined,
    draftNotice: r.draftNotice, restoreDraft: actions.restoreDraft, discardDraft: actions.discardDraft,
    showTemplates: actions.showTemplates, openWorkflowPicker: actions.openWorkflowPicker,
    importWorkflowJson: canImport ? actions.importWorkflowJson : undefined, designerWorkflowId: r.designerWorkflowId, onboarding,
  }), [r.result, r.resultStale, r.running, r.debugBusy, r.anyBusy, r.debugSession, r.draftNotice, r.designerWorkflowId,
    canContinue, canRunSelected, canEnd, canImport, actions, onboarding]);
  return { graph, selection, layout, run };
}
