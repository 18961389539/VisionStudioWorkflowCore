import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Card, Col, Divider, Input, Modal, Progress, Row, Select, Space, Statistic, Table, Tag, Typography, message } from 'antd';
import type { MediaItemDescriptor, RunTraceRecord, ValidationDatasetDetail, ValidationDatasetSummary, ValidationResultRecord, ValidationRunRecord, WorkflowPayload } from '../types';
import { localizeStatus } from '../i18n';

type Props = { open: boolean; onClose: () => void; currentWorkflow?: WorkflowPayload };
const clsColor: Record<string, string> = { TRUE_OK: 'green', TRUE_NG: 'green', FALSE_OK: 'red', FALSE_NG: 'orange', ERROR: 'volcano' };

export default function DatasetValidationPanel({ open, onClose, currentWorkflow }: Props) {
  const [datasets, setDatasets] = useState<ValidationDatasetSummary[]>([]);
  const [datasetId, setDatasetId] = useState<string>();
  const [detail, setDetail] = useState<ValidationDatasetDetail>();
  const [traces, setTraces] = useState<RunTraceRecord[]>([]);
  const [selectedTraceIds, setSelectedTraceIds] = useState<string[]>([]);
  const [mediaItems, setMediaItems] = useState<MediaItemDescriptor[]>([]);
  const [selectedMediaRefs, setSelectedMediaRefs] = useState<string[]>([]);
  const [runs, setRuns] = useState<ValidationRunRecord[]>([]);
  const [selectedRunId, setSelectedRunId] = useState<string>();
  const [selectedRun, setSelectedRun] = useState<ValidationRunRecord>();
  const [workflowSourceRunId, setWorkflowSourceRunId] = useState<string>();
  const [workflowText, setWorkflowText] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [newName, setNewName] = useState('验证数据集');
  const [newDescription, setNewDescription] = useState('');
  const [baselineRunId, setBaselineRunId] = useState<string>();
  const [candidateRunId, setCandidateRunId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [messageApi, holder] = message.useMessage();

  const replayableTraces = useMemo(() => traces.filter((x) => x.hasReplayInput && !x.nodeReports.some((r) => r.nodeType === 'camera.syncCapture')), [traces]);
  const labeledMediaItems = useMemo(() => mediaItems.filter((x) => x.label === 'OK' || x.label === 'NG'), [mediaItems]);
  const baseline = runs.find((x) => x.runId === baselineRunId)?.summary;
  const candidate = runs.find((x) => x.runId === candidateRunId)?.summary;

  const loadDatasets = async () => {
    const response = await fetch('/api/validation/datasets');
    if (!response.ok) return;
    const data: ValidationDatasetSummary[] = await response.json();
    setDatasets(data);
    if (!datasetId && data.length) setDatasetId(data[0].id);
  };
  const loadTraces = async () => {
    const response = await fetch('/api/traces?take=500');
    if (response.ok) setTraces(await response.json());
  };
  const loadMedia = async () => {
    const response = await fetch('/api/media/items?take=500');
    if (response.ok) setMediaItems(await response.json());
  };
  const loadDetail = async (id: string) => {
    const [datasetResponse, runResponse] = await Promise.all([fetch(`/api/validation/datasets/${id}`), fetch(`/api/validation/datasets/${id}/runs`)]);
    if (datasetResponse.ok) {
      const data: ValidationDatasetDetail = await datasetResponse.json();
      setDetail(data);
      if (!workflowSourceRunId) {
        const traceItem = data.items.find((x) => x.sourceKind === 'TRACE' && x.sourceRunId);
        if (traceItem?.sourceRunId) setWorkflowSourceRunId(traceItem.sourceRunId);
      }
    }
    if (runResponse.ok) {
      const data: ValidationRunRecord[] = await runResponse.json();
      setRuns(data);
      if (!selectedRunId && data.length) setSelectedRunId(data[0].runId);
    }
  };
  const loadRun = async (id: string) => {
    const response = await fetch(`/api/validation/runs/${id}?includeResults=true`);
    if (response.ok) setSelectedRun(await response.json());
  };

  useEffect(() => { if (open) { void loadDatasets(); void loadTraces(); void loadMedia(); } }, [open]);
  useEffect(() => { if (open && datasetId) void loadDetail(datasetId); }, [open, datasetId]);
  useEffect(() => { if (open && selectedRunId) void loadRun(selectedRunId); }, [open, selectedRunId]);
  useEffect(() => {
    if (!open || !runs.some((x) => x.status === 'Running') || !datasetId) return;
    const timer = window.setInterval(() => { void loadDetail(datasetId); if (selectedRunId) void loadRun(selectedRunId); }, 1000);
    return () => window.clearInterval(timer);
  }, [open, datasetId, selectedRunId, runs]);

  const createDataset = async () => {
    const response = await fetch('/api/validation/datasets', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: newName, description: newDescription }) });
    const data = await response.json();
    if (!response.ok) return messageApi.error(data.detail ?? '创建数据集失败');
    setCreateOpen(false); setDatasetId(data.dataset.id); await loadDatasets();
  };
  const addSelected = async () => {
    if (!datasetId || selectedTraceIds.length === 0) return;
    const items = selectedTraceIds.map((id) => { const t = traces.find((x) => x.runId === id); return { sourceKind: 'TRACE', sourceRef: id, expectedDisposition: t?.disposition === 'OK' ? 'OK' : 'NG' }; });
    const response = await fetch(`/api/validation/datasets/${datasetId}/items`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ items }) });
    const data = await response.json();
    if (!response.ok) return messageApi.error(data.detail ?? data.error ?? '添加样本失败');
    setSelectedTraceIds([]); setDetail(data); await loadDatasets();
  };
  const addSelectedMedia = async () => {
    if (!datasetId || selectedMediaRefs.length === 0) return;
    const items = selectedMediaRefs.map((ref) => {
      const item = mediaItems.find((x) => x.relativePath === ref);
      const label = item?.label === 'NG' ? 'NG' : 'OK';
      return { sourceKind: 'MEDIA', sourceRef: ref, expectedDisposition: label };
    });
    const response = await fetch(`/api/validation/datasets/${datasetId}/items`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ items }) });
    const data = await response.json();
    if (!response.ok) return messageApi.error(data.detail ?? data.error ?? '添加媒体样本失败');
    setSelectedMediaRefs([]); setDetail(data); await loadDatasets();
  };
  const updateLabel = async (itemId: string, expectedDisposition: 'OK' | 'NG') => {
    if (!datasetId) return;
    const response = await fetch(`/api/validation/datasets/${datasetId}/items/${itemId}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedDisposition }) });
    if (response.ok) setDetail(await response.json());
  };
  const removeItem = async (itemId: string) => {
    if (!datasetId) return;
    const response = await fetch(`/api/validation/datasets/${datasetId}/items/${itemId}`, { method: 'DELETE' });
    if (response.ok) await loadDetail(datasetId);
  };
  const loadCandidateWorkflow = async () => {
    if (!workflowSourceRunId) return;
    const response = await fetch(`/api/traces/${workflowSourceRunId}/workflow`);
    if (!response.ok) return messageApi.error('加载工作流失败');
    const workflow: WorkflowPayload = await response.json();
    setWorkflowText(JSON.stringify(workflow, null, 2));
  };
  const startRun = async () => {
    if (!datasetId) return;
    if (!workflowSourceRunId && !workflowText.trim()) return messageApi.warning('请加载运行追踪中的工作流，或使用当前设计器中的工作流');
    let workflow: WorkflowPayload | undefined;
    if (workflowText.trim()) {
      try { workflow = JSON.parse(workflowText) as WorkflowPayload; } catch { return messageApi.error('候选工作流 JSON 格式无效'); }
    }
    setBusy(true);
    try {
      const response = await fetch(`/api/validation/datasets/${datasetId}/runs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceWorkflowRunId: workflowSourceRunId ?? 'designer-current', workflow }) });
      const data = await response.json();
      if (!response.ok) return messageApi.error(data.detail ?? data.error ?? '启动验证失败');
      setSelectedRunId(data.runId); messageApi.success('批量验证已启动'); await loadDetail(datasetId);
    } finally { setBusy(false); }
  };
  const cancelRun = async (runId: string) => {
    const response = await fetch(`/api/validation/runs/${runId}/cancel`, { method: 'POST' });
    if (response.ok) messageApi.success('已请求取消');
  };

  return <Modal open={open} width={1560} title="数据集 / 批量验证" footer={null} onCancel={onClose} destroyOnHidden>
    {holder}
    <Alert type="info" showIcon style={{ marginBottom: 12 }} message="V0.48 可在已保存的回放输入上运行同一候选工作流。数据集标签表示预期 OK/NG；批量结果会统计误判、失败节点和耗时分布。" />
    <Row gutter={12}>
      <Col span={5}>
        <Card size="small" title="数据集" extra={<Button size="small" onClick={() => setCreateOpen(true)}>新建</Button>}>
          <Select style={{ width: '100%', marginBottom: 8 }} value={datasetId} onChange={(v) => { setDatasetId(v); setSelectedRunId(undefined); setSelectedRun(undefined); setWorkflowText(''); setWorkflowSourceRunId(undefined); }} options={datasets.map((x) => ({ value: x.id, label: `${x.name} (${x.itemCount})` }))} />
          {detail && <>
            <Typography.Title level={5} style={{ margin: '8px 0 0' }}>{detail.dataset.name}</Typography.Title>
            <Typography.Paragraph type="secondary">{detail.dataset.description || '暂无说明'}</Typography.Paragraph>
            <Typography.Text code>{detail.dataset.topologyHash?.slice(0, 16) ?? '尚无拓扑信息'}</Typography.Text>
          </>}
        </Card>
        <Card size="small" title="添加可回放的运行追踪" style={{ marginTop: 12 }}>
          <Table size="small" pagination={{ pageSize: 6 }} rowKey="runId" dataSource={replayableTraces}
            rowSelection={{ selectedRowKeys: selectedTraceIds, onChange: (keys) => setSelectedTraceIds(keys.map(String)) }}
            columns={[
              { title: '判定', dataIndex: 'disposition', width: 65, render: (v: string) => <Tag color={v === 'OK' ? 'green' : 'red'}>{v === 'OK' ? '合格' : '不合格'}</Tag> },
              { title: '运行 ID', dataIndex: 'runId', render: (v: string) => <Typography.Text code>{v.slice(0, 8)}</Typography.Text> }
            ]} />
          <Button block type="primary" disabled={!selectedTraceIds.length} onClick={() => void addSelected()}>添加 {selectedTraceIds.length || ''} 条运行追踪</Button>
        </Card>
        <Card size="small" title="添加媒体库图像" style={{ marginTop: 12 }}>
          <Table size="small" pagination={{ pageSize: 6 }} rowKey="relativePath" dataSource={labeledMediaItems}
            rowSelection={{ selectedRowKeys: selectedMediaRefs, onChange: (keys) => setSelectedMediaRefs(keys.map(String)) }}
            columns={[
              { title: '标签', dataIndex: 'label', width: 72, render: (v: string) => <Tag color={v === 'OK' ? 'green' : v === 'NG' ? 'red' : 'default'}>{v === 'OK' ? '合格' : v === 'NG' ? '不合格' : v}</Tag> },
              { title: '图像', dataIndex: 'name', ellipsis: true }
            ]} />
          <Button block type="primary" ghost disabled={!selectedMediaRefs.length} onClick={() => void addSelectedMedia()}>添加 {selectedMediaRefs.length || ''} 张图像</Button>
        </Card>
      </Col>
      <Col span={19}>
        <Card size="small" title="已标注数据集">
          <Table size="small" pagination={{ pageSize: 7 }} rowKey="itemId" dataSource={detail?.items ?? []} columns={[
            { title: '#', dataIndex: 'sortOrder', width: 50 },
            { title: '来源类型', dataIndex: 'sourceKind', width: 80, render: (v: string) => <Tag color={v === 'TRACE' ? 'blue' : 'purple'}>{v === 'TRACE' ? '运行追踪' : v === 'MEDIA' ? '媒体' : v}</Tag> },
            { title: '来源', dataIndex: 'sourceRef', width: 180, render: (v: string) => <Typography.Text code>{v.length > 28 ? `${v.slice(0, 25)}…` : v}</Typography.Text> },
            { title: '名称', dataIndex: 'displayName' },
            { title: '预期结果', width: 110, render: (_: unknown, row) => <Select size="small" value={row.expectedDisposition} onChange={(v) => void updateLabel(row.itemId, v)} options={[{ value: 'OK', label: '合格' }, { value: 'NG', label: '不合格' }]} /> },
            { title: '可回放', dataIndex: 'replayReady', width: 75, render: (v: boolean) => <Tag color={v ? 'green' : 'red'}>{v ? '就绪' : '缺失'}</Tag> },
            { title: '', width: 70, render: (_: unknown, row) => <Button size="small" danger onClick={() => void removeItem(row.itemId)}>移除</Button> }
          ]} />
        </Card>

        <Row gutter={12} style={{ marginTop: 12 }}>
          <Col span={10}>
            <Card size="small" title="候选工作流">
              <Space.Compact style={{ width: '100%', marginBottom: 8 }}>
                <Select style={{ flex: 1 }} placeholder="选择来源运行追踪" value={workflowSourceRunId} onChange={setWorkflowSourceRunId} options={replayableTraces.map((x) => ({ value: x.runId, label: `${x.workflowName} · ${x.runId.slice(0, 8)}` }))} />
                <Button onClick={() => void loadCandidateWorkflow()}>加载追踪工作流</Button>
                <Button onClick={() => { if (!currentWorkflow) return; setWorkflowSourceRunId(undefined); setWorkflowText(JSON.stringify(currentWorkflow, null, 2)); }}>使用当前设计器</Button>
              </Space.Compact>
              <Input.TextArea rows={10} value={workflowText} onChange={(e) => setWorkflowText(e.target.value)} placeholder="加载来源工作流后，可编辑参数或 ROI JSON。留空时将直接使用来源工作流。" />
              <Button style={{ marginTop: 8 }} block type="primary" loading={busy} disabled={!detail?.items.length} onClick={() => void startRun()}>运行数据集验证</Button>
            </Card>
          </Col>
          <Col span={14}>
            <Card size="small" title="验证运行记录">
              <Table size="small" pagination={{ pageSize: 5 }} rowKey="runId" dataSource={runs} onRow={(row) => ({ onClick: () => setSelectedRunId(row.runId) })} columns={[
                { title: '状态', dataIndex: 'status', width: 95, render: (v: string) => <Tag color={v === 'Completed' ? 'green' : v === 'Running' ? 'blue' : v === 'Failed' ? 'red' : 'gold'}>{localizeStatus(v)}</Tag> },
                { title: '进度', width: 160, render: (_: unknown, row) => <Progress size="small" percent={row.requestedCount ? Math.round(100 * row.completedCount / row.requestedCount) : 0} /> },
                { title: '准确率', width: 90, render: (_: unknown, row) => row.summary ? `${(row.summary.accuracy * 100).toFixed(1)}%` : '-' },
                { title: '误判为 OK', width: 85, render: (_: unknown, row) => row.summary?.falseOk ?? '-' },
                { title: '误判为 NG', width: 85, render: (_: unknown, row) => row.summary?.falseNg ?? '-' },
                { title: '耗时 P95', width: 85, render: (_: unknown, row) => row.summary ? `${row.summary.p95DurationMs.toFixed(1)} 毫秒` : '-' },
                { title: '', width: 70, render: (_: unknown, row) => row.status === 'Running' ? <Button size="small" danger onClick={(e) => { e.stopPropagation(); void cancelRun(row.runId); }}>取消</Button> : null }
              ]} />
            </Card>
          </Col>
        </Row>

        {selectedRun && <Card size="small" title={`结果 · ${selectedRun.runId.slice(0, 16)}`} style={{ marginTop: 12 }}>
          {selectedRun.summary && <Row gutter={8} style={{ marginBottom: 10 }}>
            <Col span={4}><Statistic title="准确率" value={selectedRun.summary.accuracy * 100} precision={1} suffix="%" /></Col>
            <Col span={4}><Statistic title="误判为 OK" value={selectedRun.summary.falseOk} /></Col>
            <Col span={4}><Statistic title="误判为 NG" value={selectedRun.summary.falseNg} /></Col>
            <Col span={4}><Statistic title="错误数" value={selectedRun.summary.errors} /></Col>
            <Col span={4}><Statistic title="耗时 P95" value={selectedRun.summary.p95DurationMs} precision={1} suffix="毫秒" /></Col>
            <Col span={4}><Statistic title="耗时 P99" value={selectedRun.summary.p99DurationMs} precision={1} suffix="毫秒" /></Col>
          </Row>}
          <Table size="small" pagination={{ pageSize: 8 }} rowKey="itemId" dataSource={(selectedRun.results ?? []) as ValidationResultRecord[]} columns={[
            { title: '来源类型', dataIndex: 'sourceKind', width: 80, render: (v: string) => v === 'TRACE' ? '运行追踪' : v === 'MEDIA' ? '媒体' : v },
            { title: '来源', dataIndex: 'sourceRef', width: 160, render: (v: string) => <Typography.Text code>{v.length > 24 ? `${v.slice(0, 21)}…` : v}</Typography.Text> },
            { title: '预期结果', dataIndex: 'expectedDisposition', width: 85, render: (v: string) => v === 'OK' ? '合格' : v === 'NG' ? '不合格' : v },
            { title: '实际结果', dataIndex: 'actualDisposition', width: 85, render: (v: string) => v === 'OK' ? '合格' : v === 'NG' ? '不合格' : v },
            { title: '分类', dataIndex: 'classification', width: 105, render: (v: string) => <Tag color={clsColor[v] ?? 'default'}>{{ TRUE_OK: '正确判定为合格', TRUE_NG: '正确判定为不合格', FALSE_OK: '误判为合格', FALSE_NG: '误判为不合格', ERROR: '错误' }[v] ?? v}</Tag> },
            { title: '耗时', dataIndex: 'durationMs', width: 90, render: (v: number) => `${v.toFixed(2)} 毫秒` },
            { title: '失败节点', dataIndex: 'failedNodeIds', render: (v: string[]) => v?.length ? v.join(', ') : '-' },
            { title: '错误信息', dataIndex: 'error', render: (v?: string | null) => v ?? '-' }
          ]} />
        </Card>}

        <Card size="small" title="A/B 验证对比" style={{ marginTop: 12 }}>
          <Space style={{ marginBottom: 10 }}>
            <Select style={{ width: 250 }} placeholder="选择基线运行" value={baselineRunId} onChange={setBaselineRunId} options={runs.filter((x) => x.summary).map((x) => ({ value: x.runId, label: `${x.runId.slice(0, 12)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
            <Select style={{ width: 250 }} placeholder="选择候选运行" value={candidateRunId} onChange={setCandidateRunId} options={runs.filter((x) => x.summary).map((x) => ({ value: x.runId, label: `${x.runId.slice(0, 12)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
          </Space>
          {baseline && candidate ? <Row gutter={8}>
            <Col span={6}><Statistic title="准确率变化" value={(candidate.accuracy - baseline.accuracy) * 100} precision={1} suffix="百分点" /></Col>
            <Col span={6}><Statistic title="误判为 OK 变化" value={candidate.falseOk - baseline.falseOk} /></Col>
            <Col span={6}><Statistic title="误判为 NG 变化" value={candidate.falseNg - baseline.falseNg} /></Col>
            <Col span={6}><Statistic title="耗时 P95 变化" value={candidate.p95DurationMs - baseline.p95DurationMs} precision={1} suffix="毫秒" /></Col>
          </Row> : <Typography.Text type="secondary">请选择两条已完成的验证运行记录，以比较参数集。</Typography.Text>}
        </Card>
      </Col>
    </Row>

    <Modal open={createOpen} title="创建验证数据集" okText="创建" cancelText="取消" onOk={() => void createDataset()} onCancel={() => setCreateOpen(false)}>
      <Input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="数据集名称" />
      <Input.TextArea value={newDescription} onChange={(e) => setNewDescription(e.target.value)} rows={4} placeholder="用途 / 产品 / 验收范围" style={{ marginTop: 8 }} />
    </Modal>
  </Modal>;
}
