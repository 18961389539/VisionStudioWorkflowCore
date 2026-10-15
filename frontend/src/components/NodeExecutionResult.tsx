import { useId } from 'react';
import { Alert, Tag } from 'antd';
import type { NodeCatalogItem, RunResult, VisionValueSnapshot } from '../types';
import ExecutionStatus from './ExecutionStatus';
import { portNames } from '../i18n/catalog';

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
  return <section className="node-execution-result" aria-label="本节点运行结果">
    <button type="button" className="node-result-heading" aria-label="本节点运行结果" aria-expanded={expanded}
      aria-controls={bodyId} onClick={() => onExpandedChange(!expanded)}>
      <span aria-hidden="true">{expanded ? '▾' : '▸'}</span><strong>本节点运行结果</strong>
      {requestError && <Tag color="error">本次测试未完成</Tag>}
      {report && <><Tag color={report.success ? 'success' : 'error'}><ExecutionStatus status={report.success ? (report.phase === 'Warmup' ? 'warmup' : 'ok') : 'error'} label={`${requestError ? '上次' : ''}${report.success ? (report.phase === 'Warmup' ? '预热成功' : '执行成功') : '执行失败'}`} /></Tag><span>{report.durationMs.toFixed(2)} 毫秒</span></>}
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
      {report && <>
        {report.outputs && Object.keys(report.outputs).length > 0 ? <Values values={report.outputs} catalog={catalog} />
          : <div className="node-result-scalars">{Object.entries(report.summary ?? {}).map(([key, value]) => <span key={key}><b>{portNames[key] ?? key}</b> {valueText(value)}</span>)}</div>}
        {report.inputs && Object.keys(report.inputs).length > 0 && <details><summary>本次运行的输入</summary><Values values={report.inputs} catalog={catalog} input /></details>}
        <details><summary>完整摘要</summary><pre className="debug-json">{JSON.stringify(report.summary, null, 2)}</pre></details>
      </>}
    </>}
    </div>
  </section>;
}
