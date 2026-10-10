import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Form, InputNumber, message, Modal, Select, Space, Statistic, Switch, Table, Tag, Typography } from 'antd';
import type { RobotCommandTraceRecord, RobotDescriptor, RobotRuntimeSettings } from '../types';
import { localizeStatus } from '../i18n';
import { apiErrorMessage, responseErrorMessage } from '../apiErrors';

// R04：轮询超时——请求挂起时必须暴露失败，而不是让旧数据静默伪装成最新。
const ROBOT_POLL_TIMEOUT_MS = 5000;

type Props = { open: boolean; onClose: () => void };

type TargetForm = {
  x: number;
  y: number;
  rDeg: number;
  action: string;
  waitForInPosition: boolean;
  maxRetries: number;
  retryDelayMs: number;
  autoAck: boolean;
  timeoutMs: number;
};

const connectionColor: Record<string, string> = {
  Connected: 'green', Connecting: 'blue', Disconnected: 'default', Faulted: 'red'
};
const handshakeColor: Record<string, string> = {
  Ready: 'green', TargetAccepted: 'gold', Executing: 'blue', InPosition: 'cyan', Stopped: 'orange', Faulted: 'red', Disconnected: 'default'
};
const signalColor = (value: boolean, danger = false) => value ? (danger ? 'red' : 'green') : 'default';

export default function RobotPanel({ open, onClose }: Props) {
  const [robots, setRobots] = useState<RobotDescriptor[]>([]);
  const [traces, setTraces] = useState<RobotCommandTraceRecord[]>([]);
  const [selectedId, setSelectedId] = useState('virtual-abb-1');
  const [busy, setBusy] = useState('');
  const [settingsForm] = Form.useForm<RobotRuntimeSettings>();
  const [targetForm] = Form.useForm<TargetForm>();
  const [messageApi, contextHolder] = message.useMessage();

  const selected = useMemo(() => robots.find((x) => x.id === selectedId) ?? robots[0], [robots, selectedId]);

  // R04：轮询代次 + 取消 + 超时——旧响应绝不覆盖新结果，挂起请求可被显式中断。
  const refreshGeneration = useRef(0);
  const refreshAbort = useRef<AbortController | null>(null);

  const refresh = async (quiet = false) => {
    const generation = ++refreshGeneration.current;
    refreshAbort.current?.abort();
    const controller = new AbortController();
    refreshAbort.current = controller;
    const timeout = window.setTimeout(() => controller.abort(), ROBOT_POLL_TIMEOUT_MS);
    try {
      const [robotResponse, traceResponse] = await Promise.all([
        fetch('/api/robots', { signal: controller.signal }),
        fetch(`/api/robot-traces?take=20${selectedId ? `&robotId=${encodeURIComponent(selectedId)}` : ''}`, { signal: controller.signal })
      ]);
      if (generation !== refreshGeneration.current) return; // 旧响应：丢弃
      if (!robotResponse.ok) throw new Error(await responseErrorMessage(robotResponse, '机器人接口不可用'));
      const data: RobotDescriptor[] = await robotResponse.json();
      setRobots(data);
      if (traceResponse.ok) setTraces(await traceResponse.json());
      if (!data.some((x) => x.id === selectedId) && data[0]) setSelectedId(data[0].id);
    } catch (error) {
      if (generation !== refreshGeneration.current) return;
      if (!quiet) messageApi.error(error instanceof Error ? error.message : 'Robot refresh failed');
    } finally {
      window.clearTimeout(timeout);
      if (refreshAbort.current === controller) refreshAbort.current = null;
    }
  };

  useEffect(() => {
    if (!open) return;
    refresh();
    const timer = window.setInterval(() => refresh(true), 300);
    return () => window.clearInterval(timer);
  }, [open, selectedId]);

  useEffect(() => {
    if (!selected) return;
    settingsForm.setFieldsValue(selected.settings);
    targetForm.setFieldsValue({
      x: selected.activeTarget?.x ?? selected.currentPose.x,
      y: selected.activeTarget?.y ?? selected.currentPose.y,
      rDeg: selected.activeTarget?.rDeg ?? selected.currentPose.thetaDeg,
      action: 'Handshake',
      waitForInPosition: true,
      maxRetries: 1,
      retryDelayMs: 100,
      autoAck: true,
      timeoutMs: 5000
    });
  }, [selected?.id]);

  const command = async (id: string, action: 'connect' | 'disconnect' | 'stop' | 'reset') => {
    setBusy(action);
    try {
      const response = await fetch(`/api/robots/${encodeURIComponent(id)}/${action}`, { method: 'POST' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(apiErrorMessage(data, `${localizeStatus(action)}失败`));
      await refresh(true);
      messageApi.success(`${localizeStatus(action)}成功`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : `${localizeStatus(action)}失败`);
    } finally { setBusy(''); }
  };

  const acknowledge = async () => {
    if (!selected?.lastCommandId) return;
    setBusy('ack');
    try {
      const response = await fetch(`/api/robots/${encodeURIComponent(selected.id)}/ack/${selected.lastCommandId}`, { method: 'POST' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(apiErrorMessage(data, '确认失败'));
      await refresh(true);
      messageApi.success('握手已确认');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '确认失败');
    } finally { setBusy(''); }
  };

  const saveSettings = async (values: RobotRuntimeSettings) => {
    if (!selected) return;
    setBusy('settings');
    try {
      const response = await fetch(`/api/robots/${encodeURIComponent(selected.id)}/settings`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values)
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(apiErrorMessage(data, '应用设置失败'));
      await refresh(true);
      messageApi.success('机器人设置已应用');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '应用设置失败');
    } finally { setBusy(''); }
  };

  const sendTarget = async (values: TargetForm) => {
    if (!selected) return;
    setBusy('target');
    try {
      const response = await fetch(`/api/robots/${encodeURIComponent(selected.id)}/target`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          ...values,
          frame: selected.baseFrame,
          unit: selected.unit,
          robot: selected.vendor === 'PLC Bridge' ? 'ABB' : selected.vendor,
          guidanceMode: 'Manual'
        })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(apiErrorMessage(data, '机器人目标指令失败'));
      await refresh(true);
      messageApi.success(values.action === 'Handshake' ? '工业握手已完成' : '机器人指令已接受');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '机器人目标指令失败');
    } finally { setBusy(''); }
  };

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={1280} title="机器人运行时 · 适配器 SDK / 工业握手" destroyOnHidden>
      {contextHolder}
      <div className="robot-runtime-grid">
        <section>
          <Space style={{ marginBottom: 10 }} wrap>
            <Button onClick={() => refresh()}>刷新</Button>
            <Tag color="red">IRobot2DAdapter</Tag>
            <Tag color="blue">RobotManager</Tag>
            <Tag color="purple">6 位握手信号</Tag>
            <Tag color="cyan">TCP JSON :40501</Tag>
          </Space>
          <Table<RobotDescriptor>
            size="small"
            pagination={false}
            rowKey="id"
            dataSource={robots}
            rowClassName={(row) => row.id === selected?.id ? 'robot-selected-row' : ''}
            onRow={(row) => ({ onClick: () => setSelectedId(row.id) })}
            columns={[
              { title: 'ID', dataIndex: 'id', width: 125 },
              { title: '驱动', dataIndex: 'driver', width: 110 },
              { title: '连接状态', width: 95, render: (_: unknown, row) => <Tag color={connectionColor[row.connectionState]}>{localizeStatus(row.connectionState)}</Tag> },
              { title: '状态', width: 105, render: (_: unknown, row) => <Tag color={handshakeColor[row.handshakeState]}>{localizeStatus(row.handshakeState)}</Tag> },
              { title: '指令编号', dataIndex: 'lastCommandId', width: 55 },
              { title: '位姿', render: (_: unknown, row) => `${row.currentPose.x.toFixed(2)}, ${row.currentPose.y.toFixed(2)}, ${row.currentPose.thetaDeg.toFixed(2)}°` }
            ]}
          />

          {selected && <>
            <div className="robot-command-row">
              <Button loading={busy === 'connect'} onClick={() => command(selected.id, 'connect')}>连接</Button>
              <Button loading={busy === 'disconnect'} onClick={() => command(selected.id, 'disconnect')}>断开</Button>
              <Button danger loading={busy === 'stop'} onClick={() => command(selected.id, 'stop')}>停止</Button>
              <Button loading={busy === 'reset'} onClick={() => command(selected.id, 'reset')}>复位故障</Button>
              <Button loading={busy === 'ack'} disabled={!selected.handshake.complete && !selected.handshake.error} onClick={acknowledge}>确认</Button>
            </div>

            <div className="robot-stats-grid">
              <Statistic title="X" value={selected.currentPose.x} precision={3} suffix={selected.unit} />
              <Statistic title="Y" value={selected.currentPose.y} precision={3} suffix={selected.unit} />
              <Statistic title="R" value={selected.currentPose.thetaDeg} precision={3} suffix="°" />
              <Statistic title="指令编号" value={selected.lastCommandId} />
            </div>

            <Typography.Title level={5}>工业握手信号</Typography.Title>
            <Space wrap style={{ marginBottom: 12 }}>
              <Tag color={signalColor(selected.handshake.targetReady)}>目标就绪 {selected.handshake.targetReady ? '1' : '0'}</Tag>
              <Tag color={signalColor(selected.handshake.execute)}>执行 {selected.handshake.execute ? '1' : '0'}</Tag>
              <Tag color={signalColor(selected.handshake.busy)}>忙碌 {selected.handshake.busy ? '1' : '0'}</Tag>
              <Tag color={signalColor(selected.handshake.complete)}>完成 {selected.handshake.complete ? '1' : '0'}</Tag>
              <Tag color={signalColor(selected.handshake.error, true)}>错误 {selected.handshake.error ? '1' : '0'}</Tag>
              <Tag color={signalColor(selected.handshake.ack)}>确认 {selected.handshake.ack ? '1' : '0'}</Tag>
            </Space>

            <Typography.Title level={5}>运行时设置</Typography.Title>
            <Form form={settingsForm} layout="vertical" onFinish={saveSettings}>
              <div className="robot-settings-grid">
                <Form.Item label="直线速度（毫米/秒）" name="linearSpeedMmPerSec"><InputNumber min={1} max={selected.capabilities.maxLinearSpeedMmPerSec} style={{ width: '100%' }} /></Form.Item>
                <Form.Item label="角速度（度/秒）" name="angularSpeedDegPerSec"><InputNumber min={1} max={selected.capabilities.maxAngularSpeedDegPerSec} style={{ width: '100%' }} /></Form.Item>
                <Form.Item label="位置容差（毫米）" name="positionToleranceMm"><InputNumber min={0.001} max={10} step={0.01} style={{ width: '100%' }} /></Form.Item>
                <Form.Item label="角度容差（度）" name="angleToleranceDeg"><InputNumber min={0.001} max={10} step={0.01} style={{ width: '100%' }} /></Form.Item>
              </div>
              <Button htmlType="submit" loading={busy === 'settings'}>应用设置</Button>
            </Form>
          </>}
        </section>

        <section className="robot-command-panel">
          <Typography.Title level={5}>手动目标 / 握手测试</Typography.Title>
          {selected ? <>
            <Alert
              type="warning"
              showIcon
              message="仅用于模拟和协议验证"
              description="ABB、PLC 和 TCP 模拟器用于验证软件握手，不提供控制器安全、RAPID 运动规划、轴限位或碰撞检测。"
            />
            <Form form={targetForm} layout="vertical" onFinish={sendTarget} style={{ marginTop: 12 }}>
              <div className="robot-target-grid">
                <Form.Item label={`X (${selected.unit})`} name="x"><InputNumber style={{ width: '100%' }} /></Form.Item>
                <Form.Item label={`Y (${selected.unit})`} name="y"><InputNumber style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="R（度）" name="rDeg"><InputNumber style={{ width: '100%' }} /></Form.Item>
              </div>
              <Form.Item label="操作" name="action"><Select options={[{ value: 'Handshake', label: '工业握手' }, { value: 'Move', label: '移动' }, { value: 'SendTarget', label: '仅发送目标' }]} /></Form.Item>
              <div className="robot-settings-grid">
                <Form.Item label="等待完成" name="waitForInPosition" valuePropName="checked"><Switch /></Form.Item>
                <Form.Item label="自动确认" name="autoAck" valuePropName="checked"><Switch /></Form.Item>
                <Form.Item label="最大重试次数" name="maxRetries"><InputNumber min={0} max={10} style={{ width: '100%' }} /></Form.Item>
                <Form.Item label="重试间隔（毫秒）" name="retryDelayMs"><InputNumber min={0} max={10000} style={{ width: '100%' }} /></Form.Item>
                <Form.Item label="超时（毫秒）" name="timeoutMs"><InputNumber min={50} max={120000} style={{ width: '100%' }} /></Form.Item>
              </div>
              <Button type="primary" htmlType="submit" loading={busy === 'target'}>执行测试目标</Button>
            </Form>

            <Typography.Title level={5} style={{ marginTop: 18 }}>机器人指令追踪</Typography.Title>
            <Table<RobotCommandTraceRecord>
              size="small"
              rowKey="traceId"
              dataSource={traces}
              pagination={{ pageSize: 5, size: 'small' }}
              expandable={{
                expandedRowRender: (trace) => <Table
                  size="small"
                  pagination={false}
                  rowKey={(event) => `${event.timestamp}-${event.stage}-${event.attempt}`}
                  dataSource={trace.events}
                  columns={[
                    { title: '时间', width: 92, render: (_: unknown, e) => new Date(e.timestamp).toLocaleTimeString('zh-CN') },
                    { title: '尝试', dataIndex: 'attempt', width: 42 },
                    { title: '阶段', dataIndex: 'stage', width: 95, render: (v: string) => localizeStatus(v) },
                    { title: '消息', dataIndex: 'message' }
                  ]}
                />
              }}
              columns={[
                { title: '追踪 ID', width: 94, render: (_: unknown, row) => <code>{row.traceId.slice(0, 8)}</code> },
                { title: '指令编号', dataIndex: 'commandId', width: 48 },
                { title: '尝试次数', dataIndex: 'maxAttempt', width: 42 },
                { title: '阶段', dataIndex: 'lastStage', width: 92, render: (v: string) => localizeStatus(v) },
                { title: '状态', width: 85, render: (_: unknown, row) => <Tag color={row.status === 'Complete' ? 'green' : row.status === 'Error' ? 'red' : 'blue'}>{localizeStatus(row.status)}</Tag> }
              ]}
            />
            {selected.error && <Alert type="error" showIcon message={selected.error} />}
          </> : <div className="job-empty">尚未注册机器人</div>}
        </section>
      </div>
    </Modal>
  );
}
