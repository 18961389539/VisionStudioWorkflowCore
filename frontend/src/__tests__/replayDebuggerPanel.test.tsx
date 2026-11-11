import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ReplayDebuggerPanel from '../components/ReplayDebuggerPanel';
import type { OfflineReplayContext, OfflineReplayResult } from '../types';
import { deferred, jsonResponse } from './fixtures/tuning';

const flush = async () => { await act(async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); }); };

function replayContext(runId: string, nodeId: string): OfflineReplayContext {
  return {
    sourceRunId: runId,
    trace: {} as OfflineReplayContext['trace'],
    workflow: { id: `wf-${runId}`, name: runId, nodes: [{ id: nodeId, name: nodeId, type: 'image.synthetic' }], edges: [] },
    replayable: true,
    hasReplayInput: true,
    orderedNodeIds: [nodeId],
    executionModel: 'offline'
  };
}

function replayResult(runId: string, nodeId: string): OfflineReplayResult {
  return {
    runId: `replay-${runId}`, success: true, totalDurationMs: 4, previewAvailable: false, nodeReports: [],
    sourceRunId: runId, dispositionChanged: false, executionModel: 'offline',
    comparison: [{ nodeId, nodeType: 'image.synthetic', summaryChanged: false, status: 'Same' }]
  } as unknown as OfflineReplayResult;
}

beforeEach(() => {
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
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('replay debugger panel', () => {
  it('切换运行后忽略迟到的旧回放上下文', async () => {
    const contextA = deferred<Response>();
    let signal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/traces/run-A/replay/context') { signal = init?.signal ?? undefined; return contextA.promise; }
      if (url === '/api/traces/run-B/replay/context') return Promise.resolve(jsonResponse(replayContext('run-B', 'b-node')));
      return Promise.resolve(jsonResponse({}));
    }));

    const { rerender } = render(<ReplayDebuggerPanel open runId="run-A" onClose={() => undefined} />);
    await flush();
    rerender(<ReplayDebuggerPanel open runId="run-B" onClose={() => undefined} />);
    await flush();

    expect((await screen.findAllByText('b-node')).length).toBeGreaterThan(0);
    expect(signal?.aborted).toBe(true);

    await act(async () => { contextA.resolve(jsonResponse(replayContext('run-A', 'a-node'))); });
    await flush();

    expect(screen.getAllByText('b-node').length).toBeGreaterThan(0);
    expect(screen.queryAllByText('a-node')).toHaveLength(0);
  });

  it('切换运行后迟到的回放结果不能显示在新运行下', async () => {
    const pendingReplay = deferred<Response>();
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/traces/run-A/replay/context') return Promise.resolve(jsonResponse(replayContext('run-A', 'a-node')));
      if (url === '/api/traces/run-B/replay/context') return Promise.resolve(jsonResponse(replayContext('run-B', 'b-node')));
      if (url === '/api/traces/run-A/replay' && init?.method === 'POST') return pendingReplay.promise;
      return Promise.resolve(jsonResponse({}));
    }));

    const { rerender } = render(<ReplayDebuggerPanel open runId="run-A" onClose={() => undefined} />);
    await flush();
    fireEvent.click(screen.getByRole('button', { name: '完整回放' }));
    await flush();

    rerender(<ReplayDebuggerPanel open runId="run-B" onClose={() => undefined} />);
    await flush();
    expect((await screen.findAllByText('b-node')).length).toBeGreaterThan(0);

    await act(async () => { pendingReplay.resolve(jsonResponse(replayResult('run-A', 'a-node'))); });
    await flush();

    expect(screen.queryByText('基线与回放对比')).toBeNull();
    expect(screen.getAllByText('b-node').length).toBeGreaterThan(0);
  });
});
