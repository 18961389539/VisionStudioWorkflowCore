import { useState } from 'react';
import { Alert, Button, InputNumber, Tag } from 'antd';
import { caliperSpokeStatus, type CaliperDiagnosticManifest, type CaliperSpokeProfile } from '../caliperCircleDiagnostics';

const number = (value: number | null | undefined, digits = 2) => value == null ? '—' : Number(value.toFixed(digits)).toString();
const pointColor = (status: string) => status === 'used' ? '#52c41a' : '#faad14';
const circleFailureReasons: Record<string, string> = {
  'insufficient-valid-calipers': '有效卡尺数量不足', 'fitted-radius-outside-tolerance': '拟合半径超出容差',
  'fit-error-exceeds-tolerance': '拟合误差超出容差', 'all-spokes-exceed-max-radius-deviation': '所有候选均超过半径偏差',
  'search-region-outside-image': '搜索区域位于图像外', 'search-region-clipped-too-short': '搜索区域裁切后过短',
  'too-few-edge-points': '边缘点少于 3 个', 'degenerate-circle-fit': '测点分布无法稳定拟合圆'
};

function ProfileCharts({ manifest, profile }: { manifest: CaliperDiagnosticManifest; profile: CaliperSpokeProfile }) {
  const [hover, setHover] = useState<number>();
  const left = 37, right = 344, top = 17, bottom = 103;
  const firstRadius = profile.startRadius;
  const lastRadius = firstRadius + Math.max(1, profile.profile.length - 1) * profile.spacing;
  const x = (radius: number) => left + (radius - firstRadius) / Math.max(1e-6, lastRadius - firstRadius) * (right - left);
  const differences = profile.profile.slice(1).map((value, index) => value === null || profile.profile[index] === null
    ? null : value - profile.profile[index]!);
  const amplitudeMax = Math.max(10, manifest.minContrast * 1.2,
    ...profile.candidates.map(candidate => Math.abs(candidate.amplitude) * 1.15),
    ...differences.map(value => Math.abs(value ?? 0) * 1.15));
  const grayY = (value: number) => bottom - value / 255 * (bottom - top);
  const responseY = (value: number) => (bottom + top) / 2 - value / amplitudeMax * (bottom - top) / 2;
  const path = (values: (number | null)[], offset: number, y: (value: number) => number) => {
    let gap = true;
    return values.map((value, index) => {
      if (value === null) { gap = true; return ''; }
      const command = `${gap ? 'M' : 'L'}${x(firstRadius + (index + offset) * profile.spacing).toFixed(2)},${y(value).toFixed(2)}`;
      gap = false;
      return command;
    }).join(' ');
  };
  const selected = manifest.spokes[profile.index];
  const hoverIndex = hover === undefined ? undefined : Math.min(profile.profile.length - 1, hover);
  const onHover = (event: React.PointerEvent<SVGSVGElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    const svgX = (event.clientX - rect.left) * 360 / rect.width;
    if (svgX < left || svgX > right) { setHover(undefined); return; }
    setHover(Math.round((svgX - left) / (right - left) * (profile.profile.length - 1)));
  };
  const markers = <>
    {manifest.nominalRadius >= firstRadius && manifest.nominalRadius <= lastRadius && <line
      x1={x(manifest.nominalRadius)} x2={x(manifest.nominalRadius)} y1={top} y2={bottom} stroke="#8093ad" strokeDasharray="2 3" />}
    {profile.edgeRadius != null && <line x1={x(profile.edgeRadius)} x2={x(profile.edgeRadius)} y1={top} y2={bottom}
      stroke={pointColor(selected.status)} strokeWidth={1.5} strokeDasharray="4 2" />}
    {hoverIndex !== undefined && <line x1={x(firstRadius + hoverIndex * profile.spacing)} x2={x(firstRadius + hoverIndex * profile.spacing)}
      y1={top} y2={bottom} stroke="currentColor" opacity={.6} />}
  </>;
  const axes = <>
    <path d={`M${left},${top}V${bottom}H${right}`} fill="none" stroke="currentColor" opacity={.3} />
    {[0, .5, 1].map(part => <text key={part} x={left + part * (right - left)} y={119} textAnchor="middle">
      {number(firstRadius + part * (lastRadius - firstRadius), 1)}
    </text>)}
    <text x={right} y={134} textAnchor="end">距圆心半径 / px · 内 → 外</text>
  </>;
  const invalidBands = profile.profile.map((value, index) => value !== null ? null : <rect key={index}
    x={Math.max(left, x(firstRadius + (index - .5) * profile.spacing))} y={top}
    width={Math.min(right, x(firstRadius + (index + .5) * profile.spacing)) - Math.max(left, x(firstRadius + (index - .5) * profile.spacing))}
    height={bottom - top} fill="currentColor" opacity={.09} />);

  return <div className="caliper-diagnostic-charts">
    <div className="caliper-diagnostic-chart">
      <strong>灰度剖面</strong>
      <svg viewBox="0 0 360 140" role="img" aria-label="所选卡尺灰度剖面，横轴为距圆心半径，灰色区域为无效采样"
        onPointerMove={onHover} onPointerLeave={() => setHover(undefined)}>
        {invalidBands}{axes}{markers}
        {[0, 128, 255].map(value => <g key={value}><line x1={left} x2={right} y1={grayY(value)} y2={grayY(value)} stroke="currentColor" opacity={.08} />
          <text x={left - 5} y={grayY(value) + 3} textAnchor="end">{value}</text></g>)}
        <path d={path(profile.profile, 0, grayY)} fill="none" stroke="#36a9ff" strokeWidth={1.5} />
      </svg>
    </div>
    <div className="caliper-diagnostic-chart">
      <strong>梯度与边缘响应</strong>
      <svg viewBox="0 0 360 140" role="img" aria-label="相邻灰度差、算法候选响应与对比度阈值"
        onPointerMove={onHover} onPointerLeave={() => setHover(undefined)}>
        {invalidBands}{axes}{markers}
        <line x1={left} x2={right} y1={responseY(0)} y2={responseY(0)} stroke="currentColor" opacity={.2} />
        {(manifest.minContrast > 0 ? [-1, 1] : [1]).map(sign => <g key={sign}>
          <line x1={left} x2={right} y1={responseY(sign * manifest.minContrast)} y2={responseY(sign * manifest.minContrast)}
            stroke="#fa8c16" strokeDasharray="4 3" opacity={manifest.polarity === (sign === 1 ? 'falling' : 'rising') ? .2 : .8} />
          <text x={left - 5} y={responseY(sign * manifest.minContrast) + (sign === 1 ? -4 : 11)} textAnchor="end">{manifest.minContrast === 0 ? '' : sign === 1 ? '+' : '−'}{number(manifest.minContrast, 1)}</text>
        </g>)}
        <path d={path(differences, .5, responseY)} fill="none" stroke="#8093ad" strokeWidth={1.2} opacity={.7} />
        {profile.candidates.map((candidate, index) => {
          const radius = firstRadius + candidate.position;
          const chosen = profile.edgeRadius != null && Math.abs(radius - profile.edgeRadius) < 1e-6;
          const color = chosen ? pointColor(selected.status) : '#a78bfa';
          return <g key={index}>
            <line x1={x(radius)} x2={x(radius)} y1={responseY(0)} y2={responseY(candidate.amplitude)} stroke={color} />
            <circle cx={x(radius)} cy={responseY(candidate.amplitude)} r={chosen ? 3.5 : 2.5} fill={color}>
              <title>{chosen ? '选中边缘' : '候选边缘'}：半径 {number(radius, 3)} px，响应 {number(candidate.amplitude, 2)}</title>
            </circle>
          </g>;
        })}
      </svg>
    </div>
    <output aria-label="剖面游标读数" className="caliper-diagnostic-cursor">
      {hoverIndex === undefined ? '移到曲线上查看半径、灰度与相邻灰度差。'
        : `R ${number(firstRadius + hoverIndex * profile.spacing, 3)} px · 灰度 ${number(profile.profile[hoverIndex], 2)} · 相邻差 ${number(differences[hoverIndex - 1], 2)}`}
    </output>
    <details className="caliper-diagnostic-explanation"><summary>图例与算法说明</summary>
      <p>蓝线：投影灰度；浅灰线：相邻灰度差（参考）；紫点：算法实际候选；绿/黄点与竖线：选中边缘；橙虚线：对比度阈值；灰色区：无效采样。</p>
      <p>阈值作用于算法实际响应。长剖面使用高斯滤波，短连续段使用局部梯度，相邻灰度差仅供观察变化。</p>
    </details>
  </div>;
}

export default function CaliperCircleDiagnosticsPanel({ manifest, profile, index, available, loading, error, stale,
  busy, onSelect, onRetry, onRun, runDisabled }: {
  manifest?: CaliperDiagnosticManifest; profile?: CaliperSpokeProfile; index: number; available: boolean;
  loading: boolean; error?: string; stale: boolean; busy: boolean; onSelect: (index: number) => void;
  onRetry: () => void; onRun: () => void; runDisabled: boolean;
}) {
  const selected = manifest?.spokes[index];
  const status = selected ? caliperSpokeStatus[selected.status] ?? selected.status : '';
  const abnormalCount = manifest?.spokes.filter(spoke => spoke.status !== 'used').length ?? 0;
  const nextAbnormal = () => {
    if (!manifest) return;
    for (let offset = 1; offset <= manifest.spokes.length; offset++) {
      const next = (index + offset) % manifest.spokes.length;
      if (manifest.spokes[next].status !== 'used') { onSelect(next); return; }
    }
  };
  return <section className="caliper-circle-diagnostics" aria-label="单卡尺诊断">
    <div className="caliper-diagnostic-heading"><strong>单卡尺诊断</strong>
      <Button size="small" loading={busy} disabled={runDisabled} onClick={onRun}>运行并更新诊断</Button>
    </div>
    {stale && <Alert type="warning" showIcon title="诊断已过期 · 当前参数待验证" description="图像、卡尺和曲线均保留上次运行快照。运行节点后更新。" />}
    {!available && <p>请运行此节点以采集诊断；诊断包括实际输入图像、ROI 和坐标变换后的采样结果。</p>}
    {error && <div role="alert" className="caliper-diagnostic-error">{error}<Button size="small" onClick={onRetry}>重试读取</Button></div>}
    {loading && <p role="status">正在读取{manifest ? '卡尺剖面' : '卡尺诊断'}…</p>}
    {manifest && selected && <>
      <div className="caliper-diagnostic-selector" role="group" aria-label="选择诊断卡尺">
        <Button size="small" aria-label="上一条卡尺" disabled={index === 0} onClick={() => onSelect(index - 1)}>‹</Button>
        <InputNumber size="small" aria-label="诊断卡尺序号" min={1} max={manifest.spokes.length} precision={0}
          value={index + 1} onChange={value => { if (value !== null) onSelect(value - 1); }} />
        <span>/ {manifest.spokes.length} 条</span>
        <Button size="small" aria-label="下一条卡尺" disabled={index === manifest.spokes.length - 1} onClick={() => onSelect(index + 1)}>›</Button>
        <Button size="small" disabled={abnormalCount === 0} onClick={nextAbnormal}>下一异常 ({abnormalCount})</Button>
      </div>
      <div className="caliper-diagnostic-summary" aria-live="polite">
        <Tag color={selected.status === 'used' ? 'green' : 'gold'}>{status}</Tag>
        <span>#{index + 1} · {number(selected.angleDeg)}°{selected.clipped ? ' · 已按图像边界裁切' : ''}</span>
      </div>
      {profile && profile.profile.length > 0 && <ProfileCharts key={`${profile.runId}:${profile.executionSequence}:${profile.index}`} manifest={manifest} profile={profile} />}
      {profile && profile.profile.length === 0 && <p>此卡尺没有可绘制的剖面。{status}。</p>}
      <dl>
        <div><dt>有效采样</dt><dd>{selected.validSampleCount} / {selected.sampleCount}</dd></div>
        <div><dt>合格候选</dt><dd>{selected.candidateCount} 个</dd></div>
        <div><dt>选中半径</dt><dd>{number(selected.edgeRadius, 3)} px</dd></div>
        <div><dt>半径偏差</dt><dd>{number(selected.edgeRadius == null ? null : selected.edgeRadius - manifest.nominalRadius, 3)} px</dd></div>
        <div><dt>边缘强度 / 阈值</dt><dd>{number(selected.edgeStrength)} / {number(manifest.minContrast)}</dd></div>
        <div><dt>极性 / 选边</dt><dd>{{ any: '任意', rising: '暗→亮', falling: '亮→暗' }[manifest.polarity] ?? manifest.polarity} / {{ strongest: '最强', firstfromstart: '内→外', firstfromend: '外→内' }[manifest.searchDirection] ?? manifest.searchDirection}</dd></div>
      </dl>
      {!manifest.found && <p className="caliper-diagnostic-quality-note">整体未通过：{circleFailureReasons[manifest.reason] ?? caliperSpokeStatus[manifest.reason] ?? manifest.reason}。单条卡尺参与拟合仍不代表整体测量合格。</p>}
      <p>点击图像中的径向卡尺可切换；空白处拖动平移，滚轮缩放。</p>
      <small className="caliper-diagnostic-provenance">运行 {manifest.runId} · 序列 {manifest.executionSequence} · 投影 {manifest.sampleWidth} px</small>
    </>}
  </section>;
}
