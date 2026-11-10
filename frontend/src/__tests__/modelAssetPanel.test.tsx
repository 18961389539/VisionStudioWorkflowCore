import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ModelAssetPanel from '../components/ModelAssetPanel';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const asset = {
  id: 'shape-a', name: '上盖模型', description: '', createdAt: '2026-11-09T10:00:00Z', updatedAt: '2026-11-09T10:00:00Z',
  latestVersion: 2,
  versions: [
    { version: 2, sha256: 'b'.repeat(64), sizeBytes: 2048, trainingConfig: 'cfg-2', sourceImageSha256: '', note: 'retrained', createdAt: '2026-11-09T10:00:00Z', createdBy: 'engineer' },
    { version: 1, sha256: 'a'.repeat(64), sizeBytes: 1024, trainingConfig: 'cfg-1', sourceImageSha256: '', note: '', createdAt: '2026-11-08T10:00:00Z', createdBy: 'engineer' }
  ]
};

function stubApi() {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url === '/api/model-assets') return json([asset]);
    if (url === '/api/model-assets/shape-a/references') return json([{ workflowId: 'wf-1', nodeId: 'shape-1' }]);
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

describe('model asset panel', () => {
  it('lists assets with their latest version and size', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi());
    render(<ModelAssetPanel open onClose={() => undefined} />);

    expect(await screen.findByText('上盖模型')).toBeInTheDocument();
    expect(screen.getByText('v2')).toBeInTheDocument();
    expect(screen.getByText('2.0 KB')).toBeInTheDocument();
    expect(screen.getByText('shape-a')).toBeInTheDocument();
  });

  it('shows which workflow nodes reference an asset', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi());
    render(<ModelAssetPanel open onClose={() => undefined} />);

    // antd 会在两个汉字之间插入空格（autoInsertSpace），可访问名是「引 用」。
    fireEvent.click(await screen.findByRole('button', { name: /^引\s*用$/ }));

    await waitFor(() => expect(screen.getByText('wf-1')).toBeInTheDocument());
    expect(screen.getByText('shape-1')).toBeInTheDocument();
  });

  it('surfaces the server reference list when deletion is refused', async () => {
    stubBrowserApis();
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/model-assets') return json([asset]);
      if (url === '/api/model-assets/shape-a' && init?.method === 'DELETE')
        return json({ title: 'Operation conflicts with current state', detail: 'Shape model asset shape-a is referenced by 1 workflow node(s): wf-1/shape-1.' }, 409);
      return json({});
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<ModelAssetPanel open onClose={() => undefined} />);

    fireEvent.click(await screen.findByRole('button', { name: /^删\s*除$/ }));
    // 行按钮与 Popconfirm 的确认按钮同名：取最后一个（弹出的确认按钮）。
    const confirmButtons = await screen.findAllByRole('button', { name: /^删\s*除$/ });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]);

    await waitFor(() => expect(screen.getByText(/referenced by 1 workflow node/)).toBeInTheDocument());
  });
});

describe('model asset reclaim', () => {
  const plan = {
    candidates: [{ assetId: 'shape-a', version: 1, sha256: 'a'.repeat(64), sizeBytes: 1024 }],
    reclaimableBytes: 1024, totalVersions: 2, protectedAsLatest: 1, protectedByReference: 0
  };

  it('previews candidates before deleting and posts an explicit confirm', async () => {
    stubBrowserApis();
    const calls: { url: string; method: string; body?: string }[] = [];
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined });
      if (url === '/api/model-assets/gc/preview') return json(plan);
      if (url === '/api/model-assets/gc') return json(plan);
      if (url === '/api/model-assets') return json([asset]);
      return json({});
    }));

    render(<ModelAssetPanel open onClose={() => undefined} />);
    const reclaimButton = (await screen.findByText('清理未引用版本')).closest('button');
    expect(reclaimButton).not.toBeNull();
    fireEvent.click(reclaimButton!);

    // 预览弹窗里能看到候选版本与被保护的数量，确认前不会发回收请求。
    expect(await screen.findByText(/共 2 个版本：1 个是各资产最新版/)).toBeInTheDocument();
    expect(screen.getAllByText('shape-a').length).toBeGreaterThan(0);
    expect(calls.some((call) => call.url === '/api/model-assets/gc')).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: /回收 1 个版本/ }));

    await waitFor(() => {
      const applyCall = calls.find((call) => call.url === '/api/model-assets/gc');
      expect(applyCall?.method).toBe('POST');
      expect(JSON.parse(applyCall!.body!)).toEqual({ confirm: true });
    });
  });
});
