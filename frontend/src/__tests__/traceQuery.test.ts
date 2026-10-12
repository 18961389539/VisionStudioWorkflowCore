import { describe, expect, it } from 'vitest';
import {
  buildStatsParams, buildTraceParams, describeRange,
  effectiveRange, presetRange, type TraceFilterState,
} from '../traceQuery';

const NOW = new Date('2026-10-11T15:30:00+08:00');

describe('presetRange', () => {
  it('最近一小时返回一小时前的 UTC ISO 起点', () => {
    const range = presetRange({ preset: 'hour' }, NOW);
    expect(range.from).toBe('2026-10-11T06:30:00.000Z');
    expect(range.to).toBeUndefined();
  });

  it('今天返回本地零点的 UTC 起点', () => {
    const range = presetRange({ preset: 'today' }, NOW);
    expect(range.from).toBe('2026-10-10T16:00:00.000Z'); // 本地 2026-10-11 00:00 (+08:00)
    expect(range.to).toBeUndefined();
  });

  it('昨天返回昨天零点到今天零点前一毫秒（含 <= 边界）', () => {
    const range = presetRange({ preset: 'yesterday' }, NOW);
    expect(range.from).toBe('2026-10-09T16:00:00.000Z');
    expect(range.to).toBe('2026-10-10T15:59:59.999Z');
  });

  it('自定义时间窗原样透传', () => {
    const filter: TraceFilterState = {
      preset: 'custom',
      customFrom: '2026-10-01T00:00:00.000Z',
      customTo: '2026-10-02T00:00:00.000Z',
    };
    expect(presetRange(filter)).toEqual({ from: filter.customFrom, to: filter.customTo });
  });
});

describe('effectiveRange（运行 ID 直查不受时间窗限制）', () => {
  it('runIdPrefix 非空时忽略时间窗', () => {
    const range = effectiveRange({ preset: 'today', runIdPrefix: 'abc' }, NOW);
    expect(range).toEqual({});
  });

  it('runIdPrefix 为空白时回到预设时间窗', () => {
    const range = effectiveRange({ preset: 'today', runIdPrefix: '   ' }, NOW);
    expect(range.from).toBe('2026-10-10T16:00:00.000Z');
  });
});

describe('buildTraceParams', () => {
  it('包含分页、筛选与时间窗，空值与空白被剔除', () => {
    const params = buildTraceParams(
      { preset: 'today', jobId: ' job-1 ', executionStatus: 'Failed', errorCode: '', workflow: '  ' },
      100, 50, NOW,
    );
    expect(params.get('offset')).toBe('100');
    expect(params.get('limit')).toBe('50');
    expect(params.get('jobId')).toBe('job-1');
    expect(params.get('executionStatus')).toBe('Failed');
    expect(params.get('from')).toBe('2026-10-10T16:00:00.000Z');
    expect(params.has('errorCode')).toBe(false);
    expect(params.has('workflow')).toBe(false);
  });

  it('runIdPrefix 直查时不带时间参数', () => {
    const params = buildTraceParams({ preset: 'today', runIdPrefix: '3f2a' }, 0, 50);
    expect(params.get('runIdPrefix')).toBe('3f2a');
    expect(params.has('from')).toBe(false);
    expect(params.has('to')).toBe(false);
  });

  it('jobVersion 仅在有值时携带', () => {
    const withVersion = buildTraceParams({ preset: 'today', jobVersion: 3 }, 0, 10);
    expect(withVersion.get('jobVersion')).toBe('3');
    const withoutVersion = buildTraceParams({ preset: 'today', jobVersion: null }, 0, 10);
    expect(withoutVersion.has('jobVersion')).toBe(false);
  });
});

describe('buildStatsParams 与列表口径一致', () => {
  it('统计参数与列表参数的筛选部分完全相同（仅少分页）', () => {
    const filter: TraceFilterState = { preset: 'week', disposition: 'NG', workflow: '压装' };
    const list = buildTraceParams(filter, 40, 20, NOW);
    const stats = buildStatsParams(filter, NOW);
    for (const [key, value] of list.entries()) {
      if (key === 'offset' || key === 'limit') {
        expect(stats.has(key)).toBe(false);
      } else {
        expect(stats.get(key)).toBe(value);
      }
    }
  });
});

describe('describeRange', () => {
  it('预设时间窗带标签与具体边界', () => {
    const text = describeRange({ preset: 'today' }, NOW);
    expect(text).toContain('今天');
    expect(text).toContain('00:00:00');
  });

  it('运行 ID 直查模式说明不限时间', () => {
    expect(describeRange({ preset: 'today', runIdPrefix: 'abc' }, NOW)).toContain('不限时间');
  });
});
