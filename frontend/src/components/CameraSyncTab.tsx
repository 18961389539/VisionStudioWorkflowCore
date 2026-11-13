import { Alert, Button, Select, Space, Statistic, Table, Tag, Typography } from 'antd';
import type { CameraSynchronizationCaptureResult, CameraSynchronizationGroup, CameraSynchronizationGroupStatus, CameraSynchronizationStatistics } from '../types';
import { localizeStatus } from '../i18n';

export type CameraSyncTabProps = {
  busy?: string;
  selectedSyncId?: string;
  setSelectedSyncId: (value?: string) => void;
  syncGroups: CameraSynchronizationGroup[];
  refreshSyncGroups: (quiet?: boolean) => void | Promise<void>;
  refreshSyncStatus: (quiet?: boolean) => void | Promise<void>;
  runSyncTrigger: (scheduled: boolean) => void | Promise<void>;
  syncStatus?: CameraSynchronizationGroupStatus;
  syncCapture?: CameraSynchronizationCaptureResult;
  syncStatistics?: CameraSynchronizationStatistics;
  refreshSyncStatistics: (quiet?: boolean) => void | Promise<void>;
};

/** 「同步」标签页：同步组选择、立即/定时广播与偏差统计。 */
export default function CameraSyncTab(props: CameraSyncTabProps) {
  const {
    busy, selectedSyncId, setSelectedSyncId, syncGroups, refreshSyncGroups, refreshSyncStatus,
    runSyncTrigger, syncStatus, syncCapture, syncStatistics, refreshSyncStatistics,
  } = props;
  return (
    <>
<Typography.Title level={5}>多相机同步</Typography.Title>
<Alert type="info" showIcon title="PTP + GigE Vision Action Command" description="创建使用相同驱动的同步组，为每台相机设置一致的 Action 密钥，将 TriggerSource 设为 Action1，然后发送立即或定时广播。系统优先使用设备 / PTP 时间戳计算偏差；设备时间戳不可用时才回退到主机接收时间。" style={{ marginBottom: 10 }} />
<Space wrap style={{ marginBottom: 8 }}>
  <Select allowClear placeholder="选择已保存的同步组" value={selectedSyncId} onChange={setSelectedSyncId} style={{ minWidth: 240 }} options={syncGroups.map((x) => ({ value: x.id, label: `${x.name} · ${x.cameraIds.length} 台相机` }))} />
  <Button onClick={() => refreshSyncGroups()}>刷新分组</Button>
  <Button onClick={() => refreshSyncStatus()} disabled={!selectedSyncId}>刷新状态</Button>
  <Button type="primary" onClick={() => runSyncTrigger(false)} disabled={!selectedSyncId} loading={busy === 'sync-trigger'}>立即触发</Button>
  <Button onClick={() => runSyncTrigger(true)} disabled={!selectedSyncId} loading={busy === 'sync-schedule'}>定时触发</Button>
</Space>
{syncStatus && <>
  <Space wrap style={{ marginBottom: 8 }}>
    <Tag color={syncStatus.ready ? 'green' : 'red'}>{syncStatus.ready ? '就绪' : '未就绪'}</Tag>
    <Tag color={syncStatus.ptpReady ? 'green' : 'gold'}>PTP {syncStatus.ptpReady ? '已就绪' : '未锁定'}</Tag>
    <Tag>同步偏差 {syncStatus.latestSkewUs == null ? '—' : `${syncStatus.latestSkewUs.toFixed(1)} µs`} · {localizeStatus(syncStatus.timestampBasis)}</Tag>
    <Tag>配置哈希 {syncStatus.group.configurationHash.slice(0, 12)}</Tag>
  </Space>
  {syncStatus.error && <Alert type="warning" showIcon title={syncStatus.error} style={{ marginBottom: 8 }} />}
  <Table size="small" rowKey="cameraId" dataSource={syncStatus.members} pagination={false} columns={[
    { title: '相机', dataIndex: 'cameraId' },
    { title: 'PTP 状态', render: (_: unknown, row: CameraSynchronizationGroupStatus['members'][number]) => <Tag color={['Locked','Slave','Master'].includes(row.timeSync.state) ? 'green' : 'gold'}>{localizeStatus(row.timeSync.state)}</Tag> },
    { title: '时钟偏移', render: (_: unknown, row: CameraSynchronizationGroupStatus['members'][number]) => row.timeSync.offsetFromMasterNs == null ? '—' : `${row.timeSync.offsetFromMasterNs} ns` },
    { title: '帧序号', render: (_: unknown, row: CameraSynchronizationGroupStatus['members'][number]) => row.latestFrame ? `#${row.latestFrame.sequence}` : '—' }
  ]} />
</>}
{syncCapture && <Alert style={{ marginTop: 8 }} type={syncCapture.withinTolerance ? 'success' : 'warning'} showIcon title={`${syncCapture.scheduled ? '定时' : '立即'}采集 · 同步偏差 ${syncCapture.triggerSkewUs.toFixed(1)} µs（${localizeStatus(syncCapture.timestampBasis)}）`} description={`已确认设备数：${syncCapture.command.acknowledgedDevices}；允许偏差：${syncCapture.maxAllowedSkewUs.toFixed(1)} µs`} />}
<Space wrap style={{ marginTop: 10, marginBottom: 6 }}><Typography.Text strong>同步调试</Typography.Text><Button size="small" onClick={() => refreshSyncStatistics()} disabled={!selectedSyncId}>刷新统计</Button></Space>
{syncStatistics && <>
  <Alert
    showIcon
    type={syncStatistics.commissioning.status === 'Pass' ? 'success' : syncStatistics.commissioning.status === 'Fail' ? 'error' : 'info'}
    title={`调试评估：${syncStatistics.commissioning.status === 'InsufficientData' ? '数据不足' : localizeStatus(syncStatistics.commissioning.status)}`}
    description={syncStatistics.commissioning.reasons.join(' ')}
    style={{ marginBottom: 8 }}
  />
  <div className="camera-settings-grid">
    <Statistic title="样本数" value={syncStatistics.totalRuns} suffix={`/ ${syncStatistics.commissioning.minimumSamples} 个最低样本`} />
    <Statistic title="完成率" value={syncStatistics.completionRate * 100} precision={1} suffix="%" />
    <Statistic title="偏差合格率" value={syncStatistics.tolerancePassRate * 100} precision={1} suffix="%" />
    <Statistic title="偏差 P50" value={syncStatistics.p50SkewUs ?? 0} precision={1} suffix="µs" />
    <Statistic title="偏差 P95" value={syncStatistics.p95SkewUs ?? 0} precision={1} suffix="µs" />
    <Statistic title="偏差 P99" value={syncStatistics.p99SkewUs ?? 0} precision={1} suffix="µs" />
    <Statistic title="最大偏差" value={syncStatistics.maxSkewUs ?? 0} precision={1} suffix="µs" />
    <Statistic title="耗时 P95" value={syncStatistics.p95DurationMs ?? 0} precision={1} suffix="毫秒" />
  </div>
  <Typography.Text type="secondary">PTP 计时：{syncStatistics.devicePtpRuns} 次 · 主机接收时间回退：{syncStatistics.hostArrivalRuns} 次 · 失败：{syncStatistics.failedRuns} 次 · 已取消：{syncStatistics.cancelledRuns} 次</Typography.Text>
</>}
    </>
  );
}
