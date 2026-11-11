import { describe, expect, it } from 'vitest';
import { formatScalar, pickSummaryKeys, summarizeScalars } from '../summaryText';

// 摘要主键表：footer 只显示前 3 个标量，所以"哪些字段优先"决定了用户能不能看见截断。
describe('summary primary keys', () => {
  it('puts blob count, truncation and emitted area first', () => {
    const summary = { mode: 'all', count: 20, inputCount: 37, truncated: true, totalArea: 1234, roi: 'FullImage' };
    expect(pickSummaryKeys(summary, 'measure.blobs')).toEqual(['count', 'truncated', 'totalArea']);
  });

  it('deprioritizes meta keys instead of letting them take a slot', () => {
    const summary = { roi: 'FullImage', alpha: 1, beta: 2, gamma: 3 };
    expect(pickSummaryKeys(summary, 'unknown.node')).toEqual(['alpha', 'beta', 'gamma']);
  });

  it('skips primary keys the node did not emit', () => {
    const summary = { count: 3, totalArea: 90 };
    expect(pickSummaryKeys(summary, 'measure.blobs')).toEqual(['count', 'totalArea']);
  });

  it('renders the truncation flag inline', () => {
    expect(summarizeScalars({ count: 20, truncated: true, totalArea: 12.3456 }, 3)).toContain('truncated');
    expect(formatScalar(true)).toBe('true');
    expect(formatScalar(12.3456)).toBe('12.346');
  });
});
