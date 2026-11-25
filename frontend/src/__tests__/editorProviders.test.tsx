import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createRef, memo, startTransition, Suspense, useState } from 'react';
import { expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import { GraphHistory } from '../history';
import {
  EditorProviders, useEditorAnnotationAction, useEditorGraph, useEditorLayout, useEditorRun, useEditorSelection,
  type EditorGraphValue, type EditorLayoutValue, type EditorRunValue, type EditorSelectionValue,
} from '../editorContexts';
import { useEditorProviderValues } from '../useEditorProviderValues';
import { useStableArray } from '../useStableArray';

const noop = () => {};
const fitView = async () => true;
const openPicker = async () => {};
const history = new GraphHistory();
const nodes: Node[] = [{ id: 'a', position: { x: 0, y: 0 }, data: {} }];
const graph: EditorGraphValue = {
  nodes, edges: [], displayEdges: [], isValidConnection: () => true, handleNodesChange: noop, handleEdgesChange: noop,
  onConnect: noop, onConnectEnd: noop, setDragging: noop, pushHistory: noop, history, historyVersion: 0,
  undo: noop, redo: noop, addNode: noop, deleteElements: noop, updateNodeParameter: noop,
  updateCanvasAnnotation: noop, addCanvasNote: noop, createGroup: noop, alignSelection: noop,
  fitView, flowAreaRef: createRef(), onDrop: noop, cancelLongPress: noop, longPressRef: { current: undefined },
  catalog: [], recentNodeTypes: [], nodeLabels: {},
};
const selection: EditorSelectionValue = {
  selectedNodeLabel: 'a', selectedNodeConfigurable: true, selectedId: 'a', setSelectedId: noop, selectedNodeIds: ['a'], selectedEdgeIds: [],
  openNodeConfiguration: noop, focusNode: noop, searchInputRef: createRef(), searchQuery: 'a', setSearchQuery: noop,
  searchOpen: true, setSearchOpen: noop, searchMatches: [{ id: 'a', label: 'a' }], connectionPicker: undefined, setConnectionPicker: noop,
  connectionPickerRef: createRef(), nodeContextMenu: undefined, setNodeContextMenu: noop, nodeContextMenuItems: [],
  openNodeMenuAt: noop,
};
const layout: EditorLayoutValue = {
  layout: { left: 220, bottom: 220, leftCollapsed: false, bottomCollapsed: false, bottomMaximized: false, preset: 'edit', bottomSizing: 'auto' },
  applyLayoutPreset: noop, focusMode: false, toggleFocusMode: noop, exitFocusMode: noop,
  compactNodes: false, setCompactNodes: noop, imagePreviewOpen: false, setImagePreviewOpen: noop,
  themeMode: 'dark', locale: 'zh-CN',
};
const run: EditorRunValue = {
  result: undefined, resultStale: false, running: false, debugBusy: false, anyBusy: false, draftNotice: undefined,
  restoreDraft: noop, discardDraft: noop, showTemplates: noop, openWorkflowPicker: openPicker, designerWorkflowId: 'A',
};

it('isolates domain consumers for 120 graph frames and keeps latest committed actions', () => {
  const counts = { graph: 0, selection: 0, layout: 0, run: 0, annotation: 0 };
  const actions = vi.fn();
  const Graph = memo(function Graph() {
    counts.graph++;
    const value = useEditorGraph();
    return <span data-testid="graph">{value.nodes[0].position.x}:{value.historyVersion}</span>;
  });
  const Selection = memo(function Selection() {
    counts.selection++;
    return <span data-testid="selection">{useEditorSelection().selectedId}</span>;
  });
  const Layout = memo(function Layout() {
    counts.layout++;
    return <span data-testid="layout">{String(useEditorLayout().focusMode)}</span>;
  });
  const Run = memo(function Run() {
    counts.run++;
    const value = useEditorRun();
    return <button onClick={value.restoreDraft}>{String(value.running)}</button>;
  });
  const Annotation = memo(function Annotation() {
    counts.annotation++;
    const update = useEditorAnnotationAction();
    return <button onClick={() => update('note', { content: 'new' })}>note</button>;
  });
  function Harness({ frame, selectionValue = selection, layoutValue = layout, runValue = run, graphValue = graph }: {
    frame: number; selectionValue?: EditorSelectionValue; layoutValue?: EditorLayoutValue;
    runValue?: EditorRunValue; graphValue?: EditorGraphValue;
  }) {
    const selectedNodeIds = useStableArray([...selectionValue.selectedNodeIds], Object.is);
    const selectedEdgeIds = useStableArray([...selectionValue.selectedEdgeIds], Object.is);
    const searchMatches = useStableArray(selectionValue.searchMatches.map((item) => ({ ...item })),
      (left, right) => left.id === right.id && left.label === right.label);
    const values = useEditorProviderValues({
      graph: { ...graphValue, nodes: [{ ...nodes[0], position: { x: frame, y: 0 } }] },
      selection: { ...selectionValue, selectedNodeIds, selectedEdgeIds, searchMatches }, layout: { ...layoutValue },
      run: { ...runValue, restoreDraft: () => actions(frame), onboarding: {
        open: false, onClose: noop, onOpenTemplates: noop, onOpenCameras: noop, onOpenCommand: noop,
        onOpenValidation: noop, onOpenJobs: noop,
      } },
    });
    return <EditorProviders {...values}><Graph /><Selection /><Layout /><Run /><Annotation /></EditorProviders>;
  }
  const view = render(<Harness frame={0} />);
  for (let frame = 1; frame <= 120; frame++) view.rerender(<Harness frame={frame} />);
  expect(counts).toEqual({ graph: 121, selection: 1, layout: 1, run: 1, annotation: 1 });
  expect(screen.getByTestId('graph').textContent).toBe('120:0');
  fireEvent.click(screen.getByRole('button', { name: 'false' }));
  expect(actions).toHaveBeenLastCalledWith(120);
  view.rerender(<Harness frame={120} selectionValue={{ ...selection, selectedId: 'b' }} />);
  expect(screen.getByTestId('selection').textContent).toBe('b');
  expect(counts.selection).toBe(2);
  view.rerender(<Harness frame={120} selectionValue={{ ...selection, selectedId: 'b' }} layoutValue={{ ...layout, focusMode: true }} />);
  expect(screen.getByTestId('layout').textContent).toBe('true');
  expect(counts.layout).toBe(2);
  view.rerender(<Harness frame={120} selectionValue={{ ...selection, selectedId: 'b' }} layoutValue={{ ...layout, focusMode: true }}
    runValue={{ ...run, running: true }} graphValue={{ ...graph, historyVersion: 1 }} />);
  expect(screen.getByTestId('graph').textContent).toBe('120:1');
  expect(screen.getByRole('button', { name: 'true' })).toBeTruthy();
  expect(counts.run).toBe(2);
  expect(counts.annotation).toBe(1);
  const changedAction = vi.fn();
  view.rerender(<Harness frame={120} graphValue={{ ...graph, updateCanvasAnnotation: changedAction }} />);
  fireEvent.click(screen.getByRole('button', { name: 'note' }));
  expect(changedAction).toHaveBeenCalledWith('note', { content: 'new' });
  expect(counts.annotation).toBe(2);
});

it('does not expose callbacks from a suspended, uncommitted render', async () => {
  let release!: () => void;
  let ready = false;
  const pending = new Promise<void>((resolve) => { release = () => { ready = true; resolve(); }; });
  const called = vi.fn();
  function Block({ frame }: { frame: number }) {
    if (frame === 1 && !ready) throw pending;
    return <span data-testid="committed-frame">{frame}</span>;
  }
  function RunButton() {
    const { restoreDraft } = useEditorRun();
    return <button onClick={restoreDraft}>restore</button>;
  }
  function Harness() {
    const [frame, setFrame] = useState(0);
    const values = useEditorProviderValues({ graph, selection, layout, run: { ...run, restoreDraft: () => called(frame) } });
    return <>
      <button onClick={() => startTransition(() => setFrame(1))}>transition</button>
      <EditorProviders {...values}><RunButton /><Block frame={frame} /></EditorProviders>
    </>;
  }
  render(<Suspense fallback={<div>loading</div>}><Harness /></Suspense>);
  fireEvent.click(screen.getByRole('button', { name: 'transition' }));
  expect(screen.getByTestId('committed-frame').textContent).toBe('0');
  fireEvent.click(screen.getByRole('button', { name: 'restore' }));
  expect(called).toHaveBeenLastCalledWith(0);
  await act(async () => { release(); await pending; });
  await waitFor(() => expect(screen.getByTestId('committed-frame').textContent).toBe('1'));
  fireEvent.click(screen.getByRole('button', { name: 'restore' }));
  expect(called).toHaveBeenLastCalledWith(1);
});
