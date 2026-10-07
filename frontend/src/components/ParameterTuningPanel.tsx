import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Col, Divider, Input, InputNumber, Modal, Progress, Row, Select, Space, Statistic, Switch, Table, Tag, Typography, message } from 'antd';
import ImageViewer from './ImageViewer';
import type {
  NodeCatalogItem, ParameterDescriptor, ParameterTuningPreviewResult, ValidationDatasetDetail,
  ValidationDatasetSummary, ValidationRunRecord, VisionRoi, WorkflowPayload
} from '../types';
import { localizeStatus } from '../i18n';

type Props = {
  open: boolean;
  onClose: () => void;
  currentWorkflow: WorkflowPayload;
  catalog: NodeCatalogItem[];
  initialNodeId?: string;
  onApplyWorkflow: (workflow: WorkflowPayload) => void;
};

type ParamMap = Record<string, unknown>;
const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const classificationColor: Record<string, string> = { TRUE_OK: 'green', TRUE_NG: 'green', FALSE_OK: 'red', FALSE_NG: 'orange', ERROR: 'volcano' };

function ParameterEditor({ descriptor, value, onChange }: { descriptor: ParameterDescriptor; value: unknown; onChange: (value: unknown) => void }) {
  const effective = value ?? descriptor.defaultValue;
  if (descriptor.type === 'number') return <InputNumber style={{ width: '100%' }} value={Number(effective ?? 0)} min={descriptor.min} max={descriptor.max} step={descriptor.step} onChange={onChange} />;
  if (descriptor.type === 'boolean') return <Switch checked={Boolean(effective)} onChange={onChange} />;
  if (descriptor.type === 'select') return <Select style={{ width: '100%' }} value={String(effective ?? '')} options={descriptor.options} onChange={onChange} />;
  if (descriptor.type === 'textarea') return <Input.TextArea autoSize={{ minRows: 3, maxRows: 8 }} value={String(effective ?? '')} onChange={(e) => onChange(e.target.value)} />;
  return <Input value={String(effective ?? '')} onChange={(e) => onChange(e.target.value)} />;
}

export default function ParameterTuningPanel({ open, onClose, currentWorkflow, catalog, initialNodeId, onApplyWorkflow }: Props) {
  const [workflow, setWorkflow] = useState<WorkflowPayload>(() => clone(currentWorkflow));
  const [nodeId, setNodeId] = useState<string>();
  const [datasets, setDatasets] = useState<ValidationDatasetSummary[]>([]);
  const [datasetId, setDatasetId] = useState<string>();
  const [dataset, setDataset] = useState<ValidationDatasetDetail>();
  const [itemId, setItemId] = useState<string>();
  const [preview, setPreview] = useState<ParameterTuningPreviewResult>();
  const [previewUrl, setPreviewUrl] = useState<string>();
  const [autoPreview, setAutoPreview] = useState(true);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [runs, setRuns] = useState<ValidationRunRecord[]>([]);
  const [validationBusy, setValidationBusy] = useState(false);
  const [baselineRunId, setBaselineRunId] = useState<string>();
  const [candidateRunId, setCandidateRunId] = useState<string>();
  const [messageApi, holder] = message.useMessage();
  const previewSeq = useRef(0);

  const selectedNode = workflow.nodes.find((x) => x.id === nodeId);
  const selectedCatalog = catalog.find((x) => x.type === selectedNode?.type);
  const parameters = (selectedNode?.parameters ?? {}) as ParamMap;
  const roi = parameters.roi as VisionRoi | undefined;
  const roiEnabled = Boolean(selectedCatalog?.capabilities?.supportsRoi || selectedCatalog?.inputs.some((x) => x.dataType === 'Image'));
  const tunableNodes = useMemo(() => workflow.nodes.filter((node) => {
    const meta = catalog.find((x) => x.type === node.type);
    return Boolean(meta && (meta.parameters.length > 0 || meta.capabilities?.supportsRoi || meta.inputs.some((x) => x.dataType === 'Image')));
  }), [workflow.nodes, catalog]);
  const completedRuns = runs.filter((x) => x.status === 'Completed' && x.summary);
  const baseline = completedRuns.find((x) => x.runId === baselineRunId)?.summary;
  const candidate = completedRuns.find((x) => x.runId === candidateRunId)?.summary;

  const loadDatasets = async () => {
    const response = await fetch('/api/validation/datasets');
    if (!response.ok) return;
    const data: ValidationDatasetSummary[] = await response.json();
    setDatasets(data);
    if (!datasetId && data.length) setDatasetId(data[0].id);
  };
  const loadDataset = async (id: string) => {
    const [detailResponse, runsResponse] = await Promise.all([
      fetch(`/api/validation/datasets/${id}`), fetch(`/api/validation/datasets/${id}/runs`)
    ]);
    if (detailResponse.ok) {
      const detail: ValidationDatasetDetail = await detailResponse.json();
      setDataset(detail);
      if (!detail.items.some((x) => x.itemId === itemId && x.replayReady)) setItemId(detail.items.find((x) => x.replayReady)?.itemId);
    }
    if (runsResponse.ok) setRuns(await runsResponse.json());
  };

  useEffect(() => {
    if (!open) return;
    const fresh = clone(currentWorkflow);
    setWorkflow(fresh);
    const preferred = fresh.nodes.some((x) => x.id === initialNodeId) ? initialNodeId : undefined;
    setNodeId(preferred ?? fresh.nodes.find((n) => {
      const meta = catalog.find((x) => x.type === n.type); return Boolean(meta?.parameters.length || meta?.capabilities?.supportsRoi);
    })?.id ?? fresh.nodes[0]?.id);
    setPreview(undefined); setPreviewUrl(undefined);
    void loadDatasets();
  }, [open]);
  useEffect(() => { if (open && datasetId) void loadDataset(datasetId); }, [open, datasetId]);
  useEffect(() => {
    if (!open || !datasetId || !runs.some((x) => x.status === 'Running')) return;
    const timer = window.setInterval(() => { void loadDataset(datasetId); }, 1000);
    return () => window.clearInterval(timer);
  }, [open, datasetId, runs]);

  const updateNode = (mutate: (parameters: ParamMap) => ParamMap) => {
    if (!nodeId) return;
    setWorkflow((current) => ({ ...current, nodes: current.nodes.map((node) => node.id === nodeId ? { ...node, parameters: mutate({ ...(node.parameters ?? {}) }) } : node) }));
  };
  const updateParameter = (key: string, value: unknown) => updateNode((p) => ({ ...p, [key]: value }));
  const updateRoi = (value?: VisionRoi) => updateNode((p) => { if (value) p.roi = value; else delete p.roi; return p; });

  const runPreview = async (fullWorkflow: boolean, quiet = false) => {
    if (!datasetId || !itemId || !nodeId) return;
    const seq = ++previewSeq.current;
    setPreviewBusy(true);
    try {
      const response = await fetch('/api/tuning/preview', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ datasetId, itemId, workflow, targetNodeId: nodeId, fullWorkflow })
      });
      const data = await response.json();
      if (seq !== previewSeq.current) return;
      if (!response.ok) { if (!quiet) messageApi.error(data.detail ?? data.error ?? '参数预览失败'); return; }
      const result = data as ParameterTuningPreviewResult;
      setPreview(result);
      setPreviewUrl(result.replay.previewAvailable ? `/api/runs/${result.replay.runId}/preview?t=${Date.now()}` : undefined);
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : '参数预览失败');
    } finally { if (seq === previewSeq.current) setPreviewBusy(false); }
  };

  useEffect(() => {
    if (!open || !autoPreview || !datasetId || !itemId || !nodeId) return;
    const timer = window.setTimeout(() => { void runPreview(false, true); }, 550);
    return () => window.clearTimeout(timer);
  }, [open, autoPreview, datasetId, itemId, nodeId, workflow]);

  const startValidation = async () => {
    if (!datasetId) return;
    setValidationBusy(true);
    try {
      const response = await fetch(`/api/validation/datasets/${datasetId}/runs`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sourceWorkflowRunId: 'parameter-tuning-v0.49', workflow })
      });
      const data = await response.json();
      if (!response.ok) return messageApi.error(data.detail ?? data.error ?? '数据集验证启动失败');
      setCandidateRunId(data.runId);
      messageApi.success('当前候选工作流的数据集验证已启动');
      await loadDataset(datasetId);
    } finally { setValidationBusy(false); }
  };

  const loadValidationCandidate = async (runId: string) => {
    const response = await fetch(`/api/validation/runs/${runId}/workflow`);
    if (!response.ok) return messageApi.error('无法加载验证候选项');
    const candidate: WorkflowPayload = await response.json();
    setWorkflow(candidate);
    if (!candidate.nodes.some((x) => x.id === nodeId)) setNodeId(candidate.nodes[0]?.id);
    messageApi.success('候选工作流已载入参数调优工作区');
  };

  const resetCandidate = () => {
    const fresh = clone(currentWorkflow); setWorkflow(fresh); setPreview(undefined); setPreviewUrl(undefined);
    if (!fresh.nodes.some((x) => x.id === nodeId)) setNodeId(fresh.nodes[0]?.id);
  };

  const nodeReport = preview?.selectedNodeReport;
  const progressRun = runs.find((x) => x.status === 'Running');

  return <Modal open={open} width={1680} title="参数调优工作区 · V0.49" footer={null} onCancel={onClose} destroyOnHidden>
    {holder}
    <Alert type="info" showIcon style={{ marginBottom: 10 }} message="快速参数 / ROI 预览为临时运行，不会写入运行追踪历史。使用“验证数据集”可生成可持久保存的回归证据和 A/B 指标。" />
    <Row gutter={10}>
      <Col span={5}>
        <Card size="small" title="1 · 数据集 / 样本">
          <Typography.Text type="secondary">数据集</Typography.Text>
          <Select style={{ width: '100%', margin: '4px 0 8px' }} value={datasetId} onChange={(v) => { setDatasetId(v); setItemId(undefined); setPreview(undefined); }} options={datasets.map((x) => ({ value: x.id, label: `${x.name} (${x.itemCount})` }))} />
          <Typography.Text type="secondary">样本</Typography.Text>
          <Select style={{ width: '100%', marginTop: 4 }} value={itemId} onChange={(v) => { setItemId(v); setPreview(undefined); }} options={(dataset?.items ?? []).map((x) => ({ value: x.itemId, disabled: !x.replayReady, label: `${x.expectedDisposition} · ${x.displayName} · ${x.sourceKind}` }))} />
          {dataset && <div style={{ marginTop: 8 }}><Tag>{dataset.items.length} 个样本</Tag><Tag color={dataset.dataset.topologyHash ? 'blue' : 'gold'}>{dataset.dataset.topologyHash ? '拓扑已锁定' : '等待拓扑'}</Tag></div>}
        </Card>
        <Card size="small" title="2 · 调优节点" style={{ marginTop: 10 }}>
          <Select style={{ width: '100%' }} value={nodeId} onChange={(v) => { setNodeId(v); setPreview(undefined); }} options={tunableNodes.map((x) => ({ value: x.id, label: `${x.name ?? x.id} · ${x.type}` }))} />
          {selectedCatalog && <><Typography.Title level={5} style={{ margin: '10px 0 2px' }}>{selectedCatalog.displayName}</Typography.Title><Typography.Text type="secondary">{selectedCatalog.description}</Typography.Text></>}
        </Card>
        <Card size="small" title="参数" style={{ marginTop: 10, maxHeight: 520, overflow: 'auto' }}>
          {!selectedCatalog?.parameters.length && !roiEnabled && <Typography.Text type="secondary">此节点没有可调参数。</Typography.Text>}
          {selectedCatalog?.parameters.map((descriptor) => <div key={descriptor.name} style={{ marginBottom: 10 }}>
            <Typography.Text strong>{descriptor.label}{descriptor.unit ? ` (${descriptor.unit})` : ''}</Typography.Text>
            {descriptor.description && <div><Typography.Text type="secondary" style={{ fontSize: 12 }}>{descriptor.description}</Typography.Text></div>}
            <div style={{ marginTop: 4 }}><ParameterEditor descriptor={descriptor} value={parameters[descriptor.name]} onChange={(value) => updateParameter(descriptor.name, value)} /></div>
          </div>)}
          {roiEnabled && <Tag color="blue">可在图像查看器中编辑 ROI</Tag>}
        </Card>
      </Col>

      <Col span={12}>
        <Card size="small" title="3 · 实时离线预览" extra={<Space><span>自动预览</span><Switch size="small" checked={autoPreview} onChange={setAutoPreview} /><Button size="small" loading={previewBusy} onClick={() => void runPreview(false)}>运行当前节点</Button><Button size="small" type="primary" ghost loading={previewBusy} onClick={() => void runPreview(true)}>运行完整工作流</Button></Space>}>
          <div style={{ height: 510 }}>
            <ImageViewer previewUrl={previewUrl} imageWidth={preview?.replay.previewWidth} imageHeight={preview?.replay.previewHeight} overlays={preview?.replay.overlays ?? []} roi={roi} roiEnabled={roiEnabled} roiTargetLabel={selectedNode?.name ?? selectedNode?.id} loading={previewBusy} onRoiChange={updateRoi} />
          </div>
          <Space wrap style={{ marginTop: 8 }}>
            {preview && <Tag color={preview.replay.success ? 'green' : 'red'}>{preview.replay.success ? '成功' : '失败'}</Tag>}
            {preview && <Tag>{preview.replay.totalDurationMs.toFixed(2)} 毫秒</Tag>}
            {preview?.classification && <Tag color={classificationColor[preview.classification] ?? 'default'}>{preview.expectedDisposition === 'OK' ? '合格' : '不合格'} → {preview.actualDisposition === 'OK' ? '合格' : '不合格'} · {({ TRUE_OK: '正确判定为合格', TRUE_NG: '正确判定为不合格', FALSE_OK: '误判为合格', FALSE_NG: '误判为不合格', ERROR: '错误' }[preview.classification] ?? preview.classification)}</Tag>}
            {preview?.replay.error && <Tag color="red">{preview.replay.error}</Tag>}
          </Space>
        </Card>
        <Row gutter={10} style={{ marginTop: 10 }}>
          <Col span={12}><Card size="small" title="节点输入"><pre style={{ maxHeight: 180, overflow: 'auto', whiteSpace: 'pre-wrap' }}>{JSON.stringify(nodeReport?.inputs ?? {}, null, 2)}</pre></Card></Col>
          <Col span={12}><Card size="small" title="节点输出"><pre style={{ maxHeight: 180, overflow: 'auto', whiteSpace: 'pre-wrap' }}>{JSON.stringify(nodeReport?.outputs ?? {}, null, 2)}</pre></Card></Col>
        </Row>
        <Card size="small" title="节点摘要" style={{ marginTop: 10 }}>
          <Space wrap>{nodeReport && <><Tag color={nodeReport.success ? 'green' : 'red'}>{nodeReport.success ? '成功' : '错误'}</Tag><Tag>{nodeReport.durationMs.toFixed(3)} 毫秒</Tag><Tag>{localizeStatus(nodeReport.phase)}</Tag></>}</Space>
          <pre style={{ maxHeight: 150, overflow: 'auto', whiteSpace: 'pre-wrap' }}>{JSON.stringify(nodeReport?.summary ?? {}, null, 2)}</pre>
        </Card>
      </Col>

      <Col span={7}>
        <Card size="small" title="4 · 候选工作流操作">
          <Space wrap>
            <Button onClick={resetCandidate}>还原为设计器版本</Button>
            <Button type="primary" onClick={() => onApplyWorkflow(clone(workflow))}>应用到设计器</Button>
            <Button type="primary" ghost loading={validationBusy} disabled={!datasetId} onClick={() => void startValidation()}>验证数据集</Button>
          </Space>
          {progressRun && <div style={{ marginTop: 10 }}><Typography.Text>验证进度 {progressRun.completedCount}/{progressRun.requestedCount}</Typography.Text><Progress percent={progressRun.requestedCount ? Math.round(progressRun.completedCount / progressRun.requestedCount * 100) : 0} /></div>}
        </Card>
        <Card size="small" title="已保存的验证候选项" style={{ marginTop: 10 }}>
          <Table size="small" pagination={{ pageSize: 6 }} rowKey="runId" dataSource={runs} columns={[
            { title: '状态', dataIndex: 'status', width: 88, render: (v: string) => <Tag color={v === 'Completed' ? 'green' : v === 'Running' ? 'blue' : 'red'}>{localizeStatus(v)}</Tag> },
            { title: '准确率', render: (_: unknown, r: ValidationRunRecord) => r.summary ? `${(r.summary.accuracy * 100).toFixed(1)}%` : '-' },
            { title: '误判 OK', render: (_: unknown, r: ValidationRunRecord) => r.summary?.falseOk ?? '-' },
            { title: '误判 NG', render: (_: unknown, r: ValidationRunRecord) => r.summary?.falseNg ?? '-' },
            { title: 'P95 耗时', render: (_: unknown, r: ValidationRunRecord) => r.summary ? `${r.summary.p95DurationMs.toFixed(1)} 毫秒` : '-' },
            { title: '', width: 60, render: (_: unknown, r: ValidationRunRecord) => <Button size="small" disabled={r.status === 'Running'} onClick={() => void loadValidationCandidate(r.runId)}>加载</Button> }
          ]} />
        </Card>
        <Card size="small" title="5 · A/B 回归对比" style={{ marginTop: 10 }}>
          <Select placeholder="选择基线运行" style={{ width: '100%', marginBottom: 6 }} value={baselineRunId} onChange={setBaselineRunId} options={completedRuns.map((x) => ({ value: x.runId, label: `${x.runId.slice(-8)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
          <Select placeholder="选择候选运行" style={{ width: '100%' }} value={candidateRunId} onChange={setCandidateRunId} options={completedRuns.map((x) => ({ value: x.runId, label: `${x.runId.slice(-8)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
          {baseline && candidate && <>
            <Divider style={{ margin: '10px 0' }} />
            <Row gutter={6}>
              <Col span={12}><Statistic title="准确率变化" precision={2} suffix="百分点" value={(candidate.accuracy - baseline.accuracy) * 100} /></Col>
              <Col span={12}><Statistic title="P95 耗时变化" precision={2} suffix="毫秒" value={candidate.p95DurationMs - baseline.p95DurationMs} /></Col>
              <Col span={12}><Statistic title="误判 OK 变化" value={candidate.falseOk - baseline.falseOk} /></Col>
              <Col span={12}><Statistic title="误判 NG 变化" value={candidate.falseNg - baseline.falseNg} /></Col>
            </Row>
          </>}
        </Card>
      </Col>
    </Row>
  </Modal>;
}
