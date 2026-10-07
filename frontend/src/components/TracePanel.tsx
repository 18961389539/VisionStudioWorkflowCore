import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Modal, Space, Statistic, Table, Tag, Typography, message } from 'antd';
import type { RunTraceRecord, TraceStats, TraceStorageStatus } from '../types';
import { localizeStatus } from '../i18n';
import StoragePanel from './StoragePanel';
import ReplayDebuggerPanel from './ReplayDebuggerPanel';
import RunObservabilityPanel from './RunObservabilityPanel';
import TraceAnalysisPanel from './TraceAnalysisPanel';
import InvestigationPanel from './InvestigationPanel';

type Props = { open: boolean; onClose: () => void };

const dispositionColor: Record<string, string> = { OK: 'green', NG: 'red', REVIEW: 'orange', ERROR: 'volcano' };

export default function TracePanel({ open, onClose }: Props) {
  const [traces, setTraces] = useState<RunTraceRecord[]>([]);
  const [stats, setStats] = useState<TraceStats>({ total: 0, ok: 0, ng: 0, review: 0, errors: 0, averageDurationMs: 0 });
  const [selectedId, setSelectedId] = useState<string>();
  const [storage, setStorage] = useState<TraceStorageStatus>();
  const [storageOpen, setStorageOpen] = useState(false);
  const [replayOpen, setReplayOpen] = useState(false);
  const [replayNodeId, setReplayNodeId] = useState<string>();
  const [messageApi, contextHolder] = message.useMessage();
  const selected = useMemo(() => traces.find((x) => x.runId === selectedId), [traces, selectedId]);

  const refresh = async () => {
    const [listResponse, statsResponse, storageResponse] = await Promise.all([fetch('/api/traces?take=100'), fetch('/api/traces/stats?days=7'), fetch('/api/storage/status')]);
    if (listResponse.ok) {
      const data: RunTraceRecord[] = await listResponse.json();
      setTraces(data);
      if (!selectedId && data.length) setSelectedId(data[0].runId);
    }
    if (statsResponse.ok) setStats(await statsResponse.json());
    if (storageResponse.ok) setStorage(await storageResponse.json());
  };

  useEffect(() => { if (open) void refresh(); }, [open]);

  const cleanup = async () => {
    const response = await fetch('/api/storage/cleanup', { method: 'POST' });
    const data = await response.json();
    if (!response.ok) return messageApi.error(data.error ?? '清理失败');
    messageApi.success(`保留策略清理了 ${data.changed ?? 0} 条记录 / 个文件`);
    await refresh();
  };

  const openReplay = (nodeId?: string) => {
    setReplayNodeId(nodeId);
    setReplayOpen(true);
  };

  const disposition = async (value: 'OK' | 'NG' | 'REVIEW') => {
    if (!selected) return;
    const response = await fetch(`/api/traces/${selected.runId}/disposition`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ disposition: value })
    });
    const data = await response.json();
    if (!response.ok) return messageApi.error(data.error ?? '更新失败');
    messageApi.success(`运行记录 ${selected.runId.slice(0, 8)} → ${localizeStatus(value)}`);
    await refresh();
  };

  return (
    <Modal open={open} width={1380} title="运行追溯" footer={null} onCancel={onClose}>
      {contextHolder}
      <div className="trace-stats">
        <Statistic title="近 7 天运行数" value={stats.total} />
        <Statistic title="合格" value={stats.ok} />
        <Statistic title="不合格" value={stats.ng} />
        <Statistic title="待复核" value={stats.review} />
        <Statistic title="错误" value={stats.errors} />
        <Statistic title="平均耗时" value={stats.averageDurationMs} suffix="毫秒" precision={1} />
        <Statistic title="已索引记录" value={storage?.traceCount ?? 0} />
        <Statistic title="预览文件" value={storage?.previewCount ?? 0} />
        <Statistic title="回放输入" value={storage?.replayInputCount ?? 0} />
      </div>
      {storage && <Alert
        type="info" showIcon
        message={`SQLite ${(storage.databaseBytes / 1024 / 1024).toFixed(2)} MB · 文件 ${(storage.artifactBytes / 1024 / 1024).toFixed(2)} MB · 架构 v${storage.schema?.currentVersion ?? '?'} · 容量 ${localizeStatus(storage.capacity?.level) ?? '未知'} · 合格预览 1/${storage.retention.okPreviewSampleEvery} · 回放 1/${storage.retention.okReplaySampleEvery}`}
        action={<Space><Button size="small" onClick={() => setStorageOpen(true)}>存储维护</Button><Button size="small" onClick={() => void cleanup()}>清理过期数据</Button></Space>}
        style={{ marginBottom: 12 }}
      />}
      <div className="trace-grid">
        <Table
          size="small" pagination={{ pageSize: 10 }} rowKey="runId" dataSource={traces}
          onRow={(row) => ({ onClick: () => setSelectedId(row.runId) })}
          rowClassName={(row) => row.runId === selectedId ? 'trace-selected-row' : ''}
          columns={[
            { title: '时间', dataIndex: 'startedAt', width: 150, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
            { title: '来源', dataIndex: 'source', width: 100, render: (v: string) => localizeStatus(v) },
            { title: '作业', width: 110, render: (_: unknown, row) => row.jobId ? `${row.jobId} V${row.jobVersion}` : '临时运行' },
            { title: '判定', dataIndex: 'disposition', width: 95, render: (v: string) => <Tag color={dispositionColor[v]}>{localizeStatus(v)}</Tag> },
            { title: '耗时', dataIndex: 'totalDurationMs', width: 80, render: (v: number) => `${v.toFixed(1)} 毫秒` },
            { title: '运行 ID', dataIndex: 'runId', render: (v: string) => <Typography.Text code>{v.slice(0, 12)}</Typography.Text> }
          ]}
        />

        <section className="trace-detail">
          {!selected ? <div>请选择一条运行记录。</div> : <>
            <div className="trace-detail-heading">
              <div>
                <strong>{selected.workflowName}</strong>
                <div>{selected.jobId ? `${selected.jobId} · V${selected.jobVersion}` : selected.source}</div>
              </div>
              <Space>
                <Button size="small" onClick={() => disposition('OK')}>标记合格</Button>
                <Button size="small" danger onClick={() => disposition('NG')}>标记不合格</Button>
                <Button size="small" onClick={() => disposition('REVIEW')}>标记待复核</Button>
                <Button size="small" type="primary" ghost onClick={() => openReplay()}>回放 / 调试</Button>
              </Space>
            </div>
            {selected.error && <Alert type="error" showIcon message={selected.error} />}
            {selected.hasPreview
              ? <img className="trace-preview" src={`/api/traces/${selected.runId}/preview?t=${Date.now()}`} />
              : <div className="trace-no-preview">没有预览图像</div>}
            <div className="trace-meta">
              <span>工作流：{selected.workflowId}</span>
              <span>哈希：{selected.workflowHash?.slice(0, 16) ?? '临时工作流'}</span>
              <span>节点数：{selected.nodeCount}</span>
              <span>覆盖层：{selected.overlayCount}</span>
              <span>回放输入：{selected.hasReplayInput ? `有 · ${selected.replaySourceNodeId ?? '图像'}` : '无 / 不需要'}</span>
            </div>
            <Table
              size="small" pagination={false} rowKey={(row) => row.nodeId} dataSource={selected.nodeReports}
              columns={[
                { title: '节点', dataIndex: 'nodeType', width: 150 },
                { title: '阶段', dataIndex: 'phase', width: 80, render: (v: string) => v === 'Warmup' ? '预热' : '运行' },
                { title: '耗时（毫秒）', dataIndex: 'durationMs', width: 90, render: (v: number) => v.toFixed(2) },
                { title: '摘要', dataIndex: 'summary', render: (v: unknown) => <Typography.Text code>{JSON.stringify(v)}</Typography.Text> }
              ]}
            />
            <TraceAnalysisPanel runId={selected.runId} onReplay={openReplay} />
            <InvestigationPanel runId={selected.runId} onReplay={openReplay} />
            <RunObservabilityPanel runId={selected.runId} />
          </>}
        </section>
      </div>
      <StoragePanel open={storageOpen} onClose={() => setStorageOpen(false)} />
      <ReplayDebuggerPanel open={replayOpen} runId={selected?.runId} initialNodeId={replayNodeId} onClose={() => setReplayOpen(false)} />
    </Modal>
  );
}
