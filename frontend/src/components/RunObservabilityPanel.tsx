import { useEffect, useMemo, useState } from 'react';
import { Alert, Spin, Statistic, Table, Tag, Tooltip, Typography } from 'antd';
import type { NodePerformanceBaseline, RunObservabilitySnapshot } from '../types';
import { localizeStatus } from '../i18n';
import { localizeNodeType } from '../i18n/catalog';
import { apiFetch } from '../apiClient';
import { traces } from '../endpoints';

type Props = { runId?: string; nodeLabels?: Record<string, string>; nodeTypeLabels?: Record<string, string> };

function MetricHelp({ label, description }: { label: string; description: string }) {
  return <Tooltip title={description} trigger={['hover', 'focus']}><span tabIndex={0} className="metric-help" aria-label={`${label}说明`}>{label}</span></Tooltip>;
}

const statusColor: Record<NodePerformanceBaseline['status'], string> = {
  Insufficient: 'default',
  Normal: 'green',
  Slow: 'orange',
  VerySlow: 'red'
};

export default function RunObservabilityPanel({ runId, nodeLabels = {}, nodeTypeLabels = {} }: Props) {
  const [data, setData] = useState<RunObservabilitySnapshot>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!runId) { setData(undefined); setError(undefined); return; }
    const controller = new AbortController();
    setData(undefined);
    setLoading(true);
    setError(undefined);
    apiFetch(traces.observability(runId), { signal: controller.signal })
      .then(async (response) => {
        const payload = await response.json();
        if (!response.ok) throw new Error(payload.detail ?? payload.error ?? 'Failed to load run observability');
        if (!controller.signal.aborted) setData(payload);
      })
      .catch((err) => { if (err.name !== 'AbortError') setError(err.message); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [runId]);

  const baselineByNode = useMemo(() => new Map((data?.baselines ?? []).map((x) => [`${x.nodeId}\u001f${x.nodeType}`, x])), [data]);
  const maxEnd = Math.max(1, ...(data?.timeline ?? []).map((x) => x.endOffsetMs));
  const nameOf = (id: string, type: string) => nodeLabels[id] ?? nodeTypeLabels[type] ?? localizeNodeType(type);

  if (!runId) return null;
  if (loading || (!error && data?.runId !== runId)) return <div className="run-observability-loading"><Spin size="small" /> 正在加载运行时间线…</div>;
  if (error) return <Alert type="error" showIcon message="运行观测数据不可用" description={error} />;
  if (!data?.timelineAvailable) return <Alert type="info" showIcon message="此运行记录没有精确的节点时间线" description="V0.60 之前的追溯记录仍可查看，但系统不会根据完成顺序推测节点开始时间。" />;

  return <section className="run-observability">
    <div className="run-observability-heading">
      <div>
        <Typography.Text strong>运行观测</Typography.Text>
        <div>节点执行时间线与历史耗时基线</div>
      </div>
    </div>
    <div className="run-observability-stats">
      <Statistic title={<MetricHelp label="时间线跨度" description="从最早节点开始到最晚节点结束的时间；并行节点的时间会重叠。" />} value={data.timelineSpanMs} precision={1} suffix="毫秒" />
      <Statistic title={<MetricHelp label="主机 / 调度耗时" description="流程总耗时减去节点时间线跨度，用于观察节点执行之外的开销。" />} value={data.runtimeOverheadMs} precision={1} suffix="毫秒" />
      <Statistic title={<MetricHelp label="最大并发数" description="本次运行中，同时执行的节点数量的最大值。" />} value={data.peakConcurrency} />
      <Statistic title={<MetricHelp label="基线运行数" description="用于对比的历史运行数量，取近 7 天最多 100 次可比运行。" />} value={data.baselineRunCount} />
      <Statistic title={<MetricHelp label="慢节点数" description="与各自历史耗时相比，状态为偏慢或明显偏慢的节点数量。" />} value={data.slowNodeCount} />
    </div>
    <div className="run-timeline">
      {data.timeline.map((item) => {
        const baseline = baselineByNode.get(`${item.nodeId}\u001f${item.nodeType}`);
        const left = Math.max(0, Math.min(100, item.startOffsetMs / maxEnd * 100));
        const width = Math.max(0.75, Math.min(100 - left, Math.max(0, item.endOffsetMs - item.startOffsetMs) / maxEnd * 100));
        return <div className="run-timeline-row" key={`${item.executionSequence}-${item.nodeId}`}>
          <div className="run-timeline-label" title={`${item.nodeId} · ${item.nodeType}`}>
            <span>#{item.executionSequence}</span>{nameOf(item.nodeId, item.nodeType)}
          </div>
          <div className="run-timeline-track" title={`${item.startOffsetMs.toFixed(2)} → ${item.endOffsetMs.toFixed(2)} ms`}>
            <div className={`run-timeline-bar ${item.success ? 'ok' : 'failed'}`} style={{ left: `${left}%`, width: `${width}%` }} />
          </div>
          <div className="run-timeline-duration">{item.durationMs.toFixed(2)} 毫秒</div>
          <Tag color={baseline ? statusColor[baseline.status] : 'default'}>{baseline ? localizeStatus(baseline.status) : '无基线'}</Tag>
        </div>;
      })}
    </div>
    <Table
      size="small"
      pagination={false}
      rowKey={(row) => `${row.nodeId}-${row.nodeType}`}
      dataSource={data.baselines}
      columns={[
        { title: '节点', dataIndex: 'nodeType', width: 170, render: (v: string, row) => <div className="run-node-cell" title={`${row.nodeId} · ${v}`}><span>{nameOf(row.nodeId, v)}</span><Typography.Text type="secondary" className="run-node-type">{v}</Typography.Text></div> },
        { title: '当前耗时', dataIndex: 'currentDurationMs', width: 90, render: (v: number) => `${v.toFixed(2)} 毫秒` },
        { title: <MetricHelp label="中位耗时（P50）" description="约一半的历史执行样本耗时不超过此值。" />, dataIndex: 'p50Ms', width: 125, render: (v: number, row) => row.sampleCount ? `${v.toFixed(2)} 毫秒` : '—' },
        { title: <MetricHelp label="高位耗时（P95）" description="约 95% 的历史执行样本耗时不超过此值，用于观察较慢情况。" />, dataIndex: 'p95Ms', width: 125, render: (v: number, row) => row.sampleCount ? `${v.toFixed(2)} 毫秒` : '—' },
        { title: <MetricHelp label="样本数" description="该节点参与耗时统计的历史执行样本数量；少于 5 个样本时显示样本不足。" />, dataIndex: 'sampleCount', width: 75 },
        { title: <MetricHelp label="相对中位耗时" description="当前耗时 ÷ 历史中位耗时；例如 2× 表示本次耗时约为历史中位值的两倍。" />, dataIndex: 'ratioToMedian', width: 125, render: (v?: number | null) => v == null ? '—' : `${v.toFixed(2)}×` },
        { title: '状态', dataIndex: 'status', width: 100, render: (v: NodePerformanceBaseline['status']) => <Tag color={statusColor[v]}>{localizeStatus(v)}</Tag> }
      ]}
    />
  </section>;
}
