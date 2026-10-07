import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Checkbox, Descriptions, InputNumber, message, Modal, Select, Space, Table, Tag } from 'antd';
import type { AlarmRecord, ProductionRuntimeConfig, ProductionRuntimeStatus } from '../types';
import { localizeStatus } from '../i18n';

type JobItem = { id: string; name: string; productId?: string | null; recipeCode?: string | null; publishedVersion?: number | null };

type Props = { open: boolean; onClose: () => void; embedded?: boolean };

const defaultConfig: ProductionRuntimeConfig = {
  jobId: null,
  autoStart: false,
  cycleDelayMs: 25,
  maxCycleMs: 15000,
  maxConsecutiveFailures: 3,
  autoRecover: true,
  recoveryDelayMs: 1000,
  stopTimeoutMs: 5000,
  ptpDriftGuardEnabled: true,
  ptpGuardWindowRuns: 20,
  ptpGuardMinimumEvidenceRuns: 5,
  ptpGuardMinimumReadyRate: 0.99,
  ptpGuardCheckEveryCycles: 10,
  ptpGuardFaultOnMasterClockChange: true,
  synchronizationHealthGuardEnabled: true,
  synchronizationGuardWindowRuns: 20,
  synchronizationGuardMinimumEvidenceRuns: 5,
  synchronizationGuardMaximumFailureRate: 0.05,
  synchronizationGuardMaximumFrameTimeoutRate: 0.05,
  synchronizationGuardMaxConsecutiveFailures: 2,
  synchronizationGuardMaxConsecutiveSkewViolations: 3,
  synchronizationGuardMaximumNativeFrameLossRate: 0.005,
  synchronizationGuardMaximumBufferUnderruns: 0,
  synchronizationGuardMaximumResynchronizations: 0,
  synchronizationGuardMaximumSequenceGapRate: 0.01,
  synchronizationGuardCheckEveryCycles: 5,
  synchronizationGuardUnhealthyChecksToFault: 2,
  synchronizationGuardHealthyChecksToRecover: 3,
  synchronizationGuardRecoveryTimeoutMs: 30000
};

export default function ProductionPanel({ open, onClose, embedded = false }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [status, setStatus] = useState<ProductionRuntimeStatus>();
  const [config, setConfig] = useState<ProductionRuntimeConfig>(defaultConfig);
  const [jobs, setJobs] = useState<JobItem[]>([]);
  const [alarms, setAlarms] = useState<AlarmRecord[]>([]);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const [statusRes, configRes, jobsRes, alarmsRes] = await Promise.all([
        fetch('/api/production/status'), fetch('/api/production/config'), fetch('/api/jobs'), fetch('/api/alarms?activeOnly=false')
      ]);
      if (statusRes.ok) setStatus(await statusRes.json());
      if (configRes.ok) setConfig(await configRes.json());
      if (jobsRes.ok) setJobs(await jobsRes.json());
      if (alarmsRes.ok) setAlarms(await alarmsRes.json());
    } catch { /* keep the operator screen alive while the host restarts */ }
  }, []);

  useEffect(() => {
    if (!open) return;
    void refresh();
    const timer = window.setInterval(() => void refresh(), 1000);
    return () => window.clearInterval(timer);
  }, [open, refresh]);

  const call = async (url: string, method = 'POST', body?: unknown) => {
    setBusy(true);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
      });
      const data = response.status === 204 ? undefined : await response.json();
      if (!response.ok) throw new Error(data?.error ?? '操作失败');
      messageApi.success('生产运行时配置已更新');
      await refresh();
      return data;
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '操作失败');
    } finally { setBusy(false); }
  };

  const locked = Boolean(status?.productionLocked);
  const stateColor = status?.state === 'Running' ? 'green' : status?.state === 'Faulted' ? 'red' : status?.state === 'Recovering' ? 'orange' : 'default';
  const body = (
    <Space direction="vertical" style={{ width: '100%' }} size="middle">
      {contextHolder}
      <Alert
        type={locked ? 'warning' : 'info'}
        showIcon
        message={locked ? `生产已锁定到 ${status?.jobId ?? '-'} V${status?.lockedJobVersion ?? '-'}` : '当前已停止：可以编辑生产配置'}
        description={locked ? `工作流：${status?.lockedWorkflowHash ?? '-'} · 依赖：${status?.lockedDependencyManifestHash?.slice(0, 12) ?? '-'}。发布作业或编辑依赖不会热切换当前运行时。` : '启动时会校验并锁定当前已发布作业及运行时依赖清单，直到停止。'}
      />

      {status?.ptpGuard?.enabled && (
        <Alert
          type={status.ptpGuard.healthy ? 'success' : 'error'}
          showIcon
          message={`PTP 生产保护 · ${status.ptpGuard.healthy ? '正常' : '已阻止 / 故障'}`}
          description={status.ptpGuard.summary ?? `${status.ptpGuard.groups.length} 个受保护的同步组`}
        />
      )}

      {status?.synchronizationGuard?.enabled && (
        <Alert
          type={status.synchronizationGuard.healthy ? 'success' : status.synchronizationGuard.liveRecoverableOnly ? 'warning' : 'error'}
          showIcon
          message={`同步健康保护 · ${status.synchronizationGuard.healthy ? '正常' : status.synchronizationGuard.liveRecoverableOnly ? '可恢复' : '已降级 / 故障'}`}
          description={status.synchronizationGuard.summary ?? `${status.synchronizationGuard.groups.length} 个同步相机组`}
        />
      )}

      <Descriptions bordered size="small" column={4}>
        <Descriptions.Item label="状态"><Tag color={stateColor}>{localizeStatus(status?.state ?? 'Unknown')}</Tag></Descriptions.Item>
        <Descriptions.Item label="周期数">{status?.cycleCount ?? 0}</Descriptions.Item>
        <Descriptions.Item label="合格 / 不合格">{status?.okCount ?? 0} / {status?.ngCount ?? 0}</Descriptions.Item>
        <Descriptions.Item label="错误数">{status?.errorCount ?? 0}</Descriptions.Item>
        <Descriptions.Item label="最近耗时（毫秒）">{(status?.lastDurationMs ?? 0).toFixed(2)}</Descriptions.Item>
        <Descriptions.Item label="看门狗触发次数">{status?.watchdogTrips ?? 0}</Descriptions.Item>
        <Descriptions.Item label="连续失败次数">{status?.consecutiveFailures ?? 0}</Descriptions.Item>
        <Descriptions.Item label="最近判定">{localizeStatus(status?.lastDisposition) ?? '-'}</Descriptions.Item>
        <Descriptions.Item label="依赖哈希" span={2}>{status?.lockedDependencyManifestHash?.slice(0, 20) ?? '-'}</Descriptions.Item>
        <Descriptions.Item label="运行 ID" span={2}>{status?.currentRunId ?? '-'}</Descriptions.Item>
        <Descriptions.Item label="最近错误" span={2}>{status?.lastError ?? '-'}</Descriptions.Item>
        <Descriptions.Item label="PTP 保护">
          <Tag color={!status?.ptpGuard?.enabled ? 'default' : status.ptpGuard.healthy ? 'green' : 'red'}>
            {!status?.ptpGuard?.enabled ? '关闭' : status.ptpGuard.healthy ? '正常' : '故障'}
          </Tag>
        </Descriptions.Item>
        <Descriptions.Item label="PTP 同步组">{status?.ptpGuard?.groups?.length ?? 0}</Descriptions.Item>
        <Descriptions.Item label="同步保护">
          <Tag color={!status?.synchronizationGuard?.enabled ? 'default' : status.synchronizationGuard.healthy ? 'green' : status.synchronizationGuard.liveRecoverableOnly ? 'orange' : 'red'}>
            {!status?.synchronizationGuard?.enabled ? '关闭' : status.synchronizationGuard.healthy ? '正常' : status.synchronizationGuard.liveRecoverableOnly ? '可恢复' : '故障'}
          </Tag>
        </Descriptions.Item>
        <Descriptions.Item label="同步组">{status?.synchronizationGuard?.groups?.length ?? 0}</Descriptions.Item>
      </Descriptions>

      <Space wrap>
        <span>生产作业</span>
        <Select
          style={{ width: 260 }}
          disabled={locked}
          value={config.jobId ?? undefined}
          placeholder="选择已发布的配方"
          options={jobs.filter(x => x.publishedVersion != null).map(x => ({ value: x.id, label: `${x.productId ? `${x.productId} / ${x.recipeCode ?? x.id} · ` : ''}${x.name} · V${x.publishedVersion}` }))}
          onChange={(jobId) => setConfig({ ...config, jobId })}
        />
        <Checkbox disabled={locked} checked={config.autoStart} onChange={e => setConfig({ ...config, autoStart: e.target.checked })}>自动启动</Checkbox>
        <Checkbox disabled={locked} checked={config.autoRecover} onChange={e => setConfig({ ...config, autoRecover: e.target.checked })}>自动恢复</Checkbox>
        <Checkbox disabled={locked} checked={config.ptpDriftGuardEnabled} onChange={e => setConfig({ ...config, ptpDriftGuardEnabled: e.target.checked })}>PTP 漂移保护</Checkbox>
        <Checkbox disabled={locked} checked={config.synchronizationHealthGuardEnabled} onChange={e => setConfig({ ...config, synchronizationHealthGuardEnabled: e.target.checked })}>同步健康保护</Checkbox>
      </Space>

      <Space wrap>
        <span>周期延迟</span><InputNumber disabled={locked} min={0} max={60000} value={config.cycleDelayMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, cycleDelayMs: Number(v ?? 0) })} />
        <span>看门狗超时</span><InputNumber disabled={locked} min={100} max={600000} value={config.maxCycleMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, maxCycleMs: Number(v ?? 15000) })} />
        <span>最大连续失败次数</span><InputNumber disabled={locked} min={1} max={1000} value={config.maxConsecutiveFailures} onChange={v => setConfig({ ...config, maxConsecutiveFailures: Number(v ?? 3) })} />
        <span>恢复延迟</span><InputNumber disabled={locked} min={0} max={600000} value={config.recoveryDelayMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, recoveryDelayMs: Number(v ?? 1000) })} />
        <span>停止超时</span><InputNumber disabled={locked} min={100} max={60000} value={config.stopTimeoutMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, stopTimeoutMs: Number(v ?? 5000) })} />
      </Space>

      <Space wrap>
        <b>PTP 保护</b>
        <span>统计窗口</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={5} max={500} value={config.ptpGuardWindowRuns} addonAfter="次" onChange={v => setConfig({ ...config, ptpGuardWindowRuns: Number(v ?? 20) })} />
        <span>最少样本数</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={1} max={config.ptpGuardWindowRuns} value={config.ptpGuardMinimumEvidenceRuns} addonAfter="次" onChange={v => setConfig({ ...config, ptpGuardMinimumEvidenceRuns: Number(v ?? 5) })} />
        <span>最低就绪率</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={50} max={100} value={Math.round(config.ptpGuardMinimumReadyRate * 1000) / 10} addonAfter="%" onChange={v => setConfig({ ...config, ptpGuardMinimumReadyRate: Number(v ?? 99) / 100 })} />
        <span>检查周期</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={1} max={10000} value={config.ptpGuardCheckEveryCycles} addonAfter="周期" onChange={v => setConfig({ ...config, ptpGuardCheckEveryCycles: Number(v ?? 10) })} />
        <Checkbox disabled={locked || !config.ptpDriftGuardEnabled} checked={config.ptpGuardFaultOnMasterClockChange} onChange={e => setConfig({ ...config, ptpGuardFaultOnMasterClockChange: e.target.checked })}>主时钟变更时触发故障</Checkbox>
      </Space>

      <Space wrap>
        <b>同步健康保护</b>
        <span>统计窗口</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={5} max={500} value={config.synchronizationGuardWindowRuns} addonAfter="次" onChange={v => setConfig({ ...config, synchronizationGuardWindowRuns: Number(v ?? 20) })} />
        <span>最少样本数</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={1} max={config.synchronizationGuardWindowRuns} value={config.synchronizationGuardMinimumEvidenceRuns} addonAfter="次" onChange={v => setConfig({ ...config, synchronizationGuardMinimumEvidenceRuns: Number(v ?? 5) })} />
        <span>最大失败率</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={0} max={50} value={config.synchronizationGuardMaximumFailureRate * 100} addonAfter="%" onChange={v => setConfig({ ...config, synchronizationGuardMaximumFailureRate: Number(v ?? 5) / 100 })} />
        <span>最大帧超时率</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={0} max={50} value={config.synchronizationGuardMaximumFrameTimeoutRate * 100} addonAfter="%" onChange={v => setConfig({ ...config, synchronizationGuardMaximumFrameTimeoutRate: Number(v ?? 5) / 100 })} />
        <span>连续失败阈值</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardMaxConsecutiveFailures} addonAfter="次" onChange={v => setConfig({ ...config, synchronizationGuardMaxConsecutiveFailures: Number(v ?? 2) })} />
        <span>连续偏差阈值</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardMaxConsecutiveSkewViolations} addonAfter="次" onChange={v => setConfig({ ...config, synchronizationGuardMaxConsecutiveSkewViolations: Number(v ?? 3) })} />
        <span>原生帧丢失率</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={0} max={50} step={0.05} value={config.synchronizationGuardMaximumNativeFrameLossRate * 100} addonAfter="%" onChange={v => setConfig({ ...config, synchronizationGuardMaximumNativeFrameLossRate: Number(v ?? 0.5) / 100 })} />
        <span>最大缓冲区欠载</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={0} max={1000000} value={config.synchronizationGuardMaximumBufferUnderruns} onChange={v => setConfig({ ...config, synchronizationGuardMaximumBufferUnderruns: Number(v ?? 0) })} />
        <span>最大重新同步次数</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={0} max={1000000} value={config.synchronizationGuardMaximumResynchronizations} onChange={v => setConfig({ ...config, synchronizationGuardMaximumResynchronizations: Number(v ?? 0) })} />
        <span>序号间隔回退率</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={0} max={50} step={0.1} value={config.synchronizationGuardMaximumSequenceGapRate * 100} addonAfter="%" onChange={v => setConfig({ ...config, synchronizationGuardMaximumSequenceGapRate: Number(v ?? 1) / 100 })} />
        <span>检查周期</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={1} max={10000} value={config.synchronizationGuardCheckEveryCycles} addonAfter="周期" onChange={v => setConfig({ ...config, synchronizationGuardCheckEveryCycles: Number(v ?? 5) })} />
        <span>触发故障连续检查数</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardUnhealthyChecksToFault} addonAfter="次" onChange={v => setConfig({ ...config, synchronizationGuardUnhealthyChecksToFault: Number(v ?? 2) })} />
        <span>恢复连续检查数</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardHealthyChecksToRecover} addonAfter="次" onChange={v => setConfig({ ...config, synchronizationGuardHealthyChecksToRecover: Number(v ?? 3) })} />
        <span>恢复超时</span><InputNumber disabled={locked || !config.synchronizationHealthGuardEnabled} min={1000} max={600000} step={1000} value={config.synchronizationGuardRecoveryTimeoutMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, synchronizationGuardRecoveryTimeoutMs: Number(v ?? 30000) })} />
      </Space>

      <Space>
        <Button disabled={locked} loading={busy} onClick={() => call('/api/production/config', 'PUT', config)}>保存配置</Button>
        <Button type="primary" disabled={locked || !config.jobId} loading={busy} onClick={() => call('/api/production/start', 'POST', { jobId: config.jobId })}>启动生产</Button>
        <Button danger disabled={!locked} loading={busy} onClick={() => call('/api/production/stop')}>停止</Button>
        <Button disabled={status?.state !== 'Faulted'} loading={busy} onClick={() => call('/api/production/recover')}>恢复</Button>
      </Space>

      {status?.ptpGuard?.enabled && status.ptpGuard.groups.length > 0 && (
        <>
          <div className="panel-title">PTP 生产保护</div>
          <Table
            size="small" rowKey="groupId" pagination={false} dataSource={status.ptpGuard.groups}
            columns={[
              { title: '分组', dataIndex: 'groupName' },
              { title: '状态', width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => <Tag color={g.healthy ? 'green' : 'red'}>{g.healthy ? '正常' : '故障'}</Tag> },
              { title: '样本数', width: 95, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => `${g.evidenceRuns}/${g.windowRuns}` },
              { title: '就绪率', width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => g.ptpReadyRate == null ? '-' : `${(g.ptpReadyRate * 100).toFixed(1)}%` },
              { title: '偏移绝对值 P95', width: 120, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => g.p95MaxAbsOffsetNs == null ? '-' : `${g.p95MaxAbsOffsetNs.toFixed(0)} ns` },
              { title: '主时钟变更次数', dataIndex: 'masterClockChanges', width: 115 },
              { title: '原因', dataIndex: 'reason' }
            ]}
          />
        </>
      )}

      {status?.synchronizationGuard?.enabled && status.synchronizationGuard.groups.length > 0 && (
        <>
          <div className="panel-title">同步健康保护</div>
          <Table
            size="small" rowKey="groupId" pagination={false} dataSource={status.synchronizationGuard.groups}
            columns={[
              { title: '分组', dataIndex: 'groupName' },
              { title: '状态', width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => <Tag color={g.healthy ? 'green' : g.liveReady ? 'orange' : 'red'}>{g.healthy ? '正常' : g.liveReady ? '降级' : '离线'}</Tag> },
              { title: '完成率', width: 100, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => `${(g.completionRate * 100).toFixed(1)}%` },
              { title: '超时率', width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => `${(g.frameTimeoutRate * 100).toFixed(1)}%` },
              { title: '动作确认缺失', dataIndex: 'actionAckShortfallRuns', width: 90 },
              { title: '连续偏差次数', dataIndex: 'consecutiveSkewViolations', width: 95 },
              { title: '传输证据', width: 135, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => g.transport ? <Tag color={g.transport.evidenceMode === 'vendor-native' ? 'green' : g.transport.nativeCameras > 0 ? 'gold' : 'default'}>{g.transport.evidenceMode === 'vendor-native' ? '厂商原生' : '序号估算'}</Tag> : '-' },
              { title: '原生帧丢失率', width: 100, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => g.transport?.nativeFrameLossRate == null ? '-' : `${(g.transport.nativeFrameLossRate * 100).toFixed(3)}%` },
              { title: '丢包 / 重发', width: 115, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => g.transport ? `${g.transport.lostPackets + g.transport.failedPackets} / ${g.transport.resendRequests}` : '-' },
              { title: '序号间隔回退率', width: 100, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => `${(g.estimatedSequenceGapRate * 100).toFixed(2)}%` },
              { title: '原因', dataIndex: 'reason' }
            ]}
          />
        </>
      )}

      <div className="panel-title">告警</div>
      <Table
        size="small"
        rowKey="id"
        pagination={{ pageSize: 6 }}
        dataSource={alarms}
        columns={[
          { title: '状态', width: 90, render: (_: unknown, a: AlarmRecord) => <Tag color={a.active ? 'red' : 'green'}>{a.active ? '活动' : '已恢复'}</Tag> },
          { title: '级别', dataIndex: 'severity', width: 90, render: (v: string) => ({ Critical: '严重', Error: '错误', Warning: '警告', Info: '信息' }[v] ?? v) },
          { title: '代码', dataIndex: 'code', width: 150 },
          { title: '消息', dataIndex: 'message' },
          { title: '发生时间', dataIndex: 'raisedAt', width: 180, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
          { title: '确认', width: 90, render: (_: unknown, a: AlarmRecord) => a.acknowledged ? <Tag>已确认</Tag> : <Button size="small" onClick={() => call(`/api/alarms/${a.id}/ack`)}>确认</Button> }
        ]}
      />
    </Space>
  );

  if (embedded) return <div className="runtime-only-page"><div className="runtime-only-title">VISIONSTUDIO · 生产运行</div>{body}</div>;
  return <Modal open={open} onCancel={onClose} footer={null} title="生产运行" width={1120}>{body}</Modal>;
}
