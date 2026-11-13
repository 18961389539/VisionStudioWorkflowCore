import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import CameraPanel from '../components/CameraPanel';
import type { CameraDescriptor } from '../types';

/**
 * 相机连接与参数应用的状态机（2026-11-13）。
 * 此前单测只覆盖轮询节奏与媒体分页；"设备命令失败后界面是否还停在旧状态"、
 * "参数被后端拒绝时用户是否看得到原因"这两类现场问题没有护栏。
 */
const camera: CameraDescriptor = {
  id: 'virtual-1',
  name: '虚拟相机',
  driver: 'virtual',
  state: 'Open',
  framesCaptured: 0,
  source: 'virtual',
  error: null,
  settings: {
    exposureUs: 5000,
    gainDb: 0,
    targetFps: 10,
    triggerMode: 'Software',
    outputPixelFormat: 'Auto',
    externalTriggerSource: 'Line1'
  },
  capabilities: {
    exposure: true,
    gain: true,
    frameRate: true,
    softwareTrigger: true,
    externalTrigger: true,
    hostSimulatedExternalTrigger: false,
    minExposureUs: 10,
    maxExposureUs: 1000000,
    minGainDb: 0,
    maxGainDb: 48,
    maxFps: 240,
    outputPixelFormats: ['Auto', 'Mono8', 'Bgr8']
  },
  acquisition: {
    acquisitionState: 'Running',
    framesPublished: 12,
    ringOverwrites: 0,
    acquisitionErrors: 0,
    reconnectCount: 0,
    lastSequence: 12,
    actualFps: 10,
    lastFrameAt: null,
    runtimeError: null,
    frameTimeouts: 0,
    driverDroppedFrames: 0,
    nativePixelFormat: 'Mono8'
  }
};

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status,
  headers: { 'Content-Type': 'application/json' }
});

const flush = async () => {
  await act(async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); });
};

function stubLayout() {
  vi.stubGlobal('matchMedia', vi.fn((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn()
  })));
  vi.stubGlobal('ResizeObserver', class {
    observe() { /* jsdom 无布局 */ }
    unobserve() { /* jsdom 无布局 */ }
    disconnect() { /* jsdom 无布局 */ }
  });
}

type Handler = (url: string, init?: RequestInit) => Promise<Response> | undefined;

function mount(handler?: Handler) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const custom = handler?.(url, init);
    if (custom) return custom;
    if (url === '/api/cameras') return json([camera]);
    if (url.includes('/api/cameras/adapters')) return json([]);
    if (url.includes('/api/media/items')) return json([], 200);
    if (url.includes('/api/cameras/virtual-1/preview')) return json({}, 404);
    return json([]);
  });
  vi.stubGlobal('fetch', fetchMock);
  return {
    ...render(<CameraPanel open onClose={vi.fn()} />),
    fetchMock
  };
}

const cameraListCalls = (fetchMock: ReturnType<typeof vi.fn>) =>
  fetchMock.mock.calls.filter(([input]) => String(input) === '/api/cameras').length;

beforeEach(() => { stubLayout(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('CameraPanel 设备命令与参数错误', () => {
  it('打开设备成功后立即刷新列表；失败时显示后端原因', async () => {
    let openFails = false;
    const { fetchMock } = mount((url, init) => {
      if (url === '/api/cameras/virtual-1/open' && init?.method === 'POST') {
        return Promise.resolve(openFails ? json({ detail: '设备已被占用' }, 409) : json({}));
      }
      return undefined;
    });

    const openButton = await screen.findByRole('button', { name: '打开设备' });
    await waitFor(() => expect(openButton).toBeEnabled());
    const before = cameraListCalls(fetchMock);

    fireEvent.click(openButton);
    await flush();
    expect(fetchMock.mock.calls.some(([url, init]) => String(url) === '/api/cameras/virtual-1/open' && init?.method === 'POST')).toBe(true);
    expect(cameraListCalls(fetchMock)).toBeGreaterThan(before);

    openFails = true;
    fireEvent.click(openButton);
    expect(await screen.findByText('设备已被占用')).toBeInTheDocument();
  });

  it('参数被后端拒绝时显示原因且保留输入值', async () => {
    const { fetchMock } = mount((url, init) => {
      if (url === '/api/cameras/virtual-1/settings' && init?.method === 'PUT') {
        return Promise.resolve(json({ detail: '曝光时间超出设备范围' }, 400));
      }
      return undefined;
    });

    const exposure = await screen.findByRole('spinbutton', { name: '曝光时间（μs）' });
    fireEvent.change(exposure, { target: { value: '123' } });
    await flush();
    fireEvent.click(screen.getByRole('button', { name: '应用设置' }));

    expect(await screen.findByText('曝光时间超出设备范围')).toBeInTheDocument();
    expect(exposure).toHaveValue('123');
    expect(fetchMock.mock.calls.some(([url, init]) => String(url) === '/api/cameras/virtual-1/settings' && init?.method === 'PUT')).toBe(true);
  });

  it('运行期掉线信息直接显示在采集页', async () => {
    mount((url) => {
      if (url === '/api/cameras') {
        return Promise.resolve(json([{
          ...camera,
          state: 'Faulted',
          acquisition: { ...camera.acquisition, acquisitionState: 'Reconnecting', runtimeError: '设备断线，正在重连' }
        }]));
      }
      return undefined;
    });

    expect(await screen.findByText('设备断线，正在重连')).toBeInTheDocument();
    expect(await screen.findByText('设备 故障')).toBeInTheDocument();
  });
});
