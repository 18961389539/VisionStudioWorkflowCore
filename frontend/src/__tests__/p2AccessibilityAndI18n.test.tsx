import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createRef } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import EditorHeader from '../components/EditorHeader';
import EditorCanvasArea from '../components/EditorCanvasArea';
import EditorBottomDock from '../components/EditorBottomDock';
import ShortcutHelpModal from '../components/ShortcutHelpModal';
import ProductionPanel from '../components/ProductionPanel';
import { I18nProvider } from '../i18n/provider';
import { ThemeProvider } from '../theme';
import { buildPaletteTargets } from '../appEditorMenus';
import { defaultProductionConfig } from '../productionConfig';
import type { InputRef } from 'antd';

const focusNodeMock = vi.fn();
const searchMatchesMock = [
  { id: 'n1', position: { x: 0, y: 0 }, data: { label: '阈值分割' } },
  { id: 'n2', position: { x: 100, y: 0 }, data: { label: '找圆测量' } },
];

vi.mock('../useMonitorStream', () => ({
  useMonitorSnapshot: () => undefined,
}));

vi.mock('@xyflow/react', () => ({
  ReactFlow: () => <div data-testid="react-flow-mock" />,
  Background: () => null,
  Controls: () => null,
  useReactFlow: () => ({ getViewport: () => ({ x: 0, y: 0, zoom: 1 }) }),
}));

vi.mock('../editorContexts', () => ({
  useEditorGraph: () => ({
    nodes: searchMatchesMock,
    edges: [],
    displayEdges: [],
    isValidConnection: () => true,
    handleNodesChange: vi.fn(),
    handleEdgesChange: vi.fn(),
    onConnect: vi.fn(),
    onConnectEnd: vi.fn(),
    setDragging: vi.fn(),
    pushHistory: vi.fn(),
    history: { canUndo: false, canRedo: false },
    undo: vi.fn(),
    redo: vi.fn(),
    addNode: vi.fn(),
    deleteElements: vi.fn(),
    alignSelection: vi.fn(),
    fitView: vi.fn(async () => true),
    flowAreaRef: { current: null },
    onDrop: vi.fn(),
    cancelLongPress: vi.fn(),
    longPressRef: { current: undefined },
    catalog: [],
    recentNodeTypes: [],
    nodeLabels: {},
  }),
  useEditorSelection: () => ({
    selectedNodeLabel: undefined,
    selectedNodeConfigurable: false,
    selectedId: '',
    setSelectedId: vi.fn(),
    selectedNodeIds: [],
    selectedEdgeIds: [],
    openNodeConfiguration: vi.fn(),
    focusNode: focusNodeMock,
    searchInputRef: createRef<InputRef>(),
    searchQuery: '测',
    setSearchQuery: vi.fn(),
    searchOpen: true,
    setSearchOpen: vi.fn(),
    searchMatches: searchMatchesMock.map((node) => ({ id: node.id, label: node.data.label })),
    connectionPicker: undefined,
    setConnectionPicker: vi.fn(),
    connectionPickerRef: { current: null },
    nodeContextMenu: undefined,
    setNodeContextMenu: vi.fn(),
    nodeContextMenuItems: [],
    openNodeMenuAt: vi.fn(),
  }),
  useEditorLayout: () => ({
    layout: { left: 220, bottom: 220, leftCollapsed: false, bottomCollapsed: false, bottomMaximized: false, preset: 'edit' },
    applyLayoutPreset: vi.fn(),
    focusMode: false,
    toggleFocusMode: vi.fn(),
    exitFocusMode: vi.fn(),
    compactNodes: false,
    setCompactNodes: vi.fn(),
    imagePreviewOpen: false,
    setImagePreviewOpen: vi.fn(),
    themeMode: 'dark',
    locale: 'zh-CN',
  }),
  useEditorRun: () => ({
    result: undefined,
    resultStale: false,
    running: false,
    debugBusy: false,
    anyBusy: false,
    draftNotice: undefined,
    restoreDraft: vi.fn(),
    discardDraft: vi.fn(),
    showTemplates: vi.fn(),
    openWorkflowPicker: vi.fn(async () => undefined),
    designerWorkflowId: 'wf-1',
    onboarding: undefined,
  }),
}));

describe('P2 accessibility and i18n polish', () => {
  beforeEach(() => {
    focusNodeMock.mockReset();
    vi.stubGlobal('ResizeObserver', class {
      observe() { /* noop */ }
      unobserve() { /* noop */ }
      disconnect() { /* noop */ }
    });
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: vi.fn(() => ({
        matches: false, media: '', onchange: null,
        addListener: vi.fn(), removeListener: vi.fn(),
        addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
  });

  afterEach(() => {
    cleanup();
    window.localStorage.clear();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('translates EditorHeader controls including Open workflow and Abort/Elapsed when switching to English', () => {
    const noop = vi.fn();
    const onOpenCommand = vi.fn();
    render(
      <I18nProvider>
        <ThemeProvider>
          <EditorHeader
            menus={{ debug: [], device: [], production: [], tools: [], more: { items: [] } }}
            layoutPreset="edit"
            onApplyLayoutPreset={noop}
            onOpenCommand={onOpenCommand}
            templates={{ show: noop, openSaved: noop }}
            debug={{ inSession: false, statusText: 'idle' }}
            run={{ running: true, debugBusy: false, onRun: noop, onAbort: noop, startedAt: Date.now() }}
            workflow={{ name: 'Demo', id: 'demo', onRename: noop, dirty: false }}
            validation={{ status: 'idle', needsRevalidate: false, issueCount: 0, busy: false, onValidate: noop }}
            catalogError="offline"
            save={{ busy: false, onSave: noop }}
          />
        </ThemeProvider>
      </I18nProvider>
    );

    expect(screen.getByRole('button', { name: '打开流程' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '打开命令面板' }));
    expect(onOpenCommand).toHaveBeenCalledOnce();
    expect(screen.getByText('目录回退')).toBeTruthy();
    expect(screen.getByRole('button', { name: '中止' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '切换界面语言' }));

    expect(screen.getByRole('button', { name: 'Open workflow' })).toBeTruthy();
    expect(screen.getByText('Catalog fallback')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Abort' })).toBeTruthy();
    expect(screen.getByText(/Elapsed/)).toBeTruthy();
  });

  it('supports ARIA listbox and ArrowDown/ArrowUp/Enter keyboard navigation in canvas search', () => {
    render(<EditorCanvasArea />);

    const input = screen.getByRole('combobox');
    expect(input.getAttribute('aria-expanded')).toBe('true');
    const listbox = screen.getByRole('listbox', { name: '画布节点搜索结果' });
    expect(listbox).toBeTruthy();

    const options = screen.getAllByRole('option');
    expect(options).toHaveLength(2);
    expect(options[0].getAttribute('aria-selected')).toBe('true');
    expect(options[1].getAttribute('aria-selected')).toBe('false');

    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(screen.getAllByRole('option')[1].getAttribute('aria-selected')).toBe('true');
    expect(input.getAttribute('aria-activedescendant')).toBe('canvas-search-option-1');

    fireEvent.keyDown(input, { key: 'Enter' });
    expect(focusNodeMock).toHaveBeenCalledWith('n2');
  });

  it('associates ProductionPanel config labels with inputs via htmlFor', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/production/status')
        ? { state: 'Stopped', productionLocked: false, cycleCount: 0, okCount: 0, ngCount: 0, errorCount: 0, consecutiveFailures: 0, watchdogTrips: 0, lastDurationMs: 0 }
        : url.includes('/api/production/config')
          ? defaultProductionConfig
          : url.endsWith('/api/alarms/summary')
            ? { activeCount: 0, criticalCount: 0, pendingCount: 0, latestActiveMessage: null }
            : [];
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json', ETag: '"cfg-1"', 'X-Total-Count': '0' },
      });
    }));

    render(<ProductionPanel open onClose={() => undefined} embedded />);
    await waitFor(() => expect(screen.getByText('配置')).toBeTruthy());

    await act(async () => { fireEvent.click(screen.getByText('配置')); });
    await act(async () => { fireEvent.click(screen.getByText('同步健康保护')); });

    const windowInput = screen.getByLabelText('统计窗口') as HTMLInputElement;
    expect(windowInput.id).toBe('prod-sync-window');
    expect((screen.getByLabelText('触发故障连续检查数') as HTMLInputElement).id).toBe('prod-sync-unhealthy-fault');
  });

  it('displays live node run progress in EditorHeader and switches shell dock/shortcuts/palette to English', () => {
    window.localStorage.setItem('visionstudio.lang', 'en');
    const noop = vi.fn();

    render(
      <I18nProvider>
        <ThemeProvider>
          <EditorHeader
            menus={{ debug: [], device: [], production: [], tools: [], more: { items: [] } }}
            layoutPreset="edit"
            onApplyLayoutPreset={noop}
            onOpenCommand={noop}
            templates={{ show: noop, openSaved: noop }}
            debug={{ inSession: false, statusText: 'idle' }}
            run={{ running: true, debugBusy: false, onRun: noop, onAbort: noop, startedAt: Date.now(), progress: { completed: 2, total: 5 } }}
            workflow={{ name: 'Demo', id: 'demo', onRename: noop, dirty: false }}
            validation={{ status: 'idle', needsRevalidate: false, issueCount: 0, busy: false, onValidate: noop }}
            save={{ busy: false, onSave: noop }}
          />
          <EditorBottomDock
            layout={{ left: 220, bottom: 220, leftCollapsed: true, bottomCollapsed: true, bottomMaximized: false, bottomSizing: 'auto', preset: 'edit' }}
            updateLayout={noop}
            bottomMax={500}
            presetBottom={220}
            shownBottomBase={220}
            bottomTab="result"
            onBottomTabChange={noop}
            onPreviewHeight={noop}
            validationIssues={[]}
            locateIssue={noop}
            resultStale={false}
            focusNode={noop}
            nodeLabels={{}}
            nodeTypeLabels={{}}
          />
          <ShortcutHelpModal open onClose={noop} />
        </ThemeProvider>
      </I18nProvider>
    );

    expect(screen.getByText(/Elapsed.*2\/5/)).toBeTruthy();
    expect(screen.getByText('Results')).toBeTruthy();
    expect(screen.getByText('Issues')).toBeTruthy();
    expect(screen.getByText('Observability')).toBeTruthy();
    expect(screen.getByText('Click tab to expand')).toBeTruthy();
    expect(screen.getByText('Keyboard shortcuts')).toBeTruthy();
    expect(buildPaletteTargets(false, 'en').some((item) => item.key === 'auto-layout' && item.title === 'Auto-layout canvas')).toBe(true);
  });
});
