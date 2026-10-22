import { useCallback, useEffect, useMemo, useState } from 'react';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Alert, Button, Modal, Segmented, Space, Statistic, Table, Tag, Typography } from 'antd';
import type { AssetEventEnvelope, AssetHealthLevel, AssetHealthSnapshot, AssetKind, DiagnosticsSummary } from '../types';
import { localizeStatus } from '../i18n';

type Props = { open: boolean; onClose: () => void };
type Filter = 'All' | AssetKind;

const levelColor: Record<AssetHealthLevel, string> = {
  Unknown: 'default', Healthy: 'green', Degraded: 'orange', Faulted: 'red', Offline: 'default'
};
const severityColor: Record<string, string> = { Info: 'blue', Warning: 'orange', Error: 'red', Critical: 'magenta' };

export default function DiagnosticsPanel({ open, onClose }: Props) {
  const [assets, setAssets] = useState<AssetHealthSnapshot[]>([]);
  const [events, setEvents] = useState<AssetEventEnvelope[]>([]);
  const [summary, setSummary] = useState<DiagnosticsSummary>();
  const [filter, setFilter] = useState<Filter>('All');
  const [live, setLive] = useState<'connecting' | 'connected' | 'reconnecting' | 'offline'>('offline');
  const [error, setError] = useState<string>();

  const refresh = useCallback(async () => {
    try {
      const [a, e, s] = await Promise.all([
        fetch('/api/diagnostics/assets'), fetch('/api/diagnostics/events?take=250'), fetch('/api/diagnostics/summary')
      ]);
      if (!a.ok || !e.ok || !s.ok) throw new Error('Diagnostics API unavailable');
      setAssets(await a.json()); setEvents(await e.json()); setSummary(await s.json()); setError(undefined);
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
    {error && <Alert type="warning" showIcon message={error} style={{ marginBottom: 12 }} />}
    {summary?.staleProviders && summary.staleProviders.length > 0 && (
      <Alert
        type="warning"
        showIcon
        style={{ marginBottom: 12 }}
        message={`诊断采样故障：${summary.staleProviders.join('、')} 的数据可能过期（已保留上次快照，暂不作为删除处理）`}
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
  </Modal>;
}
