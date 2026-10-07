import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Checkbox, Col, Input, Modal, Row, Select, Space, Table, Tag, Typography, message } from 'antd';
import type { NodeRunReport, OfflineReplayContext, OfflineReplayResult, WorkflowPayload } from '../types';

type Props = { open: boolean; runId?: string; initialNodeId?: string; onClose: () => void };
type DebugMode = 'Full' | 'RunNode' | 'RunFromNode' | 'Breakpoints';

function cloneWorkflow(workflow: WorkflowPayload): WorkflowPayload {
  return JSON.parse(JSON.stringify(workflow)) as WorkflowPayload;
}

export default function ReplayDebuggerPanel({ open, runId, initialNodeId, onClose }: Props) {
  const [context, setContext] = useState<OfflineReplayContext>();
  const [workflow, setWorkflow] = useState<WorkflowPayload>();
  const [result, setResult] = useState<OfflineReplayResult>();
  const [selectedNodeId, setSelectedNodeId] = useState<string>();
  const [breakpoints, setBreakpoints] = useState<string[]>([]);
  const [parameterText, setParameterText] = useState('{}');
  const [running, setRunning] = useState(false);
  const [messageApi, holder] = message.useMessage();

  const selectedNode = workflow?.nodes.find((x) => x.id === selectedNodeId);
  const selectedReport: NodeRunReport | undefined = result?.nodeReports.find((x) => x.nodeId === selectedNodeId);

  const load = async () => {
    if (!runId) return;
    const response = await fetch(`/api/traces/${runId}/replay/context`);
    const data = await response.json();
    if (!response.ok) {
      messageApi.error(data.detail ?? data.error ?? '加载回放上下文失败');
      return;
    }
    const next = data as OfflineReplayContext;
    setContext(next);
    setWorkflow(cloneWorkflow(next.workflow));
    const preferred = initialNodeId && next.workflow.nodes.some((x) => x.id === initialNodeId) ? initialNodeId : undefined;
    setSelectedNodeId(preferred ?? next.orderedNodeIds[0] ?? next.workflow.nodes[0]?.id);
    setResult(undefined);
    setBreakpoints([]);
  };

  useEffect(() => { if (open && runId) void load(); }, [open, runId, initialNodeId]);
  useEffect(() => {
    const node = workflow?.nodes.find((x) => x.id === selectedNodeId);
    setParameterText(JSON.stringify(node?.parameters ?? {}, null, 2));
  }, [selectedNodeId, workflow]);

  const applyParameters = () => {
    if (!workflow || !selectedNodeId) return;
    try {
      const parsed = JSON.parse(parameterText) as Record<string, unknown>;
      setWorkflow({ ...workflow, nodes: workflow.nodes.map((node) => node.id === selectedNodeId ? { ...node, parameters: parsed } : node) });
      messageApi.success('参数已应用到回放工作区');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '参数 JSON 格式无效');
    }
  };

  const execute = async (mode: DebugMode) => {
    if (!runId || !workflow || !context?.replayable) return;
    if ((mode === 'RunNode' || mode === 'RunFromNode') && !selectedNodeId) return messageApi.warning('请先选择节点');
    if (mode === 'Breakpoints' && breakpoints.length === 0) return messageApi.warning('请至少选择一个断点');
    setRunning(true);
    try {
      const response = await fetch(`/api/traces/${runId}/replay`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          workflow,
          options: {
            mode,
            targetNodeId: mode === 'RunNode' || mode === 'RunFromNode' ? selectedNodeId : null,
            breakpoints: mode === 'Breakpoints' ? breakpoints : []
          }
        })
      });
      const data = await response.json();
      setResult(data as OfflineReplayResult);
      if (!response.ok) messageApi.error(data.error ?? data.detail ?? '回放失败');
      else messageApi.success(`${mode === 'Full' ? '完整回放' : mode === 'RunNode' ? '运行节点' : mode === 'RunFromNode' ? '从此处运行' : '运行到断点'}已完成 · ${Number(data.totalDurationMs).toFixed(2)} 毫秒`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '回放失败');
    } finally {
      setRunning(false);
    }
  };

  const comparisonById = useMemo(() => new Map((result?.comparison ?? []).map((x) => [x.nodeId, x])), [result]);

  return (
    <Modal open={open} width={1480} title={`离线回放 / 工作流调试器${runId ? ` · ${runId.slice(0, 12)}` : ''}`} footer={null} onCancel={onClose} destroyOnClose>
      {holder}
      {!context ? <Alert type="info" showIcon message="正在加载回放上下文…" /> : <>
        <Alert
          type={context.replayable ? 'success' : 'warning'} showIcon
          message={context.replayable ? `回放已就绪 · 输入${context.hasReplayInput ? '已保存' : '非必需'}` : '此追溯记录无法离线回放'}
          description={context.blockReason ?? context.executionModel}
          style={{ marginBottom: 12 }}
        />
        <Space wrap style={{ marginBottom: 12 }}>
          <Button type="primary" loading={running} disabled={!context.replayable} onClick={() => void execute('Full')}>完整回放</Button>
          <Button loading={running} disabled={!context.replayable || !selectedNodeId} onClick={() => void execute('RunNode')}>单步运行所选节点</Button>
          <Button loading={running} disabled={!context.replayable || !selectedNodeId} onClick={() => void execute('RunFromNode')}>从此处运行</Button>
          <Button loading={running} disabled={!context.replayable || breakpoints.length === 0} onClick={() => void execute('Breakpoints')}>运行到断点</Button>
          <Button onClick={() => context && setWorkflow(cloneWorkflow(context.workflow))}>重置参数</Button>
          {context.hasReplayInput && <Button onClick={() => window.open(`/api/traces/${context.sourceRunId}/replay/input`, '_blank')}>回放输入</Button>}
          {result?.previewAvailable && <Button onClick={() => window.open(`/api/runs/${result.runId}/preview?t=${Date.now()}`, '_blank')}>回放预览</Button>}
        </Space>

        <Row gutter={12}>
          <Col span={9}>
            <div className="panel-title">执行顺序 / 断点</div>
            <Table
              size="small" pagination={false} rowKey="id" scroll={{ y: 410 }}
              dataSource={(context.orderedNodeIds.length ? context.orderedNodeIds : workflow?.nodes.map((x) => x.id) ?? []).map((id, index) => {
                const node = workflow?.nodes.find((x) => x.id === id);
                const compare = comparisonById.get(id);
                return { id, index: index + 1, type: node?.type ?? '', compare };
              })}
              onRow={(row) => ({ onClick: () => setSelectedNodeId(row.id) })}
              rowClassName={(row) => row.id === selectedNodeId ? 'trace-selected-row' : ''}
              columns={[
                { title: '#', dataIndex: 'index', width: 42 },
                { title: '节点', dataIndex: 'id', width: 150 },
                { title: '类型', dataIndex: 'type' },
                { title: '差异', width: 82, render: (_: unknown, row) => row.compare ? <Tag color={row.compare.status === 'Same' ? 'green' : 'orange'}>{row.compare.status === 'Same' ? '无变化' : '已变化'}</Tag> : '-' },
                { title: 'BP', width: 48, render: (_: unknown, row) => <Checkbox checked={breakpoints.includes(row.id)} onChange={(e) => setBreakpoints((current) => e.target.checked ? [...current, row.id] : current.filter((x) => x !== row.id))} /> }
              ]}
            />
          </Col>
          <Col span={7}>
            <div className="panel-title">参数调整</div>
            <Typography.Text strong>{selectedNode?.name ?? selectedNode?.id ?? '请选择节点'}</Typography.Text>
            <Input.TextArea rows={17} value={parameterText} onChange={(e) => setParameterText(e.target.value)} style={{ marginTop: 8, fontFamily: 'monospace' }} />
            <Button block style={{ marginTop: 8 }} onClick={applyParameters}>应用参数 / 感兴趣区域</Button>
          </Col>
          <Col span={8}>
            <div className="panel-title">节点输入 / 输出检查</div>
            {!selectedReport ? <Alert type="info" message="执行回放或单步运行以查看运行时数据。" /> : <>
              <Alert type={selectedReport.success ? 'success' : 'error'} showIcon message={`${selectedReport.nodeType} · ${selectedReport.durationMs.toFixed(2)} 毫秒`} description={selectedReport.error ?? undefined} />
              <Typography.Title level={5}>输入</Typography.Title>
              <pre className="debug-json">{JSON.stringify(selectedReport.inputs ?? {}, null, 2)}</pre>
              <Typography.Title level={5}>输出</Typography.Title>
              <pre className="debug-json">{JSON.stringify(selectedReport.outputs ?? {}, null, 2)}</pre>
              <Typography.Title level={5}>摘要</Typography.Title>
              <pre className="debug-json">{JSON.stringify(selectedReport.summary ?? {}, null, 2)}</pre>
            </>}
          </Col>
        </Row>

        {result && <>
          <div className="panel-title" style={{ marginTop: 14 }}>基线与回放对比</div>
          <Alert
            type={!result.success || result.dispositionChanged ? 'warning' : 'success'} showIcon
            message={`回放${result.success ? '已完成' : '失败'} · ${result.totalDurationMs.toFixed(2)} 毫秒 · 基线判定 ${result.baselineDisposition ?? '-'}${result.dispositionChanged ? ' · 判定已变化' : ''}`}
            description={result.error ?? result.haltReason ?? result.executionModel}
            style={{ marginBottom: 8 }}
          />
          <Table
            size="small" pagination={false} rowKey="nodeId" dataSource={result.comparison}
            columns={[
              { title: '节点 ID', dataIndex: 'nodeId', width: 180 },
              { title: '类型', dataIndex: 'nodeType', width: 170 },
              { title: '基线耗时', dataIndex: 'baselineDurationMs', width: 90, render: (v?: number | null) => v == null ? '-' : `${v.toFixed(2)} 毫秒` },
              { title: '回放耗时', dataIndex: 'replayDurationMs', width: 90, render: (v?: number | null) => v == null ? '-' : `${v.toFixed(2)} 毫秒` },
              { title: '耗时变化（毫秒）', dataIndex: 'durationDeltaMs', width: 120, render: (v?: number | null) => v == null ? '-' : `${v >= 0 ? '+' : ''}${v.toFixed(2)}` },
              { title: '摘要', dataIndex: 'summaryChanged', width: 90, render: (v: boolean) => <Tag color={v ? 'orange' : 'green'}>{v ? '已变化' : '无变化'}</Tag> },
              { title: '状态', dataIndex: 'status', render: (v: string) => <Tag color={v === 'Same' ? 'green' : 'gold'}>{v === 'Same' ? '无变化' : '已变化'}</Tag> }
            ]}
          />
        </>}
      </>}
    </Modal>
  );
}
