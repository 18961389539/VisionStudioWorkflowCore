import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import DatasetValidationPanel from '../components/DatasetValidationPanel';
import { deferred, jsonResponse, tuningDataset, tuningRun, tuningWorkflow } from './fixtures/tuning';

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
  it('保留明细加载期间手动选择的来源追踪', async () => {
    const detail = deferred<Response>();
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/validation/datasets') return Promise.resolve(jsonResponse([tuningDataset('A').dataset]));
      if (url === '/api/traces?take=500') return Promise.resolve(jsonResponse([
        { runId: 'manual-trace', workflowName: 'Manual', hasReplayInput: true, nodeReports: [] },
      ]));
      if (url === '/api/media/items?take=500') return Promise.resolve(jsonResponse([]));
      if (url === '/api/validation/datasets/A') return detail.promise;
      if (url === '/api/validation/datasets/A/runs') return Promise.resolve(jsonResponse([]));
      return Promise.resolve(jsonResponse(tuningWorkflow));
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<DatasetValidationPanel open onClose={() => undefined} />);
    await flush();
    const card = screen.getByText('候选工作流').closest('.ant-card') as HTMLElement;
    fireEvent.mouseDown(within(card).getByRole('combobox'));
    fireEvent.click(await screen.findByText('Manual · manual-t', { selector: '.ant-select-item-option-content' }));
    await act(async () => {
      const dataset = tuningDataset('A');
      detail.resolve(jsonResponse({ ...dataset, items: [{ ...dataset.items[0], sourceRunId: 'default-trace' }] }));
    });
    await flush();
    fireEvent.click(within(card).getByRole('button', { name: '加载追踪工作流' }));
    await flush();
    expect(fetchMock).toHaveBeenCalledWith('/api/traces/manual-trace/workflow', expect.anything());
  });

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

  it('runs the recipe snapshot and automatically links a completed passing validation', async () => {
    let started = false;
    let startBody: Record<string, unknown> | undefined;
    let linkBody: Record<string, unknown> | undefined;
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/validation/datasets') return jsonResponse([tuningDataset('A').dataset]);
      if (url === '/api/traces?take=500' || url === '/api/media/items?take=500') return jsonResponse([]);
      if (url === '/api/validation/datasets/A') return jsonResponse(tuningDataset('A'));
      if (url === '/api/validation/datasets/A/runs' && init?.method === 'POST') {
        started = true;
        startBody = JSON.parse(String(init.body));
        return jsonResponse({ runId: 'recipe-run' });
      }
      if (url === '/api/validation/datasets/A/runs') return jsonResponse(started ? [tuningRun('A', 'recipe-run')] : []);
      if (url === '/api/validation/runs/recipe-run?includeResults=true') return jsonResponse(tuningRun('A', 'recipe-run'));
      if (url === '/api/jobs/recipe-a/versions/2/validation' && init?.method === 'POST') {
        linkBody = JSON.parse(String(init.body));
        return jsonResponse({ accepted: true });
      }
      return jsonResponse({});
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<DatasetValidationPanel open onClose={() => undefined} initialWorkflow={tuningWorkflow}
      autoLinkRecipe={{ jobId: 'recipe-a', version: 2 }} />);
    await screen.findByText('A-sample');
    expect(screen.getByPlaceholderText('加载来源工作流后，可编辑参数或 ROI JSON。留空时将直接使用来源工作流。'))
      .toHaveValue(JSON.stringify(tuningWorkflow, null, 2));
    fireEvent.click(screen.getByRole('button', { name: '运行数据集验证' }));

    await waitFor(() => expect(linkBody).toEqual({ validationRunId: 'recipe-run' }));
    expect(startBody).toEqual({ sourceWorkflowRunId: 'designer-current', workflow: tuningWorkflow });
    expect(await screen.findByText('V2 验证通过并已自动关联')).toBeInTheDocument();
  });
});
