import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import SecurityAdminPanel from '../components/SecurityAdminPanel';
import DiagnosticsPanel from '../components/DiagnosticsPanel';
import { buildPaletteTargets, buildToolMenuItems, runPaletteCommand } from '../appEditorMenus';

const securityMock = vi.fn();

vi.mock('../security', () => ({
  useSecurityAccount: () => securityMock(),
}));

vi.mock('@microsoft/signalr', () => {
  class FakeHubConnection {
    state = 'Disconnected';
    on() { /* noop */ }
    onreconnecting() { /* noop */ }
    onreconnected() { /* noop */ }
    onclose() { /* noop */ }
    start() { return Promise.resolve(); }
    stop() { return Promise.resolve(); }
  }
  return {
    HubConnectionBuilder: class {
      withUrl() { return this; }
      withAutomaticReconnect() { return this; }
      configureLogging() { return this; }
      build() { return new FakeHubConnection(); }
    },
    HubConnectionState: { Disconnected: 'Disconnected' },
    LogLevel: { Warning: 2 },
  };
});

function okJson(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

describe('Administrator experience', () => {
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

  it('blocks non-admin access cleanly without calling admin security endpoints', () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'eng1', username: 'eng1', displayName: '工程师', role: 'Engineer' },
    });

    render(<SecurityAdminPanel open onClose={() => undefined} />);
    expect(screen.getByText('需要管理员权限（Administrator）')).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('supports user unlock, service token issuance, and audit chain verification for Administrator', async () => {
    const calls: string[] = [];
    const users = [{
      id: 'u-op1', username: 'op1', displayName: '操作员1', role: 'Operator', enabled: true,
      createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-01T00:00:00Z',
      lastLoginAt: null, passwordChangedAt: '2026-11-01T00:00:00Z',
    }];
    const tokens = [{
      id: 't1', name: 'mes-line1', prefix: 'vst_AbCdEfGh', scopes: ['results:read'],
      createdAt: '2026-11-01T00:00:00Z', createdBy: 'admin', expiresAt: null, lastUsedAt: null, revokedAt: null,
    }];
    const audits = [{
      sequence: 1, at: '2026-11-25T00:00:00Z', userId: 'adm1', username: 'admin', role: 'Administrator',
      action: 'auth.login', resource: 'session', method: 'POST', path: '/api/auth/login',
      statusCode: 200, success: true, target: 'admin', correlationId: 'c1', clientIp: '127.0.0.1',
    }];

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push(`${init?.method ?? 'GET'} ${url}`);
      if (url === '/api/security/users') return okJson(users);
      if (url === '/api/security/users/u-op1/unlock' && init?.method === 'POST') return okJson(users[0]);
      if (url === '/api/security/service-tokens' && init?.method === 'POST') {
        return okJson({
          token: 'vst_new_secret_plaintext_token_1234567890',
          details: { ...tokens[0], id: 't2', name: 'mes-line2', prefix: 'vst_new_secr' },
        });
      }
      if (url === '/api/security/service-tokens') return okJson(tokens);
      if (url.startsWith('/api/audit')) return okJson(audits);
      if (url === '/api/security/audit/verify') {
        return okJson({
          valid: true, checked: 1, firstInvalidSeq: null, firstInvalidReason: null,
          lastSeq: 1, lastHash: 'abc', verifiedAt: '2026-11-25T01:00:00Z',
        });
      }
      return okJson({});
    }));

    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'adm1', username: 'admin', displayName: '管理员', role: 'Administrator' },
    });

    render(<SecurityAdminPanel open onClose={() => undefined} />);
    await waitFor(() => expect(screen.getByText('op1')).toBeTruthy());

    fireEvent.click(screen.getByRole('button', { name: '解 锁' }));
    await waitFor(() => expect(calls).toContain('POST /api/security/users/u-op1/unlock'));

    await act(async () => { fireEvent.click(screen.getByText('服务令牌 (MES/上位机)')); });
    expect(screen.getByText('mes-line1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '签发令牌' }));
    fireEvent.change(await screen.findByPlaceholderText('例如 mes-line1'), { target: { value: 'mes-line2' } });
    fireEvent.click(screen.getByRole('button', { name: '签 发' }));
    expect(await screen.findByText('vst_new_secret_plaintext_token_1234567890')).toBeTruthy();

    await act(async () => { fireEvent.click(screen.getByText('审计日志与链校验')); });
    fireEvent.click(screen.getByRole('button', { name: '校验审计链完整性' }));
    expect(await screen.findByText('审计哈希链完整（已核验 1 条）')).toBeTruthy();
  });

  it('shows and triggers diagnostic bundle download for Administrator only', async () => {
    const bundleCalls: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/diagnostics/bundle') {
        bundleCalls.push(url);
        return new Response(new Blob(['zip'], { type: 'application/zip' }), {
          status: 200,
          headers: { 'Content-Disposition': 'attachment; filename="diag.zip"' },
        });
      }
      if (url === '/api/diagnostics/assets' || url === '/api/diagnostics/events?take=250') return okJson([]);
      if (url === '/api/diagnostics/summary') return okJson({ total: 0, healthy: 0, degraded: 0, faulted: 0, offline: 0 });
      return okJson({});
    }));
    vi.stubGlobal('URL', {
      createObjectURL: vi.fn(() => 'blob:diag'),
      revokeObjectURL: vi.fn(),
    });

    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'op1', username: 'op1', displayName: '操作员', role: 'Operator' },
    });
    const { rerender } = render(<DiagnosticsPanel open onClose={() => undefined} />);
    await waitFor(() => expect(screen.getByText('资产总数')).toBeTruthy());
    expect(screen.queryByRole('button', { name: '下载诊断包' })).toBeNull();

    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'adm1', username: 'admin', displayName: '管理员', role: 'Administrator' },
    });
    rerender(<DiagnosticsPanel open onClose={() => undefined} />);
    const downloadButton = screen.getByRole('button', { name: '下载诊断包' });
    fireEvent.click(downloadButton);
    await waitFor(() => expect(bundleCalls).toEqual(['/api/diagnostics/bundle']));
  });

  it('exposes SecurityAdminPanel in Tools menu and Command Palette', () => {
    const setSecurityOpen = vi.fn();
    const noop = vi.fn();
    const openers = {
      setCalibrationOpen: noop, setTracesOpen: noop, setJobsOpen: noop, setValidationOpen: noop,
      setTuningOpen: noop, setModulesOpen: noop, setCamerasOpen: noop, setRobotsOpen: noop,
      setDevicesOpen: noop, setProductionOpen: noop, setSpcOpen: noop, setDiagnosticsOpen: noop,
      setStorageOpen: noop, setSecurityOpen, setPluginsOpen: noop, setModelPackagesOpen: noop,
      setModelAssetsOpen: noop, setFramesOpen: noop, setHardwareProvenanceOpen: noop, setShortcutsOpen: noop, setAboutOpen: noop,
    };

    expect(buildPaletteTargets(false).some((x) => x.key === 'panel:security')).toBe(true);
    runPaletteCommand('panel:security', {
      ...openers,
      run: noop, validate: noop, save: noop, fitView: noop, toggleFocusMode: noop, toggleTheme: noop, toggleLang: noop,
      focusMode: false,
    });
    expect(setSecurityOpen).toHaveBeenCalledWith(true);

    const tools = buildToolMenuItems({
      catalogSource: 'runtime', catalogCount: 10, catalogSchemaVersion: 1, loadedPluginCount: 0,
      history: { canUndo: false, canRedo: false }, themeMode: 'light',
      undo: noop, redo: noop, toggleTheme: noop, openWorkflowPicker: noop, reopenOnboarding: noop,
    }, openers);
    expect(tools.some((x) => x && 'key' in x && x.key === 'security')).toBe(true);
  });
});
