import { describe, expect, it } from 'vitest';
import {
  buildStatsParams, buildTraceParams, describeRange,
  effectiveRange, localDateTimeToIso, presetRange, toLocalDateTimeInput, validateTraceFilter, type TraceFilterState,
} from '../traceQuery';

const NOW = new Date('2026-10-11T15:30:00+08:00');
const DAY_MS = 86_400_000;

/** 与 presetRange 的契约一致：按运行器本地日历取零点，再序列化成 UTC ISO。固定 +08 时间戳只在东八区机器上成立。 */
function startOfLocalDay(value: Date): Date {
  const copy = new Date(value);
  copy.setHours(0, 0, 0, 0);
  return copy;
}

describe('presetRange', () => {
  it('最近一小时返回一小时前的 UTC ISO 起点', () => {
    const range = presetRange({ preset: 'hour' }, NOW);
    expect(range.from).toBe('2026-10-11T06:30:00.000Z');
    expect(range.to).toBeUndefined();
  });

  it('今天返回本地零点的 UTC 起点', () => {
    const range = presetRange({ preset: 'today' }, NOW);
    const localMidnight = startOfLocalDay(NOW);
    expect(range.from).toBe(localMidnight.toISOString());
    expect(new Date(range.from!).getHours()).toBe(0);
    expect(new Date(range.from!).getMinutes()).toBe(0);
    expect(new Date(range.from!).getSeconds()).toBe(0);
    expect(new Date(range.from!).getMilliseconds()).toBe(0);
    expect(range.to).toBeUndefined();
  });

  it('昨天返回昨天零点到今天零点前一毫秒（含 <= 边界）', () => {
    const range = presetRange({ preset: 'yesterday' }, NOW);
    const todayStart = startOfLocalDay(NOW);
    const yesterdayStart = startOfLocalDay(new Date(NOW.getTime() - DAY_MS));
    expect(range.from).toBe(yesterdayStart.toISOString());
    expect(range.to).toBe(new Date(todayStart.getTime() - 1).toISOString());
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
    expect(range.from).toBe(startOfLocalDay(NOW).toISOString());
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
    expect(params.get('from')).toBe(startOfLocalDay(NOW).toISOString());
    expect(params.has('errorCode')).toBe(false);
    expect(params.has('workflow')).toBe(false);
  });

  it('runIdPrefix 直查时不带时间参数', () => {
    const params = buildTraceParams({ preset: 'today', runIdPrefix: '3f2a' }, 0, 50);
    expect(params.get('runIdPrefix')).toBe('3f2a');
    expect(params.has('from')).toBe(false);
    expect(params.has('to')).toBe(false);
  });

  it('列表默认不携带逐节点报告，可显式恢复', () => {
    // 性能（2026-10-26）：一页 50 条记录各带几十份 NodeReports 是响应体大头，界面只用选中那条。
    expect(buildTraceParams({ preset: 'today' }, 0, 50).get('includeReports')).toBe('false');
    expect(buildTraceParams({ preset: 'today' }, 0, 50, new Date(), true).get('includeReports')).toBe('true');
  });

  it('jobVersion 仅在有值时携带', () => {
    const withVersion = buildTraceParams({ preset: 'today', jobVersion: 3 }, 0, 10);
    expect(withVersion.get('jobVersion')).toBe('3');
    const withoutVersion = buildTraceParams({ preset: 'today', jobVersion: null }, 0, 10);
    expect(withoutVersion.has('jobVersion')).toBe(false);
  });
});

describe('buildStatsParams 与列表口径一致', () => {
  it('空筛选显式查询全部历史，避免后端回落到近 7 天', () => {
    expect(buildStatsParams({ preset: 'custom' }).get('allTime')).toBe('true');
    expect(buildStatsParams({ preset: 'custom', workflow: '  ' }).get('allTime')).toBe('true');
    expect(buildStatsParams({ preset: 'today' }).has('allTime')).toBe(false);
  });

  it('统计参数与列表参数的筛选部分完全相同（仅少分页与列表专属的体积开关）', () => {
    const filter: TraceFilterState = { preset: 'week', disposition: 'NG', workflow: '压装' };
    const list = buildTraceParams(filter, 40, 20, NOW);
    const stats = buildStatsParams(filter, NOW);
    for (const [key, value] of list.entries()) {
      // includeReports 只影响列表响应体积，与统计口径无关
      if (key === 'offset' || key === 'limit' || key === 'includeReports') {
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

describe('自定义时间输入', () => {
  it('本地日期时间与 UTC ISO 往返一致，不发生时区偏移', () => {
    const input = '2026-10-20T18:35';
    const iso = localDateTimeToIso(input);
    expect(iso).toBe(new Date(2026, 9, 20, 18, 35).toISOString());
    expect(toLocalDateTimeInput(iso)).toBe(input);
  });

  it('空值和无效输入安全处理', () => {
    expect(localDateTimeToIso('')).toBeUndefined();
    expect(localDateTimeToIso('invalid')).toBeUndefined();
    expect(toLocalDateTimeInput()).toBe('');
    expect(toLocalDateTimeInput('invalid')).toBe('');
  });

  it('拒绝倒置和无效时间，允许相等及单侧时间窗', () => {
    expect(validateTraceFilter({ preset: 'custom', customFrom: 'bad' })).toContain('有效');
    expect(validateTraceFilter({ preset: 'custom', customFrom: '2026-10-21', customTo: '2026-10-20' })).toContain('开始时间');
    expect(validateTraceFilter({ preset: 'custom', customFrom: '2026-10-20', customTo: '2026-10-20' })).toBeNull();
    expect(validateTraceFilter({ preset: 'custom', customTo: '2026-10-20' })).toBeNull();
    expect(validateTraceFilter({ preset: 'custom', customFrom: '2026-10-20' })).toBeNull();
  });

  it('ID 直查和非自定义预设不受遗留自定义字段影响', () => {
    expect(validateTraceFilter({ preset: 'custom', runIdPrefix: 'run-a', customFrom: 'bad' })).toBeNull();
    expect(validateTraceFilter({ preset: 'today', customFrom: 'bad' })).toBeNull();
  });
});

describe('按本地日历计算日期预设', () => {
  it.each([new Date(2026, 2, 9, 0, 30), new Date(2026, 10, 2, 23, 30)])('夏令时切换前后仍取正确的日历日期：%s', (now) => {
    const yesterday = new Date(now);
    yesterday.setDate(yesterday.getDate() - 1);
    yesterday.setHours(0, 0, 0, 0);
    expect(presetRange({ preset: 'yesterday' }, now).from).toBe(yesterday.toISOString());
    const weekStart = new Date(now);
    weekStart.setDate(weekStart.getDate() - 6);
    weekStart.setHours(0, 0, 0, 0);
    expect(presetRange({ preset: 'week' }, now).from).toBe(weekStart.toISOString());
  });
});
