import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import NodeExecutionResult from '../components/NodeExecutionResult';
import type { NodeCatalogItem, RunResult } from '../types';

const catalog: NodeCatalogItem = {
  type: 'match.template', displayName: '模板匹配', category: 'Matching', pluginId: 'builtin',
  inputs: [], outputs: [{ name: 'found', dataType: 'Boolean' }], parameters: []
};

function result(type: string, summary: Record<string, unknown>): RunResult {
  return {
    runId: 'run-1', success: true, totalDurationMs: 1, previewAvailable: false,
    nodeReports: [{ nodeId: 'n1', nodeType: type, success: true, durationMs: 1,
      summary: { found: true, ...summary },
      outputs: { found: { type: 'Boolean', display: 'true', value: true } } }]
  };
}

function mount(type: string, summary: Record<string, unknown>) {
  return render(<NodeExecutionResult nodeId="n1" catalog={{ ...catalog, type }} result={result(type, summary)}
    stale={false} expanded onExpandedChange={() => {}} />);
}

afterEach(cleanup);

describe('MatchingAutoTuneReport', () => {
  it('shows the NCC recommendation that this run actually used', () => {
    mount('match.template', {
      autoTuneNcc: {
        version: 'auto-v1', profile: 'balanced', pyramidLevels: 5, angleStepDeg: 2.83,
        warnings: ['roi-mask-not-cropped']
      }
    });

    const block = screen.getByLabelText('自动调参结果');
    expect(block.textContent).toContain('层数 5');
    expect(block.textContent).toContain('2.830°');
    expect(block.textContent).toContain('模板 ROI 只作掩膜未裁剪');
  });

  it('shows the legacy shape recommendation with quality and contrast reference', () => {
    mount('match.shape', {
      autoTuneShape: {
        version: 'auto-v1', profile: 'speed', pyramidLevels: 3, angleStepDeg: 5.657,
        contrast: 20, minContrast: 5, quality: 'good', warnings: ['profile-speed']
      }
    });

    const block = screen.getByLabelText('自动调参结果');
    expect(block.textContent).toContain('match.shape');
    expect(block.textContent).toContain('最小对比度 5');
    expect(block.textContent).toContain('对比度参考 20（不应用）');
    expect(block.textContent).toContain('良好');
    expect(block.textContent).toContain('速度取向');
  });

  it('renders nothing when the node ran with manual parameters', () => {
    mount('match.template', { pyramidLevels: 5 });

    expect(screen.queryByLabelText('自动调参结果')).toBeNull();
  });
});
