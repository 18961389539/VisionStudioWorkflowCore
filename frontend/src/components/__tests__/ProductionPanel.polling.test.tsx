import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ProductionPanel from '../ProductionPanel';

function okResponse(data: unknown): Response {
  return { ok: true, status: 200, json: async () => data } as Response;
}

function failedResponse(status: number, body: unknown = {}): Response {
  return { ok: false, status, json: async () => body } as Response;
}

const statusBody = { state: 'Stopped', productionLocked: false };

/**
 * R04 回归（2026-10-11 复审报告）：生产界面此前把 HTTP 失败当作刷新成功——四个接口只在
 * response.ok 时更新数据，随后**无条件**更新 lastUpdatedAt 并清 stale，于是 503/401/500 时
 * 旧产量/运行状态仍显示"数据更新于当前时间"。维护/待重启期间可稳定触发全部 503。
 */
describe('ProductionPanel polling (R04)', () => {
  beforeEach(() => {
    // antd 的 Table/Tabs 需要 ResizeObserver（jsdom 不实现）。
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
        addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn()
      }))
    });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  const flush = async () => {
    await act(async () => {
      for (let i = 0; i < 6; i++) await Promise.resolve();
    });
  };

  it('marks the panel stale when the status endpoint starts failing (instead of showing current time)', async () => {
    vi.useFakeTimers();
    let statusCalls = 0;
    vi.stubGlobal('fetch', vi.fn((url: string) => {
      if (url.includes('/api/production/status')) {
        statusCalls++;
        return Promise.resolve(statusCalls === 1 ? okResponse(statusBody) : failedResponse(503, { detail: 'Restart required', code: 'restart_required' }));
      }
      if (url.includes('/api/production/device-actions/pending')) return Promise.resolve(failedResponse(204));
      if (url.includes('/api/jobs') || url.includes('/api/alarms')) return Promise.resolve(okResponse([]));
      return Promise.resolve(okResponse({}));
    }));

    render(<ProductionPanel open onClose={() => undefined} embedded />);
    await flush();
    expect(screen.getByText(/数据更新于/)).toBeTruthy();

    // 第二次轮询返回 503：必须显示"数据已过期"并给出最后成功时间，绝不能再显示"数据更新于当前时间"。
    await act(async () => { vi.advanceTimersByTime(1100); });
    await flush();
    const header = screen.getByText(/数据已过期|数据更新于/).textContent ?? '';
    expect(header).toContain('数据已过期');
    expect(header).toContain('最后成功更新');
    expect(statusCalls).toBeGreaterThanOrEqual(2);
  });

  it('never claims fresh data while the very first status request is failing', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((url: string) => {
      if (url.includes('/api/production/status')) return Promise.resolve(failedResponse(401, { detail: 'session expired' }));
      if (url.includes('/api/production/device-actions/pending')) return Promise.resolve(failedResponse(204));
      if (url.includes('/api/jobs') || url.includes('/api/alarms')) return Promise.resolve(okResponse([]));
      return Promise.resolve(okResponse({}));
    }));

    render(<ProductionPanel open onClose={() => undefined} embedded />);
    await flush();
    const text = document.body.textContent ?? '';
    expect(text).toContain('数据已过期');
    expect(text).not.toContain('数据更新于');
  });
});
