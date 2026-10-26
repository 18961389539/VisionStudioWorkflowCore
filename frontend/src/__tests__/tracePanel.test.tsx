import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigProvider } from 'antd';
import TracePanel from '../components/TracePanel';
import type { RunTraceRecord } from '../types';
import { useTraceQuery, type TraceQueryResult } from '../useTraceQuery';

vi.mock('../useTraceQuery', () => ({ useTraceQuery: vi.fn() }));
vi.mock('../components/StoragePanel', () => ({ default: () => null }));
vi.mock('../components/ReplayDebuggerPanel', () => ({ default: () => null }));

function mount(overrides: Partial<TraceQueryResult> = {}, props: { initialRunId?: string } = {}) {
  vi.mocked(useTraceQuery).mockReturnValue({
    data: null, stats: null, storage: null, error: null, warnings: [],
    loading: false, stale: false, rangeDescription: null, loadedAt: null, reload: vi.fn(),
    ...overrides,
  });
  return render(<ConfigProvider theme={{ token: { motion: false } }}><TracePanel open onClose={vi.fn()} initialRunId={props.initialRunId} /></ConfigProvider>);
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('追溯查询反馈', () => {
  it('列表不带节点报告时，选中后按需取详情并渲染报告表', async () => {
    // 性能（2026-10-26）：列表默认 includeReports=false，报告只在选中后取一次。
    const base = {
      runId: 'run-detail', startedAt: new Date().toISOString(), source: 'job', jobId: 'job-1', jobVersion: 1,
      workflowId: 'wf-1', workflowName: '流程', workflowHash: null, dependencyManifestHash: null,
      executionStatus: 'Completed', disposition: 'OK', totalDurationMs: 12, nodeCount: 1, overlayCount: 0,
      hasPreview: false, hasReplayInput: false, replaySourceNodeId: null, error: null, note: null,
      nodeReports: []
    };
    const record = base as unknown as RunTraceRecord;
    const detail = {
      ...base,
      nodeReports: [{ nodeId: 'n1', nodeType: 'image.threshold', success: true, durationMs: 1, summary: {} }]
    } as unknown as RunTraceRecord;
    // 面板内还有调查/观测等子面板会各自请求；只对追溯详情返回数据，其余一律 404，
    // 避免用同一份假数据喂给形状不同的接口。
    const fetchMock = vi.fn(async (url: string) => (String(url).includes('/api/traces/run-detail')
      ? { ok: true, status: 200, json: async () => detail } as Response
      : { ok: false, status: 404, json: async () => ({}) } as Response));
    vi.stubGlobal('fetch', fetchMock);
    mount({ data: { total: 1, offset: 0, limit: 50, items: [record] } });
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(await screen.findByText('image.threshold')).toBeTruthy();
  });

  it('从运行结果跳转进来时直接筛到那次运行', () => {
    mount({}, { initialRunId: 'run-abc123' });
    expect(vi.mocked(useTraceQuery).mock.lastCall?.[1]).toMatchObject({ preset: 'week', runIdPrefix: 'run-abc123' });
  });

  it('显式点击查询时，即使筛选值未变也会刷新', () => {
    const reload = vi.fn();
    mount({ reload });
    fireEvent.click(screen.getByRole('button', { name: /^查\s*询$/ }));
    expect(reload).toHaveBeenCalledOnce();
  });

  it('不可用的统计显示占位符，不冒充零条记录，并支持重试', () => {
    const reload = vi.fn();
    mount({ warnings: ['统计查询失败：HTTP 503'], reload });
    const values = [...document.querySelectorAll('.ant-statistic-content-value')];
    expect(values).toHaveLength(9);
    expect(values.every((value) => value.textContent === '—')).toBe(true);
    expect(screen.getByText('统计查询失败：HTTP 503')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /^重\s*试$/ }));
    expect(reload).toHaveBeenCalledOnce();
  });

  it('新查询失败时明确显示上次结果及其原始范围', () => {
    mount({ data: { total: 0, offset: 0, limit: 50, items: [] }, stale: true, error: '查询超时', rangeDescription: '昨天的范围' });
    expect(screen.getByText('结果范围：昨天的范围')).toBeInTheDocument();
    expect(screen.getByText('查询未成功，当前显示上次结果，请重试')).toBeInTheDocument();
    expect(screen.getByText('查询超时')).toBeInTheDocument();
  });

  it('自定义时间编辑后保持用户输入的本地时间，提交转换为 UTC', () => {
    mount();
    fireEvent.click(screen.getByText('自定义'));
    const start = screen.getByLabelText('自定义开始时间');
    const end = screen.getByLabelText('自定义结束时间');
    fireEvent.change(start, { target: { value: '2026-10-20T18:35' } });
    fireEvent.change(end, { target: { value: '2026-10-20T19:35' } });
    expect(start).toHaveValue('2026-10-20T18:35');
    expect(end).toHaveValue('2026-10-20T19:35');
    fireEvent.click(screen.getByRole('button', { name: /^查\s*询$/ }));
    expect(vi.mocked(useTraceQuery).mock.lastCall?.[1]).toMatchObject({
      preset: 'custom', customFrom: new Date(2026, 9, 20, 18, 35).toISOString(), customTo: new Date(2026, 9, 20, 19, 35).toISOString(),
    });
  });

  it('开始时间晚于结束时间时提示纠正并禁止提交', () => {
    mount();
    fireEvent.click(screen.getByText('自定义'));
    fireEvent.change(screen.getByLabelText('自定义开始时间'), { target: { value: '2026-10-21T10:00' } });
    fireEvent.change(screen.getByLabelText('自定义结束时间'), { target: { value: '2026-10-20T10:00' } });
    expect(screen.getByText('开始时间不能晚于结束时间')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^查\s*询$/ })).toBeDisabled();
  });
});
