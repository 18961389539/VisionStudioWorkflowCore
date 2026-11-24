import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import SpcChart from '../components/SpcChart';
import type { SpcControlChart } from '../types';

const chart: SpcControlChart = {
  kind: 'xbar',
  label: '子组均值 (X̄)',
  centerLine: 10,
  upperControlLimit: 10.9,
  lowerControlLimit: 9.1,
  points: [
    { index: 0, value: 9.8, timestamp: '2026-11-23T08:00:00Z', beyondLimits: false },
    { index: 1, value: 10.1, timestamp: '2026-11-23T08:05:00Z', beyondLimits: false },
    { index: 2, value: 11.2, timestamp: '2026-11-23T08:10:00Z', beyondLimits: true }
  ],
  violations: [{ rule: 'rule1', startIndex: 2, endIndex: 2, description: '第 3 点超出控制上限。' }]
};

afterEach(cleanup);

describe('SpcChart', () => {
  it('draws trend, limits, spec lines and marks beyond-limit points', () => {
    const { container } = render(<SpcChart chart={chart} label="子组均值 (X̄)" locale="zh-CN" emptyText="无数据"
      specLimits={{ lsl: 8, usl: 12 }} />);

    const svg = screen.getByRole('img', { name: /子组均值 \(X̄\) · 3 点 · 1 条规则违例/ });
    expect(svg).toBeInTheDocument();
    const points = container.querySelectorAll('circle.spc-chart-point');
    expect(points).toHaveLength(3);
    expect(points[2]).toHaveClass('spc-chart-point-beyond');
    expect(container.querySelectorAll('.spc-chart-line-limit')).toHaveLength(2);
    expect(container.querySelectorAll('.spc-chart-line-spec')).toHaveLength(2);
    expect(container.querySelector('.spc-chart-line-center')).toBeInTheDocument();
    expect(container.querySelectorAll('.spc-chart-gridline')).toHaveLength(5);
    for (const point of container.querySelectorAll('circle')) {
      expect(point.getAttribute('cx')).not.toContain('NaN');
      expect(point.getAttribute('cy')).not.toContain('NaN');
    }
  });

  it('rings every point covered by a multi-point rule span', () => {
    const ruleChart: SpcControlChart = {
      ...chart,
      points: chart.points.map((point) => ({ ...point, beyondLimits: false })),
      violations: [{ rule: 'rule4', startIndex: 0, endIndex: 2, description: '连续 8 点位于中心线同一侧。' }]
    };
    const { container } = render(<SpcChart chart={ruleChart} label="子组均值 (X̄)" locale="zh-CN" emptyText="无数据" />);
    expect(container.querySelectorAll('.spc-chart-point-violation')).toHaveLength(3);
    expect(container.querySelectorAll('.spc-chart-point-beyond')).toHaveLength(0);
  });

  it('renders a single point without limits and keeps finite coordinates', () => {
    const single: SpcControlChart = {
      ...chart,
      centerLine: 10, upperControlLimit: null, lowerControlLimit: null,
      points: [{ index: 0, value: 10, timestamp: '2026-11-23T08:00:00Z', beyondLimits: false }],
      violations: []
    };
    const { container } = render(<SpcChart chart={single} label="单值 (I)" locale="zh-CN" emptyText="无数据" />);
    const point = container.querySelector('circle.spc-chart-point');
    expect(point?.getAttribute('cx')).toBe('510');
    expect(point?.getAttribute('cy')).toBe('125');
    expect(container.querySelector('.spc-chart-line-center')).toBeInTheDocument();
  });

  it('shows the empty placeholder when no points exist', () => {
    render(<SpcChart chart={{ ...chart, points: [], violations: [] }} label="子组均值 (X̄)" locale="zh-CN" emptyText="暂无控制图数据" />);
    expect(screen.getByText('暂无控制图数据')).toBeInTheDocument();
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });
});