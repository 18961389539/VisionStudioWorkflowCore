import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ProductionPanel from '../components/ProductionPanel';
import { defaultProductionConfig as defaults } from '../productionConfig';

const ok = (data: unknown) => ({ ok: true, status: 200, json: async () => data }) as Response;
const empty = () => ({ ok: true, status: 204 }) as Response;
const stopped = { state: 'Stopped', productionLocked: false, cycleCount: 0, okCount: 0, ngCount: 0,
  errorCount: 0, consecutiveFailures: 0, watchdogTrips: 0, lastDurationMs: 0 };
const config = { ...defaults, jobId: 'A', maxRecoveryAttempts: 7 };
const flush = async () => { await act(async () => { for (let i = 0; i < 15; i++) await Promise.resolve(); }); };
const tick = async () => { await act(async () => { await vi.advanceTimersByTimeAsync(1000); }); };
const cycleInput = () => screen.getByRole('spinbutton', { name: '周期延迟' });
const saveButton = () => screen.getByRole('button', { name: '保存配置' });
const startButton = () => screen.getByRole('button', { name: '启动生产' });
const changeDelay = async (delay: number) => { fireEvent.change(cycleInput(), { target: { value: String(delay) } }); await flush(); };
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
type Fetcher = (url: string, init?: RequestInit) => Promise<Response>;
function mount(override?: (base: Fetcher) => Fetcher) {
  let remote = { ...config };
  let status = { ...stopped };
  const base: Fetcher = async (url, init) => {
    if (url === '/api/production/config' && init?.method === 'PUT') { remote = JSON.parse(String(init.body)); return ok(remote); }
    if (url === '/api/production/start') { status = { ...status, state: 'Running', productionLocked: true }; return ok(status); }
    if (url.endsWith('/config')) return ok(remote);
    if (url.endsWith('/status')) return ok(status);
    if (url.endsWith('/pending')) return empty();
    return ok(url === '/api/jobs' ? [{ id: 'A', name: '检测配方', publishedVersion: 1 }] : []);
  };
  const fetchMock = vi.fn(override ? override(base) : base);
  vi.stubGlobal('fetch', fetchMock);
  return {
    ...render(<ConfigProvider theme={{ token: { motion: false } }}><ProductionPanel open embedded onClose={vi.fn()} /></ConfigProvider>),
    fetchMock, setRemote: (value: typeof config) => { remote = value; },
  };
}
async function openConfig() { await flush(); fireEvent.click(screen.getByRole('tab', { name: '生产配置' })); await flush(); }

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('生产配置编辑与运行入口', () => {
  it('轮询更新监控，不覆盖输入；未保存时禁用启动，保存后使用已保存配置', async () => {
    const { fetchMock } = mount();
    await openConfig();
    expect(startButton()).toBeEnabled();
    expect(saveButton()).toBeDisabled();
    await changeDelay(120);
    await tick();
    expect(cycleInput()).toHaveValue('120');
    expect(screen.getByText('生产配置有未保存修改')).toBeInTheDocument();
    expect(startButton()).toBeDisabled();
    fireEvent.click(startButton());
    expect(fetchMock.mock.calls.some(([url]) => url.endsWith('/start'))).toBe(false);
    fireEvent.click(saveButton());
    await flush();
    expect(screen.queryByText('生产配置有未保存修改')).toBeNull();
    expect(startButton()).toBeEnabled();
    const saved = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!;
    expect(JSON.parse(String(saved[1]!.body))).toMatchObject({ cycleDelayMs: 120, jobId: 'A', maxRecoveryAttempts: 7 });
    fireEvent.click(startButton());
    await flush();
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('/start'))).toHaveLength(1);
    expect(screen.getByRole('button', { name: /^停\s*止$/ })).toBeEnabled();
    expect(cycleInput()).toBeDisabled();
  });

  it('保存等待期间新增输入保留为未保存，下次提交才包含这些修改', async () => {
    const { fetchMock, setRemote } = mount();
    const pending = deferred<Response>();
    const base = fetchMock.getMockImplementation()!;
    let saves = 0;
    fetchMock.mockImplementation(async (url, init) => url.endsWith('/config') && init?.method === 'PUT' && ++saves === 1
      ? pending.promise : base(url, init));
    await openConfig();
    await changeDelay(100);
    fireEvent.click(saveButton());
    await flush();
    await changeDelay(250);
    setRemote({ ...config, cycleDelayMs: 100 });
    await act(async () => { pending.resolve(ok({ ...config, cycleDelayMs: 100 })); });
    await flush();
    expect(cycleInput()).toHaveValue('250');
    expect(startButton()).toBeDisabled();
    expect(saveButton()).toBeEnabled();
    expect(screen.queryByText('服务端配置已变更，当前编辑已保留')).toBeNull();
    fireEvent.click(saveButton());
    await flush();
    const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT');
    expect(writes.map(([, init]) => JSON.parse(String(init!.body)).cycleDelayMs)).toEqual([100, 250]);
    expect(startButton()).toBeEnabled();
  });

  it('服务端配置改变时阻止覆盖，放弃修改后明确载入最新配置', async () => {
    const { fetchMock, setRemote } = mount();
    await openConfig();
    await changeDelay(100);
    setRemote({ ...config, cycleDelayMs: 45 });
    await tick();
    expect(cycleInput()).toHaveValue('100');
    expect(screen.getByText('服务端配置已变更，当前编辑已保留')).toBeInTheDocument();
    expect(saveButton()).toBeDisabled();
    expect(startButton()).toBeDisabled();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: '放弃修改，载入最新配置' }));
    await flush();
    expect(cycleInput()).toHaveValue('45');
    expect(startButton()).toBeEnabled();
  });

  it.each(['before-edit', 'after-edit'])('保存响应慢于轮询（%s）时仍保留改回原值的输入，并要求再次保存', async (order) => {
    const { fetchMock, setRemote } = mount();
    const pending = deferred<Response>(); const base = fetchMock.getMockImplementation()!;
    let saves = 0;
    fetchMock.mockImplementation(async (url, init) => url.endsWith('/config') && init?.method === 'PUT' && ++saves === 1
      ? pending.promise : base(url, init));
    await openConfig();
    await changeDelay(100);
    fireEvent.click(saveButton()); await flush();
    setRemote({ ...config, cycleDelayMs: 100 });
    if (order === 'before-edit') await tick();
    await changeDelay(config.cycleDelayMs);
    if (order === 'after-edit') await tick();
    expect(cycleInput()).toHaveValue(String(config.cycleDelayMs));
    await act(async () => { pending.resolve(ok({ ...config, cycleDelayMs: 100 })); });
    await flush();
    expect(cycleInput()).toHaveValue(String(config.cycleDelayMs));
    expect(screen.getByText('生产配置有未保存修改')).toBeInTheDocument();
    expect(screen.queryByText('服务端配置已变更，当前编辑已保留')).toBeNull();
    expect(startButton()).toBeDisabled();
    expect(saveButton()).toBeEnabled();
    fireEvent.click(saveButton()); await flush();
    const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT');
    expect(writes.map(([, init]) => JSON.parse(String(init!.body)).cycleDelayMs)).toEqual([100, config.cycleDelayMs]);
    expect(startButton()).toBeEnabled();
    expect(fetchMock.mock.calls.some(([url]) => url.endsWith('/start'))).toBe(false);
  });

  it('配置尚未读到时禁用输入和保存，不能把展示用默认值写回服务器', async () => {
    const { fetchMock } = mount((base) => (url, init) => url.endsWith('/config') ? new Promise<Response>(() => {}) : base(url, init));
    await openConfig();
    expect(cycleInput()).toBeDisabled();
    expect(saveButton()).toBeDisabled();
    expect(startButton()).toBeDisabled();
    fireEvent.click(saveButton());
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false);
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(screen.getByText('生产配置暂不可用')).toBeInTheDocument();
  });

  it('配置成功响应缺字段时显示错误，保留其它资源的刷新', async () => {
    mount((base) => async (url, init) => url.endsWith('/config') ? ok({})
      : url.endsWith('/pending') ? ok({ runId: 'pending-run', deviceIds: ['device-1'] }) : base(url, init));
    await openConfig();
    expect(saveButton()).toBeDisabled();
    expect(screen.getByText('生产配置响应无效。')).toBeInTheDocument();
    expect(screen.getByText('运行：pending-run')).toBeInTheDocument();
    expect(screen.getByText(/数据更新于/)).toBeInTheDocument();
  });

  it('保存拒绝保留输入与错误详情，允许核对原因后重新保存', async () => {
    const { fetchMock } = mount();
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (url, init) => init?.method === 'PUT'
      ? { ok: false, status: 409, json: async () => ({ detail: 'config locked', correlationId: 'trace-save' }) } as Response : base(url, init));
    await openConfig();
    await changeDelay(100);
    fireEvent.click(saveButton());
    await flush();
    expect(cycleInput()).toHaveValue('100');
    expect(screen.getByText('生产操作未完成或结果未确认')).toBeInTheDocument();
    expect(screen.getAllByText(/config locked.*trace-save/).length).toBeGreaterThan(0);
    expect(startButton()).toBeDisabled();
    expect(saveButton()).toBeEnabled();
  });

  it('核对窗口固定已审查的动作，重复确认和验证期间取消都不产生额外提交', async () => {
    let pending = { runId: 'old-run', manifestHash: 'old-manifest', deviceIds: ['old-device'], robotIds: [] };
    const submission = deferred<Response>();
    const { fetchMock } = mount((base) => async (url, init) => url.endsWith('/pending') ? ok(pending)
      : url.endsWith('/resolve') ? submission.promise : base(url, init));
    await flush();
    fireEvent.click(screen.getByRole('button', { name: '提交核对证据' }));
    const dialog = screen.getByRole('dialog', { name: '提交设备动作核对证据' });
    fireEvent.change(within(dialog).getByLabelText('现场核对证据（必填）'), { target: { value: '现场核对完毕，设备已复位' } });
    fireEvent.change(within(dialog).getByLabelText('设备身份证据（必填）'), { target: { value: '铭牌 old-device 与待核对记录一致' } });
    pending = { runId: 'new-run', manifestHash: 'new-manifest', deviceIds: ['new-device'], robotIds: [] };
    await tick();
    expect(within(dialog).getByText('本次核对目标：old-run')).toBeInTheDocument();
    const submit = within(dialog).getByRole('button', { name: '提交核对' });
    const cancel = within(dialog).getByRole('button', { name: /^取\s*消$/ });
    act(() => { fireEvent.click(submit); fireEvent.click(submit); fireEvent.click(cancel); });
    await flush();
    const writes = fetchMock.mock.calls.filter(([url]) => url.endsWith('/resolve'));
    expect(writes).toHaveLength(1);
    expect(JSON.parse(String(writes[0][1]!.body))).toMatchObject({ runId: 'old-run', manifestHash: 'old-manifest', deviceIds: ['old-device'] });
    expect(cancel).toBeDisabled();
    expect(dialog).toBeInTheDocument();
    await act(async () => { submission.resolve({ ok: false, status: 409, json: async () => ({ detail: 'resolution target changed' }) } as Response); });
    await flush();
    expect(dialog).toBeInTheDocument();
    expect(cancel).toBeEnabled();
    expect(screen.getAllByText('resolution target changed').length).toBeGreaterThan(0);
  });
});
