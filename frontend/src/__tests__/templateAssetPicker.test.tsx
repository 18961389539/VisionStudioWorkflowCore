import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import TemplateAssetPicker from '../components/TemplateAssetPicker';

const asset = {
  id: 'tpl-a', name: '上盖模板', description: '', createdAt: '2026-11-10T01:00:00Z', updatedAt: '2026-11-10T01:00:00Z',
  latestVersion: 2,
  versions: [
    { version: 2, sha256: 'b'.repeat(64), sizeBytes: 2048, width: 640, height: 480, contentType: 'image/png', note: '', createdAt: '2026-11-10T01:00:00Z', createdBy: 'engineer' }
  ]
};

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

describe('template asset picker', () => {
  it('lists template assets and reports the chosen id with its latest version', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      expect(String(input)).toBe('/api/template-assets');
      return new Response(JSON.stringify([asset]), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }));
    const onChange = vi.fn();
    render(<TemplateAssetPicker assetId="" version={0} onChange={onChange} />);

    expect(screen.getByText('未绑定资产（使用模板端口或模板文件）')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '从资产选择' }));

    expect(await screen.findByText('上盖模板')).toBeInTheDocument();
    expect(screen.getByText('640 × 480')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /选\s*用/ }));

    await waitFor(() => expect(onChange).toHaveBeenCalledWith('tpl-a', 2));
  });

  it('clears the binding back to port/file resolution', async () => {
    stubBrowserApis();
    const onChange = vi.fn();
    render(<TemplateAssetPicker assetId="tpl-a" version={2} onChange={onChange} />);

    expect(screen.getByText('tpl-a · v2')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /清\s*除/ }));

    expect(onChange).toHaveBeenCalledWith('', 0);
  });
});
