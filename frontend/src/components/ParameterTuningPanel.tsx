import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Col, Divider, Input, InputNumber, Modal, Progress, Row, Select, Space, Statistic, Switch, Table, Tag, Typography, message } from 'antd';
import ImageViewer from './ImageViewer';
import MatrixEditor from './MatrixEditor';
import type {
  NodeCatalogItem, ParameterDescriptor, ValidationRunRecord, VisionRoi, WorkflowPayload
} from '../types';
import { localizeStatus } from '../i18n';
import { isParameterVisible } from '../validation';
import { useTuningData } from '../useTuningData';
import { useTuningPreview } from '../useTuningPreview';
import { useTuningCandidates } from '../useTuningCandidates';
import type { PrepareWorkflowChange, PreparedWorkflowChange } from '../useWorkflowChanges';

type Props = {
  open: boolean;
  onClose: () => void;
  currentWorkflow: WorkflowPayload;
  catalog: NodeCatalogItem[];
  initialNodeId?: string;
  prepareWorkflowChange: PrepareWorkflowChange;
  onApplied: () => void;
};

type ParamMap = Record<string, unknown>;
const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const classificationColor: Record<string, string> = { TRUE_OK: 'green', TRUE_NG: 'green', FALSE_OK: 'red', FALSE_NG: 'orange', ERROR: 'volcano' };

function ParameterEditor({ descriptor, value, onChange, nodeType }: { descriptor: ParameterDescriptor; value: unknown; onChange: (value: unknown) => void; nodeType?: string }) {
  const effective = value ?? descriptor.defaultValue;
  if (nodeType === 'jlvision.matrixOp' && ['matrixA', 'matrixB'].includes(descriptor.name))
    return <MatrixEditor label={descriptor.label} value={String(effective ?? '')} onChange={onChange} />;
  if (descriptor.type === 'number') return <InputNumber aria-label={descriptor.label} style={{ width: '100%' }} value={descriptor.optional && effective == null ? null : Number(effective ?? 0)} min={descriptor.min} max={descriptor.max} step={descriptor.step} onChange={onChange} />;
  if (descriptor.type === 'boolean') return <Switch aria-label={descriptor.label} checked={Boolean(effective)} onChange={onChange} />;
  if (descriptor.type === 'select') return <Select aria-label={descriptor.label} style={{ width: '100%' }} value={String(effective ?? '')} options={descriptor.options} onChange={onChange} />;
  if (descriptor.type === 'textarea') return <Input.TextArea aria-label={descriptor.label} autoSize={{ minRows: 3, maxRows: 8 }} value={String(effective ?? '')} onChange={(e) => onChange(e.target.value)} />;
  return <Input aria-label={descriptor.label} value={String(effective ?? '')} onChange={(e) => onChange(e.target.value)} />;
}

export default function ParameterTuningPanel({ open, onClose, currentWorkflow, catalog, initialNodeId, prepareWorkflowChange, onApplied }: Props) {
  const [workflow, setWorkflow] = useState<WorkflowPayload>(() => clone(currentWorkflow));
  const [nodeId, setNodeId] = useState<string>();
  const [autoPreview, setAutoPreview] = useState(true);
  const [revision, setRevision] = useState(0);
  const [comparison, setComparison] = useState<{ datasetId?: string; baseline?: string; candidate?: string }>({});
  const [messageApi, holder] = message.useMessage();
  const sourceChange = useRef<PreparedWorkflowChange | undefined>(undefined);
  const data = useTuningData(open);
  const { datasets, datasetId, dataset, runs, itemId } = data;
  const previewControl = useTuningPreview({ open, workflow, nodeId, sample: data.sample, ready: data.ready, autoPreview, revision });
  const { preview, previewUrl, busy: previewBusy } = previewControl;
  const actions = useTuningCandidates({ open, datasetId, scopeRevision: data.scopeRevision, workflow, revision, runs, runsRevision: data.runsRevision, runsFresh: data.runsFresh,
    refreshRuns: data.refreshRuns, onStarted: data.acceptRun,
    onLoad: (candidate) => {
      setWorkflow(candidate); setRevision((value) => value + 1);
      setNodeId((current) => candidate.nodes.some((node) => node.id === current) ? current : candidate.nodes[0]?.id);
    }, success: (text) => { void messageApi.success(text); } });
  const { validationBusy } = actions;
  const baselineRunId = comparison.datasetId === datasetId ? comparison.baseline : undefined;
  const candidateRunId = comparison.datasetId === datasetId ? comparison.candidate : undefined;
  const chooseComparison = (kind: 'baseline' | 'candidate', runId: string) => setComparison((current) => ({
    ...(current.datasetId === datasetId ? current : {}), datasetId, [kind]: runId,
  }));
  useEffect(() => { if (actions.submitted) chooseComparison('candidate', actions.submitted.runId); }, [actions.submitted?.runId]);

  const selectedNode = workflow.nodes.find((x) => x.id === nodeId);
  const selectedCatalog = catalog.find((x) => x.type === selectedNode?.type);
  const parameters = (selectedNode?.parameters ?? {}) as ParamMap;
  const connectedDeviceValue = selectedCatalog?.type === 'device.writeTag'
    && workflow.edges.some(edge => edge.targetNodeId === nodeId && edge.targetPort === 'value');
  const connectedCoordinatePorts = selectedCatalog && ['coordinate.point', 'coordinate.point3D', 'coordinate.pose'].includes(selectedCatalog.type)
    ? workflow.edges.filter(edge => edge.targetNodeId === nodeId && ['x', 'y', 'z', 'thetaDeg'].includes(edge.targetPort)).map(edge => edge.targetPort) : [];
  const connectedMatrixPorts = selectedCatalog?.type === 'jlvision.matrixOp'
    ? workflow.edges.filter(edge => edge.targetNodeId === nodeId && ['matrixA', 'matrixB'].includes(edge.targetPort)).map(edge => edge.targetPort) : [];
  const httpPorts = selectedCatalog?.type === 'output.httpRequest' ? workflow.edges.filter(edge => edge.targetNodeId === nodeId).map(edge => edge.targetPort) : [];
  const overriddenHttpParameters = [...httpPorts.filter(port => ['url', 'body', 'idempotencyKey'].includes(port)), ...(httpPorts.includes('value') ? ['body', 'contentType'] : [])];
  const overriddenSaveParameters = selectedCatalog?.type === 'output.saveImage'
    ? workflow.edges.filter(edge => edge.targetNodeId === nodeId && ['path', 'partId'].includes(edge.targetPort)).map(edge => edge.targetPort) : [];
  const automaticPsfSize = selectedCatalog?.type === 'jlvision.restoration' && ['psfMotion', 'psfDefocus'].includes(String(parameters.operator))
    && workflow.edges.some(edge => edge.targetNodeId === nodeId && edge.targetPort === 'image');
  const roi = parameters.roi as VisionRoi | undefined;
  const roiEnabled = Boolean(selectedCatalog?.capabilities?.supportsRoi || selectedCatalog?.inputs.some((x) => x.dataType === 'Image'));
  const tunableNodes = useMemo(() => workflow.nodes.filter((node) => {
    const meta = catalog.find((x) => x.type === node.type);
    return Boolean(meta && (meta.parameters.length > 0 || meta.capabilities?.supportsRoi || meta.inputs.some((x) => x.dataType === 'Image')));
  }), [workflow.nodes, catalog]);
  const completedRuns = runs.filter((x) => x.status === 'Completed' && x.summary);
  const baseline = completedRuns.find((x) => x.runId === baselineRunId)?.summary;
  const candidate = completedRuns.find((x) => x.runId === candidateRunId)?.summary;

  useEffect(() => {
    if (!open) return;
    sourceChange.current?.cancel();
    sourceChange.current = prepareWorkflowChange('应用参数调优', 'edit');
    const fresh = clone(currentWorkflow);
    setWorkflow(fresh);
    const preferred = fresh.nodes.some((x) => x.id === initialNodeId) ? initialNodeId : undefined;
    setNodeId(preferred ?? fresh.nodes.find((n) => {
      const meta = catalog.find((x) => x.type === n.type); return Boolean(meta?.parameters.length || meta?.capabilities?.supportsRoi);
    })?.id ?? fresh.nodes[0]?.id);
    setRevision((value) => value + 1);
    return () => { sourceChange.current?.cancel(); sourceChange.current = undefined; };
  }, [open]);
  const updateNode = (mutate: (parameters: ParamMap) => ParamMap) => {
    if (!nodeId) return;
    setRevision((value) => value + 1);
    setWorkflow((current) => ({ ...current, nodes: current.nodes.map((node) => node.id === nodeId ? { ...node, parameters: mutate({ ...(node.parameters ?? {}) }) } : node) }));
  };
  const updateParameter = (key: string, value: unknown) => updateNode((p) => ({ ...p, [key]: value }));
  const updateRoi = (value?: VisionRoi) => updateNode((p) => { if (value) p.roi = value; else delete p.roi; return p; });

  const resetCandidate = () => {
    sourceChange.current?.cancel();
    sourceChange.current = prepareWorkflowChange('应用参数调优', 'edit');
    const fresh = clone(currentWorkflow); setWorkflow(fresh); setRevision((value) => value + 1);
    if (!fresh.nodes.some((x) => x.id === nodeId)) setNodeId(fresh.nodes[0]?.id);
  };

  const nodeReport = preview?.selectedNodeReport;
  const progressRun = runs.find((x) => x.status === 'Running');

  // 非模态（2026-10-25）：调参天生要"边看画布边改"。撤掉遮罩后画布仍可平移/缩放/选中，
  // 但弹窗宽度保持原值——面板内部是像素下限栅格，压窄会直接破版。
  return <Modal open={open} width={1680} mask={false} title="参数调优工作区" footer={null} onCancel={onClose} destroyOnHidden>
    {holder}
    <Alert type="info" showIcon style={{ marginBottom: 10 }} title="快速参数 / ROI 预览为临时运行，不会写入运行追踪历史。使用“验证数据集”可生成可持久保存的回归证据和 A/B 指标。" />
    {Object.entries(data.errors).map(([resource, text]) => <Alert key={resource} type="warning" showIcon style={{ marginBottom: 8 }}
      title={`${({ list: '数据集列表', detail: '数据集详情', runs: '验证记录' })[resource]}暂不可用`} description={text}
      action={<Button onClick={data.refresh}>重新读取</Button>} />)}
    {previewControl.error && <Alert type="error" showIcon title="离线预览失败" description={previewControl.error} style={{ marginBottom: 8 }} />}
    {actions.errors.candidate && <Alert type="error" showIcon title="候选载入未完成" description={actions.errors.candidate} style={{ marginBottom: 8 }} />}
    {actions.errors.validation && <Alert type="error" showIcon title="验证启动未完成或结果未确认" description={actions.errors.validation} style={{ marginBottom: 8 }}
      action={actions.uncertain && <Space><Button onClick={data.refreshRuns}>刷新验证记录</Button>
        <Button disabled={!actions.canAcknowledge} onClick={actions.acknowledge}>已核对记录，允许重新提交</Button></Space>} />}
    {actions.submittedStale && <Alert type="info" showIcon title="当前参数与已提交验证的快照不同" description="验证记录对应提交时的候选工作流；当前修改需另行验证。" style={{ marginBottom: 8 }} />}
    <Row gutter={10}>
      <Col span={5}>
        <Card size="small" title="1 · 数据集 / 样本">
          <Typography.Text type="secondary">数据集</Typography.Text>
          <Select style={{ width: '100%', margin: '4px 0 8px' }} aria-label="调优数据集" loading={data.loading.list} value={datasetId} onChange={data.chooseDataset} options={datasets.map((x) => ({ value: x.id, label: `${x.name} (${x.itemCount})` }))} />
          <Typography.Text type="secondary">样本</Typography.Text>
          <Select style={{ width: '100%', marginTop: 4 }} aria-label="调优样本" loading={data.loading.detail} disabled={!data.ready} value={itemId} onChange={data.chooseItem} options={(dataset?.items ?? []).map((x) => ({ value: x.itemId, disabled: !x.replayReady, label: `${x.expectedDisposition} · ${x.displayName} · ${x.sourceKind}` }))} />
          {dataset && !data.sample && <Typography.Text type="warning">当前数据集没有可离线重放的样本。</Typography.Text>}
          {dataset && <div style={{ marginTop: 8 }}><Tag>{dataset.items.length} 个样本</Tag><Tag color={dataset.dataset.topologyHash ? 'blue' : 'gold'}>{dataset.dataset.topologyHash ? '拓扑已锁定' : '等待拓扑'}</Tag></div>}
        </Card>
        <Card size="small" title="2 · 调优节点" style={{ marginTop: 10 }}>
          <Select style={{ width: '100%' }} aria-label="调优节点" value={nodeId} onChange={setNodeId} options={tunableNodes.map((x) => ({ value: x.id, label: `${x.name ?? x.id} · ${x.type}` }))} />
          {selectedCatalog && <><Typography.Title level={5} style={{ margin: '10px 0 2px' }}>{selectedCatalog.displayName}</Typography.Title><Typography.Text type="secondary">{selectedCatalog.description}</Typography.Text></>}
        </Card>
        <Card size="small" title="参数" style={{ marginTop: 10, maxHeight: 520, overflow: 'auto' }}>
          {!selectedCatalog?.parameters.length && !roiEnabled && <Typography.Text type="secondary">此节点没有可调参数。</Typography.Text>}
          {connectedMatrixPorts.length > 0 && <Typography.Paragraph type="secondary">{connectedMatrixPorts.join('、')} 使用接线数据；请在上游节点调节矩阵。</Typography.Paragraph>}
          {connectedCoordinatePorts.length > 0 && <Typography.Paragraph type="secondary">{connectedCoordinatePorts.join('、')} 来自上游输入；请在上游节点调节坐标。</Typography.Paragraph>}
          {connectedDeviceValue && <Typography.Paragraph type="secondary">写入值来自上游输入，备用类型和值不参与本次执行。</Typography.Paragraph>}
          {automaticPsfSize && <Typography.Paragraph type="secondary">模糊核宽高跟随参考图像，尺寸参数由输入确定。</Typography.Paragraph>}
          {selectedCatalog?.parameters.filter((descriptor) => !connectedCoordinatePorts.includes(descriptor.name) && !connectedMatrixPorts.includes(descriptor.name) && !overriddenHttpParameters.includes(descriptor.name) && !overriddenSaveParameters.includes(descriptor.name)
            && !(connectedDeviceValue && ['valueType', 'fallbackValue'].includes(descriptor.name))
            && !(automaticPsfSize && ['width', 'height'].includes(descriptor.name)) && isParameterVisible(descriptor, selectedCatalog.parameters, parameters)).map((descriptor) => <div key={descriptor.name} style={{ marginBottom: 10 }}>
            <Typography.Text strong>{descriptor.label}{descriptor.unit ? ` (${descriptor.unit})` : ''}</Typography.Text>
            {descriptor.description && <div><Typography.Text type="secondary" style={{ fontSize: 12 }}>{descriptor.description}</Typography.Text></div>}
            <div style={{ marginTop: 4 }}><ParameterEditor nodeType={selectedCatalog.type} descriptor={descriptor} value={parameters[descriptor.name]} onChange={(value) => updateParameter(descriptor.name, value)} /></div>
          </div>)}
          {roiEnabled && <Tag color="blue">可在图像查看器中编辑 ROI</Tag>}
        </Card>
      </Col>

      <Col span={12}>
        <Card size="small" title="3 · 实时离线预览" extra={<Space><span>自动预览</span><Switch aria-label="自动预览" size="small" checked={autoPreview} onChange={setAutoPreview} /><Button size="small" disabled={!data.ready || !nodeId} loading={previewBusy && !previewControl.automatic && !previewControl.fullWorkflow} onClick={() => void previewControl.run(false)}>运行当前节点</Button><Button size="small" type="primary" ghost disabled={!data.ready || !nodeId} loading={previewBusy && !previewControl.automatic && Boolean(previewControl.fullWorkflow)} onClick={() => void previewControl.run(true)}>运行完整工作流</Button></Space>}>
          {previewControl.backgroundStale && <Alert type="info" showIcon title="背景为上次预览，当前参数的结果尚未更新" style={{ marginBottom: 8 }} />}
          <div style={{ height: 510 }}>
            <ImageViewer previewUrl={previewUrl} imageWidth={previewControl.imageWidth} imageHeight={previewControl.imageHeight} overlays={preview?.replay.overlays ?? []} roi={roi} roiEnabled={roiEnabled && data.ready} roiTargetLabel={selectedNode?.name ?? selectedNode?.id} loading={previewBusy} onRoiChange={updateRoi} />
          </div>
          <Space wrap style={{ marginTop: 8 }}>
            {preview && <Tag>{preview.fullWorkflow ? '完整流程离线预览' : '当前节点离线预览'}</Tag>}
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
            <Button type="primary" onClick={() => {
              const change = sourceChange.current;
              if (change) void change.apply(clone(workflow)).then((applied) => { if (applied && sourceChange.current === change) onApplied(); });
            }}>应用到设计器</Button>
            <Button type="primary" ghost loading={validationBusy} disabled={!dataset || Boolean(data.errors.detail) || Boolean(data.loading.detail) || !dataset.items.length || actions.validatingCurrent || actions.uncertain} onClick={() => void actions.startValidation()}>验证数据集</Button>
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
            { title: '', width: 60, render: (_: unknown, r: ValidationRunRecord) => <Button size="small" loading={actions.candidateBusy} disabled={r.status === 'Running'} onClick={() => void actions.loadCandidate(r.runId)}>加载</Button> }
          ]} />
        </Card>
        <Card size="small" title="5 · A/B 回归对比" style={{ marginTop: 10 }}>
          <Select placeholder="选择基线运行" style={{ width: '100%', marginBottom: 6 }} aria-label="基线验证运行" value={baselineRunId} onChange={(runId) => chooseComparison('baseline', runId)} options={completedRuns.map((x) => ({ value: x.runId, label: `${x.runId.slice(-8)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
          <Select placeholder="选择候选运行" style={{ width: '100%' }} aria-label="候选验证运行" value={candidateRunId} onChange={(runId) => chooseComparison('candidate', runId)} options={completedRuns.map((x) => ({ value: x.runId, label: `${x.runId.slice(-8)} · ${(x.summary!.accuracy * 100).toFixed(1)}%` }))} />
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
