import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Empty, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Switch, Table, Tabs, Tag, Typography, Upload, message } from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import type { CameraAdapterDescriptor, CameraCommissioningProfile, CameraCommissioningSnapshot, CameraDescriptor, CameraDiscoveredDevice, CameraFeatureDescriptor, CameraFeatureProfileRecord, CameraSettings, CameraSynchronizationGroup, CameraSynchronizationGroupStatus, CameraSynchronizationCaptureResult, CameraSynchronizationRunRecord, CameraSynchronizationStatistics, CameraSynchronizationPtpDiagnostics, CameraSynchronizationCommissioningTest, GigENetworkDiagnostics, MediaCollectionDescriptor, MediaItemDescriptor, MediaLibraryStatus } from '../types';
import { localizeStatus } from '../i18n';
import { responseErrorMessage } from '../apiErrors';
import { apiFetch, cachedGet, invalidateCache, RequestToken } from '../apiClient';
import { apiUrl, cameras as cameraEndpoints, cameraSync, cameraFeatureProfiles, media } from '../endpoints';
import { useMonitorSnapshot } from '../useMonitorStream';
import CameraAcquisitionTab from './CameraAcquisitionTab';
import CameraDiagnosticsTab from './CameraDiagnosticsTab';
import CameraMediaTab from './CameraMediaTab';
import CameraRegistrationTab from './CameraRegistrationTab';
import CameraSyncTab from './CameraSyncTab';
import { acquisitionColor, stateColor, type SyncGroupFormValues } from './cameraPanelShared';

// R04：轮询超时——请求挂起时必须暴露失败，而不是让旧数据静默伪装成最新。
const CAMERA_POLL_TIMEOUT_MS = 5000;

type Props = { open: boolean; onClose: () => void };

// 2026-11-13：五个标签页已抽成独立组件（采集/同步/诊断/设备注册/媒体库），
// 表单值类型、媒体展示常量与状态色统一放在 cameraPanelShared.ts，避免两处口径漂移。
// 「参数」页仍内联：它把 selected + commissioningForm + 功能浏览器三者的状态绑在一起，
// 抽出去要传 20+ 个回调，反而更难读。

async function responseError(response: Response, fallback: string) {
  try {
    const data = await response.json();
    return data.detail ?? data.error ?? fallback;
  } catch { return fallback; }
}

export default function CameraPanel({ open, onClose }: Props) {
  const monitorSnapshot = useMonitorSnapshot(open);
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
  const [mediaTotal, setMediaTotal] = useState(0);
  const [mediaPage, setMediaPage] = useState(1);
  const [mediaPageSize, setMediaPageSize] = useState(20);
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

  // R04：轮询代次 + 取消 + 超时——旧响应绝不覆盖新结果，挂起请求可被显式中断。
  // 轮询 token 仅在 refresh 作用域内使用（超时触发取消），无需组件级引用。
  const refreshGeneration = useRef(0);
  const refreshInFlight = useRef(false);

  const refresh = async (quiet = false) => {
    // F04：单次刷新完成前跳过本轮 tick——不取消仍在进行的请求（否则慢但健康的接口永远拿不到结果）。
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    const generation = ++refreshGeneration.current;
    const token = new RequestToken();
    const timeout = window.setTimeout(() => token.cancel(), CAMERA_POLL_TIMEOUT_MS);
    try {
      const response = await apiFetch(cameraEndpoints.list, { signal: token.signal });
      if (generation !== refreshGeneration.current) return; // 旧响应：丢弃
      if (!response.ok) throw new Error(await responseErrorMessage(response, '相机接口不可用'));
      const data: CameraDescriptor[] = await response.json();
      // C06：body 解析后再核对一次代次，迟到 body 不得覆盖新状态。
      if (generation !== refreshGeneration.current) return;
      setCameras(data);
      if (!data.some((x) => x.id === selectedId) && data.length > 0) setSelectedId(data[0].id);
    } catch (error) {
      if (generation !== refreshGeneration.current) return;
      if (!quiet) messageApi.error(error instanceof Error ? error.message : '相机接口不可用');
    } finally {
      window.clearTimeout(timeout);
      refreshInFlight.current = false;
    }
  };

  const refreshAdapters = async (quiet = false) => {
    try {
      const data = await cachedGet<CameraAdapterDescriptor[]>(cameraEndpoints.adapters, { ttlMs: 60_000, errorLabel: '相机适配器接口不可用' });
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
      const response = await apiFetch(cameraEndpoints.discover(discoverDriver));
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
      const response = await apiFetch(apiUrl('cameras/vendor'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) });
      if (!response.ok) throw new Error(await responseError(response, '注册厂商相机失败'));
      const data: CameraDescriptor = await response.json();
      setSelectedId(data.id); await refresh(true); messageApi.success(`相机 ${data.id} 已注册`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '注册厂商相机失败'); }
    finally { setBusy(undefined); }
  };

  const refreshSyncGroups = async (quiet = false) => {
    try {
      const response = await apiFetch(apiUrl('camera-sync/groups'));
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
      const response = await apiFetch(cameraSync.groupStatus(selectedSyncId));
      if (!response.ok) throw new Error(await responseError(response, '同步状态不可用'));
      setSyncStatus(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '同步状态不可用'); }
  };

  const refreshSyncRuns = async (quiet = false) => {
    if (!selectedSyncId) { setSyncRuns([]); return; }
    try {
      const response = await apiFetch(cameraSync.runs(selectedSyncId));
      if (!response.ok) throw new Error(await responseError(response, '同步历史记录不可用'));
      setSyncRuns(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '同步历史记录不可用'); }
  };

  const refreshSyncStatistics = async (quiet = false) => {
    if (!selectedSyncId) { setSyncStatistics(undefined); return; }
    try {
      const response = await apiFetch(cameraSync.groupStatistics(selectedSyncId));
      if (!response.ok) throw new Error(await responseError(response, '同步统计不可用'));
      setSyncStatistics(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '同步统计不可用'); }
  };

  const refreshSyncPtpDiagnostics = async (quiet = false) => {
    if (!selectedSyncId) { setSyncPtpDiagnostics(undefined); return; }
    try {
      const response = await apiFetch(cameraSync.groupPtpDiagnostics(selectedSyncId));
      if (!response.ok) throw new Error(await responseError(response, 'PTP 诊断不可用'));
      setSyncPtpDiagnostics(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : 'PTP 诊断不可用'); }
  };

  const refreshGigEDiagnostics = async (quiet = false) => {
    if (!selectedSyncId) { setGigEDiagnostics(undefined); return; }
    try {
      const response = await apiFetch(cameraSync.groupGigeDiagnostics(selectedSyncId));
      if (!response.ok) throw new Error(await responseError(response, 'GigE 网络诊断不可用'));
      setGigEDiagnostics(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : 'GigE 网络诊断不可用'); }
  };

  const startGigENetworkTest = async () => {
    if (!selectedSyncId) return;
    setBusy('gige-test-start');
    try {
      const response = await apiFetch(cameraSync.groupGigeTests(selectedSyncId), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ iterations: syncTestIterations, scheduled: syncTestScheduled, frameTimeoutMs: 3000, delayMs: 50, minimumSamples: Math.min(20, syncTestIterations) }) });
      if (!response.ok) throw new Error(await responseError(response, '启动 GigE 网络测试失败'));
      await refreshSyncTests(true); messageApi.success(`GigE 网络测试已启动（${syncTestIterations} 个周期）`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '启动 GigE 网络测试失败'); }
    finally { setBusy(undefined); }
  };

  const exportGigENetworkReport = async (testId: string) => {
    const response = await apiFetch(cameraSync.gigeTestReport(testId));
    if (!response.ok) { messageApi.error(await responseError(response, 'GigE 网络报告不可用')); return; }
    const report = await response.json();
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = `gige-network-commissioning-${testId}.json`; a.click(); URL.revokeObjectURL(url);
  };


  const refreshSyncTests = async (quiet = false) => {
    if (!selectedSyncId) { setSyncTests([]); return; }
    try {
      const response = await apiFetch(cameraSync.groupCommissioningTests(selectedSyncId, 'limit=10'));
      if (!response.ok) throw new Error(await responseError(response, '调试测试记录不可用'));
      setSyncTests(await response.json());
    } catch (error) { if (!quiet) messageApi.error(error instanceof Error ? error.message : '调试测试记录不可用'); }
  };

  const startSyncTest = async () => {
    if (!selectedSyncId) return;
    setBusy('sync-test-start');
    try {
      const response = await apiFetch(cameraSync.groupCommissioningTests(selectedSyncId), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ iterations: syncTestIterations, scheduled: syncTestScheduled, frameTimeoutMs: 3000, delayMs: 50, minimumSamples: Math.min(20, syncTestIterations) }) });
      if (!response.ok) throw new Error(await responseError(response, '启动调试测试失败'));
      await refreshSyncTests(true); messageApi.success(`调试测试已启动（${syncTestIterations} 轮）`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '启动调试测试失败'); }
    finally { setBusy(undefined); }
  };

  const cancelSyncTest = async (testId: string) => {
    const response = await apiFetch(cameraSync.commissioningTestCancel(testId), { method: 'POST' });
    if (!response.ok) messageApi.error(await responseError(response, '取消调试测试失败'));
    await refreshSyncTests(true);
  };

  const exportSyncTestReport = async (testId: string) => {
    const response = await apiFetch(cameraSync.commissioningTestReport(testId));
    if (!response.ok) { messageApi.error(await responseError(response, '调试报告不可用')); return; }
    const report = await response.json();
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = `camera-sync-commissioning-${testId}.json`; a.click(); URL.revokeObjectURL(url);
  };

  const exportSyncTestHtml = async (testId: string) => {
    const response = await apiFetch(cameraSync.commissioningTestReportHtml(testId));
    if (!response.ok) { messageApi.error(await responseError(response, '调试 HTML 报告不可用')); return; }
    const blob = await response.blob(); const url = URL.createObjectURL(blob); const a = document.createElement('a');
    a.href = url; a.download = `camera-sync-commissioning-${testId}.html`; a.click(); URL.revokeObjectURL(url);
  };

  const openSyncTestHtml = async (testId: string) => {
    const response = await apiFetch(cameraSync.commissioningTestReportHtml(testId));
    if (!response.ok) { messageApi.error(await responseError(response, '调试 HTML 报告不可用')); return; }
    const blob = await response.blob(); const url = URL.createObjectURL(blob);
    const opened = window.open(url, '_blank', 'noopener,noreferrer');
    if (!opened) messageApi.warning('浏览器阻止了报告页面，请使用“导出 HTML 报告”下载。');
    window.setTimeout(() => URL.revokeObjectURL(url), 60000);
  };

  const saveSyncGroup = async (values: SyncGroupFormValues) => {
    const id = String(values.id ?? '').trim();
    setBusy('sync-save');
    try {
      const payload = { ...values, id, cameraIds: values.cameraIds ?? [], groupMask: values.groupMask ?? -1 };
      const response = await apiFetch(cameraSync.group(id), { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
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
      const response = await apiFetch(cameraSync.groupAction(selectedSyncId, endpoint), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ scheduled, frameTimeoutMs: 3000 }) });
      if (!response.ok) throw new Error(await responseError(response, scheduled ? 'Scheduled Action Command failed' : 'Action Command failed'));
      const result: CameraSynchronizationCaptureResult = await response.json();
      setSyncCapture(result); await refreshSyncStatus(true); await refreshSyncRuns(true); await refreshSyncStatistics(true); await refreshSyncPtpDiagnostics(true);
      if (result.withinTolerance) messageApi.success(`Sync skew ${result.triggerSkewUs.toFixed(1)} µs`);
      else messageApi.warning(`Sync skew ${result.triggerSkewUs.toFixed(1)} µs exceeds ${result.maxAllowedSkewUs.toFixed(1)} µs`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Synchronization trigger failed'); }
    finally { setBusy(undefined); }
  };

  const refreshMedia = async (quiet = false, page = mediaPage, pageSize = mediaPageSize) => {
    try {
      const query = new URLSearchParams();
      if (mediaCollection) query.set('collection', mediaCollection);
      if (mediaLabel) query.set('label', mediaLabel);
      query.set('skip', String(Math.max(0, page - 1) * pageSize));
      query.set('take', String(pageSize));
      const [statusResponse, collectionsResponse, itemsResponse] = await Promise.all([
        apiFetch(media.status), apiFetch(media.collections), apiFetch(media.itemsQuery(query.toString()))
      ]);
      if (!statusResponse.ok || !collectionsResponse.ok || !itemsResponse.ok) throw new Error('Media Library API unavailable');
      setMediaStatus(await statusResponse.json());
      setCollections(await collectionsResponse.json());
      const items: MediaItemDescriptor[] = await itemsResponse.json();
      const rawTotal = itemsResponse.headers.get('X-Total-Count');
      const parsedTotal = rawTotal === null ? Number.NaN : Number(rawTotal);
      const total = Number.isFinite(parsedTotal) ? parsedTotal : items.length;
      const lastPage = Math.max(1, Math.ceil(total / pageSize));
      if (page > lastPage) {
        // 删除当前页最后一项后页码可能越界：收到总数后回退到最后一页重新取。
        setMediaPage(lastPage);
        await refreshMedia(quiet, lastPage, pageSize);
        return;
      }
      setMediaTotal(total);
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
    // 2026-11-12：1 s 轮询改为推送触发（服务端内容变化才广播），定时器只做断线兜底。
    const timer = window.setInterval(() => refresh(true), 10_000);
    return () => window.clearInterval(timer);
  }, [open]);

  // 相机列表/状态变化时立即刷新；预览仍由本地 500 ms tick 驱动，不经过网络。
  useEffect(() => {
    if (monitorSnapshot) void refresh(true);
  }, [monitorSnapshot?.capturedAt]);

  useEffect(() => { if (open) { setMediaPage(1); void refreshMedia(true, 1, mediaPageSize); } }, [mediaCollection, mediaLabel]);
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
      const response = await apiFetch(cameraEndpoints.action(id, action), { method: 'POST' });
      if (!response.ok) throw new Error(await responseError(response, `${action} failed`));
      await refresh(true); setPreviewTick((x) => x + 1);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : `${action} failed`); }
    finally { setBusy(undefined); }
  };

  const saveSettings = async (values: CameraSettings) => {
    if (!selected) return;
    setBusy(`${selected.id}:settings`);
    try {
      const response = await apiFetch(cameraEndpoints.settings(selected.id), { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) });
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
        apiFetch(cameraEndpoints.commissioning(selected.id)),
        apiFetch(cameraEndpoints.features(selected.id))
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
      const response = await apiFetch(cameraFeatureProfiles.byDriver(selected.driver));
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
      const response = await apiFetch(cameraEndpoints.commissioning(selected.id), {
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
      const response = await apiFetch(cameraEndpoints.feature(selected.id, feature.key), {
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
      const response = await apiFetch(apiUrl('camera-feature-profiles/capture'), {
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
      const response = await apiFetch(cameraFeatureProfiles.apply(selectedFeatureProfile, selected.id), { method: 'POST' });
      if (!response.ok) throw new Error(await responseError(response, 'Apply saved profile failed'));
      await refreshCommissioning(true); await refresh(true); messageApi.success('Saved profile applied');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Apply saved profile failed'); }
    finally { setBusy(undefined); }
  };

  const deleteFeatureProfile = async () => {
    if (!selectedFeatureProfile) return;
    setBusy('profile-delete');
    try {
      const response = await apiFetch(cameraFeatureProfiles.byId(selectedFeatureProfile), { method: 'DELETE' });
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
      const response = await apiFetch(apiUrl('camera-feature-profiles/import'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      if (!response.ok) throw new Error(await responseError(response, 'Import profile failed'));
      const saved: CameraFeatureProfileRecord = await response.json(); setSelectedFeatureProfile(saved.id); await refreshFeatureProfiles(true); messageApi.success(`Imported ${saved.name}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Import profile failed'); }
    finally { setBusy(undefined); }
  };

  const registerFile = async (values: { id: string; name?: string; source: string }) => {
    setBusy('register');
    try {
      const response = await apiFetch(apiUrl('cameras/file'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) });
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
      const response = await apiFetch(apiUrl('media/collections'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: newCollection.trim() }) });
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
      const response = await apiFetch(apiUrl('media/import'), { method: 'POST', body });
      if (!response.ok) throw new Error(await responseError(response, 'Media import failed'));
      const imported: MediaItemDescriptor[] = await response.json();
      setUploadFiles([]); setMediaCollection(uploadCollection.trim()); setMediaPage(1); await refreshMedia(true, 1, mediaPageSize); messageApi.success(`Imported ${imported.length} image(s)`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Media import failed'); }
    finally { setBusy(undefined); }
  };

  const deleteMedia = async (item: MediaItemDescriptor) => {
    setBusy(`delete:${item.relativePath}`);
    try {
      const response = await apiFetch(media.itemByPath(item.relativePath), { method: 'DELETE' });
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
            <Button onClick={() => { refresh(); invalidateCache(cameraEndpoints.adapters); refreshAdapters(); }}>刷新</Button><Tag color="cyan">连续采集工作进程</Tag><Tag color="geekblue">环形缓冲区 × 4</Tag><Tag color="purple">共享帧租约</Tag>
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
            {selected && selected.acquisition.acquisitionState !== 'Stopped' && selected.acquisition.acquisitionState !== 'Faulted' ? <img key={`${selectedId}-${previewTick}`} src={cameraEndpoints.preview(selectedId, `maxWidth=960&quality=78&t=${previewTick}`)} alt={`${selectedId} 预览`} onError={(event) => { event.currentTarget.style.opacity = '0.25'; }} onLoad={(event) => { event.currentTarget.style.opacity = '1'; }} /> : <div className="camera-preview-empty">开始采集后即可预览</div>}
            <Alert type="info" showIcon title="预览和工作流共用同一采集流" description="CameraAcquisitionWorker 只采集一次；网页预览和“采集图像”节点分别从有界 CameraFrameHub 获取独立帧租约。" />
          </div>
          <Tabs
            defaultActiveKey="acquisition"
            items={[
              { key: 'acquisition', label: '采集', children: <CameraAcquisitionTab
                busy={busy}
                selected={selected}
                settingsForm={settingsForm}
                saveSettings={saveSettings}
                editedTriggerMode={editedTriggerMode}
              /> },
              { key: 'parameters', label: '参数', children: (<>
          {/* ── 参数：厂商调试配置（触发 / 闪光灯 / 功能浏览器） ── */}
          {selected && ['basler-pylon', 'hikrobot-mvs'].includes(selected.driver) ? <>
            <Typography.Title level={5}>触发 / 闪光灯调试</Typography.Title>
            <Alert type="info" showIcon style={{ marginBottom: 10 }} title="修改传输或 I/O 功能前，请先停止采集" description="调试配置会适配 Basler/Hikrobot GenICam/MVS 的常见功能。对于不支持的功能，系统会跳过并报告，不会猜测配置值。" />
            {selected.state === 'Closed' ? <Alert type="warning" showIcon title="请先打开相机以读取调试功能" /> : commissioning && <>
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
              { key: 'sync', label: '同步', children: <CameraSyncTab
                busy={busy}
                selectedSyncId={selectedSyncId}
                setSelectedSyncId={setSelectedSyncId}
                syncGroups={syncGroups}
                refreshSyncGroups={refreshSyncGroups}
                refreshSyncStatus={refreshSyncStatus}
                runSyncTrigger={runSyncTrigger}
                syncStatus={syncStatus}
                syncCapture={syncCapture}
                syncStatistics={syncStatistics}
                refreshSyncStatistics={refreshSyncStatistics}
              /> },
              { key: 'diagnostics', label: '诊断', children: <CameraDiagnosticsTab
                busy={busy}
                selected={selected}
                adapters={adapters}
                cameras={cameras}
                selectedSyncId={selectedSyncId}
                syncForm={syncForm}
                saveSyncGroup={saveSyncGroup}
                syncPtpDiagnostics={syncPtpDiagnostics}
                refreshSyncPtpDiagnostics={refreshSyncPtpDiagnostics}
                gigeDiagnostics={gigeDiagnostics}
                refreshGigEDiagnostics={refreshGigEDiagnostics}
                startGigENetworkTest={startGigENetworkTest}
                syncTestIterations={syncTestIterations}
                setSyncTestIterations={setSyncTestIterations}
                syncTests={syncTests}
                syncTestScheduled={syncTestScheduled}
                setSyncTestScheduled={setSyncTestScheduled}
                startSyncTest={startSyncTest}
                refreshSyncTests={refreshSyncTests}
                cancelSyncTest={cancelSyncTest}
                openSyncTestHtml={openSyncTestHtml}
                exportSyncTestHtml={exportSyncTestHtml}
                exportSyncTestReport={exportSyncTestReport}
                exportGigENetworkReport={exportGigENetworkReport}
                syncRuns={syncRuns}
                refreshSyncRuns={refreshSyncRuns}
                refreshSyncStatistics={refreshSyncStatistics}
              /> },
              { key: 'registration', label: '设备注册', children: <CameraRegistrationTab
                busy={busy}
                adapters={adapters}
                discoverDriver={discoverDriver}
                setDiscoverDriver={setDiscoverDriver}
                setDiscovered={setDiscovered}
                setSelectedDiscovered={setSelectedDiscovered}
                discoverVendor={discoverVendor}
                discovered={discovered}
                selectDiscovered={selectDiscovered}
                selectedDiscovered={selectedDiscovered}
                vendorForm={vendorForm}
                registerVendor={registerVendor}
                form={form}
                registerFile={registerFile}
                sourceOptions={sourceOptions}
              /> },
              { key: 'media', label: '媒体库', children: <CameraMediaTab
                busy={busy}
                mediaStatus={mediaStatus}
                newCollection={newCollection}
                setNewCollection={setNewCollection}
                createCollection={createCollection}
                uploadCollection={uploadCollection}
                setUploadCollection={setUploadCollection}
                collections={collections}
                uploadLabel={uploadLabel}
                setUploadLabel={setUploadLabel}
                uploadFiles={uploadFiles}
                setUploadFiles={setUploadFiles}
                importMedia={importMedia}
                deleteMedia={deleteMedia}
                mediaCollection={mediaCollection}
                setMediaCollection={setMediaCollection}
                mediaLabel={mediaLabel}
                setMediaLabel={setMediaLabel}
                mediaItems={mediaItems}
                mediaPage={mediaPage}
                setMediaPage={setMediaPage}
                mediaPageSize={mediaPageSize}
                setMediaPageSize={setMediaPageSize}
                mediaTotal={mediaTotal}
                refreshMedia={refreshMedia}
                setSelectedMedia={setSelectedMedia}
                selectedMedia={selectedMedia}
                applyMediaAsFileCameraSource={(source) => form.setFieldsValue({ source })}
              /> }
            ]}
          />
        </section>
      </div>
    </Modal>
  );
}
