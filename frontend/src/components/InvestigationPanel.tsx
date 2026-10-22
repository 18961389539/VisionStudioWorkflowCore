import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Input, Select, Space, Spin, Statistic, Table, Tag, Typography, message } from 'antd';
import type { InvestigationCaseDetail, InvestigationTrendDashboard, InvestigationVerificationEvidence, ValidationDatasetSummary, ValidationRunRecord } from '../types';

type Props = { runId?: string; onReplay?: (nodeId?: string) => void };

const statusColor: Record<string, string> = { Open: 'red', Investigating: 'orange', Resolved: 'blue', Verified: 'green', Closed: 'default' };
const severityColor: Record<string, string> = { Low: 'default', Medium: 'blue', High: 'orange', Critical: 'red' };
const gateColor: Record<string, string> = { Passed: 'green', Failed: 'red', Inconclusive: 'gold' };

export default function InvestigationPanel({ runId, onReplay }: Props) {
  const [detail, setDetail] = useState<InvestigationCaseDetail>();
  const [trends, setTrends] = useState<InvestigationTrendDashboard>();
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const [note, setNote] = useState('');
  const [rootCause, setRootCause] = useState('');
  const [datasets, setDatasets] = useState<ValidationDatasetSummary[]>([]);
  const [datasetId, setDatasetId] = useState<string>();
  const [validationRuns, setValidationRuns] = useState<ValidationRunRecord[]>([]);
  const [baselineRunId, setBaselineRunId] = useState<string>();
  const [candidateRunId, setCandidateRunId] = useState<string>();
  const [gateNote, setGateNote] = useState('');
  const [messageApi, contextHolder] = message.useMessage();

  const load = async () => {
    if (!runId) return;
    setLoading(true); setError(undefined);
    try {
      const [caseResponse, trendResponse] = await Promise.all([
        fetch(`/api/investigations/for-trace/${runId}`),
        fetch('/api/investigations/trends?days=30&top=8')
      ]);
      if (caseResponse.ok) {
        const payload = await caseResponse.json() as InvestigationCaseDetail;
        setDetail(payload); setRootCause(payload.case.rootCause ?? '');
      } else if (caseResponse.status === 404) setDetail(undefined);
      else {
        const payload = await caseResponse.json();
        throw new Error(payload.detail ?? payload.error ?? 'Investigation lookup failed');
      }
      if (trendResponse.ok) setTrends(await trendResponse.json());
    } catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setLoading(false); }
  };

  const loadDatasets = async () => {
    try {
      const response = await fetch('/api/validation/datasets');
      if (response.ok) setDatasets(await response.json());
    } catch { /* verification workspace remains optional */ }
  };

  const loadValidationRuns = async (selectedDatasetId: string) => {
    try {
      const response = await fetch(`/api/validation/datasets/${selectedDatasetId}/runs`);
      if (!response.ok) return;
      const runs = (await response.json() as ValidationRunRecord[]).filter(x => x.status === 'Completed' && x.summary);
      setValidationRuns(runs);
      setBaselineRunId(runs[1]?.runId ?? runs[0]?.runId);
      setCandidateRunId(runs[0]?.runId);
    } catch { setValidationRuns([]); }
  };

  useEffect(() => { if (runId) void load(); else setDetail(undefined); }, [runId]);
  useEffect(() => { if (detail?.case.status === 'Resolved') void loadDatasets(); }, [detail?.case.id, detail?.case.status]);
  useEffect(() => { if (datasetId) void loadValidationRuns(datasetId); else setValidationRuns([]); }, [datasetId]);

  const track = async () => {
    if (!runId) return;
    setSaving(true);
    try {
      const response = await fetch(`/api/investigations/from-trace/${runId}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      const payload = await response.json();
      if (!response.ok) throw new Error(payload.detail ?? payload.error ?? 'Unable to create investigation');
      setDetail(payload); setRootCause(payload.case.rootCause ?? '');
      messageApi.success(payload.case.reopenCount > 0 ? 'Existing investigation reopened with new evidence' : 'Investigation case is tracking this signature');
      await load();
    } catch (err) { messageApi.error(err instanceof Error ? err.message : String(err)); }
    finally { setSaving(false); }
  };

  const update = async (patch: Record<string, unknown>) => {
    if (!detail) return;
    setSaving(true);
    try {
      const response = await fetch(`/api/investigations/${detail.case.id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) });
      const payload = await response.json();
      if (!response.ok) throw new Error(payload.detail ?? payload.error ?? 'Case update failed');
      setDetail(payload); setRootCause(payload.case.rootCause ?? ''); setNote('');
      messageApi.success(`Case → ${payload.case.status}`);
      const trendResponse = await fetch('/api/investigations/trends?days=30&top=8');
      if (trendResponse.ok) setTrends(await trendResponse.json());
    } catch (err) { messageApi.error(err instanceof Error ? err.message : String(err)); }
    finally { setSaving(false); }
  };

  const evaluateGate = async () => {
    if (!detail || !datasetId || !baselineRunId || !candidateRunId) return;
    setSaving(true);
    try {
      const response = await fetch(`/api/investigations/${detail.case.id}/verification-evidence`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ datasetId, baselineValidationRunId: baselineRunId, candidateValidationRunId: candidateRunId, note: gateNote || null })
      });
      const payload = await response.json() as InvestigationVerificationEvidence & { detail?: string; error?: string };
      if (!response.ok) throw new Error(payload.detail ?? payload.error ?? 'Verification gate evaluation failed');
      if (payload.gateStatus === 'Passed') messageApi.success(`Verification gate → ${payload.gateStatus}`);
      else messageApi.warning(`Verification gate → ${payload.gateStatus}`);
      setGateNote('');
      await load();
    } catch (err) { messageApi.error(err instanceof Error ? err.message : String(err)); }
    finally { setSaving(false); }
  };

  const latestVerification = detail?.verificationEvidence?.[0];
  const freshPassed = useMemo(() => {
    if (!detail?.case.resolvedAt || !latestVerification || latestVerification.gateStatus !== 'Passed') return false;
    return new Date(latestVerification.createdAt).getTime() >= new Date(detail.case.resolvedAt).getTime();
  }, [detail?.case.resolvedAt, latestVerification]);

  const nextAction = useMemo(() => {
    if (!detail) return undefined;
    const status = detail.case.status;
    if (status === 'Open') return { label: 'Start investigation', status: 'Investigating' };
    if (status === 'Investigating') return { label: 'Resolve', status: 'Resolved', noteField: 'resolutionNote' };
    if (status === 'Resolved') return { label: 'Verify fix', status: 'Verified', noteField: 'verificationNote' };
    if (status === 'Verified') return { label: 'Close case', status: 'Closed' };
    if (status === 'Closed') return { label: 'Reopen', status: 'Open' };
    return undefined;
  }, [detail]);

  if (!runId) return null;
  if (loading) return <div className="investigation-loading"><Spin size="small" /> 正在加载调查证据…</div>;

  return <section className="investigation-panel">
    {contextHolder}
    <div className="investigation-heading">
      <div><Typography.Text strong>问题调查与回归验证门禁</Typography.Text><div>运行追踪 → 特征签名 → 问题案例 → 修复 → 数据集 A/B 回归 → 验证通过</div></div>
      {!detail && <Button type="primary" ghost loading={saving} onClick={() => void track()}>跟踪此特征</Button>}
    </div>
    {error && <Alert type="error" showIcon message="调查信息不可用" description={error} />}

    {detail ? <div className="investigation-case-card">
      <div className="investigation-case-title">
        <Space wrap>
          <Tag color={statusColor[detail.case.status] ?? 'default'}>{detail.case.status}</Tag>
          <Tag color={severityColor[detail.case.severity] ?? 'default'}>{detail.case.severity}</Tag>
          <Typography.Text strong>{detail.case.title}</Typography.Text>
          <Typography.Text code>{detail.case.signatureId}</Typography.Text>
        </Space>
        <Space>
          {onReplay && <Button size="small" onClick={() => onReplay(detail.case.primaryNodeId ?? undefined)}>回放证据</Button>}
          <Button size="small" onClick={() => void track()} loading={saving}>关联当前运行追踪</Button>
        </Space>
      </div>
      <div className="investigation-stats">
        <Statistic title="发生次数" value={detail.case.occurrenceCount} />
        <Statistic title="关联证据数" value={detail.evidenceRuns.length} />
        <Statistic title="回归门禁数" value={detail.verificationEvidence?.length ?? 0} />
        <Statistic title="重新打开次数" value={detail.case.reopenCount} />
        <Statistic title="最近发生" value={new Date(detail.case.lastSeenAt).toLocaleDateString('zh-CN')} />
      </div>
      <Typography.Text type="secondary">{detail.case.fingerprint}</Typography.Text>
      <div className="investigation-notes">
        <div><Typography.Text strong>根因</Typography.Text><Input.TextArea rows={2} value={rootCause} onChange={(e) => setRootCause(e.target.value)} placeholder="填写已确认的原因，而非表面现象" /></div>
        <div><Typography.Text strong>{nextAction?.status === 'Verified' ? '验证结论' : '处理 / 流转说明'}</Typography.Text><Input.TextArea rows={2} value={note} onChange={(e) => setNote(e.target.value)} placeholder="说明变更内容，或回归证据所证明的结论" /></div>
      </div>
      <Space wrap style={{ marginTop: 8 }}>
        <Button size="small" loading={saving} onClick={() => void update({ rootCause })}>保存根因</Button>
        {nextAction && <Button size="small" type="primary" loading={saving}
          disabled={nextAction.status === 'Verified' && !freshPassed}
          title={nextAction.status === 'Verified' && !freshPassed ? '请先在最近一次处理完成后运行新的数据集回归验证，并确保通过。' : undefined}
          onClick={() => {
            const patch: Record<string, unknown> = { status: nextAction.status, rootCause };
            if (nextAction.noteField) patch[nextAction.noteField] = note;
            void update(patch);
          }}>{nextAction.label}</Button>}
        {detail.case.status === 'Resolved' && <Button size="small" onClick={() => void update({ status: 'Investigating' })}>返回调查</Button>}
      </Space>
      {detail.case.resolutionNote && <Alert type="info" showIcon message="处理记录" description={detail.case.resolutionNote} style={{ marginTop: 8 }} />}
      {detail.case.verificationNote && <Alert type="success" showIcon message="验证记录" description={detail.case.verificationNote} style={{ marginTop: 8 }} />}

      {detail.case.status === 'Resolved' && <div style={{ marginTop: 12, padding: 12, border: '1px solid rgba(128,128,128,.25)', borderRadius: 6 }}>
        <Typography.Text strong>数据集回归验证门禁</Typography.Text>
        <div><Typography.Text type="secondary">基线运行必须复现此特征；候选运行必须消除该特征，且不能引入质量回归。</Typography.Text></div>
        <Space wrap style={{ marginTop: 8 }}>
          <Select style={{ width: 240 }} placeholder="选择数据集" value={datasetId} onChange={(v) => { setDatasetId(v); setBaselineRunId(undefined); setCandidateRunId(undefined); }} options={datasets.map(x => ({ value: x.id, label: `${x.name} (${x.itemCount})` }))} />
          <Select style={{ width: 260 }} placeholder="选择基线验证运行" value={baselineRunId} onChange={setBaselineRunId} options={validationRuns.map(x => ({ value: x.runId, label: `${x.runId.slice(0, 14)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
          <Select style={{ width: 260 }} placeholder="选择候选验证运行" value={candidateRunId} onChange={setCandidateRunId} options={validationRuns.map(x => ({ value: x.runId, label: `${x.runId.slice(0, 14)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
        </Space>
        <Input value={gateNote} onChange={(e) => setGateNote(e.target.value)} placeholder="可选：补充证据说明或依赖变更" style={{ marginTop: 8 }} />
        <Button type="primary" ghost loading={saving} disabled={!datasetId || !baselineRunId || !candidateRunId || baselineRunId === candidateRunId} onClick={() => void evaluateGate()} style={{ marginTop: 8 }}>评估验证门禁</Button>
        {!freshPassed && <Alert type="warning" showIcon style={{ marginTop: 8 }} message="此问题处理完成后，需要有一条新的通过记录才能标记为已验证。" />}
      </div>}

      {latestVerification && <Alert
        style={{ marginTop: 10 }} showIcon type={latestVerification.gateStatus === 'Passed' ? 'success' : latestVerification.gateStatus === 'Failed' ? 'error' : 'warning'}
        message={<Space><span>最近一次回归门禁</span><Tag color={gateColor[latestVerification.gateStatus] ?? 'default'}>{({ Passed: '通过', Failed: '失败', Inconclusive: '结论不明' }[latestVerification.gateStatus] ?? latestVerification.gateStatus)}</Tag><Typography.Text code>{latestVerification.datasetName}</Typography.Text></Space>}
        description={<div>
          <div>特征命中数：基线 {latestVerification.baselineSignatureHits} → 候选 {latestVerification.candidateSignatureHits}；候选可检查结果 {latestVerification.candidateInspectableResults}。</div>
          <div>误判为 OK：{latestVerification.baselineSummary.falseOk} → {latestVerification.candidateSummary.falseOk}；误判为 NG：{latestVerification.baselineSummary.falseNg} → {latestVerification.candidateSummary.falseNg}；错误：{latestVerification.baselineSummary.errors} → {latestVerification.candidateSummary.errors}。</div>
          {latestVerification.reasons.map((x, i) => <div key={`reason-${i}`}>阻止原因：{x}</div>)}
          {latestVerification.warnings.map((x, i) => <div key={`warning-${i}`}>提示：{x}</div>)}
        </div>} />}

      {detail.verificationEvidence?.length > 0 && <Table size="small" pagination={false} rowKey="id" dataSource={detail.verificationEvidence.slice(0, 8)} style={{ marginTop: 8 }} columns={[
        { title: '门禁状态', dataIndex: 'gateStatus', width: 110, render: (v: string) => <Tag color={gateColor[v] ?? 'default'}>{({ Passed: '通过', Failed: '失败', Inconclusive: '结论不明' }[v] ?? v)}</Tag> },
        { title: '数据集', dataIndex: 'datasetName' },
        { title: '特征命中', width: 110, render: (_: unknown, row: InvestigationVerificationEvidence) => `${row.baselineSignatureHits} → ${row.candidateSignatureHits}` },
        { title: '误判为 OK', width: 100, render: (_: unknown, row: InvestigationVerificationEvidence) => `${row.baselineSummary.falseOk} → ${row.candidateSummary.falseOk}` },
        { title: '误判为 NG', width: 100, render: (_: unknown, row: InvestigationVerificationEvidence) => `${row.baselineSummary.falseNg} → ${row.candidateSummary.falseNg}` },
        { title: '创建时间', dataIndex: 'createdAt', width: 170, render: (v: string) => new Date(v).toLocaleString('zh-CN') }
      ]} />}

      <Table size="small" pagination={false} rowKey="runId" dataSource={detail.evidenceRuns.slice(0, 12)} style={{ marginTop: 8 }} columns={[
        { title: '证据运行', dataIndex: 'runId', render: (v: string) => <Typography.Text code>{v.slice(0, 12)}</Typography.Text> },
        { title: '运行时间', dataIndex: 'startedAt', width: 170, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
        { title: '判定结果', dataIndex: 'disposition', width: 100, render: (v: string) => <Tag>{v === 'OK' ? '合格' : v === 'NG' ? '不合格' : v}</Tag> },
        { title: '运行耗时（毫秒）', dataIndex: 'totalDurationMs', width: 110, render: (v: number) => v.toFixed(1) },
        { title: '节点耗时（毫秒）', dataIndex: 'nodeDurationMs', width: 110, render: (v?: number | null) => v == null ? '—' : v.toFixed(2) }
      ]} />
    </div> : <Alert type="info" showIcon message="此特征尚未登记为工程问题" description="当故障需要明确负责人、调查、处理和验证时，可创建问题案例，避免信息只留在运行追踪中。" />}

    {trends && <div className="signature-trends">
      <div className="investigation-stats">
        <Statistic title="Tracked cases" value={trends.trackedCases} />
        <Statistic title="Active" value={trends.activeCases} />
        <Statistic title="Verified / closed" value={trends.verifiedOrClosedCases} />
        <Statistic title="30d linked evidence" value={trends.evidenceOccurrences} />
      </div>
      <Table size="small" pagination={false} rowKey="caseId" dataSource={trends.topSignatures} columns={[
        { title: 'Top signature · 30d', dataIndex: 'title' },
        { title: 'Node', dataIndex: 'primaryNodeType', width: 150, render: (v?: string | null) => v ?? 'workflow' },
        { title: 'Category', dataIndex: 'category', width: 160, render: (v: string) => <Tag>{v}</Tag> },
        { title: 'Status', dataIndex: 'status', width: 110, render: (v: string) => <Tag color={statusColor[v] ?? 'default'}>{v}</Tag> },
        { title: 'Occurrences', dataIndex: 'occurrences', width: 100 },
        { title: 'Last seen', dataIndex: 'lastSeenAt', width: 120, render: (v: string) => v && v !== '0001-01-01T00:00:00+00:00' ? new Date(v).toLocaleDateString() : '—' }
      ]} />
      {trends.timeline.length > 0 && <div className="signature-trend-strip" title="Tracked signature evidence by day">
        {trends.timeline.slice(-30).map((x) => <span key={x.day} style={{ height: `${Math.max(6, Math.min(42, x.total * 6))}px` }} title={`${x.day}: ${x.total}`} />)}
      </div>}
    </div>}
  </section>;
}
