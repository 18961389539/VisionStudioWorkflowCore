import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import TemplateAssetTable from '../components/TemplateAssetTable';

const asset = {
  id: 'tpl-a', name: '上盖模板', description: '', createdAt: '2026-11-10T02:00:00Z', updatedAt: '2026-11-10T02:00:00Z',
  latestVersion: 1,
  versions: [{ version: 1, sha256: 'a'.repeat(64), sizeBytes: 4096, width: 800, height: 600, contentType: 'image/png', note: '', createdAt: '2026-11-10T02:00:00Z', createdBy: '' }]
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

describe('template asset table', () => {
  it('lists template assets with decoded dimensions', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify([asset]), { status: 200, headers: { 'Content-Type': 'application/json' } })));
    render(<TemplateAssetTable />);

    expect(await screen.findByText('上盖模板')).toBeInTheDocument();
    expect(screen.getByText('800 × 600')).toBeInTheDocument();
    expect(screen.getByText('4.0 KB')).toBeInTheDocument();
  });

  it('uploads a local image and lets the server decode its size', async () => {
    stubBrowserApis();
    const calls: { url: string; method: string; body?: string }[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined });
      if (url === '/api/template-assets' && init?.method === 'POST') return new Response(JSON.stringify(asset), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url === '/api/template-assets') return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }));

    render(<TemplateAssetTable />);
    await screen.findByText('还没有模板图资产：点「上传模板图」选一张本地图片');

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    const bytes = new Uint8Array([137, 80, 78, 71]);
    const file = new File([bytes], 'cover.png', { type: 'image/png' });
    Object.defineProperty(file, 'arrayBuffer', { value: async () => bytes.buffer });
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);

    await waitFor(() => {
      const post = calls.find((call) => call.method === 'POST');
      expect(post?.url).toBe('/api/template-assets');
      const body = JSON.parse(post!.body!);
      expect(body.name).toBe('cover');
      expect(body.contentType).toBe('image/png');
      // 尺寸交给服务端解码，不传浏览器猜测值。
      expect(body.width).toBe(0);
      expect(body.height).toBe(0);
      expect(typeof body.imageBase64).toBe('string');
    });
  });
});
