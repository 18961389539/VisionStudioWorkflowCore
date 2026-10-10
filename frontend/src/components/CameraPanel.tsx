import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Empty, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Statistic, Switch, Table, Tabs, Tag, Typography, Upload, message } from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import type { CameraAdapterDescriptor, CameraCommissioningProfile, CameraCommissioningSnapshot, CameraDescriptor, CameraDiscoveredDevice, CameraFeatureDescriptor, CameraFeatureProfileRecord, CameraOutputPixelFormat, CameraSettings, CameraTriggerMode, CameraSynchronizationGroup, CameraSynchronizationGroupStatus, CameraSynchronizationCaptureResult, CameraSynchronizationRunRecord, CameraSynchronizationStatistics, CameraSynchronizationPtpDiagnostics, CameraSynchronizationCommissioningTest, GigENetworkDiagnostics, MediaCollectionDescriptor, MediaItemDescriptor, MediaLibraryStatus } from '../types';
import { localizeStatus } from '../i18n';

type Props = { open: boolean; onClose: () => void };

const stateColor: Record<string, string> = { Closed: 'default', Open: 'blue', Streaming: 'green', Faulted: 'red' };
const acquisitionColor: Record<string, string> = { Stopped: 'default', Starting: 'processing', Stopping: 'processing', Running: 'green', WaitingTrigger: 'gold', Reconnecting: 'orange', Faulted: 'red' };
const labelColor: Record<string, string> = { Unlabeled: 'default', OK: 'green', NG: 'red', Review: 'gold' };
const labelText: Record<string, string> = { Unlabeled: '未标注', OK: '合格', NG: '不合格', Review: '待复核' };
const formatBytes = (value: number) => value < 1024 ? `${value} B` : value < 1024 * 1024 ? `${(value / 1024).toFixed(1)} KiB` : `${(value / 1024 / 1024).toFixed(1)} MiB`;

async function responseError(response: Response, fallback: string) {
  try {
    const data = await response.json();
    return data.detail ?? data.error ?? fallback;
  } catch { return fallback; }
}

export default function CameraPanel({ open, onClose }: Props) {
  const [cameras, setCameras] = useState<CameraDescriptor[]>([]);
  const [adapters, setAdapters] = useState<CameraAdapterDescriptor[]>([]);
  const [discovered, setDiscovered] = useState<CameraDiscoveredDevice[]>([]);
  const [discoverDriver, setDiscoverDriver] = useState('basler-pylon');
  const [selectedDiscovered, setSelectedDiscovered] = useState<CameraDiscoveredDevice>();
  const [selectedId, setSelectedId] = useState('virtual-1');
  const [previewTick, setPreviewTick] = useState(0);
  const [busy, setBusy] = useState<string>();
  const [mediaStatus, setMediaStatus] = useState<MediaLibraryStatus>();
  const [collections, setCollections] = useState<MediaCollectionDescriptor[]>([]);
  const [mediaItems, setMediaItems] = useState<MediaItemDescriptor[]>([]);
  const [mediaCollection, setMediaCollection] = useState<string>();
  const [mediaLabel, setMediaLabel] = useState<string>();
  const [selectedMedia, setSelectedMedia] = useState<MediaItemDescriptor>();
  const [uploadFiles, setUploadFiles] = useState<UploadFile[]>([]);
  const [uploadCollection, setUploadCollection] = useState('offline');
  const [uploadLabel, setUploadLabel] = useState('Unlabeled');
  const [newCollection, setNewCollection] = useState('');
  const [form] = Form.useForm();
  const [vendorForm] = Form.useForm();
  const [settingsForm] = Form.useForm<CameraSettings>();
  const [commissioningForm] = Form.useForm<CameraCommissioningProfile>();
  const editedTriggerMode = Form.useWatch("triggerMode", settingsForm);
  const [commissioning, setCommissioning] = useState<CameraCommissioningSnapshot>();
  const [features, setFeatures] = useState<CameraFeatureDescriptor[]>([]);
  const [featureEdits, setFeatureEdits] = useState<Record<string, string>>({});
  const [featureProfiles, setFeatureProfiles] = useState<CameraFeatureProfileRecord[]>([]);
  const [selectedFeatureProfile, setSelectedFeatureProfile] = useState<string>();
  const [profileId, setProfileId] = useState('camera-profile-1');
  const [profileName, setProfileName] = useState('Commissioned Camera Profile');
  const [syncGroups, setSyncGroups] = useState<CameraSynchronizationGroup[]>([]);
  const [selectedSyncId, setSelectedSyncId] = useState<string>();
  const [syncStatus, setSyncStatus] = useState<CameraSynchronizationGroupStatus>();
  const [syncCapture, setSyncCapture] = useState<CameraSynchronizationCaptureResult>();
  const [syncRuns, setSyncRuns] = useState<CameraSynchronizationRunRecord[]>([]);
  const [syncStatistics, setSyncStatistics] = useState<CameraSynchronizationStatistics>();
  const [syncPtpDiagnostics, setSyncPtpDiagnostics] = useState<CameraSynchronizationPtpDiagnostics>();
  const [gigeDiagnostics, setGigEDiagnostics] = useState<GigENetworkDiagnostics>();
  const [syncTests, setSyncTests] = useState<CameraSynchronizationCommissioningTest[]>([]);
  const [syncTestIterations, setSyncTestIterations] = useState(100);
  const [syncTestScheduled, setSyncTestScheduled] = useState(false);
  const [syncForm] = Form.useForm();
  const [messageApi, contextHolder] = message.useMessage();

  const selected = useMemo(() => cameras.find((camera) => camera.id === selectedId), [cameras, selectedId]);

  const refresh = async (quiet = false) => {
    try {
      const response = await fetch('/api/cameras');
      if (!response.ok) throw new Error(await responseError(response, '相机接口不可用'));
      const data: CameraDescriptor[] = await response.json();
      setCameras(data);
      if (!data.some((x) => x.id === selectedId) && data.length > 0) setSelectedId(data[0].id);
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : '相机接口不可用');
    }
  };

  const refreshAdapters = async (quiet = false) => {
    try {
      const response = await fetch('/api/cameras/adapters');
      if (!response.ok) throw new Error(await responseError(response, '相机适配器接口不可用'));
      const data: CameraAdapterDescriptor[] = await response.json();
      setAdapters(data);
      if (!data.some((x) => x.driver === discoverDriver) && data.length > 0) setDiscoverDriver(data[0].driver);
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : '相机适配器接口不可用');
    }
  };

  const discoverVendor = async () => {
    if (!discoverDriver) return;
    setBusy('discover-vendor');
    try {
      const response = await fetch(`/api/cameras/discover?driver=${encodeURIComponent(discoverDriver)}`);
      if (!response.ok) throw new Error(await responseError(response, '发现相机失败'));
      const data: CameraDiscoveredDevice[] = await response.json();
      setDiscovered(data); setSelectedDiscovered(undefined); vendorForm.resetFields();
      if (data.length === 0) messageApi.info('此适配器未发现相机');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '发现相机失败'); }
    finally { setBusy(undefined); }
  };

  const selectDiscovered = (device: CameraDiscoveredDevice) => {
    setSelectedDiscovered(device);
    const suffix = (device.serialNumber || device.deviceKey).replace(/[^a-zA-Z0-9_-]/g, '').slice(-12).toLowerCase() || '1';
    const prefix = device.driver.startsWith('basler') ? 'basler' : 'hik';
    vendorForm.setFieldsValue({
      driver: device.driver, id: `${prefix}-${suffix}`, name: device.userDefinedName || `${device.vendor} ${device.model}`,
      serialNumber: device.serialNumber, userDefinedName: device.userDefinedName, deviceKey: device.deviceKey, ringCapacity: 4
    });
  };

  const registerVendor = async (values: { driver: string; id: string; name?: string; serialNumber?: string; userDefinedName?: string; deviceKey?: string; ringCapacity?: number }) => {
    setBusy('register-vendor');
    try {
      const response = await fetch('/api/cameras/vendor', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) });
      if (!response.ok) throw new Error(await responseError(response, '注册厂商相机失败'));
      const data: CameraDescriptor = await response.json();
      setSelectedId(data.id); await refresh(true); messageApi.success(`相机 ${data.id} 已注册`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '注册厂商相机失败'); }
    finally { setBusy(undefined); }
  };

  const refreshSyncGroups = async (quiet = false) => {
    try {
      const response = await fetch('/api/camera-sync/groups');
      if (!response.ok) throw new Error(await responseError(response, '相机同步接口不可用'));
      const data: CameraSynchronizationGroup[] = await response.json();
      setSyncGroups(data);
      const nextId = selectedSyncId && data.some((x) => x.id === selectedSyncId) ? selectedSyncId : data[0]?.id;
      if (nextId !== selectedSyncId) setSelectedSyncId(nextId);
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '相机同步接口不可用'); }
  };

  const refreshSyncStatus = async (quiet = false) => {
    if (!selectedSyncId) { setSyncStatus(undefined); return; }
    try {
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/status`);
      if (!response.ok) throw new Error(await responseError(response, '同步状态不可用'));
      setSyncStatus(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '同步状态不可用'); }
  };

  const refreshSyncRuns = async (quiet = false) => {
    if (!selectedSyncId) { setSyncRuns([]); return; }
    try {
      const response = await fetch(`/api/camera-sync/runs?groupId=${encodeURIComponent(selectedSyncId)}&limit=20`);
      if (!response.ok) throw new Error(await responseError(response, '同步历史记录不可用'));
      setSyncRuns(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '同步历史记录不可用'); }
  };

  const refreshSyncStatistics = async (quiet = false) => {
    if (!selectedSyncId) { setSyncStatistics(undefined); return; }
    try {
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/statistics?limit=100&minimumSamples=20`);
      if (!response.ok) throw new Error(await responseError(response, '同步统计不可用'));
      setSyncStatistics(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '同步统计不可用'); }
  };

  const refreshSyncPtpDiagnostics = async (quiet = false) => {
    if (!selectedSyncId) { setSyncPtpDiagnostics(undefined); return; }
    try {
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/ptp-diagnostics?limit=100`);
      if (!response.ok) throw new Error(await responseError(response, 'PTP 诊断不可用'));
      setSyncPtpDiagnostics(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : 'PTP 诊断不可用'); }
  };

  const refreshGigEDiagnostics = async (quiet = false) => {
    if (!selectedSyncId) { setGigEDiagnostics(undefined); return; }
    try {
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/gige-network-diagnostics?limit=100`);
      if (!response.ok) throw new Error(await responseError(response, 'GigE 网络诊断不可用'));
      setGigEDiagnostics(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : 'GigE 网络诊断不可用'); }
  };

  const startGigENetworkTest = async () => {
    if (!selectedSyncId) return;
    setBusy('gige-test-start');
    try {
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/gige-network-tests`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ iterations: syncTestIterations, scheduled: syncTestScheduled, frameTimeoutMs: 3000, delayMs: 50, minimumSamples: Math.min(20, syncTestIterations) }) });
      if (!response.ok) throw new Error(await responseError(response, '启动 GigE 网络测试失败'));
      await refreshSyncTests(true); messageApi.success(`GigE 网络测试已启动（${syncTestIterations} 个周期）`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '启动 GigE 网络测试失败'); }
    finally { setBusy(undefined); }
  };

  const exportGigENetworkReport = async (testId: string) => {
    const response = await fetch(`/api/camera-sync/gige-network-tests/${encodeURIComponent(testId)}/report`);
    if (!response.ok) { messageApi.error(await responseError(response, 'GigE 网络报告不可用')); return; }
    const report = await response.json();
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = `gige-network-commissioning-${testId}.json`; a.click(); URL.revokeObjectURL(url);
  };


  const refreshSyncTests = async (quiet = false) => {
    if (!selectedSyncId) { setSyncTests([]); return; }
    try {
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/commissioning-tests?limit=10`);
      if (!response.ok) throw new Error(await responseError(response, '调试测试记录不可用'));
      setSyncTests(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '调试测试记录不可用'); }
  };

  const startSyncTest = async () => {
    if (!selectedSyncId) return;
    setBusy('sync-test-start');
    try {
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/commissioning-tests`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ iterations: syncTestIterations, scheduled: syncTestScheduled, frameTimeoutMs: 3000, delayMs: 50, minimumSamples: Math.min(20, syncTestIterations) }) });
      if (!response.ok) throw new Error(await responseError(response, '启动调试测试失败'));
      await refreshSyncTests(true); messageApi.success(`调试测试已启动（${syncTestIterations} 轮）`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '启动调试测试失败'); }
    finally { setBusy(undefined); }
  };

  const cancelSyncTest = async (testId: string) => {
    const response = await fetch(`/api/camera-sync/commissioning-tests/${encodeURIComponent(testId)}/cancel`, { method: 'POST' });
    if (!response.ok) messageApi.error(await responseError(response, '取消调试测试失败'));
    await refreshSyncTests(true);
  };

  const exportSyncTestReport = async (testId: string) => {
    const response = await fetch(`/api/camera-sync/commissioning-tests/${encodeURIComponent(testId)}/report`);
    if (!response.ok) { messageApi.error(await responseError(response, '调试报告不可用')); return; }
    const report = await response.json();
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = `camera-sync-commissioning-${testId}.json`; a.click(); URL.revokeObjectURL(url);
  };

  const exportSyncTestHtml = async (testId: string) => {
    const response = await fetch(`/api/camera-sync/commissioning-tests/${encodeURIComponent(testId)}/report.html`);
    if (!response.ok) { messageApi.error(await responseError(response, '调试 HTML 报告不可用')); return; }
    const blob = await response.blob(); const url = URL.createObjectURL(blob); const a = document.createElement('a');
    a.href = url; a.download = `camera-sync-commissioning-${testId}.html`; a.click(); URL.revokeObjectURL(url);
  };

  const openSyncTestHtml = async (testId: string) => {
    const response = await fetch(`/api/camera-sync/commissioning-tests/${encodeURIComponent(testId)}/report.html`);
    if (!response.ok) { messageApi.error(await responseError(response, '调试 HTML 报告不可用')); return; }
    const blob = await response.blob(); const url = URL.createObjectURL(blob);
    const opened = window.open(url, '_blank', 'noopener,noreferrer');
    if (!opened) messageApi.warning('浏览器阻止了报告页面，请使用“导出 HTML 报告”下载。');
    window.setTimeout(() => URL.revokeObjectURL(url), 60000);
  };

  const saveSyncGroup = async (values: any) => {
    const id = String(values.id ?? '').trim();
    setBusy('sync-save');
    try {
      const payload = { ...values, id, cameraIds: values.cameraIds ?? [], groupMask: values.groupMask ?? -1 };
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(id)}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
      if (!response.ok) throw new Error(await responseError(response, 'Save synchronization group failed'));
      const saved: CameraSynchronizationGroup = await response.json();
      setSelectedSyncId(saved.id); await refreshSyncGroups(true); messageApi.success(`Synchronization group ${saved.name} saved`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Save synchronization group failed'); }
    finally { setBusy(undefined); }
  };

  const runSyncTrigger = async (scheduled: boolean) => {
    if (!selectedSyncId) return;
    setBusy(scheduled ? 'sync-schedule' : 'sync-trigger');
    try {
      const endpoint = scheduled ? 'schedule' : 'trigger';
      const response = await fetch(`/api/camera-sync/groups/${encodeURIComponent(selectedSyncId)}/${endpoint}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ scheduled, frameTimeoutMs: 3000 }) });
      if (!response.ok) throw new Error(await responseError(response, scheduled ? 'Scheduled Action Command failed' : 'Action Command failed'));
      const result: CameraSynchronizationCaptureResult = await response.json();
      setSyncCapture(result); await refreshSyncStatus(true); await refreshSyncRuns(true); await refreshSyncStatistics(true); await refreshSyncPtpDiagnostics(true);
      if (result.withinTolerance) messageApi.success(`Sync skew ${result.triggerSkewUs.toFixed(1)} µs`);
      else messageApi.warning(`Sync skew ${result.triggerSkewUs.toFixed(1)} µs exceeds ${result.maxAllowedSkewUs.toFixed(1)} µs`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Synchronization trigger failed'); }
    finally { setBusy(undefined); }
  };

  const refreshMedia = async (quiet = false) => {
    try {
      const query = new URLSearchParams();
      if (mediaCollection) query.set('collection', mediaCollection);
      if (mediaLabel) query.set('label', mediaLabel);
      query.set('take', '300');
      const [statusResponse, collectionsResponse, itemsResponse] = await Promise.all([
        fetch('/api/media/status'), fetch('/api/media/collections'), fetch(`/api/media/items?${query}`)
      ]);
      if (!statusResponse.ok || !collectionsResponse.ok || !itemsResponse.ok) throw new Error('Media Library API unavailable');
      setMediaStatus(await statusResponse.json());
      setCollections(await collectionsResponse.json());
      const items: MediaItemDescriptor[] = await itemsResponse.json();
      setMediaItems(items);
      if (selectedMedia && !items.some((x) => x.relativePath === selectedMedia.relativePath)) setSelectedMedia(undefined);
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : 'Media Library API unavailable');
    }
  };

  useEffect(() => {
    if (!open) return;
    refresh();
    refreshAdapters();
    refreshMedia();
    refreshSyncGroups();
    const timer = window.setInterval(() => refresh(true), 1000);
    return () => window.clearInterval(timer);
  }, [open]);

  useEffect(() => { if (open) refreshMedia(true); }, [mediaCollection, mediaLabel]);
  useEffect(() => { if (open) { refreshSyncStatus(true); refreshSyncRuns(true); refreshSyncStatistics(true); refreshSyncPtpDiagnostics(true); refreshGigEDiagnostics(true); refreshSyncTests(true); } }, [open, selectedSyncId]);
  useEffect(() => { if (!open || !selectedSyncId) return; const timer = window.setInterval(() => refreshSyncTests(true), 1000); return () => window.clearInterval(timer); }, [open, selectedSyncId]);
  useEffect(() => {
    const group = syncGroups.find((x) => x.id === selectedSyncId);
    if (group) syncForm.setFieldsValue(group);
  }, [selectedSyncId, syncGroups]);
  useEffect(() => { if (selected) settingsForm.setFieldsValue(selected.settings); }, [selected?.id, selected?.settings.exposureUs, selected?.settings.gainDb, selected?.settings.targetFps, selected?.settings.triggerMode, selected?.settings.outputPixelFormat, selected?.settings.externalTriggerSource]);
  useEffect(() => { if (open && selected) { refreshFeatureProfiles(true); if (selected.state !== 'Closed') refreshCommissioning(true); else { setCommissioning(undefined); setFeatures([]); } } }, [open, selected?.id, selected?.driver, selected?.state]);
  useEffect(() => {
    if (!open || !selectedId) return;
    const timer = window.setInterval(() => setPreviewTick((x) => x + 1), 500);
    return () => window.clearInterval(timer);
  }, [open, selectedId]);

  const command = async (id: string, action: 'open' | 'close' | 'start' | 'stop' | 'trigger') => {
    setBusy(`${id}:${action}`);
    try {
      const response = await fetch(`/api/cameras/${encodeURIComponent(id)}/${action}`, { method: 'POST' });
      if (!response.ok) throw new Error(await responseError(response, `${action} failed`));
      await refresh(true); setPreviewTick((x) => x + 1);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : `${action} failed`); }
    finally { setBusy(undefined); }
  };

  const saveSettings = async (values: CameraSettings) => {
    if (!selected) return;
    setBusy(`${selected.id}:settings`);
    try {
      const response = await fetch(`/api/cameras/${encodeURIComponent(selected.id)}/settings`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) });
      if (!response.ok) throw new Error(await responseError(response, 'Update settings failed'));
      await refresh(true); messageApi.success('Camera settings updated');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Update settings failed'); }
    finally { setBusy(undefined); }
  };

  const refreshCommissioning = async (quiet = false) => {
    if (!selected || !['basler-pylon', 'hikrobot-mvs'].includes(selected.driver)) {
      setCommissioning(undefined); setFeatures([]); return;
    }
    if (selected.state === 'Closed') {
      setCommissioning(undefined); setFeatures([]); return;
    }
    try {
      const [snapshotResponse, featuresResponse] = await Promise.all([
        fetch(`/api/cameras/${encodeURIComponent(selected.id)}/commissioning`),
        fetch(`/api/cameras/${encodeURIComponent(selected.id)}/features`)
      ]);
      if (!snapshotResponse.ok) throw new Error(await responseError(snapshotResponse, 'Commissioning profile unavailable'));
      if (!featuresResponse.ok) throw new Error(await responseError(featuresResponse, 'Feature browser unavailable'));
      const snapshot: CameraCommissioningSnapshot = await snapshotResponse.json();
      const featureList: CameraFeatureDescriptor[] = await featuresResponse.json();
      setCommissioning(snapshot); setFeatures(featureList);
      commissioningForm.setFieldsValue(snapshot.profile);
      setFeatureEdits(Object.fromEntries(featureList.map((x) => [x.key, x.value ?? ''])));
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : 'Commissioning API unavailable');
    }
  };

  const refreshFeatureProfiles = async (quiet = false) => {
    if (!selected || !['basler-pylon', 'hikrobot-mvs'].includes(selected.driver)) { setFeatureProfiles([]); return; }
    try {
      const response = await fetch(`/api/camera-feature-profiles?driver=${encodeURIComponent(selected.driver)}`);
      if (!response.ok) throw new Error(await responseError(response, 'Camera profiles unavailable'));
      const data: CameraFeatureProfileRecord[] = await response.json();
      setFeatureProfiles(data);
      if (selectedFeatureProfile && !data.some((x) => x.id === selectedFeatureProfile)) setSelectedFeatureProfile(undefined);
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : 'Camera profiles unavailable'); }
  };

  const applyCommissioning = async (values: CameraCommissioningProfile) => {
    if (!selected) return;
    setBusy(`${selected.id}:commissioning`);
    try {
      const payload: CameraCommissioningProfile = { ...values, schemaVersion: 1, acquisition: selected.settings };
      const response = await fetch(`/api/cameras/${encodeURIComponent(selected.id)}/commissioning`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
      });
      if (!response.ok) throw new Error(await responseError(response, 'Apply commissioning profile failed'));
      await refreshCommissioning(true); await refresh(true); messageApi.success('Commissioning profile applied');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Apply commissioning profile failed'); }
    finally { setBusy(undefined); }
  };

  const setFeature = async (feature: CameraFeatureDescriptor) => {
    if (!selected) return;
    setBusy(`feature:${feature.key}`);
    try {
      const response = await fetch(`/api/cameras/${encodeURIComponent(selected.id)}/features/${encodeURIComponent(feature.key)}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ value: featureEdits[feature.key] ?? '' })
      });
      if (!response.ok) throw new Error(await responseError(response, `Update ${feature.key} failed`));
      await refreshCommissioning(true); messageApi.success(`${feature.key} updated`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : `Update ${feature.key} failed`); }
    finally { setBusy(undefined); }
  };

  const captureFeatureProfile = async () => {
    if (!selected || !profileId.trim() || !profileName.trim()) return;
    setBusy('profile-capture');
    try {
      const response = await fetch('/api/camera-feature-profiles/capture', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: profileId.trim(), name: profileName.trim(), cameraId: selected.id })
      });
      if (!response.ok) throw new Error(await responseError(response, 'Save feature profile failed'));
      const saved: CameraFeatureProfileRecord = await response.json();
      setSelectedFeatureProfile(saved.id); await refreshFeatureProfiles(true); messageApi.success(`Saved ${saved.name}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Save feature profile failed'); }
    finally { setBusy(undefined); }
  };

  const applySavedFeatureProfile = async () => {
    if (!selected || !selectedFeatureProfile) return;
    setBusy('profile-apply');
    try {
      const response = await fetch(`/api/camera-feature-profiles/${encodeURIComponent(selectedFeatureProfile)}/apply/${encodeURIComponent(selected.id)}`, { method: 'POST' });
      if (!response.ok) throw new Error(await responseError(response, 'Apply saved profile failed'));
      await refreshCommissioning(true); await refresh(true); messageApi.success('Saved profile applied');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Apply saved profile failed'); }
    finally { setBusy(undefined); }
  };

  const deleteFeatureProfile = async () => {
    if (!selectedFeatureProfile) return;
    setBusy('profile-delete');
    try {
      const response = await fetch(`/api/camera-feature-profiles/${encodeURIComponent(selectedFeatureProfile)}`, { method: 'DELETE' });
      if (!response.ok) throw new Error(await responseError(response, 'Delete profile failed'));
      setSelectedFeatureProfile(undefined); await refreshFeatureProfiles(true); messageApi.success('Profile deleted');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Delete profile failed'); }
    finally { setBusy(undefined); }
  };

  const exportFeatureProfile = () => {
    const record = featureProfiles.find((x) => x.id === selectedFeatureProfile); if (!record) return;
    const blob = new Blob([JSON.stringify({ schema: 'visionstudio.camera-feature-profile', version: 1, ...record }, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob); const anchor = document.createElement('a'); anchor.href = url; anchor.download = `${record.id}.camera-profile.json`; anchor.click(); URL.revokeObjectURL(url);
  };

  const importFeatureProfileFile = async (file: File) => {
    setBusy('profile-import');
    try {
      const parsed = JSON.parse(await file.text());
      const body = {
        id: parsed.id ?? file.name.replace(/\.camera-profile\.json$|\.json$/i, '').replace(/[^a-zA-Z0-9_.-]/g, '-'),
        name: parsed.name ?? parsed.id ?? 'Imported Camera Profile',
        driver: parsed.driver ?? selected?.driver,
        sourceCameraId: parsed.sourceCameraId ?? null,
        profile: parsed.profile ?? parsed
      };
      if (!body.driver) throw new Error('Imported profile does not declare a camera driver');
      const response = await fetch('/api/camera-feature-profiles/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      if (!response.ok) throw new Error(await responseError(response, 'Import profile failed'));
      const saved: CameraFeatureProfileRecord = await response.json(); setSelectedFeatureProfile(saved.id); await refreshFeatureProfiles(true); messageApi.success(`Imported ${saved.name}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Import profile failed'); }
    finally { setBusy(undefined); }
  };

  const registerFile = async (values: { id: string; name?: string; source: string }) => {
    setBusy('register');
    try {
      const response = await fetch('/api/cameras/file', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) });
      if (!response.ok) throw new Error(await responseError(response, 'Register file camera failed'));
      const data: CameraDescriptor = await response.json();
      form.resetFields(); setSelectedId(data.id); await refresh(true); messageApi.success(`Camera ${data.id} registered`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Register file camera failed'); }
    finally { setBusy(undefined); }
  };

  const createCollection = async () => {
    if (!newCollection.trim()) return;
    setBusy('create-collection');
    try {
      const response = await fetch('/api/media/collections', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: newCollection.trim() }) });
      if (!response.ok) throw new Error(await responseError(response, 'Create collection failed'));
      setUploadCollection(newCollection.trim()); setNewCollection(''); await refreshMedia(true); messageApi.success('Media collection created');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Create collection failed'); }
    finally { setBusy(undefined); }
  };

  const importMedia = async () => {
    const nativeFiles = uploadFiles.map((x) => x.originFileObj).filter(Boolean);
    if (!uploadCollection.trim() || nativeFiles.length === 0) return;
    setBusy('media-import');
    try {
      const body = new FormData(); body.append('collection', uploadCollection.trim()); body.append('label', uploadLabel);
      nativeFiles.forEach((file) => body.append('files', file as Blob, (file as File).name));
      const response = await fetch('/api/media/import', { method: 'POST', body });
      if (!response.ok) throw new Error(await responseError(response, 'Media import failed'));
      const imported: MediaItemDescriptor[] = await response.json();
      setUploadFiles([]); setMediaCollection(uploadCollection.trim()); await refreshMedia(true); messageApi.success(`Imported ${imported.length} image(s)`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Media import failed'); }
    finally { setBusy(undefined); }
  };

  const deleteMedia = async (item: MediaItemDescriptor) => {
    setBusy(`delete:${item.relativePath}`);
    try {
      const response = await fetch(`/api/media/items?path=${encodeURIComponent(item.relativePath)}`, { method: 'DELETE' });
      if (!response.ok) throw new Error(await responseError(response, 'Delete media failed'));
      if (selectedMedia?.relativePath === item.relativePath) setSelectedMedia(undefined);
      await refreshMedia(true); messageApi.success('Media item deleted');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Delete media failed'); }
    finally { setBusy(undefined); }
  };

  const canTrigger = selected?.settings.triggerMode === 'Software' || (selected?.settings.triggerMode === 'External' && selected?.capabilities.hostSimulatedExternalTrigger);
  const sourceOptions = [
    ...collections.map((x) => ({ value: x.source, label: `${x.name} (${x.itemCount} images)` })),
    ...mediaItems.slice(0, 40).map((x) => ({ value: x.source, label: `${x.collection}/${x.label}/${x.name}` }))
  ];

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={1320} title="相机采集运行时与媒体库" destroyOnHidden>
      {contextHolder}
      <div className="camera-runtime-grid">
        <section className="camera-device-list">
          <Space style={{ marginBottom: 10 }} wrap>
            <Button onClick={() => refresh()}>刷新</Button><Tag color="cyan">连续采集工作进程</Tag><Tag color="geekblue">环形缓冲区 × 4</Tag><Tag color="purple">共享帧租约</Tag>
          </Space>
          <Table<CameraDescriptor> size="small" pagination={false} rowKey="id" dataSource={cameras} rowClassName={(row) => row.id === selectedId ? 'camera-selected-row' : ''} onRow={(row) => ({ onClick: () => setSelectedId(row.id) })} columns={[
            { title: '编号', dataIndex: 'id', width: 120 }, { title: '驱动', dataIndex: 'driver', width: 78 },
            { title: '设备状态', dataIndex: 'state', width: 90, render: (value: string) => <Tag color={stateColor[value] ?? 'default'}>{localizeStatus(value)}</Tag> },
            { title: '采集状态', width: 118, render: (_: unknown, row) => <Tag color={acquisitionColor[row.acquisition.acquisitionState] ?? 'default'}>{localizeStatus(row.acquisition.acquisitionState)}</Tag> },
            { title: '帧率', width: 64, render: (_: unknown, row) => row.acquisition.actualFps.toFixed(1) }, { title: '序号', width: 70, render: (_: unknown, row) => row.acquisition.lastSequence },
            { title: '丢帧', width: 60, render: (_: unknown, row) => row.acquisition.ringOverwrites + row.acquisition.driverDroppedFrames }, { title: '来源', dataIndex: 'source', ellipsis: true }
          ]} />

          <div className="camera-command-row">
            <Button disabled={!selected} title={!selected ? '请先在列表中选择一台相机' : undefined} loading={busy === `${selectedId}:open`} onClick={() => selected && command(selected.id, 'open')}>打开设备</Button>
            <Button type="primary" disabled={!selected} title={!selected ? '请先在列表中选择一台相机' : undefined} loading={busy === `${selectedId}:start`} onClick={() => selected && command(selected.id, 'start')}>开始采集</Button>
            <Button disabled={!selected} title={!selected ? '请先在列表中选择一台相机' : undefined} loading={busy === `${selectedId}:stop`} onClick={() => selected && command(selected.id, 'stop')}>停止采集</Button>
            <Button disabled={!selected} title={!selected ? '请先在列表中选择一台相机' : undefined} loading={busy === `${selectedId}:close`} onClick={() => selected && command(selected.id, 'close')}>关闭设备</Button>
            <Button
              disabled={!selected || !canTrigger}
              title={!selected ? '请先在列表中选择一台相机' : !canTrigger ? '连续采集模式下无需手动触发；如需手动触发请在“参数”中切换为软件触发或外部触发' : '立即触发一帧'}
              loading={busy === `${selectedId}:trigger`}
              onClick={() => selected && command(selected.id, 'trigger')}
            >触发采集</Button>
          </div>
          <div className="camera-command-hint">
            {!selected
              ? '请先在列表中选择一台相机'
              : !canTrigger
                ? `当前为${selected.settings.triggerMode === 'Continuous' ? '连续采集' : '外部触发'}模式，无法手动触发`
                : '操作按钮已按设备状态启用'}
          </div>

        </section>

        {/* 右列：实时预览常驻 + 按任务分标签（采集 / 参数 / 同步 / 诊断 / 设备注册 / 媒体库） */}
        <section className="camera-main-section">
          <div className="camera-preview-panel">
            <div className="camera-preview-title"><strong>{selected?.name ?? '未选择相机'}</strong><span>{selected?.id}</span></div>
            {selected && selected.acquisition.acquisitionState !== 'Stopped' && selected.acquisition.acquisitionState !== 'Faulted' ? <img key={`${selectedId}-${previewTick}`} src={`/api/cameras/${encodeURIComponent(selectedId)}/preview?maxWidth=960&quality=78&t=${previewTick}`} alt={`${selectedId} 预览`} onError={(event) => { event.currentTarget.style.opacity = '0.25'; }} onLoad={(event) => { event.currentTarget.style.opacity = '1'; }} /> : <div className="camera-preview-empty">开始采集后即可预览</div>}
            <Alert type="info" showIcon message="预览和工作流共用同一采集流" description="CameraAcquisitionWorker 只采集一次；网页预览和“采集图像”节点分别从有界 CameraFrameHub 获取独立帧租约。" />
          </div>
          <Tabs
            defaultActiveKey="acquisition"
            items={[
              { key: 'acquisition', label: '采集', children: (<>
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
            {selected.acquisition.runtimeError && <Alert type="warning" showIcon message={selected.acquisition.runtimeError} style={{ marginBottom: 8 }} />}
            {selected.acquisition.transport?.error && <Alert type="info" showIcon message={selected.acquisition.transport.error} style={{ marginBottom: 8 }} />}
          </> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="请在左侧选择一台相机" />}
              </>) },
              { key: 'parameters', label: '参数', children: (<>
          {/* ── 参数：厂商调试配置（触发 / 闪光灯 / 功能浏览器） ── */}
          {selected && ['basler-pylon', 'hikrobot-mvs'].includes(selected.driver) ? <>
            <Typography.Title level={5}>触发 / 闪光灯调试</Typography.Title>
            <Alert type="info" showIcon style={{ marginBottom: 10 }} message="修改传输或 I/O 功能前，请先停止采集" description="调试配置会适配 Basler/Hikrobot GenICam/MVS 的常见功能。对于不支持的功能，系统会跳过并报告，不会猜测配置值。" />
            {selected.state === 'Closed' ? <Alert type="warning" showIcon message="请先打开相机以读取调试功能" /> : commissioning && <>
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
              <div className="media-create-row" style={{ marginTop: 8 }}><Input value={profileId} onChange={(e) => setProfileId(e.target.value)} placeholder="配置 ID" /><Input value={profileName} onChange={(e) => setProfileName(e.target.value)} placeholder="配置名称" /><Button loading={busy === 'profile-capture'} onClick={captureFeatureProfile}>保存当前配置</Button></div>
              <Space wrap style={{ marginBottom: 10 }}>
                <Select style={{ minWidth: 280 }} allowClear placeholder="选择已保存的功能配置" value={selectedFeatureProfile} onChange={setSelectedFeatureProfile} options={featureProfiles.map((x) => ({ value: x.id, label: `${x.name} · ${x.profileHash.slice(0, 8)}` }))} />
                <Button disabled={!selectedFeatureProfile || selected.state !== 'Open'} loading={busy === 'profile-apply'} onClick={applySavedFeatureProfile}>应用已保存配置</Button>
                <Button disabled={!selectedFeatureProfile} onClick={exportFeatureProfile}>导出 JSON</Button>
                <Upload accept=".json" showUploadList={false} beforeUpload={(file) => { importFeatureProfileFile(file as File); return Upload.LIST_IGNORE; }}><Button loading={busy === 'profile-import'}>导入 JSON</Button></Upload>
                <Popconfirm title="确定删除所选相机配置吗？" okText="删除" cancelText="取消" onConfirm={deleteFeatureProfile}><Button danger disabled={!selectedFeatureProfile} loading={busy === 'profile-delete'}>删除</Button></Popconfirm>
              </Space>

              <Typography.Text strong>功能浏览器</Typography.Text>
              <Table<CameraFeatureDescriptor> size="small" rowKey="key" dataSource={features} pagination={{ pageSize: 8, size: 'small' }} columns={[
                { title: '类别', dataIndex: 'category', width: 105 },
                { title: '功能', dataIndex: 'displayName', width: 170, render: (_: unknown, row) => <><div>{row.displayName}</div><Typography.Text type="secondary" style={{ fontSize: 11 }}>{row.key}</Typography.Text></> },
                { title: '值', render: (_: unknown, row) => row.options?.length ? <Select size="small" style={{ width: '100%' }} value={featureEdits[row.key]} onChange={(value) => setFeatureEdits((old) => ({ ...old, [row.key]: value }))} options={row.options.map((value) => ({ value, label: value }))} /> : <Input size="small" value={featureEdits[row.key] ?? ''} suffix={row.unit ?? undefined} onChange={(e) => setFeatureEdits((old) => ({ ...old, [row.key]: e.target.value }))} /> },
                { title: '配置', width: 70, render: (_: unknown, row) => row.profileEligible ? <Tag color="green">可配置</Tag> : <Tag>只读</Tag> },
                { title: '', width: 64, render: (_: unknown, row) => <Button size="small" disabled={!row.writable || !row.profileEligible || selected.state !== 'Open'} loading={busy === `feature:${row.key}`} onClick={() => setFeature(row)}>设置</Button> }
              ]} />
            </>}
          </> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={selected ? '该相机驱动无厂商调试配置（仅 Basler / Hikrobot 支持）' : '请在左侧选择一台相机'} />}
              </>) },
              { key: 'sync', label: '同步', children: (<>
          <Typography.Title level={5}>多相机同步</Typography.Title>
          <Alert type="info" showIcon message="PTP + GigE Vision Action Command" description="创建使用相同驱动的同步组，为每台相机设置一致的 Action 密钥，将 TriggerSource 设为 Action1，然后发送立即或定时广播。系统优先使用设备 / PTP 时间戳计算偏差；设备时间戳不可用时才回退到主机接收时间。" style={{ marginBottom: 10 }} />
          <Space wrap style={{ marginBottom: 8 }}>
            <Select allowClear placeholder="选择已保存的同步组" value={selectedSyncId} onChange={setSelectedSyncId} style={{ minWidth: 240 }} options={syncGroups.map((x) => ({ value: x.id, label: `${x.name} · ${x.cameraIds.length} 台相机` }))} />
            <Button onClick={() => refreshSyncGroups()}>刷新分组</Button>
            <Button onClick={() => refreshSyncStatus()} disabled={!selectedSyncId}>刷新状态</Button>
            <Button type="primary" onClick={() => runSyncTrigger(false)} disabled={!selectedSyncId} loading={busy === 'sync-trigger'}>立即触发</Button>
            <Button onClick={() => runSyncTrigger(true)} disabled={!selectedSyncId} loading={busy === 'sync-schedule'}>定时触发</Button>
          </Space>
          {syncStatus && <>
            <Space wrap style={{ marginBottom: 8 }}>
              <Tag color={syncStatus.ready ? 'green' : 'red'}>{syncStatus.ready ? '就绪' : '未就绪'}</Tag>
              <Tag color={syncStatus.ptpReady ? 'green' : 'gold'}>PTP {syncStatus.ptpReady ? '已就绪' : '未锁定'}</Tag>
              <Tag>同步偏差 {syncStatus.latestSkewUs == null ? '—' : `${syncStatus.latestSkewUs.toFixed(1)} µs`} · {localizeStatus(syncStatus.timestampBasis)}</Tag>
              <Tag>配置哈希 {syncStatus.group.configurationHash.slice(0, 12)}</Tag>
            </Space>
            {syncStatus.error && <Alert type="warning" showIcon message={syncStatus.error} style={{ marginBottom: 8 }} />}
            <Table size="small" rowKey="cameraId" dataSource={syncStatus.members} pagination={false} columns={[
              { title: '相机', dataIndex: 'cameraId' },
              { title: 'PTP 状态', render: (_: unknown, row: any) => <Tag color={['Locked','Slave','Master'].includes(row.timeSync.state) ? 'green' : 'gold'}>{localizeStatus(row.timeSync.state)}</Tag> },
              { title: '时钟偏移', render: (_: unknown, row: any) => row.timeSync.offsetFromMasterNs == null ? '—' : `${row.timeSync.offsetFromMasterNs} ns` },
              { title: '帧序号', render: (_: unknown, row: any) => row.latestFrame ? `#${row.latestFrame.sequence}` : '—' }
            ]} />
          </>}
          {syncCapture && <Alert style={{ marginTop: 8 }} type={syncCapture.withinTolerance ? 'success' : 'warning'} showIcon message={`${syncCapture.scheduled ? '定时' : '立即'}采集 · 同步偏差 ${syncCapture.triggerSkewUs.toFixed(1)} µs（${localizeStatus(syncCapture.timestampBasis)}）`} description={`已确认设备数：${syncCapture.command.acknowledgedDevices}；允许偏差：${syncCapture.maxAllowedSkewUs.toFixed(1)} µs`} />}
          <Space wrap style={{ marginTop: 10, marginBottom: 6 }}><Typography.Text strong>同步调试</Typography.Text><Button size="small" onClick={() => refreshSyncStatistics()} disabled={!selectedSyncId}>刷新统计</Button></Space>
          {syncStatistics && <>
            <Alert
              showIcon
              type={syncStatistics.commissioning.status === 'Pass' ? 'success' : syncStatistics.commissioning.status === 'Fail' ? 'error' : 'info'}
              message={`调试评估：${syncStatistics.commissioning.status === 'InsufficientData' ? '数据不足' : localizeStatus(syncStatistics.commissioning.status)}`}
              description={syncStatistics.commissioning.reasons.join(' ')}
              style={{ marginBottom: 8 }}
            />
            <div className="camera-settings-grid">
              <Statistic title="样本数" value={syncStatistics.totalRuns} suffix={`/ ${syncStatistics.commissioning.minimumSamples} 个最低样本`} />
              <Statistic title="完成率" value={syncStatistics.completionRate * 100} precision={1} suffix="%" />
              <Statistic title="偏差合格率" value={syncStatistics.tolerancePassRate * 100} precision={1} suffix="%" />
              <Statistic title="偏差 P50" value={syncStatistics.p50SkewUs ?? 0} precision={1} suffix="µs" />
              <Statistic title="偏差 P95" value={syncStatistics.p95SkewUs ?? 0} precision={1} suffix="µs" />
              <Statistic title="偏差 P99" value={syncStatistics.p99SkewUs ?? 0} precision={1} suffix="µs" />
              <Statistic title="最大偏差" value={syncStatistics.maxSkewUs ?? 0} precision={1} suffix="µs" />
              <Statistic title="耗时 P95" value={syncStatistics.p95DurationMs ?? 0} precision={1} suffix="毫秒" />
            </div>
            <Typography.Text type="secondary">PTP 计时：{syncStatistics.devicePtpRuns} 次 · 主机接收时间回退：{syncStatistics.hostArrivalRuns} 次 · 失败：{syncStatistics.failedRuns} 次 · 已取消：{syncStatistics.cancelledRuns} 次</Typography.Text>
          </>}
              </>) },
              { key: 'diagnostics', label: '诊断', children: (<>
          {/* 底层采集计数器与厂商传输遥测：属于高级诊断，从主区域收起 */}
          {selected && <>
            <div className="camera-stats-grid">
              <Statistic title="已发布帧数" value={selected.acquisition.framesPublished} />
              <Statistic title="环形缓冲区覆盖数" value={selected.acquisition.ringOverwrites} />
              <Statistic title="驱动丢帧数" value={selected.acquisition.driverDroppedFrames} />
              <Statistic title="帧超时次数" value={selected.acquisition.frameTimeouts} />
              <Statistic title="采集错误数" value={selected.acquisition.acquisitionErrors} />
              <Statistic title="重连次数" value={selected.acquisition.reconnectCount} />
            </div>
            {selected.acquisition.transport && <>
              <Typography.Title level={5}>厂商传输遥测</Typography.Title>
              <Space wrap style={{ marginBottom: 8 }}>
                <Tag color={selected.acquisition.transport.native ? 'green' : 'default'}>{selected.acquisition.transport.native ? 'SDK 原生计数器' : '不可用'}</Tag>
                <Tag>{selected.acquisition.transport.source}</Tag>
                {selected.acquisition.transport.throughputMbps != null && <Tag color="blue">{selected.acquisition.transport.throughputMbps.toFixed(1)} Mbit/s</Tag>}
                {selected.acquisition.transport.receivedBytes != null && <Tag>{formatBytes(selected.acquisition.transport.receivedBytes)}</Tag>}
                {selected.acquisition.transport.receivedFrames != null && <Tag>接收帧数 {selected.acquisition.transport.receivedFrames}</Tag>}
                {selected.acquisition.transport.lostFrames != null && <Tag color={selected.acquisition.transport.lostFrames > 0 ? 'red' : 'green'}>丢失帧数 {selected.acquisition.transport.lostFrames}</Tag>}
                {selected.acquisition.transport.failedFrames != null && <Tag color={selected.acquisition.transport.failedFrames > 0 ? 'red' : 'green'}>失败帧数 {selected.acquisition.transport.failedFrames}</Tag>}
                {selected.acquisition.transport.bufferUnderruns != null && <Tag color={selected.acquisition.transport.bufferUnderruns > 0 ? 'red' : 'green'}>缓冲区欠载 {selected.acquisition.transport.bufferUnderruns}</Tag>}
                {selected.acquisition.transport.lostPackets != null && <Tag color={selected.acquisition.transport.lostPackets > 0 ? 'orange' : 'green'}>丢包数 {selected.acquisition.transport.lostPackets}</Tag>}
                {selected.acquisition.transport.failedPackets != null && <Tag color={selected.acquisition.transport.failedPackets > 0 ? 'orange' : 'green'}>失败包数 {selected.acquisition.transport.failedPackets}</Tag>}
                {selected.acquisition.transport.resendRequests != null && <Tag>重发请求数 {selected.acquisition.transport.resendRequests}</Tag>}
                {selected.acquisition.transport.resentPackets != null && <Tag>已重发包数 {selected.acquisition.transport.resentPackets}</Tag>}
                {selected.acquisition.transport.resynchronizations != null && <Tag color={selected.acquisition.transport.resynchronizations > 0 ? 'red' : 'green'}>重新同步次数 {selected.acquisition.transport.resynchronizations}</Tag>}
              </Space>
            </>}
          </>}
          <Space wrap style={{ marginTop: 10, marginBottom: 6 }}><Typography.Text strong>PTP 计时诊断</Typography.Text><Button size="small" onClick={() => refreshSyncPtpDiagnostics()} disabled={!selectedSyncId}>刷新 PTP</Button></Space>
          {syncPtpDiagnostics && <>
            <div className="camera-settings-grid">
              <Statistic title="PTP 证据运行数" value={syncPtpDiagnostics.runsWithPtpEvidence} />
              <Statistic title="PTP 未就绪次数" value={syncPtpDiagnostics.ptpNotReadyRuns} />
              <Statistic title="主时钟不一致次数" value={syncPtpDiagnostics.inconsistentMasterClockRuns} />
              <Statistic title="偏移绝对值 P95" value={syncPtpDiagnostics.p95MaxAbsOffsetNs ?? 0} precision={0} suffix="ns" />
              <Statistic title="最大偏移绝对值" value={syncPtpDiagnostics.maxAbsOffsetNs ?? 0} suffix="ns" />
              <Statistic title="偏移与同步偏差相关系数" value={syncPtpDiagnostics.offsetSkewPearsonCorrelation ?? 0} precision={3} />
            </div>
            <Table size="small" rowKey="cameraId" dataSource={syncPtpDiagnostics.cameras} pagination={false} columns={[
              { title: '相机', dataIndex: 'cameraId' },
              { title: 'PTP 就绪率', render: (_: unknown, row: any) => `${row.readySamples}/${row.samples} (${(row.readyRate * 100).toFixed(1)}%)` },
              { title: '偏移绝对值 P95', render: (_: unknown, row: any) => row.p95AbsOffsetNs == null ? '—' : `${row.p95AbsOffsetNs.toFixed(0)} ns` },
              { title: '最大偏移绝对值', render: (_: unknown, row: any) => row.maxAbsOffsetNs == null ? '—' : `${row.maxAbsOffsetNs} ns` },
              { title: '主时钟变更次数', dataIndex: 'masterClockChanges' }
            ]} />
          </>}

          <Space wrap style={{ marginTop: 12, marginBottom: 6 }}><Typography.Text strong>GigE 网络调试</Typography.Text><Button size="small" onClick={() => refreshGigEDiagnostics()} disabled={!selectedSyncId}>刷新网络诊断</Button><Button type="primary" size="small" onClick={startGigENetworkTest} disabled={!selectedSyncId || syncTests.some((x) => x.status === 'Queued' || x.status === 'Running')} loading={busy === 'gige-test-start'}>运行 {syncTestIterations} 轮 GigE 测试</Button></Space>
          {gigeDiagnostics && <>
            <Alert showIcon type={gigeDiagnostics.assessment.status === 'Pass' ? 'success' : gigeDiagnostics.assessment.status === 'Fail' ? 'error' : 'info'} message={`GigE 网络：${gigeDiagnostics.assessment.status === 'InsufficientData' ? '数据不足' : localizeStatus(gigeDiagnostics.assessment.status)}`} description={gigeDiagnostics.assessment.reasons.join(' ')} style={{ marginBottom: 8 }} />
            <div className="camera-settings-grid">
              <Statistic title="证据运行数" value={gigeDiagnostics.assessment.evidenceRuns} suffix={`/ ${gigeDiagnostics.assessment.minimumEvidenceRuns}`} />
              <Statistic title="网卡峰值负载" value={(gigeDiagnostics.assessment.peakNicUtilization ?? 0) * 100} precision={1} suffix="%" />
              <Statistic title="帧异常率" value={(gigeDiagnostics.assessment.aggregateFrameIssueRate ?? 0) * 100} precision={3} suffix="%" />
              <Statistic title="数据包异常率" value={(gigeDiagnostics.assessment.aggregatePacketIssueRate ?? 0) * 100} precision={3} suffix="%" />
              <Statistic title="重发请求率" value={(gigeDiagnostics.assessment.aggregateResendRequestRate ?? 0) * 100} precision={3} suffix="%" />
            </div>
            <Table size="small" rowKey="cameraId" dataSource={gigeDiagnostics.cameras} pagination={false} columns={[
              { title: '相机', dataIndex: 'cameraId' },
              { title: 'IP / 网卡', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => `${row.cameraIpAddress ?? '—'} / ${row.nicName ?? '未映射'}` },
              { title: '链路速率', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.nicSpeedMbps == null ? '—' : `${row.nicSpeedMbps.toFixed(0)} Mbit/s` },
              { title: '数据包大小', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.packetSizeBytes == null ? '—' : `${row.packetSizeBytes} 字节` },
              { title: '包间延迟', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.interPacketDelayTicks == null ? '—' : `${row.interPacketDelayTicks} 个时钟周期` },
              { title: '吞吐量', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => row.currentThroughputMbps == null ? '—' : `${row.currentThroughputMbps.toFixed(1)} Mbit/s` },
              { title: '遥测来源', render: (_: unknown, row: GigENetworkDiagnostics['cameras'][number]) => <Tag color={row.nativeTransportTelemetry ? 'green' : 'gold'}>{row.nativeTransportTelemetry ? '原生' : '回退'}</Tag> }
            ]} />
            {gigeDiagnostics.nicLoads.length > 0 && <Table size="small" rowKey="nicId" dataSource={gigeDiagnostics.nicLoads} pagination={false} columns={[
              { title: '网卡', dataIndex: 'nicName' },
              { title: '相机', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => row.cameraIds.join(', ') },
              { title: '链路速率 / MTU', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => `${row.speedMbps.toFixed(0)} Mbit/s / MTU ${row.mtuBytes ?? '—'}` },
              { title: '相机负载', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => `${row.currentCameraThroughputMbps.toFixed(1)} Mbit/s（${(row.currentUtilization * 100).toFixed(1)}%）` },
              { title: '状态', render: (_: unknown, row: GigENetworkDiagnostics['nicLoads'][number]) => <Tag color={row.level === 'Critical' ? 'red' : row.level === 'Warning' ? 'gold' : 'green'}>{localizeStatus(row.level)}</Tag> }
            ]} />}
            <Alert type="info" showIcon message="网络调优建议" description={gigeDiagnostics.assessment.recommendations.join(' ')} style={{ marginTop: 8 }} />
          </>}

          <Space wrap style={{ marginTop: 12, marginBottom: 6 }}><Typography.Text strong>自动调试测试</Typography.Text></Space>
          <Space wrap style={{ marginBottom: 8 }}>
            <Select value={syncTestIterations} onChange={setSyncTestIterations} style={{ width: 130 }} options={[20, 100, 500].map((value) => ({ value, label: `${value} 个周期` }))} />
            <Switch checked={syncTestScheduled} onChange={setSyncTestScheduled} checkedChildren="定时" unCheckedChildren="立即" />
            <Button type="primary" onClick={startSyncTest} disabled={!selectedSyncId || syncTests.some((x) => x.status === 'Queued' || x.status === 'Running')} loading={busy === 'sync-test-start'}>开始测试</Button>
            <Button size="small" onClick={() => refreshSyncTests()} disabled={!selectedSyncId}>刷新</Button>
          </Space>
          <Table<CameraSynchronizationCommissioningTest> size="small" rowKey="testId" dataSource={syncTests} pagination={{ pageSize: 4, size: 'small' }} columns={[
            { title: '开始时间', dataIndex: 'startedAt', width: 165, render: (value: string) => new Date(value).toLocaleString('zh-CN') },
            { title: '模式', width: 90, render: (_: unknown, row) => row.scheduled ? '定时' : '立即' },
            { title: '进度', width: 130, render: (_: unknown, row) => `${row.completedIterations} / ${row.requestedIterations}` },
            { title: '状态', width: 110, render: (_: unknown, row) => <Tag color={row.status === 'Completed' ? (row.report?.statistics.commissioning.passed ? 'green' : 'red') : row.status === 'Running' ? 'processing' : row.status === 'Failed' ? 'red' : 'default'}>{localizeStatus(row.status)}</Tag> },
            { title: '结果', render: (_: unknown, row) => row.report ? `${localizeStatus(row.report.statistics.commissioning.status)} · P99 ${row.report.statistics.p99SkewUs?.toFixed(1) ?? '—'} µs · 完成率 ${(row.report.statistics.completionRate * 100).toFixed(1)}%` : (row.error ?? '—') },
            { title: '操作', width: 210, render: (_: unknown, row) => <Space size={4}>{(row.status === 'Running' || row.status === 'Queued') && <Button size="small" danger onClick={() => cancelSyncTest(row.testId)}>取消</Button>}{row.report && <><Button size="small" onClick={() => openSyncTestHtml(row.testId)}>查看</Button><Button size="small" onClick={() => exportSyncTestHtml(row.testId)}>HTML</Button><Button size="small" onClick={() => exportSyncTestReport(row.testId)}>JSON</Button><Button size="small" onClick={() => exportGigENetworkReport(row.testId)}>GigE</Button></>}</Space> }
          ]} />

          <Space wrap style={{ marginTop: 10, marginBottom: 6 }}><Typography.Text strong>最近同步运行记录</Typography.Text><Button size="small" onClick={() => { refreshSyncRuns(); refreshSyncStatistics(true); refreshSyncPtpDiagnostics(true); refreshGigEDiagnostics(true); }} disabled={!selectedSyncId}>刷新历史</Button></Space>
          <Table<CameraSynchronizationRunRecord> size="small" rowKey="runId" dataSource={syncRuns} pagination={{ pageSize: 5, size: 'small' }} columns={[
            { title: '完成时间', dataIndex: 'completedAt', width: 175, render: (value: string) => new Date(value).toLocaleString('zh-CN') },
            { title: '来源', dataIndex: 'source', width: 90 },
            { title: '模式', width: 90, render: (_: unknown, row) => row.scheduled ? '定时' : '立即' },
            { title: '同步偏差', width: 110, render: (_: unknown, row) => row.triggerSkewUs == null ? '—' : `${row.triggerSkewUs.toFixed(1)} µs` },
            { title: '结果', width: 100, render: (_: unknown, row) => <Tag color={row.outcome === 'Completed' && row.withinTolerance ? 'green' : row.outcome === 'Completed' ? 'gold' : 'red'}>{row.outcome === 'Completed' ? (row.withinTolerance ? '在容差内' : '超出容差') : localizeStatus(row.outcome)}</Tag> },
            { title: 'PTP 就绪', width: 120, render: (_: unknown, row) => row.ptpSnapshots.length === 0 ? '—' : `${row.ptpSnapshots.filter((x) => ['Locked','Slave','Master'].includes(x.state)).length}/${row.ptpSnapshots.length} 台` },
            { title: '帧', render: (_: unknown, row) => row.frames.map((x) => `${x.cameraId}#${x.sequence}`).join(' · ') }
          ]} />

          <Form form={syncForm} layout="vertical" onFinish={saveSyncGroup} initialValues={{ id: 'sync-group-1', name: 'Synchronized Cameras', driver: 'basler-pylon', cameraIds: [], deviceKey: 1, groupKey: 1, groupMask: -1, broadcastAddress: '255.255.255.255', requirePtpLocked: true, maxPtpOffsetNs: 1000000, maxTriggerSkewUs: 100, scheduledLeadTimeMs: 100 }}>
            <div className="camera-file-form">
              <Form.Item label="分组 ID" name="id" rules={[{ required: true }]}><Input /></Form.Item>
              <Form.Item label="名称" name="name" rules={[{ required: true }]}><Input /></Form.Item>
            </div>
            <div className="camera-file-form">
              <Form.Item label="驱动" name="driver" rules={[{ required: true }]}><Select options={adapters.filter((x) => x.isSdkAvailable).map((x) => ({ value: x.driver, label: `${x.vendor} · ${x.driver}` }))} /></Form.Item>
              <Form.Item label="相机" name="cameraIds" rules={[{ required: true }]}><Select mode="multiple" options={cameras.filter((x) => x.driver.includes('basler') || x.driver.includes('hikrobot')).map((x) => ({ value: x.id, label: `${x.name} · ${x.driver}` }))} /></Form.Item>
            </div>
            <div className="camera-settings-grid">
              <Form.Item label="设备密钥" name="deviceKey"><InputNumber style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="组密钥" name="groupKey"><InputNumber style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="组掩码" name="groupMask"><InputNumber style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="定时提前量（毫秒）" name="scheduledLeadTimeMs"><InputNumber min={10} max={60000} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="最大 PTP 偏移（ns）" name="maxPtpOffsetNs"><InputNumber min={0} style={{ width: '100%' }} /></Form.Item>
              <Form.Item label="最大触发偏差（µs）" name="maxTriggerSkewUs"><InputNumber min={0.01} style={{ width: '100%' }} /></Form.Item>
            </div>
            <Form.Item label="广播地址" name="broadcastAddress"><Input /></Form.Item>
            <Form.Item label="要求 PTP 锁定" name="requirePtpLocked" valuePropName="checked"><Switch /></Form.Item>
            <Button htmlType="submit" loading={busy === 'sync-save'}>保存同步组</Button>
          </Form>

              </>) },
              { key: 'registration', label: '设备注册', children: (<>
          <Typography.Title level={5}>厂商相机</Typography.Title>
          <Alert type="info" showIcon message="运行时加载的 SDK 适配器" description="Basler pylon 和 Hikrobot MVS 将通过本机已安装的 SDK 进行发现。VisionStudio 不会重新分发厂商 DLL。" style={{ marginBottom: 10 }} />
          <Space wrap style={{ marginBottom: 8 }}>
            {adapters.map((adapter) => <Tag key={adapter.driver} color={adapter.isSdkAvailable ? 'green' : 'red'}>{adapter.vendor}：{adapter.isSdkAvailable ? 'SDK 已就绪' : 'SDK 不可用'}</Tag>)}
          </Space>
          <div className="media-filter-row">
            <Select value={discoverDriver} onChange={(value) => { setDiscoverDriver(value); setDiscovered([]); setSelectedDiscovered(undefined); }} options={adapters.map((x) => ({ value: x.driver, label: `${x.vendor} · ${x.driver}`, disabled: !x.isSdkAvailable }))} />
            <Button loading={busy === 'discover-vendor'} onClick={discoverVendor}>发现设备</Button>
          </div>
          {adapters.find((x) => x.driver === discoverDriver && !x.isSdkAvailable)?.sdkError && <Alert type="warning" showIcon message={adapters.find((x) => x.driver === discoverDriver)?.sdkError} style={{ marginBottom: 8 }} />}
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
          <Alert type="info" showIcon message="仅支持沙箱媒体来源" description="文件相机接受媒体库中的 media:// 引用。绝对主机路径、UNC 路径和 ../ 路径跳转均会被拒绝。" style={{ marginBottom: 10 }} />
          <Form form={form} layout="vertical" onFinish={registerFile} initialValues={{ id: 'file-1', name: 'File Camera 1' }}>
            <div className="camera-file-form"><Form.Item label="编号" name="id" rules={[{ required: true }]}><Input /></Form.Item><Form.Item label="名称" name="name"><Input /></Form.Item></div>
            <Form.Item label="媒体集合或图像" name="source" rules={[{ required: true }]}><Select showSearch options={sourceOptions} placeholder="media://offline 或已导入的图像" /></Form.Item>
            <Button htmlType="submit" loading={busy === 'register'}>注册</Button>
          </Form>

              </>) },
              { key: 'media', label: '媒体库', children: (<>
          <Typography.Title level={5}>媒体库</Typography.Title>
          <Space wrap style={{ marginBottom: 8 }}>
            <Tag color="blue">{mediaStatus?.collectionCount ?? 0} 个集合</Tag><Tag>{mediaStatus?.itemCount ?? 0} 张图像</Tag><Tag color="red">{mediaStatus?.ngItemCount ?? 0} 个 NG</Tag><Tag>{formatBytes(mediaStatus?.totalBytes ?? 0)} / {formatBytes(mediaStatus?.maxLibraryBytes ?? 0)}</Tag>
          </Space>
          <div className="media-create-row"><Input value={newCollection} onChange={(e) => setNewCollection(e.target.value)} placeholder="新建集合" onPressEnter={createCollection} /><Button loading={busy === 'create-collection'} onClick={createCollection}>创建</Button></div>
          <div className="media-upload-grid">
            <Select value={uploadCollection} onChange={setUploadCollection} options={collections.map((x) => ({ value: x.name, label: x.name }))} placeholder="选择集合" />
            <Select value={uploadLabel} onChange={setUploadLabel} options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))} />
          </div>
          <Upload.Dragger multiple accept=".bmp,.png,.jpg,.jpeg,.tif,.tiff" fileList={uploadFiles} beforeUpload={() => false} onChange={({ fileList }) => setUploadFiles(fileList)} onRemove={(file) => { setUploadFiles((list) => list.filter((x) => x.uid !== file.uid)); return true; }}>
            <p>将图像拖到此处，或点击选择文件</p><p className="ant-upload-hint">导入的图像将保存在配置的媒体根目录中。</p>
          </Upload.Dragger>
          <Button type="primary" style={{ marginTop: 8 }} disabled={!uploadCollection || uploadFiles.length === 0} loading={busy === 'media-import'} onClick={importMedia}>导入 {uploadFiles.length || ''} 张图像</Button>
          <div className="media-filter-row">
            <Select allowClear placeholder="全部集合" value={mediaCollection} onChange={setMediaCollection} options={collections.map((x) => ({ value: x.name, label: x.name }))} />
            <Select allowClear placeholder="全部标签" value={mediaLabel} onChange={setMediaLabel} options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))} />
            <Button onClick={() => refreshMedia()}>扫描</Button>
          </div>
          <Table<MediaItemDescriptor> size="small" rowKey="relativePath" dataSource={mediaItems} pagination={{ pageSize: 8, size: 'small' }} onRow={(row) => ({ onClick: () => setSelectedMedia(row) })} rowClassName={(row) => row.relativePath === selectedMedia?.relativePath ? 'camera-selected-row' : ''} columns={[
            { title: '标签', dataIndex: 'label', width: 86, render: (value: string) => <Tag color={labelColor[value]}>{labelText[value] ?? value}</Tag> },
            { title: '图像', dataIndex: 'name', ellipsis: true }, { title: '集合', dataIndex: 'collection', width: 110 }, { title: '大小', width: 80, render: (_: unknown, row) => formatBytes(row.bytes) },
            { title: '', width: 70, render: (_: unknown, row) => <Popconfirm title="确定删除此媒体项吗？" okText="删除" cancelText="取消" onConfirm={() => deleteMedia(row)}><Button size="small" danger loading={busy === `delete:${row.relativePath}`}>删除</Button></Popconfirm> }
          ]} />

          {/* 所选媒体预览：与媒体库同属一个任务上下文 */}
          <div className="media-preview-panel">
            <div className="media-preview-title"><strong>所选媒体</strong><span>{selectedMedia?.source ?? '—'}</span></div>
            {selectedMedia ? <img className="media-preview-image" src={`/api/media/preview?path=${encodeURIComponent(selectedMedia.relativePath)}`} alt={selectedMedia.name} /> : <div className="media-preview-empty">选择最近样本或 NG 样本进行预览</div>}
            {selectedMedia && <Space wrap><Tag color={labelColor[selectedMedia.label]}>{labelText[selectedMedia.label] ?? selectedMedia.label}</Tag><Button size="small" onClick={() => form.setFieldsValue({ source: selectedMedia.source })}>用作文件相机来源</Button></Space>}
          </div>
              </>) }
            ]}
          />
        </section>
      </div>
    </Modal>
  );
}
