import { useEffect, useMemo, useState } from 'react';
import { Alert, Spin, Statistic, Table, Tag, Typography } from 'antd';
import type { NodePerformanceBaseline, RunObservabilitySnapshot } from '../types';
import { localizeStatus } from '../i18n';

type Props = { runId?: string };

const statusColor: Record<NodePerformanceBaseline['status'], string> = {
  Insufficient: 'default',
  Normal: 'green',
  Slow: 'orange',
  VerySlow: 'red'
};

export default function RunObservabilityPanel({ runId }: Props) {
  const [data, setData] = useState<RunObservabilitySnapshot>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!runId) { setData(undefined); setError(undefined); return; }
    const controller = new AbortController();
    setLoading(true);
    setError(undefined);
    fetch(`/api/traces/${runId}/observability?days=7&history=100`, { signal: controller.signal })
      .then(async (response) => {
        const payload = await response.json();
        if (!response.ok) throw new Error(payload.detail ?? payload.error ?? 'Failed to load run observability');
        setData(payload);
      })
      .catch((err) => { if (err.name !== 'AbortError') setError(err.message); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [runId]);

  const baselineByNode = useMemo(() => new Map((data?.baselines ?? []).map((x) => [`${x.nodeId}\u001f${x.nodeType}`, x])), [data]);
  const maxEnd = Math.max(1, ...(data?.timeline ?? []).map((x) => x.endOffsetMs));

  if (!runId) return null;
  if (loading) return <div className="run-observability-loading"><Spin size="small" /> 正在加载运行时间线…</div>;
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
      <Statistic title="时间线跨度" value={data.timelineSpanMs} precision={1} suffix="毫秒" />
      <Statistic title="主机 / 调度耗时" value={data.runtimeOverheadMs} precision={1} suffix="毫秒" />
      <Statistic title="最大并发数" value={data.peakConcurrency} />
      <Statistic title="基线运行数" value={data.baselineRunCount} />
      <Statistic title="慢节点数" value={data.slowNodeCount} />
    </div>
    <div className="run-timeline">
      {data.timeline.map((item) => {
        const baseline = baselineByNode.get(`${item.nodeId}\u001f${item.nodeType}`);
        const left = Math.max(0, Math.min(100, item.startOffsetMs / maxEnd * 100));
        const width = Math.max(0.75, Math.min(100 - left, Math.max(0, item.endOffsetMs - item.startOffsetMs) / maxEnd * 100));
        return <div className="run-timeline-row" key={`${item.executionSequence}-${item.nodeId}`}>
          <div className="run-timeline-label" title={`${item.nodeId} · ${item.nodeType}`}>
            <span>#{item.executionSequence}</span>{item.nodeType}
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
        { title: '节点', dataIndex: 'nodeType', width: 170, render: (v: string, row) => <span title={row.nodeId}>{v}</span> },
        { title: '当前耗时', dataIndex: 'currentDurationMs', width: 90, render: (v: number) => `${v.toFixed(2)} 毫秒` },
        { title: 'P50', dataIndex: 'p50Ms', width: 85, render: (v: number, row) => row.sampleCount ? `${v.toFixed(2)} 毫秒` : '—' },
        { title: 'P95', dataIndex: 'p95Ms', width: 85, render: (v: number, row) => row.sampleCount ? `${v.toFixed(2)} 毫秒` : '—' },
        { title: '样本数', dataIndex: 'sampleCount', width: 75 },
        { title: '中位数倍数', dataIndex: 'ratioToMedian', width: 85, render: (v?: number | null) => v == null ? '—' : `${v.toFixed(2)}×` },
        { title: '状态', dataIndex: 'status', width: 100, render: (v: NodePerformanceBaseline['status']) => <Tag color={statusColor[v]}>{localizeStatus(v)}</Tag> }
      ]}
    />
  </section>;
}
