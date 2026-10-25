import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Input, InputNumber, Select, Space, Steps, Table, Tag, Typography, Upload, message } from 'antd';
import type { CameraIntrinsicAssetDescriptor, CameraIntrinsicVersionSnapshot, CalibrationWizardHint, CalibrationWizardSession } from '../types';
import { apiFetch } from '../apiClient';
import { responseErrorMessage } from '../apiErrors';
import DataErrorAlert from './DataErrorAlert';
import { calibration, cameraIntrinsics, cameras as cameraEndpoints } from '../endpoints';

type Props = {
  canApplyToSelectedNode: boolean;
  selectedNodeType?: string;
  onApplyToSelectedNode: (parameters: Record<string, unknown>) => void;
};

type CameraListItem = { id: string; name?: string; acquisition?: { acquisitionState?: string } };

function stableJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
  if (value && typeof value === 'object') {
    const object = value as Record<string, unknown>;
    return `{${Object.keys(object).sort().map((key) => `${JSON.stringify(key)}:${stableJson(object[key])}`).join(',')}}`;
  }
  return JSON.stringify(value) ?? 'null';
}

const stepTitles = ['标定板设置', '采集 / 导入视图', '审核视图', '求解内参', '导出应用'];

export default function CalibrationIntrinsicsWizard({ canApplyToSelectedNode, selectedNodeType, onApplyToSelectedNode }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [loadError, setLoadError] = useState<string>();
  const [session, setSession] = useState<CalibrationWizardSession>();
  const [pattern, setPattern] = useState('chessboard');
  const [columns, setColumns] = useState(7);
  const [rows, setRows] = useState(6);
  const [spacingMm, setSpacingMm] = useState(10);
  const [minBlurVariance, setMinBlurVariance] = useState(40);
  const [minCoverage, setMinCoverage] = useState(0.25);
  const [imageWidth, setImageWidth] = useState<number | null>(null);
  const [imageHeight, setImageHeight] = useState<number | null>(null);
  const [importJson, setImportJson] = useState('');
  const [documentJson, setDocumentJson] = useState('');
  const [solveSummary, setSolveSummary] = useState<Record<string, unknown>>();
  const [maxViewRms, setMaxViewRms] = useState(0);
  const [maxRms, setMaxRms] = useState(0);
  const [maxPoseViewRms, setMaxPoseViewRms] = useState(0);
  const [cameraModel, setCameraModel] = useState<'pinhole' | 'fisheye' | 'telecentric'>('pinhole');
  const [telecentricMagnification, setTelecentricMagnification] = useState(0.2);
  const [telecentricPixelSizeUm, setTelecentricPixelSizeUm] = useState(3.45);
  const [fisheyeBalance, setFisheyeBalance] = useState(0);
  const [hints, setHints] = useState<CalibrationWizardHint[]>([]);
  const [cameras, setCameras] = useState<CameraListItem[]>([]);
  const [cameraId, setCameraId] = useState<string>();
  const [previewUrl, setPreviewUrl] = useState<string>();
  const [previewQuality, setPreviewQuality] = useState<string>();
  const [viewStatsRows, setViewStatsRows] = useState<Record<string, unknown>[]>([]);
  const [intrinsicAssets, setIntrinsicAssets] = useState<CameraIntrinsicAssetDescriptor[]>([]);
  const [intrinsicAssetId, setIntrinsicAssetId] = useState('camera-intrinsics-1');
  const [intrinsicAssetName, setIntrinsicAssetName] = useState('Camera Intrinsic Calibration');
  const [pinnedAssetId, setPinnedAssetId] = useState<string>();
  const [pinnedVersion, setPinnedVersion] = useState(0);
  const [pinnedModel, setPinnedModel] = useState<unknown>();

  const enabledCount = session?.enabledCount ?? 0;
  const guidance = useMemo(() => {
    if (!session?.views.length) return '上传标定板图像、从相机刷新检测，或粘贴 detectBoard 累积的 calibrationDataJson。目标：8–20 张、覆盖视场边缘并带倾角变化。';
    const fails = session.views.filter((v) => v.quality.verdict === 'Fail').length;
    const warns = session.views.filter((v) => v.quality.verdict === 'Warn').length;
    return `已收集 ${session.views.length} 张（启用 ${enabledCount}）。失败 ${fails} · 警告 ${warns}。启用满 3 张即可求解。会话会持久化到服务器 data/calibration-wizard。`;
  }, [session, enabledCount]);

  useEffect(() => {
    void (async () => {
      try {
        const response = await apiFetch(cameraEndpoints.list);
        if (!response.ok) {
          setLoadError(await responseErrorMessage(response, `读取相机列表失败（HTTP ${response.status}）`));
          return;
        }
        const list = await response.json() as CameraListItem[];
        setCameras(list);
        setLoadError(undefined);
        if (!cameraId && list[0]) setCameraId(list[0].id);
      } catch (error) {
        setLoadError(error instanceof Error ? `读取相机列表失败：${error.message}` : '读取相机列表失败：网络请求未完成');
      }
    })();
  }, []);

  const refreshIntrinsicAssets = async () => {
    try {
      const response = await apiFetch(cameraIntrinsics.list);
      if (!response.ok) {
        setLoadError(await responseErrorMessage(response, `读取内参资产失败（HTTP ${response.status}）`));
        return;
      }
      setIntrinsicAssets(await response.json() as CameraIntrinsicAssetDescriptor[]);
      setLoadError(undefined);
    } catch (error) {
      setLoadError(error instanceof Error ? `读取内参资产失败：${error.message}` : '读取内参资产失败：网络请求未完成');
    }
  };

  useEffect(() => { void refreshIntrinsicAssets(); }, []);

  const pinMatchesSession = Boolean(pinnedAssetId && pinnedVersion > 0 && session?.model && stableJson(session.model) === stableJson(pinnedModel));

  useEffect(() => () => {
    if (previewUrl) URL.revokeObjectURL(previewUrl);
  }, [previewUrl]);

  const applySessionState = (next: CalibrationWizardSession) => {
    setSession(next);
    setCameraModel(next.cameraModel === 'fisheye' || next.cameraModel === 'telecentric' ? next.cameraModel : 'pinhole');
    setTelecentricMagnification(next.telecentricMagnification ?? 0.2);
    setTelecentricPixelSizeUm(next.telecentricPixelSizeUm ?? 3.45);
  };

  const refresh = async (next: CalibrationWizardSession) => {
    setSession(next);
  };

  const changeCameraModel = (next: 'pinhole' | 'fisheye' | 'telecentric') => {
    if (next === cameraModel) return;
    setCameraModel(next);
    setSession((current) => current ? { ...current, cameraModel: next, model: undefined, lastSolveSummary: null } : current);
    if (next === 'telecentric') setMaxPoseViewRms(0);
    setSolveSummary(undefined);
    setViewStatsRows([]);
    setPinnedAssetId(undefined);
    setPinnedVersion(0);
    setPinnedModel(undefined);
  };

  const changeTelecentricParameters = (magnification: number, pixelSizeUm: number) => {
    setTelecentricMagnification(magnification);
    setTelecentricPixelSizeUm(pixelSizeUm);
    setSession((current) => current ? {
      ...current,
      telecentricMagnification: magnification,
      telecentricPixelSizeUm: pixelSizeUm,
      model: undefined,
      lastSolveSummary: null
    } : current);
    setSolveSummary(undefined);
    setViewStatsRows([]);
    setPinnedAssetId(undefined);
    setPinnedVersion(0);
    setPinnedModel(undefined);
  };

  const createSession = async () => {
    setBusy(true);
    try {
      const body: Record<string, unknown> = { pattern, columns, rows, spacingMm, minBlurVariance, minCoverage };
      if (imageWidth && imageHeight) { body.imageWidth = imageWidth; body.imageHeight = imageHeight; }
      const response = await apiFetch(calibration.wizardSessions, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '创建向导会话失败');
      applySessionState(data);
      setSolveSummary(undefined);
      setViewStatsRows([]);
      setHints([]);
      setStep(1);
      messageApi.success('内参标定向导会话已创建（已落盘）');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '创建失败'); }
    finally { setBusy(false); }
  };

  const uploadImages = async (files: File[]) => {
    if (!session) return messageApi.warning('请先创建会话');
    setBusy(true);
    try {
      let current = session;
      for (const file of files) {
        const form = new FormData();
        form.append('image', file);
        form.append('label', file.name);
        const response = await apiFetch(calibration.wizardSessionViewFromImage(session.sessionId), {
          method: 'POST', body: form
        });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error ?? `图像 ${file.name} 检测失败`);
        current = data;
      }
      await refresh(current);
      await loadHints(current.sessionId);
      setStep(2);
      messageApi.success(`已处理 ${files.length} 张图像`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '上传失败'); }
    finally { setBusy(false); }
  };

  const importCalibrationJson = async () => {
    if (!session) return messageApi.warning('请先创建会话');
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionViews(session.sessionId), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ calibrationDataJson: importJson, label: 'import' })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '导入失败');
      await refresh(data);
      await loadHints(data.sessionId);
      setStep(2);
      messageApi.success('已导入标定视图 JSON');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导入失败'); }
    finally { setBusy(false); }
  };

  const importDocument = async () => {
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionImport, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ documentJson })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '会话文档导入失败');
      applySessionState(data);
      setSolveSummary((data.lastSolveSummary as Record<string, unknown> | null) ?? undefined);
      setStep(data.views?.length ? 2 : 1);
      await loadHints(data.sessionId);
      messageApi.success('已从会话文档恢复（含视图与结果）');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导入失败'); }
    finally { setBusy(false); }
  };

  const loadHints = async (sessionId: string) => {
    try {
      const response = await apiFetch(calibration.wizardSessionHints(sessionId));
      if (!response.ok) return;
      const data = await response.json();
      setHints(Array.isArray(data.hints) ? data.hints : []);
    } catch { /* ignore */ }
  };

  const previewCamera = async (addIfAccepted: boolean) => {
    if (!session) return messageApi.warning('请先创建会话');
    if (!cameraId) return messageApi.warning('请选择相机');
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionPreview(session.sessionId), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cameraId, addIfAccepted, label: `cam-${cameraId}` })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '相机预览检测失败');
      if (previewUrl) URL.revokeObjectURL(previewUrl);
      if (typeof data.previewJpegBase64 === 'string') {
        const bytes = Uint8Array.from(atob(data.previewJpegBase64), (c) => c.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], { type: 'image/jpeg' }));
        setPreviewUrl(url);
      }
      setPreviewQuality(`${data.quality?.verdict ?? '?'}: ${data.quality?.reason ?? ''}`);
      if (data.session) await refresh(data.session);
      if (Array.isArray(data.hints)) setHints(data.hints);
      messageApi.success(addIfAccepted && data.found ? '已检测并接受当前帧' : (data.found ? '检测成功（未写入会话）' : '未检测到标定板'));
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '预览失败'); }
    finally { setBusy(false); }
  };

  const setEnabled = async (index: number, enabled: boolean) => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionViewEnabled(session.sessionId, index), {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enabled })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '更新失败');
      await refresh(data);
      await loadHints(data.sessionId);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '更新失败'); }
    finally { setBusy(false); }
  };

  const removeView = async (index: number) => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionView(session.sessionId, index), { method: 'DELETE' });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '删除失败');
      await refresh(data);
      await loadHints(data.sessionId);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '删除失败'); }
    finally { setBusy(false); }
  };

  const disableOutliers = async () => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionDisableOutliers(session.sessionId), {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ useSolveOutlier: true })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '禁用离群失败');
      await refresh(data);
      messageApi.success('已禁用求解标记的离群/最差视图，请重新求解');
      setStep(3);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '操作失败'); }
    finally { setBusy(false); }
  };

  const calibrate = async () => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionCalibrate(session.sessionId), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ maxViewRms, maxRms, maxPoseViewRms: cameraModel === 'telecentric' ? 0 : maxPoseViewRms, cameraModel, telecentricMagnification, telecentricPixelSizeUm })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '标定求解失败');
      applySessionState(data.session);
      setSolveSummary(data.result);
      setPinnedAssetId(undefined);
      setPinnedVersion(0);
      setPinnedModel(undefined);
      if (Array.isArray(data.hints)) setHints(data.hints);
      const statsRaw = data.result?.viewStats;
      if (typeof statsRaw === 'string') {
        try { setViewStatsRows(JSON.parse(statsRaw)); } catch { setViewStatsRows([]); }
      } else { setViewStatsRows([]); }
      setStep(4);
      messageApi.success(`求解完成 · RMS ${Number(data.result?.rms ?? 0).toFixed(4)} px`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '求解失败'); }
    finally { setBusy(false); }
  };

  const exportDocument = async () => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionExport(session.sessionId));
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '导出失败');
      const sourceDocument = data.document && typeof data.document === 'object' ? data.document as Record<string, unknown> : {};
      const exportedDocument = {
        ...sourceDocument,
        cameraModel,
        telecentricMagnification,
        telecentricPixelSizeUm,
        model: session.model ?? null,
        lastSolveSummary: session.model ? sourceDocument.lastSolveSummary ?? null : null
      };
      const text = JSON.stringify(exportedDocument, null, 2);
      setDocumentJson(text);
      if (Array.isArray(data.hints)) setHints(data.hints);
      await navigator.clipboard?.writeText(text).catch(() => undefined);
      messageApi.success('已导出完整会话文档（含视图点与结果），可复制保存');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导出失败'); }
    finally { setBusy(false); }
  };

  const saveIntrinsicAsset = async () => {
    if (!session?.model) return messageApi.warning('请先完成内参求解');
    if (!intrinsicAssetId.trim() || !intrinsicAssetName.trim()) return messageApi.warning('请填写资产 ID 和名称');
    setBusy(true);
    try {
      const existing = intrinsicAssets.find((asset) => asset.id === intrinsicAssetId.trim());
      const url = existing ? cameraIntrinsics.versions(existing.id) : cameraIntrinsics.list;
      const body = existing
        ? { sessionId: session.sessionId, note: `Wizard save ${new Date().toISOString()}` }
        : { sessionId: session.sessionId, id: intrinsicAssetId.trim(), name: intrinsicAssetName.trim(), description: 'Camera intrinsic model with source wizard views', note: 'Initial intrinsic calibration' };
      const response = await apiFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const data = await response.json() as Record<string, unknown>;
      if (!response.ok) throw new Error(String(data.error ?? '保存相机内参资产失败'));
      const version = Number(data.version ?? data.latestVersion ?? 0);
      if (!Number.isInteger(version) || version < 1) throw new Error('服务端未返回有效版本号');
      setPinnedAssetId(intrinsicAssetId.trim());
      setPinnedVersion(version);
      setPinnedModel(session.model);
      await refreshIntrinsicAssets();
      messageApi.success(`已保存相机内参资产 ${intrinsicAssetId.trim()} V${version}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '保存失败'); }
    finally { setBusy(false); }
  };

  const loadIntrinsicVersion = async (id: string, version: number) => {
    setBusy(true);
    try {
      const response = await apiFetch(cameraIntrinsics.version(id, String(version)));
      const snapshot = await response.json() as CameraIntrinsicVersionSnapshot & { error?: string };
      if (!response.ok) throw new Error(snapshot.error ?? '加载相机内参版本失败');
      const importResponse = await apiFetch(calibration.wizardSessionImport, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ documentJson: snapshot.sourceDocumentJson })
      });
      const restored = await importResponse.json() as CalibrationWizardSession & { error?: string };
      if (!importResponse.ok) throw new Error(restored.error ?? '恢复标定源视图失败');
      let summary: Record<string, unknown> = {};
      try { summary = JSON.parse(snapshot.summaryJson) as Record<string, unknown>; } catch { /* checksum validation already rejects invalid saved JSON */ }
      applySessionState(restored);
      setSolveSummary(summary);
      const stats = summary.viewStats;
      if (typeof stats === 'string') { try { setViewStatsRows(JSON.parse(stats) as Record<string, unknown>[]); } catch { setViewStatsRows([]); } }
      else setViewStatsRows([]);
      setIntrinsicAssetId(id);
      setIntrinsicAssetName(intrinsicAssets.find((asset) => asset.id === id)?.name ?? id);
      setPinnedAssetId(id);
      setPinnedVersion(version);
      setPinnedModel(snapshot.model);
      setStep(4);
      messageApi.success(`已加载相机内参资产 ${id} V${version}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '加载失败'); }
    finally { setBusy(false); }
  };

  const publishIntrinsicVersion = async (id: string, version: number, rollback = false) => {
    setBusy(true);
    try {
      const url = rollback
        ? cameraIntrinsics.rollback(id, String(version))
        : cameraIntrinsics.publish(id, String(version));
      const response = await apiFetch(url, { method: 'POST' });
      const data = await response.json() as { error?: string };
      if (!response.ok) throw new Error(data.error ?? '发布相机内参版本失败');
      await refreshIntrinsicAssets();
      messageApi.success(`${rollback ? '已回滚' : '已发布'}相机内参资产 ${id} V${version}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '发布失败'); }
    finally { setBusy(false); }
  };

  const applyModel = async () => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardSessionExport(session.sessionId));
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '导出失败');
      if (selectedNodeType === 'calibration.cameraIntrinsics') {
        onApplyToSelectedNode({ calibrationDataJson: data.calibrationDataJson ?? '', cameraModel, maxViewRms, maxRms, maxPoseViewRms: cameraModel === 'telecentric' ? 0 : maxPoseViewRms, telecentricMagnification, telecentricPixelSizeUm });
        messageApi.success('已将视图 JSON 应用到相机内参节点');
      } else if (selectedNodeType === 'image.undistort') {
        if (!session.model) throw new Error('相机模型已失效或尚未求解，请先重新运行标定。');
        const bound = pinMatchesSession && pinnedAssetId
          ? { intrinsicsAssetId: pinnedAssetId, intrinsicsAssetVersion: pinnedVersion }
          : { intrinsicsAssetId: '', intrinsicsAssetVersion: 0 };
        onApplyToSelectedNode({ calibrationJson: data.calibrationJson ?? '', fisheyeBalance, ...bound });
        messageApi.success(pinMatchesSession ? `已应用并绑定内参资产 ${pinnedAssetId} V${pinnedVersion}` : '已将内参模型应用到去畸变节点（未绑定资产版本）');
      } else {
        onApplyToSelectedNode({
          calibrationDataJson: data.calibrationDataJson ?? '',
          cameraModel,
          telecentricMagnification,
          telecentricPixelSizeUm,
          calibrationJson: session.model ? data.calibrationJson ?? '' : ''
        });
        messageApi.success('已导出标定结果（请选择内参或去畸变节点以精确写入）');
      }
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导出失败'); }
    finally { setBusy(false); }
  };

  const verdictColor = (v: string) => v === 'Pass' ? 'green' : v === 'Warn' ? 'gold' : 'red';

  return (
    <div>
      {contextHolder}
      <DataErrorAlert scope="标定基础数据" error={loadError} onRetry={() => void refreshIntrinsicAssets()} />
      <Steps size="small" current={step} items={stepTitles.map((title) => ({ title }))} style={{ marginBottom: 16 }}
        onChange={(value) => setStep(value)} />
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        title="相机内参向导（Batch F）"
        description="会话落盘 · 质量审核与离群处理 · JLVision 多项式针孔 / 物方远心模型、OpenCV 鱼眼模型 · SQLite 不可变内参版本、发布/回滚 · 可绑定到去畸变节点。远心倍率和像元尺寸请按镜头与相机数据表填写。" />
      <Typography.Paragraph type="secondary">{guidance}</Typography.Paragraph>
      {hints.length > 0 && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }}
          title="建议下一姿态（规则提示，非自动规划）"
          description={<ul style={{ margin: 0, paddingLeft: 18 }}>{hints.map((h) => <li key={h.code}>{h.message}</li>)}</ul>} />
      )}

      {step === 0 && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          <Space wrap>
            <Select value={pattern} onChange={setPattern} style={{ width: 160 }} options={[
              { value: 'chessboard', label: '棋盘格' }, { value: 'circles', label: '对称圆点阵' }
            ]} />
            <InputNumber addonBefore="列" min={2} max={30} value={columns} onChange={(v) => setColumns(Number(v ?? 7))} />
            <InputNumber addonBefore="行" min={2} max={30} value={rows} onChange={(v) => setRows(Number(v ?? 6))} />
            <InputNumber addonBefore="间距 mm" min={0.001} max={10000} step={0.1} value={spacingMm} onChange={(v) => setSpacingMm(Number(v ?? 10))} />
            <InputNumber addonBefore="最小清晰度" min={0} value={minBlurVariance} onChange={(v) => setMinBlurVariance(Number(v ?? 0))} />
            <InputNumber addonBefore="最小覆盖" min={0} max={1} step={0.01} value={minCoverage} onChange={(v) => setMinCoverage(Number(v ?? 0))} />
            <InputNumber addonBefore="宽" min={16} value={imageWidth ?? undefined} onChange={(v) => setImageWidth(v == null ? null : Number(v))} placeholder="可选" />
            <InputNumber addonBefore="高" min={16} value={imageHeight ?? undefined} onChange={(v) => setImageHeight(v == null ? null : Number(v))} placeholder="可选" />
          </Space>
          <Button type="primary" loading={busy} onClick={createSession}>创建向导会话</Button>
          <Input.TextArea rows={4} value={documentJson} onChange={(e) => setDocumentJson(e.target.value)}
            placeholder="或粘贴完整会话 documentJson（intrinsics-wizard-v1）以恢复上次进度" />
          <Button loading={busy} disabled={!documentJson.trim()} onClick={() => void importDocument()}>从会话文档恢复</Button>
        </Space>
      )}

      {step === 1 && session && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          <Typography.Text code>session {session.sessionId}</Typography.Text>
          <Space wrap>
            <Select
              style={{ minWidth: 220 }}
              placeholder="选择相机"
              value={cameraId}
              onChange={setCameraId}
              options={cameras.map((c) => ({ value: c.id, label: `${c.id}${c.acquisition?.acquisitionState ? ` (${c.acquisition.acquisitionState})` : ''}` }))}
            />
            <Button loading={busy} onClick={() => void previewCamera(false)}>刷新检测预览</Button>
            <Button type="primary" loading={busy} onClick={() => void previewCamera(true)}>检测并接受当前帧</Button>
          </Space>
          {previewUrl && (
            <div>
              <Typography.Text type="secondary">{previewQuality}</Typography.Text>
              <div><img src={previewUrl} alt="board preview" style={{ maxWidth: '100%', maxHeight: 320, border: '1px solid #ddd' }} /></div>
            </div>
          )}
          <Upload.Dragger multiple accept="image/*" disabled={busy} beforeUpload={() => false}
            onChange={(info) => {
              const files = info.fileList.map((f) => f.originFileObj).filter(Boolean) as File[];
              if (files.length) void uploadImages(files);
            }}>
            <p>拖拽或点击上传标定板图像（将调用 detectBoard + 质量门控）</p>
          </Upload.Dragger>
          <Input.TextArea rows={6} value={importJson} onChange={(e) => setImportJson(e.target.value)}
            placeholder='或粘贴 calibrationDataJson：{"imageWidth":...,"imageHeight":...,"views":[...]}' />
          <Space>
            <Button loading={busy} onClick={importCalibrationJson} disabled={!importJson.trim()}>导入 JSON 视图</Button>
            <Button onClick={() => setStep(2)} disabled={!session.views.length}>去审核</Button>
          </Space>
        </Space>
      )}

      {step === 2 && session && (
        <Space direction="vertical" style={{ width: '100%' }}>
          <Table size="small" rowKey="index" pagination={false} dataSource={session.views} scroll={{ y: 320 }}
            columns={[
              { title: '#', dataIndex: 'index', width: 48 },
              { title: '标签', dataIndex: 'label', width: 140 },
              { title: '启用', width: 70, render: (_: unknown, row) => (
                <Button size="small" type={row.enabled ? 'primary' : 'default'} onClick={() => void setEnabled(row.index, !row.enabled)}>
                  {row.enabled ? '开' : '关'}
                </Button>
              ) },
              { title: '质量', width: 90, render: (_: unknown, row) => <Tag color={verdictColor(row.quality.verdict)}>{row.quality.verdict}</Tag> },
              { title: '角点', width: 80, render: (_: unknown, row) => `${row.quality.cornerCount}/${row.quality.expectedCorners}` },
              { title: '覆盖', width: 110, render: (_: unknown, row) => `${(row.quality.coverageX * 100).toFixed(0)}%×${(row.quality.coverageY * 100).toFixed(0)}%` },
              { title: '清晰度', width: 90, render: (_: unknown, row) => row.quality.blurVariance.toFixed(1) },
              { title: '说明', render: (_: unknown, row) => <span title={row.quality.guidance}>{row.quality.reason}</span> },
              { title: '', width: 64, render: (_: unknown, row) => <Button size="small" danger onClick={() => void removeView(row.index)}>删除</Button> }
            ]} />
          <Space>
            <Button onClick={() => setStep(1)}>继续采集</Button>
            <Button type="primary" disabled={enabledCount < 3} onClick={() => setStep(3)}>去求解（已启用 {enabledCount}）</Button>
          </Space>
        </Space>
      )}

      {step === 3 && session && (
        <Space direction="vertical" size="middle">
          <Space>
            <Select value={cameraModel} onChange={changeCameraModel} style={{ width: 220 }} options={[
              { value: 'pinhole', label: '针孔（JLVision 多项式）' },
              { value: 'fisheye', label: '鱼眼（OpenCV 四系数）' },
              { value: 'telecentric', label: '物方远心（JLVision 多项式）' }
            ]} />
            {cameraModel === 'telecentric' && <>
              <InputNumber addonBefore="镜头倍率" min={0.001} max={10} step={0.01} value={telecentricMagnification} onChange={(value) => changeTelecentricParameters(Number(value ?? 0.2), telecentricPixelSizeUm)} />
              <InputNumber addonBefore="像元尺寸 μm" min={0.1} max={100} step={0.01} value={telecentricPixelSizeUm} onChange={(value) => changeTelecentricParameters(telecentricMagnification, Number(value ?? 3.45))} />
            </>}
            <InputNumber addonBefore="单视图 RMS 上限" min={0} max={100} step={0.1} value={maxViewRms} onChange={(v) => setMaxViewRms(Number(v ?? 0))} />
            <InputNumber addonBefore="拟合模型单视图 RMS 上限" min={0} max={100} step={0.1} value={maxPoseViewRms} disabled={cameraModel === 'telecentric'} onChange={(v) => setMaxPoseViewRms(Number(v ?? 0))} />
            <InputNumber addonBefore="模型 RMS 上限" min={0} max={1000} step={0.1} value={maxRms} onChange={(v) => setMaxRms(Number(v ?? 0))} />
            <Button type="primary" loading={busy} disabled={enabledCount < 3} onClick={calibrate}>运行内参标定</Button>
          </Space>
          <Typography.Text type="secondary">0 = 关闭门控。针孔与远心单视图门限采用平面预检；鱼眼采用拟合模型逐视图重投影。远心倍率和像元尺寸初值应来自数据表；远心投影不提供针孔位姿 RMS。</Typography.Text>
        </Space>
      )}

      {step === 4 && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          {solveSummary && (
            <Alert type="success" showIcon title={`RMS ${Number(solveSummary.rms ?? 0).toFixed(4)} px · 启用视图 ${String(solveSummary.enabledViews ?? enabledCount)}`}
              description={`最差视图索引 ${String(solveSummary.worstViewIndex ?? '无')} · 离群: ${String((solveSummary.nodeSummary as Record<string, unknown> | undefined)?.outlierViewIndex ?? '无')}`} />
          )}
          {viewStatsRows.length > 0 && (
            <Table size="small" pagination={false} rowKey={(r) => String(r.index)} dataSource={viewStatsRows}
              columns={[
                { title: 'view', dataIndex: 'index', width: 60 },
                { title: 'covX', render: (_: unknown, r) => Number(r.coverageX ?? 0).toFixed(2) },
                { title: 'covY', render: (_: unknown, r) => Number(r.coverageY ?? 0).toFixed(2) },
                { title: 'H-RMS', render: (_: unknown, r) => r.homographyRms == null ? '-' : Number(r.homographyRms).toFixed(3) },
                { title: 'Model-RMS', render: (_: unknown, r) => r.poseReprojectionRms == null ? '-' : Number(r.poseReprojectionRms).toFixed(3) }
              ]} />
          )}
          <Space wrap>
            <Input value={intrinsicAssetId} onChange={(event) => setIntrinsicAssetId(event.target.value)} addonBefore="资产 ID" style={{ width: 300 }} />
            <Input value={intrinsicAssetName} onChange={(event) => setIntrinsicAssetName(event.target.value)} addonBefore="名称" style={{ width: 330 }} />
            <Button type="primary" loading={busy} disabled={!session?.model} onClick={() => void saveIntrinsicAsset()}>
              {intrinsicAssets.some((asset) => asset.id === intrinsicAssetId.trim()) ? '保存新版本' : '创建版本资产'}
            </Button>
            <Button loading={busy} onClick={() => void refreshIntrinsicAssets()}>刷新资产</Button>
            {pinMatchesSession && pinnedAssetId && <Tag color="green">绑定 {pinnedAssetId} V{pinnedVersion}</Tag>}
          </Space>
          {selectedNodeType === 'image.undistort' && cameraModel === 'fisheye' && (
            <Space>
              <InputNumber addonBefore="鱼眼视场平衡" min={0} max={1} step={0.01} value={fisheyeBalance} onChange={(value) => setFisheyeBalance(Number(value ?? 0))} />
              <Typography.Text type="secondary">0 收紧校正视场，1 保留更多视场并允许边缘留黑。</Typography.Text>
            </Space>
          )}
          <Table size="small" rowKey="id" pagination={false} dataSource={intrinsicAssets} scroll={{ x: 900, y: 220 }}
            columns={[
              { title: '资产', width: 240, render: (_: unknown, row: CameraIntrinsicAssetDescriptor) => <><b>{row.name}</b><div><Typography.Text type="secondary" code>{row.id}</Typography.Text></div></> },
              { title: '已发布', width: 90, render: (_: unknown, row: CameraIntrinsicAssetDescriptor) => row.publishedVersion ? <Tag color="green">V{row.publishedVersion}</Tag> : <Tag>无</Tag> },
              { title: '版本 / 加载 / 发布', render: (_: unknown, row: CameraIntrinsicAssetDescriptor) => <Space wrap>{row.versions.map((version) => <Space key={version.version} size={4}><Button size="small" loading={busy} type={version.published ? 'primary' : 'default'} onClick={() => void loadIntrinsicVersion(row.id, version.version)}>加载 V{version.version}</Button>{version.published ? <Tag color="green">生效</Tag> : <Button size="small" loading={busy} onClick={() => void publishIntrinsicVersion(row.id, version.version, Boolean(row.publishedVersion && version.version < row.latestVersion))}>{row.publishedVersion && version.version < row.latestVersion ? `回滚到 V${version.version}` : `发布 V${version.version}`}</Button>}</Space>)}</Space> }
            ]} />
          <Space wrap>
            <Button danger loading={busy} onClick={() => void disableOutliers()}>禁用离群/最差视图</Button>
            <Button loading={busy} onClick={() => void exportDocument()}>导出完整会话文档</Button>
            <Button type="primary" loading={busy} disabled={!canApplyToSelectedNode} onClick={applyModel}>
              应用到所选节点{selectedNodeType ? `（${selectedNodeType}）` : ''}
            </Button>
            <Button onClick={() => setStep(2)}>返回审核</Button>
          </Space>
          {documentJson && <Input.TextArea rows={5} value={documentJson} readOnly />}
        </Space>
      )}
    </div>
  );
}
