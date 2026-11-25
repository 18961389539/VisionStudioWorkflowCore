import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ProductionPanel from '../components/ProductionPanel';
import { defaultProductionConfig as defaults } from '../productionConfig';

// 推送通道单独测试；这里只覆盖轮询兜底（2026-11-13 起为 10 s）。
vi.mock('../useMonitorStream', () => ({ useMonitorSnapshot: () => undefined }));

const ok = (data: unknown, etag = '"config-test"') => ({ ok: true, status: 200, headers: new Headers({ ETag: etag }), json: async () => data }) as Response;
const empty = () => ({ ok: true, status: 204 }) as Response;
const stopped = { state: 'Stopped', productionLocked: false, cycleCount: 0, okCount: 0, ngCount: 0,
  errorCount: 0, consecutiveFailures: 0, watchdogTrips: 0, lastDurationMs: 0 };
const config = { ...defaults, jobId: 'A', maxRecoveryAttempts: 7 };
const flush = async () => { await act(async () => { for (let i = 0; i < 15; i++) await Promise.resolve(); }); };
const tick = async () => { await act(async () => { await vi.advanceTimersByTimeAsync(10_000); }); };
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
function mount(override?: (base: Fetcher) => Fetcher, props?: { kiosk?: boolean; initialJobId?: string; initialView?: 'operator' | 'stations' | 'deliveries' }) {
  let remote = { ...config };
  let revision = '"config-test"';
  let status = { ...stopped };
  const base: Fetcher = async (url, init) => {
    if (url === '/api/production/config' && init?.method === 'PUT') { remote = JSON.parse(String(init.body)); return ok(remote, revision); }
    if (url === '/api/production/start') { status = { ...status, state: 'Running', productionLocked: true }; return ok(status); }
    if (url.endsWith('/config')) return ok(remote, revision);
    if (url.endsWith('/status')) return ok(status);
    if (url.endsWith('/pending')) return empty();
    return ok(url === '/api/jobs' ? [
      { id: 'A', name: '检测配方', publishedVersion: 1 },
      { id: 'B', name: '新发布配方', publishedVersion: 2 }
    ] : []);
  };
  const fetchMock = vi.fn(override ? override(base) : base);
  vi.stubGlobal('fetch', fetchMock);
  return {
    ...render(<ConfigProvider theme={{ token: { motion: false } }}><ProductionPanel open embedded onClose={vi.fn()} {...props} /></ConfigProvider>),
    fetchMock, setRemote: (value: typeof config, nextRevision = revision) => { remote = value; revision = nextRevision; },
  };
}
async function openConfig() {
  await flush();
  // 配置类标签收在「配置」分组里，默认停在「运行」分组
  if (!screen.queryByRole('tab', { name: '生产配置' })) {
    fireEvent.click(screen.getByRole('radio', { name: '配置' }));
    await flush();
  }
  fireEvent.click(screen.getByRole('tab', { name: '生产配置' }));
  await flush();
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('生产配置编辑与运行入口', () => {
  it('停止等待超过 20 秒仍接收结果，故障响应不会显示已停止', async () => {
    const pending = deferred<Response>();
    let signal: AbortSignal | undefined;
    let current = { ...stopped, state: 'Running', productionLocked: true, lastError: '' };
    mount((base) => (url, init) => {
      if (url.endsWith('/status')) return Promise.resolve(ok(current));
      if (url.endsWith('/stop')) { signal = init?.signal ?? undefined; return pending.promise; }
      return base(url, init);
    });
    await flush();
    fireEvent.click(screen.getByRole('button', { name: /^停\s*止$/ }));
    const confirmation = screen.getByText('停止生产运行').closest('.ant-popconfirm')!;
    fireEvent.click(within(confirmation as HTMLElement).getByRole('button', { name: /^停\s*止$/ }));
    await act(async () => { await vi.advanceTimersByTimeAsync(21_000); });
    expect(signal?.aborted).toBe(false);

    current = { ...current, state: 'Faulted', lastError: '停止超时，执行仍在收尾' };
    await act(async () => { pending.resolve(ok(current)); });
    expect(screen.getAllByText(current.lastError).length).toBeGreaterThan(0);
    expect(screen.queryByText('生产已停止')).not.toBeInTheDocument();
  });

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
    expect(new Headers(saved[1]?.headers).get('If-Match')).toBe('"config-test"');
    expect(JSON.parse(String(saved[1]!.body))).toMatchObject({ cycleDelayMs: 120, jobId: 'A', maxRecoveryAttempts: 7 });
    fireEvent.click(startButton());
    await flush();
    expect(fetchMock.mock.calls.filter(([url]) => url.endsWith('/start'))).toHaveLength(1);
    expect(screen.getByRole('button', { name: /^停\s*止$/ })).toBeEnabled();
    expect(cycleInput()).toBeDisabled();
  });

  it('preselects a newly published recipe as an unsaved local production draft without starting it', async () => {
    const { fetchMock } = mount(undefined, { initialJobId: 'B' });
    await openConfig();

    expect(screen.getByText('已将刚发布的配方选为本机候选生产作业')).toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
    fireEvent.click(saveButton());
    await flush();

    const save = fetchMock.mock.calls.find(([url, init]) => url.endsWith('/config') && init?.method === 'PUT');
    expect(JSON.parse(String(save?.[1]?.body))).toMatchObject({ jobId: 'B' });
    expect(fetchMock.mock.calls.some(([url]) => url.endsWith('/start'))).toBe(false);
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

  it('合并不同字段后保留两边改动，点击保存时才携带最新修订提交', async () => {
    const { fetchMock, setRemote } = mount();
    await openConfig();
    await changeDelay(120);
    setRemote({ ...config, maxRecoveryAttempts: 8 }, '"revision-2"');
    await tick();
    expect(saveButton()).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: '合并到最新配置' }));
    await flush();
    expect(cycleInput()).toHaveValue('120');
    expect(startButton()).toBeDisabled();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false);

    fireEvent.click(saveButton());
    await flush();
    const write = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1];
    expect(new Headers(write?.headers).get('If-Match')).toBe('"revision-2"');
    expect(JSON.parse(String(write?.body))).toMatchObject({ cycleDelayMs: 120, maxRecoveryAttempts: 8 });
    expect(startButton()).toBeEnabled();
  });

  it('同一字段的冲突需要选择，继续收到更新后要求重新核对', async () => {
    const { setRemote } = mount();
    await openConfig();
    await changeDelay(120);
    setRemote({ ...config, cycleDelayMs: 45 });
    await tick();
    const mergeButton = () => screen.getByRole('button', { name: '合并到最新配置' });
    expect(mergeButton()).toBeDisabled();
    fireEvent.click(screen.getByRole('radio', { name: '保留草稿值' }));
    expect(mergeButton()).toBeEnabled();

    setRemote({ ...config, cycleDelayMs: 50 });
    await tick();
    expect(mergeButton()).toBeDisabled();
    fireEvent.click(screen.getByRole('radio', { name: '使用服务端值' }));
    fireEvent.click(mergeButton());
    await flush();
    expect(cycleInput()).toHaveValue('50');
    expect(saveButton()).toBeDisabled();
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

describe('生产面板标签分组', () => {
  it('默认停在运行组，配置类标签收进配置组', async () => {
    mount();
    await flush();
    expect(screen.getByRole('tab', { name: '运行监控' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: '生产配置' })).toBeNull();

    fireEvent.click(screen.getByRole('radio', { name: '配置' }));
    await flush();
    expect(screen.getByRole('tab', { name: '生产配置' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'PTP 保护' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: '运行监控' })).toBeNull();
  });

  it('在配置组里切回运行组时落到第一个运行标签，不会停在已隐藏的标签上', async () => {
    mount();
    await flush();
    fireEvent.click(screen.getByRole('radio', { name: '配置' }));
    await flush();
    fireEvent.click(screen.getByRole('tab', { name: 'PTP 保护' }));
    await flush();
    fireEvent.click(screen.getByRole('radio', { name: '运行' }));
    await flush();
    expect(screen.getByRole('tab', { name: '运行监控' })).toHaveAttribute('aria-selected', 'true');
  });
});

describe('操作员告警卡', () => {
  it('严重告警时标出严重级并显示最近一条内容', async () => {
    mount((base) => async (url, init) => {
      if (url.includes('/operator-layout')) return ok({ title: null, widgets: ['Result', 'Counts', 'Alarms'] });
      if (url.endsWith('/alarms/summary')) return ok({ activeCount: 3, criticalCount: 2, pendingCount: 1, latestActiveMessage: '安全门被打开' });
      return base(url, init);
    });
    await flush();
    fireEvent.click(screen.getByRole('tab', { name: '操作员' }));
    await flush();
    const card = document.querySelector('[data-widget="Alarms"]') as HTMLElement;
    expect(card).not.toBeNull();
    expect(card.getAttribute('data-severity')).toBe('critical');
    expect(within(card).getByText('安全门被打开')).toBeInTheDocument();
    expect(within(card).getByText('待确认 1 · 严重 2')).toBeInTheDocument();
  });

  it('无告警时不给严重级颜色，只说明无活动告警', async () => {
    mount((base) => async (url, init) => {
      if (url.includes('/operator-layout')) return ok({ title: null, widgets: ['Result', 'Counts', 'Alarms'] });
      if (url.endsWith('/alarms/summary')) return ok({ activeCount: 0, criticalCount: 0, pendingCount: 0, latestActiveMessage: null });
      return base(url, init);
    });
    await flush();
    fireEvent.click(screen.getByRole('tab', { name: '操作员' }));
    await flush();
    const card = document.querySelector('[data-widget="Alarms"]') as HTMLElement;
    expect(card.getAttribute('data-severity')).toBeNull();
    expect(within(card).getByText('无活动告警')).toBeInTheDocument();
  });

  it('告警统计读取失败时不保留上一轮的严重级', async () => {
    let fail = false;
    mount((base) => async (url, init) => {
      if (url.includes('/operator-layout')) return ok({ title: null, widgets: ['Result', 'Counts', 'Alarms'] });
      if (url.endsWith('/alarms/summary')) return fail
        ? { ok: false, status: 503, json: async () => ({ detail: 'alarm store offline' }) } as Response
        : ok({ activeCount: 3, criticalCount: 2, pendingCount: 1, latestActiveMessage: '安全门被打开' });
      return base(url, init);
    });
    await flush();
    fireEvent.click(screen.getByRole('tab', { name: '操作员' }));
    await flush();
    expect((document.querySelector('[data-widget="Alarms"]') as HTMLElement).getAttribute('data-severity')).toBe('critical');

    fail = true;
    await tick();
    const card = document.querySelector('[data-widget="Alarms"]') as HTMLElement;
    expect(card.getAttribute('data-severity')).toBeNull();
    expect(within(card).getByText('告警统计未确认')).toBeInTheDocument();
  });
});

describe('车间大屏形态（kiosk）', () => {
  it('只渲染操作员工作台：没有标签栏、没有布局编辑入口', async () => {
    mount(undefined, { kiosk: true });
    await flush();
    // 标签栏是误触来源，kiosk 下必须整条消失
    expect(screen.queryByRole('tablist')).toBeNull();
    expect(screen.getByRole('region', { name: '操作员工作台' })).toBeInTheDocument();
    expect(screen.getByText('最近判定')).toBeInTheDocument();
    // 工程师入口不随大屏暴露
    expect(screen.queryByRole('button', { name: '配置操作员界面' })).toBeNull();
    // 页面标题让位给工作台本身
    expect(screen.queryByText('VisionStudio · 生产运行')).toBeNull();
  });

  it('状态读取失败时保留上次数值并标注过期，而不是抹成破折号', async () => {
    let fail = false;
    const running = { ...stopped, state: 'Running', productionLocked: true,
      cycleCount: 12480, okCount: 12351, ngCount: 129, lastDurationMs: 186.4,
      lastDisposition: 'NG', lastCycleAt: new Date().toISOString() };
    mount((base) => async (url, init) => {
      if (url.endsWith('/status')) return fail
        ? { ok: false, status: 503, json: async () => ({ detail: 'gateway down' }) } as Response
        : ok(running);
      return base(url, init);
    });
    await flush();
    fireEvent.click(screen.getByRole('tab', { name: '操作员' }));
    await flush();
    expect(screen.getByText(/12,480/)).toBeInTheDocument();

    fail = true;
    await tick();
    // 关键：数字仍是最后一次读数（车间里破折号会被读成“今天零产量”）
    expect(screen.getByText(/12,480/)).toBeInTheDocument();
    expect(screen.getAllByText(/数据已过期/).length).toBeGreaterThan(0);

    fail = false;
    await tick();
    expect(screen.queryByText(/数据已过期/)).toBeNull();
  });

  it('非 kiosk 仍保留标签栏与布局编辑入口（回归）', async () => {
    mount();
    await flush();
    expect(screen.getByRole('tablist')).toBeInTheDocument();
    expect(screen.getByText('VisionStudio · 生产运行')).toBeInTheDocument();
  });
});
