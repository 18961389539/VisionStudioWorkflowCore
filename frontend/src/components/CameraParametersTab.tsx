import { Alert, Button, Empty, Form, Input, InputNumber, Popconfirm, Select, Space, Switch, Table, Tag, Typography, Upload, type FormInstance } from 'antd';
import type {
  CameraCommissioningProfile, CameraCommissioningSnapshot, CameraDescriptor,
  CameraFeatureDescriptor, CameraFeatureProfileRecord
} from '../types';

type Props = {
  busy?: string;
  selected?: CameraDescriptor;
  commissioning?: CameraCommissioningSnapshot;
  commissioningForm: FormInstance<CameraCommissioningProfile>;
  refreshCommissioning: (quiet?: boolean) => Promise<void> | void;
  applyCommissioning: (values: CameraCommissioningProfile) => Promise<void> | void;
  profileId: string;
  setProfileId: (value: string) => void;
  profileName: string;
  setProfileName: (value: string) => void;
  captureFeatureProfile: () => Promise<void> | void;
  selectedFeatureProfile?: string;
  setSelectedFeatureProfile: (value: string | undefined) => void;
  featureProfiles: CameraFeatureProfileRecord[];
  applySavedFeatureProfile: () => Promise<void> | void;
  exportFeatureProfile: () => Promise<void> | void;
  importFeatureProfileFile: (file: File) => Promise<void> | void;
  deleteFeatureProfile: () => Promise<void> | void;
  features: CameraFeatureDescriptor[];
  featureEdits: Record<string, string>;
  setFeatureEdits: React.Dispatch<React.SetStateAction<Record<string, string>>>;
  setFeature: (feature: CameraFeatureDescriptor) => Promise<void> | void;
};

export default function CameraParametersTab({
  busy,
  selected,
  commissioning,
  commissioningForm,
  refreshCommissioning,
  applyCommissioning,
  profileId,
  setProfileId,
  profileName,
  setProfileName,
  captureFeatureProfile,
  selectedFeatureProfile,
  setSelectedFeatureProfile,
  featureProfiles,
  applySavedFeatureProfile,
  exportFeatureProfile,
  importFeatureProfileFile,
  deleteFeatureProfile,
  features,
  featureEdits,
  setFeatureEdits,
  setFeature
}: Props) {
  if (!selected || !['basler-pylon', 'hikrobot-mvs'].includes(selected.driver)) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={selected ? '该相机驱动无厂商调试配置（仅 Basler / Hikrobot 支持）' : '请在左侧选择一台相机'}
      />
    );
  }

  return (
    <>
      <Typography.Title level={5}>触发 / 闪光灯调试</Typography.Title>
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 10 }}
        title="修改传输或 I/O 功能前，请先停止采集"
        description="调试配置会适配 Basler/Hikrobot GenICam/MVS 的常见功能。对于不支持的功能，系统会跳过并报告，不会猜测配置值。"
      />
      {selected.state === 'Closed' ? (
        <Alert type="warning" showIcon title="请先打开相机以读取调试功能" />
      ) : commissioning && (
        <>
          <Space wrap style={{ marginBottom: 8 }}>
            <Tag color="blue">配置 {commissioning.profileHash?.slice(0, 12) ?? '尚未采集'}</Tag>
            <Tag color={commissioning.capabilities.gigENetworkTuning ? 'green' : 'default'}>GigE 调优</Tag>
            <Tag color={commissioning.capabilities.strobeOutput ? 'green' : 'default'}>闪光灯输出</Tag>
            <Tag color={commissioning.capabilities.ptp ? 'green' : 'default'}>PTP</Tag>
            <Tag color={commissioning.capabilities.actionCommand ? 'green' : 'default'}>Action Command 就绪</Tag>
            <Button size="small" onClick={() => refreshCommissioning()}>重新读取相机</Button>
          </Space>
          <Form form={commissioningForm} layout="vertical" onFinish={applyCommissioning} initialValues={{ schemaVersion: 1 }}>
            <div className="camera-settings-grid">
              <Form.Item label="数据包大小（字节）" name="packetSizeBytes"><InputNumber min={576} max={16384} disabled={!commissioning.capabilities.gigENetworkTuning} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="包间延迟（时钟周期）" name="interPacketDelayTicks"><InputNumber min={0} disabled={!commissioning.capabilities.gigENetworkTuning} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="触发延迟（µs）" name="triggerDelayUs"><InputNumber min={0} disabled={!commissioning.capabilities.triggerDelay} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="触发输入线路" name="triggerInputLine"><Select allowClear disabled={!commissioning.capabilities.lineDebouncer} options={(commissioning.capabilities.inputLines ?? []).map((value) => ({ value, label: value }))} /></Form.Item>
              <Form.Item label="输入消抖（µs）" name="lineDebouncerUs"><InputNumber min={0} disabled={!commissioning.capabilities.lineDebouncer} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="输出线路" name="outputLine"><Select allowClear disabled={!commissioning.capabilities.strobeOutput} options={(commissioning.capabilities.outputLines ?? []).map((value) => ({ value, label: value }))} /></Form.Item>
              <Form.Item label="输出来源" name="outputSource"><Select allowClear disabled={!commissioning.capabilities.strobeOutput} options={(commissioning.capabilities.outputSources ?? []).map((value) => ({ value, label: value }))} /></Form.Item>
              <Form.Item label="反转输出" name="outputInverted" valuePropName="checked"><Switch disabled={!commissioning.capabilities.strobeOutput} /></Form.Item>
              <Form.Item label="启用闪光灯" name="strobeEnabled" valuePropName="checked"><Switch disabled={!commissioning.capabilities.strobeOutput} /></Form.Item>
              <Form.Item label="闪光灯延迟（µs）" name="strobeDelayUs"><InputNumber disabled={!commissioning.capabilities.strobeOutput} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="闪光灯持续时间（µs）" name="strobeDurationUs"><InputNumber min={0} disabled={!commissioning.capabilities.strobeOutput} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="闪光灯预延迟（µs）" name="strobePreDelayUs"><InputNumber min={0} disabled={!commissioning.capabilities.strobeOutput} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="PTP" name="ptpEnabled" valuePropName="checked"><Switch disabled={!commissioning.capabilities.ptp} /></Form.Item>
              <Form.Item label="Action 设备密钥" name="actionDeviceKey"><InputNumber min={0} disabled={!commissioning.capabilities.actionCommand} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Action 组密钥" name="actionGroupKey"><InputNumber min={0} disabled={!commissioning.capabilities.actionCommand} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="Action 组掩码" name="actionGroupMask"><InputNumber min={0} disabled={!commissioning.capabilities.actionCommand} style={{ width: '100%' }} /></Form.Item>
            </div>
            <Button type="primary" htmlType="submit" disabled={selected.state !== 'Open'} loading={busy === `${selected.id}:commissioning`}>应用调试配置</Button>
          </Form>

          <Typography.Text strong>命名配置</Typography.Text>
          <div className="media-create-row" style={{ marginTop: 8 }}>
            <Input value={profileId} onChange={(e) => setProfileId(e.target.value)} placeholder="配置 ID" />
            <Input value={profileName} onChange={(e) => setProfileName(e.target.value)} placeholder="配置名称" />
            <Button loading={busy === 'profile-capture'} onClick={captureFeatureProfile}>保存当前配置</Button>
          </div>
          <Space wrap style={{ marginBottom: 10 }}>
            <Select
              style={{ minWidth: 280 }}
              allowClear
              placeholder="选择已保存的功能配置"
              value={selectedFeatureProfile}
              onChange={setSelectedFeatureProfile}
              options={featureProfiles.map((x) => ({ value: x.id, label: `${x.name} · ${x.profileHash.slice(0, 8)}` }))}
            />
            <Button disabled={!selectedFeatureProfile || selected.state !== 'Open'} loading={busy === 'profile-apply'} onClick={applySavedFeatureProfile}>应用已保存配置</Button>
            <Button disabled={!selectedFeatureProfile} onClick={exportFeatureProfile}>导出 JSON</Button>
            <Upload accept=".json" showUploadList={false} beforeUpload={(file) => { importFeatureProfileFile(file as File); return Upload.LIST_IGNORE; }}>
              <Button loading={busy === 'profile-import'}>导入 JSON</Button>
            </Upload>
            <Popconfirm title="确定删除所选相机配置吗？" okText="删除" cancelText="取消" onConfirm={deleteFeatureProfile}>
              <Button danger disabled={!selectedFeatureProfile} loading={busy === 'profile-delete'}>删除</Button>
            </Popconfirm>
          </Space>

          <Typography.Text strong>功能浏览器</Typography.Text>
          <Table<CameraFeatureDescriptor>
            size="small"
            rowKey="key"
            dataSource={features}
            pagination={{ pageSize: 8, size: 'small' }}
            columns={[
              { title: '类别', dataIndex: 'category', width: 105 },
              {
                title: '功能',
                dataIndex: 'displayName',
                width: 170,
                render: (_: unknown, row) => (
                  <>
                    <div>{row.displayName}</div>
                    <Typography.Text type="secondary" style={{ fontSize: 11 }}>{row.key}</Typography.Text>
                  </>
                )
              },
              {
                title: '值',
                render: (_: unknown, row) => row.options?.length ? (
                  <Select
                    size="small"
                    style={{ width: '100%' }}
                    value={featureEdits[row.key]}
                    onChange={(value) => setFeatureEdits((old) => ({ ...old, [row.key]: value }))}
                    options={row.options.map((value) => ({ value, label: value }))}
                  />
                ) : (
                  <Input
                    size="small"
                    value={featureEdits[row.key] ?? ''}
                    suffix={row.unit ?? undefined}
                    onChange={(e) => setFeatureEdits((old) => ({ ...old, [row.key]: e.target.value }))}
                  />
                )
              },
              { title: '配置', width: 70, render: (_: unknown, row) => row.profileEligible ? <Tag color="green">可配置</Tag> : <Tag>只读</Tag> },
              { title: '', width: 64, render: (_: unknown, row) => <Button size="small" disabled={!row.writable || !row.profileEligible || selected.state !== 'Open'} loading={busy === `feature:${row.key}`} onClick={() => setFeature(row)}>设置</Button> }
            ]}
          />
        </>
      )}
    </>
  );
}
