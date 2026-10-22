import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import PluginPackagePanel from '../components/PluginPackagePanel';
import type { PluginPackageVersionInfo } from '../types';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const installed: PluginPackageVersionInfo = {
  id: 'plugin.demo', name: 'Demo', version: '1.0.0', active: true, pending: false,
  publisherTrusted: true, packageSha256: 'a'.repeat(64), contentSha256: 'b'.repeat(64),
  signed: true, signatureValid: true, restartRequired: false,
  vendor: 'Demo Vendor', installedAt: '2026-10-22T00:00:00Z', packageFormat: 'vspkg'
};

function stubApi(overrides: Record<string, (init?: RequestInit) => Response | Promise<Response>> = {}) {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const handler = overrides[url];
    if (handler) return handler(init);
    if (url === '/api/plugins' || url === '/api/plugin-workers' || url === '/api/plugin-benchmarks'
      || url === '/api/validation/datasets' || url === '/api/plugin-workers/performance')
      return json([]);
    if (url === '/api/plugin-packages') return json([installed]);
    if (url === '/api/plugin-publishers') return json([]);
    return json({});
  });
}

function stubBrowserApis() {
  vi.stubGlobal('matchMedia', vi.fn((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn()
  })));
  vi.stubGlobal('ResizeObserver', class {
    observe() { /* jsdom 无布局 */ }
    unobserve() { /* no-op */ }
    disconnect() { /* no-op */ }
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('PluginPackagePanel install smoke', () => {
  it('shows the backend rejection when install fails and keeps the package selected', async () => {
    const fetchMock = stubApi({
      '/api/plugin-packages/install': () => json({ detail: 'signature not trusted', code: 'plugin-untrusted' }, 422)
    });
    stubBrowserApis();
    vi.stubGlobal('fetch', fetchMock);

    render(<PluginPackagePanel open onClose={() => undefined} onCatalogRefresh={() => undefined} />);

    // 安装按钮依赖已选择的文件：直接调用上传回调等效于用户选择文件。
    const file = new File([new Uint8Array([1, 2, 3])], 'demo.vspkg');
    const upload = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(upload, { target: { files: [file] } });

    const install = await screen.findByRole('button', { name: /安\s*装/ });
    await waitFor(() => expect(install).not.toBeDisabled());
    fireEvent.click(install);

    expect(await screen.findByText(/signature not trusted/)).toBeTruthy();
    // 失败后不清理已选文件：用户修正（如信任发布者）后可直接重试。
    expect(install).not.toBeDisabled();
  });
});
