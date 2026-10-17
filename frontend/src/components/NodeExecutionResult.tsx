import { useId } from 'react';
import { Alert, Tag } from 'antd';
import type { NodeCatalogItem, RunResult, VisionValueSnapshot } from '../types';
import ExecutionStatus from './ExecutionStatus';
import { portNames } from '../i18n/catalog';
import { pickSummaryKeys } from '../summaryText';

const measurementNodeTypes = new Set(['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle']);
const geometryResultNodeTypes = new Set(['image.crop', 'roi.transform', ...measurementNodeTypes]);

function measurementNotFoundMessage(type: string, summary: Record<string, unknown> | undefined) {
  if (!measurementNodeTypes.has(type) || summary?.found !== false) return undefined;
  const reason = typeof summary.reason === 'string' ? summary.reason : '';
  if (reason === 'no-edge-above-threshold') {
    const noEdgeMessages: Record<string, string> = {
      'feature.edgeSubpixel': '搜索线内没有达到最小对比度的边缘。检查搜索线是否横穿目标边缘，并核对最小对比度、边缘极性、搜索方向和 ROI。',
      'measure.caliper': '搜索线内没有达到最小对比度的边缘。检查搜索线是否横穿目标边缘，并核对最小对比度、边缘极性、搜索方向和 ROI。',
      'measure.caliperLine': '沿边缘布置的平行搜索剖面没有检出达到最小对比度的边缘。检查搜索线、沿边缘跨度、最小对比度、极性、搜索方向和 ROI。',
      'measure.caliperCircle': '标称圆的径向搜索弧内没有达到最小对比度的边缘。检查标称圆、圆弧范围、径向搜索带、最小对比度、极性、搜索方向和 ROI。'
    };
    return noEdgeMessages[type] ?? '搜索范围内没有达到最小对比度的边缘。请检查搜索几何、最小对比度、边缘极性、搜索方向和 ROI。';
  }
  const messages: Record<string, string> = {
    'search-line-outside-image': '搜索线完全位于输入图像之外。请调整搜索线，或检查图像裁剪和坐标变换。',
    'search-region-outside-image': '所有卡尺都落在输入图像之外。请移动搜索区域，或检查图像裁剪和坐标变换。',
    'search-line-clipped-too-short': '搜索线与图像只有很短的交集，裁切后不足 2 px。请调整线的位置或方向。',
    'search-region-clipped-too-short': type === 'measure.caliperCircle'
      ? '径向搜索线与图像相交的部分不足 2 px。请调整标称圆心和半径，让搜索弧落入图像。'
      : '平行搜索剖面与图像相交的部分不足 2 px。请把搜索线移入图像，或缩小沿边缘跨度。',
    'profile-has-no-finite-samples': type === 'measure.caliperCircle'
      ? '有效径向采样点不足，ROI 可能屏蔽了搜索弧。请检查 ROI、标称圆和径向搜索带。'
      : '有效采样点不足，ROI 可能屏蔽了搜索线，或线段落在图像边缘之外。请检查 ROI 和搜索线。',
    'profile-has-insufficient-contiguous-samples': type === 'measure.caliperCircle'
      ? '径向剖面有采样点，但 ROI 将其切成了过短片段，无法可靠定位边缘。请放宽 ROI 或调整标称圆和径向搜索带。'
      : type === 'measure.caliperLine'
        ? '卡尺剖面有采样点，但 ROI 将其切成了过短片段，无法可靠定位边缘。请放宽 ROI，或调整搜索线和沿边缘跨度。'
        : '搜索剖面有采样点，但 ROI 将其切成了过短片段，无法可靠定位边缘。请放宽 ROI 或调整搜索线。',
    'too-few-edge-points': type === 'measure.caliperCircle'
      ? '找到的边缘点不足 3 个，无法拟合圆。请增加卡尺数量、扩大搜索弧角范围，或改善边缘对比度。'
      : '找到的边缘点不足 3 个，无法拟合。请增加卡尺数量、扩大沿边缘跨度，或改善边缘对比度。',
    'insufficient-valid-calipers': '拟合后保留的有效卡尺数低于“最少有效卡尺数”。请增加卡尺数量、扩大有效边缘覆盖，或降低门槛。',
    'degenerate-line-fit': '边缘点无法形成有效直线。请检查搜索线是否横穿目标边缘，并减少误检或扩大采样跨度。',
    'degenerate-circle-fit': '检出的边缘点分布接近直线，无法稳定拟合圆。请扩大搜索弧角范围或检查标称圆、径向搜索带和误检点。',
    'all-spokes-exceed-max-radius-deviation': '所有候选边缘都超出最大半径偏差。请检查标称圆和搜索带，或放宽最大半径偏差。',
    'fitted-radius-outside-tolerance': '已拟合出候选圆，但半径超出允许偏差。请查看候选圆叠加，核对标称半径和拟合半径公差。'
  };
  return messages[reason] ?? `没有找到有效测量结果${reason ? `（${reason}）` : ''}。请检查搜索几何、ROI 和边缘阈值。`;
}

function executionErrorMessage(catalog: NodeCatalogItem, error: string): string | undefined {
  const type = catalog.type;
  const common: Record<string, string> = {
    'The search line is too short (minimum 2 px).': '搜索线长度不足 2 px。请把搜索线拉长后重试。',
    'Search line coordinates must be finite and within +/-10000000 pixels.': '搜索线坐标必须是有限数值，且绝对值不超过 10000000 px。请检查上游几何或坐标变换。',
    'Search line parameters must be finite numbers.': '搜索线参数必须是有效数值。请检查起点和终点坐标。',
    'The connected search line has non-finite endpoints.': '上游搜索线包含无效坐标。请检查提供搜索线的节点输出。',
    'Cannot clip a search line to an empty image.': '无法将搜索线裁剪到空图像。请检查输入图像尺寸和上游输出。',
    'Measurement transform must be a finite, orientation-preserving rigid px-to-px transform (translation and rotation only).': '测量变换必须是有效的像素坐标刚性变换，只能包含平移和旋转。请检查 transform 输入。'
  };
  if (common[error] && ['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'].includes(type))
    return common[error];
  const invalidParameter = error.match(/^Parameter '([^']+)' must be a finite number\.$/);
  if (invalidParameter && ['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle', 'roi.transform'].includes(type)) {
    const name = invalidParameter[1];
    const label = catalog.parameters.find(parameter => parameter.name === name)?.label ?? name;
    return `参数“${label}”必须是有限数值。请检查并修正后重试。`;
  }
  const invalidChoice = error.match(/^Parameter '([^']+)' must be one of \[([^\]]+)\]\.$/);
  if (invalidChoice && ['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'].includes(type)) {
    const [, name, rawChoices] = invalidChoice;
    const parameter = catalog.parameters.find(item => item.name === name);
    const label = parameter?.label ?? name;
    const choices = rawChoices.split(',').map(value => value.trim()).filter(Boolean).map(value =>
      parameter?.options?.find(option => option.value.toLowerCase() === value.toLowerCase())?.label ?? value);
    return `参数“${label}”的选项值无效。可选项：${choices.join('、')}。`;
  }
  if (error === 'Polygon ROI exceeds the 500000-point limit.'
    && ['image.crop', 'roi.transform', 'feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'].includes(type))
    return '多边形 ROI 最多允许 500000 个顶点。';
  if (['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'].includes(type)) {
    if (error.startsWith('Rectangle ROI requires finite coordinates'))
      return '矩形 ROI 必须具有有限坐标和正宽高，且边界在允许范围内。';
    if (error.startsWith('Circle ROI requires finite center coordinates'))
      return '圆形 ROI 必须具有有限圆心坐标和正半径，且坐标在允许范围内。';
    if (error.startsWith('Polygon ROI requires 3..'))
      return '多边形 ROI 至少需要 3 个有效顶点，且坐标在允许范围内。';
    if (error === 'Polygon ROI vertices must enclose a finite non-zero area.')
      return '多边形 ROI 顶点必须围成有限且非零的面积。';
    if (error === 'Brush ROI requires 1..10000 strokes and at least one add stroke.')
      return '笔刷 ROI 需要 1 至 10000 笔有效笔迹，并至少包含一笔添加选区。';
    if (error.startsWith('Brush ROI strokes require a valid mode'))
      return '笔刷 ROI 的笔迹模式、坐标或宽度无效。请检查 ROI 笔迹。';
    if (error === 'Brush ROI may contain at most 500000 points.')
      return '笔刷 ROI 最多允许 500000 个坐标点。';
    const unsupportedRoi = error.match(/^ROI type '(.+)' is not supported\.$/);
    if (unsupportedRoi) return `不支持此 ROI 类型（${unsupportedRoi[1]}）。请使用矩形、圆形、多边形或笔刷 ROI。`;
  }

  const messages: Record<string, Record<string, string>> = {
    'image.crop': {
      'Image crop requires a non-empty image.': '裁剪输入图像为空。请检查 image 输入和上游节点输出。'
    },
    'roi.transform': {
      'Connect a non-empty debugImage before capturing the reference.': '获取基准前，请先连接有效的调试图像。',
      'Scene dimensions differ from the saved reference. Capture a new reference before correcting the ROI.': '当前场景图像尺寸与已保存基准不一致。请重新获取基准后再执行 ROI 修正。',
      "ROI transform requires either a connected 'roi' input or a 'roi' parameter.": '定位修正 ROI 需要连接 roi 输入，或在节点中绘制基准 ROI。',
      'Base/current pose components must be finite (non-finite pose received).': '基准位姿或当前位姿包含无效数值。请检查定位节点的位姿输出。',
      'Angle period must be greater than 0 and at most 360 degrees.': '角度周期必须大于 0° 且不超过 360°。',
      'The circle ROI radius must be non-negative.': '圆形 ROI 半径不能为负数。',
      'The polygon ROI needs at least 3 points.': '多边形 ROI 至少需要 3 个顶点。',
      'The rectangle ROI width/height must be non-negative.': '矩形 ROI 的宽度和高度不能为负数。',
      'The brush ROI needs 1..10000 strokes.': '笔刷 ROI 需要 1 至 10000 笔有效笔迹。',
      'The brush ROI needs finite points in at least one add stroke.': '笔刷 ROI 至少需要一笔包含有效坐标的添加笔迹。',
      'Brush ROI strokes need a valid mode, positive width and points.': '笔刷笔迹的模式、宽度或坐标无效。请检查 ROI 笔迹。'
    },
    'measure.caliper': {
      'Caliper measurement requires a non-empty image.': '卡尺输入图像为空。请检查 image 输入和上游节点输出。'
    },
    'measure.caliperLine': {
      'Caliper line measurement requires a non-empty image.': '卡尺找线的输入图像为空。请检查 image 输入和上游节点输出。'
    },
    'feature.edgeSubpixel': {
      'Subpixel edge detection requires a non-empty image.': '亚像素边缘检测的输入图像为空。请检查 image 输入和上游节点输出。'
    },
    'measure.caliperCircle': {
      'Caliper circle measurement requires a non-empty image.': '卡尺找圆的输入图像为空。请检查 image 输入和上游节点输出。',
      'The connected circle input must have a finite center and positive radius.': '上游圆心或半径无效。请检查圆几何输出，半径必须为正数。',
      'Caliper circle requires a finite center and a positive radius.': '标称圆心必须是有效坐标，半径必须为正数。',
      'Caliper circle coordinates and radius must be within 10000000 pixels.': '标称圆坐标和半径的绝对值不能超过 10000000 px。请检查上游几何和坐标系。',
      'Caliper circle center after coordinate correction must be finite and within +/-10000000 pixels.': '坐标修正后的圆心必须是有效坐标，且绝对值不能超过 10000000 px。请检查圆心、transform 和图像坐标系。'
    }
  };
  const exact = messages[type]?.[error];
  if (exact) return exact;

  if (type === 'roi.transform') {
    const boundedPose = error.match(/^Base\/current pose coordinates must be within ±([\d.]+) px\.$/);
    if (boundedPose) return `基准位姿和当前位姿坐标必须在 ±${boundedPose[1]} px 范围内。`;
    const invalidRoi = error.match(/^Unsupported ROI type '(.+)'\.$/);
    if (invalidRoi) return `不支持此 ROI 类型（${invalidRoi[1]}）。请使用矩形、圆形、多边形或笔刷 ROI。`;
    if (error.startsWith('Brush ROI strokes need a valid mode, finite points within '))
      return '笔刷 ROI 笔迹包含无效坐标、模式或宽度。请检查笔迹，并将笔宽设在 1 至 1024 px 之间。';
    if (error === 'The brush ROI may contain at most 500000 points.') return '笔刷 ROI 最多允许 500000 个坐标点。';
    if (error === 'The polygon ROI must enclose a finite non-zero area.') return '多边形 ROI 必须围成有限且非零的面积。';
    if (error.startsWith('The rectangle ROI needs positive dimensions and coordinates within '))
      return '矩形 ROI 必须具有正宽高，且坐标需在允许范围内。';
    if (error.startsWith('The circle ROI needs a positive radius and coordinates within '))
      return '圆形 ROI 必须具有正半径，且坐标需在允许范围内。';
    if (error.startsWith('The polygon ROI needs at least three finite points within '))
      return '多边形 ROI 至少需要 3 个有效顶点，且坐标需在允许范围内。';
  }
  return undefined;
}

type Props = {
  nodeId: string;
  catalog: NodeCatalogItem;
  result?: RunResult;
  receivedAt?: string;
  stale: boolean;
  draftResult?: boolean;
  expanded: boolean;
  onExpandedChange: (expanded: boolean) => void;
  requestError?: string;
};

function valueText(value: unknown): string {
  if (value === undefined || value === null) return '—';
  if (typeof value === 'boolean') return value ? '是' : '否';
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

function summaryForDisplay(summary: Record<string, unknown>) {
  return Object.fromEntries(Object.entries(summary).map(([key, value]) => [key,
    key === 'referenceImage' && typeof value === 'string' && value.length > 0
      ? `[图像数据已隐藏，${value.length.toLocaleString()} 个 Base64 字符]`
      : value]));
}

function summaryJsonForDisplay(summary: Record<string, unknown>) {
  return JSON.stringify(summaryForDisplay(summary), null, 2);
}

function Values({ values, catalog, input = false }: { values: Record<string, VisionValueSnapshot>; catalog: NodeCatalogItem; input?: boolean }) {
  const ports = input ? catalog.inputs : catalog.outputs;
  const labelForPort = (name: string) => {
    const port = ports.find((item) => item.name.toLowerCase() === name.toLowerCase());
    return port?.label ?? portNames[port?.name ?? name] ?? name;
  };
  return <div className="node-result-table-wrap"><table className="node-info-table node-result-table">
    <thead><tr><th>名称</th><th>类型</th><th>值</th></tr></thead>
    <tbody>{Object.entries(values).map(([name, value]) => <tr key={name}>
      <td>{labelForPort(name)}</td>
      <td>{value.type}</td><td><span>{value.display || valueText(value.value)}</span></td>
    </tr>)}</tbody>
  </table></div>;
}

export default function NodeExecutionResult({ nodeId, catalog, result, receivedAt, stale, draftResult, expanded, onExpandedChange, requestError }: Props) {
  const bodyId = useId();
  const reports = (result?.nodeReports ?? []).filter((report) => report.nodeId === nodeId);
  const report = reports[reports.length - 1];
  const notFound = Boolean(report?.success && report.summary.found === false && measurementNodeTypes.has(catalog.type));
  const notFoundMessage = measurementNotFoundMessage(catalog.type, report?.summary);
  const reportErrorMessage = report?.error ? executionErrorMessage(catalog, report.error) : undefined;
  const reportSummaryKeys = report && geometryResultNodeTypes.has(catalog.type)
    ? pickSummaryKeys(report.summary, catalog.type,
      ['measure.caliperLine', 'measure.caliperCircle', 'measure.caliper'].includes(catalog.type) ? 5 : 4) : [];
  const insufficientSamplesKey = report?.summary.reason === 'profile-has-insufficient-contiguous-samples'
    ? catalog.type === 'measure.caliperLine' ? 'profilesWithInsufficientContiguousSamples'
      : catalog.type === 'measure.caliperCircle' ? 'spokesWithInsufficientContiguousSamples'
        : undefined
    : undefined;
  const prominentSummaryKeys = insufficientSamplesKey
    ? ['found', insufficientSamplesKey, ...reportSummaryKeys.filter(key => !['found', 'x', 'y', 'edgeX', 'edgeY', 'strength', insufficientSamplesKey, 'reason'].includes(key))].slice(0, 5)
    : report?.summary.reason === 'insufficient-valid-calipers'
    ? ['found', 'sampleCount', 'minValidCalipers', catalog.type === 'measure.caliperCircle' ? 'rejectedSpokes' : 'rejectedCalipers', 'ignoredByFit']
      .filter(key => key in report.summary).slice(0, 5)
    : catalog.type === 'measure.caliper' && report?.summary.found === false
    ? ['found', ...reportSummaryKeys.filter(key => !['found', 'x', 'y', 'strength', 'reason'].includes(key))].slice(0, 4)
    : reportSummaryKeys;
  return <section className="node-execution-result" aria-label="本节点运行结果">
    <button type="button" className="node-result-heading" aria-label="本节点运行结果" aria-expanded={expanded}
      aria-controls={bodyId} onClick={() => onExpandedChange(!expanded)}>
      <span aria-hidden="true">{expanded ? '▾' : '▸'}</span><strong>本节点运行结果</strong>
      {requestError && <Tag color="error">本次测试未完成</Tag>}
      {report && <>{notFound
        ? <Tag color="warning">未找到有效结果</Tag>
        : <Tag color={report.success ? 'success' : 'error'}><ExecutionStatus status={report.success ? (report.phase === 'Warmup' ? 'warmup' : 'ok') : 'error'} label={`${requestError ? '上次' : ''}${report.success ? (report.phase === 'Warmup' ? '预热成功' : '执行成功') : '执行失败'}`} /></Tag>}
        <span>{report.durationMs.toFixed(2)} 毫秒</span></>}
      {!result && !requestError && <span>尚无结果</span>}
      {result && !report && <Tag color={result.success ? 'default' : 'error'}>{result.success ? '本节点未执行' : '运行失败'}</Tag>}
      {stale && result && <Tag color="gold"><ExecutionStatus status="stale" label="已过期 · 当前参数待运行" /></Tag>}
      <span className="node-result-toggle-label">{expanded ? '收起' : '展开'}</span>
    </button>
    <div id={bodyId} hidden={!expanded} className="node-result-content">
    {requestError && <Alert type="error" showIcon title="本次节点测试未完成" description={requestError} />}
    {!result ? <p className="node-info-muted">尚无运行结果。可使用暂存参数运行此节点；测试会预热上游输入。</p> : <>
      <p className="node-result-time">{draftResult ? '暂存参数测试' : '流程运行结果'}{receivedAt ? ` · 更新于 ${new Date(receivedAt).toLocaleString()}` : ''}{reports.length > 1 ? ` · 显示最近一次（共 ${reports.length} 次）` : ''}</p>
      {!report && <Alert type={result.success ? 'info' : 'error'} showIcon title="本次运行未产生此节点的报告" description={result.error ?? result.haltReason ?? '该节点未执行，请检查上游输入或分支条件。'} />}
      {report?.error && <Alert type="error" showIcon title={reportErrorMessage ? '节点执行失败' : report.error}
        description={reportErrorMessage && <div><p>{reportErrorMessage}</p><details><summary>查看原始诊断</summary><pre>{report.error}</pre></details></div>} />}
      {notFoundMessage && <Alert type="warning" showIcon title="节点已运行，但未检出目标" description={notFoundMessage} />}
      {report && <>
        {prominentSummaryKeys.length > 0 && <div className="node-result-scalars" aria-label="关键测量结果">
          {prominentSummaryKeys.map(key => <span key={key}><b>{portNames[key] ?? key}</b> {valueText(report.summary[key])}</span>)}
        </div>}
        {report.outputs && Object.keys(report.outputs).length > 0 ? <Values values={report.outputs} catalog={catalog} />
          : <div className="node-result-scalars">{Object.entries(summaryForDisplay(report.summary)).map(([key, value]) => <span key={key}><b>{portNames[key] ?? key}</b> {valueText(value)}</span>)}</div>}
        {report.inputs && Object.keys(report.inputs).length > 0 && <details><summary>本次运行的输入</summary><Values values={report.inputs} catalog={catalog} input /></details>}
        <details><summary>完整摘要</summary><pre className="debug-json">{summaryJsonForDisplay(report.summary)}</pre></details>
      </>}
    </>}
    </div>
  </section>;
}
