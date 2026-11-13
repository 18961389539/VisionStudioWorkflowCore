import type { FormInstance } from 'antd';
import { Alert, Button, Form, Input, InputNumber, Select, Space, Table, Tag, Typography } from 'antd';
import type { CameraAdapterDescriptor, CameraDiscoveredDevice } from '../types';

export type VendorRegisterValues = {
  driver: string;
  id: string;
  name?: string;
  serialNumber?: string;
  userDefinedName?: string;
  deviceKey?: string;
  ringCapacity?: number;
};

export type FileRegisterValues = { id: string; name?: string; source: string };

export type CameraRegistrationTabProps = {
  busy?: string;
  adapters: CameraAdapterDescriptor[];
  discoverDriver: string;
  setDiscoverDriver: (value: string) => void;
  setDiscovered: (value: CameraDiscoveredDevice[]) => void;
  setSelectedDiscovered: (value?: CameraDiscoveredDevice) => void;
  discoverVendor: () => void | Promise<void>;
  discovered: CameraDiscoveredDevice[];
  selectDiscovered: (device: CameraDiscoveredDevice) => void;
  selectedDiscovered?: CameraDiscoveredDevice;
  vendorForm: FormInstance;
  registerVendor: (values: VendorRegisterValues) => void | Promise<void>;
  form: FormInstance;
  registerFile: (values: FileRegisterValues) => void | Promise<void>;
  sourceOptions: { value: string; label: string }[];
};

/** 「设备注册」标签页：厂商 SDK 发现 + 厂商相机注册 + 沙箱文件相机注册。 */
export default function CameraRegistrationTab(props: CameraRegistrationTabProps) {
  const {
    busy, adapters, discoverDriver, setDiscoverDriver, setDiscovered, setSelectedDiscovered,
    discoverVendor, discovered, selectDiscovered, selectedDiscovered, vendorForm, registerVendor,
    form, registerFile, sourceOptions,
  } = props;
  return (
    <>
<Typography.Title level={5}>厂商相机</Typography.Title>
<Alert type="info" showIcon title="运行时加载的 SDK 适配器" description="Basler pylon 和 Hikrobot MVS 将通过本机已安装的 SDK 进行发现。VisionStudio 不会重新分发厂商 DLL。" style={{ marginBottom: 10 }} />
<Space wrap style={{ marginBottom: 8 }}>
  {adapters.map((adapter) => <Tag key={adapter.driver} color={adapter.isSdkAvailable ? 'green' : 'red'}>{adapter.vendor}：{adapter.isSdkAvailable ? 'SDK 已就绪' : 'SDK 不可用'}</Tag>)}
</Space>
<div className="media-filter-row">
  <Select value={discoverDriver} onChange={(value) => { setDiscoverDriver(value); setDiscovered([]); setSelectedDiscovered(undefined); }} options={adapters.map((x) => ({ value: x.driver, label: `${x.vendor} · ${x.driver}`, disabled: !x.isSdkAvailable }))} />
  <Button loading={busy === 'discover-vendor'} onClick={discoverVendor}>发现设备</Button>
</div>
{adapters.find((x) => x.driver === discoverDriver && !x.isSdkAvailable)?.sdkError && <Alert type="warning" showIcon title={adapters.find((x) => x.driver === discoverDriver)?.sdkError} style={{ marginBottom: 8 }} />}
<Table<CameraDiscoveredDevice> size="small" rowKey="deviceKey" dataSource={discovered} pagination={{ pageSize: 4, size: 'small' }} onRow={(row) => ({ onClick: () => selectDiscovered(row) })} rowClassName={(row) => row.deviceKey === selectedDiscovered?.deviceKey ? 'camera-selected-row' : ''} columns={[
  { title: '厂商', dataIndex: 'vendor', width: 82 }, { title: '型号', dataIndex: 'model', width: 145 }, { title: '序列号', dataIndex: 'serialNumber', width: 145 },
  { title: '名称 / IP', render: (_: unknown, row) => row.userDefinedName || row.ipAddress || '—', ellipsis: true }, { title: '传输层', dataIndex: 'transport', width: 70 }
]} />
{selectedDiscovered && <Form form={vendorForm} layout="vertical" onFinish={registerVendor}>
  <Form.Item name="driver" hidden><Input /></Form.Item><Form.Item name="serialNumber" hidden><Input /></Form.Item><Form.Item name="userDefinedName" hidden><Input /></Form.Item><Form.Item name="deviceKey" hidden><Input /></Form.Item>
  <div className="camera-file-form"><Form.Item label="运行时 ID" name="id" rules={[{ required: true }]}><Input /></Form.Item><Form.Item label="名称" name="name"><Input /></Form.Item></div>
  <Form.Item label="环形缓冲区容量" name="ringCapacity"><InputNumber min={2} max={64} style={{ width: '100%' }} /></Form.Item>
  <Button type="primary" htmlType="submit" loading={busy === 'register-vendor'}>注册 {selectedDiscovered.vendor} 相机</Button>
</Form>}

<Typography.Title level={5}>注册文件相机</Typography.Title>
<Alert type="info" showIcon title="仅支持沙箱媒体来源" description="文件相机接受媒体库中的 media:// 引用。绝对主机路径、UNC 路径和 ../ 路径跳转均会被拒绝。" style={{ marginBottom: 10 }} />
<Form form={form} layout="vertical" onFinish={registerFile} initialValues={{ id: 'file-1', name: 'File Camera 1' }}>
  <div className="camera-file-form"><Form.Item label="编号" name="id" rules={[{ required: true }]}><Input /></Form.Item><Form.Item label="名称" name="name"><Input /></Form.Item></div>
  <Form.Item label="媒体集合或图像" name="source" rules={[{ required: true }]}><Select showSearch options={sourceOptions} placeholder="media://offline 或已导入的图像" /></Form.Item>
  <Button htmlType="submit" loading={busy === 'register'}>注册</Button>
</Form>

    </>
  );
}
