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

function mount(handler?: Handler, initialTab: 'acquisition' | 'media' = 'acquisition') {
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
    ...render(<CameraPanel open initialTab={initialTab} onClose={vi.fn()} />),
    fetchMock
  };
}

const cameraListCalls = (fetchMock: ReturnType<typeof vi.fn>) =>
  fetchMock.mock.calls.filter(([input]) => String(input) === '/api/cameras').length;

beforeEach(() => { stubLayout(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('CameraPanel 设备命令与参数错误', () => {
  it('媒体库支持选择文件夹并批量导入其中的图像', async () => {
    const { fetchMock } = mount(undefined, 'media');
    expect(await screen.findByRole('button', { name: '选择文件夹' })).toBeInTheDocument();
    const directoryInput = Array.from(document.querySelectorAll<HTMLInputElement>('input[type="file"]'))
      .find((input) => input.hasAttribute('directory') || input.hasAttribute('webkitdirectory'));
    expect(directoryInput).toBeDefined();

    const first = new File(['first'], 'first.png', { type: 'image/png' });
    const second = new File(['second'], 'nested.jpg', { type: 'image/jpeg' });
    Object.defineProperty(second, 'webkitRelativePath', { value: 'sample/nested.jpg' });
    fireEvent.change(directoryInput!, { target: { files: [first, second] } });

    const importButton = await screen.findByRole('button', { name: /导入 2 张图像/ });
    fireEvent.click(importButton);
    await waitFor(() => {
      const request = fetchMock.mock.calls.find(([url]) => String(url) === '/api/media/import');
      expect(request).toBeDefined();
      const form = request![1]?.body as FormData;
      expect(form.getAll('files')).toHaveLength(2);
      expect(form.getAll('relativePaths')).toEqual(['first.png', 'sample/nested.jpg']);
    });
  });

  it('媒体库将 4000 张图片保存在上传队列中且不渲染全部文件', async () => {
    const { fetchMock } = mount(undefined, 'media');
    expect(await screen.findByRole('button', { name: '选择文件夹' })).toBeInTheDocument();
    const directoryInput = Array.from(document.querySelectorAll<HTMLInputElement>('input[type="file"]'))
      .find((input) => input.hasAttribute('directory') || input.hasAttribute('webkitdirectory'));
    expect(directoryInput).toBeDefined();

    const files = Array.from({ length: 4000 }, (_, index) =>
      new File([String(index)], `image-${index}.png`, { type: 'image/png' }));
    fireEvent.change(directoryInput!, { target: { files } });

    expect(await screen.findByRole('button', { name: '导入 4000 张图像' })).toBeInTheDocument();
    expect(screen.getByText(/将分 20 批上传/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '导入 4000 张图像' }));
    await waitFor(() => {
      expect(fetchMock.mock.calls.filter(([url]) => String(url) === '/api/media/import')).toHaveLength(20);
    });
  });

  it('媒体库支持索引筛选和批量改标签', async () => {
    const item = {
      relativePath: 'offline/OK/camera/frame.png',
      source: 'media://offline/OK/camera/frame.png',
      collection: 'offline',
      label: 'OK',
      name: 'frame.png',
      extension: '.png',
      bytes: 100,
      modifiedAt: '2026-11-20T00:00:00Z',
      width: 1280,
      height: 720
    };
    const { fetchMock } = mount((url, init) => {
      if (url.includes('/api/media/items')) return Promise.resolve(new Response(JSON.stringify([item]), {
        status: 200,
        headers: { 'Content-Type': 'application/json', 'X-Total-Count': '1' }
      }));
      if (url === '/api/media/bulk' && init?.method === 'POST')
        return Promise.resolve(json({ succeeded: 1, items: [], failures: [] }));
      return undefined;
    }, 'media');
    expect(await screen.findByText('frame.png')).toBeInTheDocument();

    fireEvent.change(screen.getByRole('textbox', { name: '搜索媒体文件名' }), { target: { value: 'frame' } });
    fireEvent.change(screen.getByRole('spinbutton', { name: '最小宽度' }), { target: { value: '640' } });
    fireEvent.click(screen.getByRole('button', { name: /^扫\s*描$/ }));
    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([url]) =>
        String(url).includes('search=frame') && String(url).includes('minWidth=640'))).toBe(true);
    });

    const checkboxes = screen.getAllByRole('checkbox');
    fireEvent.click(checkboxes.at(-1)!);
    fireEvent.click(screen.getByRole('button', { name: '改标签' }));
    await waitFor(() => {
      const request = fetchMock.mock.calls.find(([url]) => String(url) === '/api/media/bulk');
      expect(request).toBeDefined();
      expect(JSON.parse(String(request![1]?.body))).toMatchObject({
        action: 'relabel',
        paths: ['offline/OK/camera/frame.png'],
        targetLabel: 'Unlabeled'
      });
    });
  });

  it('媒体库可扫描完整性并提供索引修复入口', async () => {
    const { fetchMock } = mount((url) => {
      if (url === '/api/media/integrity')
        return Promise.resolve(json({
          scannedAt: '2026-11-26T10:00:00Z',
          fileCount: 4209,
          indexedCount: 4208,
          duplicateGroups: 2,
          issues: [{ path: 'missing.png', kind: 'missing-index', detail: null }]
        }));
      return undefined;
    }, 'media');
    await screen.findByRole('button', { name: '扫描完整性' });
    fireEvent.click(screen.getByRole('button', { name: '扫描完整性' }));

    expect(await screen.findByText(/媒体完整性：4209 个文件，2 组重复/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '修复索引并清理缩略图' })).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([url]) => String(url) === '/api/media/integrity')).toBe(true);
  });

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
