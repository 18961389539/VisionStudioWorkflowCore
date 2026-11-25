import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import PluginPackagePanel from '../components/PluginPackagePanel';
import ProductionPanel from '../components/ProductionPanel';
import { defaultProductionConfig } from '../productionConfig';
import { buildPaletteTargets, buildToolMenuItems, runPaletteCommand } from '../appEditorMenus';

const securityMock = vi.fn();

vi.mock('../security', () => ({
  useSecurityAccount: () => securityMock(),
}));

vi.mock('../useMonitorStream', () => ({
  useMonitorSnapshot: () => undefined,
}));

function okJson(body: unknown, headers?: Record<string, string>) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json', 'X-Total-Count': '0', ETag: '"cfg-1"', ...headers },
  });
}

describe('Engineer experience improvements', () => {
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
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('disables admin-only plugin package mutations for Engineer while keeping them enabled for Administrator', async () => {
    const packageRow = {
      id: 'sample.plugin', name: 'Sample Plugin', version: '1.0.0', active: false, pending: false,
      restartRequired: false, publisherSubject: 'CN=Vendor', publisherTrusted: true, packageSha256: 'abcdef1234567890',
    };
    const publisherRow = {
      thumbprint: 'THUMBPRINT123', subject: 'CN=Vendor', displayName: 'Vendor', notBefore: '2026-01-01', notAfter: '2028-01-01',
    };
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/plugins') return okJson([]);
      if (url === '/api/plugin-packages') return okJson([packageRow]);
      if (url === '/api/plugin-publishers') return okJson([publisherRow]);
      if (url === '/api/plugin-workers') return okJson([]);
      if (url === '/api/plugin-workers/performance') return okJson([]);
      if (url === '/api/plugin-benchmarks') return okJson([]);
      if (url === '/api/validation/datasets') return okJson([]);
      return okJson({});
    }));

    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'eng1', username: 'eng1', displayName: '工程师', role: 'Engineer' },
    });

    const { rerender } = render(<PluginPackagePanel open onClose={() => undefined} onCatalogRefresh={async () => undefined} />);
    await waitFor(() => expect(screen.getByText('Sample Plugin')).toBeTruthy());

    expect(screen.getByText('当前角色仅支持预检、进程池重启、性能分析与基准测试')).toBeTruthy();
    expect((screen.getByRole('button', { name: '安 装' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '选 择' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '信任发布者' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '选择证书' }) as HTMLButtonElement).disabled).toBe(true);
    const removeButtons = screen.getAllByRole('button', { name: '移 除' }) as HTMLButtonElement[];
    expect(removeButtons.length).toBe(2);
    expect(removeButtons.every((button) => button.disabled)).toBe(true);

    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'adm1', username: 'adm1', displayName: '管理员', role: 'Administrator' },
    });
    rerender(<PluginPackagePanel open onClose={() => undefined} onCatalogRefresh={async () => undefined} />);

    expect(screen.queryByText('当前角色仅支持预检、进程池重启、性能分析与基准测试')).toBeNull();
    expect((screen.getByRole('button', { name: '选 择' }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: '选择证书' }) as HTMLButtonElement).disabled).toBe(false);
    const adminRemoveButtons = screen.getAllByRole('button', { name: '移 除' }) as HTMLButtonElement[];
    expect(adminRemoveButtons.every((button) => !button.disabled)).toBe(true);
  });

  it('groups sync health guard inputs and applies presets cleanly for Engineer', async () => {
    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'eng1', username: 'eng1', displayName: '工程师', role: 'Engineer' },
    });
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/production/status')) {
        return okJson({
          state: 'Stopped', productionLocked: false, cycleCount: 0, okCount: 0, ngCount: 0,
          errorCount: 0, consecutiveFailures: 0, watchdogTrips: 0, lastDurationMs: 0,
        });
      }
      if (url.includes('/api/production/config')) return okJson(defaultProductionConfig);
      if (url.includes('/api/alarms/summary')) return okJson({ activeCount: 0, criticalCount: 0, pendingCount: 0, latestActiveMessage: null });
      if (url.includes('/api/jobs') || url.includes('/api/alarms')) return okJson([]);
      return okJson({});
    }));

    render(<ProductionPanel open onClose={() => undefined} embedded />);
    await waitFor(() => expect(screen.getByText('配置')).toBeTruthy());

    await act(async () => { fireEvent.click(screen.getByText('配置')); });
    await act(async () => { fireEvent.click(screen.getByText('同步健康保护')); });

    expect(screen.getByText('1. 采样窗口与检查频率')).toBeTruthy();
    expect(screen.getByText('2. 容错与帧丢失门限')).toBeTruthy();
    expect(screen.getByText('3. 故障判定与自动恢复')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '严苛产线' }));
    expect(screen.getAllByText('配置有未保存修改，保存后才可启动。').length).toBeGreaterThan(0);
    expect((screen.getByRole('button', { name: '保存配置' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('exposes StoragePanel in Tools menu and Command Palette', () => {
    const setStorageOpen = vi.fn();
    const noop = vi.fn();
    const openers = {
      setCalibrationOpen: noop, setTracesOpen: noop, setJobsOpen: noop, setValidationOpen: noop,
      setTuningOpen: noop, setModulesOpen: noop, setCamerasOpen: noop, setRobotsOpen: noop,
      setDevicesOpen: noop, setProductionOpen: noop, setSpcOpen: noop, setDiagnosticsOpen: noop,
      setStorageOpen, setSecurityOpen: noop, setPluginsOpen: noop, setModelPackagesOpen: noop, setModelAssetsOpen: noop,
      setFramesOpen: noop, setHardwareProvenanceOpen: noop, setShortcutsOpen: noop, setAboutOpen: noop,
    };

    const palette = buildPaletteTargets(false);
    expect(palette.some((item) => item.key === 'panel:storage' && item.title === '存储维护与备份')).toBe(true);

    runPaletteCommand('panel:storage', {
      ...openers,
      run: noop, validate: noop, save: noop, fitView: noop, toggleFocusMode: noop, toggleTheme: noop, toggleLang: noop,
      focusMode: false,
    });
    expect(setStorageOpen).toHaveBeenCalledWith(true);

    const tools = buildToolMenuItems({
      catalogSource: 'runtime', catalogCount: 10, catalogSchemaVersion: 1, loadedPluginCount: 0,
      history: { canUndo: false, canRedo: false }, themeMode: 'light',
      undo: noop, redo: noop, toggleTheme: noop, openWorkflowPicker: noop, reopenOnboarding: noop,
    }, openers);
    const storageItem = tools.find((item) => item && 'key' in item && item.key === 'storage');
    expect(storageItem).toBeTruthy();
  });
});
