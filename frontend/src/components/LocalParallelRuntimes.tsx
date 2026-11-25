import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Checkbox, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Table, Tag, message } from 'antd';
import { apiFetch, apiJson } from '../apiClient';
import { production } from '../endpoints';
import { localizeStatus } from '../i18n';
import type { ContractDto } from '../types';
import type { ProductionJobItem } from '../useProductionMonitor';
import { useMonitorSnapshot } from '../useMonitorStream';

type StationRuntimeStatus = ContractDto<'ProductionStationRuntimeStatus'>;
type StationRuntimeConfig = ContractDto<'ProductionRuntimeConfig'>;
type PendingDeviceAction = ContractDto<'ProductionDeviceActionPendingState'>;

const stateColor = (state: StationRuntimeStatus['state']) => {
  if (state === 'Running') return 'green';
  if (state === 'Faulted') return 'red';
  if (state === 'Recovering' || state === 'Starting' || state === 'Stopping') return 'orange';
  return 'default';
};

export default function LocalParallelRuntimes({ jobs, canConfigure, canReconcile = false }: {
  jobs: ProductionJobItem[];
  canConfigure: boolean;
  canReconcile?: boolean;
}) {
  const [messageApi, contextHolder] = message.useMessage();
  const [runtimes, setRuntimes] = useState<StationRuntimeStatus[]>([]);
  const [loading, setLoading] = useState(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [newStationId, setNewStationId] = useState('');
  const [newJobId, setNewJobId] = useState<string | undefined>(undefined);
  const [configStationId, setConfigStationId] = useState<string | null>(null);
  const [configEtag, setConfigEtag] = useState<string | null>(null);
  const [configDraft, setConfigDraft] = useState<StationRuntimeConfig | null>(null);
  const [configSaving, setConfigSaving] = useState(false);
  const [resolveStationId, setResolveStationId] = useState<string | null>(null);
  const [pendingState, setPendingState] = useState<PendingDeviceAction | null>(null);
  const [resolveSubmitting, setResolveSubmitting] = useState(false);
  const [resolveForm] = Form.useForm<{ evidence: string; identityEvidence: string; reason?: string }>();

  const publishedJobs = jobs.filter((job) => job.publishedVersion != null);

  const loadRuntimes = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await apiJson<StationRuntimeStatus[]>(production.runtimes, {
        signal,
        errorLabel: '本机多工位状态读取失败',
      });
      setRuntimes(list);
      setError(null);
    } catch (err) {
      if ((err as { name?: string })?.name === 'AbortError') return;
      setError(err instanceof Error ? err.message : '本机多工位状态读取失败');
    }
  }, []);

  const monitorSnapshot = useMonitorSnapshot(true);

  useEffect(() => {
    if (monitorSnapshot?.runtimes) {
      setRuntimes(monitorSnapshot.runtimes);
      setError(null);
    }
  }, [monitorSnapshot?.runtimes]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    void loadRuntimes(controller.signal).finally(() => setLoading(false));
    const timer = window.setInterval(() => {
      void loadRuntimes(controller.signal);
    }, 10_000);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [loadRuntimes]);

  const runAction = async (key: string, task: () => Promise<unknown>, successText: string) => {
    setBusyKey(key);
    try {
      await task();
      void messageApi.success(successText);
      await loadRuntimes();
    } catch (err) {
      const text = err instanceof Error ? err.message : '操作失败';
      setError(text);
      void messageApi.error(text);
    } finally {
      setBusyKey(null);
    }
  };

  const handleCreateAndStart = async () => {
    const stationId = newStationId.trim();
    if (!stationId) {
      void messageApi.warning('请输入工位 ID（字母、数字、- 或 _）');
      return;
    }
    if (!newJobId) {
      void messageApi.warning('请选择该工位要运行的已发布配方');
      return;
    }
    await runAction(
      `create:${stationId}`,
      () => apiJson(production.runtimeStart(stationId), {
        method: 'POST',
        body: JSON.stringify({ jobId: newJobId }),
        errorLabel: `启动工位 ${stationId} 失败`,
      }),
      `工位 ${stationId} 已启动`
    );
    setNewStationId('');
  };

  const openConfigModal = async (stationId: string) => {
    setBusyKey(`cfg:${stationId}`);
    try {
      const response = await apiFetch(production.runtimeConfig(stationId));
      if (!response.ok) throw new Error(`读取 ${stationId} 配置失败 (${response.status})`);
      const etag = response.headers.get('ETag');
      const data = await response.json() as StationRuntimeConfig;
      setConfigStationId(stationId);
      setConfigEtag(etag);
      setConfigDraft(data);
    } catch (err) {
      void messageApi.error(err instanceof Error ? err.message : '读取工位配置失败');
    } finally {
      setBusyKey(null);
    }
  };

  const saveConfigModal = async () => {
    if (!configStationId || !configDraft || !configEtag) return;
    setConfigSaving(true);
    try {
      await apiJson(production.runtimeConfig(configStationId), {
        method: 'PUT',
        headers: { 'If-Match': configEtag },
        body: JSON.stringify(configDraft),
        errorLabel: `保存 ${configStationId} 配置失败`,
      });
      void messageApi.success(`工位 ${configStationId} 配置已保存`);
      setConfigStationId(null);
      setConfigDraft(null);
      await loadRuntimes();
    } catch (err) {
      void messageApi.error(err instanceof Error ? err.message : '保存工位配置失败');
    } finally {
      setConfigSaving(false);
    }
  };

  const openResolveModal = async (stationId: string) => {
    setBusyKey(`pending:${stationId}`);
    try {
      const response = await apiFetch(production.runtimePendingActions(stationId));
      if (response.status === 204) {
        void messageApi.info(`工位 ${stationId} 当前没有待核对的设备动作`);
        return;
      }
      if (!response.ok) throw new Error(`读取 ${stationId} 待核对动作失败 (${response.status})`);
      const pending = await response.json() as PendingDeviceAction;
      setResolveStationId(stationId);
      setPendingState(pending);
      resolveForm.resetFields();
    } catch (err) {
      void messageApi.error(err instanceof Error ? err.message : '读取待核对动作失败');
    } finally {
      setBusyKey(null);
    }
  };

  const submitResolve = async () => {
    if (!resolveStationId || !pendingState) return;
    setResolveSubmitting(true);
    try {
      const values = await resolveForm.validateFields();
      await apiJson(production.runtimeResolveAction(resolveStationId), {
        method: 'POST',
        body: JSON.stringify({
          runId: pendingState.runId ?? '',
          manifestHash: pendingState.manifestHash ?? '',
          deviceIds: pendingState.deviceIds ?? [],
          robotIds: pendingState.robotIds ?? [],
          cameraIds: pendingState.cameraIds ?? [],
          reason: values.reason ?? 'manual reconciliation',
          evidence: values.evidence,
          deviceIdentityEvidence: values.identityEvidence,
        }),
        errorLabel: `提交 ${resolveStationId} 核对证据失败`,
      });
      void messageApi.success(`工位 ${resolveStationId} 设备动作已核对解除`);
      setResolveStationId(null);
      setPendingState(null);
      await loadRuntimes();
    } catch (err) {
      if (err instanceof Error) void messageApi.error(err.message);
    } finally {
      setResolveSubmitting(false);
    }
  };

  return (
    <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
      {contextHolder}
      <Alert
        type="info"
        showIcon
        title="本机多工位并行生产"
        description="单个宿主进程内可同时运行多个独立工位与不同工作流。共享同一相机、PLC 或机器人的工位会自动按硬件租约互斥（409 冲突提示）；互不重叠硬件的工位独立循环、独立计数且互不阻塞故障。"
      />
      {error && <Alert type="error" showIcon title="多工位操作提示" description={error} />}
      <Space wrap>
        <Button
          type="primary"
          loading={busyKey === 'start-all'}
          disabled={busyKey !== null}
          onClick={() => void runAction('start-all', () => apiJson(production.runtimesStartAll, { method: 'POST', errorLabel: '全部启动失败' }), '已触发全部已配置工位启动')}
        >
          全部启动
        </Button>
        <Button
          danger
          loading={busyKey === 'stop-all'}
          disabled={busyKey !== null}
          onClick={() => void runAction('stop-all', () => apiJson(production.runtimesStopAll, { method: 'POST', errorLabel: '全部停止失败' }), '已停止全部工位')}
        >
          全部停止
        </Button>
        <Button disabled={busyKey !== null} onClick={() => void loadRuntimes()}>刷新</Button>
      </Space>

      {canConfigure && (
        <Space wrap>
          <Input
            style={{ width: 180 }}
            placeholder="新工位 ID，如 line-2"
            value={newStationId}
            disabled={busyKey !== null}
            onChange={(event) => setNewStationId(event.target.value)}
          />
          <Select
            style={{ width: 260 }}
            placeholder="选择已发布配方"
            value={newJobId}
            disabled={busyKey !== null}
            options={publishedJobs.map((job) => ({ value: job.id, label: `${job.name} · V${job.publishedVersion}` }))}
            onChange={(value) => setNewJobId(value)}
          />
          <Button
            type="primary"
            ghost
            loading={busyKey?.startsWith('create:') ?? false}
            disabled={busyKey !== null}
            onClick={() => void handleCreateAndStart()}
          >
            添加并启动工位
          </Button>
        </Space>
      )}

      <Table<StationRuntimeStatus>
        rowKey="stationId"
        size="small"
        loading={loading && runtimes.length === 0}
        pagination={false}
        dataSource={runtimes}
        columns={[
          {
            title: '工位 ID',
            dataIndex: 'stationId',
            render: (value: string) => value === 'default' ? <Space><strong>default</strong><Tag>默认</Tag></Space> : <strong>{value}</strong>,
          },
          {
            title: '状态',
            dataIndex: 'state',
            render: (value: StationRuntimeStatus['state']) => <Tag color={stateColor(value)}>{localizeStatus(value)}</Tag>,
          },
          {
            title: '配方 / 版本',
            render: (_, row) => row.jobId ? `${row.jobId}${row.lockedJobVersion != null ? ` · V${row.lockedJobVersion}` : ''}` : '—',
          },
          {
            title: '周期 / OK / NG / 异常',
            render: (_, row) => `${row.cycleCount} / ${row.okCount} / ${row.ngCount} / ${row.errorCount}`,
          },
          {
            title: '最近耗时',
            dataIndex: 'lastDurationMs',
            render: (value: number) => value > 0 ? `${value.toFixed(1)} ms` : '—',
          },
          {
            title: '最近错误',
            dataIndex: 'lastError',
            ellipsis: true,
            render: (value?: string | null) => value || '—',
          },
          {
            title: '操作',
            render: (_, row) => {
              const isStopped = row.state === 'Stopped';
              const isFaulted = row.state === 'Faulted';
              return (
                <Space size="small" wrap>
                  {row.state === 'Running' && (
                    <Button
                      size="small"
                      disabled={busyKey !== null}
                      loading={busyKey === `trigger:${row.stationId}`}
                      onClick={() => void runAction(
                        `trigger:${row.stationId}`,
                        () => apiJson(production.runtimeTrigger(row.stationId), { method: 'POST', errorLabel: `触发 ${row.stationId} 失败` }),
                        `已向工位 ${row.stationId} 发送触发信号`,
                      )}
                    >
                      触发一次
                    </Button>
                  )}
                  <Button
                    size="small"
                    href={`/?view=operator&stationId=${encodeURIComponent(row.stationId)}`}
                    target="_blank"
                    rel="noreferrer"
                  >
                    大屏
                  </Button>
                  {isStopped && (
                    <Button
                      size="small"
                      type="primary"
                      disabled={busyKey !== null || !row.jobId}
                      loading={busyKey === `start:${row.stationId}`}
                      onClick={() => void runAction(
                        `start:${row.stationId}`,
                        () => apiJson(production.runtimeStart(row.stationId), {
                          method: 'POST',
                          body: JSON.stringify({ jobId: row.jobId }),
                          errorLabel: `启动 ${row.stationId} 失败`,
                        }),
                        `工位 ${row.stationId} 已启动`,
                      )}
                    >
                      启动
                    </Button>
                  )}
                  {!isStopped && (
                    <Button
                      size="small"
                      danger
                      disabled={busyKey !== null}
                      loading={busyKey === `stop:${row.stationId}`}
                      onClick={() => void runAction(
                        `stop:${row.stationId}`,
                        () => apiJson(production.runtimeStop(row.stationId), { method: 'POST', errorLabel: `停止 ${row.stationId} 失败` }),
                        `工位 ${row.stationId} 已停止`,
                      )}
                    >
                      停止
                    </Button>
                  )}
                  {isFaulted && (
                    <Button
                      size="small"
                      disabled={busyKey !== null}
                      loading={busyKey === `recover:${row.stationId}`}
                      onClick={() => void runAction(
                        `recover:${row.stationId}`,
                        () => apiJson(production.runtimeRecover(row.stationId), { method: 'POST', errorLabel: `恢复 ${row.stationId} 失败` }),
                        `工位 ${row.stationId} 已恢复`,
                      )}
                    >
                      恢复
                    </Button>
                  )}
                  {canReconcile && isStopped && (
                    <Button
                      size="small"
                      disabled={busyKey !== null}
                      loading={busyKey === `pending:${row.stationId}`}
                      onClick={() => void openResolveModal(row.stationId)}
                    >
                      核对动作
                    </Button>
                  )}
                  {canConfigure && isStopped && (
                    <Button
                      size="small"
                      disabled={busyKey !== null}
                      loading={busyKey === `cfg:${row.stationId}`}
                      onClick={() => void openConfigModal(row.stationId)}
                    >
                      配置
                    </Button>
                  )}
                  {canConfigure && row.stationId !== 'default' && isStopped && (
                    <Popconfirm
                      title={`确认移除工位 ${row.stationId}？`}
                      onConfirm={() => void runAction(
                        `remove:${row.stationId}`,
                        () => apiJson(production.runtimeRemove(row.stationId), { method: 'DELETE', errorLabel: `移除 ${row.stationId} 失败` }),
                        `工位 ${row.stationId} 已移除`,
                      )}
                    >
                      <Button size="small" disabled={busyKey !== null}>移除</Button>
                    </Popconfirm>
                  )}
                </Space>
              );
            },
          },
        ]}
      />
      <Modal
        open={configStationId !== null}
        title={`工位配置 · ${configStationId ?? ''}`}
        okText="保存"
        cancelText="取消"
        confirmLoading={configSaving}
        onCancel={() => { if (!configSaving) { setConfigStationId(null); setConfigDraft(null); } }}
        onOk={() => void saveConfigModal()}
      >
        {configDraft && (
          <Space orientation="vertical" size="middle" style={{ width: '100%' }}>
            <Space wrap>
              <span>生产配方</span>
              <Select
                style={{ width: 280 }}
                value={configDraft.jobId ?? undefined}
                placeholder="选择已发布配方"
                options={publishedJobs.map((job) => ({ value: job.id, label: `${job.name} · V${job.publishedVersion}` }))}
                onChange={(jobId) => setConfigDraft({ ...configDraft, jobId })}
              />
            </Space>
            <Space wrap>
              <Checkbox checked={configDraft.autoStart} onChange={(e) => setConfigDraft({ ...configDraft, autoStart: e.target.checked })}>
                自动启动
              </Checkbox>
              <Checkbox checked={configDraft.autoRecover} onChange={(e) => setConfigDraft({ ...configDraft, autoRecover: e.target.checked })}>
                自动恢复
              </Checkbox>
            </Space>
            <Space wrap>
              <span>周期延迟</span>
              <Space.Compact>
                <InputNumber min={0} max={60000} value={configDraft.cycleDelayMs}
                  onChange={(v) => setConfigDraft({ ...configDraft, cycleDelayMs: Number(v ?? 0) })} />
                <Space.Addon>ms</Space.Addon>
              </Space.Compact>
              <span>看门狗超时</span>
              <Space.Compact>
                <InputNumber min={100} max={600000} value={configDraft.maxCycleMs}
                  onChange={(v) => setConfigDraft({ ...configDraft, maxCycleMs: Number(v ?? 15000) })} />
                <Space.Addon>ms</Space.Addon>
              </Space.Compact>
              <span>连续失败上限</span>
              <InputNumber min={1} max={1000} value={configDraft.maxConsecutiveFailures}
                onChange={(v) => setConfigDraft({ ...configDraft, maxConsecutiveFailures: Number(v ?? 3) })} />
            </Space>
            <Space wrap>
              <span>触发模式</span>
              <Select
                style={{ width: 160 }}
                value={configDraft.triggerMode ?? 'Continuous'}
                options={[
                  { value: 'Continuous', label: '连续循环' },
                  { value: 'ExternalSignal', label: '外部信号触发' },
                ]}
                onChange={(triggerMode) => setConfigDraft({ ...configDraft, triggerMode })}
              />
              <span>触发等待超时</span>
              <Space.Compact>
                <InputNumber
                  min={100}
                  max={300000}
                  disabled={(configDraft.triggerMode ?? 'Continuous') !== 'ExternalSignal'}
                  value={configDraft.triggerTimeoutMs ?? 30000}
                  onChange={(v) => setConfigDraft({ ...configDraft, triggerTimeoutMs: Number(v ?? 30000) })}
                />
                <Space.Addon>ms</Space.Addon>
              </Space.Compact>
            </Space>
          </Space>
        )}
      </Modal>
      <Modal
        open={canReconcile && resolveStationId !== null}
        title={`核对未确认设备动作 · ${resolveStationId ?? ''}`}
        okText="提交核对并解除"
        cancelText="取消"
        confirmLoading={resolveSubmitting}
        onCancel={() => { if (!resolveSubmitting) { setResolveStationId(null); setPendingState(null); } }}
        onOk={() => void submitResolve()}
      >
        {pendingState && (
          <Space orientation="vertical" size="small" style={{ width: '100%' }}>
            <div>运行 ID：{pendingState.runId ?? '未知'}</div>
            <div>清单哈希：{pendingState.manifestHash ?? '未知'}</div>
            <Form form={resolveForm} layout="vertical" disabled={resolveSubmitting}>
              <Form.Item name="evidence" label="现场核对证据（必填）" rules={[{ required: true, message: '请填写现场核对证据' }]}>
                <Input.TextArea rows={3} placeholder="例：PLC 触发位已复位，机器人已回安全位" />
              </Form.Item>
              <Form.Item name="identityEvidence" label="设备身份证据（必填）" rules={[{ required: true, message: '请填写设备身份证据' }]}>
                <Input.TextArea rows={2} placeholder="例：设备序列号与在线诊断端点一致" />
              </Form.Item>
              <Form.Item name="reason" label="核对说明（可选）">
                <Input placeholder="例：现场确认动作已完成或已撤销" />
              </Form.Item>
            </Form>
          </Space>
        )}
      </Modal>
    </Space>
  );
}
