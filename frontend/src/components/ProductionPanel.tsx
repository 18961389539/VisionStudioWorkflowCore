import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Checkbox, Descriptions, Form, Input, InputNumber, message, Modal, Select, Space, Table, Tabs, Tag } from 'antd';
import type { AlarmRecord, ProductionRuntimeConfig, ProductionRuntimeStatus } from '../types';
import { localizeStatus } from '../i18n';
import { responseErrorMessage } from '../apiErrors';

// R04：轮询节拍、单请求超时与"最后成功时间"语义——HTTP 失败不得让旧状态冒充实时数据。
const PRODUCTION_POLL_TIMEOUT_MS = 5000;

type JobItem = { id: string; name: string; productId?: string | null; recipeCode?: string | null; publishedVersion?: number | null };

/// R05：待核对的设备动作（管理员核对入口的数据契约，与后端 ProductionDeviceActionPendingState 对齐）。
export type DeviceActionPendingState = {
  runId?: string | null;
  manifestHash?: string | null;
  deviceIds: string[];
  robotIds: string[];
  deviceFingerprints?: Record<string, string>;
  phase?: string;
  setAt?: string | null;
  reason?: string | null;
  unreadable?: boolean;
};

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

const severityLabels: Record<string, string> = { Critical: '严重', Error: '错误', Warning: '警告', Info: '信息' };

export default function ProductionPanel({ open, onClose, embedded = false }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [status, setStatus] = useState<ProductionRuntimeStatus>();
  const [config, setConfig] = useState<ProductionRuntimeConfig>(defaultConfig);
  const [jobs, setJobs] = useState<JobItem[]>([]);
  const [alarms, setAlarms] = useState<AlarmRecord[]>([]);
  const [busy, setBusy] = useState<string>();
  const [startError, setStartError] = useState<string>();
  const [lastUpdatedAt, setLastUpdatedAt] = useState<Date>();
  const [staleConnection, setStaleConnection] = useState(false);
  const [refreshError, setRefreshError] = useState<string>();
  // R04：轮询代次 + 取消 + 超时。旧响应绝不覆盖新结果；请求挂起也不会让界面停留在
  // "数据更新于当前时间"的假象上（503/401/500 必须显式标记陈旧并给出原因）。
  const refreshGeneration = useRef(0);
  const refreshAbort = useRef<AbortController | null>(null);
  // R05：待核对设备动作——管理员必须在产品内看到清单并提交核对证据（此前后端具备能力但界面缺失）。
  const [pendingActions, setPendingActions] = useState<DeviceActionPendingState>();
  const [resolveOpen, setResolveOpen] = useState(false);
  const [resolveForm] = Form.useForm<{ evidence: string; identityEvidence: string; reason?: string }>();

  const refresh = useCallback(async () => {
    const generation = ++refreshGeneration.current;
    refreshAbort.current?.abort();
    const controller = new AbortController();
    refreshAbort.current = controller;
    const timeout = window.setTimeout(() => controller.abort(), PRODUCTION_POLL_TIMEOUT_MS);
    try {
      const [statusRes, configRes, jobsRes, alarmsRes] = await Promise.all([
        fetch('/api/production/status', { signal: controller.signal }),
        fetch('/api/production/config', { signal: controller.signal }),
        fetch('/api/jobs', { signal: controller.signal }),
        fetch('/api/alarms?activeOnly=false', { signal: controller.signal })
      ]);
      if (generation !== refreshGeneration.current) return; // 旧响应：丢弃，不覆盖更新的结果
      // 只有**关键生产快照**真正取到才算刷新成功——可选数据（配置/配方/报警）失败只降级显示。
      if (!statusRes.ok)
        throw new Error(await responseErrorMessage(statusRes, `生产状态读取失败（HTTP ${statusRes.status}）`));
      setStatus(await statusRes.json());
      if (configRes.ok) setConfig(await configRes.json());
      if (jobsRes.ok) setJobs(await jobsRes.json());
      if (alarmsRes.ok) setAlarms(await alarmsRes.json());
      try {
        const pendingRes = await fetch('/api/production/device-actions/pending', { signal: controller.signal });
        if (generation === refreshGeneration.current)
          setPendingActions(pendingRes.ok && pendingRes.status !== 204 ? await pendingRes.json() : undefined);
      } catch { /* 待核对清单是辅助信息：读取失败不影响主快照的新鲜度判定 */ }
      setLastUpdatedAt(new Date());
      setStaleConnection(false);
      setRefreshError(undefined);
    } catch (error) {
      if (generation !== refreshGeneration.current) return;
      // keep the operator screen alive while the host restarts — but never present stale data as current
      setStaleConnection(true);
      setRefreshError(error instanceof Error ? error.message : '生产状态读取失败');
    } finally {
      window.clearTimeout(timeout);
      if (refreshAbort.current === controller) refreshAbort.current = null;
    }
  }, []);

  useEffect(() => {
    if (!open) return;
    void refresh();
    const timer = window.setInterval(() => void refresh(), 1000);
    return () => {
      window.clearInterval(timer);
      refreshAbort.current?.abort();
    };
  }, [open, refresh]);

  // 错误原因优先后端的 ProblemDetails.detail；失败信息同时返回调用方，用于持久展示而不是一闪而过
  const call = async (url: string, method = 'POST', body?: unknown, successMessage = '生产配置已更新', action = 'call') => {
    setBusy(action);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body)
      });
      const data = response.status === 204 ? undefined : await response.json().catch(() => undefined);
      if (!response.ok) throw new Error(data?.detail ?? data?.error ?? `操作失败（HTTP ${response.status}）`);
      messageApi.success(successMessage);
      await refresh();
      return { ok: true as const, data };
    } catch (error) {
      const text = error instanceof Error ? error.message : '操作失败';
      messageApi.error(text);
      return { ok: false as const, error: text };
    } finally { setBusy(undefined); }
  };

  const startProduction = async () => {
    setStartError(undefined);
    const result = await call('/api/production/start', 'POST', { jobId: config.jobId }, '生产已启动', 'start');
    if (!result.ok) setStartError(result.error);
  };

  const locked = Boolean(status?.productionLocked);
  const state = status?.state ?? 'Stopped';
  const stateColor = state === 'Running' ? 'green' : state === 'Faulted' ? 'red' : state === 'Recovering' ? 'orange' : state === 'Starting' || state === 'Stopping' ? 'processing' : 'default';
  const selectedJobId = status?.jobId ?? config.jobId ?? undefined;
  const activeJob = jobs.find((x) => x.id === selectedJobId);
  const activeAlarms = alarms.filter((a) => a.active);
  const criticalAlarms = activeAlarms.filter((a) => a.severity === 'Critical');
  const latestAlarm = [...activeAlarms].sort((a, b) => Date.parse(b.raisedAt) - Date.parse(a.raisedAt))[0];
  const jobLabel = activeJob
    ? `${activeJob.productId ? `${activeJob.productId} / ` : ''}${activeJob.recipeCode ?? activeJob.name ?? activeJob.id}`
    : selectedJobId ?? '未选择';
  const jobVersion = status?.lockedJobVersion ?? activeJob?.publishedVersion ?? null;
  const startBlockedReason = locked
    ? '生产运行中：启动前需要先停止当前运行。'
    : !config.jobId
      ? '尚未选择生产作业：请在“生产配置”中选择已发布的配方版本。'
      : undefined;

  // 首屏：状态、作业版本、产量与节拍集中在一条横幅上（操作员远距离可读）
  const hero = (
    <div className="production-hero">
      <div className="production-hero-state">
        <Tag color={stateColor}>{localizeStatus(state)}</Tag>
        <span className="production-hero-sub">{locked ? `已锁定到 ${status?.jobId ?? '-'} V${status?.lockedJobVersion ?? '-'}` : '当前已停止：可以编辑生产配置'}</span>
      </div>
      <div className="production-hero-metrics">
        <div className="production-hero-metric"><b title={jobLabel}>{jobLabel}</b><span>作业 / 配方</span></div>
        <div className="production-hero-metric"><b>{jobVersion != null ? `V${jobVersion}` : '-'}</b><span>版本</span></div>
        <div className="production-hero-metric"><b>{status?.cycleCount ?? 0}</b><span>周期数</span></div>
        <div className="production-hero-metric"><b>{status?.okCount ?? 0}</b><span>合格</span></div>
        <div className="production-hero-metric"><b>{status?.ngCount ?? 0}</b><span>不合格</span></div>
        <div className="production-hero-metric"><b>{(status?.lastDurationMs ?? 0).toFixed(1)}</b><span>最近耗时（毫秒）</span></div>
      </div>
      <span className={`production-hero-updated${staleConnection ? ' stale' : ''}`}>
        {staleConnection
          ? `数据已过期 · 最后成功更新 ${lastUpdatedAt?.toLocaleTimeString('zh-CN') ?? '—'}${refreshError ? ` · ${refreshError}` : ''}`
          : `数据更新于 ${lastUpdatedAt?.toLocaleTimeString('zh-CN') ?? '-'}`}
      </span>
    </div>
  );

  const actions = (
    <div className="production-actions">
      <Button type="primary" disabled={locked || !config.jobId} loading={busy === 'start'} onClick={() => void startProduction()}>启动生产</Button>
      <Button danger disabled={!locked} loading={busy === 'stop'} onClick={() => void call('/api/production/stop', 'POST', undefined, '生产已停止', 'stop')}>停止</Button>
      <Button disabled={state !== 'Faulted'} loading={busy === 'recover'} onClick={() => void call('/api/production/recover', 'POST', undefined, '已发送恢复请求', 'recover')}>恢复</Button>
      {startBlockedReason && <span className="production-actions-hint">{startBlockedReason}</span>}
    </div>
  );

  const monitorTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
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
        <Descriptions.Item label="开始时间">{status?.startedAt ? new Date(status.startedAt).toLocaleString('zh-CN') : '-'}</Descriptions.Item>
        <Descriptions.Item label="最近周期">{status?.lastCycleAt ? new Date(status.lastCycleAt).toLocaleString('zh-CN') : '-'}</Descriptions.Item>
        <Descriptions.Item label="错误数">{status?.errorCount ?? 0}</Descriptions.Item>
        <Descriptions.Item label="看门狗触发次数">{status?.watchdogTrips ?? 0}</Descriptions.Item>
        <Descriptions.Item label="连续失败次数">{status?.consecutiveFailures ?? 0}</Descriptions.Item>
        <Descriptions.Item label="最近判定">{localizeStatus(status?.lastDisposition) ?? '-'}</Descriptions.Item>
        <Descriptions.Item label="运行 ID" span={2}>{status?.currentRunId ?? '-'}</Descriptions.Item>
        <Descriptions.Item label="依赖哈希" span={2}>{status?.lockedDependencyManifestHash?.slice(0, 20) ?? '-'}</Descriptions.Item>
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
    </Space>
  );

  const alarmsTab = (
    <Table
      size="small"
      rowKey="id"
      pagination={{ pageSize: 8 }}
      dataSource={alarms}
      columns={[
        { title: '状态', width: 90, render: (_: unknown, a: AlarmRecord) => <Tag color={a.active ? 'red' : 'green'}>{a.active ? '活动' : '已恢复'}</Tag> },
        { title: '级别', dataIndex: 'severity', width: 90, render: (v: string) => severityLabels[v] ?? v },
        { title: '代码', dataIndex: 'code', width: 150 },
        { title: '消息', dataIndex: 'message' },
        { title: '发生时间', dataIndex: 'raisedAt', width: 180, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
        { title: '确认', width: 90, render: (_: unknown, a: AlarmRecord) => a.acknowledged ? <Tag>已确认</Tag> : <Button size="small" loading={busy === `ack:${a.id}`} onClick={() => void call(`/api/alarms/${a.id}/ack`, 'POST', undefined, '告警已确认', `ack:${a.id}`)}>确认</Button> }
      ]}
    />
  );

  const saveRow = (
    <Space>
      <Button loading={busy === 'save'} disabled={locked} onClick={() => void call('/api/production/config', 'PUT', config, '生产配置已保存', 'save')}>保存配置</Button>
      <span className="production-actions-hint">{locked ? '运行期间配置已锁定，停止后可编辑。' : '修改后需保存才会生效。'}</span>
    </Space>
  );

  const configTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Space wrap>
        <span>生产作业</span>
        <Select
          style={{ width: 320 }}
          disabled={locked}
          value={config.jobId ?? undefined}
          placeholder="选择已发布的配方"
          options={jobs.filter(x => x.publishedVersion != null).map(x => ({ value: x.id, label: `${x.productId ? `${x.productId} / ${x.recipeCode ?? x.id} · ` : ''}${x.name} · V${x.publishedVersion}` }))}
          onChange={(jobId) => setConfig({ ...config, jobId })}
        />
        <Checkbox disabled={locked} checked={config.autoStart} onChange={e => setConfig({ ...config, autoStart: e.target.checked })}>自动启动</Checkbox>
        <Checkbox disabled={locked} checked={config.autoRecover} onChange={e => setConfig({ ...config, autoRecover: e.target.checked })}>自动恢复</Checkbox>
      </Space>
      <Space wrap>
        <span>周期延迟</span><InputNumber disabled={locked} min={0} max={60000} value={config.cycleDelayMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, cycleDelayMs: Number(v ?? 0) })} />
        <span>看门狗超时</span><InputNumber disabled={locked} min={100} max={600000} value={config.maxCycleMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, maxCycleMs: Number(v ?? 15000) })} />
        <span>最大连续失败次数</span><InputNumber disabled={locked} min={1} max={1000} value={config.maxConsecutiveFailures} onChange={v => setConfig({ ...config, maxConsecutiveFailures: Number(v ?? 3) })} />
        <span>恢复延迟</span><InputNumber disabled={locked} min={0} max={600000} value={config.recoveryDelayMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, recoveryDelayMs: Number(v ?? 1000) })} />
        <span>停止超时</span><InputNumber disabled={locked} min={100} max={60000} value={config.stopTimeoutMs} addonAfter="毫秒" onChange={v => setConfig({ ...config, stopTimeoutMs: Number(v ?? 5000) })} />
      </Space>
      {saveRow}
    </Space>
  );

  const ptpTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Checkbox disabled={locked} checked={config.ptpDriftGuardEnabled} onChange={e => setConfig({ ...config, ptpDriftGuardEnabled: e.target.checked })}>启用 PTP 漂移保护</Checkbox>
      <Space wrap>
        <span>统计窗口</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={5} max={500} value={config.ptpGuardWindowRuns} addonAfter="次" onChange={v => setConfig({ ...config, ptpGuardWindowRuns: Number(v ?? 20) })} />
        <span>最少样本数</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={1} max={config.ptpGuardWindowRuns} value={config.ptpGuardMinimumEvidenceRuns} addonAfter="次" onChange={v => setConfig({ ...config, ptpGuardMinimumEvidenceRuns: Number(v ?? 5) })} />
        <span>最低就绪率</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={50} max={100} value={Math.round(config.ptpGuardMinimumReadyRate * 1000) / 10} addonAfter="%" onChange={v => setConfig({ ...config, ptpGuardMinimumReadyRate: Number(v ?? 99) / 100 })} />
        <span>检查周期</span><InputNumber disabled={locked || !config.ptpDriftGuardEnabled} min={1} max={10000} value={config.ptpGuardCheckEveryCycles} addonAfter="周期" onChange={v => setConfig({ ...config, ptpGuardCheckEveryCycles: Number(v ?? 10) })} />
        <Checkbox disabled={locked || !config.ptpDriftGuardEnabled} checked={config.ptpGuardFaultOnMasterClockChange} onChange={e => setConfig({ ...config, ptpGuardFaultOnMasterClockChange: e.target.checked })}>主时钟变更时触发故障</Checkbox>
      </Space>
      {saveRow}
    </Space>
  );

  const syncTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Checkbox disabled={locked} checked={config.synchronizationHealthGuardEnabled} onChange={e => setConfig({ ...config, synchronizationHealthGuardEnabled: e.target.checked })}>启用同步健康保护</Checkbox>
      <Space wrap>
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
      {saveRow}
    </Space>
  );

  const body = (
    <Space direction="vertical" style={{ width: '100%' }} size="middle">
      {contextHolder}
      {hero}
      {actions}
      {/* R05：待核对设备动作——管理员必须在产品内看到清单、提交可追溯的证据；
          核对前所有设备动作入口（生产/手动/临时运行）都会被统一授权闸门拒绝。 */}
      {pendingActions && (
        <Alert
          type="error"
          showIcon
          message="存在待核对的设备动作：核对前所有设备动作入口都会被拒绝"
          description={
            <Space direction="vertical" size={4}>
              <span>原因：{pendingActions.reason ?? '上一周期的设备动作结果未确认'}{pendingActions.unreadable ? '（安全状态文件不可读，需人工处理）' : ''}</span>
              {pendingActions.runId && <span>运行：{pendingActions.runId}</span>}
              {pendingActions.deviceIds.length > 0 && <span>涉及设备：{pendingActions.deviceIds.join('、')}</span>}
              {pendingActions.robotIds.length > 0 && <span>涉及机器人：{pendingActions.robotIds.join('、')}</span>}
              <span>步骤：现场核对设备物理状态与身份 → 提交核对证据（写入审计）→ 生产/手动动作恢复可用。</span>
              <Button danger size="small" onClick={() => setResolveOpen(true)}>提交核对证据</Button>
            </Space>
          }
        />
      )}
      <Modal
        open={resolveOpen}
        onCancel={() => setResolveOpen(false)}
        title="提交设备动作核对证据"
        okText="提交核对"
        okButtonProps={{ danger: true }}
        onOk={async () => {
          try {
            const values = await resolveForm.validateFields();
            setBusy('resolve');
            const response = await fetch('/api/production/device-actions/resolve', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                runId: pendingActions?.runId ?? '',
                manifestHash: pendingActions?.manifestHash ?? '',
                deviceIds: pendingActions?.deviceIds ?? [],
                robotIds: pendingActions?.robotIds ?? [],
                reason: values.reason ?? 'manual reconciliation',
                evidence: values.evidence,
                deviceIdentityEvidence: values.identityEvidence
              })
            });
            if (!response.ok) throw new Error(await responseErrorMessage(response, `核对提交失败（HTTP ${response.status}）`));
            messageApi.success('核对证据已提交并写入审计');
            setResolveOpen(false);
            resolveForm.resetFields();
            await refresh();
          } catch (error) {
            if (error instanceof Error && error.message) messageApi.error(error.message);
          } finally { setBusy(undefined); }
        }}
      >
        <Form form={resolveForm} layout="vertical">
          <Form.Item name="evidence" label="现场核对证据（必填）" rules={[{ required: true, message: '请填写现场核对证据' }]}>
            <Input.TextArea rows={3} placeholder="例：PLC 触发位已复位为 0，机器人回到安全位，示教器无待处理指令（含时间与执行人）" />
          </Form.Item>
          <Form.Item name="identityEvidence" label="设备身份证据（必填）" rules={[{ required: true, message: '请填写设备身份证据' }]}>
            <Input.TextArea rows={2} placeholder="例：设备铭牌/序列号、在线诊断中的端点与驱动版本，与待核对清单一致" />
          </Form.Item>
          <Form.Item name="reason" label="核对说明（可选）">
            <Input placeholder="例：现场确认上一周期动作已完成/已撤销" />
          </Form.Item>
        </Form>
      </Modal>
      {state === 'Faulted' && (
        <Alert
          type="error"
          showIcon
          message={`生产已进入故障状态${status?.consecutiveFailures ? ` · 连续失败 ${status.consecutiveFailures} 次` : ''}`}
          description={<>
            <div>原因：{status?.lastError ?? '运行时未提供错误详情，请查看下方保护状态与告警记录。'}</div>
            <div>下一步：检查设备与同步健康后点击“恢复”；若故障持续，先“停止”再重新“启动”。</div>
          </>}
        />
      )}
      {startError && (
        <Alert type="warning" showIcon closable message="启动未成功" description={startError} onClose={() => setStartError(undefined)} />
      )}
      {status?.ptpGuard?.enabled && !status.ptpGuard.healthy && (
        <Alert type="error" showIcon message="PTP 生产保护已阻止运行" description={status.ptpGuard.summary ?? `${status.ptpGuard.groups.length} 个受保护的同步组未就绪。`} />
      )}
      {status?.synchronizationGuard?.enabled && !status.synchronizationGuard.healthy && (
        <Alert
          type={status.synchronizationGuard.liveRecoverableOnly ? 'warning' : 'error'}
          showIcon
          message="同步健康保护降级"
          description={status.synchronizationGuard.summary ?? `${status.synchronizationGuard.groups.length} 个同步相机组未达标。`}
        />
      )}
      {activeAlarms.length > 0 && (
        <Alert
          type="warning"
          showIcon
          message={`${activeAlarms.length} 条活动告警${criticalAlarms.length ? ` · ${criticalAlarms.length} 条严重` : ''}`}
          description={latestAlarm ? `最近：${latestAlarm.message}` : undefined}
        />
      )}
      <Tabs
        defaultActiveKey="monitor"
        items={[
          { key: 'monitor', label: '运行监控', children: monitorTab },
          { key: 'alarms', label: `告警${activeAlarms.length ? `（${activeAlarms.length}）` : ''}`, children: alarmsTab },
          { key: 'config', label: '生产配置', children: configTab },
          { key: 'ptp', label: 'PTP 保护', children: ptpTab },
          { key: 'sync', label: '同步健康保护', children: syncTab }
        ]}
      />
    </Space>
  );

  if (embedded) return <div className="runtime-only-page"><div className="runtime-only-title">VISIONSTUDIO · 生产运行</div>{body}</div>;
  return <Modal open={open} onCancel={onClose} footer={null} title="生产运行" width={1120}>{body}</Modal>;
}