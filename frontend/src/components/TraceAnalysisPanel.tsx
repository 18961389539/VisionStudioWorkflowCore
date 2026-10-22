import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Space, Spin, Statistic, Table, Tag, Typography } from 'antd';
import type { FailureSignatureAnalysis, RunTraceComparison, TraceNodeDelta } from '../types';
import { localizeStatus } from '../i18n';

type Props = {
  runId?: string;
  onReplay?: (nodeId?: string) => void;
};

const statusColor: Record<string, string> = {
  Same: 'green',
  Added: 'blue',
  Missing: 'default',
  TypeChanged: 'purple',
  StatusChanged: 'red',
  OutputChanged: 'orange',
  PerformanceRegression: 'volcano'
};

const categoryColor: Record<string, string> = {
  ExecutionFailure: 'red',
  QualityNG: 'orange',
  PerformanceRegression: 'volcano',
  None: 'default'
};

export default function TraceAnalysisPanel({ runId, onReplay }: Props) {
  const [comparison, setComparison] = useState<RunTraceComparison>();
  const [signature, setSignature] = useState<FailureSignatureAnalysis>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!runId) { setComparison(undefined); setSignature(undefined); setError(undefined); return; }
    const controller = new AbortController();
    setLoading(true);
    setError(undefined);
    Promise.all([
      fetch(`/api/traces/${runId}/compare`, { signal: controller.signal }),
      fetch(`/api/traces/${runId}/failure-signature?days=30&take=12`, { signal: controller.signal })
    ]).then(async ([compareResponse, signatureResponse]) => {
      const comparePayload = await compareResponse.json();
      const signaturePayload = await signatureResponse.json();
      if (!compareResponse.ok) throw new Error(comparePayload.detail ?? comparePayload.error ?? '追溯记录对比失败');
      if (!signatureResponse.ok) throw new Error(signaturePayload.detail ?? signaturePayload.error ?? '故障特征分析失败');
      setComparison(comparePayload as RunTraceComparison);
      setSignature(signaturePayload as FailureSignatureAnalysis);
    }).catch((err) => {
      if (err.name !== 'AbortError') setError(err.message);
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false);
    });
    return () => controller.abort();
  }, [runId]);

  const changedNodes = useMemo(
    () => (comparison?.nodes ?? []).filter((x) => x.status !== 'Same' || x.performanceStatus === 'Slow' || x.performanceStatus === 'VerySlow'),
    [comparison]
  );

  if (!runId) return null;
  if (loading) return <div className="trace-analysis-loading"><Spin size="small" /> 正在对比追溯记录并归类故障特征…</div>;
  if (error) return <Alert type="error" showIcon message="追溯分析不可用" description={error} />;
  if (!comparison || !signature) return null;

  return <section className="trace-analysis">
    <div className="trace-analysis-heading">
      <div>
        <Typography.Text strong>追溯对比 + 故障特征</Typography.Text>
        <div>最近的合格基线 · 节点差异 · 重复异常指纹</div>
      </div>
      {signature.hasSignature && onReplay && <Button size="small" type="primary" ghost onClick={() => onReplay(signature.replayNodeId ?? undefined)}>
        回放可疑节点
      </Button>}
    </div>

    {!comparison.baselineAvailable ? <Alert
      type="info" showIcon
      message="同一分组中没有更早的合格基线"
      description="此工作流 / 来源 / 作业版本至少有一条更早完成且判定合格的运行记录后，才能进行对比。"
    /> : <>
      <div className="trace-analysis-stats">
        <Statistic title="基线记录" value={comparison.baselineRunId?.slice(0, 8) ?? '—'} />
        <Statistic title="当前耗时" value={comparison.currentDurationMs} precision={1} suffix="毫秒" />
        <Statistic title="耗时变化" value={comparison.durationDeltaMs ?? 0} precision={1} suffix="毫秒" />
        <Statistic title="变更节点" value={comparison.changedNodeCount} />
        <Statistic title="性能下降节点" value={comparison.performanceRegressionNodeCount} />
      </div>
      {comparison.workflowHashChanged && <Alert type="warning" showIcon message="工作流哈希与所选基线不同" style={{ marginBottom: 8 }} />}
      <Table
        size="small"
        pagination={false}
        rowKey={(row) => row.nodeId}
        dataSource={changedNodes.length ? changedNodes : comparison.nodes.slice(0, 8)}
        columns={[
          { title: '节点', dataIndex: 'nodeType', width: 160, render: (v: string, row: TraceNodeDelta) => <span title={row.nodeId}>{v}</span> },
          { title: '基线耗时', dataIndex: 'baselineDurationMs', width: 90, render: (v?: number | null) => v == null ? '—' : `${v.toFixed(2)} 毫秒` },
          { title: '当前耗时', dataIndex: 'currentDurationMs', width: 90, render: (v?: number | null) => v == null ? '—' : `${v.toFixed(2)} 毫秒` },
          { title: 'Δ', dataIndex: 'durationDeltaMs', width: 80, render: (v?: number | null) => v == null ? '—' : `${v >= 0 ? '+' : ''}${v.toFixed(2)}` },
          { title: '×', dataIndex: 'durationRatio', width: 70, render: (v?: number | null) => v == null ? '—' : `${v.toFixed(2)}×` },
          { title: '变更字段', dataIndex: 'summaryChangedKeys', render: (v: string[]) => v.length ? v.slice(0, 4).join('、') : '—' },
          { title: '性能', dataIndex: 'performanceStatus', width: 92, render: (v: string) => <Tag color={v === 'VerySlow' ? 'red' : v === 'Slow' ? 'orange' : v === 'Normal' ? 'green' : 'default'}>{localizeStatus(v)}</Tag> },
          { title: '差异', dataIndex: 'status', width: 126, render: (v: string) => <Tag color={statusColor[v] ?? 'default'}>{localizeStatus(v)}</Tag> }
        ]}
      />
    </>}

    <div className="failure-signature-card">
      {!signature.hasSignature ? <Alert
        type="success" showIcon
        message="未发现故障特征"
        description="此运行没有执行失败、不合格判定或 V0.60 耗时回归特征。"
      /> : <>
        <div className="failure-signature-title">
          <Space wrap>
            <Tag color={categoryColor[signature.category] ?? 'default'}>{localizeStatus(signature.category)}</Tag>
            <Typography.Text code>{signature.signatureId}</Typography.Text>
            <Tag color={signature.recurring ? 'red' : 'blue'}>{signature.recurring ? `出现 ${signature.occurrenceCount} 次` : '首次出现'}</Tag>
          </Space>
          <Typography.Text type="secondary">近 30 天分组范围</Typography.Text>
        </div>
        <div className="failure-signature-fingerprint">{signature.fingerprint}</div>
        {signature.recommendedAction && <Alert type="info" showIcon message={signature.recommendedAction} style={{ marginTop: 8 }} />}
        {signature.recentOccurrences.length > 0 && <Table
          size="small"
          pagination={false}
          rowKey="runId"
          dataSource={signature.recentOccurrences}
          style={{ marginTop: 8 }}
          columns={[
            { title: '匹配的运行记录', dataIndex: 'runId', render: (v: string) => <Typography.Text code>{v.slice(0, 12)}</Typography.Text> },
            { title: '时间', dataIndex: 'startedAt', width: 165, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
            { title: '判定', dataIndex: 'disposition', width: 100, render: (v: string) => <Tag>{localizeStatus(v)}</Tag> },
            { title: '运行耗时（毫秒）', dataIndex: 'totalDurationMs', width: 120, render: (v: number) => v.toFixed(1) },
            { title: '节点耗时（毫秒）', dataIndex: 'nodeDurationMs', width: 120, render: (v?: number | null) => v == null ? '—' : v.toFixed(2) }
          ]}
        />}
      </>}
    </div>
  </section>;
}
