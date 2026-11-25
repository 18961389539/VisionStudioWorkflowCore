import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import LocalParallelRuntimes from '../components/LocalParallelRuntimes';

const securityMock = vi.fn();

vi.mock('../security', () => ({
  useSecurityAccount: () => securityMock(),
  SecurityAccountBadge: () => <div data-testid="security-badge" />,
}));

vi.mock('../useMonitorStream', () => ({
  useMonitorSnapshot: () => undefined,
}));

vi.mock('../components/ProductionPanel', () => ({
  default: ({ embedded, runtimeOnly, initialView, headerActions }: { embedded?: boolean; runtimeOnly?: boolean; initialView?: string; headerActions?: React.ReactNode }) => (
    <div data-testid="operator-workstation" data-embedded={String(embedded)} data-runtime-only={String(runtimeOnly)} data-initial-view={initialView}>
      {headerActions}
    </div>
  ),
}));

vi.mock('../components/EditorHeader', () => ({
  default: () => <div data-testid="editor-header" />,
}));

vi.mock('../components/EditorCanvasArea', () => ({
  default: () => <div data-testid="editor-canvas" />,
}));

vi.mock('../components/EditorBottomDock', () => ({
  default: () => null,
}));

vi.mock('../components/EditorOverlays', () => ({
  default: () => null,
}));

vi.mock('../components/EditorPanels', () => ({
  default: () => null,
}));

vi.mock('../components/Toolbox', () => ({
  default: () => null,
}));

function okJson(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

describe('Operator experience protections', () => {
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
    window.history.replaceState({}, '', '/');
  });

  it('hides device action reconciliation button in LocalParallelRuntimes unless canReconcile is true', async () => {
    const station = {
      stationId: 'line-1',
      state: 'Stopped',
      jobId: 'job-1',
      lockedJobVersion: 1,
      cycleCount: 10,
      okCount: 9,
      ngCount: 1,
      errorCount: 0,
      lastDurationMs: 12.5,
      lastError: null,
      productionLocked: false,
    };
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/production/runtimes') return okJson([station]);
      return okJson({});
    }));

    const { rerender } = render(<LocalParallelRuntimes jobs={[]} canConfigure={false} canReconcile={false} />);
    await waitFor(() => expect(screen.getByText('line-1')).toBeTruthy());
    expect(screen.queryByRole('button', { name: '核对动作' })).toBeNull();

    rerender(<LocalParallelRuntimes jobs={[]} canConfigure={false} canReconcile />);
    expect(screen.getByRole('button', { name: '核对动作' })).toBeTruthy();
  });

  it('routes an Operator on / directly to the production workstation instead of the workflow editor', async () => {
    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'op-1', username: 'op1', displayName: '操作员', role: 'Operator' },
      busy: false,
      logout: async () => undefined,
      onPasswordChanged: () => undefined,
    });
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/host-mode') return okJson({ runtimeOnly: false });
      if (url === '/api/catalog') return okJson([]);
      if (url === '/api/plugins') return okJson([]);
      return okJson({});
    }));

    const { default: App } = await import('../App');
    render(<App />);

    const workstation = await screen.findByTestId('operator-workstation');
    expect(workstation.getAttribute('data-embedded')).toBe('true');
    expect(workstation.getAttribute('data-runtime-only')).toBe('false');
    expect(workstation.getAttribute('data-initial-view')).toBe('operator');
    expect(screen.queryByTestId('editor-header')).toBeNull();
  });

  it('keeps the workflow editor for an Engineer on /', async () => {
    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'eng-1', username: 'eng1', displayName: '工程师', role: 'Engineer' },
      busy: false,
      logout: async () => undefined,
      onPasswordChanged: () => undefined,
    });
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/host-mode') return okJson({ runtimeOnly: false });
      if (url === '/api/catalog') return okJson([]);
      if (url === '/api/plugins') return okJson([]);
      return okJson({});
    }));

    const { default: App } = await import('../App');
    render(<App />);

    expect(await screen.findByTestId('editor-header')).toBeTruthy();
    expect(screen.queryByTestId('operator-workstation')).toBeNull();
  });

  it('shows admin controls in RuntimeOnly workstation for Administrator and hides them for Operator', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/host-mode') return okJson({ runtimeOnly: true });
      if (url === '/api/catalog') return okJson([]);
      if (url === '/api/plugins') return okJson([]);
      return okJson({});
    }));
    window.history.replaceState({}, '', '/?view=operator');

    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'op-1', username: 'op1', displayName: '操作员', role: 'Operator' },
      busy: false,
      logout: async () => undefined,
      onPasswordChanged: () => undefined,
    });

    const { default: App } = await import('../App');
    const { rerender } = render(<App />);
    await screen.findByTestId('operator-workstation');
    expect(screen.getByRole('button', { name: '诊 断' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '安全管理' })).toBeNull();
    expect(screen.queryByRole('button', { name: '存储与备份' })).toBeNull();

    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'adm-1', username: 'admin', displayName: '管理员', role: 'Administrator' },
      busy: false,
      logout: async () => undefined,
      onPasswordChanged: () => undefined,
    });
    rerender(<App />);
    expect(screen.getByRole('button', { name: '安全管理' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '存储与备份' })).toBeTruthy();
  });
});
