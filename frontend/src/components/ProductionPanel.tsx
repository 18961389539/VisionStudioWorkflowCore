import { lazy, Suspense, useEffect, useRef, useState, type ReactNode } from 'react';
import {
  Alert, Button, Checkbox, Descriptions, Form, Input, InputNumber, message, Modal, Popconfirm, Radio, Segmented, Select, Space, Table,
  Tabs, Tag, Typography
} from 'antd';
import type { AlarmRecord, ProductionRuntimeStatus } from '../types';
import { localizeStatus, type MessageKey, type MessageParams } from '../i18n';
import { useI18n } from '../i18n/provider';
import { isProductionConfig, isProductionStatus } from '../productionConfig';
import { ALARM_PAGE_SIZE, useProductionMonitor, type DeviceActionPendingState } from '../useProductionMonitor';
import { useProductionConfigDraft, type ProductionConfigMerge } from '../useProductionConfigDraft';
import { useProductionActions } from '../useProductionActions';
import { alarms as alarmEndpoints, production } from '../endpoints';
import { useSecurityAccount } from '../security';
import { OperatorDashboard } from './OperatorDashboard';
import './ProductionWorkstation.css';

const ProductionStations = lazy(() => import('./ProductionStations'));
const LocalParallelRuntimes = lazy(() => import('./LocalParallelRuntimes'));
const OperatorAlarmQueue = lazy(() => import('./OperatorAlarmQueue'));
const OperatorLayoutDialog = lazy(() => import('./OperatorLayoutDialog'));
const RecipeDeliveryInbox = lazy(() => import('./ProductionRecipeRollouts').then((module) => ({ default: module.RecipeDeliveryInbox })));

export type { DeviceActionPendingState } from '../useProductionMonitor';

type Props = { open: boolean; onClose: () => void; embedded?: boolean; runtimeOnly?: boolean; initialView?: 'operator' | 'stations' | 'deliveries';
  initialJobId?: string;
  stationId?: string;
  /** 嵌入式生产页顶栏右侧操作区（账号徽标、诊断、管理按钮等），纳入文档流避免固定定位遮挡 */
  headerActions?: ReactNode;
  /** 车间大屏形态：只渲染操作员工作台，隐藏标签栏与工程师入口（`?view=operator&kiosk=1`）。 */
  kiosk?: boolean };

type Translate = (key: MessageKey, params?: MessageParams) => string;

const severityLabelKeys = {
  Critical: 'production.severity.critical',
  Error: 'production.severity.error',
  Warning: 'production.severity.warning',
  Info: 'production.severity.info'
} as const satisfies Record<string, MessageKey>;

const maxStopTimeoutMs = 60_000;
const configFieldLabelKeys = {
  jobId: 'production.configField.jobId',
  autoStart: 'production.configField.autoStart',
  cycleDelayMs: 'production.configField.cycleDelayMs',
  maxCycleMs: 'production.configField.maxCycleMs',
  maxConsecutiveFailures: 'production.configField.maxConsecutiveFailures',
  autoRecover: 'production.configField.autoRecover',
  recoveryDelayMs: 'production.configField.recoveryDelayMs',
  maxRecoveryAttempts: 'production.configField.maxRecoveryAttempts',
  stopTimeoutMs: 'production.configField.stopTimeoutMs',
  ptpDriftGuardEnabled: 'production.configField.ptpDriftGuardEnabled',
  synchronizationHealthGuardEnabled: 'production.configField.synchronizationHealthGuardEnabled'
} as const satisfies Record<string, MessageKey>;

/** 配置字段可能来自服务端新增字段：未登记的一律回落到原始字段名，不隐藏信息。 */
function configFieldLabel(field: string, t: Translate): string {
  const key = (configFieldLabelKeys as Record<string, MessageKey | undefined>)[field];
  return key ? t(key) : field;
}

function displayConfigValue(value: unknown, t: Translate): string {
  if (value == null) return '—';
  if (typeof value === 'boolean') return value ? t('production.value.enabled') : t('production.value.disabled');
  return String(value);
}

function ProductionConfigConflict({ merge, disabled, onStale }: {
  merge: ProductionConfigMerge;
  disabled: boolean;
  onStale: () => void;
}) {
  const { t } = useI18n();
  const [choices, setChoices] = useState<Parameters<ProductionConfigMerge['apply']>[0]>({});
  const overlapping = merge.fields.filter(({ source }) => source === 'choice');
  return <Alert type="warning" showIcon title={t('production.merge.title')} description={
    <Space orientation="vertical" align="start">
      <div>{overlapping.length > 0 ? t('production.merge.choiceRequired') : t('production.merge.disjoint')}{t('production.merge.onlyDraft')}</div>
      <details open={overlapping.length > 0}>
        <summary>{t('production.merge.compare')}</summary>
        <ul>{merge.fields.map(({ field, draft, latest, source }) => {
          const label = configFieldLabel(field, t);
          return <li key={field}>
            <span>{t('production.merge.fieldDelta', { label, draft: displayConfigValue(draft, t), latest: displayConfigValue(latest, t) })}</span>
            {source === 'choice' && <Radio.Group
              aria-label={t('production.merge.aria', { label })}
              value={choices[field]} disabled={disabled}
              onChange={(event) => setChoices((previous) => ({ ...previous, [field]: event.target.value }))}
            >
              <Radio value="draft">{t('production.merge.keepDraft')}</Radio>
              <Radio value="remote">{t('production.merge.useRemote')}</Radio>
            </Radio.Group>}
          </li>;
        })}</ul>
      </details>
      <Button disabled={disabled || overlapping.some(({ field }) => !choices[field])}
        onClick={() => { if (!merge.apply(choices)) onStale(); }}>
        {t('production.merge.apply')}
      </Button>
    </Space>
  } />;
}

export default function ProductionPanel({ open, onClose, embedded = false, runtimeOnly = false, initialView, initialJobId, kiosk = false, stationId, headerActions }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const { t, dateLocale: locale } = useI18n();
  // 列表分隔符随语言切换：中文用顿号，英文用逗号加空格。
  const listSeparator = locale.startsWith('zh') ? '、' : ', ';
  const { enabled: securityEnabled, user } = useSecurityAccount();
  const canReconcile = !securityEnabled || user?.role === 'Administrator';
  const canEngineer = !securityEnabled || user?.role === 'Engineer' || user?.role === 'Administrator';
  const canConfigure = !runtimeOnly && canEngineer;
  const monitor = useProductionMonitor(open, canReconcile, stationId);
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
  const initialJobAppliedRef = useRef<string | undefined>(undefined);
  const [resolveForm] = Form.useForm<{ evidence: string; identityEvidence: string; reason?: string }>();
  const stationScoped = Boolean(stationId && stationId.toLowerCase() !== 'default');
  const actionStationId = stationId ?? 'default';
  const stationActionUrl = {
    start: stationScoped ? production.runtimeStart(actionStationId) : production.start,
    stop: stationScoped ? production.runtimeStop(actionStationId) : production.stop,
    recover: stationScoped ? production.runtimeRecover(actionStationId) : production.recover,
    config: stationScoped ? production.runtimeConfig(actionStationId) : production.config,
    resolve: stationScoped ? production.runtimeResolveAction(actionStationId) : production.resolveAction,
  };
  useEffect(() => {
    if (!open) { setResolveOpen(false); setResolveSubmitting(false); setLayoutEditorOpen(false); }
    return () => { resolveRequest.current = undefined; };
  }, [open]);

  const applyStatus = (data: unknown) => {
    if (!isProductionStatus(data)) throw new Error(t('production.error.invalidStatus'));
    monitor.acceptStatus(data);
    return data;
  };
  const acceptStatus = (data: unknown) => { applyStatus(data); };
  const stopProduction = () => {
    void call({
      url: stationActionUrl.stop, action: 'stop', timeoutMs: maxStopTimeoutMs + 10_000, successMessage: t('production.toast.stopped'),
      onSuccess: (data) => {
        const next = applyStatus(data);
        if (next.state === 'Stopped' && !next.productionLocked) return;
        return { error: typeof next.lastError === 'string' && next.lastError
          ? next.lastError : t('production.error.stopIncomplete') };
      },
    });
  };
  const startProduction = () => {
    const current = readCurrent();
    if (!statusFresh || !configReady || locked || current.dirty || current.conflict || !current.config?.jobId) return;
    void call({ url: stationActionUrl.start, body: { jobId: current.config.jobId }, successMessage: t('production.toast.started'), action: 'start', onSuccess: acceptStatus });
  };
  const saveConfig = async () => {
    const current = readCurrent();
    if (!statusFresh || !configReady || locked || !current.dirty || current.conflict || !current.config || !monitor.configRevision) return;
    const submitted = current.config;
    const finishSave = beginSave();
    if (!finishSave) return;
    try {
      await call({ url: stationActionUrl.config, method: 'PUT', body: submitted, headers: { 'If-Match': monitor.configRevision },
        successMessage: t('production.toast.configSaved'), action: 'save', onSuccess: (data, response) => {
        if (!isProductionConfig(data)) throw new Error(t('production.error.invalidConfig'));
        const revision = response.headers.get('ETag');
        if (!revision) throw new Error(t('production.error.missingRevision'));
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
    : selectedJobId ?? t('production.hero.noJob');
  const jobVersion = status?.lockedJobVersion ?? activeJob?.publishedVersion ?? null;
  const stateDetail = !statusFresh ? t('production.state.unconfirmed')
    : locked ? t('production.state.lockedTo', { jobId: status?.jobId ?? '-', version: status?.lockedJobVersion ?? '-' })
      : canConfigure ? t('production.state.stoppedEditable') : t('production.state.stopped');
  const startBlockedReason = !statusFresh ? t('production.blocked.statusUnconfirmed')
    : locked ? t('production.blocked.locked')
      : !configReady ? t('production.blocked.configNotReady')
        : conflict ? t('production.blocked.conflict')
          : dirty ? t('production.blocked.dirty')
            : !config.jobId ? t('production.blocked.noJob') : undefined;

  useEffect(() => {
    if (!open) {
      initialJobAppliedRef.current = undefined;
      return;
    }
    if (!initialJobId || !configReady || !statusFresh || locked
      || !jobs.some((job) => job.id === initialJobId && job.publishedVersion != null)
      || initialJobAppliedRef.current === initialJobId) return;
    initialJobAppliedRef.current = initialJobId;
    if (config?.jobId !== initialJobId) changeConfig({ jobId: initialJobId });
  }, [open, initialJobId, configReady, statusFresh, locked, jobs, config?.jobId, changeConfig]);

  // 首屏：状态、作业版本、产量与节拍集中在一条横幅上（操作员远距离可读）
  const hero = (
    <div className="production-hero">
      <div className="production-hero-state">
        {/* 过期时不再用绿/红表示状态：颜色本身会被读成"当前在线"，与"状态未确认"矛盾 */}
        <Tag color={!status || !statusFresh ? 'default' : stateColor}>{status ? localizeStatus(state) : t('production.hero.reading')}</Tag>
        <span className="production-hero-sub">{stateDetail}</span>
      </div>
      <div className="production-hero-metrics">
        <div className="production-hero-metric"><b title={jobLabel}>{jobLabel}</b><span>{t('production.hero.jobRecipe')}</span></div>
        <div className="production-hero-metric"><b>{jobVersion != null ? `V${jobVersion}` : '-'}</b><span>{locked ? t('production.hero.runningVersion') : t('production.hero.publishedVersion')}</span></div>
        {activeTab !== 'operator' && <>
          <div className="production-hero-metric"><b>{status?.cycleCount ?? 0}</b><span>{t('production.hero.cycles')}</span></div>
          <div className="production-hero-metric"><b>{status?.okCount ?? 0}</b><span>{t('production.hero.ok')}</span></div>
          <div className="production-hero-metric"><b>{status?.ngCount ?? 0}</b><span>{t('production.hero.ng')}</span></div>
          <div className="production-hero-metric"><b>{(status?.lastDurationMs ?? 0).toFixed(1)}</b><span>{t('production.hero.lastDuration')}</span></div>
        </>}
      </div>
      <span className={`production-hero-updated${!statusFresh ? ' stale' : ''}`}>
        {!statusFresh
          ? t('production.hero.stale', {
            time: lastUpdatedAt?.toLocaleTimeString(locale) ?? '—',
            error: errors.status ? ` · ${errors.status}` : ''
          })
          : t('production.hero.updatedAt', { time: lastUpdatedAt?.toLocaleTimeString(locale) ?? '-' })}
      </span>
    </div>
  );

  const actions = (
    <div className="production-actions">
      <Button type="primary" disabled={Boolean(startBlockedReason) || busy.size > 0} loading={busy.has('start')} onClick={() => void startProduction()}>{t('production.action.start')}</Button>
      <Popconfirm
        title={t('production.action.stopConfirmTitle')}
        description={t('production.action.stopConfirmBody', { jobId: status?.jobId ?? '-', version: status?.lockedJobVersion ?? '-' })}
        okText={t('production.action.stop')} cancelText={t('production.action.cancel')} okButtonProps={{ danger: true }}
        onConfirm={stopProduction}
        disabled={!locked && statusFresh}>
        <Button danger disabled={!locked && statusFresh} loading={busy.has('stop')}>{t('production.action.stop')}</Button>
      </Popconfirm>
      <Button disabled={state !== 'Faulted' || !statusFresh || dirty || !configReady || busy.size > 0} loading={busy.has('recover')} onClick={() => void call({ url: stationActionUrl.recover, successMessage: t('production.toast.recoverSent'), action: 'recover', onSuccess: acceptStatus })}>{t('production.action.recover')}</Button>
      {startBlockedReason && <span className="production-actions-hint">{startBlockedReason}</span>}
    </div>
  );

  const monitorTab = (
    <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type={locked ? 'warning' : 'info'}
        showIcon
        title={statusFresh && locked ? t('production.monitor.lockedTo', { jobId: status?.jobId ?? '-', version: status?.lockedJobVersion ?? '-' }) : stateDetail}
        description={locked ? t('production.monitor.lockedDescription', {
          workflow: status?.lockedWorkflowHash ?? '-',
          dependency: status?.lockedDependencyManifestHash?.slice(0, 12) ?? '-'
        }) : t('production.monitor.lockHint')}
      />

      {status?.ptpGuard?.enabled && (
        <Alert
          type={status.ptpGuard.healthy ? 'success' : 'error'}
          showIcon
          title={t('production.monitor.ptpTitle', { state: status.ptpGuard.healthy ? t('production.value.normal') : t('production.value.blockedFault') })}
          description={status.ptpGuard.summary ?? t('production.monitor.guardGroups', { count: status.ptpGuard.groups.length })}
        />
      )}

      {status?.synchronizationGuard?.enabled && (
        <Alert
          type={status.synchronizationGuard.healthy ? 'success' : status.synchronizationGuard.liveRecoverableOnly ? 'warning' : 'error'}
          showIcon
          title={t('production.monitor.syncTitle', {
            state: status.synchronizationGuard.healthy ? t('production.value.normal')
              : status.synchronizationGuard.liveRecoverableOnly ? t('production.value.recoverable') : t('production.value.degradedFault')
          })}
          description={status.synchronizationGuard.summary ?? t('production.monitor.syncGroups', { count: status.synchronizationGuard.groups.length })}
        />
      )}

      <Descriptions bordered size="small" column={4}>
        <Descriptions.Item label={t('production.desc.startedAt')}>{status?.startedAt ? new Date(status.startedAt).toLocaleString(locale) : '-'}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.lastCycle')}>{status?.lastCycleAt ? new Date(status.lastCycleAt).toLocaleString(locale) : '-'}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.errorCount')}>{status?.errorCount ?? 0}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.watchdogTrips')}>{status?.watchdogTrips ?? 0}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.consecutiveFailures')}>{status?.consecutiveFailures ?? 0}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.lastDisposition')}>{localizeStatus(status?.lastDisposition) ?? '-'}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.runId')} span={2}>{status?.currentRunId ?? '-'}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.dependencyHash')} span={2}>{status?.lockedDependencyManifestHash?.slice(0, 20) ?? '-'}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.lastError')} span={2}>{status?.lastError ?? '-'}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.ptpGuard')}>
          <Tag color={!status?.ptpGuard?.enabled ? 'default' : status.ptpGuard.healthy ? 'green' : 'red'}>
            {!status?.ptpGuard?.enabled ? t('production.value.disabled') : status.ptpGuard.healthy ? t('production.value.normal') : t('production.value.fault')}
          </Tag>
        </Descriptions.Item>
        <Descriptions.Item label={t('production.desc.ptpGroups')}>{status?.ptpGuard?.groups?.length ?? 0}</Descriptions.Item>
        <Descriptions.Item label={t('production.desc.syncGuard')}>
          <Tag color={!status?.synchronizationGuard?.enabled ? 'default' : status.synchronizationGuard.healthy ? 'green' : status.synchronizationGuard.liveRecoverableOnly ? 'orange' : 'red'}>
            {!status?.synchronizationGuard?.enabled ? t('production.value.disabled') : status.synchronizationGuard.healthy ? t('production.value.normal') : status.synchronizationGuard.liveRecoverableOnly ? t('production.value.recoverable') : t('production.value.fault')}
          </Tag>
        </Descriptions.Item>
        <Descriptions.Item label={t('production.desc.syncGroups')}>{status?.synchronizationGuard?.groups?.length ?? 0}</Descriptions.Item>
      </Descriptions>

      {status?.ptpGuard?.enabled && status.ptpGuard.groups.length > 0 && (
        <>
          <div className="panel-title">{t('production.monitor.ptpSection')}</div>
          <Table
            size="small" rowKey="groupId" pagination={false} dataSource={status.ptpGuard.groups}
            columns={[
              { title: t('production.col.group'), dataIndex: 'groupName' },
              { title: t('production.col.status'), width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => <Tag color={g.healthy ? 'green' : 'red'}>{g.healthy ? t('production.value.normal') : t('production.value.fault')}</Tag> },
              { title: t('production.col.samples'), width: 95, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => `${g.evidenceRuns}/${g.windowRuns}` },
              { title: t('production.col.readyRate'), width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => g.ptpReadyRate == null ? '-' : `${(g.ptpReadyRate * 100).toFixed(1)}%` },
              { title: t('production.col.p95Offset'), width: 120, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['ptpGuard']>['groups'][number]) => g.p95MaxAbsOffsetNs == null ? '-' : `${g.p95MaxAbsOffsetNs.toFixed(0)} ns` },
              { title: t('production.col.masterClockChanges'), dataIndex: 'masterClockChanges', width: 115 },
              { title: t('production.col.reason'), dataIndex: 'reason' }
            ]}
          />
        </>
      )}

      {status?.synchronizationGuard?.enabled && status.synchronizationGuard.groups.length > 0 && (
        <>
          <div className="panel-title">{t('production.monitor.syncSection')}</div>
          <Table
            size="small" rowKey="groupId" pagination={false} dataSource={status.synchronizationGuard.groups}
            columns={[
              { title: t('production.col.group'), dataIndex: 'groupName' },
              { title: t('production.col.status'), width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => <Tag color={g.healthy ? 'green' : g.liveReady ? 'orange' : 'red'}>{g.healthy ? t('production.value.normal') : g.liveReady ? t('production.value.degraded') : t('production.value.offline')}</Tag> },
              { title: t('production.col.completionRate'), width: 100, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => `${(g.completionRate * 100).toFixed(1)}%` },
              { title: t('production.col.timeoutRate'), width: 90, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => `${(g.frameTimeoutRate * 100).toFixed(1)}%` },
              { title: t('production.col.actionAckShortfall'), dataIndex: 'actionAckShortfallRuns', width: 90 },
              { title: t('production.col.consecutiveSkew'), dataIndex: 'consecutiveSkewViolations', width: 95 },
              { title: t('production.col.transportEvidence'), width: 135, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => g.transport ? <Tag color={g.transport.evidenceMode === 'vendor-native' ? 'green' : g.transport.nativeCameras > 0 ? 'gold' : 'default'}>{g.transport.evidenceMode === 'vendor-native' ? t('production.transport.vendorNative') : t('production.transport.sequenceEstimate')}</Tag> : '-' },
              { title: t('production.col.nativeFrameLoss'), width: 100, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => g.transport?.nativeFrameLossRate == null ? '-' : `${(g.transport.nativeFrameLossRate * 100).toFixed(3)}%` },
              { title: t('production.col.lostResend'), width: 115, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => g.transport ? `${g.transport.lostPackets + g.transport.failedPackets} / ${g.transport.resendRequests}` : '-' },
              { title: t('production.col.sequenceGapRate'), width: 100, render: (_: unknown, g: NonNullable<ProductionRuntimeStatus['synchronizationGuard']>['groups'][number]) => `${(g.estimatedSequenceGapRate * 100).toFixed(2)}%` },
              { title: t('production.col.reason'), dataIndex: 'reason' }
            ]}
          />
        </>
      )}
    </Space>
  );

  const acknowledgeAlarm = (id: string) => call({
    url: alarmEndpoints.ack(id), successMessage: t('production.toast.alarmAcked'), action: `ack:${id}`,
    onSuccess: (data) => {
      const alarm = data as Partial<AlarmRecord> | undefined;
      if (alarm?.id !== id || alarm.acknowledged !== true) throw new Error(t('production.error.invalidAckReceipt'));
      monitor.invalidateAlarmReads();
    },
  });

  const alarmsTab = (
    <>
      <Radio.Group aria-label={t('production.alarms.filter')} value={alarmFilter} onChange={(event) => setAlarmFilter(event.target.value)}>
        <Radio.Button value="all">{t('production.alarms.filterAll')}</Radio.Button>
        <Radio.Button value="active">{t('production.alarms.filterActive')}</Radio.Button>
        <Radio.Button value="pending">{t('production.alarms.filterPending')}</Radio.Button>
      </Radio.Group>
      <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 8, fontSize: 12 }}>
        {t('production.alarms.retention')}
      </Typography.Text>
    <Table
      size="small"
      rowKey="id"
      loading={alarmsLoading && alarms.length === 0}
      locale={{ emptyText: errors.alarms ? t('production.alarms.emptyUnavailable')
        : alarmFilter === 'pending' ? t('production.alarms.emptyPending')
          : alarmFilter === 'active' ? t('production.alarms.emptyActive') : t('production.alarms.empty') }}
      pagination={{ current: alarmPage, pageSize: ALARM_PAGE_SIZE, total: alarmTotal, showSizeChanger: false, onChange: setAlarmPage }}
      dataSource={alarms}
      columns={[
        { title: t('production.alarms.col.status'), width: 90, render: (_: unknown, a: AlarmRecord) => <Tag color={a.active ? 'red' : 'green'}>{a.active ? t('production.alarms.active') : t('production.alarms.recovered')}</Tag> },
        { title: t('production.alarms.col.severity'), dataIndex: 'severity', width: 90, render: (v: string) => (severityLabelKeys as Record<string, MessageKey | undefined>)[v] ? t((severityLabelKeys as Record<string, MessageKey>)[v]) : v },
        { title: t('production.alarms.col.code'), dataIndex: 'code', width: 150 },
        { title: t('production.alarms.col.message'), dataIndex: 'message' },
        { title: t('production.alarms.col.lastRaised'), width: 180, render: (_: unknown, a: AlarmRecord) => new Date(a.lastRaisedAt ?? a.raisedAt).toLocaleString(locale) },
        { title: t('production.alarms.col.count'), dataIndex: 'occurrenceCount', width: 72, render: (value: AlarmRecord['occurrenceCount']) => value ?? 1 },
        { title: t('production.alarms.col.ack'), width: 90, render: (_: unknown, a: AlarmRecord) => a.acknowledged ? <Tag>{t('production.alarms.acked')}</Tag> :
          <Button size="small" loading={busy.has(`ack:${a.id}`)} onClick={() => void acknowledgeAlarm(a.id)}>{t('production.alarms.ack')}</Button> }
      ]}
      />
    </>
  );

  const saveRow = (
    <Space>
      <Button loading={busy.has('save')} disabled={configLocked || !dirty || conflict || busy.size > 0} onClick={() => void saveConfig()}>{t('production.save.save')}</Button>
      <Button disabled={!dirty || busy.has('save') || !configReady} onClick={discard}>{t('production.save.discard')}</Button>
      <span className="production-actions-hint">{locked ? t('production.save.lockedHint') : dirty ? t('production.save.dirtyHint') : configReady ? t('production.save.syncedHint') : t('production.save.readingHint')}</span>
    </Space>
  );

  const configTab = (
    <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
      {initialJobId && config.jobId === initialJobId && dirty && <Alert type="info" showIcon
        title={t('production.config.publishedCandidateTitle')}
        description={t('production.config.publishedCandidateBody')} />}
      <Space wrap>
        <label htmlFor="prod-cfg-job">{t('production.config.job')}</label>
        <Select
          id="prod-cfg-job"
          style={{ width: 320 }} aria-label={t('production.config.job')}
          disabled={configLocked}
          value={config.jobId ?? undefined}
          placeholder={t('production.config.selectPublished')}
          options={jobs.filter(x => x.publishedVersion != null).map(x => ({ value: x.id, label: `${x.name} · V${x.publishedVersion}` }))}
          onChange={(jobId) => changeConfig({ jobId })}
        />
        <Checkbox disabled={configLocked} checked={config.autoStart} onChange={e => changeConfig({ autoStart: e.target.checked })}>{t('production.config.autoStart')}</Checkbox>
        <Checkbox disabled={configLocked} checked={config.autoRecover} onChange={e => changeConfig({ autoRecover: e.target.checked })}>{t('production.config.autoRecover')}</Checkbox>
      </Space>
      <Space wrap>
        <label htmlFor="prod-cfg-cycle-delay">{t('production.config.cycleDelay')}</label><Space.Compact><InputNumber id="prod-cfg-cycle-delay" aria-label={t('production.config.cycleDelay')} disabled={configLocked} min={0} max={60000} value={config.cycleDelayMs} onChange={v => changeConfig({ cycleDelayMs: Number(v ?? 0) })} /><Space.Addon>{t('production.unit.ms')}</Space.Addon></Space.Compact>
        <label htmlFor="prod-cfg-max-cycle">{t('production.config.watchdogTimeout')}</label><Space.Compact><InputNumber id="prod-cfg-max-cycle" aria-label={t('production.config.watchdogTimeout')} disabled={configLocked} min={100} max={600000} value={config.maxCycleMs} onChange={v => changeConfig({ maxCycleMs: Number(v ?? 15000) })} /><Space.Addon>{t('production.unit.ms')}</Space.Addon></Space.Compact>
        <label htmlFor="prod-cfg-max-failures">{t('production.config.maxConsecutiveFailures')}</label><InputNumber id="prod-cfg-max-failures" disabled={configLocked} min={1} max={1000} value={config.maxConsecutiveFailures} onChange={v => changeConfig({ maxConsecutiveFailures: Number(v ?? 3) })} />
        <label htmlFor="prod-cfg-recovery-delay">{t('production.config.recoveryDelay')}</label><Space.Compact><InputNumber id="prod-cfg-recovery-delay" disabled={configLocked} min={0} max={600000} value={config.recoveryDelayMs} onChange={v => changeConfig({ recoveryDelayMs: Number(v ?? 1000) })} /><Space.Addon>{t('production.unit.ms')}</Space.Addon></Space.Compact>
        <label htmlFor="prod-cfg-stop-timeout">{t('production.config.stopTimeout')}</label><Space.Compact><InputNumber id="prod-cfg-stop-timeout" disabled={configLocked} min={100} max={maxStopTimeoutMs} value={config.stopTimeoutMs} onChange={v => changeConfig({ stopTimeoutMs: Number(v ?? 5000) })} /><Space.Addon>{t('production.unit.ms')}</Space.Addon></Space.Compact>
      </Space>
      {saveRow}
    </Space>
  );

  const ptpTab = (
    <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
      <Checkbox disabled={configLocked} checked={config.ptpDriftGuardEnabled} onChange={e => changeConfig({ ptpDriftGuardEnabled: e.target.checked })}>{t('production.ptp.enable')}</Checkbox>
      <Space wrap>
        <label htmlFor="prod-ptp-window">{t('production.ptp.window')}</label><Space.Compact><InputNumber id="prod-ptp-window" disabled={configLocked || !config.ptpDriftGuardEnabled} min={5} max={500} value={config.ptpGuardWindowRuns} onChange={v => changeConfig({ ptpGuardWindowRuns: Number(v ?? 20) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
        <label htmlFor="prod-ptp-min-evidence">{t('production.ptp.minEvidence')}</label><Space.Compact><InputNumber id="prod-ptp-min-evidence" disabled={configLocked || !config.ptpDriftGuardEnabled} min={1} max={config.ptpGuardWindowRuns} value={config.ptpGuardMinimumEvidenceRuns} onChange={v => changeConfig({ ptpGuardMinimumEvidenceRuns: Number(v ?? 5) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
        <label htmlFor="prod-ptp-ready-rate">{t('production.ptp.minReadyRate')}</label><Space.Compact><InputNumber id="prod-ptp-ready-rate" disabled={configLocked || !config.ptpDriftGuardEnabled} min={50} max={100} value={Math.round(config.ptpGuardMinimumReadyRate * 1000) / 10} onChange={v => changeConfig({ ptpGuardMinimumReadyRate: Number(v ?? 99) / 100 })} /><Space.Addon>%</Space.Addon></Space.Compact>
        <label htmlFor="prod-ptp-check-cycles">{t('production.ptp.checkCycles')}</label><Space.Compact><InputNumber id="prod-ptp-check-cycles" disabled={configLocked || !config.ptpDriftGuardEnabled} min={1} max={10000} value={config.ptpGuardCheckEveryCycles} onChange={v => changeConfig({ ptpGuardCheckEveryCycles: Number(v ?? 10) })} /><Space.Addon>{t('production.unit.cycles')}</Space.Addon></Space.Compact>
        <Checkbox disabled={configLocked || !config.ptpDriftGuardEnabled} checked={config.ptpGuardFaultOnMasterClockChange} onChange={e => changeConfig({ ptpGuardFaultOnMasterClockChange: e.target.checked })}>{t('production.ptp.faultOnMasterClockChange')}</Checkbox>
      </Space>
      {saveRow}
    </Space>
  );

  const applySyncPreset = (preset: 'strict' | 'balanced' | 'relaxed') => {
    if (preset === 'strict') {
      changeConfig({
        synchronizationHealthGuardEnabled: true,
        synchronizationGuardWindowRuns: 30,
        synchronizationGuardMinimumEvidenceRuns: 10,
        synchronizationGuardCheckEveryCycles: 3,
        synchronizationGuardMaximumFailureRate: 0.01,
        synchronizationGuardMaximumFrameTimeoutRate: 0.01,
        synchronizationGuardMaxConsecutiveFailures: 1,
        synchronizationGuardMaxConsecutiveSkewViolations: 2,
        synchronizationGuardMaximumNativeFrameLossRate: 0.001,
        synchronizationGuardMaximumBufferUnderruns: 0,
        synchronizationGuardMaximumResynchronizations: 0,
        synchronizationGuardMaximumSequenceGapRate: 0.002,
        synchronizationGuardUnhealthyChecksToFault: 1,
        synchronizationGuardHealthyChecksToRecover: 5,
        synchronizationGuardRecoveryTimeoutMs: 30000,
      });
      return;
    }
    if (preset === 'relaxed') {
      changeConfig({
        synchronizationHealthGuardEnabled: true,
        synchronizationGuardWindowRuns: 10,
        synchronizationGuardMinimumEvidenceRuns: 3,
        synchronizationGuardCheckEveryCycles: 10,
        synchronizationGuardMaximumFailureRate: 0.15,
        synchronizationGuardMaximumFrameTimeoutRate: 0.15,
        synchronizationGuardMaxConsecutiveFailures: 4,
        synchronizationGuardMaxConsecutiveSkewViolations: 5,
        synchronizationGuardMaximumNativeFrameLossRate: 0.02,
        synchronizationGuardMaximumBufferUnderruns: 3,
        synchronizationGuardMaximumResynchronizations: 2,
        synchronizationGuardMaximumSequenceGapRate: 0.05,
        synchronizationGuardUnhealthyChecksToFault: 3,
        synchronizationGuardHealthyChecksToRecover: 2,
        synchronizationGuardRecoveryTimeoutMs: 60000,
      });
      return;
    }
    changeConfig({
      synchronizationHealthGuardEnabled: true,
      synchronizationGuardWindowRuns: 20,
      synchronizationGuardMinimumEvidenceRuns: 5,
      synchronizationGuardCheckEveryCycles: 5,
      synchronizationGuardMaximumFailureRate: 0.05,
      synchronizationGuardMaximumFrameTimeoutRate: 0.05,
      synchronizationGuardMaxConsecutiveFailures: 2,
      synchronizationGuardMaxConsecutiveSkewViolations: 3,
      synchronizationGuardMaximumNativeFrameLossRate: 0.005,
      synchronizationGuardMaximumBufferUnderruns: 0,
      synchronizationGuardMaximumResynchronizations: 0,
      synchronizationGuardMaximumSequenceGapRate: 0.01,
      synchronizationGuardUnhealthyChecksToFault: 2,
      synchronizationGuardHealthyChecksToRecover: 3,
      synchronizationGuardRecoveryTimeoutMs: 30000,
    });
  };

  const syncTab = (
    <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
      <Space wrap align="center">
        <Checkbox disabled={configLocked} checked={config.synchronizationHealthGuardEnabled} onChange={e => changeConfig({ synchronizationHealthGuardEnabled: e.target.checked })}>{t('production.sync.enable')}</Checkbox>
        <Typography.Text type="secondary">{t('production.sync.preset')}</Typography.Text>
        <Button size="small" disabled={configLocked} onClick={() => applySyncPreset('strict')}>{t('production.sync.presetStrict')}</Button>
        <Button size="small" disabled={configLocked} onClick={() => applySyncPreset('balanced')}>{t('production.sync.presetBalanced')}</Button>
        <Button size="small" disabled={configLocked} onClick={() => applySyncPreset('relaxed')}>{t('production.sync.presetRelaxed')}</Button>
      </Space>

      <div>
        <Typography.Text strong style={{ display: 'block', marginBottom: 6 }}>{t('production.sync.sectionWindow')}</Typography.Text>
        <Space wrap>
          <label htmlFor="prod-sync-window">{t('production.ptp.window')}</label><Space.Compact><InputNumber id="prod-sync-window" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={5} max={500} value={config.synchronizationGuardWindowRuns} onChange={v => changeConfig({ synchronizationGuardWindowRuns: Number(v ?? 20) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-min-evidence">{t('production.ptp.minEvidence')}</label><Space.Compact><InputNumber id="prod-sync-min-evidence" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={config.synchronizationGuardWindowRuns} value={config.synchronizationGuardMinimumEvidenceRuns} onChange={v => changeConfig({ synchronizationGuardMinimumEvidenceRuns: Number(v ?? 5) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-check-cycles">{t('production.ptp.checkCycles')}</label><Space.Compact><InputNumber id="prod-sync-check-cycles" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={10000} value={config.synchronizationGuardCheckEveryCycles} onChange={v => changeConfig({ synchronizationGuardCheckEveryCycles: Number(v ?? 5) })} /><Space.Addon>{t('production.unit.cycles')}</Space.Addon></Space.Compact>
        </Space>
      </div>

      <div>
        <Typography.Text strong style={{ display: 'block', marginBottom: 6 }}>{t('production.sync.sectionThresholds')}</Typography.Text>
        <Space wrap>
          <label htmlFor="prod-sync-max-fail">{t('production.sync.maxFailureRate')}</label><Space.Compact><InputNumber id="prod-sync-max-fail" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} value={config.synchronizationGuardMaximumFailureRate * 100} onChange={v => changeConfig({ synchronizationGuardMaximumFailureRate: Number(v ?? 5) / 100 })} /><Space.Addon>%</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-max-timeout">{t('production.sync.maxFrameTimeoutRate')}</label><Space.Compact><InputNumber id="prod-sync-max-timeout" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} value={config.synchronizationGuardMaximumFrameTimeoutRate * 100} onChange={v => changeConfig({ synchronizationGuardMaximumFrameTimeoutRate: Number(v ?? 5) / 100 })} /><Space.Addon>%</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-consec-fail">{t('production.sync.maxConsecutiveFailures')}</label><Space.Compact><InputNumber id="prod-sync-consec-fail" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardMaxConsecutiveFailures} onChange={v => changeConfig({ synchronizationGuardMaxConsecutiveFailures: Number(v ?? 2) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-consec-skew">{t('production.sync.maxConsecutiveSkew')}</label><Space.Compact><InputNumber id="prod-sync-consec-skew" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardMaxConsecutiveSkewViolations} onChange={v => changeConfig({ synchronizationGuardMaxConsecutiveSkewViolations: Number(v ?? 3) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-native-loss">{t('production.sync.maxNativeFrameLoss')}</label><Space.Compact><InputNumber id="prod-sync-native-loss" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} step={0.05} value={config.synchronizationGuardMaximumNativeFrameLossRate * 100} onChange={v => changeConfig({ synchronizationGuardMaximumNativeFrameLossRate: Number(v ?? 0.5) / 100 })} /><Space.Addon>%</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-underruns">{t('production.sync.maxBufferUnderruns')}</label><InputNumber id="prod-sync-underruns" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={1000000} value={config.synchronizationGuardMaximumBufferUnderruns} onChange={v => changeConfig({ synchronizationGuardMaximumBufferUnderruns: Number(v ?? 0) })} />
          <label htmlFor="prod-sync-resyncs">{t('production.sync.maxResync')}</label><InputNumber id="prod-sync-resyncs" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={1000000} value={config.synchronizationGuardMaximumResynchronizations} onChange={v => changeConfig({ synchronizationGuardMaximumResynchronizations: Number(v ?? 0) })} />
          <label htmlFor="prod-sync-seq-gap">{t('production.sync.maxSequenceGap')}</label><Space.Compact><InputNumber id="prod-sync-seq-gap" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={0} max={50} step={0.1} value={config.synchronizationGuardMaximumSequenceGapRate * 100} onChange={v => changeConfig({ synchronizationGuardMaximumSequenceGapRate: Number(v ?? 1) / 100 })} /><Space.Addon>%</Space.Addon></Space.Compact>
        </Space>
      </div>

      <div>
        <Typography.Text strong style={{ display: 'block', marginBottom: 6 }}>{t('production.sync.sectionRecovery')}</Typography.Text>
        <Space wrap>
          <label htmlFor="prod-sync-unhealthy-fault">{t('production.sync.unhealthyToFault')}</label><Space.Compact><InputNumber id="prod-sync-unhealthy-fault" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardUnhealthyChecksToFault} onChange={v => changeConfig({ synchronizationGuardUnhealthyChecksToFault: Number(v ?? 2) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-healthy-recover">{t('production.sync.healthyToRecover')}</label><Space.Compact><InputNumber id="prod-sync-healthy-recover" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1} max={100} value={config.synchronizationGuardHealthyChecksToRecover} onChange={v => changeConfig({ synchronizationGuardHealthyChecksToRecover: Number(v ?? 3) })} /><Space.Addon>{t('production.unit.runs')}</Space.Addon></Space.Compact>
          <label htmlFor="prod-sync-recover-timeout">{t('production.sync.recoveryTimeout')}</label><Space.Compact><InputNumber id="prod-sync-recover-timeout" disabled={configLocked || !config.synchronizationHealthGuardEnabled} min={1000} max={600000} step={1000} value={config.synchronizationGuardRecoveryTimeoutMs} onChange={v => changeConfig({ synchronizationGuardRecoveryTimeoutMs: Number(v ?? 30000) })} /><Space.Addon>{t('production.unit.ms')}</Space.Addon></Space.Compact>
        </Space>
      </div>
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
          title={t('production.reconcile.pendingUnavailable')}
          description={t('production.reconcile.pendingUnavailableBody', { error: errors.pending })}
        />
      )}
      {canReconcile && pendingActions && (
        <Alert
          type="error"
          showIcon
          title={t('production.reconcile.pendingTitle')}
          description={
            <Space orientation="vertical" size={4}>
              <span>{t('production.label.reason')}{pendingActions.reason ?? t('production.reconcile.reasonFallback')}{pendingActions.unreadable ? t('production.reconcile.unreadable') : ''}</span>
              {pendingActions.runId && <span>{t('production.reconcile.runLabel')}{pendingActions.runId}</span>}
              {(pendingActions.deviceIds?.length ?? 0) > 0 && <span>{t('production.reconcile.devicesLabel')}{(pendingActions.deviceIds ?? []).join(listSeparator)}</span>}
              {(pendingActions.robotIds?.length ?? 0) > 0 && <span>{t('production.reconcile.robotsLabel')}{(pendingActions.robotIds ?? []).join(listSeparator)}</span>}
              {(pendingActions.manualIntents ?? []).map((intent) => (
                <span key={intent.runId}>
                  {t('production.reconcile.openIntent')}<b>{intent.action}</b>（{intent.state}{intent.closedReason ? ` · ${intent.closedReason}` : ''}）
                  {intent.deviceIds.length > 0 ? t('production.reconcile.deviceSuffix', { ids: intent.deviceIds.join(listSeparator) }) : ''}
                  {intent.robotIds.length > 0 ? t('production.reconcile.robotSuffix', { ids: intent.robotIds.join(listSeparator) }) : ''}
                  {' '}
                  <Button danger size="small" onClick={() => { setResolveTarget({ kind: 'manual', runId: intent.runId, action: intent.action }); setResolveOpen(true); }}>
                    {t('production.reconcile.resolveThis')}
                  </Button>
                </span>
              ))}
              <span>{t('production.reconcile.steps')}</span>
              <Button danger size="small" onClick={() => { setResolveTarget({ kind: 'production', pending: pendingActions }); setResolveOpen(true); }}>{t('production.reconcile.submit')}</Button>
            </Space>
          }
        />
      )}
      <Modal
        open={canReconcile && resolveOpen}
        onCancel={() => { if (!resolveRequest.current) setResolveOpen(false); }}
        closable={!resolveSubmitting} keyboard={!resolveSubmitting} mask={{ closable: !resolveSubmitting }}
        confirmLoading={resolveSubmitting} cancelButtonProps={{ disabled: resolveSubmitting }}
        title={resolveTarget.kind === 'manual' ? t('production.reconcile.manualTitle', { action: resolveTarget.action }) : t('production.reconcile.modalTitle')}
        okText={t('production.reconcile.ok')} cancelText={t('production.action.cancel')}
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
            const result = await call({ url: stationActionUrl.resolve, action: 'resolve', successMessage: t('production.toast.reconcileSubmitted'), body: {
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
        <div>{t('production.reconcile.target')}{resolveTarget.kind === 'manual' ? resolveTarget.runId : resolveTarget.pending.runId ?? t('production.reconcile.targetFallback')}</div>
        <Form form={resolveForm} layout="vertical" disabled={resolveSubmitting}>
          <Form.Item name="evidence" label={t('production.reconcile.evidenceLabel')} rules={[{ required: true, message: t('production.reconcile.evidenceRequired') }]}>
            <Input.TextArea rows={3} placeholder={t('production.reconcile.evidencePlaceholder')} />
          </Form.Item>
          <Form.Item name="identityEvidence" label={t('production.reconcile.identityLabel')} rules={[{ required: true, message: t('production.reconcile.identityRequired') }]}>
            <Input.TextArea rows={2} placeholder={t('production.reconcile.identityPlaceholder')} />
          </Form.Item>
          <Form.Item name="reason" label={t('production.reconcile.reasonInputLabel')}>
            <Input placeholder={t('production.reconcile.reasonPlaceholder')} />
          </Form.Item>
        </Form>
      </Modal>
      {state === 'Faulted' && (
        <Alert
          type="error"
          showIcon
          title={status?.consecutiveFailures
            ? t('production.fault.titleWithFailures', { count: status.consecutiveFailures })
            : t('production.fault.title')}
          description={<>
            <div>{t('production.label.reason')}{status?.lastError ?? t('production.fault.reasonFallback')}</div>
            <div>{t('production.fault.nextStep')}</div>
          </>}
        />
      )}
      {operationError && <Alert type="error" showIcon title={t('production.alert.operationFailed')} description={operationError} />}
      {errors.config && <Alert type="warning" showIcon title={t('production.alert.configUnavailable')} description={errors.config} action={<Button onClick={monitor.refresh}>{t('production.alert.retry')}</Button>} />}
      {errors.jobs && <Alert type="warning" showIcon title={t('production.alert.jobsStale')} description={errors.jobs} />}
      {errors.alarms && <Alert type="warning" showIcon title={t('production.alert.alarmsStale')} description={errors.alarms} />}
      {errors.alarmSummary && <Alert type="warning" showIcon title={t('production.alert.alarmSummaryStale')} description={errors.alarmSummary} />}
      {merge && <ProductionConfigConflict key={JSON.stringify(merge.fields)} merge={merge} disabled={configLocked || busy.size > 0}
        onStale={() => { void messageApi.warning(t('production.merge.stale')); }} />}
      {dirty && !conflict && <Alert type="info" showIcon title={t('production.alert.dirtyTitle')} description={t('production.alert.dirtyBody')} />}
      {status?.ptpGuard?.enabled && !status.ptpGuard.healthy && (
        <Alert type="error" showIcon title={t('production.alert.ptpBlocked')} description={status.ptpGuard.summary ?? t('production.alert.ptpBlockedBody', { count: status.ptpGuard.groups.length })} />
      )}
      {status?.synchronizationGuard?.enabled && !status.synchronizationGuard.healthy && (
        <Alert
          type={status.synchronizationGuard.liveRecoverableOnly ? 'warning' : 'error'}
          showIcon
          title={t('production.alert.syncDegraded')}
          description={status.synchronizationGuard.summary ?? t('production.alert.syncDegradedBody', { count: status.synchronizationGuard.groups.length })}
        />
      )}
      {activeAlarmCount > 0 && (
        <Alert
          type="warning"
          showIcon
          title={criticalAlarmCount
            ? t('production.alert.activeAlarmsWithCritical', { count: activeAlarmCount, critical: criticalAlarmCount })
            : t('production.alert.activeAlarms', { count: activeAlarmCount })}
          description={latestActiveMessage ? t('production.alert.latest', { message: latestActiveMessage }) : undefined}
          action={!kiosk && pendingAlarmCount > 0 && <Button size="small" onClick={() => { setAlarmFilter('pending'); setActiveTab('alarms'); }}>
            {t('production.alert.viewPending', { count: pendingAlarmCount })}
          </Button>}
        />
      )}
    </>
  );

  const operatorTab = open && <div className="operator-workspace-content">
    {pendingAlarmCount > 0 && <Suspense fallback={<span role="status">{t('production.loading.alarmQueue')}</span>}>
      <OperatorAlarmQueue key={stationScoped ? stationId : 'default'} busy={busy} onAcknowledge={acknowledgeAlarm} stationId={stationScoped ? stationId : undefined} />
    </Suspense>}
    <OperatorDashboard key={`${stationId ?? 'default'}-${selectedJobId ?? ''}-${layoutRevision}`} jobId={selectedJobId} status={status} statusFresh={statusFresh}
      alarmSummary={alarmSummary} alarmFresh={!errors.alarmSummary && Boolean(alarmSummary)} kiosk={kiosk} lastUpdatedAt={lastUpdatedAt}
      stationId={stationScoped ? stationId : undefined}
      onEdit={canConfigure && !kiosk && jobs.length > 0 ? () => setLayoutEditorOpen(true) : undefined} />
  </div>;

  // kiosk：没有标签栏，只有操作员工作台——车间大屏上任何可切换的入口都是误触来源。
  // 标签分两组：运行（操作员/监控/告警/工位/收件箱）与配置（生产配置/PTP/同步健康）。
  // 此前 8 个同层标签挤在一起，"PTP 保护"要扫过整排才找得到，且操作与配置语义混在一处。
  const runTabKeys = ['operator', 'monitor', 'parallel', 'alarms', 'stations', ...(canEngineer ? ['deliveries'] : [])];
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
    { key: 'operator', label: t('production.tab.operator'), children: shownTab === 'operator' && operatorTab },
    { key: 'monitor', label: t('production.tab.monitor'), children: monitorTab },
    { key: 'parallel', label: t('production.tab.parallel'), children: open && shownTab === 'parallel' &&
      <Suspense fallback={<span role="status">{t('production.loading.parallel')}</span>}>
        <LocalParallelRuntimes jobs={jobs} canConfigure={canConfigure} canReconcile={canReconcile} />
      </Suspense> },
    { key: 'alarms', label: activeAlarmCount ? t('production.tab.alarmsWithCount', { count: activeAlarmCount }) : t('production.tab.alarms'), children: alarmsTab },
    { key: 'stations', label: t('production.tab.stations'), children: open && shownTab === 'stations' &&
      <Suspense fallback={<span role="status">{t('production.loading.stations')}</span>}>
        <ProductionStations canSend={canConfigure} canControl />
      </Suspense> },
    ...(canEngineer ? [{ key: 'deliveries', label: t('production.tab.deliveries'), children: open && shownTab === 'deliveries' &&
      <Suspense fallback={<span role="status">{t('production.loading.deliveries')}</span>}>
        <RecipeDeliveryInbox onPublished={monitor.refresh} />
      </Suspense> }] : []),
  ];
  const configTabItems = canConfigure ? [
    { key: 'config', label: t('production.tab.config'), children: configTab },
    { key: 'ptp', label: t('production.tab.ptp'), children: ptpTab },
    { key: 'sync', label: t('production.tab.sync'), children: syncTab },
  ] : [];

  const body = kiosk ? (
    <Space orientation="vertical" style={{ width: '100%' }} size="middle">
      {contextHolder}
      {localDetails}
      {operatorTab}
    </Space>
  ) : (
    <Space orientation="vertical" style={{ width: '100%' }} size="middle">
      {contextHolder}
      {shownTab !== 'stations' && shownTab !== 'deliveries' && shownTab !== 'parallel' && localDetails}
      {canConfigure && <Segmented size="small" value={tabGroup}
        onChange={(value) => switchTabGroup(value as 'run' | 'configure')}
        options={[{ label: t('production.group.run'), value: 'run' }, { label: t('production.group.configure'), value: 'configure' }]} />}
      <Tabs
        activeKey={shownTab}
        onChange={setActiveTab}
        items={tabGroup === 'run' ? runTabItems : configTabItems}
      />
      {open && canConfigure && layoutEditorOpen && <Suspense fallback={<span role="status">{t('production.loading.layout')}</span>}>
        <OperatorLayoutDialog jobs={jobs} initialJobId={selectedJobId}
          onClose={() => setLayoutEditorOpen(false)} onSaved={() => setLayoutRevision((revision) => revision + 1)} />
      </Suspense> }
    </Space>
  );

  if (embedded) return <div className={`runtime-only-page${kiosk ? ' is-kiosk' : ''}`}>
    {!kiosk && (
      <header className="runtime-only-header">
        <div className="runtime-only-title">{t('production.title.runtime')}</div>
        {headerActions && <div className="runtime-only-actions">{headerActions}</div>}
      </header>
    )}
    {body}
  </div>;
  return <Modal open={open} onCancel={onClose} footer={null} title={t('production.title.panel')} width="min(1120px, calc(100vw - 32px))">{body}</Modal>;
}
