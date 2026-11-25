import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import RunPanel from '../components/RunPanel';
import type { RunResult } from '../types';

beforeEach(() => {
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function failedRun(error?: string): RunResult {
  return {
    runId: 'run-1',
    success: false,
    totalDurationMs: 12,
    previewAvailable: false,
    nodeReports: [{
      nodeId: 'camera',
      nodeType: 'image.capture',
      success: false,
      durationMs: 12,
      summary: {},
      error
    }]
  };
}

describe('RunPanel failure details', () => {
  it('shows the failed node error inline without requiring a hover', () => {
    render(<RunPanel result={failedRun('相机连接超时')} />);

    expect(screen.getByRole('columnheader', { name: '失败详情' })).toBeTruthy();
    expect(screen.getByText('相机连接超时')).toBeTruthy();
  });

  it('shows a useful fallback when the failed node has no error message', () => {
    render(<RunPanel result={failedRun()} />);

    expect(screen.getByText('未提供错误详情')).toBeTruthy();
  });
});
