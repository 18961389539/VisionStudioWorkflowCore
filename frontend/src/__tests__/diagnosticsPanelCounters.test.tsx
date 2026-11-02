import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { apiFetch } = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('../apiClient', () => ({ apiFetch }));

// DiagnosticsPanel 会建 SignalR 连接；这里只关心 HTTP 快照里的计数器状态，
// 把 hub 换成永不触发事件的桩，避免测试依赖真实 WebSocket。
vi.mock('@microsoft/signalr', () => {
  const connection = {
    state: 'Disconnected',
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
    start: () => Promise.resolve(),
    stop: () => Promise.resolve()
  };
  const builder: Record<string, unknown> = {};
  builder.withUrl = () => builder;
  builder.withAutomaticReconnect = () => builder;
  builder.configureLogging = () => builder;
  builder.build = () => connection;
  return {
    HubConnectionBuilder: function HubConnectionBuilder() { return builder; },
    HubConnectionState: { Disconnected: 'Disconnected' },
    LogLevel: { Warning: 'Warning' }
  };
});

import DiagnosticsPanel from '../components/DiagnosticsPanel';
import type { RunCounterPersistenceStatus } from '../types';

const summary = { total: 0, healthy: 0, degraded: 0, faulted: 0, offline: 0, updatedAt: '2026-10-29T00:00:00Z' };

function respond(body: unknown) {
  return Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as Response);
}

function stubApi(counters: RunCounterPersistenceStatus | undefined) {
  // apiFetch 的签名是 (input: RequestInfo | URL, options)，input 可能是 URL 对象也可能是字符串，
  // 这里统一字符串化，避免某次调用未传参时把类型错误暴露成 "undefined.includes"。
  apiFetch.mockImplementation((input?: RequestInfo | URL) => {
    const url = String(input ?? '');
    if (url.includes('diagnostics/assets')) return respond([]);
    if (url.includes('diagnostics/events')) return respond([]);
    if (url.includes('diagnostics/summary')) return respond(summary);
    if (url.includes('diagnostics/run-counters')) {
      return counters ? respond(counters) : Promise.reject(new Error('unavailable'));
    }
    return respond({});
  });
}

describe('DiagnosticsPanel 跨轮计数落盘状态', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    // antd Modal/Segmented 依赖这两个浏览器 API，jsdom 没有实现。
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addEventListener() {}, removeEventListener() {} })));
  });
  afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

  it('落盘降级时提示磁盘副本落后，并带出最近错误', async () => {
    stubApi({
      snapshotPath: 'data/runtime/counters.json',
      counterCount: 3,
      lastWriteAt: null,
      consecutiveFailures: 5,
      lastError: '磁盘空间不足',
      degraded: true
    });

    render(<DiagnosticsPanel open onClose={() => {}} />);

    // 现场语义要写清楚：不是"计数错了"，而是"重启后会倒退"。
    expect(await screen.findByText(/跨轮计数未落盘/)).toBeTruthy();
    expect(screen.getByText(/磁盘空间不足/)).toBeTruthy();
  });

  it('落盘正常时不打扰用户', async () => {
    stubApi({
      snapshotPath: 'data/runtime/counters.json',
      counterCount: 2,
      lastWriteAt: '2026-10-29T00:00:05Z',
      consecutiveFailures: 0,
      lastError: null,
      degraded: false
    });

    render(<DiagnosticsPanel open onClose={() => {}} />);

    expect(await screen.findByText('资产总数')).toBeTruthy();
    expect(screen.queryByText(/跨轮计数未落盘/)).toBeNull();
  });

  it('计数器端点自身失败时不影响面板其余部分', async () => {
    stubApi(undefined);

    render(<DiagnosticsPanel open onClose={() => {}} />);

    // 端点不可用只让这一块消失；资产/事件/汇总照常渲染，不应报"诊断 API 不可用"。
    expect(await screen.findByText('资产总数')).toBeTruthy();
    expect(screen.queryByText(/Diagnostics API unavailable/)).toBeNull();
  });
});
