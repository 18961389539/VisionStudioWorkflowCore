import { act, cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CameraPanel from '../components/CameraPanel';

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('CameraPanel polling lifecycle', () => {
  it('starts polling only when opened and stops after unmount', async () => {
    // 挂起的请求足以观察轮询节奏；无需构造完整相机数据。
    vi.useFakeTimers();
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
    await act(async () => { await Promise.resolve(); });
    expect(fetchMock).not.toHaveBeenCalled();

    view.rerender(<CameraPanel open onClose={() => undefined} />);
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    expect(fetchMock.mock.calls.some(([url]) => String(url) === '/api/cameras')).toBe(true);

    // 2026-11-12：变化由服务端推送触发，定时器只做 10 s 断线兜底——这里验证兜底确实会自己再拉一次。
    const whileOpen = fetchMock.mock.calls.length;
    await act(async () => { vi.advanceTimersByTime(10_000); await Promise.resolve(); });
    expect(fetchMock.mock.calls.length).toBeGreaterThan(whileOpen);

    view.unmount();
    const afterUnmount = fetchMock.mock.calls.length;
    await act(async () => { vi.advanceTimersByTime(30_000); await Promise.resolve(); });
    expect(fetchMock.mock.calls.length).toBe(afterUnmount);
  });
});
