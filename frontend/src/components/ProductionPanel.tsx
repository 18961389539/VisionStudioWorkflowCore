import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import {
  Alert, Button, Checkbox, Descriptions, Form, Input, InputNumber, message, Modal, Popconfirm, Radio, Segmented, Select, Space, Table,
  Tabs, Tag, Typography
} from 'antd';
import type { AlarmRecord, ProductionRuntimeStatus } from '../types';
import { localizeStatus } from '../i18n';
import { isProductionConfig, isProductionStatus } from '../productionConfig';
import { ALARM_PAGE_SIZE, useProductionMonitor, type DeviceActionPendingState } from '../useProductionMonitor';
import { useProductionConfigDraft, type ProductionConfigMerge } from '../useProductionConfigDraft';
import { useProductionActions } from '../useProductionActions';
import { apiUrl, alarms as alarmEndpoints } from '../endpoints';
import { useSecurityAccount } from '../security';
import { OperatorDashboard } from './OperatorDashboard';
import './ProductionWorkstation.css';

const ProductionStations = lazy(() => import('./ProductionStations'));
const OperatorAlarmQueue = lazy(() => import('./OperatorAlarmQueue'));
const OperatorLayoutDialog = lazy(() => import('./OperatorLayoutDialog'));
const RecipeDeliveryInbox = lazy(() => import('./ProductionRecipeRollouts').then((module) => ({ default: module.RecipeDeliveryInbox })));

export type { DeviceActionPendingState } from '../useProductionMonitor';

type Props = { open: boolean; onClose: () => void; embedded?: boolean; runtimeOnly?: boolean; initialView?: 'operator' | 'stations';
  /** 车间大屏形态：只渲染操作员工作台，隐藏标签栏与工程师入口（`?view=operator&kiosk=1`）。 */
  kiosk?: boolean };

const severityLabels: Record<string, string> = { Critical: '严重', Error: '错误', Warning: '警告', Info: '信息' };
const maxStopTimeoutMs = 60_000;
const configFieldLabels: Record<string, string> = {
  jobId: '生产作业', autoStart: '自动启动', cycleDelayMs: '周期延迟', maxCycleMs: '单周期超时',
  maxConsecutiveFailures: '连续失败上限', autoRecover: '自动恢复', recoveryDelayMs: '恢复等待',
  maxRecoveryAttempts: '自动恢复次数', stopTimeoutMs: '停止超时', ptpDriftGuardEnabled: 'PTP 漂移保护',
  synchronizationHealthGuardEnabled: '同步健康保护',
};

function displayConfigValue(value: unknown): string {
  if (value == null) return '—';
  if (typeof value === 'boolean') return value ? '启用' : '关闭';
  return String(value);
}

function ProductionConfigConflict({ merge, disabled, onStale }: {
  merge: ProductionConfigMerge;
  disabled: boolean;
  onStale: () => void;
}) {
  const [choices, setChoices] = useState<Parameters<ProductionConfigMerge['apply']>[0]>({});
  const overlapping = merge.fields.filter(({ source }) => source === 'choice');
  return <Alert type="warning" showIcon title="服务端配置已变更，当前编辑已保留" description={
    <Space orientation="vertical" align="start">
      <div>{overlapping.length > 0 ? '请为同时修改的字段选择要保留的值。' : '草稿与服务端修改了不同字段，可以直接合并。'}合并只更新草稿。</div>
      <details open={overlapping.length > 0}>
        <summary>比较配置差异</summary>
        <ul>{merge.fields.map(({ field, draft, latest, source }) => {
          const label = configFieldLabels[field] ?? field;
          return <li key={field}>
            <span>{label} · 当前草稿 {displayConfigValue(draft)} · 服务端 {displayConfigValue(latest)}</span>
            {source === 'choice' && <Radio.Group
              aria-label={`${label}合并方式`}
              value={choices[field]} disabled={disabled}
              onChange={(event) => setChoices((previous) => ({ ...previous, [field]: event.target.value }))}
            >
              <Radio value="draft">保留草稿值</Radio>
              <Radio value="remote">使用服务端值</Radio>
            </Radio.Group>}
          </li>;
        })}</ul>
      </details>
      <Button disabled={disabled || overlapping.some(({ field }) => !choices[field])}
        onClick={() => { if (!merge.apply(choices)) onStale(); }}>
        合并到最新配置
      </Button>
    </Space>
  } />;
}

export default function ProductionPanel({ open, onClose, embedded = false, runtimeOnly = false, initialView, kiosk = false }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const { enabled: securityEnabled, user } = useSecurityAccount();
  const canReconcile = !securityEnabled || user?.role === 'Administrator';
  const canEngineer = !securityEnabled || user?.role === 'Engineer' || user?.role === 'Administrator';
  const canConfigure = !runtimeOnly && canEngineer;
  const monitor = useProductionMonitor(open, canReconcile);
  const { status, jobs, alarms, alarmSummary, alarmTotal, alarmPage, alarmFilter, alarmsLoading, setAlarmPage, setAlarmFilter,
    pending: pendingActions, errors, lastUpdatedAt, statusFresh, configReady } = monitor;
  const { config, dirty, conflict, merge, changeConfig, beginSave, acceptSaved, discard, readCurrent } = useProductionConfigDraft(monitor.config, open);
  const { busy, error: operationError, call } = useProductionActions(open, messageApi, monitor.refresh);
  // kiosk 下没有标签栏可切：固定停在操作员页，避免默认的“运行监控”把 hero 指标重复一遍。
  const [activeTab, setActiveTab] = useState(kiosk ? 'operator' : initialView ?? 'monitor');
  const [tabGroup, setTabGroup] = useState<'run' | 'configure'>('run');
  const [layoutEditorOpen, setLayoutEditorOpen] = useState(false);
  const [layoutRevision, setLayoutRevision] = useState(0);
  const [resolveOpen, setResolveOpen] = useState(false);
  const [resolveTarget, setResolveTarget] = useState<{ kind: 'production'; pending: DeviceActionPendingState } | { kind: 'manual'; runId: string; action: string }>({ kind: 'production', pending: {} });
  const [resolveSubmitting, setResolveSubmitting] = useState(false);
  const resolveRequest = useRef<object | undefined>(undefined);
  const [resolveForm] = Form.useForm<{ evidence: string; identityEvidence: string; reason?: string }>();
  useEffect(() => {
    if (!open) { setResolveOpen(false); setResolveSubmitting(false); setLayoutEditorOpen(false); }
    return () => { resolveRequest.current = undefined; };
  }, [open]);

  const applyStatus = (data: unknown) => {
    if (!isProductionStatus(data)) throw new Error('响应未包含有效的生产状态');
    monitor.acceptStatus(data);
    return data;
  };
  const acceptStatus = (data: unknown) => { applyStatus(data); };
  const stopProduction = () => {
    void call({
      url: apiUrl('production/stop'), action: 'stop', timeoutMs: maxStopTimeoutMs + 10_000, successMessage: '生产已停止',
      onSuccess: (data) => {
        const next = applyStatus(data);
        if (next.state === 'Stopped' && !next.productionLocked) return;
        return { error: typeof next.lastError === 'string' && next.lastError
          ? next.lastError : '生产尚未完成停止，请查看运行状态。' };
      },
    });
  };
  const startProduction = () => {
    const current = readCurrent();
    if (!statusFresh || !configReady || locked || current.dirty || current.conflict || !current.config?.jobId) return;
    void call({ url: apiUrl('production/start'), body: { jobId: current.config.jobId }, successMessage: '生产已启动', action: 'start', onSuccess: acceptStatus });
  };
  const saveConfig = async () => {
    const current = readCurrent();
    if (!statusFresh || !configReady || locked || !current.dirty || current.conflict || !current.config || !monitor.configRevision) return;
    const submitted = current.config;
    const finishSave = beginSave();
    if (!finishSave) return;
    try {
      await call({ url: apiUrl('production/config'), method: 'PUT', body: submitted, headers: { 'If-Match': monitor.configRevision },
        successMessage: '生产配置已保存', action: 'save', onSuccess: (data, response) => {
        if (!isProductionConfig(data)) throw new Error('响应未包含有效的生产配置');
        const revision = response.headers.get('ETag');
        if (!revision) throw new Error('响应未包含生产配置修订号');
        acceptSaved(submitted, data);
        monitor.acceptConfig(data, revision);
      } });
    } finally { finishSave(); }
  };

  const locked = Boolean(status?.productionLocked);
  const state = status?.state ?? 'Stopped';
  const configLocked = !canConfigure || locked || !configReady || !statusFresh;
  const stateColor = state === 'Running' ? 'green' : state === 'Faulted' ? 'red' : state === 'Recovering' ? 'orange' : state === 'Starting' || state === 'Stopping' ? 'processing' : 'default';
  const selectedJobId = status?.jobId ?? monitor.config?.jobId ?? undefined;
  const activeJob = jobs.find((x) => x.id === selectedJobId);
  const activeAlarmCount = alarmSummary?.activeCount ?? 0;
  const criticalAlarmCount = alarmSummary?.criticalCount ?? 0;
  const pendingAlarmCount = alarmSummary?.pendingCount ?? 0;
  const latestActiveMessage = alarmSummary?.latestActiveMessage;
  const jobLabel = activeJob
    ? activeJob.name
    : selectedJobId ?? '未选择';
  const jobVersion = status?.lockedJobVersion ?? activeJob?.publishedVersion ?? null;
  const stateDetail = !statusFresh ? '生产状态尚未确认'
    : locked ? `已锁定到 ${status?.jobId ?? '-'} V${status?.lockedJobVersion ?? '-'}`
      : canConfigure ? '当前已停止：可以编辑生产配置' : '当前已停止';
  const startBlockedReason = !statusFresh ? '生产状态尚未确认，请等待刷新或核对连接。'
    : locked ? '生产运行中：启动前需要先停止当前运行。'
      : !configReady ? '生产配置尚未读取成功，暂不能启动。'
        : conflict ? '服务端配置已变更，请先核对最新配置。'
          : dirty ? '配置有未保存修改，请先保存配置再启动。'
            : !config.jobId ? '尚未选择生产作业：请在“生产配置”中选择已发布的配方版本。' : undefined;

  // 首屏：状态、作业版本、产量与节拍集中在一条横幅上（操作员远距离可读）
  const hero = (
    <div className="production-hero">
      <div className="production-hero-state">
        {/* 过期时不再用绿/红表示状态：颜色本身会被读成"当前在线"，与"状态未确认"矛盾 */}
        <Tag color={!status || !statusFresh ? 'default' : stateColor}>{status ? localizeStatus(state) : '读取中'}</Tag>
        <span className="production-hero-sub">{stateDetail}</span>
      </div>
      <div className="production-hero-metrics">
        <div className="production-hero-metric"><b title={jobLabel}>{jobLabel}</b><span>作业 / 配方</span></div>
        <div className="production-hero-metric"><b>{jobVersion != null ? `V${jobVersion}` : '-'}</b><span>{locked ? '运行版本' : '已发布版本'}</span></div>
        {activeTab !== 'operator' && <>
          <div className="production-hero-metric"><b>{status?.cycleCount ?? 0}</b><span>周期数</span></div>
          <div className="production-hero-metric"><b>{status?.okCount ?? 0}</b><span>合格</span></div>
          <div className="production-hero-metric"><b>{status?.ngCount ?? 0}</b><span>不合格</span></div>
          <div className="production-hero-metric"><b>{(status?.lastDurationMs ?? 0).toFixed(1)}</b><span>最近耗时（毫秒）</span></div>
        </>}
      </div>
      <span className={`production-hero-updated${!statusFresh ? ' stale' : ''}`}>
        {!statusFresh
          ? `数据已过期 · 最后成功更新 ${lastUpdatedAt?.toLocaleTimeString('zh-CN') ?? '—'}${errors.status ? ` · ${errors.status}` : ''}`
          : `数据更新于 ${lastUpdatedAt?.toLocaleTimeString('zh-CN') ?? '-'}`}
      </span>
    </div>
  );

  const actions = (
    <div className="production-actions">
      <Button type="primary" disabled={Boolean(startBlockedReason) || busy.size > 0} loading={busy.has('start')} onClick={() => void startProduction()}>启动生产</Button>
      <Popconfirm
        title="停止生产运行"
        description={`当前锁定在 ${status?.jobId ?? '-'} V${status?.lockedJobVersion ?? '-'}。停止后产线不再接受触发，恢复需要重新启动。`}
        okText="停止" cancelText="取消" okButtonProps={{ danger: true }}
        onConfirm={stopProduction}
        disabled={!locked && statusFresh}>
        <Button danger disabled={!locked && statusFresh} loading={busy.has('stop')}>停止</Button>
      </Popconfirm>
      <Button disabled={state !== 'Faulted' || !statusFresh || dirty || !configReady || busy.size > 0} loading={busy.has('recover')} onClick={() => void call({ url: apiUrl('production/recover'), successMessage: '已发送恢复请求', action: 'recover', onSuccess: acceptStatus })}>恢复</Button>
      {startBlockedReason && <span className="production-actions-hint">{startBlockedReason}</span>}
    </div>
  );

  const monitorTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type={locked ? 'warning' : 'info'}
        showIcon
        title={statusFresh && locked ? `生产已锁定到 ${status?.jobId ?? '-'} V${status?.lockedJobVersion ?? '-'}` : stateDetail}
        description={locked ? `工作流：${status?.lockedWorkflowHash ?? '-'} · 依赖：${status?.lockedDependencyManifestHash?.slice(0, 12) ?? '-'}。发布作业或编辑依赖不会热切换当前运行时。` : '启动时会校验并锁定当前已发布作业及运行时依赖清单，直到停止。'}
      />

      {status?.ptpGuard?.enabled && (
        <Alert
          type={status.ptpGuard.healthy ? 'success' : 'error'}
          showIcon
          title={`PTP 生产保护 · ${status.ptpGuard.healthy ? '正常' : '已阻止 / 故障'}`}
          description={status.ptpGuard.summary ?? `${status.ptpGuard.groups.length} 个受保护的同步组`}
        />
      )}

      {status?.synchronizationGuard?.enabled && (
        <Alert
          type={status.synchronizationGuard.healthy ? 'success' : status.synchronizationGuard.liveRecoverableOnly ? 'warning' : 'error'}
          showIcon
          title={`同步健康保护 · ${status.synchronizationGuard.healthy ? '正常' : status.synchronizationGuard.liveRecoverableOnly ? '可恢复' : '已降级 / 故障'}`}
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

  const acknowledgeAlarm = (id: string) => call({
    url: alarmEndpoints.ack(id), successMessage: '告警已确认', action: `ack:${id}`,
    onSuccess: (data) => {
      const alarm = data as Partial<AlarmRecord> | undefined;
      if (alarm?.id !== id || alarm.acknowledged !== true) throw new Error('告警确认回执无效');
      monitor.invalidateAlarmReads();
    },
  });

  const alarmsTab = (
    <>
      <Radio.Group aria-label="告警筛选" value={alarmFilter} onChange={(event) => setAlarmFilter(event.target.value)}>
        <Radio.Button value="all">全部</Radio.Button>
        <Radio.Button value="active">活动告警</Radio.Button>
        <Radio.Button value="pending">待确认</Radio.Button>
      </Radio.Group>
      <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 8, fontSize: 12 }}>
        活动告警全部保留，已恢复告警按时间保留至总数上限（2000 条）。
      </Typography.Text>
    <Table
      size="small"
      rowKey="id"
      loading={alarmsLoading && alarms.length === 0}
      locale={{ emptyText: errors.alarms ? '告警记录暂不可用'
        : alarmFilter === 'pending' ? '没有待确认的活动告警'
          : alarmFilter === 'active' ? '没有活动告警' : '暂无告警记录' }}
      pagination={{ current: alarmPage, pageSize: ALARM_PAGE_SIZE, total: alarmTotal, showSizeChanger: false, onChange: setAlarmPage }}
      dataSource={alarms}
      columns={[
        { title: '状态', width: 90, render: (_: unknown, a: AlarmRecord) => <Tag color={a.active ? 'red' : 'green'}>{a.active ? '活动' : '已恢复'}</Tag> },
        { title: '级别', dataIndex: 'severity', width: 90, render: (v: string) => severityLabels[v] ?? v },
        { title: '代码', dataIndex: 'code', width: 150 },
        { title: '消息', dataIndex: 'message' },
        { title: '最近发生', width: 180, render: (_: unknown, a: AlarmRecord) => new Date(a.lastRaisedAt ?? a.raisedAt).toLocaleString('zh-CN') },
        { title: '次数', dataIndex: 'occurrenceCount', width: 72, render: (value: AlarmRecord['occurrenceCount']) => value ?? 1 },
        { title: '确认', width: 90, render: (_: unknown, a: AlarmRecord) => a.acknowledged ? <Tag>已确认</Tag> :
          <Button size="small" loading={busy.has(`ack:${a.id}`)} onClick={() => void acknowledgeAlarm(a.id)}>确认</Button> }
      ]}
      />
    </>
  );

  const saveRow = (
    <Space>
      <Button loading={busy.has('save')} disabled={configLocked || !dirty || conflict || busy.size > 0} onClick={() => void saveConfig()}>保存配置</Button>
      <Button disabled={!dirty || busy.has('save') || !configReady} onClick={discard}>放弃修改，载入最新配置</Button>
      <span className="production-actions-hint">{locked ? '运行期间配置已锁定，停止后可编辑。' : dirty ? '配置有未保存修改，保存后才可启动。' : configReady ? '配置已与服务端同步。' : '正在读取生产配置…'}</span>
    </Space>
  );

  const configTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Space wrap>
        <span>生产作业</span>
        <Select
          style={{ width: 320 }} aria-label="生产作业"
          disabled={configLocked}
          value={config.jobId ?? undefined}
          placeholder="选择已发布的配方"
          options={jobs.filter(x => x.publishedVersion != null).map(x => ({ value: x.id, label: `${x.name} · V${x.publishedVersion}` }))}
          onChange={(jobId) => changeConfig({ jobId })}
        />
        <Checkbox disabled={configLocked} checked={config.autoStart} onChange={e => changeConfig({ autoStart: e.target.checked })}>自动启动</Checkbox>
        <Checkbox disabled={configLocked} checked={config.autoRecover} onChange={e => changeConfig({ autoRecover: e.target.checked })}>自动恢复</Checkbox>
      </Space>
      <Space wrap>
        <span>周期延迟</span><InputNumber aria-label="周期延迟" disabled={configLocked} min={0} max={60000} value={config.cycleDelayMs} addonAfter="毫秒" onChange={v => changeConfig({ cycleDelayMs: Number(v ?? 0) })} />
        <span>看门狗超时</span><InputNumber aria-label="看门狗超时" disabled={configLocked} min={100} max={600000} value={config.maxCycleMs} addonAfter="毫秒" onChange={v => changeConfig({ maxCycleMs: Number(v ?? 15000) })} />
        <span>最大连续失败次数</span><InputNumber disabled={configLocked} min={1} max={1000} value={config.maxConsecutiveFailures} onChange={v => changeConfig({ maxConsecutiveFailures: Number(v ?? 3) })} />
        <span>恢复延迟</span><InputNumber disabled={configLocked} min={0} max={600000} value={config.recoveryDelayMs} addonAfter="毫秒" onChange={v => changeConfig({ recoveryDelayMs: Number(v ?? 1000) })} />
        <span>停止超时</span><InputNumber disabled={configLocked} min={100} max={maxStopTimeoutMs} value={config.stopTimeoutMs} addonAfter="毫秒" onChange={v => changeConfig({ stopTimeoutMs: Number(v ?? 5000) })} />
      </Space>
      {saveRow}
    </Space>
  );

  const ptpTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Checkbox disabled={configLocked} checked={config.ptpDriftGuardEnabled} onChange={e => changeConfig({ ptpDriftGuardEnabled: e.target.checked })}>启用 PTP 漂移保护</Checkbox>
      <Space wrap>
        <span>统计窗口</span><InputNumber disabled={configLocked || !config.ptpDriftGuardEnabled} min={5} max={500} value={config.ptpGuardWindowRuns} addonAfter="次" onChange={v => changeConfig({ ptpGuardWindowRuns: Number(v ?? 20) })} />
        <span>最少样本数</span><InputNumber disabled={configLocked || !config.ptpDriftGuardEnabled} min={1} max={config.ptpGuardWindowRuns} value={config.ptpGuardMinimumEvidenceRuns} addonAfter="次" onChange={v => changeConfig({ ptpGuardMinimumEvidenceRuns: Number(v ?? 5) })} />
        <span>最低就绪率</span><InputNumber disabled={configLocked || !config.ptpDriftGuardEnabled} min={50} max={100} value={Math.round(config.ptpGuardMinimumReadyRate * 1000) / 10} addonAfter="%" onChange={v => changeConfig({ ptpGuardMinimumReadyRate: Number(v ?? 99) / 100 })} />
        <span>检查周期</span><InputNumber disabled={configLocked || !config.ptpDriftGuardEnabled} min={1} max={10000} value={config.ptpGuardCheckEveryCycles} addonAfter="周期" onChange={v => changeConfig({ ptpGuardCheckEveryCycles: Number(v ?? 10) })} />
        <Checkbox disabled={configLocked || !config.ptpDriftGuardEnabled} checked={config.ptpGuardFaultOnMasterClockChange} onChange={e => changeConfig({ ptpGuardFaultOnMasterClockChange: e.target.checked })}>主时钟变更时触发故障</Checkbox>
      </Space>
      {saveRow}
    </Space>
  );

  const syncTab = (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Checkbox disabled={configLocked} checked={config.synchronizationHealthGuardEnabled} onChange={e => changeConfig({ synchronizationHealthGuardEnabled: e.target.checked })}>启用同步健康保护</Checkbox>
      <Space wrap>
        <span>统计窗口</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={5} max={500} value={config.synchronizationGuardWindowRuns} addonAfter="次" onChange={v => changeConfig({ synchronizationGuardWindowRuns: Number(v ?? 20) })} />
        <span>最少样本数</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={config.synchronizationGuardWindowRuns} value={config.synchronizationGuardMinimumEvidenceRuns} addonAfter="次" onChange={v => changeConfig({ synchronizationGuardMinimumEvidenceRuns: Number(v ?? 5) })} />
        <span>最大失败率</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} value={config.synchronizationGuardMaximumFailureRate * 100} addonAfter="%" onChange={v => changeConfig({ synchronizationGuardMaximumFailureRate: Number(v ?? 5) / 100 })} />
        <span>最大帧超时率</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} value={config.synchronizationGuardMaximumFrameTimeoutRate * 100} addonAfter="%" onChange={v => changeConfig({ synchronizationGuardMaximumFrameTimeoutRate: Number(v ?? 5) / 100 })} />
        <span>连续失败阈值</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardMaxConsecutiveFailures} addonAfter="次" onChange={v => changeConfig({ synchronizationGuardMaxConsecutiveFailures: Number(v ?? 2) })} />
        <span>连续偏差阈值</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardMaxConsecutiveSkewViolations} addonAfter="次" onChange={v => changeConfig({ synchronizationGuardMaxConsecutiveSkewViolations: Number(v ?? 3) })} />
        <span>原生帧丢失率</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} step={0.05} value={config.synchronizationGuardMaximumNativeFrameLossRate * 100} addonAfter="%" onChange={v => changeConfig({ synchronizationGuardMaximumNativeFrameLossRate: Number(v ?? 0.5) / 100 })} />
        <span>最大缓冲区欠载</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={1000000} value={config.synchronizationGuardMaximumBufferUnderruns} onChange={v => changeConfig({ synchronizationGuardMaximumBufferUnderruns: Number(v ?? 0) })} />
        <span>最大重新同步次数</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={1000000} value={config.synchronizationGuardMaximumResynchronizations} onChange={v => changeConfig({ synchronizationGuardMaximumResynchronizations: Number(v ?? 0) })} />
        <span>序号间隔回退率</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} step={0.1} value={config.synchronizationGuardMaximumSequenceGapRate * 100} addonAfter="%" onChange={v => changeConfig({ synchronizationGuardMaximumSequenceGapRate: Number(v ?? 1) / 100 })} />
        <span>检查周期</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={10000} value={config.synchronizationGuardCheckEveryCycles} addonAfter="周期" onChange={v => changeConfig({ synchronizationGuardCheckEveryCycles: Number(v ?? 5) })} />
        <span>触发故障连续检查数</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardUnhealthyChecksToFault} addonAfter="次" onChange={v => changeConfig({ synchronizationGuardUnhealthyChecksToFault: Number(v ?? 2) })} />
        <span>恢复连续检查数</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardHealthyChecksToRecover} addonAfter="次" onChange={v => changeConfig({ synchronizationGuardHealthyChecksToRecover: Number(v ?? 3) })} />
        <span>恢复超时</span><InputNumber disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1000} max={600000} step={1000} value={config.synchronizationGuardRecoveryTimeoutMs} addonAfter="毫秒" onChange={v => changeConfig({ synchronizationGuardRecoveryTimeoutMs: Number(v ?? 30000) })} />
      </Space>
      {saveRow}
    </Space>
  );

  const localDetails = (
    <>
      {hero}
      {actions}
      {/* R05：待核对设备动作——管理员必须在产品内看到清单、提交可追溯的证据；
          核对前所有设备动作入口（生产/手动/临时运行）都会被统一授权闸门拒绝。 */}
      {canReconcile && errors.pending && (
        <Alert
          type="warning"
          showIcon
          title="待核对动作清单暂不可用"
          description={`${errors.pending}。保留最后读取到的清单，请核对连接和权限。`}
        />
      )}
      {canReconcile && pendingActions && (
        <Alert
          type="error"
          showIcon
          title="存在待核对的设备动作：核对前所有设备动作入口都会被拒绝"
          description={
            <Space direction="vertical" size={4}>
              <span>原因：{pendingActions.reason ?? '上一周期的设备动作结果未确认'}{pendingActions.unreadable ? '（安全状态文件不可读，需人工处理）' : ''}</span>
              {pendingActions.runId && <span>运行：{pendingActions.runId}</span>}
              {(pendingActions.deviceIds?.length ?? 0) > 0 && <span>涉及设备：{(pendingActions.deviceIds ?? []).join('、')}</span>}
              {(pendingActions.robotIds?.length ?? 0) > 0 && <span>涉及机器人：{(pendingActions.robotIds ?? []).join('、')}</span>}
              {(pendingActions.manualIntents ?? []).map((intent) => (
                <span key={intent.runId}>
                  未闭合动作：<b>{intent.action}</b>（{intent.state}{intent.closedReason ? ` · ${intent.closedReason}` : ''}）
                  {intent.deviceIds.length > 0 ? ` · 设备 ${intent.deviceIds.join('、')}` : ''}
                  {intent.robotIds.length > 0 ? ` · 机器人 ${intent.robotIds.join('、')}` : ''}
                  {' '}
                  <Button danger size="small" onClick={() => { setResolveTarget({ kind: 'manual', runId: intent.runId, action: intent.action }); setResolveOpen(true); }}>
                    核对这条动作
                  </Button>
                </span>
              ))}
              <span>步骤：现场核对设备物理状态与身份 → 提交核对证据（写入审计）→ 生产/手动动作恢复可用。</span>
              <Button danger size="small" onClick={() => { setResolveTarget({ kind: 'production', pending: pendingActions }); setResolveOpen(true); }}>提交核对证据</Button>
            </Space>
          }
        />
      )}
      <Modal
        open={canReconcile && resolveOpen}
        onCancel={() => { if (!resolveRequest.current) setResolveOpen(false); }}
        closable={!resolveSubmitting} keyboard={!resolveSubmitting} mask={{ closable: !resolveSubmitting }}
        confirmLoading={resolveSubmitting} cancelButtonProps={{ disabled: resolveSubmitting }}
        title={resolveTarget.kind === 'manual' ? `核对动作：${resolveTarget.action}` : '提交设备动作核对证据'}
        okText="提交核对" cancelText="取消"
        okButtonProps={{ danger: true }}
        onOk={async () => {
          if (resolveRequest.current) return;
          const request = {};
          resolveRequest.current = request;
          const owns = () => resolveRequest.current === request;
          setResolveSubmitting(true);
          try {
            const values = await resolveForm.validateFields();
            if (!owns()) return;
            const result = await call({ url: apiUrl('production/device-actions/resolve'), action: 'resolve', successMessage: '核对证据已提交并写入审计', body: {
              runId: resolveTarget.kind === 'production' ? (resolveTarget.pending.runId ?? '') : '',
              manifestHash: resolveTarget.kind === 'production' ? (resolveTarget.pending.manifestHash ?? '') : '',
              deviceIds: resolveTarget.kind === 'production' ? (resolveTarget.pending.deviceIds ?? []) : [],
              robotIds: resolveTarget.kind === 'production' ? (resolveTarget.pending.robotIds ?? []) : [],
              reason: values.reason ?? 'manual reconciliation', evidence: values.evidence,
              deviceIdentityEvidence: values.identityEvidence,
              manualIntentId: resolveTarget.kind === 'manual' ? resolveTarget.runId : null,
            }, onSuccess: acceptStatus });
            if (owns() && result.ok) { setResolveOpen(false); resolveForm.resetFields(); }
          } catch { /* 字段校验错误由 Form 显示，后端错误由操作区保留。 */ }
          finally { if (owns()) { resolveRequest.current = undefined; setResolveSubmitting(false); } }
        }}
      >
        <div>本次核对目标：{resolveTarget.kind === 'manual' ? resolveTarget.runId : resolveTarget.pending.runId ?? '安全状态文件中的未确认动作'}</div>
        <Form form={resolveForm} layout="vertical" disabled={resolveSubmitting}>
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
          title={`生产已进入故障状态${status?.consecutiveFailures ? ` · 连续失败 ${status.consecutiveFailures} 次` : ''}`}
          description={<>
            <div>原因：{status?.lastError ?? '运行时未提供错误详情，请查看下方保护状态与告警记录。'}</div>
            <div>下一步：检查设备与同步健康后点击“恢复”；若故障持续，先“停止”再重新“启动”。</div>
          </>}
        />
      )}
      {operationError && <Alert type="error" showIcon title="生产操作未完成或结果未确认" description={operationError} />}
      {errors.config && <Alert type="warning" showIcon title="生产配置暂不可用" description={errors.config} action={<Button onClick={monitor.refresh}>重试读取</Button>} />}
      {errors.jobs && <Alert type="warning" showIcon title="配方列表已过期" description={errors.jobs} />}
      {errors.alarms && <Alert type="warning" showIcon title="告警记录已过期" description={errors.alarms} />}
      {errors.alarmSummary && <Alert type="warning" showIcon title="活动告警统计未更新" description={errors.alarmSummary} />}
      {merge && <ProductionConfigConflict key={JSON.stringify(merge.fields)} merge={merge} disabled={configLocked || busy.size > 0}
        onStale={() => { void messageApi.warning('配置已再次变更，请重新核对差异。'); }} />}
      {dirty && !conflict && <Alert type="info" showIcon title="生产配置有未保存修改" description="实时监控继续刷新，配置草稿会保留；保存配置后才可启动生产。" />}
      {status?.ptpGuard?.enabled && !status.ptpGuard.healthy && (
        <Alert type="error" showIcon title="PTP 生产保护已阻止运行" description={status.ptpGuard.summary ?? `${status.ptpGuard.groups.length} 个受保护的同步组未就绪。`} />
      )}
      {status?.synchronizationGuard?.enabled && !status.synchronizationGuard.healthy && (
        <Alert
          type={status.synchronizationGuard.liveRecoverableOnly ? 'warning' : 'error'}
          showIcon
          title="同步健康保护降级"
          description={status.synchronizationGuard.summary ?? `${status.synchronizationGuard.groups.length} 个同步相机组未达标。`}
        />
      )}
      {activeAlarmCount > 0 && (
        <Alert
          type="warning"
          showIcon
          title={`${activeAlarmCount} 条活动告警${criticalAlarmCount ? ` · ${criticalAlarmCount} 条严重` : ''}`}
          description={latestActiveMessage ? `最近：${latestActiveMessage}` : undefined}
          action={!kiosk && pendingAlarmCount > 0 && <Button size="small" onClick={() => { setAlarmFilter('pending'); setActiveTab('alarms'); }}>
            查看待确认告警（{pendingAlarmCount}）
          </Button>}
        />
      )}
    </>
  );

  const operatorTab = open && <div className="operator-workspace-content">
    {pendingAlarmCount > 0 && <Suspense fallback={<span role="status">正在读取待确认告警…</span>}>
      <OperatorAlarmQueue busy={busy} onAcknowledge={acknowledgeAlarm} />
    </Suspense>}
    <OperatorDashboard key={`${selectedJobId ?? ''}-${layoutRevision}`} jobId={selectedJobId} status={status} statusFresh={statusFresh}
      alarmSummary={alarmSummary} alarmFresh={!errors.alarmSummary && Boolean(alarmSummary)} kiosk={kiosk} lastUpdatedAt={lastUpdatedAt}
      onEdit={canConfigure && !kiosk && jobs.length > 0 ? () => setLayoutEditorOpen(true) : undefined} />
  </div>;

  // kiosk：没有标签栏，只有操作员工作台——车间大屏上任何可切换的入口都是误触来源。
  // 标签分两组：运行（操作员/监控/告警/工位/收件箱）与配置（生产配置/PTP/同步健康）。
  // 此前 8 个同层标签挤在一起，"PTP 保护"要扫过整排才找得到，且操作与配置语义混在一处。
  const runTabKeys = ['operator', 'monitor', 'alarms', 'stations', ...(canEngineer ? ['deliveries'] : [])];
  const configTabKeys = canConfigure ? ['config', 'ptp', 'sync'] : [];
  // 回退用"工程师最常用的运行监控"而不是列表第一项（第一项是操作员预览，只适合操作员视角）
  const runFallbackTab = canConfigure ? 'monitor' : 'operator';
  const shownTab = (tabGroup === 'run' ? runTabKeys : configTabKeys).includes(activeTab)
    ? activeTab : (tabGroup === 'run' ? runFallbackTab : 'config');
  const switchTabGroup = (next: 'run' | 'configure') => {
    setTabGroup(next);
    const keys = next === 'run' ? runTabKeys : configTabKeys;
    if (!keys.includes(activeTab)) setActiveTab(next === 'run' ? runFallbackTab : 'config');
  };
  const runTabItems = [
    { key: 'operator', label: '操作员', children: shownTab === 'operator' && operatorTab },
    { key: 'monitor', label: '运行监控', children: monitorTab },
    { key: 'alarms', label: `告警${activeAlarmCount ? `（${activeAlarmCount}）` : ''}`, children: alarmsTab },
    { key: 'stations', label: '多工位', children: open && shownTab === 'stations' &&
      <Suspense fallback={<span role="status">正在加载工位总览…</span>}><ProductionStations canSend={canConfigure} /></Suspense> },
    ...(canEngineer ? [{ key: 'deliveries', label: '配方收件箱', children: open && shownTab === 'deliveries' &&
      <Suspense fallback={<span role="status">正在加载配方收件箱…</span>}>
        <RecipeDeliveryInbox onPublished={monitor.refresh} />
      </Suspense> }] : []),
  ];
  const configTabItems = canConfigure ? [
    { key: 'config', label: '生产配置', children: configTab },
    { key: 'ptp', label: 'PTP 保护', children: ptpTab },
    { key: 'sync', label: '同步健康保护', children: syncTab },
  ] : [];

  const body = kiosk ? (
    <Space direction="vertical" style={{ width: '100%' }} size="middle">
      {contextHolder}
      {localDetails}
      {operatorTab}
    </Space>
  ) : (
    <Space direction="vertical" style={{ width: '100%' }} size="middle">
      {contextHolder}
      {shownTab !== 'stations' && shownTab !== 'deliveries' && localDetails}
      {canConfigure && <Segmented size="small" value={tabGroup}
        onChange={(value) => switchTabGroup(value as 'run' | 'configure')}
        options={[{ label: '运行', value: 'run' }, { label: '配置', value: 'configure' }]} />}
      <Tabs
        activeKey={shownTab}
        onChange={setActiveTab}
        items={tabGroup === 'run' ? runTabItems : configTabItems}
      />
      {open && canConfigure && layoutEditorOpen && <Suspense fallback={<span role="status">正在加载操作员布局…</span>}>
        <OperatorLayoutDialog jobs={jobs} initialJobId={selectedJobId}
          onClose={() => setLayoutEditorOpen(false)} onSaved={() => setLayoutRevision((revision) => revision + 1)} />
      </Suspense> }
    </Space>
  );

  if (embedded) return <div className={`runtime-only-page${kiosk ? ' is-kiosk' : ''}`}>
    {!kiosk && <div className="runtime-only-title">VisionStudio · 生产运行</div>}
    {body}
  </div>;
  return <Modal open={open} onCancel={onClose} footer={null} title="生产运行" width={1120}>{body}</Modal>;
}
