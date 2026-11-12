import { useCallback, useEffect, useMemo, useState } from 'react';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Alert, Button, Modal, Segmented, Space, Statistic, Table, Tag, Typography } from 'antd';
import type { AssetEventEnvelope, AssetHealthLevel, AssetHealthSnapshot, AssetKind, DiagnosticsSummary, NodeSpanRecord, NodeSpanSnapshot, RunCounterPersistenceStatus } from '../types';
import { localizeStatus } from '../i18n';
import { apiFetch } from '../apiClient';
import { apiUrl } from '../endpoints';

type Props = { open: boolean; onClose: () => void };
type Filter = 'All' | AssetKind;

const levelColor: Record<AssetHealthLevel, string> = {
  Unknown: 'default', Healthy: 'green', Degraded: 'orange', Faulted: 'red', Offline: 'default'
};
const severityColor: Record<string, string> = { Info: 'blue', Warning: 'orange', Error: 'red', Critical: 'magenta' };

/**
 * 节点 span 快照的形状校验。
 *
 * 端点是后加的：老宿主根本没有它（请求会失败，调用处已经兜住）。但"请求成功、body 不是这个形状"
 * 同样会发生（网关错误页、被中间件包了一层的响应、代理返回的 JSON 壳），而渲染期读
 * `spans.recent.length` 一抛，整个诊断面板会连带其他区块一起白屏。形状不符即当作"没有这份数据"，
 * 面板其余部分照常显示。
 */
function normalizeNodeSpans(raw: unknown): NodeSpanSnapshot | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const value = raw as Partial<NodeSpanSnapshot>;
  if (!Array.isArray(value.recent) || !Array.isArray(value.failures)) return undefined;
  return {
    capturedAt: typeof value.capturedAt === 'string' ? value.capturedAt : '',
    capacity: Number(value.capacity ?? 0),
    captured: Number(value.captured ?? 0),
    overwritten: Number(value.overwritten ?? 0),
    recent: value.recent,
    failures: value.failures
  };
}

export default function DiagnosticsPanel({ open, onClose }: Props) {
  const [assets, setAssets] = useState<AssetHealthSnapshot[]>([]);
  const [events, setEvents] = useState<AssetEventEnvelope[]>([]);
  const [summary, setSummary] = useState<DiagnosticsSummary>();
  const [counters, setCounters] = useState<RunCounterPersistenceStatus>();
  const [spans, setSpans] = useState<NodeSpanSnapshot>();
  const [filter, setFilter] = useState<Filter>('All');
  const [live, setLive] = useState<'connecting' | 'connected' | 'reconnecting' | 'offline'>('offline');
  const [error, setError] = useState<string>();

  const refresh = useCallback(async () => {
    try {
      const [a, e, s, c, n] = await Promise.all([
        apiFetch(apiUrl('diagnostics/assets')), apiFetch(apiUrl('diagnostics/events?take=250')), apiFetch(apiUrl('diagnostics/summary')),
        // 计数器落盘状态是附加信息：单独兜住失败，不让它把整个诊断面板判成"不可用"。
        apiFetch(apiUrl('diagnostics/run-counters')).catch(() => undefined),
        // 单次执行快照同理：老版本宿主没有这个端点，面板其余部分必须照常可用。
        apiFetch(apiUrl('diagnostics/node-spans?recent=64')).catch(() => undefined)
      ]);
      if (!a.ok || !e.ok || !s.ok) throw new Error('Diagnostics API unavailable');
      setAssets(await a.json()); setEvents(await e.json()); setSummary(await s.json()); setError(undefined);
      setCounters(c && c.ok ? await c.json() : undefined);
      setSpans(n && n.ok ? normalizeNodeSpans(await n.json()) : undefined);
    } catch (ex) { setError(ex instanceof Error ? ex.message : 'Diagnostics API unavailable'); }
  }, []);

  useEffect(() => {
    if (!open) return;
    void refresh();
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/diagnostics')
      .withAutomaticReconnect([0, 1000, 3000, 10000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('assetHealth', (asset: AssetHealthSnapshot) => {
      setAssets(current => {
        const next = current.filter(x => !(x.kind === asset.kind && x.id === asset.id));
        return [...next, asset].sort((x, y) => `${x.kind}:${x.id}`.localeCompare(`${y.kind}:${y.id}`));
      });
    });
    connection.on('assetEvent', (evt: AssetEventEnvelope) => {
      setEvents(current => [evt, ...current].slice(0, 500));
      // 删除事件必须同步移除资产，否则断线/事件期间已移除的资产会一直残留在列表里
      if (evt.type === 'AssetRemoved') {
        setAssets(current => current.filter(x => !(x.kind === evt.kind && x.id === evt.assetId)));
      }
    });
    connection.on('diagnosticsSummary', (next: DiagnosticsSummary) => setSummary(next));
    connection.onreconnecting(() => setLive('reconnecting'));
    // 重连后重新拉取快照与事件：断线期间的状态变化/删除可能已被广播错过，仅恢复连接标记会长期显示过期数据
    connection.onreconnected(() => { setLive('connected'); void refresh(); });
    connection.onclose(() => setLive('offline'));

    setLive('connecting');
    void connection.start().then(() => setLive('connected')).catch(() => setLive('offline'));
    return () => { if (connection.state !== HubConnectionState.Disconnected) void connection.stop(); };
  }, [open, refresh]);

  const filteredAssets = useMemo(() => assets.filter(x => filter === 'All' || x.kind === filter), [assets, filter]);
  const filteredEvents = useMemo(() => events.filter(x => filter === 'All' || x.kind === filter), [events, filter]);

  return <Modal open={open} onCancel={onClose} footer={null} width={1280} title="诊断中心 · 资产健康 / 实时事件" destroyOnHidden>
    <Space wrap className="diagnostics-toolbar">
      <Segmented<Filter> value={filter} onChange={setFilter} options={[{ label: '全部', value: 'All' }, { label: '相机', value: 'Camera' }, { label: '设备', value: 'Device' }, { label: '机器人', value: 'Robot' }]} />
      <Tag color={live === 'connected' ? 'green' : live === 'reconnecting' || live === 'connecting' ? 'orange' : 'red'}>
        实时连接 {live === 'connected' ? '已连接' : live === 'reconnecting' ? '正在重连' : live === 'connecting' ? '连接中' : '已断开'}
      </Tag>
      <Button onClick={() => void refresh()}>刷新状态</Button>
      <Typography.Text type="secondary">仅当状态、健康度或错误发生变化时才会发送事件，不会在每次轮询时重复发送。</Typography.Text>
    </Space>
    {error && <Alert type="warning" showIcon title={error} style={{ marginBottom: 12 }} />}
    {summary?.staleProviders && summary.staleProviders.length > 0 && (
      <Alert
        type="warning"
        showIcon
        style={{ marginBottom: 12 }}
        title={`诊断采样故障：${summary.staleProviders.join('、')} 的数据可能过期（已保留上次快照，暂不作为删除处理）`}
      />
    )}
    {counters?.degraded && (
      <Alert
        type="error"
        showIcon
        style={{ marginBottom: 12 }}
        title={`跨轮计数未落盘：已连续失败 ${counters.consecutiveFailures} 次，磁盘副本落后于内存（共 ${counters.counterCount} 个计数器）。此时重启会让"本班次良率 / 连续 NG"倒退。`}
        description={counters.lastError ? `最近错误：${counters.lastError}` : undefined}
      />
    )}

    <div className="diagnostics-summary">
      <Statistic title="资产总数" value={summary?.total ?? assets.length} />
      <Statistic title="正常" value={summary?.healthy ?? assets.filter(x => x.level === 'Healthy').length} />
      <Statistic title="降级" value={summary?.degraded ?? assets.filter(x => x.level === 'Degraded').length} />
      <Statistic title="故障" value={summary?.faulted ?? assets.filter(x => x.level === 'Faulted').length} />
      <Statistic title="离线" value={summary?.offline ?? assets.filter(x => x.level === 'Offline').length} />
    </div>

    <div className="diagnostics-grid">
      <section>
        <Typography.Title level={5}>资产健康状况</Typography.Title>
        <Table<AssetHealthSnapshot> size="small" pagination={false} rowKey={x => `${x.kind}:${x.id}`} dataSource={filteredAssets}
          columns={[
            { title: '类别', dataIndex: 'kind', width: 76, render: (v: string) => localizeStatus(v) },
            { title: '资产', width: 170, render: (_, x) => <><b>{x.name}</b><div className="muted-code">{x.id}</div></> },
            { title: '健康度', dataIndex: 'level', width: 90, render: (v: AssetHealthLevel) => <Tag color={levelColor[v]}>{localizeStatus(v)}</Tag> },
            { title: '状态', dataIndex: 'state', width: 170, render: (v: string) => localizeStatus(v) },
            { title: '活动', dataIndex: 'activity', width: 170, render: (v: string) => localizeStatus(v) },
            { title: '驱动', dataIndex: 'driver', width: 120, ellipsis: true },
            { title: '错误', dataIndex: 'error', ellipsis: true, render: (v?: string) => v ? <span className="diagnostic-error">{v}</span> : '—' }
          ]} />
      </section>
      <section>
        <Typography.Title level={5}>实时事件流</Typography.Title>
        <Table<AssetEventEnvelope> size="small" pagination={{ pageSize: 12, size: 'small' }} rowKey="eventId" dataSource={filteredEvents}
          columns={[
            { title: '时间', width: 94, render: (_, x) => new Date(x.timestamp).toLocaleTimeString('zh-CN') },
            { title: '级别', dataIndex: 'severity', width: 86, render: (v: string) => <Tag color={severityColor[v] ?? 'default'}>{localizeStatus(v)}</Tag> },
            { title: '资产', width: 140, render: (_, x) => <>{x.assetName}<div className="muted-code">{localizeStatus(x.kind)}/{x.assetId}</div></> },
            { title: '事件', dataIndex: 'type', width: 110, render: (v: string) => localizeStatus(v) },
            { title: '消息', dataIndex: 'message', ellipsis: true }
          ]} />
      </section>
    </div>

    {/* 单次执行事实（2026-11-12）：分位数（node-durations）回答"平时多慢"，
        这里回答"刚才那一次为什么慢"。数据来自引擎侧进程内环形缓冲，重启清零。 */}
    {spans && (
      <section className="diagnostics-spans">
        <Typography.Title level={5}>
          最近节点执行
          <Typography.Text type="secondary" className="diagnostics-spans-note">
            显示 {spans.recent.length} 条 / 本次进程共捕获 {spans.captured} 条
            {spans.overwritten > 0 ? `；已回卷 ${spans.overwritten} 条，以下不是全量` : ''}
          </Typography.Text>
        </Typography.Title>
        <Table<NodeSpanRecord> size="small" pagination={false} rowKey={x => String(x.sequence)} dataSource={spans.recent}
          scroll={{ y: 220 }}
          columns={[
            { title: '时间', width: 96, render: (_, x) => new Date(x.at).toLocaleTimeString('zh-CN', { hour12: false }) },
            { title: '节点', width: 210, render: (_, x) => <><b>{x.nodeType}</b><div className="muted-code">{x.nodeId}</div></> },
            { title: '运行 / 作用域', width: 160, ellipsis: true, render: (_, x) => <span className="muted-code">{x.runId ?? '—'}{x.scope ? ` / ${x.scope}` : ''}</span> },
            {
              title: '耗时', width: 96, align: 'right',
              render: (_, x) => <span className={x.timedOut ? 'diagnostic-error' : undefined}>{x.durationMs.toFixed(1)} ms</span>
            },
            {
              title: '状态', width: 96,
              render: (_, x) => x.timedOut
                ? <Tag color="orange">超时</Tag>
                : x.status === 'Error' ? <Tag color="red">失败</Tag> : <Tag color="green">成功</Tag>
            },
            { title: '错误', dataIndex: 'error', ellipsis: true, render: (v?: string | null) => v ? <span className="diagnostic-error">{v}</span> : '—' }
          ]} />
      </section>
    )}
  </Modal>;
}
