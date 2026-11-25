import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import PluginPackagePanel from '../components/PluginPackagePanel';
import ScriptEditor from '../components/ScriptEditor';
import { ThemeProvider } from '../theme';

const securityMock = vi.fn();

vi.mock('../security', () => ({
  useSecurityAccount: () => securityMock(),
}));

function okJson(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

describe('Algorithm / script / plugin developer experience', () => {
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

  it('shows Plugin SDK compatibility info and allows Engineer to rescan plugins', async () => {
    const rescanCalls: string[] = [];
    const onCatalogRefresh = vi.fn(async () => undefined);
    securityMock.mockReturnValue({
      enabled: true,
      user: { id: 'eng1', username: 'eng1', displayName: '工程师', role: 'Engineer' },
    });

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/plugins') return okJson([]);
      if (url === '/api/plugins/sdk') {
        return okJson({
          apiVersion: 2,
          packageManifestSchemaVersion: 2,
          supportedLifetimes: ['Singleton', 'Scoped'],
          supportedConcurrencyModes: ['SingleThreaded', 'Concurrent'],
          supportedIsolationModes: ['InProcess', 'WorkerProcess'],
          runtimeDiscovery: true,
          runtimeReplacement: false,
          isolationBoundary: 'WorkerProcess executes plugins in an isolated process.',
        });
      }
      if (url === '/api/plugins/rescan' && init?.method === 'POST') {
        rescanCalls.push(url);
        return okJson({ loadedNow: 1, failed: 0, restartRequired: 0, plugins: [] });
      }
      if (url.startsWith('/api/plugin-') || url === '/api/validation/datasets') return okJson([]);
      return okJson({});
    }));

    render(<PluginPackagePanel open onClose={() => undefined} onCatalogRefresh={onCatalogRefresh} />);
    await waitFor(() => expect(screen.getByText(/SDK v2/)).toBeTruthy());
    expect(screen.getByText('WorkerProcess executes plugins in an isolated process.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '重新扫描插件目录' }));
    await waitFor(() => expect(rescanCalls).toEqual(['/api/plugins/rescan']));
    expect(onCatalogRefresh).toHaveBeenCalledTimes(1);
  });

  it('documents using statement for Mat disposal in ScriptEditor API panel', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => okJson({ valid: true, diagnostics: [] })));
    const onChange = vi.fn();

    render(
      <ThemeProvider>
        <ScriptEditor value="Output.Number = 1;" onChange={onChange} />
      </ThemeProvider>
    );

    fireEvent.click(screen.getByRole('button', { name: 'API' }));
    expect(await screen.findByText('using (var m = new Mat()) { }')).toBeTruthy();
  });
});
