import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import RunObservabilityPanel from '../components/RunObservabilityPanel';
import type { RunObservabilitySnapshot } from '../types';

const snapshot: RunObservabilitySnapshot = {
  runId: 'run-1', timelineAvailable: true, timelineSpanMs: 4, runtimeOverheadMs: 1, peakConcurrency: 1, slowNodeCount: 0, baselineRunCount: 10,
  timeline: [{ runId: 'run-1', executionSequence: 1, nodeId: 'threshold', nodeType: 'image.threshold', phase: 'Run', success: true, startOffsetMs: 0, endOffsetMs: 4, durationMs: 4 }],
  baselines: [{ nodeId: 'threshold', nodeType: 'image.threshold', sampleCount: 10, currentDurationMs: 4, p50Ms: 3, p95Ms: 5, maxMs: 6, ratioToMedian: 1.33, status: 'Normal' }]
};
const response = (value: unknown) => ({ ok: true, json: async () => value }) as Response;
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn() })));
  vi.stubGlobal('fetch', vi.fn(async () => response(snapshot)));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('readable run observability', () => {
  it('prioritizes the node display name and Chinese status while explaining statistical terms', async () => {
    render(<RunObservabilityPanel runId="run-1" nodeLabels={{ threshold: '瓶盖分割' }} />);
    await screen.findByRole('table');
    expect(screen.getAllByText('瓶盖分割')).toHaveLength(2);
    expect(screen.getAllByText('正常')).toHaveLength(2);
    expect(screen.queryByText('Normal')).toBeNull();
    expect(screen.getByText('image.threshold').classList.contains('run-node-type')).toBe(true);
    fireEvent.focus(screen.getByLabelText('中位耗时（P50）说明'));
    expect((await screen.findByRole('tooltip')).textContent).toContain('一半的历史执行样本');
  });

  it('falls back to translated type names and shows insufficient samples clearly', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ ...snapshot, baselines: [{ ...snapshot.baselines[0], status: 'Insufficient', sampleCount: 0 }] })));
    render(<RunObservabilityPanel runId="run-1" />);
    await screen.findByRole('table');
    expect(screen.getAllByText('图像阈值分割')).toHaveLength(2);
    expect(screen.getAllByText('样本不足')).toHaveLength(2);
  });

  it('does not replace a newly selected run with a late old response', async () => {
    let release!: (value: Response) => void;
    vi.stubGlobal('fetch', vi.fn().mockImplementationOnce(() => new Promise<Response>((resolve) => { release = resolve; }))
      .mockImplementationOnce(async () => response({ ...snapshot, runId: 'run-2', timelineSpanMs: 999 })));
    const view = render(<RunObservabilityPanel runId="run-1" />);
    view.rerender(<RunObservabilityPanel runId="run-2" />);
    await screen.findByRole('table');
    await act(async () => { release(response(snapshot)); });
    await waitFor(() => expect(screen.getByText('999')).toBeTruthy());
  });
});
