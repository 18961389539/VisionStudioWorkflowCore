import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import InvestigationPanel from '../components/InvestigationPanel';
import { jsonResponse } from './fixtures/tuning';

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

const resolvedCase = {
  case: {
    id: 'case-1', signatureId: 'sig-1', category: 'Node', title: 'Caliper drift', status: 'Resolved', severity: 'High',
    workflowId: 'wf-1', workflowHash: 'hash', source: 'production', jobId: null, jobVersion: null,
    primaryNodeId: null, primaryNodeType: null, fingerprint: 'fp', representativeRunId: 'run-1',
    firstSeenAt: '2026-11-01T00:00:00Z', lastSeenAt: '2026-11-01T00:00:00Z', occurrenceCount: 1,
    reopenCount: 0, owner: null, rootCause: null, resolutionNote: null, verificationNote: null,
    verificationRunId: null, resolvedAt: '2026-11-01T01:00:00Z', verifiedAt: null, closedAt: null,
    createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-01T01:00:00Z'
  },
  evidenceRuns: [],
  verificationEvidence: []
};

const trends = {
  windowDays: 30, trackedCases: 0, activeCases: 0, verifiedOrClosedCases: 0,
  evidenceOccurrences: 0, timeline: [], topSignatures: []
};

beforeEach(stubBrowserApis);
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('investigation panel verification workspace', () => {
  it('shows a retryable error when validation runs fail instead of an empty list', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/investigations/for-trace/run-1') return jsonResponse(resolvedCase);
      if (url === '/api/investigations/trends?days=30&top=8') return jsonResponse(trends);
      if (url === '/api/validation/datasets') return jsonResponse([{ id: 'ds-1', name: 'Dataset A', itemCount: 3 }]);
      if (url === '/api/validation/datasets/ds-1/runs') return jsonResponse({ detail: 'dataset runs unavailable' }, 500);
      return jsonResponse({});
    }));

    render(<InvestigationPanel runId="run-1" />);
    await flush();

    fireEvent.mouseDown(screen.getAllByRole('combobox')[0]);
    await flush();
    fireEvent.click(screen.getByText('Dataset A (3)', { selector: '.ant-select-item-option-content' }));
    await flush();

    expect(await screen.findByText('验证运行加载失败')).toBeInTheDocument();
    expect(screen.getByText(/dataset runs unavailable/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /重\s*试/ })).toBeInTheDocument();
  });
});
