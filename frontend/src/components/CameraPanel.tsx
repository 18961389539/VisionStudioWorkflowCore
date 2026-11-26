import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Drawer, Form, Space, Table, Tabs, Tag, message } from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import type { CameraAdapterDescriptor, CameraDescriptor, CameraDiscoveredDevice, CameraSettings, CameraSynchronizationGroup, CameraSynchronizationGroupStatus, CameraSynchronizationCaptureResult, CameraSynchronizationRunRecord, CameraSynchronizationStatistics, CameraSynchronizationPtpDiagnostics, CameraSynchronizationCommissioningTest, GigENetworkDiagnostics, MediaCollectionDescriptor, MediaItemDescriptor, MediaLibraryStatus, MediaLinkedSourceDescriptor, MediaBulkResult, MediaIntegrityReport } from '../types';
import { localizeStatus } from '../i18n';
import { responseErrorMessage } from '../apiErrors';
import { apiFetch, cachedGet, invalidateCache, RequestToken } from '../apiClient';
import { apiUrl, cameras as cameraEndpoints, cameraSync, media } from '../endpoints';
import { useMonitorSnapshot } from '../useMonitorStream';
import CameraAcquisitionTab from './CameraAcquisitionTab';
import CameraDiagnosticsTab from './CameraDiagnosticsTab';
import CameraMediaTab from './CameraMediaTab';
import CameraParametersTab from './CameraParametersTab';
import CameraRegistrationTab from './CameraRegistrationTab';
import CameraSyncTab from './CameraSyncTab';
import { useCameraCommissioning } from './useCameraCommissioning';
import { acquisitionColor, stateColor, type SyncGroupFormValues } from './cameraPanelShared';

// R04：轮询超时——请求挂起时必须暴露失败，而不是让旧数据静默伪装成最新。
const CAMERA_POLL_TIMEOUT_MS = 5000;
const MEDIA_IMPORT_BATCH_FILES = 200;
const MEDIA_IMPORT_BATCH_BYTES = 512 * 1024 * 1024;

type Props = { open: boolean; onClose: () => void; initialTab?: 'acquisition' | 'media' };

// 各标签页已拆成独立组件，共享类型与状态色放在 cameraPanelShared.ts。

async function responseError(response: Response, fallback: string) {
  try {
    const data = await response.json();
    return data.detail ?? data.error ?? fallback;
  } catch { return fallback; }
}

export default function CameraPanel({ open, onClose, initialTab = 'acquisition' }: Props) {
  const monitorSnapshot = useMonitorSnapshot(open);
  const [cameras, setCameras] = useState<CameraDescriptor[]>([]);
  const [adapters, setAdapters] = useState<CameraAdapterDescriptor[]>([]);
  const [discovered, setDiscovered] = useState<CameraDiscoveredDevice[]>([]);
  const [discoverDriver, setDiscoverDriver] = useState('basler-pylon');
  const [selectedDiscovered, setSelectedDiscovered] = useState<CameraDiscoveredDevice>();
  const [selectedId, setSelectedId] = useState('virtual-1');
  const [activeTab, setActiveTab] = useState<string>(initialTab);
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
  const [mediaSearch, setMediaSearch] = useState('');
  const [mediaMinWidth, setMediaMinWidth] = useState<number>();
  const [mediaMaxWidth, setMediaMaxWidth] = useState<number>();
  const [mediaModifiedAfter, setMediaModifiedAfter] = useState('');
  const [mediaModifiedBefore, setMediaModifiedBefore] = useState('');
  const [selectedMedia, setSelectedMedia] = useState<MediaItemDescriptor>();
  const [uploadFiles, setUploadFiles] = useState<UploadFile[]>([]);
  const [linkedSources, setLinkedSources] = useState<MediaLinkedSourceDescriptor[]>([]);
  const [linkedDirectoryName, setLinkedDirectoryName] = useState('');
  const [linkedDirectoryPath, setLinkedDirectoryPath] = useState('');
  const [mediaLoading, setMediaLoading] = useState(false);
  const [browsingLinkedName, setBrowsingLinkedName] = useState<string>();
  const [linkedItems, setLinkedItems] = useState<MediaItemDescriptor[]>([]);
  const [linkedItemsTotal, setLinkedItemsTotal] = useState(0);
  const [linkedItemsPage, setLinkedItemsPage] = useState(1);
  const [linkedItemsPageSize, setLinkedItemsPageSize] = useState(20);
  const [linkedItemsSearch, setLinkedItemsSearch] = useState('');
  const [linkedItemsLoading, setLinkedItemsLoading] = useState(false);
  const [selectedLinkedItem, setSelectedLinkedItem] = useState<MediaItemDescriptor>();
  const [mediaImportProgress, setMediaImportProgress] = useState<{ completed: number; total: number; batch: number; totalBatches: number }>();
  const [mediaIntegrity, setMediaIntegrity] = useState<MediaIntegrityReport>();
  const [uploadCollection, setUploadCollection] = useState('offline');
  const [uploadLabel, setUploadLabel] = useState('Unlabeled');
  const [newCollection, setNewCollection] = useState('');
  const mediaRefreshGeneration = useRef(0);
  const mediaRefreshToken = useRef<RequestToken | null>(null);
  const linkedRefreshGeneration = useRef(0);
  const linkedRefreshToken = useRef<RequestToken | null>(null);
  const [form] = Form.useForm();
  const [vendorForm] = Form.useForm();
  const [settingsForm] = Form.useForm<CameraSettings>();
  const editedTriggerMode = Form.useWatch("triggerMode", settingsForm);
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
  useEffect(() => { setActiveTab(initialTab); }, [initialTab]);

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

  const {
    commissioningForm, commissioning, features, featureEdits, setFeatureEdits,
    featureProfiles, selectedFeatureProfile, setSelectedFeatureProfile,
    profileId, setProfileId, profileName, setProfileName,
    clearCommissioning, refreshCommissioning, refreshFeatureProfiles,
    applyCommissioning, setFeature, captureFeatureProfile,
    applySavedFeatureProfile, deleteFeatureProfile, exportFeatureProfile, importFeatureProfileFile
  } = useCameraCommissioning({ selected, setBusy, refreshCameras: refresh, messageApi });

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

  type MediaFilterOverrides = {
    collection?: string;
    label?: string;
    search?: string;
    minWidth?: number;
    maxWidth?: number;
    modifiedAfter?: string;
    modifiedBefore?: string;
  };

  const refreshMedia = async (quiet = false, page = mediaPage, pageSize = mediaPageSize, overrides?: MediaFilterOverrides) => {
    const generation = ++mediaRefreshGeneration.current;
    mediaRefreshToken.current?.cancel();
    const token = new RequestToken();
    mediaRefreshToken.current = token;
    setMediaLoading(true);
    try {
      const collection = overrides && 'collection' in overrides ? overrides.collection : mediaCollection;
      const label = overrides && 'label' in overrides ? overrides.label : mediaLabel;
      const search = overrides && 'search' in overrides ? (overrides.search ?? '') : mediaSearch;
      const minWidth = overrides && 'minWidth' in overrides ? overrides.minWidth : mediaMinWidth;
      const maxWidth = overrides && 'maxWidth' in overrides ? overrides.maxWidth : mediaMaxWidth;
      const modifiedAfter = overrides && 'modifiedAfter' in overrides ? (overrides.modifiedAfter ?? '') : mediaModifiedAfter;
      const modifiedBefore = overrides && 'modifiedBefore' in overrides ? (overrides.modifiedBefore ?? '') : mediaModifiedBefore;

      const query = new URLSearchParams();
      if (collection) query.set('collection', collection);
      if (label) query.set('label', label);
      if (search.trim()) query.set('search', search.trim());
      if (minWidth) query.set('minWidth', String(minWidth));
      if (maxWidth) query.set('maxWidth', String(maxWidth));
      if (modifiedAfter) query.set('modifiedAfter', new Date(`${modifiedAfter}T00:00:00`).toISOString());
      if (modifiedBefore) query.set('modifiedBefore', new Date(`${modifiedBefore}T23:59:59.999`).toISOString());
      query.set('skip', String(Math.max(0, page - 1) * pageSize));
      query.set('take', String(pageSize));

      const [statusResponse, collectionsResponse, itemsResponse, linkedSourcesResponse] = await Promise.all([
        apiFetch(media.status, { signal: token.signal }),
        apiFetch(media.collections, { signal: token.signal }),
        apiFetch(media.itemsQuery(query.toString()), { signal: token.signal }),
        apiFetch(media.linkedSources, { signal: token.signal }).catch(() => undefined)
      ]);
      if (generation !== mediaRefreshGeneration.current || token.signal.aborted) return;
      if (!statusResponse.ok || !collectionsResponse.ok || !itemsResponse.ok)
        throw new Error('媒体库接口不可用');

      const [nextStatus, nextCollections, items]: [MediaLibraryStatus, MediaCollectionDescriptor[], MediaItemDescriptor[]] = await Promise.all([
        statusResponse.json(),
        collectionsResponse.json(),
        itemsResponse.json()
      ]);
      const nextLinkedSources: MediaLinkedSourceDescriptor[] = linkedSourcesResponse?.ok
        ? await linkedSourcesResponse.json()
        : [];
      if (generation !== mediaRefreshGeneration.current || token.signal.aborted) return;

      setMediaStatus(nextStatus);
      setCollections(nextCollections);
      setLinkedSources(nextLinkedSources);
      if (browsingLinkedName && !nextLinkedSources.some((item) => item.name === browsingLinkedName)) {
        setBrowsingLinkedName(undefined);
        setLinkedItems([]);
        setLinkedItemsTotal(0);
        setSelectedLinkedItem(undefined);
      }

      const rawTotal = itemsResponse.headers.get('X-Total-Count');
      const parsedTotal = rawTotal === null ? Number.NaN : Number(rawTotal);
      const total = Number.isFinite(parsedTotal) ? parsedTotal : items.length;
      const lastPage = Math.max(1, Math.ceil(total / pageSize));
      if (page > lastPage) {
        // 删除当前页最后一项后页码可能越界：收到总数后回退到最后一页重新取。
        setMediaPage(lastPage);
        await refreshMedia(quiet, lastPage, pageSize, overrides);
        return;
      }
      setMediaTotal(total);
      setMediaItems(items);
      if (selectedMedia) {
        const refreshedSelected = items.find((x) => x.relativePath === selectedMedia.relativePath);
        setSelectedMedia(refreshedSelected);
      }
    } catch (error) {
      if (generation !== mediaRefreshGeneration.current || token.signal.aborted) return;
      if (!quiet) messageApi.error(error instanceof Error ? error.message : '媒体库接口不可用');
    } finally {
      if (generation === mediaRefreshGeneration.current) setMediaLoading(false);
    }
  };

  const resetMediaFilters = () => {
    setMediaSearch('');
    setMediaCollection(undefined);
    setMediaLabel(undefined);
    setMediaMinWidth(undefined);
    setMediaMaxWidth(undefined);
    setMediaModifiedAfter('');
    setMediaModifiedBefore('');
    setMediaPage(1);
    void refreshMedia(false, 1, mediaPageSize, {
      collection: undefined,
      label: undefined,
      search: '',
      minWidth: undefined,
      maxWidth: undefined,
      modifiedAfter: '',
      modifiedBefore: ''
    });
  };

  const browseLinkedSource = async (
    name: string,
    page = 1,
    pageSize = linkedItemsPageSize,
    search = linkedItemsSearch
  ) => {
    const generation = ++linkedRefreshGeneration.current;
    linkedRefreshToken.current?.cancel();
    const token = new RequestToken();
    linkedRefreshToken.current = token;
    setBrowsingLinkedName(name);
    setLinkedItemsLoading(true);
    try {
      const query = new URLSearchParams({
        name,
        skip: String(Math.max(0, page - 1) * pageSize),
        take: String(pageSize)
      });
      if (search.trim()) query.set('search', search.trim());
      const response = await apiFetch(media.linkedSourceItems(query.toString()), { signal: token.signal });
      if (generation !== linkedRefreshGeneration.current || token.signal.aborted) return;
      if (!response.ok) throw new Error(await responseError(response, '读取服务端目录图片失败'));
      const items: MediaItemDescriptor[] = await response.json();
      if (generation !== linkedRefreshGeneration.current || token.signal.aborted) return;
      const rawTotal = response.headers.get('X-Total-Count');
      const parsedTotal = rawTotal === null ? Number.NaN : Number(rawTotal);
      setLinkedItemsTotal(Number.isFinite(parsedTotal) ? parsedTotal : items.length);
      setLinkedItems(items);
      setLinkedItemsPage(page);
      setLinkedItemsPageSize(pageSize);
      setSelectedLinkedItem((prev) => (prev && items.some((x) => x.relativePath === prev.relativePath) ? prev : items[0]));
    } catch (error) {
      if (generation !== linkedRefreshGeneration.current || token.signal.aborted) return;
      messageApi.error(error instanceof Error ? error.message : '读取服务端目录图片失败');
    } finally {
      if (generation === linkedRefreshGeneration.current) setLinkedItemsLoading(false);
    }
  };

  const monitorCapturedAt = monitorSnapshot?.capturedAt;
  const selectedCameraId = selected?.id;
  const selectedDriver = selected?.driver;
  const selectedState = selected?.state;
  const selectedExposureUs = selected?.settings.exposureUs;
  const selectedGainDb = selected?.settings.gainDb;
  const selectedTargetFps = selected?.settings.targetFps;
  const selectedTriggerMode = selected?.settings.triggerMode;
  const selectedOutputPixelFormat = selected?.settings.outputPixelFormat;
  const selectedExternalTriggerSource = selected?.settings.externalTriggerSource;

  const effectActionsRef = useRef({
    refresh: () => undefined as unknown as Promise<void>,
    refreshAdapters: () => undefined as unknown as Promise<void>,
    refreshMedia: (_quiet?: boolean, _page?: number, _pageSize?: number) => undefined as unknown as Promise<void>,
    refreshSyncGroups: () => undefined as unknown as Promise<void>,
    refreshSyncStatus: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    refreshSyncRuns: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    refreshSyncStatistics: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    refreshSyncPtpDiagnostics: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    refreshGigEDiagnostics: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    refreshSyncTests: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    refreshFeatureProfiles: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    refreshCommissioning: (_quiet?: boolean) => undefined as unknown as Promise<void>,
    clearCommissioning: () => undefined as void,
    refreshQuiet: () => undefined as unknown as Promise<void>,
    mediaPageSize,
    syncGroups,
    syncForm,
    settingsForm,
    selected
  });

  useEffect(() => {
    if (!open) return;
    void effectActionsRef.current.refresh();
    void effectActionsRef.current.refreshAdapters();
    void effectActionsRef.current.refreshSyncGroups();
    // 2026-11-12：1 s 轮询改为推送触发（服务端内容变化才广播），定时器只做断线兜底。
    const timer = window.setInterval(() => { void effectActionsRef.current.refreshQuiet(); }, 10_000);
    return () => window.clearInterval(timer);
  }, [open]);

  // 相机列表/状态变化时立即刷新；预览仍由本地 500 ms tick 驱动，不经过网络。
  useEffect(() => {
    if (monitorCapturedAt !== undefined) void effectActionsRef.current.refreshQuiet();
  }, [monitorCapturedAt]);

  useEffect(() => {
    if (!open) return;
    setMediaPage(1);
    void effectActionsRef.current.refreshMedia(true, 1, effectActionsRef.current.mediaPageSize);
  }, [open, mediaCollection, mediaLabel]);
  useEffect(() => {
    if (!open) return;
    void effectActionsRef.current.refreshSyncStatus(true);
    void effectActionsRef.current.refreshSyncRuns(true);
    void effectActionsRef.current.refreshSyncStatistics(true);
    void effectActionsRef.current.refreshSyncPtpDiagnostics(true);
    void effectActionsRef.current.refreshGigEDiagnostics(true);
    void effectActionsRef.current.refreshSyncTests(true);
  }, [open, selectedSyncId]);
  useEffect(() => {
    if (!open || !selectedSyncId) return;
    const timer = window.setInterval(() => { void effectActionsRef.current.refreshSyncTests(true); }, 1000);
    return () => window.clearInterval(timer);
  }, [open, selectedSyncId]);
  useEffect(() => {
    const group = syncGroups.find((x) => x.id === selectedSyncId);
    if (group) effectActionsRef.current.syncForm.setFieldsValue(group);
  }, [selectedSyncId, syncGroups]);
  useEffect(() => {
    const current = effectActionsRef.current.selected;
    if (current) effectActionsRef.current.settingsForm.setFieldsValue(current.settings);
  }, [selectedCameraId, selectedExposureUs, selectedGainDb, selectedTargetFps, selectedTriggerMode, selectedOutputPixelFormat, selectedExternalTriggerSource]);
  useEffect(() => {
    if (!open || !selectedCameraId) return;
    void effectActionsRef.current.refreshFeatureProfiles(true);
    if (selectedState !== 'Closed') void effectActionsRef.current.refreshCommissioning(true);
    else effectActionsRef.current.clearCommissioning();
  }, [open, selectedCameraId, selectedDriver, selectedState]);
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

  effectActionsRef.current = {
    refresh: () => refresh(false),
    refreshQuiet: () => refresh(true),
    refreshAdapters: () => refreshAdapters(false),
    refreshMedia,
    refreshSyncGroups: () => refreshSyncGroups(false),
    refreshSyncStatus,
    refreshSyncRuns,
    refreshSyncStatistics,
    refreshSyncPtpDiagnostics,
    refreshGigEDiagnostics,
    refreshSyncTests,
    refreshFeatureProfiles,
    refreshCommissioning,
    clearCommissioning,
    mediaPageSize,
    syncGroups,
    syncForm,
    settingsForm,
    selected
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
      if (!response.ok) throw new Error(await responseError(response, '创建集合失败'));
      setUploadCollection(newCollection.trim()); setNewCollection(''); await refreshMedia(true); messageApi.success('媒体集合已创建');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '创建集合失败'); }
    finally { setBusy(undefined); }
  };

  const importMedia = async (selectedDirectoryFiles?: File[]) => {
    const nativeFiles = selectedDirectoryFiles
      ?? uploadFiles
        .map((item) => item.originFileObj as File | undefined)
        .filter((file): file is File => file !== undefined);
    if (!uploadCollection.trim() || nativeFiles.length === 0) return;
    setBusy('media-import');
    const batches: File[][] = [];
    let currentBatch: File[] = [];
    let currentBytes = 0;
    for (const file of nativeFiles) {
      if (currentBatch.length > 0
        && (currentBatch.length >= MEDIA_IMPORT_BATCH_FILES || currentBytes + file.size > MEDIA_IMPORT_BATCH_BYTES)) {
        batches.push(currentBatch);
        currentBatch = [];
        currentBytes = 0;
      }
      currentBatch.push(file);
      currentBytes += file.size;
    }
    if (currentBatch.length) batches.push(currentBatch);
    let completed = 0;
    try {
      for (let index = 0; index < batches.length; index++) {
        setMediaImportProgress({ completed, total: nativeFiles.length, batch: index + 1, totalBatches: batches.length });
        const body = new FormData();
        body.append('collection', uploadCollection.trim());
        body.append('label', uploadLabel);
        for (const file of batches[index]) {
          body.append('files', file, file.name);
          body.append('relativePaths', file.webkitRelativePath || file.name);
        }
        const response = await apiFetch(media.import, { method: 'POST', body });
        if (!response.ok) throw new Error(await responseError(response, `Media batch ${index + 1} failed`));
        const imported: MediaItemDescriptor[] = await response.json();
        completed += imported.length;
      }
      setUploadFiles([]);
      setMediaCollection(uploadCollection.trim());
      setMediaPage(1);
      await refreshMedia(true, 1, mediaPageSize);
      messageApi.success(`已导入 ${completed} 张图像（${batches.length} 个批次）`);
      return true;
    } catch (error) {
      await refreshMedia(true, 1, mediaPageSize);
      messageApi.error(`${completed}/${nativeFiles.length} 已导入：${error instanceof Error ? error.message : '媒体导入失败'}`);
      return false;
    }
    finally {
      setBusy(undefined);
      setMediaImportProgress(undefined);
    }
  };

  const linkDirectory = async () => {
    if (!linkedDirectoryName.trim() || !linkedDirectoryPath.trim()) return;
    setBusy('media-link');
    try {
      const response = await apiFetch(media.linkedSources, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: linkedDirectoryName.trim(), path: linkedDirectoryPath.trim() })
      });
      if (!response.ok) throw new Error(await responseError(response, '关联服务端目录失败'));
      const saved: MediaLinkedSourceDescriptor = await response.json();
      setLinkedDirectoryName('');
      setLinkedDirectoryPath('');
      await refreshMedia(true);
      messageApi.success(`服务端目录「${saved.name}」已保存为只读引用`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '关联服务端目录失败');
    } finally { setBusy(undefined); }
  };

  const unlinkDirectory = async (name: string) => {
    setBusy(`media-unlink:${name}`);
    try {
      const response = await apiFetch(media.linkedSourceByName(name), { method: 'DELETE' });
      if (!response.ok) throw new Error(await responseError(response, '移除目录引用失败'));
      if (browsingLinkedName === name) {
        setBrowsingLinkedName(undefined);
        setLinkedItems([]);
        setLinkedItemsTotal(0);
        setSelectedLinkedItem(undefined);
      }
      await refreshMedia(true);
      messageApi.success('只读目录引用已移除，原文件未删除');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '移除目录引用失败');
    } finally { setBusy(undefined); }
  };

  const bulkUpdateMedia = async (
    paths: string[],
    action: 'delete' | 'move' | 'relabel' | 'rename',
    options: { targetCollection?: string; targetLabel?: string; targetName?: string }) => {
    if (paths.length === 0) return undefined;
    setBusy('media-bulk');
    try {
      const response = await apiFetch(media.bulk, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action, paths, ...options })
      });
      if (!response.ok) throw new Error(await responseError(response, '批量更新媒体失败'));
      const result: MediaBulkResult = await response.json();
      await refreshMedia(true);
      if (action === 'rename' && result.items.length === 1) setSelectedMedia(result.items[0] as MediaItemDescriptor);
      if (result.failures.length)
        messageApi.warning(`${result.succeeded} 项成功，${result.failures.length} 项失败：${result.failures[0].error}`);
      else
        messageApi.success(action === 'rename' ? '文件已重命名' : `已更新 ${result.succeeded} 个媒体项`);
      return result;
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '批量更新媒体失败');
      return undefined;
    } finally { setBusy(undefined); }
  };

  const activeMediaCollection = () => mediaCollection ?? uploadCollection;

  const downloadMediaManifest = async () => {
    const collection = activeMediaCollection();
    if (!collection.trim()) return;
    setBusy('media-manifest');
    try {
      const response = await apiFetch(media.manifest(collection));
      if (!response.ok) throw new Error(await responseError(response, '生成媒体清单失败'));
      const manifest = await response.json();
      const blob = new Blob([JSON.stringify(manifest, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `${collection}.media-manifest.json`;
      anchor.click();
      URL.revokeObjectURL(url);
      messageApi.success(`已生成 ${manifest.files?.length ?? 0} 项媒体清单`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '生成媒体清单失败');
    } finally { setBusy(undefined); }
  };

  const verifyMediaManifest = async (file: File) => {
    const collection = activeMediaCollection();
    if (!collection.trim()) return;
    setBusy('media-manifest-verify');
    try {
      const manifest = JSON.parse(await file.text());
      const response = await apiFetch(media.verifyManifest(collection), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(manifest)
      });
      if (!response.ok) throw new Error(await responseError(response, '校验媒体清单失败'));
      const result = await response.json();
      if (result.valid) messageApi.success(`媒体清单校验通过（${result.checked} 项）`);
      else messageApi.warning(`媒体清单发现 ${result.issues.length} 个问题`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '校验媒体清单失败');
    } finally { setBusy(undefined); }
  };

  const scanMediaIntegrity = async () => {
    setBusy('media-integrity');
    try {
      const response = await apiFetch(media.integrity);
      if (!response.ok) throw new Error(await responseError(response, '媒体完整性扫描失败'));
      const report: MediaIntegrityReport = await response.json();
      setMediaIntegrity(report);
      messageApi.info(`媒体完整性扫描完成：${report.fileCount} 项，${report.issues.length} 个问题`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '媒体完整性扫描失败');
    } finally { setBusy(undefined); }
  };

  const repairMediaIndex = async () => {
    setBusy('media-maintenance');
    try {
      const response = await apiFetch(media.maintenance, { method: 'POST' });
      if (!response.ok) throw new Error(await responseError(response, '索引修复失败'));
      const result = await response.json();
      setMediaIntegrity(result.report);
      await refreshMedia(true);
      messageApi.success(`索引已修复，清理缩略图 ${result.removedThumbnails} 个`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '索引修复失败');
    } finally { setBusy(undefined); }
  };

  const deleteMedia = async (item: MediaItemDescriptor) => {
    setBusy(`delete:${item.relativePath}`);
    try {
      const response = await apiFetch(media.itemByPath(item.relativePath), { method: 'DELETE' });
      if (!response.ok) throw new Error(await responseError(response, '删除媒体失败'));
      if (selectedMedia?.relativePath === item.relativePath) setSelectedMedia(undefined);
      await refreshMedia(true); messageApi.success('媒体项已删除');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '删除媒体失败'); }
    finally { setBusy(undefined); }
  };

  const canTrigger = selected?.settings.triggerMode === 'Software' || (selected?.settings.triggerMode === 'External' && selected?.capabilities.hostSimulatedExternalTrigger);
  const sourceOptions = [
    ...collections.map((x) => ({ value: x.source, label: `${x.name} (${x.itemCount} images)` })),
    ...mediaItems.slice(0, 40).map((x) => ({ value: x.source, label: `${x.collection}/${x.label}/${x.name}` }))
  ];

  return (
    <Drawer open={open} onClose={onClose} mask={false} size="min(1120px, 80vw)" title="相机采集运行时与媒体库" destroyOnHidden>
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
            activeKey={activeTab}
            onChange={setActiveTab}
            items={[
              { key: 'acquisition', label: '采集', children: <CameraAcquisitionTab
                busy={busy}
                selected={selected}
                settingsForm={settingsForm}
                saveSettings={saveSettings}
                editedTriggerMode={editedTriggerMode}
              /> },
              { key: 'parameters', label: '参数', children: <CameraParametersTab
                busy={busy}
                selected={selected}
                commissioning={commissioning}
                commissioningForm={commissioningForm}
                refreshCommissioning={refreshCommissioning}
                applyCommissioning={applyCommissioning}
                profileId={profileId}
                setProfileId={setProfileId}
                profileName={profileName}
                setProfileName={setProfileName}
                captureFeatureProfile={captureFeatureProfile}
                selectedFeatureProfile={selectedFeatureProfile}
                setSelectedFeatureProfile={setSelectedFeatureProfile}
                featureProfiles={featureProfiles}
                applySavedFeatureProfile={applySavedFeatureProfile}
                exportFeatureProfile={exportFeatureProfile}
                importFeatureProfileFile={importFeatureProfileFile}
                deleteFeatureProfile={deleteFeatureProfile}
                features={features}
                featureEdits={featureEdits}
                setFeatureEdits={setFeatureEdits}
                setFeature={setFeature}
              /> },
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
                mediaLoading={mediaLoading}
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
                importProgress={mediaImportProgress}
                linkedSources={linkedSources}
                linkedDirectoryName={linkedDirectoryName}
                setLinkedDirectoryName={setLinkedDirectoryName}
                linkedDirectoryPath={linkedDirectoryPath}
                setLinkedDirectoryPath={setLinkedDirectoryPath}
                linkDirectory={linkDirectory}
                unlinkDirectory={unlinkDirectory}
                browsingLinkedName={browsingLinkedName}
                setBrowsingLinkedName={setBrowsingLinkedName}
                linkedItems={linkedItems}
                linkedItemsTotal={linkedItemsTotal}
                linkedItemsPage={linkedItemsPage}
                linkedItemsPageSize={linkedItemsPageSize}
                linkedItemsSearch={linkedItemsSearch}
                setLinkedItemsSearch={setLinkedItemsSearch}
                linkedItemsLoading={linkedItemsLoading}
                selectedLinkedItem={selectedLinkedItem}
                setSelectedLinkedItem={setSelectedLinkedItem}
                browseLinkedSource={browseLinkedSource}
                bulkUpdateMedia={bulkUpdateMedia}
                mediaIntegrity={mediaIntegrity}
                downloadManifest={downloadMediaManifest}
                verifyManifest={verifyMediaManifest}
                scanIntegrity={scanMediaIntegrity}
                repairIndex={repairMediaIndex}
                mediaSearch={mediaSearch}
                setMediaSearch={setMediaSearch}
                mediaMinWidth={mediaMinWidth}
                setMediaMinWidth={setMediaMinWidth}
                mediaMaxWidth={mediaMaxWidth}
                setMediaMaxWidth={setMediaMaxWidth}
                mediaModifiedAfter={mediaModifiedAfter}
                setMediaModifiedAfter={setMediaModifiedAfter}
                mediaModifiedBefore={mediaModifiedBefore}
                setMediaModifiedBefore={setMediaModifiedBefore}
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
                resetMediaFilters={resetMediaFilters}
                setSelectedMedia={setSelectedMedia}
                selectedMedia={selectedMedia}
                onCopyMessage={(text) => { messageApi.success(text); }}
                applyMediaAsFileCameraSource={(source) => {
                  form.setFieldsValue({ source });
                  setActiveTab('registration');
                  messageApi.success('已填入文件相机来源，请确认编号并点击注册');
                }}
              /> }
            ]}
          />
        </section>
      </div>
    </Drawer>
  );
}
