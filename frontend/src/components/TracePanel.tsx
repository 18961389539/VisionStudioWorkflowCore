import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Input, InputNumber, Modal, Select, Segmented, Space, Statistic, Table, Tag, Typography, message } from 'antd';
import type { RunTraceRecord, TraceStats } from '../types';
import { localizeStatus } from '../i18n';
import {
  DISPOSITION_OPTIONS, EXECUTION_STATUS_OPTIONS, TRACE_TIME_PRESETS,
  describeRange, type TraceFilterState, type TraceTimePreset,
} from '../traceQuery';
import { useTraceQuery } from '../useTraceQuery';
import StoragePanel from './StoragePanel';
import ReplayDebuggerPanel from './ReplayDebuggerPanel';
import RunObservabilityPanel from './RunObservabilityPanel';
import TraceAnalysisPanel from './TraceAnalysisPanel';
import InvestigationPanel from './InvestigationPanel';

type Props = { open: boolean; onClose: () => void };

const dispositionColor: Record<string, string> = { OK: 'green', NG: 'red', REVIEW: 'orange', ERROR: 'volcano' };
const executionColor: Record<string, string> = {
  Running: 'blue', Completed: 'green', Failed: 'red', Canceled: 'default', Unknown: 'default',
};

const DEFAULT_FILTER: TraceFilterState = { preset: 'today' };
const DEFAULT_PAGE = { current: 1, pageSize: 50 };

export default function TracePanel({ open, onClose }: Props) {
  // applied 是真正发请求的筛选（离散控件即时生效；文本输入点“查询”或回车生效）。
  const [filter, setFilter] = useState<TraceFilterState>(DEFAULT_FILTER);
  const [appliedFilter, setAppliedFilter] = useState<TraceFilterState>(DEFAULT_FILTER);
  const [page, setPage] = useState(DEFAULT_PAGE);
  const [selectedId, setSelectedId] = useState<string>();
  const [selectedRecord, setSelectedRecord] = useState<RunTraceRecord>();
  const [storageOpen, setStorageOpen] = useState(false);
  const [replayOpen, setReplayOpen] = useState(false);
  const [replayNodeId, setReplayNodeId] = useState<string>();
  const [messageApi, contextHolder] = message.useMessage();

  const { data, stats, storage, error, loading, loadedAt, reload } = useTraceQuery(open, appliedFilter, page);

  // 选中记录缓存：翻页/刷新后明细不消失（分析不被分页打断）；
  // 新页里出现同一条记录时用新数据覆盖缓存。
  const selected = useMemo(
    () => data?.items.find((x) => x.runId === selectedId) ?? selectedRecord,
    [data, selectedId, selectedRecord],
  );

  useEffect(() => {
    if (!data || !selectedId) return;
    const fresh = data.items.find((x) => x.runId === selectedId);
    if (fresh) setSelectedRecord(fresh);
  }, [data, selectedId]);

  useEffect(() => {
    if (!data || selectedId) return;
    if (data.items.length) {
      setSelectedId(data.items[0].runId);
      setSelectedRecord(data.items[0]);
    }
  }, [data, selectedId]);

  const applyFilters = (next: TraceFilterState, nextPage = DEFAULT_PAGE) => {
    setFilter(next);
    setAppliedFilter(next);
    setPage(nextPage);
  };

  const applyDiscrete = (patch: Partial<TraceFilterState>) => {
    const next = { ...filter, ...patch };
    applyFilters(next);
  };

  const resetFilters = () => applyFilters(DEFAULT_FILTER);

  const cleanup = async () => {
    const response = await fetch('/api/storage/cleanup', { method: 'POST' });
    const data = await response.json();
    if (!response.ok) return messageApi.error(data.error ?? '清理失败');
    messageApi.success(`保留策略清理了 ${data.changed ?? 0} 条记录 / 个文件`);
    reload();
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
    reload();
  };

  const statFallback: TraceStats = { total: 0, ok: 0, ng: 0, review: 0, errors: 0, averageDurationMs: 0 };
  const statSource = stats ?? statFallback;

  return (
    <Modal open={open} width={1380} title="运行追溯" footer={null} onCancel={onClose}>
      {contextHolder}
      <div className="trace-stats-caption">
        <span>统计范围：{describeRange(appliedFilter)}</span>
        <span> · 更新于 {loadedAt ? loadedAt.toLocaleTimeString('zh-CN', { hour12: false }) : '—'} · 与列表筛选一致</span>
      </div>
      <div className="trace-stats">
        <Statistic title="运行数" value={statSource.total} />
        <Statistic title="合格" value={statSource.ok} />
        <Statistic title="不合格" value={statSource.ng} />
        <Statistic title="待复核" value={statSource.review} />
        <Statistic title="错误" value={statSource.errors} />
        <Statistic title="平均耗时" value={statSource.averageDurationMs} suffix="毫秒" precision={1} />
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

      <Space wrap size={6} style={{ marginBottom: 10 }} role="search" aria-label="运行追溯筛选">
        <Segmented
          options={TRACE_TIME_PRESETS.map((preset) => ({ label: preset.label, value: preset.key }))}
          value={filter.preset}
          onChange={(value) => applyDiscrete({ preset: value as TraceTimePreset })}
          aria-label="时间范围预设"
        />
        {filter.preset === 'custom' && (
          <Space.Compact>
            <Input type="datetime-local" style={{ width: 210 }} aria-label="自定义开始时间"
                   value={filter.customFrom ? filter.customFrom.slice(0, 16) : ''}
                   onChange={(event) => setFilter((current) => ({ ...current, customFrom: event.target.value ? new Date(event.target.value).toISOString() : undefined }))} />
            <Input type="datetime-local" style={{ width: 210 }} aria-label="自定义结束时间"
                   value={filter.customTo ? filter.customTo.slice(0, 16) : ''}
                   onChange={(event) => setFilter((current) => ({ ...current, customTo: event.target.value ? new Date(event.target.value).toISOString() : undefined }))} />
          </Space.Compact>
        )}
        <Input allowClear placeholder="运行 ID 前缀 / 完整 ID" style={{ width: 230 }} value={filter.runIdPrefix ?? ''}
               onChange={(event) => setFilter((current) => ({ ...current, runIdPrefix: event.target.value }))}
               onPressEnter={() => applyFilters(filter)}
               aria-label="按运行 ID 前缀查找（不受时间窗限制）" />
        <Input allowClear placeholder="作业 ID" style={{ width: 120 }} value={filter.jobId ?? ''}
               onChange={(event) => setFilter((current) => ({ ...current, jobId: event.target.value }))}
               onPressEnter={() => applyFilters(filter)} aria-label="按作业 ID 筛选" />
        <InputNumber min={1} placeholder="版本" style={{ width: 84 }} value={filter.jobVersion ?? null}
                     onChange={(value) => setFilter((current) => ({ ...current, jobVersion: value ?? null }))}
                     aria-label="按作业版本筛选" />
        <Input allowClear placeholder="工作流关键字" style={{ width: 150 }} value={filter.workflow ?? ''}
               onChange={(event) => setFilter((current) => ({ ...current, workflow: event.target.value }))}
               onPressEnter={() => applyFilters(filter)} aria-label="按工作流名称或 ID 关键字筛选" />
        <Select allowClear placeholder="执行状态" style={{ width: 130 }} value={filter.executionStatus || undefined}
                options={EXECUTION_STATUS_OPTIONS.map((status) => ({ value: status, label: localizeStatus(status) }))}
                onChange={(value) => applyDiscrete({ executionStatus: value ?? undefined })}
                aria-label="按执行状态筛选" />
        <Select allowClear placeholder="检测判定" style={{ width: 120 }} value={filter.disposition || undefined}
                options={DISPOSITION_OPTIONS.map((item) => ({ value: item, label: localizeStatus(item) }))}
                onChange={(value) => applyDiscrete({ disposition: value ?? undefined })}
                aria-label="按检测判定筛选" />
        <Input allowClear placeholder="来源" style={{ width: 100 }} value={filter.source ?? ''}
               onChange={(event) => setFilter((current) => ({ ...current, source: event.target.value }))}
               onPressEnter={() => applyFilters(filter)} aria-label="按来源筛选" />
        <Input allowClear placeholder="错误码" style={{ width: 120 }} value={filter.errorCode ?? ''}
               onChange={(event) => setFilter((current) => ({ ...current, errorCode: event.target.value }))}
               onPressEnter={() => applyFilters(filter)} aria-label="按错误码筛选" />
        <Button type="primary" onClick={() => applyFilters(filter)}>查询</Button>
        <Button onClick={resetFilters}>重置</Button>
      </Space>

      {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 12 }} />}

      <div className="trace-grid">
        <Table
          size="small" rowKey="runId" loading={loading}
          dataSource={data?.items ?? []}
          pagination={{
            current: page.current, pageSize: page.pageSize, total: data?.total ?? 0,
            showSizeChanger: true, pageSizeOptions: [20, 50, 100],
            showTotal: (total) => `共 ${total.toLocaleString('zh-CN')} 条历史记录`,
            onChange: (current, pageSize) => setPage({ current, pageSize }),
          }}
          onRow={(row) => ({ onClick: () => { setSelectedId(row.runId); setSelectedRecord(row); } })}
          rowClassName={(row) => row.runId === selectedId ? 'trace-selected-row' : ''}
          columns={[
            { title: '时间', dataIndex: 'startedAt', width: 150, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
            { title: '来源', dataIndex: 'source', width: 90, render: (v: string) => localizeStatus(v) },
            { title: '作业', width: 110, render: (_: unknown, row) => row.jobId ? `${row.jobId} V${row.jobVersion}` : '临时运行' },
            {
              title: '执行状态', dataIndex: 'executionStatus', width: 95,
              render: (v: string) => <Tag color={executionColor[v] ?? 'default'}>{localizeStatus(v)}</Tag>,
            },
            { title: '判定', dataIndex: 'disposition', width: 90, render: (v: string) => <Tag color={dispositionColor[v]}>{localizeStatus(v)}</Tag> },
            { title: '错误码', dataIndex: 'errorCode', width: 100, render: (v: string | null) => v ? <Tag color="volcano">{v}</Tag> : '—' },
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
              : <div className="trace-no-preview">没有预览图像（未按保留策略采样、已过期清理，或本次执行没有产出预览）</div>}
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
