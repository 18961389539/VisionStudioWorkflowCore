/**
 * SPC 控制图（自定义 SVG，2026-11-23）。
 *
 * 不引入图表库：只需要点、趋势线、中心线/控制限/规格限与违例高亮，
 * 自绘可以完全走主题令牌（深/浅两套），也避免给首屏分包加体积。
 * 坐标按 viewBox 等比缩放，宽度随容器变化；`<title>` 提供原生悬停提示，
 * 同时整个 SVG 带 `role="img"` 与汇总 aria-label。
 */
import type { SpcControlChart } from '../types';

const WIDTH = 960;
const HEIGHT = 260;
const PAD = { top: 20, right: 18, bottom: 30, left: 78 };
const INNER_WIDTH = WIDTH - PAD.left - PAD.right;
const INNER_HEIGHT = HEIGHT - PAD.top - PAD.bottom;

type Props = {
  chart: SpcControlChart;
  /** 主图才传规格限；波动图不画 LSL/USL。 */
  specLimits?: { lsl?: number | null; usl?: number | null };
  label: string;
  locale: string;
  emptyText: string;
};

const finite = (value: number | null | undefined): value is number =>
  typeof value === 'number' && Number.isFinite(value);

function formatTimestamp(timestamp: string, locale: string): string {
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) return timestamp;
  return date.toLocaleString(locale, { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function formatValue(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(3).replace(/0+$/, '').replace(/\.$/, '');
}

export default function SpcChart({ chart, specLimits, label, locale, emptyText }: Props) {
  const points = chart.points;
  if (points.length === 0) return <div className="spc-chart-empty">{emptyText}</div>;

  const values = [
    ...points.map((point) => point.value),
    chart.centerLine, chart.upperControlLimit, chart.lowerControlLimit,
    specLimits?.lsl ?? null, specLimits?.usl ?? null
  ].filter(finite);
  let min = Math.min(...values);
  let max = Math.max(...values);
  if (max - min < 1e-9) {
    // 常量过程（σ=0）或单点：给一个固定刻度，避免除零和所有点重叠在上下边缘。
    const span = Math.abs(max) > 1e-9 ? Math.abs(max) * 0.1 : 1;
    min -= span;
    max += span;
  } else {
    const padding = (max - min) * 0.08;
    min -= padding;
    max += padding;
  }

  const x = (position: number) => points.length === 1
    ? PAD.left + INNER_WIDTH / 2
    : PAD.left + (position / (points.length - 1)) * INNER_WIDTH;
  const y = (value: number) => PAD.top + ((max - value) / (max - min)) * INNER_HEIGHT;

  const violationPoints = new Set<number>();
  for (const violation of chart.violations) {
    for (let index = Math.max(0, violation.startIndex); index <= Math.min(points.length - 1, violation.endIndex); index++) {
      violationPoints.add(index);
    }
  }

  const ticks = Array.from({ length: 5 }, (_, index) => min + (index / 4) * (max - min));
  const first = points[0];
  const middle = points[Math.floor((points.length - 1) / 2)];
  const last = points[points.length - 1];
  const described = `${label} · ${points.length} 点 · ${chart.violations.length} 条规则违例`;

  const limitLine = (value: number | null | undefined, className: string, caption: string) => finite(value)
    ? <g key={caption}>
        <line className={className} x1={PAD.left} x2={PAD.left + INNER_WIDTH} y1={y(value)} y2={y(value)} />
        <text className="spc-chart-limit-label" x={PAD.left - 8} y={y(value) + 3} textAnchor="end">{caption} {formatValue(value)}</text>
      </g>
    : null;

  return (
    <svg className="spc-chart" viewBox={`0 0 ${WIDTH} ${HEIGHT}`} role="img" aria-label={described}>
      <title>{described}</title>
      {ticks.map((tick) => (
        <g key={tick}>
          <line className="spc-chart-gridline" x1={PAD.left} x2={PAD.left + INNER_WIDTH} y1={y(tick)} y2={y(tick)} />
          <text className="spc-chart-axis-text" x={PAD.left - 8} y={y(tick) + 3} textAnchor="end">{formatValue(tick)}</text>
        </g>
      ))}
      {limitLine(chart.upperControlLimit, 'spc-chart-line-limit', 'UCL')}
      {limitLine(chart.lowerControlLimit, 'spc-chart-line-limit', 'LCL')}
      {limitLine(chart.centerLine, 'spc-chart-line-center', 'CL')}
      {limitLine(specLimits?.usl, 'spc-chart-line-spec', 'USL')}
      {limitLine(specLimits?.lsl, 'spc-chart-line-spec', 'LSL')}
      <polyline className="spc-chart-trend" points={points.map((point, index) => `${x(index)},${y(point.value)}`).join(' ')} />
      {points.map((point, index) => {
        const className = point.beyondLimits
          ? 'spc-chart-point spc-chart-point-beyond'
          : violationPoints.has(index)
            ? 'spc-chart-point spc-chart-point-violation'
            : 'spc-chart-point';
        return <circle key={`${point.index}-${point.timestamp}`} className={className}
          cx={x(index)} cy={y(point.value)} r={point.beyondLimits ? 4 : violationPoints.has(index) ? 3.4 : 2.8}>
          <title>{`#${point.index + 1} · ${formatTimestamp(point.timestamp, locale)} · ${formatValue(point.value)}`}</title>
        </circle>;
      })}
      <text className="spc-chart-axis-text" x={x(0)} y={HEIGHT - 8} textAnchor="start">{formatTimestamp(first.timestamp, locale)}</text>
      {points.length > 2 && <text className="spc-chart-axis-text" x={x(Math.floor((points.length - 1) / 2))} y={HEIGHT - 8} textAnchor="middle">{formatTimestamp(middle.timestamp, locale)}</text>}
      {points.length > 1 && <text className="spc-chart-axis-text" x={x(points.length - 1)} y={HEIGHT - 8} textAnchor="end">{formatTimestamp(last.timestamp, locale)}</text>}
    </svg>
  );
}