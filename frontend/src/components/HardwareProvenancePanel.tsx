import { useEffect, useMemo, useState } from 'react';
import { Button, Descriptions, Form, Input, Modal, Select, Space, Table, Tag, Typography, message } from 'antd';
import type { HardwareProvenanceDeclaration, HardwareProvenanceSnapshot } from '../types';
import { localizeStatus } from '../i18n';

type Props = { open: boolean; onClose: () => void };

const kindOptions = [
  { value: 'all', label: '全部资产' },
  { value: 'camera', label: '相机' },
  { value: 'device', label: 'PLC / 设备' },
  { value: 'robot', label: '机器人' }
];

const fieldNames: Record<string, string> = {
  manufacturer: '制造商', productName: '产品名称', model: '型号', serialNumber: '序列号', hardwareRevision: '硬件版本',
  firmwareVersion: '固件版本', softwareVersion: '软件版本', controllerVersion: '控制器版本', programName: '程序名称',
  programHash: '程序哈希', lensModel: '镜头型号', lensSerial: '镜头序列号', lightController: '光源控制器',
  lightProgram: '光源配置', fixtureRevision: '夹具版本'
};

export default function HardwareProvenancePanel({ open, onClose }: Props) {
  const [items, setItems] = useState<HardwareProvenanceSnapshot[]>([]);
  const [selected, setSelected] = useState<HardwareProvenanceSnapshot>();
  const [filter, setFilter] = useState('all');
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [form] = Form.useForm();
  const [messageApi, contextHolder] = message.useMessage();

  const refresh = async () => {
    const response = await fetch('/api/hardware-provenance');
    if (!response.ok) return;
    const data: HardwareProvenanceSnapshot[] = await response.json();
    setItems(data);
    if (selected) setSelected(data.find((x) => x.kind === selected.kind && x.id === selected.id));
  };

  useEffect(() => { if (open) void refresh(); }, [open]);

  const visible = useMemo(() => filter === 'all' ? items : items.filter((x) => x.kind === filter), [items, filter]);

  const startEdit = () => {
    if (!selected) return;
    const d = selected.data;
    form.setFieldsValue({
      manufacturer: d.manufacturer, productName: d.productName, model: d.model, serialNumber: d.serialNumber,
      hardwareRevision: d.hardwareRevision, firmwareVersion: d.firmwareVersion, softwareVersion: d.softwareVersion,
      controllerVersion: d.controllerVersion, programName: d.programName, programHash: d.programHash,
      lensModel: d.attributes?.lensModel, lensSerial: d.attributes?.lensSerial,
      lightController: d.attributes?.lightController, lightProgram: d.attributes?.lightProgram,
      fixtureRevision: d.attributes?.fixtureRevision
    });
    setEditing(true);
  };

  const save = async () => {
    if (!selected) return;
    const value = await form.validateFields();
    const attributes = Object.fromEntries([
      ['lensModel', value.lensModel], ['lensSerial', value.lensSerial], ['lightController', value.lightController],
      ['lightProgram', value.lightProgram], ['fixtureRevision', value.fixtureRevision]
    ].filter(([, v]) => typeof v === 'string' && v.trim()));
    const body: HardwareProvenanceDeclaration = {
      manufacturer: value.manufacturer, productName: value.productName, model: value.model, serialNumber: value.serialNumber,
      hardwareRevision: value.hardwareRevision, firmwareVersion: value.firmwareVersion, softwareVersion: value.softwareVersion,
      controllerVersion: value.controllerVersion, programName: value.programName, programHash: value.programHash, attributes
    };
    setBusy(true);
    try {
      const response = await fetch(`/api/hardware-provenance/${encodeURIComponent(selected.kind)}/${encodeURIComponent(selected.id)}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.detail ?? data.error ?? '保存失败');
      setSelected(data);
      setEditing(false);
      await refresh();
      messageApi.success('硬件来源声明已保存');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '保存失败'); }
    finally { setBusy(false); }
  };

  const clearDeclaration = async () => {
    if (!selected) return;
    setBusy(true);
    try {
      const response = await fetch(`/api/hardware-provenance/${encodeURIComponent(selected.kind)}/${encodeURIComponent(selected.id)}`, { method: 'DELETE' });
      if (!response.ok && response.status !== 404) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.detail ?? '删除失败');
      }
      setEditing(false);
      await refresh();
      messageApi.success('已删除手动声明；适配器和设备描述信息仍会保留');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '删除失败'); }
    finally { setBusy(false); }
  };

  return <Modal open={open} onCancel={onClose} footer={null} width={1180} title="硬件来源信息 · 物理依赖锁定">
    {contextHolder}
    <Space style={{ marginBottom: 10 }}>
      <Select value={filter} onChange={setFilter} options={kindOptions} style={{ width: 160 }} />
      <Button onClick={refresh}>刷新</Button>
      <Typography.Text type="secondary">适配器信息与现场声明会合并生成作业依赖指纹。</Typography.Text>
    </Space>
    <div style={{ display: 'grid', gridTemplateColumns: '1.15fr 1fr', gap: 16 }}>
      <Table size="small" pagination={false} rowKey={(x) => `${x.kind}:${x.id}`} dataSource={visible}
        onRow={(row) => ({ onClick: () => setSelected(row) })}
        rowClassName={(row) => selected?.kind === row.kind && selected?.id === row.id ? 'job-selected-row' : ''}
        columns={[
          { title: '类型', dataIndex: 'kind', width: 80, render: (v: string) => <Tag>{localizeStatus(v)}</Tag> },
          { title: '资产 ID', dataIndex: 'id', width: 150 },
          { title: '型号', render: (_: unknown, row: HardwareProvenanceSnapshot) => row.data.model ?? row.data.productName ?? '—' },
          { title: '序列号', render: (_: unknown, row: HardwareProvenanceSnapshot) => row.data.serialNumber ?? '—' },
          { title: '完整度', dataIndex: 'completeness', width: 100, render: (v: string) => <Tag color={v === 'Complete' ? 'green' : v === 'Partial' ? 'gold' : 'red'}>{localizeStatus(v)}</Tag> },
          { title: '指纹', dataIndex: 'fingerprint', width: 110, render: (v: string) => <Typography.Text code>{v.slice(0, 10)}</Typography.Text> }
        ]} />
      <section>
        {!selected ? <Typography.Text type="secondary">请选择资产以查看硬件身份信息。</Typography.Text> : <>
          <Space style={{ marginBottom: 8 }} wrap>
            <Tag color={selected.hasManualDeclaration ? 'blue' : 'default'}>{selected.provider}</Tag>
            {selected.liveProbeConfigured && <Tag color={selected.liveProbeSucceeded ? 'green' : 'red'}>
              {selected.liveProbeProvider ?? '实时探测'} · {selected.liveProbeSucceeded ? '可用' : '不可用'}{selected.liveProbeRequired ? ' · 必需' : ''}
            </Tag>}
            <Button size="small" onClick={startEdit}>声明 / 编辑</Button>
            {selected.hasManualDeclaration && <Button size="small" danger onClick={clearDeclaration} loading={busy}>清除声明</Button>}
          </Space>
          <Descriptions size="small" bordered column={1}>
            <Descriptions.Item label="制造商">{selected.data.manufacturer ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="产品 / 型号">{selected.data.productName ?? '—'} / {selected.data.model ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="序列号 / 硬件版本">{selected.data.serialNumber ?? '—'} / {selected.data.hardwareRevision ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="固件 / 软件">{selected.data.firmwareVersion ?? '—'} / {selected.data.softwareVersion ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="控制器">{selected.data.controllerVersion ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="程序">{selected.data.programName ?? '—'} · {selected.data.programHash ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="外部硬件">{Object.entries(selected.data.attributes ?? {}).map(([k, v]) => <Tag key={k}>{k}={v}</Tag>)}</Descriptions.Item>
            <Descriptions.Item label="建议补充的信息">{selected.missingRecommendedFields.length ? selected.missingRecommendedFields.map(x => fieldNames[x] ?? x).join('、') : '无'}</Descriptions.Item>
            {selected.liveProbeConfigured && <Descriptions.Item label="厂商实时探测">
              {selected.liveProbeSucceeded ? '可用' : `不可用${selected.liveProbeError ? ` · ${selected.liveProbeError}` : ''}`}
            </Descriptions.Item>}
          </Descriptions>
        </>}
      </section>
    </div>

    <Modal open={editing} onCancel={() => setEditing(false)} onOk={save} confirmLoading={busy} title={`${localizeStatus(selected?.kind) ?? ''} ${selected?.id ?? ''} · 现场声明`} width={760}>
      <Typography.Paragraph type="secondary">仅填写适配器无法可靠读取的信息。被生产作业引用时，系统会阻止修改这些信息。</Typography.Paragraph>
      <Form form={form} layout="vertical">
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
          {['manufacturer','productName','model','serialNumber','hardwareRevision','firmwareVersion','softwareVersion','controllerVersion','programName','programHash','lensModel','lensSerial','lightController','lightProgram','fixtureRevision'].map((name) =>
            <Form.Item key={name} name={name} label={fieldNames[name] ?? name}><Input /></Form.Item>)}
        </div>
      </Form>
    </Modal>
  </Modal>;
}
