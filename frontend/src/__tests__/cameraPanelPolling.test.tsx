import { cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CameraPanel from '../components/CameraPanel';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('CameraPanel polling lifecycle', () => {
  it('starts polling only when opened and stops after unmount', async () => {
    // 挂起的请求足以观察轮询节奏；无需构造完整相机数据。
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
    const json = (body: unknown) => new Response(JSON.stringify(body), {
      status: 200, headers: { 'Content-Type': 'application/json' }
    });
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/cameras' || url.includes('adapters') || url.includes('/groups')
        || url.includes('/runs') || url.includes('/items') || url.includes('/collections')
        || url.includes('/tests')) return json([]);
      return json({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const view = render(<CameraPanel open={false} onClose={() => undefined} />);
    expect(fetchMock).not.toHaveBeenCalled();

    view.rerender(<CameraPanel open onClose={() => undefined} />);
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => String(url) === '/api/cameras')).toBe(true));

    const whileOpen = fetchMock.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 1_100));
    expect(fetchMock.mock.calls.length).toBeGreaterThan(whileOpen);

    view.unmount();
    const afterUnmount = fetchMock.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 1_300));
    expect(fetchMock.mock.calls.length).toBe(afterUnmount);
  });
});
