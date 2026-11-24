import { lazy, Suspense, useState } from 'react';
import { Alert, Button, Checkbox, Empty, Input, Modal, Select, Space, Spin, Table, Tag } from 'antd';
import type { ContractDto } from '../types';
import { apiJson } from '../apiClient';
import { production } from '../endpoints';
import { localizeStatus } from '../i18n';
import { stationRolloutState, type StationRollout, type StationRolloutState } from '../stationRolloutStatus';
import { useProductionPoll } from '../useProductionPoll';
import { demoStationRollouts, demoStations, isDemoStation, productionDemoEnabled } from '../productionDemoData';
import { RecipeRolloutDialog } from './ProductionRecipeRollouts';

const StationSetupWizard = lazy(() => import('./StationSetupWizard'));

type Station = ContractDto<'ProductionStationOverview'>;
type StationControlBatch = ContractDto<'StationProductionControlBatch'>;
type StationControlAction = StationControlBatch['action'];

const connectionLabels: Record<Station['connection'], string> = {
  Waiting: '等待数据', Online: '在线', Offline: '离线', Timeout: '连接超时',
  Unauthorized: '认证失败', InvalidResponse: '响应无效', IdentityMismatch: '工位身份不符',
};
const versionLabels: Record<Station['versionCheck'], string> = {
  NotConfigured: '未设置目标', Unverified: '待核验', Matches: '运行版本一致',
  PublishedOnly: '已发布 · 待启动', Mismatch: '版本不一致',
};
const rolloutLabels: Record<StationRolloutState, { text: string; color?: string }> = {
  Rejected: { text: '下发被拒绝', color: 'red' },
  Unconfirmed: { text: '下发未确认', color: 'orange' },
  Unverified: { text: '已接收 · 待核验', color: 'orange' },
  OtherRecipe: { text: '当前为其他配方', color: 'orange' },
  AwaitingPublish: { text: '已接收 · 待发布', color: 'gold' },
  AwaitingSwitch: { text: '已发布 · 待切换', color: 'processing' },
  Locked: { text: '已锁定 · 尚未运行', color: 'processing' },
  Running: { text: '已切换运行', color: 'green' },
  PublishedChanged: { text: '已锁定 · 发布版本已变更', color: 'orange' },
  HashMismatch: { text: '流程指纹不符', color: 'red' },
};

async function readStations(signal: AbortSignal): Promise<Station[]> {
  const value = await apiJson<unknown>(production.stations, { signal, errorLabel: '工位总览读取失败' });
  if (!Array.isArray(value) || !value.every((item) => item && typeof item.id === 'string'
    && typeof item.name === 'string' && typeof item.operatorUrl === 'string'
    && Object.hasOwn(connectionLabels, item.connection) && Object.hasOwn(versionLabels, item.versionCheck)
    && (item.status === null || item.status?.stationId === item.id))) {
    throw new Error('工位总览响应无效');
  }
  return value.length === 0 && productionDemoEnabled ? demoStations() : value;
}

async function readStationRollouts(signal: AbortSignal): Promise<StationRollout[]> {
  const value = await apiJson<unknown>(production.latestStationRollouts, { signal, errorLabel: '工位下发进度读取失败' });
  if (!Array.isArray(value) || !value.every((item) => item && item.delivery && typeof item.batchId === 'string'
    && typeof item.jobId === 'string' && typeof item.jobName === 'string' && Number.isInteger(item.sourceVersion)
    && typeof item.workflowHash === 'string' && typeof item.createdAt === 'string' && Number.isFinite(Date.parse(item.createdAt))
    && typeof item.delivery.stationId === 'string' && ['Received', 'Rejected', 'Unconfirmed'].includes(item.delivery.outcome))) {
    throw new Error('工位下发进度响应无效');
  }

  return value.length === 0 && productionDemoEnabled ? demoStationRollouts() : value;
}

async function controlStations(action: StationControlAction, stationIds: string[]): Promise<StationControlBatch> {
  const value = await apiJson<StationControlBatch>(production.stationControl, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ stationIds, action }), timeoutMs: 70_000,
    errorLabel: '多工位生产操作失败',
  });
  if (!value || value.action !== action || !Array.isArray(value.stations)
    || !value.stations.every((item) => item && typeof item.stationId === 'string'
      && typeof item.stationName === 'string' && typeof item.succeeded === 'boolean')) {
    throw new Error('多工位生产操作响应无效');
  }
  return value;
}

function operatorLink(value: string) {
  try {
    const url = new URL(value);
    return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? url.href : undefined;
  } catch { return undefined; }
}

function attentionRank(station: Station, rollout: StationRolloutState | undefined) {
  if (station.status?.state === 'Faulted' || Number(station.status?.criticalAlarms ?? 0) > 0 || rollout === 'HashMismatch') return 3;
  if (station.connection !== 'Online' || station.versionCheck === 'Mismatch' || rollout === 'Rejected'
    || rollout === 'Unconfirmed' || Number(station.status?.pendingAlarms ?? 0) > 0) return 2;
  return rollout && rollout !== 'Running' ? 1 : 0;
}

const formatVersion = (version: number | string | null | undefined) => version == null ? '—' : 'V' + version;

export default function ProductionStations({ canSend, canControl }: { canSend: boolean; canControl: boolean }) {
  const { data: stations, error, refresh, refreshing } = useProductionPoll(readStations);
  const rollouts = useProductionPoll(readStationRollouts, canSend, 10_000);
  const [onlyAttention, setOnlyAttention] = useState(false);
  const [search, setSearch] = useState('');
  const [phase, setPhase] = useState<'all' | 'AwaitingPublish' | 'AwaitingSwitch' | 'Running'>('all');
  const [rolloutOpen, setRolloutOpen] = useState(false);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [selectedStationIds, setSelectedStationIds] = useState<string[]>([]);
  const [controlAction, setControlAction] = useState<StationControlAction | null>(null);
  const [pendingControlAction, setPendingControlAction] = useState<StationControlAction | null>(null);
  const [controlBatch, setControlBatch] = useState<StationControlBatch | null>(null);
  const [controlError, setControlError] = useState<string | null>(null);

  if (!stations && !error) return <Spin>正在读取工位总览…</Spin>;

  const recent = new Map((canSend ? rollouts.data ?? [] : []).map((rollout) => [rollout.delivery.stationId, rollout]));
  const query = search.trim().toLocaleLowerCase();
  const rows = (stations ?? []).map((station) => {
    const followup = recent.get(station.id);
    const rollout = followup && stationRolloutState(station, followup, Boolean(error || rollouts.error));
    return { station, followup, rollout, rank: attentionRank(station, rollout) };
  });
  const visible = rows.filter(({ station, followup, rank, rollout }) => (!onlyAttention || error || rank > 0)
    && (phase === 'all' || !canSend || error || rollouts.error || rollout === phase)
    && (!query || [station.name, station.id, station.status?.job?.name, station.status?.jobId,
      station.target?.jobId, followup?.jobName, followup?.jobId].some((value) => value?.toLocaleLowerCase().includes(query))));
  if (!error) visible.sort((left, right) => right.rank - left.rank);
  const onlineCount = rows.filter(({ station }) => station.connection === 'Online').length;
  const attentionCount = rows.filter(({ rank }) => rank > 0).length;
  const simulation = productionDemoEnabled && rows.some(({ station }) => isDemoStation(station));
  const selected = rows.filter(({ station }) => selectedStationIds.includes(station.id));
  const controlIds = selected.filter(({ station }) => station.connection === 'Online'
    && station.productionControlEnabled).map(({ station }) => station.id);
  const startIds = selected.filter(({ station }) => controlIds.includes(station.id)
    && station.status?.state === 'Stopped').map(({ station }) => station.id);
  const stopIds = selected.filter(({ station }) => controlIds.includes(station.id)
    && station.status != null && station.status.lockedVersion != null
    && !['Stopped', 'Stopping'].includes(station.status.state)).map(({ station }) => station.id);
  const selectionTooLarge = selectedStationIds.length > 8;

  const runControl = async (action: StationControlAction, stationIds: string[]) => {
    if (controlAction || stationIds.length === 0 || stationIds.length > 8) return;
    setControlAction(action);
    setControlBatch(null);
    setControlError(null);
    try {
      setControlBatch(await controlStations(action, stationIds));
      refresh();
      rollouts.refresh();
    } catch (failure) {
      setControlError(failure instanceof Error ? failure.message : '多工位生产操作未完成或结果未确认');
    } finally {
      setControlAction(null);
    }
  };

  return <section className="station-overview" aria-label="多工位总览">
    <div className="workstation-heading">
      <div><h2>多工位总览</h2>
        {rows.length > 0 && <p>{error ? '正在显示最后一次读取结果' : onlineCount + ' / ' + rows.length + ' 在线 · ' + attentionCount + ' 个需关注'}</p>}
      </div>
      <Space wrap>
        {rows.length > 0 && <>
          <Input allowClear aria-label="搜索工位或配方" placeholder="搜索工位或配方" value={search}
            onChange={(event) => setSearch(event.target.value)} />
          <Checkbox checked={onlyAttention} disabled={Boolean(error)} onChange={(event) => {
            setOnlyAttention(event.target.checked);
            if (event.target.checked) setPhase('all');
          }}>仅看需关注</Checkbox>
          {canSend && (recent.size > 0 || phase !== 'all') && <Select aria-label="下发进度" className="station-rollout-filter" value={phase}
            disabled={Boolean(error || rollouts.error)} onChange={(value) => {
              setPhase(value);
              if (value !== 'all') setOnlyAttention(false);
            }} options={[
              { value: 'all', label: '所有下发进度' }, { value: 'AwaitingPublish', label: '待发布' },
              { value: 'AwaitingSwitch', label: '待切换' }, { value: 'Running', label: '已切换运行' },
            ]} />}
        </>}
        <Button loading={refreshing} onClick={() => { refresh(); rollouts.refresh(); }}>刷新总览</Button>
        {canControl && <Button type="primary" disabled={startIds.length === 0 || selectionTooLarge || controlAction !== null}
          loading={controlAction === 'Start'} onClick={() => setPendingControlAction('Start')}>启动已选</Button>}
        {canControl && <Button danger disabled={stopIds.length === 0 || selectionTooLarge || controlAction !== null}
          loading={controlAction === 'Stop'} onClick={() => setPendingControlAction('Stop')}>停止已选</Button>}
        {canSend && rows.length > 0 && <Button disabled={simulation} onClick={() => setWizardOpen(true)}>添加工位</Button>}
        {canSend && <Button type="primary" disabled={!stations || Boolean(error) || simulation} onClick={() => setRolloutOpen(true)}>配方分批下发</Button>}
      </Space>
    </div>
    {simulation && <Alert type="info" showIcon title="开发仿真工位"
      description="当前宿主未配置真实工位，以下在线、待切换和超时数据仅用于界面预览；仿真状态下不允许新增工位或下发配方。" />}
    {error && <Alert type="warning" showIcon title="工位状态尚未确认" description={error} />}
    {canSend && rollouts.error && <Alert type="warning" showIcon title="工位下发进度未更新" description={rollouts.error} />}
    {canControl && selectedStationIds.length > 0 && <Alert
      type={selectionTooLarge ? 'error' : 'info'} showIcon
      title={`已选 ${selectedStationIds.length} 个工位`}
      description={selectionTooLarge
        ? '一次最多控制 8 个工位；请取消部分选择。'
        : `可启动 ${startIds.length} 个、可停止 ${stopIds.length} 个。没有控制令牌或当前离线的工位不会提交。`} />}
    {controlError && <Alert type="error" showIcon title="多工位生产操作结果未确认" description={controlError}
      closable onClose={() => setControlError(null)} />}
    {controlBatch && <Alert type={controlBatch.stations.every((result) => result.succeeded) ? 'success' : 'warning'} showIcon
      title={`${controlBatch.action === 'Start' ? '启动' : '停止'}请求已完成：${controlBatch.stations.filter((result) => result.succeeded).length}/${controlBatch.stations.length} 成功`}
      description={<ul style={{ margin: 0, paddingLeft: 18 }}>
        {controlBatch.stations.map((result) => <li key={result.stationId}>
          {result.stationName}（{result.stationId}）：{result.succeeded
            ? `${localizeStatus(result.state) ?? '结果已确认'}${result.jobId ? ` · ${result.jobId}` : ''}`
            : `失败 · ${result.detail ?? '未知错误'}`}
        </li>)}
      </ul>}
      closable onClose={() => setControlBatch(null)} />}
    <Modal open={pendingControlAction !== null}
      title={pendingControlAction === 'Start' ? `启动 ${startIds.length} 个工位` : `停止 ${stopIds.length} 个工位`}
      footer={[
        <Button key="cancel" disabled={controlAction !== null} onClick={() => setPendingControlAction(null)}>取消</Button>,
        <Button key="confirm" type="primary" danger={pendingControlAction === 'Stop'} loading={controlAction !== null}
          onClick={() => {
            const action = pendingControlAction;
            if (!action) return;
            const ids = action === 'Start' ? startIds : stopIds;
            setPendingControlAction(null);
            void runControl(action, ids);
          }}>
          {pendingControlAction === 'Start' ? '并发启动' : '并发停止'}
        </Button>,
      ]} destroyOnHidden
      onCancel={() => { if (!controlAction) setPendingControlAction(null); }}
    >
      {pendingControlAction === 'Start'
        ? '各工位会使用自己的已配置生产作业并发启动。请确认现场设备、夹具与安全条件；离线或未配置控制令牌的工位不会提交。'
        : '各工位会并发停止各自的生产循环。离线或未配置控制令牌的工位不会提交。'}
    </Modal>
    {stations?.length === 0 && !error && <div style={{ display: 'grid', gap: 8, justifyItems: 'center', paddingBottom: 4 }}>
      <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="当前宿主尚未配置工位" />
      <p style={{ margin: 0, maxWidth: 560, textAlign: 'center', color: 'var(--vs-text-4)', fontSize: 12, lineHeight: 1.7 }}>
        工位登记要改总览宿主的站点配置并重启。用向导按后端同一套规则生成配置片段与令牌环境变量，
        避免手写 ID、地址或令牌把宿主卡在启动校验上。
      </p>
      {canSend && <Button type="primary" onClick={() => setWizardOpen(true)}>生成工位配置</Button>}
    </div>}
    {wizardOpen && <Suspense fallback={null}>
      <StationSetupWizard startIndex={rows.length} existingIds={rows.map(({ station }) => station.id)}
        stationCount={rows.length} onClose={() => setWizardOpen(false)} />
    </Suspense>}
    {canSend && rolloutOpen && stations && <RecipeRolloutDialog stations={stations}
      onCompleted={rollouts.refresh} onClose={() => setRolloutOpen(false)} />}
    {rows.length > 0 && <Table<(typeof rows)[number]> rowKey={({ station }) => station.id} size="middle" dataSource={visible} pagination={false}
      rowSelection={canControl ? {
        selectedRowKeys: selectedStationIds,
        onChange: (keys) => setSelectedStationIds(keys.map(String)),
        getCheckboxProps: ({ station }) => ({
          disabled: station.connection !== 'Online' || !station.productionControlEnabled,
          'aria-label': `选择${station.name}`,
        }),
      } : undefined}
      scroll={{ x: 980 }} locale={{ emptyText: query || phase !== 'all' ? '没有匹配的工位' : '没有需要关注的工位' }}
      columns={[
        { title: '工位', width: 160, render: (_, { station }) => <>
          <strong>{station.name}</strong><div className="station-secondary">{station.id}</div>
        </> },
        { title: '连接 / 运行', width: 140, render: (_, { station }) => {
          const live = !error && station.connection === 'Online';
          return <>
            <Tag color={live ? 'green' : 'default'}>{error ? '状态未确认' : connectionLabels[station.connection]}</Tag>
            {station.status && <div className="station-secondary">{live ? '' : '上次 · '}{localizeStatus(station.status.state)}</div>}
          </>;
        } },
        { title: '配方版本', width: 260, render: (_, { station, followup, rollout }) => <>
          <strong>{station.status?.job?.name ?? station.status?.jobId ?? '—'}</strong>
          <div className="station-secondary">发布 {formatVersion(station.status?.job?.publishedVersion)} · 运行 {formatVersion(station.status?.lockedVersion)}</div>
          {station.target && <div className="station-secondary"
            title={station.target.workflowHash ?? undefined}>目标 {station.target.jobId} / {formatVersion(station.target.version)}</div>}
          {(station.target || !followup) && <Tag color={error ? 'default' : station.versionCheck === 'Mismatch' ? 'red' : station.versionCheck === 'Matches' ? 'green' : 'default'}>
            {error && station.target ? '待核验' : versionLabels[station.versionCheck]}
          </Tag>}
          {followup && rollout && <div className="station-rollout-progress">
            <div className="station-secondary" title={followup.workflowHash}>
              最新下发 {followup.jobName} · 来源 {formatVersion(followup.sourceVersion)}
              {followup.delivery.localVersion != null && ' → 本地 ' + formatVersion(followup.delivery.localVersion)}
            </div>
            <Tag color={rolloutLabels[rollout].color}>{rolloutLabels[rollout].text}</Tag>
          </div>}
        </> },
        { title: '生产 / 告警', width: 180, render: (_, { station: { status } }) => status ? <>
          <div>{status.lockedVersion == null && Number(status.cycleCount) > 0 ? '上次会话 · ' : ''}合格 {status.okCount} · 不合格 {status.ngCount}</div>
          <div className="station-secondary">活动 {status.activeAlarms} · 待确认 {status.pendingAlarms}</div>
        </> : '—' },
        { title: '最后在线', width: 160, render: (_, { station }) =>
          station.lastSeenAt ? new Date(station.lastSeenAt).toLocaleString('zh-CN') : '—' },
        { title: '操作台', width: 90, render: (_, { station }) => {
          const href = operatorLink(station.operatorUrl);
          return href ? <a href={href} target="_blank" rel="noopener noreferrer" aria-label={'打开' + station.name + '操作台'}>打开</a> : '—';
        } },
      ]} />}
  </section>;
}
