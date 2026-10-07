import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Form, Input, InputNumber, message, Modal, Popconfirm, Select, Space, Statistic, Table, Tag, Typography } from 'antd';
import type { DeviceDescriptor, DeviceRuntimeSettings, DeviceTagDefinition } from '../types';
import { localizeStatus } from '../i18n';

type Props = { open: boolean; onClose: () => void };
type WriteForm = { tagId: string; value: string };
type RegisterForm = {
  kind: 'modbus' | 's7'; id: string; name: string; host: string; port: number; timeoutMs: number;
  unitId?: number; registerOrder?: 'ABCD' | 'CDAB' | 'BADC' | 'DCBA'; cpuType?: string; rack?: number; slot?: number; tagsJson: string;
};

const connectionColor: Record<string, string> = {
  Connected: 'green', Connecting: 'blue', Reconnecting: 'gold', Disconnected: 'default', Faulted: 'red'
};
const qualityColor: Record<string, string> = { Good: 'green', Uncertain: 'gold', Bad: 'red', Disconnected: 'default' };

const modbusTags: DeviceTagDefinition[] = [
  { id: 'ready', name: 'PLC Ready', address: 'C:0', dataType: 'Boolean', writable: false },
  { id: 'trigger', name: 'Vision Trigger', address: 'C:1', dataType: 'Boolean', writable: false },
  { id: 'resultReady', name: 'Result Ready', address: 'C:2', dataType: 'Boolean', writable: true },
  { id: 'resultOk', name: 'Result OK', address: 'C:3', dataType: 'Boolean', writable: true },
  { id: 'hostHeartbeat', name: 'Host Heartbeat', address: 'C:4', dataType: 'Boolean', writable: true },
  { id: 'deviceHeartbeat', name: 'PLC Heartbeat', address: 'C:5', dataType: 'Boolean', writable: false },
  { id: 'resultX', name: 'Result X', address: 'HR:0', dataType: 'Double', writable: true, unit: 'mm' },
  { id: 'resultY', name: 'Result Y', address: 'HR:2', dataType: 'Double', writable: true, unit: 'mm' },
  { id: 'resultR', name: 'Result R', address: 'HR:4', dataType: 'Double', writable: true, unit: 'deg' },
  { id: 'recipe', name: 'Recipe', address: 'HR:6', dataType: 'Integer', writable: true }
];

const s7Tags: DeviceTagDefinition[] = [
  { id: 'ready', name: 'PLC Ready', address: 'DB10.DBX0.0', dataType: 'Boolean', writable: false },
  { id: 'trigger', name: 'Vision Trigger', address: 'DB10.DBX0.1', dataType: 'Boolean', writable: false },
  { id: 'resultReady', name: 'Result Ready', address: 'DB10.DBX0.2', dataType: 'Boolean', writable: true },
  { id: 'resultOk', name: 'Result OK', address: 'DB10.DBX0.3', dataType: 'Boolean', writable: true },
  { id: 'hostHeartbeat', name: 'Host Heartbeat', address: 'DB10.DBX0.4', dataType: 'Boolean', writable: true },
  { id: 'deviceHeartbeat', name: 'PLC Heartbeat', address: 'DB10.DBX0.5', dataType: 'Boolean', writable: false },
  { id: 'resultX', name: 'Result X', address: 'DB10.DBD4', dataType: 'Double', writable: true, unit: 'mm' },
  { id: 'resultY', name: 'Result Y', address: 'DB10.DBD8', dataType: 'Double', writable: true, unit: 'mm' },
  { id: 'resultR', name: 'Result R', address: 'DB10.DBD12', dataType: 'Double', writable: true, unit: 'deg' },
  { id: 'recipe', name: 'Recipe', address: 'DB10.DBD16', dataType: 'Integer', writable: true }
];

const prettyTags = (tags: DeviceTagDefinition[]) => JSON.stringify(tags, null, 2);
const displayValue = (value: unknown) => {
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE';
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : value.toFixed(3);
  if (value == null) return '—';
  return String(value);
};

export default function DevicePanel({ open, onClose }: Props) {
  const [devices, setDevices] = useState<DeviceDescriptor[]>([]);
  const [selectedId, setSelectedId] = useState('virtual-modbus-1');
  const [selectedTagId, setSelectedTagId] = useState('trigger');
  const [busy, setBusy] = useState('');
  const [registerOpen, setRegisterOpen] = useState(false);
  const [settingsForm] = Form.useForm<DeviceRuntimeSettings>();
  const [writeForm] = Form.useForm<WriteForm>();
  const [registerForm] = Form.useForm<RegisterForm>();
  const [messageApi, contextHolder] = message.useMessage();

  const selected = useMemo(() => devices.find((x) => x.id === selectedId) ?? devices[0], [devices, selectedId]);
  const selectedTag = useMemo(() => selected?.tags.find((x) => x.id === selectedTagId), [selected, selectedTagId]);

  const refresh = async (quiet = false) => {
    try {
      const response = await fetch('/api/devices');
      if (!response.ok) throw new Error('Device runtime unavailable');
      const data: DeviceDescriptor[] = await response.json();
      setDevices(data);
      if (!data.some((x) => x.id === selectedId) && data[0]) setSelectedId(data[0].id);
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : 'Device refresh failed');
    }
  };

  useEffect(() => {
    if (!open) return;
    refresh();
    const timer = window.setInterval(() => refresh(true), 500);
    return () => window.clearInterval(timer);
  }, [open, selectedId]);

  useEffect(() => {
    if (!selected) return;
    settingsForm.setFieldsValue(selected.settings);
    const firstWritable = selected.tags.find((x) => x.writable);
    if (!selected.tags.some((x) => x.id === selectedTagId)) setSelectedTagId(firstWritable?.id ?? selected.tags[0]?.id ?? '');
  }, [selected?.id]);

  useEffect(() => {
    const sample = selected?.values[selectedTagId];
    writeForm.setFieldsValue({ tagId: selectedTagId, value: sample?.value == null ? '' : String(sample.value) });
  }, [selectedTagId, selected?.updatedAt]);

  const command = async (action: 'connect' | 'disconnect') => {
    if (!selected) return;
    setBusy(action);
    try {
      const response = await fetch(`/api/devices/${encodeURIComponent(selected.id)}/${action}`, { method: 'POST' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error ?? `${action} failed`);
      await refresh(true);
      messageApi.success(`${action} OK`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : `${action} failed`); }
    finally { setBusy(''); }
  };

  const readAll = async () => {
    if (!selected) return;
    setBusy('readAll');
    try {
      const response = await fetch(`/api/devices/${encodeURIComponent(selected.id)}/read-all`, { method: 'POST' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error ?? 'Batch read failed');
      await refresh(true);
      messageApi.success(`Fresh read OK · ${Array.isArray(data) ? data.length : 0} tags`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Batch read failed'); }
    finally { setBusy(''); }
  };

  const saveSettings = async (values: DeviceRuntimeSettings) => {
    if (!selected) return;
    setBusy('settings');
    try {
      const response = await fetch(`/api/devices/${encodeURIComponent(selected.id)}/settings`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values)
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error ?? 'Settings failed');
      await refresh(true);
      messageApi.success('Device settings applied');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Settings failed'); }
    finally { setBusy(''); }
  };

  const parseValue = (tag: DeviceTagDefinition | undefined, value: string): unknown => {
    if (!tag) return value;
    if (tag.dataType === 'Boolean') return ['true', '1', 'on', 'yes'].includes(value.trim().toLowerCase());
    if (tag.dataType === 'Integer') return Number.parseInt(value || '0', 10);
    if (tag.dataType === 'Double') return Number(value || '0');
    return value;
  };

  const writeTag = async (values: WriteForm) => {
    if (!selected || !selectedTag?.writable) return;
    setBusy('write');
    try {
      const response = await fetch(`/api/devices/${encodeURIComponent(selected.id)}/tags/${encodeURIComponent(values.tagId)}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ value: parseValue(selectedTag, values.value) })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error ?? 'Tag write failed');
      await refresh(true);
      messageApi.success(`${values.tagId} written`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Tag write failed'); }
    finally { setBusy(''); }
  };

  const openRegistration = (kind: 'modbus' | 's7') => {
    registerForm.setFieldsValue(kind === 'modbus' ? {
      kind, id: 'modbus-tcp-1', name: 'Modbus TCP PLC', host: '192.168.0.10', port: 502, timeoutMs: 3000, unitId: 1, registerOrder: 'ABCD', tagsJson: prettyTags(modbusTags)
    } : {
      kind, id: 's7-1500-1', name: 'Siemens S7-1500', host: '192.168.0.20', port: 102, timeoutMs: 3000, cpuType: 'S71500', rack: 0, slot: 0, tagsJson: prettyTags(s7Tags)
    });
    setRegisterOpen(true);
  };

  const registerDevice = async (values: RegisterForm) => {
    setBusy('register');
    try {
      const tags = JSON.parse(values.tagsJson) as DeviceTagDefinition[];
      if (!Array.isArray(tags) || tags.length === 0) throw new Error('Tag Mapping JSON must be a non-empty array');
      const endpoint = values.kind === 'modbus' ? '/api/devices/register/modbus-tcp' : '/api/devices/register/s7';
      const payload = values.kind === 'modbus'
        ? { id: values.id, name: values.name, host: values.host, port: values.port, unitId: values.unitId ?? 1, registerOrder: values.registerOrder ?? 'ABCD', timeoutMs: values.timeoutMs, tags }
        : { id: values.id, name: values.name, host: values.host, port: values.port, cpuType: values.cpuType ?? 'S71500', rack: values.rack ?? 0, slot: values.slot ?? 0, timeoutMs: values.timeoutMs, tags };
      const response = await fetch(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error ?? 'Device registration failed');
      setRegisterOpen(false);
      setSelectedId(values.id);
      await refresh(true);
      messageApi.success(`设备 ${values.id} 已注册`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '设备注册失败'); }
    finally { setBusy(''); }
  };

  const removeSelected = async () => {
    if (!selected || selected.id === 'virtual-modbus-1') return;
    setBusy('remove');
    try {
      const response = await fetch(`/api/devices/${encodeURIComponent(selected.id)}`, { method: 'DELETE' });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error ?? '移除失败');
      }
      setSelectedId('virtual-modbus-1');
      await refresh(true);
      messageApi.success('设备已移除');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '移除失败'); }
    finally { setBusy(''); }
  };

  const registerKind = Form.useWatch('kind', registerForm);

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={1360} title="设备运行时 · NModbus / Siemens S7 / 标签 / 诊断" destroyOnHidden>
      {contextHolder}
      <Space style={{ marginBottom: 10 }} wrap>
        <Button onClick={() => refresh()}>刷新</Button>
        <Button type="primary" onClick={() => openRegistration('modbus')}>+ Modbus TCP</Button>
        <Button onClick={() => openRegistration('s7')}>+ Siemens S7</Button>
        <Tag color="blue">设备驱动</Tag><Tag color="purple">批量设备驱动</Tag><Tag color="cyan">强类型标签</Tag><Tag color="gold">质量状态 + 时间戳</Tag>
      </Space>

      <Table<DeviceDescriptor>
        size="small" pagination={false} rowKey="id" dataSource={devices}
        rowClassName={(row) => row.id === selected?.id ? 'robot-selected-row' : ''}
        onRow={(row) => ({ onClick: () => setSelectedId(row.id) })}
        columns={[
          { title: 'ID', dataIndex: 'id', width: 150 },
          { title: '协议', dataIndex: 'protocol', width: 125 },
          { title: '连接状态', width: 110, render: (_: unknown, row) => <Tag color={connectionColor[row.connectionState]}>{localizeStatus(row.connectionState)}</Tag> },
          { title: '轮询频率', width: 82, render: (_: unknown, row) => `${row.stats.actualPollHz.toFixed(1)} Hz` },
          { title: '批量读取', width: 70, render: (_: unknown, row) => row.stats.batchReadCycles },
          { title: '往返耗时', width: 86, render: (_: unknown, row) => `${row.diagnostics.lastRoundTripMs.toFixed(1)} 毫秒` },
          { title: '终端地址', render: (_: unknown, row) => row.endpoint }
        ]}
      />

      {selected ? <div style={{ display: 'grid', gridTemplateColumns: '1.25fr .75fr', gap: 18, marginTop: 14 }}>
        <section>
          <Space wrap style={{ marginBottom: 10 }}>
            <Button loading={busy === 'connect'} onClick={() => command('connect')}>连接</Button>
            <Button loading={busy === 'disconnect'} onClick={() => command('disconnect')}>断开</Button>
            <Button loading={busy === 'readAll'} onClick={readAll}>读取最新数据</Button>
            {selected.id !== 'virtual-modbus-1' && <Popconfirm title="移除此设备？" onConfirm={removeSelected}><Button danger loading={busy === 'remove'}>移除</Button></Popconfirm>}
            <Tag color={selected.capabilities.realIo ? 'green' : 'default'}>{selected.capabilities.realIo ? '真实 I/O' : '模拟器'}</Tag>
            {selected.capabilities.supportsBatchRead && <Tag color="cyan">支持批量读取</Tag>}
          </Space>

          <div className="robot-stats-grid">
            <Statistic title="轮询频率（Hz）" value={selected.stats.actualPollHz} precision={1} />
            <Statistic title="I/O 操作数" value={selected.diagnostics.transactions} />
            <Statistic title="平均往返耗时（毫秒）" value={selected.diagnostics.averageRoundTripMs} precision={2} />
            <Statistic title="重连次数" value={selected.stats.reconnectCount} />
          </div>

          <Typography.Text type="secondary">{selected.driver} · {selected.vendor} · {selected.model}</Typography.Text>
          <Typography.Title level={5}>标签监视</Typography.Title>
          <Table<DeviceTagDefinition>
            size="small" rowKey="id" pagination={{ pageSize: 8, size: 'small' }} dataSource={selected.tags}
            rowClassName={(row) => row.id === selectedTagId ? 'robot-selected-row' : ''}
            onRow={(row) => ({ onClick: () => setSelectedTagId(row.id) })}
            columns={[
              { title: '标签', dataIndex: 'id', width: 118 },
              { title: '地址', dataIndex: 'address', width: 130 },
              { title: '类型', dataIndex: 'dataType', width: 72 },
              { title: '读 / 写', width: 50, render: (_: unknown, row) => row.writable ? '读写' : '只读' },
              { title: '数值', width: 115, render: (_: unknown, row) => displayValue(selected.values[row.id]?.value) },
              { title: '质量状态', width: 90, render: (_: unknown, row) => { const q = selected.values[row.id]?.quality ?? 'Disconnected'; return <Tag color={qualityColor[q]}>{localizeStatus(q)}</Tag>; } },
              { title: '单位', dataIndex: 'unit', width: 58 },
              { title: '时间戳', render: (_: unknown, row) => selected.values[row.id]?.timestamp ? new Date(selected.values[row.id].timestamp).toLocaleTimeString('zh-CN') : '—' }
            ]}
          />
        </section>

        <section>
          <Typography.Title level={5}>通信诊断</Typography.Title>
          <div className="robot-stats-grid">
            <Statistic title="事务数" value={selected.diagnostics.transactions} />
            <Statistic title="批量读取" value={selected.diagnostics.batchReads} />
            <Statistic title="批量写入" value={selected.diagnostics.batchWrites} />
            <Statistic title="最近往返耗时（毫秒）" value={selected.diagnostics.lastRoundTripMs} precision={2} />
          </div>
          <Space wrap style={{ margin: '8px 0 14px' }}>
            <Tag color={selected.stats.hostHeartbeat ? 'green' : 'default'}>主机心跳 {selected.stats.hostHeartbeat ? '1' : '0'}</Tag>
            <Tag color={selected.stats.deviceHeartbeat ? 'green' : 'default'}>设备心跳 {selected.stats.deviceHeartbeat ? '1' : '0'}</Tag>
            <Tag>读取错误 {selected.stats.readErrors}</Tag><Tag>写入错误 {selected.stats.writeErrors}</Tag>
          </Space>
          {selected.diagnostics.lastProtocolError && <Alert type="error" showIcon message="最近的协议错误" description={selected.diagnostics.lastProtocolError} style={{ marginBottom: 10 }} />}

          <Typography.Title level={5}>运行时设置</Typography.Title>
          <Form form={settingsForm} layout="vertical" onFinish={saveSettings}>
            <div className="robot-settings-grid">
              <Form.Item label="轮询间隔（毫秒）" name="pollIntervalMs"><InputNumber min={20} max={60000} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="重连延迟（毫秒）" name="reconnectDelayMs"><InputNumber min={100} max={60000} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="心跳间隔（毫秒）" name="heartbeatIntervalMs"><InputNumber min={100} max={60000} style={{ width: '100%' }} /></Form.Item>
            </div>
            <Form.Item label="主机心跳标签" name="hostHeartbeatTagId"><Input /></Form.Item>
            <Form.Item label="设备心跳标签" name="deviceHeartbeatTagId"><Input /></Form.Item>
            <Button htmlType="submit" loading={busy === 'settings'}>应用设置</Button>
          </Form>

          <Typography.Title level={5} style={{ marginTop: 18 }}>手动写入标签</Typography.Title>
          {selectedTag?.writable ? <Form form={writeForm} layout="vertical" onFinish={writeTag}>
            <Form.Item label="设备标签" name="tagId"><Select options={selected.tags.filter((x) => x.writable).map((x) => ({ value: x.id, label: `${x.id} · ${x.address}` }))} onChange={setSelectedTagId} /></Form.Item>
            <Form.Item label={`Value (${selectedTag.dataType})`} name="value"><Input /></Form.Item>
            <Button type="primary" htmlType="submit" loading={busy === 'write'}>写入标签</Button>
          </Form> : <Alert type="info" showIcon message="所选标签为只读" />}
          {selected.error && <Alert style={{ marginTop: 10 }} type="error" showIcon message={selected.error} />}
        </section>
      </div> : <div className="job-empty">尚未注册设备</div>}

      <Modal open={registerOpen} onCancel={() => setRegisterOpen(false)} footer={null} width={900} title="注册真实 PLC 驱动" destroyOnHidden>
        <Form form={registerForm} layout="vertical" onFinish={registerDevice}>
          <div className="robot-settings-grid">
            <Form.Item label="驱动" name="kind" rules={[{ required: true }]}><Select options={[{ value: 'modbus', label: 'NModbus · Modbus TCP' }, { value: 's7', label: 'S7.Net Plus · Siemens S7' }]} /></Form.Item>
            <Form.Item label="设备 ID" name="id" rules={[{ required: true }]}><Input /></Form.Item>
            <Form.Item label="显示名称" name="name" rules={[{ required: true }]}><Input /></Form.Item>
            <Form.Item label="主机 / IP" name="host" rules={[{ required: true }]}><Input /></Form.Item>
            <Form.Item label="端口" name="port"><InputNumber min={1} max={65535} style={{ width: '100%' }} /></Form.Item>
            <Form.Item label="超时（毫秒）" name="timeoutMs"><InputNumber min={100} max={60000} style={{ width: '100%' }} /></Form.Item>
            {registerKind === 'modbus' ? <>
              <Form.Item label="单元 ID" name="unitId"><InputNumber min={0} max={247} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="32 位字节序" name="registerOrder"><Select options={['ABCD', 'CDAB', 'BADC', 'DCBA'].map((x) => ({ value: x, label: x }))} /></Form.Item>
            </> : <>
              <Form.Item label="CPU 类型" name="cpuType"><Select options={['S7200', 'S7300', 'S7400', 'S71200', 'S71500'].map((x) => ({ value: x, label: x }))} /></Form.Item>
              <Form.Item label="机架" name="rack"><InputNumber min={0} max={7} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="插槽" name="slot"><InputNumber min={0} max={31} style={{ width: '100%' }} /></Form.Item>
            </>}
          </div>
          <Form.Item label="标签映射 JSON" name="tagsJson" rules={[{ required: true }]} extra={registerKind === 'modbus' ? '地址示例：C:0 / DI:0 / HR:0 / IR:0，索引从 0 开始。' : '地址示例：DB10.DBX0.0 / DB10.DBW2 / DB10.DBD4 / M10.0 / MW20 / MD24。'}>
            <Input.TextArea rows={15} spellCheck={false} />
          </Form.Item>
          <Alert type="info" showIcon style={{ marginBottom: 12 }} message="标签 ID 是工作流接口约定" description="只要 trigger / resultOk / resultX 等标签 ID 保持不变，之后可以调整协议地址而无需修改工作流节点。" />
          <Button type="primary" htmlType="submit" loading={busy === 'register'}>注册驱动</Button>
        </Form>
      </Modal>
    </Modal>
  );
}
