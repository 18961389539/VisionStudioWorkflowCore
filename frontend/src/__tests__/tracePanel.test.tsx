import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigProvider } from 'antd';
import TracePanel from '../components/TracePanel';
import { useTraceQuery, type TraceQueryResult } from '../useTraceQuery';

vi.mock('../useTraceQuery', () => ({ useTraceQuery: vi.fn() }));
vi.mock('../components/StoragePanel', () => ({ default: () => null }));
vi.mock('../components/ReplayDebuggerPanel', () => ({ default: () => null }));

function mount(overrides: Partial<TraceQueryResult> = {}) {
  vi.mocked(useTraceQuery).mockReturnValue({
    data: null, stats: null, storage: null, error: null, warnings: [],
    loading: false, stale: false, rangeDescription: null, loadedAt: null, reload: vi.fn(),
    ...overrides,
  });
  return render(<ConfigProvider theme={{ token: { motion: false } }}><TracePanel open onClose={vi.fn()} /></ConfigProvider>);
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('追溯查询反馈', () => {
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
