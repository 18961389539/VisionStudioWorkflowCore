import type { FormInstance } from 'antd';
import { Alert, Button, Empty, Form, InputNumber, Select, Space, Statistic, Tag, Typography } from 'antd';
import type { CameraDescriptor, CameraOutputPixelFormat, CameraSettings, CameraTriggerMode } from '../types';
import { localizeStatus } from '../i18n';
import { acquisitionColor, stateColor } from './cameraPanelShared';

export type CameraAcquisitionTabProps = {
  busy?: string;
  selected?: CameraDescriptor;
  settingsForm: FormInstance<CameraSettings>;
  saveSettings: (values: CameraSettings) => void | Promise<void>;
  editedTriggerMode?: CameraTriggerMode;
};

/** 「采集」标签页：曝光/增益/帧率/触发设置、关键指标与运行时告警。 */
export default function CameraAcquisitionTab({ busy, selected, settingsForm, saveSettings, editedTriggerMode }: CameraAcquisitionTabProps) {
  return (
    <>
{/* ── 采集：设置 + 关键指标（底层计数器见“诊断”） ── */}
{selected ? <>
  <Typography.Title level={5}>采集设置</Typography.Title>
  <Form form={settingsForm} layout="vertical" onFinish={saveSettings}><div className="camera-settings-grid">
    <Form.Item label="曝光时间（μs）" name="exposureUs"><InputNumber min={selected.capabilities.minExposureUs} max={selected.capabilities.maxExposureUs} disabled={!selected.capabilities.exposure} style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="增益（dB）" name="gainDb"><InputNumber min={selected.capabilities.minGainDb} max={selected.capabilities.maxGainDb} step={0.1} disabled={!selected.capabilities.gain} style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="目标帧率" name="targetFps"><InputNumber min={0.2} max={selected.capabilities.maxFps} step={0.5} disabled={!selected.capabilities.frameRate} style={{ width: '100%' }} /></Form.Item>
    <Form.Item label="触发模式" name="triggerMode"><Select<CameraTriggerMode> options={[{ value: 'Continuous', label: '连续采集' }, { value: 'Software', label: '软件触发', disabled: !selected.capabilities.softwareTrigger }, { value: 'External', label: '外部触发', disabled: !selected.capabilities.externalTrigger }]} /></Form.Item>
    <Form.Item label="主机输出格式" name="outputPixelFormat"><Select<CameraOutputPixelFormat> options={(selected.capabilities.outputPixelFormats ?? ['Auto', 'Mono8', 'Bgr8']).map((value) => ({ value, label: value === 'Bgr8' ? 'BGR8' : value }))} /></Form.Item>
    <Form.Item label="外部触发线路" name="externalTriggerSource"><Select options={['Line0', 'Line1', 'Line2', 'Line3'].map((value) => ({ value, label: value }))} disabled={editedTriggerMode !== 'External'} /></Form.Item>
  </div><Button htmlType="submit" loading={busy === `${selected.id}:settings`}>应用设置</Button></Form>

  <div className="camera-stats-grid">
    <Statistic title="帧率" value={selected.acquisition.actualFps} precision={1} suffix="fps" />
    <Statistic title="丢帧" value={selected.acquisition.ringOverwrites + selected.acquisition.driverDroppedFrames} />
    <Statistic title="已发布帧数" value={selected.acquisition.framesPublished} />
    <Statistic title="重连次数" value={selected.acquisition.reconnectCount} />
  </div>
  <Space wrap style={{ marginBottom: 8 }}>
    <Tag color={stateColor[selected.state] ?? 'default'}>设备 {localizeStatus(selected.state)}</Tag>
    <Tag color={acquisitionColor[selected.acquisition.acquisitionState] ?? 'default'}>采集 {localizeStatus(selected.acquisition.acquisitionState)}</Tag>
    <Tag>相机原生：{selected.acquisition.nativePixelFormat ?? '—'}</Tag>
    <Tag>主机输出：{selected.settings.outputPixelFormat}</Tag>
  </Space>
  {selected.acquisition.runtimeError && <Alert type="warning" showIcon title={selected.acquisition.runtimeError} style={{ marginBottom: 8 }} />}
  {selected.acquisition.transport?.error && <Alert type="info" showIcon title={selected.acquisition.transport.error} style={{ marginBottom: 8 }} />}
</> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="请在左侧选择一台相机" />}
    </>
  );
}
