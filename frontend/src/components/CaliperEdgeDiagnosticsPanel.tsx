import { useState } from 'react';
import { Alert, Button, Tag } from 'antd';
import { caliperEdgeReasons, caliperProfilePoint, type CaliperEdgeDiagnostic } from '../caliperEdgeDiagnostics';

const number = (value: number | null | undefined, digits = 2) => value == null || !Number.isFinite(value) ? '—' : Number(value.toFixed(digits)).toString();
const polarityLabels: Record<string, string> = { any: '任意', rising: '暗→亮', falling: '亮→暗' };
const directionLabels: Record<string, string> = { strongest: '最强', firstfromstart: '从起点', firstfromend: '从终点' };
const reasonHelp: Record<string, string> = {
  'search-line-outside-image': '将搜索线移入图像，或检查上游坐标变换。',
  'search-line-clipped-too-short': '延长图像内的搜索段，至少保留 2 px。',
  'profile-has-no-finite-samples': '检查 ROI 是否覆盖浅蓝采样带，以及 ROI 与图像是否在同一坐标系。',
  'profile-has-insufficient-contiguous-samples': 'ROI 缺口将采样分段；每段至少需要 3 个连续有效采样。',
  'no-edge-above-threshold': '核对搜索线是否穿过目标边缘，再检查极性、阈值和投影长度。'
};

function ProfileCharts({ data, inspectionPosition, onInspect }: {
  data: CaliperEdgeDiagnostic; inspectionPosition?: number; onInspect: (position?: number) => void;
}) {
  const left = 39, right = 344, top = 17, bottom = 103;
  const length = Math.max(data.spacing, (data.profile.length - 1) * data.spacing, 1e-6);
  const x = (position: number) => left + position / length * (right - left);
  const differences = data.profile.slice(1).map((value, index) => value === null || data.profile[index] === null
    ? null : value - data.profile[index]!);
  let amplitudeMax = Math.max(10, data.minContrast * 1.2);
  for (const candidate of data.candidates) amplitudeMax = Math.max(amplitudeMax, Math.abs(candidate.amplitude) * 1.15);
  for (const value of differences) amplitudeMax = Math.max(amplitudeMax, Math.abs(value ?? 0) * 1.15);
  const grayY = (value: number) => bottom - value / 255 * (bottom - top);
  const responseY = (value: number) => (bottom + top) / 2 - value / amplitudeMax * (bottom - top) / 2;
  // Keep extrema and gaps when a long search line has more samples than screen columns.
  const path = (values: (number | null)[], offset: number, y: (value: number) => number) => {
    const commands: string[] = [];
    const bucket = Math.max(1, Math.ceil(values.length / 1000));
    let gap = true;
    const add = (index: number) => {
      commands.push(`${gap ? 'M' : 'L'}${x((index + offset) * data.spacing).toFixed(2)},${y(values[index]!).toFixed(2)}`);
      gap = false;
    };
    for (let index = 0; index < values.length;) {
      if (values[index] === null) { gap = true; index++; continue; }
      const start = index;
      let minimum = start, maximum = start;
      while (index < values.length && index < start + bucket && values[index] !== null) {
        if (values[index]! < values[minimum]!) minimum = index;
        if (values[index]! > values[maximum]!) maximum = index;
        index++;
      }
      for (const position of [...new Set([start, minimum, maximum, index - 1])].sort((a, b) => a - b)) add(position);
    }
    return commands.join(' ');
  };
  const invalidBands: { start: number; end: number }[] = [];
  for (let index = 0; index < data.profile.length; index++) {
    if (data.profile[index] !== null) continue;
    const start = index;
    while (index + 1 < data.profile.length && data.profile[index + 1] === null) index++;
    invalidBands.push({ start, end: index });
  }
  const onHover = (event: React.PointerEvent<SVGSVGElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    if (!rect.width) return;
    const svgX = (event.clientX - rect.left) * 360 / rect.width;
    if (svgX < left || svgX > right) { onInspect(undefined); return; }
    const index = Math.round((svgX - left) / (right - left) * (data.profile.length - 1));
    onInspect(index * data.spacing);
  };
  const axes = <>
    <path d={`M${left},${top}V${bottom}H${right}`} fill="none" stroke="currentColor" opacity={.3} />
    {[0, .5, 1].map(part => <text key={part} x={left + part * (right - left)} y={119} textAnchor="middle">
      {number(data.startOffset + part * length, 1)}</text>)}
    <text x={right} y={134} textAnchor="end">距原始起点 / px · 起点 → 终点</text>
  </>;
  const markers = <>
    {invalidBands.map((band, index) => {
      const start = Math.max(left, x((band.start - .5) * data.spacing));
      return <rect key={index} x={start} y={top} width={Math.max(0, Math.min(right, x((band.end + .5) * data.spacing)) - start)}
        height={bottom - top} fill="currentColor" opacity={.1} />;
    })}
    {data.edgePosition != null && <line x1={x(data.edgePosition)} x2={x(data.edgePosition)} y1={top} y2={bottom}
      stroke="#52c41a" strokeWidth={1.5} strokeDasharray="4 2" />}
    {inspectionPosition !== undefined && <line x1={x(inspectionPosition)} x2={x(inspectionPosition)} y1={top} y2={bottom} stroke="#ffd666" />}
  </>;
  const hoverIndex = inspectionPosition === undefined ? undefined : Math.max(0, Math.min(data.profile.length - 1, Math.round(inspectionPosition / data.spacing)));
  const hoverPoint = inspectionPosition === undefined ? undefined : caliperProfilePoint(data, inspectionPosition);

  return <div className="caliper-diagnostic-charts">
    <div className="caliper-diagnostic-chart"><strong>灰度剖面</strong>
      <svg viewBox="0 0 360 140" role="img" aria-label="卡尺灰度剖面，横轴为距原始起点距离，灰色区域为无效采样"
        onPointerMove={onHover} onPointerLeave={() => onInspect(undefined)}>
        {axes}{markers}
        {[0, 128, 255].map(value => <g key={value}><line x1={left} x2={right} y1={grayY(value)} y2={grayY(value)} stroke="currentColor" opacity={.08} />
          <text x={left - 5} y={grayY(value) + 3} textAnchor="end">{value}</text></g>)}
        <path d={path(data.profile, 0, grayY)} fill="none" stroke="#36a9ff" strokeWidth={1.5} />
      </svg>
    </div>
    <div className="caliper-diagnostic-chart"><strong>梯度与实际候选</strong>
      <svg viewBox="0 0 360 140" role="img" aria-label="卡尺相邻灰度差、实际边缘候选与阈值"
        onPointerMove={onHover} onPointerLeave={() => onInspect(undefined)}>
        {axes}{markers}
        <line x1={left} x2={right} y1={responseY(0)} y2={responseY(0)} stroke="currentColor" opacity={.2} />
        {(data.minContrast > 0 ? [-1, 1] : [1]).map(sign => <g key={sign}>
          <line x1={left} x2={right} y1={responseY(sign * data.minContrast)} y2={responseY(sign * data.minContrast)} stroke="#fa8c16" strokeDasharray="4 3"
            opacity={data.polarity === (sign === 1 ? 'falling' : 'rising') ? .2 : .8} />
          <text x={left - 5} y={responseY(sign * data.minContrast) + (sign === 1 ? -4 : 11)} textAnchor="end">
            {data.minContrast === 0 ? '' : sign === 1 ? '+' : '−'}{number(data.minContrast, 1)}</text>
        </g>)}
        <path d={path(differences, .5, responseY)} fill="none" stroke="#8093ad" strokeWidth={1.2} opacity={.7} />
        {data.candidates.map((candidate, index) => <g key={index}>
          <line x1={x(candidate.position)} x2={x(candidate.position)} y1={responseY(0)} y2={responseY(candidate.amplitude)}
            stroke={index === data.selectedCandidateIndex ? '#52c41a' : '#a78bfa'} />
          <circle cx={x(candidate.position)} cy={responseY(candidate.amplitude)} r={index === data.selectedCandidateIndex ? 3.5 : 2.5}
            fill={index === data.selectedCandidateIndex ? '#52c41a' : '#a78bfa'}>
            <title>候选 #{index + 1}：距起点 {number(data.startOffset + candidate.position, 3)} px，响应 {number(candidate.amplitude)}</title>
          </circle>
        </g>)}
      </svg>
    </div>
    <output aria-label="卡尺剖面游标读数" className="caliper-diagnostic-cursor">
      {hoverIndex === undefined ? '移到曲线上查看灰度与图像位置；展开候选详情可定位候选。'
        : `距起点 ${number(data.startOffset + inspectionPosition!, 3)} px · (${number(hoverPoint?.x)}, ${number(hoverPoint?.y)}) · 灰度 ${number(data.profile[hoverIndex])} · 相邻差 ${number(differences[hoverIndex - 1])}`}
    </output>
  </div>;
}

export default function CaliperEdgeDiagnosticsPanel({ data, available, loading, error, stale, busy, runDisabled,
  inspectionPosition, onInspect, onRetry, onRun }: {
  data?: CaliperEdgeDiagnostic; available: boolean; loading: boolean; error?: string;
  stale: boolean; busy: boolean; runDisabled: boolean; inspectionPosition?: number;
  onInspect: (position?: number) => void; onRetry: () => void; onRun: () => void;
}) {
  const [page, setPage] = useState(0);
  const pageSize = 8, pageCount = Math.max(1, Math.ceil((data?.candidates.length ?? 0) / pageSize));
  const currentPage = Math.min(page, pageCount - 1);
  return <section className="caliper-circle-diagnostics caliper-edge-diagnostics" aria-label="边缘剖面诊断">
    <div className="caliper-diagnostic-heading"><strong>边缘剖面诊断</strong>
      <Button size="small" loading={busy} disabled={runDisabled} onClick={onRun}>运行并更新诊断</Button>
    </div>
    {stale && <Alert type="warning" showIcon title="诊断已过期 · 当前参数待验证" description="图像、搜索线和剖面均为上次运行快照；运行节点后更新。" />}
    {!available && <p>运行此节点，查看 ROI 与坐标变换后的真实采样、候选和未检出原因。</p>}
    {error && <div role="alert" className="caliper-diagnostic-error">{error}<Button size="small" onClick={onRetry}>重试读取</Button></div>}
    {loading && <p role="status">正在读取边缘剖面…</p>}
    {data && <>
      <div className="caliper-diagnostic-summary" aria-live="polite"><Tag color={data.found ? 'green' : 'gold'}>
        {caliperEdgeReasons[data.reason] ?? data.reason}</Tag><span>{data.candidates.length} 个合格候选{data.clipped ? ' · 已按图像边界裁切' : ''}</span></div>
      {!data.found && <p className="caliper-diagnostic-quality-note">{reasonHelp[data.reason] ?? '请核对图像、搜索线及采样参数。'}</p>}
      {data.profile.length > 0 ? <ProfileCharts data={data} inspectionPosition={inspectionPosition} onInspect={onInspect} />
        : <p>没有可绘制的剖面，请先将有效搜索段移入图像。</p>}
      <dl>
        <div><dt>有效采样</dt><dd>{data.validSampleCount} / {data.sampleCount}</dd></div>
        <div><dt>投影长度</dt><dd>{data.sampleWidth} px</dd></div>
        <div><dt>选点坐标</dt><dd>({number(data.edgePoint?.x, 3)}, {number(data.edgePoint?.y, 3)})</dd></div>
        <div><dt>距原始起点</dt><dd>{number(data.edgePosition == null ? null : data.startOffset + data.edgePosition, 3)} px</dd></div>
        <div><dt>强度 / 阈值</dt><dd>{number(data.edgeStrength)} / {number(data.minContrast)}</dd></div>
        <div><dt>极性 / 选边顺序</dt><dd>{polarityLabels[data.polarity] ?? data.polarity} / {directionLabels[data.direction] ?? data.direction}</dd></div>
      </dl>
      {data.candidates.length > 0 && <details className="caliper-diagnostic-explanation"><summary>候选详情（{data.candidates.length}）</summary>
        <p>点击“定位”查看候选在图像中的位置；最终输出仍按本次运行的选边顺序决定。</p>
        <table className="caliper-edge-candidates"><caption>算法实际候选 · 距离按原始起点计</caption>
          <thead><tr><th scope="col">候选</th><th scope="col">距离 px</th><th scope="col">响应</th><th scope="col">位置</th></tr></thead>
          <tbody>{data.candidates.slice(currentPage * pageSize, (currentPage + 1) * pageSize).map((candidate, offset) => {
            const index = currentPage * pageSize + offset;
            return <tr key={index} className={index === data.selectedCandidateIndex ? 'is-chosen' : ''}>
              <td>#{index + 1}{index === data.selectedCandidateIndex ? ' ✓' : ''}</td><td>{number(data.startOffset + candidate.position, 3)}</td>
              <td>{number(candidate.amplitude)}</td><td><Button size="small" type="link" aria-label={`定位候选 ${index + 1}`}
                onFocus={() => onInspect(candidate.position)} onClick={() => onInspect(candidate.position)}>定位</Button></td>
            </tr>;
          })}</tbody>
        </table>
        {pageCount > 1 && <div className="caliper-diagnostic-selector"><Button size="small" disabled={currentPage === 0} onClick={() => setPage(currentPage - 1)}>上一页</Button>
          <span>{currentPage + 1} / {pageCount}</span><Button size="small" disabled={currentPage + 1 === pageCount} onClick={() => setPage(currentPage + 1)}>下一页</Button></div>}
      </details>}
      <details className="caliper-diagnostic-explanation"><summary>图例与选边说明</summary>
        <p>蓝线：投影灰度；浅灰线：相邻灰度差（参考）；紫点：合格候选；绿点与绿虚线：最终选中边缘；橙虚线：阈值；灰色区：ROI 无效采样；黄色：当前查看位置。</p>
        <p>极性按原始起点→终点定义，选边顺序不改变极性。阈值作用于算法实际响应；长连续段使用高斯滤波，短段使用局部梯度，缺口两侧独立检测。</p>
        <p>{data.refine === 'none' ? '本次未做位置细化；采样间距和坐标变换仍可能使输出带小数。' : '本次启用亚像素位置细化。'}{data.selectedCandidateIndex != null ? ` ✓ 标记算法选中候选 #${data.selectedCandidateIndex + 1}。` : ''}</p>
      </details>
      <small className="caliper-diagnostic-provenance">运行 {data.runId} · 序列 {data.executionSequence} · 采样间距 {number(data.spacing, 4)} px</small>
    </>}
  </section>;
}
