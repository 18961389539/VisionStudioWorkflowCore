import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Checkbox, Empty, message, Modal, Select, Space, Spin, Table, Tag } from 'antd';
import { apiFetch, apiJson } from '../apiClient';
import { apiErrorMessage, readErrorPayload } from '../apiErrors';
import { jobs, production } from '../endpoints';
import type { ContractDto, JobDescriptor } from '../types';
import { useProductionActions } from '../useProductionActions';
import { useProductionPoll } from '../useProductionPoll';
import { demoRecipeDeliveries, productionDemoEnabled } from '../productionDemoData';

type Station = ContractDto<'ProductionStationOverview'>;
type Batch = ContractDto<'RecipeRolloutBatch'>;
type Receipt = ContractDto<'RecipeDeliveryReceipt'>;
type Finding = ContractDto<'Finding'>;

function useList<T>(endpoint: string) {
  const [data, setData] = useState<T[]>();
  const [error, setError] = useState<string>();
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    const lifecycle = new AbortController();
    const signal = AbortSignal.any([lifecycle.signal, AbortSignal.timeout(8_000)]);
    void apiJson<T[]>(endpoint, { signal, errorLabel: '读取失败' }).then((value) => {
      if (lifecycle.signal.aborted) return;
      if (!Array.isArray(value)) throw new Error('列表响应无效');
      signal.throwIfAborted();
      setData(value);
      setError(undefined);
    }).catch((reason) => {
      if (!lifecycle.signal.aborted) setError(reason instanceof Error ? reason.message : '读取失败');
    });
    return () => lifecycle.abort();
  }, [endpoint, revision]);
  return { data, error, refresh: () => setRevision((value) => value + 1) };
}

async function readRecipeRolloutHistory(signal: AbortSignal): Promise<Batch[]> {
  const history = await apiJson<unknown>(production.recipeRollouts, { signal, errorLabel: '下发记录读取失败' });
  if (!Array.isArray(history) || !history.every((batch) => batch && typeof batch.id === 'string'
    && typeof batch.jobId === 'string' && typeof batch.jobName === 'string' && Number.isInteger(batch.sourceVersion)
    && typeof batch.workflowHash === 'string' && typeof batch.createdAt === 'string' && Number.isFinite(Date.parse(batch.createdAt))
    && Array.isArray(batch.stations) && batch.stations.every((station: Batch['stations'][number]) => station &&
      typeof station.stationId === 'string' && ['Received', 'Rejected', 'Unconfirmed'].includes(station.outcome)))) {
    throw new Error('下发记录响应无效');
  }
  return history;
}

const timeLabel = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false });
const versionLabel = (version?: number | null) => version == null ? '—' : 'V' + version;
const failureDetails: Record<string, string> = {
  timeout: '等待工位响应超时', connection_error: '工位连接失败',
  receipt_mismatch: '回执与下发版本不符', invalid_response: '回执无效',
};
const maxBatchStations = 8;
const publishedJob = (job: JobDescriptor) => job.versions?.find((version) => version.version === job.publishedVersion);

export function RecipeRolloutDialog({ stations, onCompleted, onClose }: {
  stations: Station[];
  onCompleted: () => void;
  onClose: () => void;
}) {
  const [jobId, setJobId] = useState<string>();
  const [stationIds, setStationIds] = useState<string[]>([]);
  const [lastBatch, setLastBatch] = useState<Batch>();
  const [retryBatch, setRetryBatch] = useState<Batch>();
  const recipes = useList<JobDescriptor>(jobs.list);
  const history = useProductionPoll(readRecipeRolloutHistory, true, 10_000);
  const [messages, holder] = message.useMessage();
  const { busy, error, call } = useProductionActions(true, messages, () => { history.refresh(); onCompleted(); });
  const pending = busy.has('send-recipes');
  const eligible = stations.filter((station) => station.recipeDeliveryEnabled);
  const selectedJob = recipes.data?.find((job) => job.id === jobId);
  const version = selectedJob && publishedJob(selectedJob);
  const recent = lastBatch && !history.data?.some((batch) => batch.id === lastBatch.id)
    ? [lastBatch, ...(history.data ?? [])].sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt))
    : history.data ?? [];
  const matchesPublishedRecipe = (batch: Batch) => {
    const job = recipes.data?.find((candidate) => candidate.id === batch.jobId);
    const published = job && publishedJob(job);
    return published?.version === batch.sourceVersion && published.workflowHash === batch.workflowHash;
  };
  const retryAvailable = !retryBatch || matchesPublishedRecipe(retryBatch);
  const selectedCount = stationIds.length;
  const canSend = !recipes.error && !pending && retryAvailable && version && selectedCount > 0 && selectedCount <= maxBatchStations
    && stationIds.every((id) => eligible.some((station) => station.id === id));

  const send = () => {
    if (!canSend || !jobId || !version) return;
    void call({
      url: production.recipeRollouts, action: 'send-recipes', successMessage: '批次结果已记录',
      body: { jobId, sourceVersion: version.version, workflowHash: version.workflowHash, stationIds },
      onSuccess: (payload) => {
        const batch = payload as Batch | undefined;
        if (!batch?.id || !Array.isArray(batch.stations)) throw new Error('批次回执无效');
        setLastBatch(batch);
      },
    });
  };

  return <Modal open title="配方分批下发" footer={null} onCancel={onClose} closable={!pending}
    keyboard={!pending} mask={{ closable: !pending }} width={870}>
    {holder}
    <div className="recipe-rollout-form">
      <p>选择已发布的配方和本批工位。接收后生成本地草稿，由站点工程师发布。每批最多 {maxBatchStations} 个工位。</p>
      <div className="workstation-heading"><label htmlFor="rollout-recipe">来源配方</label>
        <Button size="small" disabled={pending} onClick={recipes.refresh}>刷新来源</Button></div>
      <Select id="rollout-recipe" aria-label="来源配方" placeholder="选择已发布版本" value={jobId} disabled={pending}
        onChange={(id) => { setJobId(id); setRetryBatch(undefined); }} options={recipes.data?.filter((job) => publishedJob(job)).map((job) => ({
          value: job.id, label: job.name + ' · ' + versionLabel(job.publishedVersion),
        }))} />
      {version && <code className="station-secondary" title={version.workflowHash}>流程指纹 {version.workflowHash.slice(0, 12)}</code>}
      <fieldset className="operator-layout-widgets" disabled={pending}>
        <legend>本批工位（{selectedCount} / {maxBatchStations}）</legend>
        {eligible.length === 0
          ? <span>当前没有开通配方接收的工位。</span>
          : <Checkbox.Group<string> className="recipe-rollout-stations" value={stationIds} onChange={setStationIds}
            options={eligible.map((station) => ({ value: station.id,
              label: station.name + (station.connection === 'Online' ? '' : '（未在线）'),
              disabled: selectedCount >= maxBatchStations && !stationIds.includes(station.id),
            }))} />}
      </fieldset>
      {retryBatch && <Alert type={retryAvailable ? 'info' : 'warning'} showIcon
        title={retryAvailable ? '已重选上批待处理工位，请核对后下发' : '来源已发布版本变化，请重新选择配方'}
        action={<Button size="small" disabled={pending} onClick={() => { setRetryBatch(undefined); setJobId(undefined); setStationIds([]); }}>退出重试</Button>} />}
      {recipes.error && <Alert type="error" showIcon title={recipes.error} />}
      {error && <Alert type="error" showIcon title={error} />}
      <Button type="primary" loading={pending} disabled={!canSend} onClick={send}>下发本批</Button>
    </div>
    <div className="workstation-heading"><h3>批次记录</h3><Button size="small" loading={history.refreshing} onClick={history.refresh}>刷新记录</Button></div>
    {history.error && <Alert type="warning" showIcon title={history.error} />}
    {!history.data && !lastBatch ? (!history.error && <Spin />) : <Table<Batch> rowKey="id" size="small" dataSource={recent}
      pagination={{ pageSize: 5, hideOnSinglePage: true }} columns={[
        { title: '来源版本', render: (_, row) => <div title={row.workflowHash}>{row.jobName} · {versionLabel(row.sourceVersion)}</div> },
        { title: '下发时间', render: (_, row) => timeLabel(row.createdAt) },
        { title: '工位回执', render: (_, row) => row.stations.map((station) => {
          const received = station.outcome === 'Received';
          const detail = station.detail ? failureDetails[station.detail] ?? station.detail : undefined;
          return <div key={station.stationId} className="rollout-receipt">
            <span>{station.stationName}</span>
            <Tag color={received ? 'green' : station.outcome === 'Rejected' ? 'red' : 'orange'}>
              {received ? '已接收 · 本地 ' + versionLabel(station.localVersion) : station.outcome === 'Rejected' ? '工位拒绝' : '未确认'}
            </Tag>
            {detail && <span className="station-secondary">{detail}</span>}
          </div>;
        }) },
        { title: '后续操作', render: (_, row) => {
          const retryIds = row.stations.filter((station) => station.outcome !== 'Received' && eligible.some((item) => item.id === station.stationId))
            .map((station) => station.stationId);
          return retryIds.length > 0 && <Button size="small" disabled={pending || Boolean(recipes.error) || Boolean(history.error) || !matchesPublishedRecipe(row)}
            onClick={() => { setRetryBatch(row); setJobId(row.jobId); setStationIds(retryIds); }}>重选待处理工位</Button>;
        } },
      ]} />}
  </Modal>;
}

export function RecipeDeliveryInbox({ onPublished }: { onPublished: () => void }) {
  const deliveries = useList<Receipt>(production.recipeDeliveries);
  const [candidate, setCandidate] = useState<Receipt>();
  const simulation = productionDemoEnabled && deliveries.data?.length === 0;
  const rows = simulation ? demoRecipeDeliveries() : deliveries.data;
  return <section className="station-overview" aria-label="工位配方收件箱">
    <div className="workstation-heading">
      <h2>工位配方收件箱</h2><Button onClick={deliveries.refresh}>刷新收件箱</Button>
    </div>
    {simulation && <Alert type="info" showIcon title="开发仿真收件箱"
      description="以下配方回执用于预览待发布、待验证和已发布状态；仿真记录不会写入工位或触发真实发布。" />}
    {deliveries.error && <Alert type="warning" showIcon title={deliveries.error} />}
    {rows && (rows.length === 0 ? <Empty description="暂无收到的配方" /> :
      <Table<Receipt> dataSource={rows} rowKey="id" size="small" scroll={{ x: 720 }}
        pagination={{ pageSize: 8, hideOnSinglePage: true }} columns={[
          { title: '收到的配方', render: (_, row) => <div title={row.workflowHash}>{row.jobName} · {row.jobId}</div> },
          { title: '来源 / 本地', render: (_, row) => versionLabel(row.sourceVersion) + ' / ' + versionLabel(row.localVersion) },
          { title: '当前发布', render: (_, row) => versionLabel(row.publishedVersion) },
          { title: '接收时间', render: (_, row) => timeLabel(row.receivedAt) },
          { title: '处理', render: (_, row) => row.publishedVersion === row.localVersion ? <Tag color="green">已发布</Tag> :
            simulation ? <Tag color="blue">仿真待发布</Tag>
              : <Button size="small" disabled={Boolean(deliveries.error)} onClick={() => setCandidate(row)}>核对并发布</Button> },
        ]} />)}
    {!deliveries.data && !deliveries.error && <Spin />}
    {candidate && <ReviewRecipeDelivery receipt={candidate} onClose={() => { setCandidate(undefined); deliveries.refresh(); }}
      onPublished={() => { setCandidate(undefined); deliveries.refresh(); onPublished(); }} />}
  </section>;
}

function ReviewRecipeDelivery({ receipt, onClose, onPublished }: {
  receipt: Receipt; onClose: () => void; onPublished: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [findings, setFindings] = useState<Finding[]>();
  const [ack, setAck] = useState(false);
  const request = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => request.current?.abort(), []);

  const publish = async () => {
    if (request.current || (findings && !ack)) return;
    const controller = new AbortController();
    request.current = controller;
    const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(20_000)]);
    setBusy(true);
    setError(undefined);
    try {
      const response = await apiFetch(production.publishRecipeDelivery(receipt.id), {
        method: 'POST', signal, headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedUpdatedAt: receipt.jobUpdatedAt, acknowledgeMaturityNodes: ack }),
      });
      if (response.ok) {
        const published: Partial<JobDescriptor> | null = await response.json();
        signal.throwIfAborted();
        if (published?.id !== receipt.jobId || published.publishedVersion !== receipt.localVersion)
          throw new Error('发布回执与本地版本不符。');
        onPublished();
        return;
      }
      const payload = await readErrorPayload(response) as Partial<ContractDto<'AcknowledgementRequired'>> | null;
      signal.throwIfAborted();
      if (response.status === 409 && payload?.code === 'NODE_MATURITY_ACK_REQUIRED' && Array.isArray(payload.findings)) {
        setFindings(payload.findings);
        setAck(false);
        return;
      }
      setError(apiErrorMessage(payload, '发布未完成（HTTP ' + response.status + '）'));
    } catch (reason) {
      if (!controller.signal.aborted) setError('发布结果未确认，请关闭后核对当前发布版本。' +
        (reason instanceof Error && !signal.aborted ? reason.message : ''));
    } finally {
      if (!controller.signal.aborted) { request.current = undefined; setBusy(false); }
    }
  };

  return <Modal open title="核对工位配方" okText="发布本地版本" cancelText="关闭" onCancel={onClose}
    onOk={() => void publish()} confirmLoading={busy} closable={!busy} keyboard={!busy}
    mask={{ closable: !busy }} cancelButtonProps={{ disabled: busy }} okButtonProps={{ disabled: Boolean(findings && !ack) }}>
    <Space orientation="vertical">
      <strong>{receipt.jobName} · 本地 {versionLabel(receipt.localVersion)}</strong>
      <span>当前发布 {versionLabel(receipt.publishedVersion)} · 来源 {versionLabel(receipt.sourceVersion)}</span>
      <code className="rollout-fingerprint">{receipt.workflowHash}</code>
      <p>发布会更新该配方的本地发布版本。正在运行的生产会话继续使用启动时锁定的版本。</p>
      {receipt.requiresValidation && <Alert type="info" showIcon title="此版本须在站点通过数据集验证。请在设计端关联该本地版本。" />}
      {findings && <Alert type="warning" showIcon title="请核对预览或实验节点"
        description={<><ul>{findings.map((finding) => <li key={finding.nodeId}>{finding.nodeType} · {finding.reason}</li>)}</ul>
          <Checkbox checked={ack} disabled={busy} onChange={(event) => setAck(event.target.checked)}>我已核对这些节点</Checkbox></>} />}
      {error && <Alert type="error" showIcon title={error} />}
    </Space>
  </Modal>;
}
