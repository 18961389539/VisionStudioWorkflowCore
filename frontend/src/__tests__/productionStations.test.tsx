import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ProductionStations from '../components/ProductionStations';

const ok = (data: unknown) => ({ ok: true, status: 200, json: async () => data }) as Response;
const station = (
  id: string,
  state: 'Stopped' | 'Running',
  productionControlEnabled = true,
) => ({
  id, name: `工位 ${id}`, operatorUrl: `https://${id}.example/?view=operator`,
  connection: 'Online', lastCheckedAt: '2026-11-24T08:00:00Z', lastSeenAt: '2026-11-24T08:00:00Z',
  status: {
    stationId: id, state, jobId: `recipe-${id}`, job: { name: `配方 ${id}`, publishedVersion: 1, publishedWorkflowHash: 'hash' },
    lockedVersion: state === 'Running' ? 1 : null, lockedWorkflowHash: state === 'Running' ? 'hash' : null,
    cycleCount: state === 'Running' ? 2 : 0, okCount: state === 'Running' ? 2 : 0, ngCount: 0,
    lastDisposition: state === 'Running' ? 'OK' : null, lastCycleAt: state === 'Running' ? '2026-11-24T08:00:00Z' : null,
    activeAlarms: 0, criticalAlarms: 0, pendingAlarms: 0,
  },
  target: null, versionCheck: 'NotConfigured', recipeDeliveryEnabled: false, productionControlEnabled,
});
const flush = async () => {
  await act(async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); });
};

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('多工位并发生产控制', () => {
  it('只把当前可启动工位提交到批量启停接口并显示逐工位结果', async () => {
    const stations = [station('a', 'Stopped'), station('b', 'Running'), station('c', 'Stopped', false)];
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      if (String(url).endsWith('/api/production/stations/control')) {
        return ok({
          action: 'Start', requestedAt: '2026-11-24T08:00:01Z',
          stations: [{ stationId: 'a', stationName: '工位 a', succeeded: true, state: 'Running', jobId: 'recipe-a', lockedVersion: 1 }],
        });
      }
      return ok(stations);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<ConfigProvider theme={{ token: { motion: false } }}>
      <ProductionStations canSend={false} canControl />
    </ConfigProvider>);
    await screen.findByText('工位 a');
    await flush();

    fireEvent.click(screen.getByRole('checkbox', { name: '选择工位 a' }));
    fireEvent.click(screen.getByRole('checkbox', { name: '选择工位 b' }));
    expect(screen.getByRole('checkbox', { name: '选择工位 c' })).toBeDisabled();
    expect(screen.getByText('已选 2 个工位')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '启动已选' }));
    const confirmation = screen.getByText('启动 1 个工位').closest('.ant-modal')!;
    await act(async () => {
      fireEvent.click(within(confirmation as HTMLElement).getByRole('button', { name: '并发启动' }));
    });
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) =>
      String(url).endsWith('/api/production/stations/control'))).toBe(true));

    const control = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/production/stations/control'));
    expect(JSON.parse(String(control?.[1]?.body))).toEqual({ stationIds: ['a'], action: 'Start' });
    expect(initMethod(control?.[1])).toBe('POST');
    await screen.findByText(/启动请求已完成：1\/1 成功/);
    expect(screen.getByText(/工位 a（a）：运行中.*recipe-a/)).toBeInTheDocument();
  });

  it('没有控制令牌时不显示远程启停入口', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ok([station('a', 'Stopped', false)])));
    render(<ConfigProvider><ProductionStations canSend={false} canControl /></ConfigProvider>);
    await screen.findByText('工位 a');
    expect(screen.getByRole('button', { name: '启动已选' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: '选择工位 a' })).toBeDisabled();
  });
});

function initMethod(init: RequestInit | undefined): string | undefined {
  return init?.method;
}
