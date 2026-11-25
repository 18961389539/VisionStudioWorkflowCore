import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import WorkflowPickerModal from '../components/WorkflowPickerModal';
import SaveAsWorkflowModal from '../components/SaveAsWorkflowModal';
import { I18nProvider } from '../i18n/provider';
import { useWorkflowPicker } from '../useWorkflowPicker';
import { buildFileMenuItems, buildPaletteTargets, runPaletteCommand } from '../appEditorMenus';

const sampleWorkflows = [
  { id: 'line-a', name: '产线 A 检测', nodeCount: 5, edgeCount: 4, updatedAt: '2026-11-20T08:00:00Z' },
  { id: 'line-b', name: '产线 B 测量', nodeCount: 8, edgeCount: 7, updatedAt: '2026-11-21T09:30:00Z' },
];

beforeEach(() => {
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

describe('Workflow lifecycle usability (P0)', () => {
  it('filters workflows by name or id, loads on double click, and deletes after confirmation', async () => {
    const onLoad = vi.fn();
    const onDelete = vi.fn(async () => true);
    const onImportJson = vi.fn();

    render(
      <I18nProvider>
        <WorkflowPickerModal
          open
          onClose={() => undefined}
          workflows={sampleWorkflows}
          loading={false}
          selected="line-a"
          onSelect={() => undefined}
          onLoad={onLoad}
          onDelete={onDelete}
          onImportJson={onImportJson}
        />
      </I18nProvider>
    );

    const search = screen.getByLabelText('搜索已保存工作流');
    fireEvent.change(search, { target: { value: 'line-b' } });
    expect(screen.queryByText('产线 A 检测')).toBeNull();
    expect(screen.getByText('产线 B 测量')).toBeTruthy();

    fireEvent.doubleClick(screen.getByText('产线 B 测量'));
    expect(onLoad).toHaveBeenCalledWith('line-b');

    const deleteButtons = screen.getAllByRole('button', { name: /删\s*除/ });
    fireEvent.click(deleteButtons[0]);
    const confirmDelete = await screen.findByRole('button', { name: '确认删除' });
    fireEvent.click(confirmDelete);
    await waitFor(() => expect(onDelete).toHaveBeenCalledWith('line-b'));

    fireEvent.click(screen.getByRole('button', { name: '导入 JSON…' }));
    expect(onImportJson).toHaveBeenCalledOnce();
  });

  it('validates workflow ID in SaveAsWorkflowModal and submits valid new id/name', () => {
    const onConfirm = vi.fn();
    render(
      <SaveAsWorkflowModal
        open
        currentId="demo-linear"
        currentName="线性流程示例"
        onClose={() => undefined}
        onConfirm={onConfirm}
      />
    );

    const nameInput = screen.getByLabelText('工作流名称') as HTMLInputElement;
    const idInput = screen.getByLabelText(/工作流 ID/) as HTMLInputElement;
    const submitBtn = screen.getByRole('button', { name: '保存为新工作流' }) as HTMLButtonElement;

    fireEvent.change(idInput, { target: { value: 'bad.id' } });
    expect(submitBtn.disabled).toBe(true);
    expect(screen.getByText(/工作流 ID 只能包含字母、数字/)).toBeTruthy();

    fireEvent.change(nameInput, { target: { value: '产线 C 流程' } });
    fireEvent.change(idInput, { target: { value: 'line-c-flow' } });
    expect(submitBtn.disabled).toBe(false);

    fireEvent.click(submitBtn);
    expect(onConfirm).toHaveBeenCalledWith('line-c-flow', '产线 C 流程');
  });

  it('deletes saved workflow via DELETE /api/workflows/:id and notifies caller', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/workflows' && !init?.method) {
        return new Response(JSON.stringify(sampleWorkflows), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      if (url === '/api/workflows/line-a' && init?.method === 'DELETE') {
        return new Response(null, { status: 204 });
      }
      return new Response(JSON.stringify({ error: 'unexpected' }), { status: 500 });
    });
    vi.stubGlobal('fetch', fetchMock);

    const messageApi = { info: vi.fn(), warning: vi.fn(), error: vi.fn(), success: vi.fn() } as never;
    const onDeleted = vi.fn();
    const prepare = vi.fn();

    const { result } = renderHook(() => useWorkflowPicker({
      designerWorkflowId: 'line-a',
      messageApi,
      prepare,
      onDeleted,
    }));

    await act(async () => { await result.current.openWorkflowPicker(); });
    expect(result.current.savedWorkflows).toHaveLength(2);

    await act(async () => {
      const ok = await result.current.deleteSavedWorkflow('line-a');
      expect(ok).toBe(true);
    });
    expect(result.current.savedWorkflows.map((x) => x.id)).toEqual(['line-b']);
    expect(result.current.workflowToLoad).toBe('line-b');
    expect(onDeleted).toHaveBeenCalledWith('line-a');
  });

  it('exposes newBlank, saveAs, importJson and exportJson in File menu and Command Palette', () => {
    const noop = vi.fn();
    const newBlank = vi.fn();
    const saveAs = vi.fn();
    const importJson = vi.fn();
    const exportJson = vi.fn();

    const fileMenu = buildFileMenuItems({ newBlank, saveAs, importJson, exportJson });
    expect(fileMenu.map((item) => item && 'key' in item ? item.key : undefined)).toEqual([
      'file-new-blank', 'file-save-as', undefined, 'file-import-json', 'file-export-json'
    ]);

    const palette = buildPaletteTargets(false);
    expect(palette.some((item) => item.key === 'save-as')).toBe(true);
    expect(palette.some((item) => item.key === 'new-blank')).toBe(true);
    expect(palette.some((item) => item.key === 'import-json')).toBe(true);
    expect(palette.some((item) => item.key === 'export-json')).toBe(true);

    const openers = {
      setCalibrationOpen: noop, setTracesOpen: noop, setJobsOpen: noop, setValidationOpen: noop,
      setTuningOpen: noop, setModulesOpen: noop, setCamerasOpen: noop, setRobotsOpen: noop,
      setDevicesOpen: noop, setProductionOpen: noop, setSpcOpen: noop, setDiagnosticsOpen: noop,
      setStorageOpen: noop, setSecurityOpen: noop, setPluginsOpen: noop, setModelPackagesOpen: noop, setModelAssetsOpen: noop,
      setFramesOpen: noop, setHardwareProvenanceOpen: noop, setShortcutsOpen: noop, setAboutOpen: noop,
    };
    const ctx = {
      ...openers, run: noop, validate: noop, save: noop,
      newBlank, saveAs, importJson, exportJson,
      fitView: noop, toggleFocusMode: noop, toggleTheme: noop, toggleLang: noop, focusMode: false,
    };
    runPaletteCommand('new-blank', ctx);
    runPaletteCommand('save-as', ctx);
    runPaletteCommand('import-json', ctx);
    runPaletteCommand('export-json', ctx);
    expect(newBlank).toHaveBeenCalledOnce();
    expect(saveAs).toHaveBeenCalledOnce();
    expect(importJson).toHaveBeenCalledOnce();
    expect(exportJson).toHaveBeenCalledOnce();
  });
});
