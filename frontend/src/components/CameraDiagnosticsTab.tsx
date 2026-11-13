import type { FormInstance } from 'antd';
import { Alert, Button, Form, Input, InputNumber, Select, Space, Statistic, Switch, Table, Tag, Typography } from 'antd';
import type { CameraAdapterDescriptor, CameraDescriptor, CameraSynchronizationCommissioningTest, CameraSynchronizationPtpDiagnostics, CameraSynchronizationRunRecord, GigENetworkDiagnostics } from '../types';
import { localizeStatus } from '../i18n';
import { formatBytes, type SyncGroupFormValues } from './cameraPanelShared';

export type CameraDiagnosticsTabProps = {
  busy?: string;
  selected?: CameraDescriptor;
  adapters: CameraAdapterDescriptor[];
  cameras: CameraDescriptor[];
  selectedSyncId?: string;
  syncForm: FormInstance;
  saveSyncGroup: (values: SyncGroupFormValues) => void | Promise<void>;
  syncPtpDiagnostics?: CameraSynchronizationPtpDiagnostics;
  refreshSyncPtpDiagnostics: (quiet?: boolean) => void | Promise<void>;
  gigeDiagnostics?: GigENetworkDiagnostics;
  refreshGigEDiagnostics: (quiet?: boolean) => void | Promise<void>;
  startGigENetworkTest: () => void;
  syncTestIterations: number;
  setSyncTestIterations: (value: number) => void;
  syncTests: CameraSynchronizationCommissioningTest[];
  syncTestScheduled: boolean;
  setSyncTestScheduled: (value: boolean) => void;
  startSyncTest: () => void;
  refreshSyncTests: (quiet?: boolean) => void | Promise<void>;
  cancelSyncTest: (testId: string) => void | Promise<void>;
  openSyncTestHtml: (testId: string) => void | Promise<void>;
  exportSyncTestHtml: (testId: string) => void | Promise<void>;
  exportSyncTestReport: (testId: string) => void | Promise<void>;
  exportGigENetworkReport: (testId: string) => void | Promise<void>;
  syncRuns: CameraSynchronizationRunRecord[];
  refreshSyncRuns: (quiet?: boolean) => void | Promise<void>;
  refreshSyncStatistics: (quiet?: boolean) => void | Promise<void>;
};

/** 「诊断」标签页：底层采集计数器、厂商传输遥测、PTP 计时、GigE 网络与自动调试测试。 */
export default function CameraDiagnosticsTab(props: CameraDiagnosticsTabProps) {
  const {
    busy, selected, adapters, cameras, selectedSyncId, syncForm, saveSyncGroup,
    syncPtpDiagnostics, refreshSyncPtpDiagnostics, gigeDiagnostics, refreshGigEDiagnostics,
    startGigENetworkTest, syncTestIterations, setSyncTestIterations, syncTests, syncTestScheduled,
    setSyncTestScheduled, startSyncTest, refreshSyncTests, cancelSyncTest, openSyncTestHtml,
    exportSyncTestHtml, exportSyncTestReport, exportGigENetworkReport, syncRuns, refreshSyncRuns,
    refreshSyncStatistics,
  } = props;
  return (
    <>
{/* 底层采集计数器与厂商传输遥测：属于高级诊断，从主区域收起 */}
{selected && <>
  <div className="camera-stats-grid">
    <Statistic title="已发布帧数" value={selected.acquisition.framesPublished} />
    <Statistic title="环形缓冲区覆盖数" value={selected.acquisition.ringOverwrites} />
    <Statistic title="驱动丢帧数" value={selected.acquisition.driverDroppedFrames} />
    <Statistic title="帧超时次数" value={selected.acquisition.frameTimeouts} />
    <Statistic title="采集错误数" value={selected.acquisition.acquisitionErrors} />
    <Statistic title="重连次数" value={selected.acquisition.reconnectCount} />
  </div>
  {selected.acquisition.transport && <>
    <Typography.Title level={5}>厂商传输遥测</Typography.Title>
    <Space wrap style={{ marginBottom: 8 }}>
      <Tag color={selected.acquisition.transport.native ? 'green' : 'default'}>{selected.acquisition.transport.native ? 'SDK 原生计数器' : '不可用'}</Tag>
      <Tag>{selected.acquisition.transport.source}</Tag>
      {selected.acquisition.transport.throughputMbps != null && <Tag color="blue">{selected.acquisition.transport.throughputMbps.toFixed(1)} Mbit/s</Tag>}
      {selected.acquisition.transport.receivedBytes != null && <Tag>{formatBytes(selected.acquisition.transport.receivedBytes)}</Tag>}
      {selected.acquisition.transport.receivedFrames != null && <Tag>接收帧数 {selected.acquisition.transport.receivedFrames}</Tag>}
      {selected.acquisition.transport.lostFrames != null && <Tag color={selected.acquisition.transport.lostFrames > 0 ? 'red' : 'green'}>丢失帧数 {selected.acquisition.transport.lostFrames}</Tag>}
      {selected.acquisition.transport.failedFrames != null && <Tag color={selected.acquisition.transport.failedFrames > 0 ? 'red' : 'green'}>失败帧数 {selected.acquisition.transport.failedFrames}</Tag>}
      {selected.acquisition.transport.bufferUnderruns != null && <Tag color={selected.acquisition.transport.bufferUnderruns > 0 ? 'red' : 'green'}>缓冲区欠载 {selected.acquisition.transport.bufferUnderruns}</Tag>}
      {selected.acquisition.transport.lostPackets != null && <Tag color={selected.acquisition.transport.lostPackets > 0 ? 'orange' : 'green'}>丢包数 {selected.acquisition.transport.lostPackets}</Tag>}
      {selected.acquisition.transport.failedPackets != null && <Tag color={selected.acquisition.transport.failedPackets > 0 ? 'orange' : 'green'}>失败包数 {selected.acquisition.transport.failedPackets}</Tag>}
      {selected.acquisition.transport.resendRequests != null && <Tag>重发请求数 {selected.acquisition.transport.resendRequests}</Tag>}
      {selected.acquisition.transport.resentPackets != null && <Tag>已重发包数 {selected.acquisition.transport.resentPackets}</Tag>}
      {selected.acquisition.transport.resynchronizations != null && <Tag color={selected.acquisition.transport.resynchronizations > 0 ? 'red' : 'green'}>重新同步次数 {selected.acquisition.transport.resynchronizations}</Tag>}
    </Space>
  </>}
</>}
<Space wrap style={{ marginTop: 10, marginBottom: 6 }}><Typography.Text strong>PTP 计时诊断</Typography.Text><Button size="small" onClick={() => refreshSyncPtpDiagnostics()} disabled={!selectedSyncId}>刷新 PTP</Button></Space>
{syncPtpDiagnostics && <>
  <div className="camera-settings-grid">
    <Statistic title="PTP 证据运行数" value={syncPtpDiagnostics.runsWithPtpEvidence} />
    <Statistic title="PTP 未就绪次数" value={syncPtpDiagnostics.ptpNotReadyRuns} />
    <Statistic title="主时钟不一致次数" value={syncPtpDiagnostics.inconsistentMasterClockRuns} />
    <Statistic title="偏移绝对值 P95" value={syncPtpDiagnostics.p95MaxAbsOffsetNs ?? 0} precision={0} suffix="ns" />
    <Statistic title="最大偏移绝对值" value={syncPtpDiagnostics.maxAbsOffsetNs ?? 0} suffix="ns" />
    <Statistic title="偏移与同步偏差相关系数" value={syncPtpDiagnostics.offsetSkewPearsonCorrelation ?? 0} precision={3} />
  </div>
  <Table size="small" rowKey="cameraId" dataSource={syncPtpDiagnostics.cameras} pagination={false} columns={[
    { title: '相机', dataIndex: 'cameraId' },
    { title: 'PTP 就绪率', render: (_: unknown, row: CameraSynchronizationPtpDiagnostics['cameras'][number]) => `${row.readySamples}/${row.samples} (${(row.readyRate * 100).toFixed(1)}%)` },
    { title: '偏移绝对值 P95', render: (_: unknown, row: CameraSynchronizationPtpDiagnostics['cameras'][number]) => row.p95AbsOffsetNs == null ? '—' : `${row.p95AbsOffsetNs.toFixed(0)} ns` },
    { title: '最大偏移绝对值', render: (_: unknown, row: CameraSynchronizationPtpDiagnostics['cameras'][number]) => row.maxAbsOffsetNs == null ? '—' : `${row.maxAbsOffsetNs} ns` },
    { title: '主时钟变更次数', dataIndex: 'masterClockChanges' }
  ]} />
</>}

<Space wrap style={{ marginTop: 12, marginBottom: 6 }}><Typography.Text strong>GigE 网络调试</Typography.Text><Button size="small" onClick={() => refreshGigEDiagnostics()} disabled={!selectedSyncId}>刷新网络诊断</Button><Button type="primary" size="small" onClick={startGigENetworkTest} disabled={!selectedSyncId || syncTests.some((x) => x.status === 'Queued' || x.status === 'Running')} loading={busy === 'gige-test-start'}>运行 {syncTestIterations} 轮 GigE 测试</Button></Space>
{gigeDiagnostics && <>
  <Alert showIcon type={gigeDiagnostics.assessment.status === 'Pass' ? 'success' : gigeDiagnostics.assessment.status === 'Fail' ? 'error' : 'info'} title={`GigE 网络：${gigeDiagnostics.assessment.status === 'InsufficientData' ? '数据不足' : localizeStatus(gigeDiagnostics.assessment.status)}`} description={gigeDiagnostics.assessment.reasons.join(' ')} style={{ marginBottom: 8 }} />
  <div className="camera-settings-grid">
    <Statistic title="证据运行数" value={gigeDiagnostics.assessment.evidenceRuns} suffix={`/ ${gigeDiagnostics.assessment.minimumEvidenceRuns}`} />
    <Statistic title="网卡峰值负载" value={(gigeDiagnostics.assessment.peakNicUtilization ?? 0) * 100} precision={1} suffix="%" />
    <Statistic title="帧异常率" value={(gigeDiagnostics.assessment.aggregateFrameIssueRate ?? 0) * 100} precision={3} suffix="%" />
    <Statistic title="数据包异常率" value={(gigeDiagnostics.assessment.aggregatePacketIssueRate ?? 0) * 100} precision={3} suffix="%" />
    <Statistic title="重发请求率" value={(gigeDiagnostics.assessment.aggregateResendRequestRate ?? 0) * 100} precision={3} suffix="%" />
  </div>
  <Table size="small" rowKey="cameraId" dataSource={gigeDiagnostics.cameras} pagination={false} columns={[
    { title: '相机', dataIndex: 'cameraId' },
    { title: 'IP / 网卡', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => `${row.cameraIpAddress ?? '—'} / ${row.nicName ?? '未映射'}` },
    { title: '链路速率', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.nicSpeedMbps == null ? '—' : `${row.nicSpeedMbps.toFixed(0)} Mbit/s` },
    { title: '数据包大小', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.packetSizeBytes == null ? '—' : `${row.packetSizeBytes} 字节` },
    { title: '包间延迟', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.interPacketDelayTicks == null ? '—' : `${row.interPacketDelayTicks} 个时钟周期` },
    { title: '吞吐量', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.currentThroughputMbps == null ? '—' : `${row.currentThroughputMbps.toFixed(1)} Mbit/s` },
    { title: '遥测来源', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => <Tag color={row.nativeTransportTelemetry ? 'green' : 'gold'}>{row.nativeTransportTelemetry ? '原生' : '回退'}</Tag> }
  ]} />
  {gigeDiagnostics.nicLoads.length > 0 && <Table size="small" rowKey="nicId" dataSource={gigeDiagnostics.nicLoads} pagination={false} columns={[
    { title: '网卡', dataIndex: 'nicName' },
    { title: '相机', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => row.cameraIds.join(', ') },
    { title: '链路速率 / MTU', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => `${row.speedMbps.toFixed(0)} Mbit/s / MTU ${row.mtuBytes ?? '—'}` },
    { title: '相机负载', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => `${row.currentCameraThroughputMbps.toFixed(1)} Mbit/s（${(row.currentUtilization * 100).toFixed(1)}%）` },
    { title: '状态', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => <Tag color={row.level === 'Critical' ? 'red' : row.level === 'Warning' ? 'gold' : 'green'}>{localizeStatus(row.level)}</Tag> }
  ]} />}
  <Alert type="info" showIcon title="网络调优建议" description={gigeDiagnostics.assessment.recommendations.join(' ')} style={{ marginTop: 8 }} />
</>}

<Space wrap style={{ marginTop: 12, marginBottom: 6 }}><Typography.Text strong>自动调试测试</Typography.Text></Space>
<Space wrap style={{ marginBottom: 8 }}>
  <Select value={syncTestIterations} onChange={setSyncTestIterations} style={{ width: 130 }} options={[20, 100, 500].map((value) => ({ value, label: `${value} 个周期` }))} />
  <Switch checked={syncTestScheduled} onChange={setSyncTestScheduled} checkedChildren="定时" unCheckedChildren="立即" />
  <Button type="primary" onClick={startSyncTest} disabled={!selectedSyncId || syncTests.some((x) => x.status === 'Queued' || x.status === 'Running')} loading={busy === 'sync-test-start'}>开始测试</Button>
  <Button size="small" onClick={() => refreshSyncTests()} disabled={!selectedSyncId}>刷新</Button>
</Space>
<Table<CameraSynchronizationCommissioningTest> size="small" rowKey="testId" dataSource={syncTests} pagination={{ pageSize: 4, size: 'small' }} columns={[
  { title: '开始时间', dataIndex: 'startedAt', width: 165, render: (value: string) => new Date(value).toLocaleString('zh-CN') },
  { title: '模式', width: 90, render: (_: unknown, row) => row.scheduled ? '定时' : '立即' },
  { title: '进度', width: 130, render: (_: unknown, row) => `${row.completedIterations} / ${row.requestedIterations}` },
  { title: '状态', width: 110, render: (_: unknown, row) => <Tag color={row.status === 'Completed' ? (row.report?.statistics.commissioning.passed ? 'green' : 'red') : row.status === 'Running' ? 'processing' : row.status === 'Failed' ? 'red' : row.status === 'Interrupted' ? 'orange' : 'default'}>{localizeStatus(row.status)}</Tag> },
  { title: '结果', render: (_: unknown, row) => row.report ? `${localizeStatus(row.report.statistics.commissioning.status)} · P99 ${row.report.statistics.p99SkewUs?.toFixed(1) ?? '—'} µs · 完成率 ${(row.report.statistics.completionRate * 100).toFixed(1)}%` : (row.error ?? '—') },
  { title: '操作', width: 210, render: (_: unknown, row) => <Space size={4}>{(row.status === 'Running' || row.status === 'Queued') && <Button size="small" danger onClick={() => cancelSyncTest(row.testId)}>取消</Button>}{row.report && <><Button size="small" onClick={() => openSyncTestHtml(row.testId)}>查看</Button><Button size="small" onClick={() => exportSyncTestHtml(row.testId)}>HTML</Button><Button size="small" onClick={() => exportSyncTestReport(row.testId)}>JSON</Button><Button size="small" onClick={() => exportGigENetworkReport(row.testId)}>GigE</Button></>}</Space> }
]} />

<Space wrap style={{ marginTop: 10, marginBottom: 6 }}><Typography.Text strong>最近同步运行记录</Typography.Text><Button size="small" onClick={() => { refreshSyncRuns(); refreshSyncStatistics(true); refreshSyncPtpDiagnostics(true); refreshGigEDiagnostics(true); }} disabled={!selectedSyncId}>刷新历史</Button></Space>
<Table<CameraSynchronizationRunRecord> size="small" rowKey="runId" dataSource={syncRuns} pagination={{ pageSize: 5, size: 'small' }} columns={[
  { title: '完成时间', dataIndex: 'completedAt', width: 175, render: (value: string) => new Date(value).toLocaleString('zh-CN') },
  { title: '来源', dataIndex: 'source', width: 90 },
  { title: '模式', width: 90, render: (_: unknown, row) => row.scheduled ? '定时' : '立即' },
  { title: '同步偏差', width: 110, render: (_: unknown, row) => row.triggerSkewUs == null ? '—' : `${row.triggerSkewUs.toFixed(1)} µs` },
  { title: '结果', width: 100, render: (_: unknown, row) => <Tag color={row.outcome === 'Completed' && row.withinTolerance ? 'green' : row.outcome === 'Completed' ? 'gold' : 'red'}>{row.outcome === 'Completed' ? (row.withinTolerance ? '在容差内' : '超出容差') : localizeStatus(row.outcome)}</Tag> },
  { title: 'PTP 就绪', width: 120, render: (_: unknown, row) => row.ptpSnapshots.length === 0 ? '—' : `${row.ptpSnapshots.filter((x) => ['Locked','Slave','Master'].includes(x.state)).length}/${row.ptpSnapshots.length} 台` },
  { title: '帧', render: (_: unknown, row) => row.frames.map((x) => `${x.cameraId}#${x.sequence}`).join(' · ') }
]} />

<Form form={syncForm} layout="vertical" onFinish={saveSyncGroup} initialValues={{ id: 'sync-group-1', name: 'Synchronized Cameras', driver: 'basler-pylon', cameraIds: [], deviceKey: 1, groupKey: 1, groupMask: -1, broadcastAddress: '255.255.255.255', requirePtpLocked: true, maxPtpOffsetNs: 1000000, maxTriggerSkewUs: 100, scheduledLeadTimeMs: 100 }}>
  <div className="camera-file-form">
    <Form.Item label="分组 ID" name="id" rules={[{ required: true }]}><Input /></Form.Item>
    <Form.Item label="名称" name="name" rules={[{ required: true }]}><Input /></Form.Item>
  </div>
  <div className="camera-file-form">
    <Form.Item label="驱动" name="driver" rules={[{ required: true }]}><Select options={adapters.filter((x) => x.isSdkAvailable).map((x) => ({ value: x.driver, label: `${x.vendor} · ${x.driver}` }))} /></Form.Item>
    <Form.Item label="相机" name="cameraIds" rules={[{ required: true }]}><Select mode="multiple" options={cameras.filter((x) => x.driver.includes('basler') || x.driver.includes('hikrobot')).map((x) => ({ value: x.id, label: `${x.name} · ${x.driver}` }))} /></Form.Item>
  </div>
  <div className="camera-settings-grid">
    <Form.Item label="设备密钥" name="deviceKey"><InputNumber style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="组密钥" name="groupKey"><InputNumber style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="组掩码" name="groupMask"><InputNumber style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="定时提前量（毫秒）" name="scheduledLeadTimeMs"><InputNumber min={10} max={60000} style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="最大 PTP 偏移（ns）" name="maxPtpOffsetNs"><InputNumber min={0} style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="最大触发偏差（µs）" name="maxTriggerSkewUs"><InputNumber min={0.01} style={{ width: '100%' }} /></Form.Item>
  </div>
  <Form.Item label="广播地址" name="broadcastAddress"><Input /></Form.Item>
  <Form.Item label="要求 PTP 锁定" name="requirePtpLocked" valuePropName="checked"><Switch /></Form.Item>
  <Button htmlType="submit" loading={busy === 'sync-save'}>保存同步组</Button>
</Form>

    </>
  );
}
