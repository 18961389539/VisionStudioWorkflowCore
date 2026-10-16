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
  const messages: Record<string, string> = {
    'no-edge-above-threshold': '搜索范围内没有达到最小对比度的边缘。检查搜索线或标称圆是否穿过目标边缘，并核对最小对比度、边缘极性、搜索方向和 ROI。',
    'search-line-outside-image': '搜索线完全位于输入图像之外。请调整搜索线，或检查图像裁剪和坐标变换。',
    'search-region-outside-image': '所有卡尺都落在输入图像之外。请移动搜索区域，或检查图像裁剪和坐标变换。',
    'search-line-clipped-too-short': '搜索线与图像只有很短的交集，裁切后不足 2 px。请调整线的位置或方向。',
    'profile-has-no-finite-samples': '有效采样点不足，ROI 可能屏蔽了搜索线，或线段落在图像边缘之外。请检查 ROI 和搜索线。',
    'too-few-edge-points': '找到的边缘点不足 3 个，无法拟合。请增加卡尺数量、扩大沿边缘跨度，或改善边缘对比度。',
    'degenerate-line-fit': '边缘点无法形成有效直线。请检查搜索线是否横穿目标边缘，并减少误检或扩大采样跨度。',
    'all-spokes-exceed-max-radius-deviation': '所有候选边缘都超出最大半径偏差。请检查标称圆和搜索带，或放宽最大半径偏差。',
    'fitted-radius-outside-tolerance': '已拟合出候选圆，但半径超出允许偏差。请查看候选圆叠加，核对标称半径和拟合半径公差。'
  };
  return messages[reason] ?? `没有找到有效测量结果${reason ? `（${reason}）` : ''}。请检查搜索几何、ROI 和边缘阈值。`;
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

function Values({ values, catalog, input = false }: { values: Record<string, VisionValueSnapshot>; catalog: NodeCatalogItem; input?: boolean }) {
  return <div className="node-result-table-wrap"><table className="node-info-table node-result-table">
    <thead><tr><th>名称</th><th>类型</th><th>值</th></tr></thead>
    <tbody>{Object.entries(values).map(([name, value]) => <tr key={name}>
      <td>{(input ? catalog.inputs : catalog.outputs).find((p) => p.name === name)?.label ?? portNames[name] ?? name}</td>
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
  const prominentSummaryKeys = report && geometryResultNodeTypes.has(catalog.type)
    ? pickSummaryKeys(report.summary, catalog.type, 4) : [];
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
      {report?.error && <Alert type="error" showIcon title={report.error} />}
      {notFoundMessage && <Alert type="warning" showIcon title="节点已运行，但未检出目标" description={notFoundMessage} />}
      {report && <>
        {prominentSummaryKeys.length > 0 && <div className="node-result-scalars" aria-label="关键测量结果">
          {prominentSummaryKeys.map(key => <span key={key}><b>{portNames[key] ?? key}</b> {valueText(report.summary[key])}</span>)}
        </div>}
        {report.outputs && Object.keys(report.outputs).length > 0 ? <Values values={report.outputs} catalog={catalog} />
          : <div className="node-result-scalars">{Object.entries(report.summary ?? {}).map(([key, value]) => <span key={key}><b>{portNames[key] ?? key}</b> {valueText(value)}</span>)}</div>}
        {report.inputs && Object.keys(report.inputs).length > 0 && <details><summary>本次运行的输入</summary><Values values={report.inputs} catalog={catalog} input /></details>}
        <details><summary>完整摘要</summary><pre className="debug-json">{JSON.stringify(report.summary, null, 2)}</pre></details>
      </>}
    </>}
    </div>
  </section>;
}
