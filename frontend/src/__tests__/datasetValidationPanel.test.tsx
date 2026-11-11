import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import DatasetValidationPanel from '../components/DatasetValidationPanel';
import { deferred, jsonResponse, tuningDataset, tuningRun } from './fixtures/tuning';

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

beforeEach(stubBrowserApis);
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('dataset validation panel', () => {
  it('切换数据集后忽略迟到的旧明细响应', async () => {
    const detailA = deferred<Response>();
    let signal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/validation/datasets') return Promise.resolve(jsonResponse([tuningDataset('A').dataset, tuningDataset('B').dataset]));
      if (url === '/api/traces?take=500') return Promise.resolve(jsonResponse([]));
      if (url === '/api/media/items?take=500') return Promise.resolve(jsonResponse([]));
      if (url === '/api/validation/datasets/A') { signal = init?.signal ?? undefined; return detailA.promise; }
      if (url === '/api/validation/datasets/A/runs') return Promise.resolve(jsonResponse([]));
      if (url === '/api/validation/datasets/B') return Promise.resolve(jsonResponse(tuningDataset('B')));
      if (url === '/api/validation/datasets/B/runs') return Promise.resolve(jsonResponse([]));
      return Promise.resolve(jsonResponse({}));
    }));

    render(<DatasetValidationPanel open onClose={() => undefined} />);
    await flush();

    fireEvent.mouseDown(screen.getAllByRole('combobox')[0]);
    await flush();
    fireEvent.click(screen.getByText('Dataset B (1)', { selector: '.ant-select-item-option-content' }));
    await flush();

    expect(await screen.findByText('B-sample')).toBeInTheDocument();
    expect(signal?.aborted).toBe(true);

    await act(async () => { detailA.resolve(jsonResponse(tuningDataset('A'))); });
    await flush();

    expect(screen.getByText('B-sample')).toBeInTheDocument();
    expect(screen.queryByText('A-sample')).toBeNull();
  });

  it('快速切换运行记录时旧结果不能覆盖新选择', async () => {
    const runA = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/validation/datasets') return Promise.resolve(jsonResponse([tuningDataset('A').dataset]));
      if (url === '/api/traces?take=500') return Promise.resolve(jsonResponse([]));
      if (url === '/api/media/items?take=500') return Promise.resolve(jsonResponse([]));
      if (url === '/api/validation/datasets/A') return Promise.resolve(jsonResponse(tuningDataset('A')));
      if (url === '/api/validation/datasets/A/runs') return Promise.resolve(jsonResponse([tuningRun('A', 'run-A'), tuningRun('A', 'run-B')]));
      if (url === '/api/validation/runs/run-A?includeResults=true') return runA.promise;
      if (url === '/api/validation/runs/run-B?includeResults=true') return Promise.resolve(jsonResponse(tuningRun('A', 'run-B')));
      return Promise.resolve(jsonResponse({}));
    }));

    render(<DatasetValidationPanel open onClose={() => undefined} />);
    await flush();

    const runsCard = screen.getByText('验证运行记录').closest('.ant-card')!;
    const rows = within(runsCard as HTMLElement).getAllByRole('row');
    expect(rows.length).toBeGreaterThanOrEqual(3);
    fireEvent.click(rows[2]);
    await flush();

    expect(await screen.findByText(/结果 · run-B/)).toBeInTheDocument();

    await act(async () => { runA.resolve(jsonResponse(tuningRun('A', 'run-A'))); });
    await flush();

    expect(screen.getByText(/结果 · run-B/)).toBeInTheDocument();
    expect(screen.queryByText(/结果 · run-A/)).toBeNull();
  });
});
