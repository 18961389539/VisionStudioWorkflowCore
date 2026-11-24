import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import SpcDashboard from '../components/SpcDashboard';
import { jsonResponse } from './fixtures/tuning';
import type { SpcAnalysis, SpcDatasetSummary } from '../types';

const flush = async () => { await act(async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); }); };

function stubBrowserApis() {
  vi.stubGlobal('matchMedia', vi.fn((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn()
  })));
  vi.stubGlobal('ResizeObserver', class {
    observe() { /* jsdom 无布局 */ }
    unobserve() { /* no-op */ }
    disconnect() { /* no-op */ }
  });
}

const datasets: SpcDatasetSummary[] = [{
  dataset: 'thickness', recordCount: 9, payloadBytes: 1024,
  firstRecordAt: '2026-11-23T08:00:00Z', lastRecordAt: '2026-11-23T08:10:00Z'
}];

const analysis: SpcAnalysis = {
  dataset: 'thickness', chartType: 'xbarR', subgroupSize: 3, grouping: 'sequential',
  recordsRead: 9, recordsUsed: 9, recordsIgnored: 0, samplesUsed: 9, samplesDropped: 0,
  nonFiniteSamples: 0, subgroupCount: 3, mean: 10,
  lowerSpecificationLimit: 8, upperSpecificationLimit: 12,
  capability: {
    cp: 1.2, cpk: 1.1, pp: 1.15, ppk: 1.05,
    lowerSpecificationLimit: 8, upperSpecificationLimit: 12,
    withinSigma: 0.5, overallSigma: 0.52, outOfSpecificationCount: 0
  },
  primaryChart: {
    kind: 'xbar', label: '子组均值 (X̄)', centerLine: 10, upperControlLimit: 10.9, lowerControlLimit: 9.1,
    points: [
      { index: 0, value: 9.8, timestamp: '2026-11-23T08:00:00Z', beyondLimits: false },
      { index: 1, value: 10.1, timestamp: '2026-11-23T08:05:00Z', beyondLimits: false },
      { index: 2, value: 11.2, timestamp: '2026-11-23T08:10:00Z', beyondLimits: true }
    ],
    violations: [{ rule: 'rule1', startIndex: 2, endIndex: 2, description: '第 3 点超出控制上限。' }]
  },
  secondaryChart: {
    kind: 'range', label: '子组极差 (R)', centerLine: 1, upperControlLimit: 2.5, lowerControlLimit: 0,
    points: [
      { index: 0, value: 0.8, timestamp: '2026-11-23T08:00:00Z', beyondLimits: false },
      { index: 1, value: 1.1, timestamp: '2026-11-23T08:05:00Z', beyondLimits: false },
      { index: 2, value: 1.2, timestamp: '2026-11-23T08:10:00Z', beyondLimits: false }
    ],
    violations: []
  },
  warnings: [], truncated: false,
  firstSampleAt: '2026-11-23T08:00:00Z', lastSampleAt: '2026-11-23T08:10:00Z'
};

beforeEach(stubBrowserApis);
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('SPC dashboard', () => {
  it('loads datasets, requests a 7-day analysis and renders stats, charts and violations', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/spc/datasets')) return jsonResponse(datasets);
      if (url.startsWith('/api/spc/series')) return jsonResponse(analysis);
      return jsonResponse({});
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<SpcDashboard open onClose={() => undefined} />);
    await flush();

    expect(await screen.findByText(/主控制图 · 子组均值/)).toBeInTheDocument();
    expect(screen.getByText(/波动图 · 子组极差/)).toBeInTheDocument();
    expect(screen.getByText('样本数')).toBeInTheDocument();
    expect(screen.getByText('子组数')).toBeInTheDocument();
    expect(screen.getByText('规则 1 · 超出 3σ')).toBeInTheDocument();
    expect(screen.getByText('#3 – #3')).toBeInTheDocument();
    expect(screen.getByText(/读取 9 条记录，采用 9 条，忽略 0 条/)).toBeInTheDocument();
    expect(screen.queryByText('SPC 分析失败')).not.toBeInTheDocument();

    const seriesUrl = fetchMock.mock.calls.map((call) => String(call[0])).find((url) => url.startsWith('/api/spc/series'));
    expect(seriesUrl).toContain('dataset=thickness');
    expect(seriesUrl).toContain('subgroupSize=5');
    expect(seriesUrl).toContain('grouping=sequential');
    expect(seriesUrl).toContain('limit=5000');
    expect(seriesUrl).toContain('from=');
    expect(seriesUrl).not.toContain('chartType=');
  });

  it('switches to individuals and sends chartType=individuals with subgroup size 1', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/spc/datasets')) return jsonResponse(datasets);
      if (url.startsWith('/api/spc/series')) return jsonResponse({ ...analysis, chartType: 'individuals', subgroupSize: 1 });
      return jsonResponse({});
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<SpcDashboard open onClose={() => undefined} />);
    await flush();
    await screen.findByText(/主控制图 · 子组均值/);

    fireEvent.mouseDown(screen.getAllByRole('combobox')[1]);
    await flush();
    fireEvent.click(screen.getByText('单值 · I-MR', { selector: '.ant-select-item-option-content' }));
    await flush();

    const seriesUrls = fetchMock.mock.calls.map((call) => String(call[0])).filter((url) => url.startsWith('/api/spc/series'));
    expect(seriesUrls.at(-1)).toContain('chartType=individuals');
    expect(seriesUrls.at(-1)).toContain('subgroupSize=1');
  });

  it('shows the backend problem detail when the analysis request fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/spc/datasets')) return jsonResponse(datasets);
      if (url.startsWith('/api/spc/series')) return jsonResponse({ detail: 'lsl 必须小于 usl。', code: 'invalid_argument' }, 400);
      return jsonResponse({});
    }));

    render(<SpcDashboard open onClose={() => undefined} />);
    await flush();

    expect(await screen.findByText('SPC 分析失败')).toBeInTheDocument();
    expect(screen.getByText(/lsl 必须小于 usl。/)).toBeInTheDocument();
  });

  it('explains when no numeric dataset exists', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/spc/datasets')) return jsonResponse([]);
      return jsonResponse({});
    }));

    render(<SpcDashboard open onClose={() => undefined} />);
    await flush();

    expect(await screen.findByText(/还没有可用的数值结果数据集/)).toBeInTheDocument();
  });

  it('shows the dataset load error with a retryable refresh action', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ detail: '结果库不可读' }, 500)));

    render(<SpcDashboard open onClose={() => undefined} />);
    await flush();

    expect(await screen.findByText('SPC 数据集加载失败')).toBeInTheDocument();
    expect(screen.getByText('结果库不可读')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /刷\s*新/ })).toBeInTheDocument();
  });
});