import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import DevicePanel from '../DevicePanel';

function okResponse(data: unknown): Response {
  return { ok: true, json: async () => data } as Response;
}

describe('DevicePanel polling', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'matchMedia', { configurable: true, value: vi.fn(() => ({
      matches: false, media: '', onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn()
    })) });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('marks successful data stale while a later poll hangs, then clears stale on recovery', async () => {
    vi.useFakeTimers();
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn((_url: string, options?: RequestInit) => {
      calls++;
      if (calls === 1) return Promise.resolve(okResponse([]));
      if (calls === 2) return new Promise<Response>((_resolve, reject) => {
        options?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      });
      return Promise.resolve(okResponse([]));
    }));

    render(<DevicePanel open onClose={() => undefined} />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText(/最后更新/).textContent).not.toContain('数据可能已过期');

    await act(async () => { vi.advanceTimersByTime(3500); });
    expect(screen.getByText(/数据可能已过期/)).toBeTruthy();
    expect(calls).toBe(2); // pending 请求未结束前不并发发起下一次轮询，避免响应倒序覆盖。

    await act(async () => { vi.advanceTimersByTime(2000); await Promise.resolve(); });
    await act(async () => { vi.advanceTimersByTime(500); await Promise.resolve(); });
    expect(screen.getByText(/最后更新/).textContent).not.toContain('数据可能已过期');
  });

  it('aborts the active poll on unmount without treating lifecycle cancellation as a fault', async () => {
    vi.useFakeTimers();
    let signal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((_url: string, options?: RequestInit) => {
      signal = options?.signal as AbortSignal;
      return new Promise<Response>(() => {});
    }));

    const { unmount } = render(<DevicePanel open onClose={() => undefined} />);
    expect(signal?.aborted).toBe(false);
    unmount();
    expect(signal?.aborted).toBe(true);
  });

  it('marks the panel stale when its very first request stays pending past the TTL', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));

    render(<DevicePanel open onClose={() => undefined} />);
    await act(async () => { vi.advanceTimersByTime(3500); });
    expect(screen.getByText(/设备数据暂不可用/)).toBeTruthy();
  });

  it('does not let an old request finally block unlock polling after close and reopen', async () => {
    vi.useFakeTimers();
    let resolveOld: ((response: Response) => void) | undefined;
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn(() => {
      calls++;
      if (calls === 1) return new Promise<Response>((resolve) => { resolveOld = resolve; });
      return new Promise<Response>(() => {});
    }));

    const view = render(<DevicePanel open onClose={() => undefined} />);
    expect(calls).toBe(1);
    view.rerender(<DevicePanel open={false} onClose={() => undefined} />);
    view.rerender(<DevicePanel open onClose={() => undefined} />);
    expect(calls).toBe(2);

    await act(async () => { resolveOld?.(okResponse([])); await Promise.resolve(); });
    await act(async () => { vi.advanceTimersByTime(1000); });
    expect(calls).toBe(2);
    view.unmount();
  });
});
