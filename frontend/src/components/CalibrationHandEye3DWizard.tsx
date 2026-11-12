import { useEffect, useMemo, useRef, useState } from 'react';
import { apiErrorMessage } from '../apiErrors';
import { Alert, Button, Input, InputNumber, Select, Space, Steps, Table, Tag, Typography, message } from 'antd';
import type { HandEye3DCalibrationAssetDescriptor, HandEye3DCalibrationVersionSnapshot, HandEye3DWizardSessionDocument, HandEye3DWizardSessionSummary } from '../types';
import { apiFetch } from '../apiClient';
import { calibration, handEye3DCalibrations } from '../endpoints';
import { download, splitNumericRows } from './handEyeWizardShared';

type Props = {
  canApplyToSelectedNode: boolean;
  selectedNodeType?: string;
  onApplyToSelectedNode: (parameters: Record<string, unknown>) => void;
};

type SampleRow = {
  key: string;
  robotToBase: string;
  targetToCamera: string;
};

type Residual = {
  index: number;
  translationError: number;
  rotationErrorDeg: number;
  rejected?: boolean;
  robotTranslation: { x: number; y: number; z: number };
  targetTranslation: { x: number; y: number; z: number };
};

const identityText = `[
  [1, 0, 0, 0],
  [0, 1, 0, 0],
  [0, 0, 1, 0],
  [0, 0, 0, 1]
]`;

function parseMatrix(text: string, label: string): number[] {
  let parsed: unknown;
  try { parsed = JSON.parse(text); }
  catch { throw new Error(`${label} 不是有效 JSON 矩阵`); }
  const values = Array.isArray(parsed) && parsed.length === 4 && parsed.every(Array.isArray)
    ? parsed.flat()
    : parsed;
  if (!Array.isArray(values) || values.length !== 16 || values.some((x) => typeof x !== 'number' || !Number.isFinite(x)))
    throw new Error(`${label} 必须是 4×4 数值矩阵`);
  return values as number[];
}

function matrixError(row: SampleRow, field: 'robotToBase' | 'targetToCamera', label: string): string | undefined {
  try { parseMatrix(row[field], label); return undefined; }
  catch (error) { return error instanceof Error ? error.message : `${label} 无效`; }
}

const matInverse = (t: number[]): number[] => {
  const r = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  for (let i = 0; i < 3; i += 1) for (let j = 0; j < 3; j += 1) r[i * 4 + j] = t[j * 4 + i];
  for (let i = 0; i < 3; i += 1) r[i * 4 + 3] = -(r[i * 4] * t[3] + r[i * 4 + 1] * t[7] + r[i * 4 + 2] * t[11]);
  return r;
};

const matMultiply = (a: number[], b: number[]): number[] => {
  const r = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  for (let i = 0; i < 3; i += 1) {
    for (let j = 0; j < 3; j += 1) r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j];
    r[i * 4 + 3] = a[i * 4] * b[3] + a[i * 4 + 1] * b[7] + a[i * 4 + 2] * b[11] + a[i * 4 + 3];
  }
  return r;
};

const rotationAngle = (t: number[]) => Math.acos(Math.max(-1, Math.min(1, (t[0] + t[5] + t[10] - 1) / 2)));

const rotationAxis = (t: number[], angle: number): number[] | null => {
  const s = 2 * Math.sin(angle);
  if (Math.abs(s) < 1e-7) return null;
  const axis = [(t[9] - t[6]) / s, (t[2] - t[8]) / s, (t[4] - t[1]) / s];
  const len = Math.hypot(axis[0], axis[1], axis[2]);
  return len < 1e-9 ? null : axis.map((x) => x / len);
};

const motionPrecheck = (matrices: number[][]) => {
  if (matrices.length < 2) return { axisCount: 0, nonParallel: false, ok: false };
  const origin = matrices[0];
  const axes: number[][] = [];
  for (let i = 1; i < matrices.length; i += 1) {
    const relative = matMultiply(matInverse(origin), matrices[i]);
    const angle = rotationAngle(relative);
    if (angle < 3 * Math.PI / 180) continue;
    const axis = rotationAxis(relative, angle);
    if (axis) axes.push(axis);
  }
  const nonParallel = axes.some((a) => axes.some((b) => Math.abs(a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) < 0.95));
  return { axisCount: axes.length, nonParallel, ok: axes.length >= 2 && nonParallel };
};

const toSamplesJson = (samples: SampleRow[]) => JSON.stringify({
  samples: samples.map((row, index) => ({
    robotToBase: parseMatrix(row.robotToBase, `样本 ${index + 1} 机器人位姿`),
    targetToCamera: parseMatrix(row.targetToCamera, `样本 ${index + 1} 标定板位姿`)
  }))
}, null, 2);

const rowsFromSamplesJson = (json: string): SampleRow[] => {
  const parsed = JSON.parse(json) as { samples?: Array<{ robotToBase: unknown; targetToCamera: unknown }> };
  if (!Array.isArray(parsed.samples)) throw new Error('JSON 需包含 samples 数组');
  return parsed.samples.map((s, i) => ({
    key: String(i),
    robotToBase: JSON.stringify(s.robotToBase),
    targetToCamera: JSON.stringify(s.targetToCamera)
  }));
};

const rowsFromCsv = (text: string): SampleRow[] => splitNumericRows(text, 32, /robot0/i, '：robot 16 + target 16')
  .map((values, index) => ({
      key: String(index),
      robotToBase: JSON.stringify(values.slice(0, 16)),
      targetToCamera: JSON.stringify(values.slice(16, 32))
    }));

const methodOptions = [
  { value: 'DANIILIDIS', label: 'Daniilidis（双四元数）' }, { value: 'TSAI', label: 'Tsai' },
  { value: 'PARK', label: 'Park' }, { value: 'HORAUD', label: 'Horaud' }, { value: 'ANDREFF', label: 'Andreff' }
];

export default function CalibrationHandEye3DWizard({ canApplyToSelectedNode, selectedNodeType, onApplyToSelectedNode }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [mode, setMode] = useState('eyeInHand');
  const [method, setMethod] = useState('DANIILIDIS');
  const [sourceFrame, setSourceFrame] = useState('Camera');
  const [targetFrame, setTargetFrame] = useState('');
  const [unit, setUnit] = useState('mm');
  const [rotationWeight, setRotationWeight] = useState(50);
  const [maxTranslationError, setMaxTranslationError] = useState(0);
  const [maxRotationErrorDeg, setMaxRotationErrorDeg] = useState(0);
  const [outlierRejection, setOutlierRejection] = useState('none');
  const [crossCheckMethod, setCrossCheckMethod] = useState('TSAI');
  const [minTranslationSpan, setMinTranslationSpan] = useState(0);
  const [samples, setSamples] = useState<SampleRow[]>([]);
  const [result, setResult] = useState<Record<string, unknown>>();
  const [residuals, setResiduals] = useState<Residual[]>([]);
  const [sessionId, setSessionId] = useState<string>();
  const [sessionName, setSessionName] = useState('');
  const [sessions, setSessions] = useState<HandEye3DWizardSessionSummary[]>([]);
  const [assets, setAssets] = useState<HandEye3DCalibrationAssetDescriptor[]>([]);
  const [assetId, setAssetId] = useState('');
  const [assetName, setAssetName] = useState('');
  const [assetVersions, setAssetVersions] = useState<Record<string, number>>({});
  const [pinnedAssetId, setPinnedAssetId] = useState<string>();
  const [pinnedVersion, setPinnedVersion] = useState(0);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const sampleErrors = useMemo(() => samples.map((row, index) => ({
    key: row.key,
    robot: matrixError(row, 'robotToBase', `样本 ${index + 1} 机器人位姿`),
    target: matrixError(row, 'targetToCamera', `样本 ${index + 1} 标定板位姿`)
  })), [samples]);
  const firstError = sampleErrors.flatMap((x) => [x.robot, x.target]).find(Boolean);

  const motion = useMemo(() => {
    if (firstError) return { axisCount: 0, nonParallel: false, ok: false };
    try { return motionPrecheck(samples.map((row) => parseMatrix(row.robotToBase, 'robot'))); }
    catch { return { axisCount: 0, nonParallel: false, ok: false }; }
  }, [samples, firstError]);

  const canSolve = samples.length >= 6 && !firstError && samples.length <= 1000 && motion.ok;
  const canApply3D = canApplyToSelectedNode && selectedNodeType === 'calibration.handEye3D';

  const resetResult = () => { setResult(undefined); setResiduals([]); };

  const updateSample = (key: string, field: 'robotToBase' | 'targetToCamera', value: string) => {
    setSamples((current) => current.map((row) => row.key === key ? { ...row, [field]: value } : row));
    resetResult();
  };

  const addSample = () => {
    setSamples((current) => [...current, { key: `${Date.now()}-${current.length}`, robotToBase: identityText, targetToCamera: identityText }]);
    resetResult();
  };

  const removeSample = (key: string) => {
    setSamples((current) => current.filter((row) => row.key !== key));
    resetResult();
  };

  const refreshSessions = async () => {
    try {
      const response = await apiFetch(calibration.wizardHandEye3DSessions);
      const data = await response.json();
      if (response.ok && Array.isArray(data)) setSessions(data as HandEye3DWizardSessionSummary[]);
    } catch { /* best-effort */ }
  };

  const refreshAssets = async () => {
    try {
      const response = await apiFetch(handEye3DCalibrations.list);
      const data = await response.json();
      if (response.ok && Array.isArray(data)) setAssets(data as HandEye3DCalibrationAssetDescriptor[]);
    } catch { /* best-effort */ }
  };

  useEffect(() => { void refreshSessions(); void refreshAssets(); }, []);

  const solve = async () => {
    let samplesJson: string;
    try { samplesJson = toSamplesJson(samples); }
    catch (error) { messageApi.error(error instanceof Error ? error.message : '矩阵格式无效'); return; }
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardHandEye3DSolve, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          samplesJson, mode, method, maxTranslationError, maxRotationErrorDeg, rotationWeight,
          sourceFrame, targetFrame, unit, outlierRejection, crossCheckMethod, minTranslationSpan
        })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(apiErrorMessage(data, '三维手眼求解失败'));
      setResult(data);
      try { setResiduals(JSON.parse(String(data.sampleResidualsJson ?? '[]')) as Residual[]); }
      catch { setResiduals([]); }
      setStep(2);
      messageApi.success(`求解完成 · 平移 RMS ${Number(data.translationRms ?? 0).toFixed(4)} ${unit} · 旋转 RMS ${Number(data.rotationRmsDeg ?? 0).toFixed(3)}°`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '三维手眼求解失败'); }
    finally { setBusy(false); }
  };

  const removeWorst = () => {
    const worstIndex = Number(result?.worstSampleIndex);
    if (!Number.isInteger(worstIndex) || worstIndex < 0 || worstIndex >= samples.length) return;
    const key = samples[worstIndex]?.key;
    if (!key) return;
    removeSample(key);
    setStep(1);
    messageApi.info(`已移除综合残差最大的样本 #${worstIndex}，请重新求解`);
  };

  const saveSession = async (): Promise<string | undefined> => {
    const name = sessionName.trim() || `三维手眼会话 ${new Date().toLocaleString()}`;
    const response = await apiFetch(calibration.wizardHandEye3DSessions, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        sessionId, name, samplesJson: toSamplesJson(samples), mode, method, rotationWeight,
        outlierRejection, crossCheckMethod, maxTranslationError, maxRotationErrorDeg, minTranslationSpan,
        sourceFrame, targetFrame, unit,
        lastSolveSummaryJson: result?.summary ? JSON.stringify(result.summary) : null
      })
    });
    const data = await response.json() as HandEye3DWizardSessionDocument & { error?: string };
    if (!response.ok) throw new Error(apiErrorMessage(data, '保存会话失败'));
    setSessionId(data.sessionId);
    setSessionName(data.name);
    await refreshSessions();
    return data.sessionId;
  };

  const handleSaveSession = async () => {
    setBusy(true);
    try { await saveSession(); messageApi.success('会话已保存（刷新后可恢复）'); }
    catch (error) { messageApi.error(error instanceof Error ? error.message : '保存失败'); }
    finally { setBusy(false); }
  };

  const applyDocument = (document: HandEye3DWizardSessionDocument) => {
    setSamples(rowsFromSamplesJson(document.samplesJson));
    setMode(document.mode);
    setMethod(document.method);
    setRotationWeight(document.rotationWeight);
    setOutlierRejection(document.outlierRejection);
    setCrossCheckMethod(document.crossCheckMethod);
    setMaxTranslationError(document.maxTranslationError);
    setMaxRotationErrorDeg(document.maxRotationErrorDeg);
    setMinTranslationSpan(document.minTranslationSpan);
    setSourceFrame(document.sourceFrame);
    setTargetFrame(document.targetFrame);
    setUnit(document.unit);
    setSessionId(document.sessionId);
    setSessionName(document.name);
    resetResult();
  };

  const loadSession = async (id: string) => {
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardHandEye3DSession(id));
      const data = await response.json() as HandEye3DWizardSessionDocument & { error?: string };
      if (!response.ok) throw new Error(apiErrorMessage(data, '加载会话失败'));
      applyDocument(data);
      setStep(1);
      messageApi.success(`已加载会话「${data.name}」`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '加载失败'); }
    finally { setBusy(false); }
  };

  const deleteSession = async (id: string) => {
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardHandEye3DSession(id), { method: 'DELETE' });
      if (!response.ok) throw new Error(apiErrorMessage(await response.json(), '删除会话失败'));
      if (sessionId === id) { setSessionId(undefined); setSessionName(''); }
      await refreshSessions();
      messageApi.success('会话已删除');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '删除失败'); }
    finally { setBusy(false); }
  };

  const importFile = async (file: File) => {
    try {
      const text = await file.text();
      const rows = /\.csv$/i.test(file.name) ? rowsFromCsv(text) : rowsFromSamplesJson(text);
      if (rows.length === 0) throw new Error('文件中没有样本');
      setSamples(rows);
      resetResult();
      messageApi.success(`已导入 ${rows.length} 组位姿对`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导入失败'); }
  };

  const exportCsv = () => {
    const header = [...Array.from({ length: 16 }, (_, i) => `robot${i}`), ...Array.from({ length: 16 }, (_, i) => `target${i}`)].join(',');
    const lines = samples.map((row) => [...parseMatrix(row.robotToBase, 'robot'), ...parseMatrix(row.targetToCamera, 'target')].join(','));
    download('handeye3d-samples.csv', [header, ...lines].join('\n'), 'text/csv');
  };

  const saveAsset = async () => {
    if (!result) return messageApi.warning('请先完成三维手眼求解');
    if (!assetId.trim() || !assetName.trim()) return messageApi.warning('请填写资产 ID 和名称');
    setBusy(true);
    try {
      const savedSessionId = await saveSession();
      const existing = assets.find((asset) => asset.id === assetId.trim());
      const url = existing ? handEye3DCalibrations.versions(existing.id) : handEye3DCalibrations.list;
      const body = existing
        ? { sessionId: savedSessionId, note: `Wizard save ${new Date().toISOString()}` }
        : { sessionId: savedSessionId, id: assetId.trim(), name: assetName.trim(), description: 'SE(3) hand-eye calibration with pose-pair evidence', note: 'Initial 3D hand-eye calibration' };
      const response = await apiFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const data = await response.json() as Record<string, unknown>;
      if (!response.ok) throw new Error(apiErrorMessage(data, '保存三维手眼资产失败'));
      const version = Number(data.version ?? data.latestVersion ?? 0);
      if (!Number.isInteger(version) || version < 1) throw new Error('服务端未返回有效版本号');
      setPinnedAssetId(assetId.trim());
      setPinnedVersion(version);
      await refreshAssets();
      messageApi.success(`已保存三维手眼资产 ${assetId.trim()} V${version}，导出时将附带版本锁定`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '保存失败'); }
    finally { setBusy(false); }
  };

  const loadAssetVersion = async (id: string, version: number) => {
    setBusy(true);
    try {
      const response = await apiFetch(handEye3DCalibrations.version(id, String(version)));
      const snapshot = await response.json() as HandEye3DCalibrationVersionSnapshot & { error?: string };
      if (!response.ok) throw new Error(snapshot.error ?? '加载三维手眼版本失败');
      const document = JSON.parse(snapshot.sourceDocumentJson) as HandEye3DWizardSessionDocument;
      applyDocument(document);
      setAssetId(id);
      setAssetName(assets.find((asset) => asset.id === id)?.name ?? id);
      setPinnedAssetId(id);
      setPinnedVersion(version);
      setStep(1);
      messageApi.success(`已加载三维手眼资产 ${id} V${version} 的样本与设置，请重新求解验证`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '加载失败'); }
    finally { setBusy(false); }
  };

  const publishAssetVersion = async (id: string, version: number, rollback = false) => {
    setBusy(true);
    try {
      const url = rollback ? handEye3DCalibrations.rollback(id, String(version)) : handEye3DCalibrations.publish(id, String(version));
      const response = await apiFetch(url, { method: 'POST' });
      const data = await response.json() as { error?: string };
      if (!response.ok) throw new Error(apiErrorMessage(data, '发布三维手眼版本失败'));
      await refreshAssets();
      messageApi.success(`${rollback ? '已回滚' : '已发布'}三维手眼资产 ${id} V${version}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '发布失败'); }
    finally { setBusy(false); }
  };

  const apply = () => {
    onApplyToSelectedNode({
      samplesJson: toSamplesJson(samples), mode, method, maxTranslationError, maxRotationErrorDeg, rotationWeight,
      sourceFrame, ...(targetFrame ? { targetFrame } : {}), unit, outlierRejection, crossCheckMethod, minTranslationSpan,
      ...(pinnedAssetId && pinnedVersion > 0 ? { handEye3DAssetId: pinnedAssetId, handEye3DAssetVersion: pinnedVersion } : {})
    });
    messageApi.success('三维手眼参数已写入 calibration.handEye3D 节点');
    setStep(3);
  };

  const matrixEditor = (field: 'robotToBase' | 'targetToCamera', label: string) => (_: unknown, row: SampleRow, index: number) => {
    const error = matrixError(row, field, `样本 ${index + 1} ${label}`);
    return <Space direction="vertical" style={{ width: '100%' }} size={4}>
      <Input.TextArea rows={5} value={row[field]} onChange={(event) => updateSample(row.key, field, event.target.value)} spellCheck={false} />
      {error && <Typography.Text type="danger" style={{ fontSize: 11 }}>{error}</Typography.Text>}
    </Space>;
  };

  const transform = result?.transform as { sourceFrame?: string; targetFrame?: string; unit?: string; matrix?: number[] } | undefined;
  const transformJson = transform ? JSON.stringify(transform, null, 2) : '';
  const rejectedIndices = useMemo(() => {
    try { return JSON.parse(String(result?.rejectedSampleIndicesJson ?? '[]')) as number[]; }
    catch { return []; }
  }, [result]);

  return (
    <div>
      {contextHolder}
      <Steps size="small" current={step} items={['配置', '录入矩阵对', '求解与残差', '应用'].map((title) => ({ title }))} style={{ marginBottom: 16 }}
        onChange={setStep} />
      <Alert type="info" showIcon style={{ marginBottom: 12 }} title="三维手眼向导（SE(3)）"
        description="逐样本录入机器人基座到工具、标定板到相机的刚性 4×4 变换，或从 JSON/CSV 批量导入。至少六组样本并覆盖两个非平行旋转轴；支持鲁棒剔除、多方法交叉验证、会话持久化与版本化资产，求解后回写 handEye3D 节点。" />

      {step === 0 && <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        <Space wrap>
          <Select style={{ width: 210 }} value={mode} onChange={(value) => { setMode(value); resetResult(); }} options={[
            { value: 'eyeInHand', label: '眼在手上：相机安装于工具' },
            { value: 'eyeToHand', label: '眼在手外：相机固定、标定板随工具' }
          ]} />
          <Select style={{ width: 210 }} value={method} onChange={(value) => { setMethod(value); resetResult(); }} options={methodOptions} />
          <Input addonBefore="sourceFrame" value={sourceFrame} onChange={(event) => { setSourceFrame(event.target.value); resetResult(); }} style={{ width: 220 }} />
          <Input addonBefore="targetFrame" placeholder="默认 RobotTool/Base" value={targetFrame} onChange={(event) => { setTargetFrame(event.target.value); resetResult(); }} style={{ width: 250 }} />
          <Select style={{ width: 130 }} value={unit} onChange={(value) => { setUnit(value); resetResult(); }} options={[
            { value: 'mm', label: '毫米 mm' }, { value: 'cm', label: '厘米 cm' }, { value: 'm', label: '米 m' }
          ]} />
        </Space>
        <Space wrap>
          <Select style={{ width: 240 }} value={outlierRejection} onChange={(value) => { setOutlierRejection(value); resetResult(); }} options={[
            { value: 'none', label: '鲁棒剔除：无（用全部样本）' },
            { value: 'iterative', label: '鲁棒剔除：迭代剔除坏样本' }
          ]} />
          <Select style={{ width: 220 }} value={crossCheckMethod} onChange={(value) => { setCrossCheckMethod(value); resetResult(); }} options={[
            { value: 'NONE', label: '交叉验证：无' }, ...methodOptions.map((o) => ({ value: o.value, label: `交叉验证：${o.label}` }))
          ]} />
          <InputNumber addonBefore="旋转权重" min={0.001} max={1000000} value={rotationWeight} onChange={(value) => { setRotationWeight(Number(value ?? 50)); resetResult(); }} />
        </Space>
        <Space wrap>
          <InputNumber addonBefore={`平移门限 ${unit}`} min={0} value={maxTranslationError} onChange={(value) => { setMaxTranslationError(Number(value ?? 0)); resetResult(); }} />
          <InputNumber addonBefore="旋转门限°" min={0} max={180} value={maxRotationErrorDeg} onChange={(value) => { setMaxRotationErrorDeg(Number(value ?? 0)); resetResult(); }} />
          <InputNumber addonBefore={`最小跨度 ${unit}`} min={0} value={minTranslationSpan} onChange={(value) => { setMinTranslationSpan(Number(value ?? 0)); resetResult(); }} />
          <Typography.Text type="secondary">门限填 0 表示关闭。输入矩阵的平移量和所选单位必须一致。</Typography.Text>
        </Space>
        <Button type="primary" onClick={() => setStep(1)}>下一步：录入样本</Button>
      </Space>}

      {step === 1 && <Space direction="vertical" style={{ width: '100%' }} size="middle">
        <Alert type={canSolve ? 'success' : 'warning'} showIcon
          title={samples.length < 6 ? `当前 ${samples.length} 组，至少需要 6 组`
            : firstError ? firstError
            : !motion.ok ? `旋转轴覆盖不足：检测到 ${motion.axisCount} 个 ≥3° 旋转轴${motion.nonParallel ? '' : '（缺乏非平行轴）'}，需至少两个非平行轴`
            : `样本数 ${samples.length}，矩阵格式有效，旋转轴覆盖 ${motion.axisCount} 个（含非平行轴）`}
          description="每个矩阵表示坐标从源框架映射到目标框架。可粘贴 4×4 嵌套数组或 16 个行优先数值；机器人矩阵方向为 RobotTool→RobotBase，视觉矩阵方向为 Target→Camera。" />
        <Space wrap>
          <Button onClick={addSample} disabled={samples.length >= 1000}>添加位姿对</Button>
          <Button onClick={() => fileInputRef.current?.click()}>导入 CSV/JSON</Button>
          <input ref={fileInputRef} type="file" accept=".json,.csv" style={{ display: 'none' }}
            onChange={(e) => { const file = e.target.files?.[0]; if (file) void importFile(file); e.target.value = ''; }} />
          <Button disabled={!samples.length} onClick={() => download('handeye3d-samples.json', toSamplesJson(samples), 'application/json')}>导出 JSON</Button>
          <Button disabled={!samples.length} onClick={exportCsv}>导出 CSV</Button>
        </Space>
        <Table size="small" pagination={false} rowKey="key" dataSource={samples} scroll={{ x: 1050 }}
          columns={[
            { title: '#', width: 48, render: (_: unknown, __: SampleRow, index: number) => index },
            { title: 'robotToBase · 工具→基座', width: 450, render: matrixEditor('robotToBase', '机器人矩阵') },
            { title: 'targetToCamera · 标定板→相机', width: 450, render: matrixEditor('targetToCamera', '视觉矩阵') },
            { title: '', width: 72, fixed: 'right', render: (_: unknown, row: SampleRow) => <Button size="small" danger onClick={() => removeSample(row.key)}>删除</Button> }
          ]} />
        <Space wrap>
          <Input addonBefore="会话名" placeholder="保存会话用" value={sessionName} onChange={(e) => setSessionName(e.target.value)} style={{ width: 260 }} />
          <Button loading={busy} disabled={!samples.length} onClick={() => void handleSaveSession()}>保存会话</Button>
        </Space>
        {sessions.length > 0 && (
          <Table size="small" pagination={false} rowKey="sessionId" dataSource={sessions}
            columns={[
              { title: '已保存会话', dataIndex: 'name' },
              { title: '样本数', dataIndex: 'sampleCount', width: 80 },
              { title: '更新时间', dataIndex: 'updatedAt', width: 200, render: (v: string) => v?.replace('T', ' ').slice(0, 19) },
              {
                title: '', width: 150, render: (_: unknown, r: HandEye3DWizardSessionSummary) => (
                  <Space>
                    <Button size="small" onClick={() => void loadSession(r.sessionId)}>加载</Button>
                    <Button size="small" danger onClick={() => void deleteSession(r.sessionId)}>删除</Button>
                  </Space>
                )
              }
            ]} />
        )}
        <Space>
          <Button onClick={() => setStep(0)}>上一步</Button>
          <Button type="primary" loading={busy} disabled={!canSolve} onClick={() => void solve()}>求解三维手眼（{samples.length} 组）</Button>
        </Space>
      </Space>}

      {step === 2 && <Space direction="vertical" style={{ width: '100%' }} size="middle">
        {result && <Alert type="success" showIcon
          title={`平移 RMS ${Number(result.translationRms ?? 0).toFixed(4)} ${unit} · 旋转 RMS ${Number(result.rotationRmsDeg ?? 0).toFixed(3)}° · 使用 ${String(result.usedSampleCount ?? samples.length)}/${samples.length} 组`}
          description={`最差样本 #${String(result.worstSampleIndex ?? '-')} · 最大平移 ${Number(result.maxTranslationError ?? 0).toFixed(4)} ${unit} · 最大旋转 ${Number(result.maxRotationErrorDeg ?? 0).toFixed(3)}° · 机器人位移跨度 ${Number(result.robotTranslationSpan ?? 0).toFixed(3)} ${unit}` +
            (result.outlierSampleIndex != null ? ` · 疑似坏样本 #${String(result.outlierSampleIndex)}（残差 > 3×中位数）` : '') +
            (rejectedIndices.length ? ` · 已剔除 [${rejectedIndices.join(', ')}]` : '') +
            (result.crossCheckTranslationDelta != null ? ` · 交叉验证 Δ平移 ${Number(result.crossCheckTranslationDelta).toFixed(4)} / Δ旋转 ${Number(result.crossCheckAngleDeltaDeg ?? 0).toFixed(3)}°` : '')} />}
        {transformJson && <Input.TextArea rows={6} value={transformJson} readOnly spellCheck={false} />}
        <Table size="small" pagination={false} rowKey="index" dataSource={residuals}
          rowClassName={(row) => Number(result?.worstSampleIndex) === row.index ? 'calibration-selected-row' : ''}
          columns={[
            { title: '#', dataIndex: 'index', width: 48 },
            { title: `平移残差 ${unit}`, render: (_: unknown, row: Residual) => row.translationError.toFixed(4) },
            { title: '旋转残差°', render: (_: unknown, row: Residual) => row.rotationErrorDeg.toFixed(3) },
            { title: 'Robot 平移', render: (_: unknown, row: Residual) => `${row.robotTranslation.x}, ${row.robotTranslation.y}, ${row.robotTranslation.z}` },
            { title: 'Target 平移', render: (_: unknown, row: Residual) => `${row.targetTranslation.x}, ${row.targetTranslation.y}, ${row.targetTranslation.z}` },
            {
              title: '', width: 130, render: (_: unknown, row: Residual) => (
                <Space size={4}>
                  {Number(result?.worstSampleIndex) === row.index && <Tag color="red">最差</Tag>}
                  {result?.outlierSampleIndex != null && Number(result.outlierSampleIndex) === row.index && <Tag color="orange">离群</Tag>}
                  {row.rejected && <Tag>已剔除</Tag>}
                </Space>
              )
            }
          ]} />
        <Space wrap>
          <Button danger onClick={removeWorst} disabled={!residuals.length || samples.length <= 6}>剔除综合残差最大样本</Button>
          <Button loading={busy} onClick={() => void solve()}>重新求解</Button>
          <Button type="primary" disabled={!canApply3D || !result} onClick={apply}>应用到 calibration.handEye3D 节点</Button>
          <Button onClick={() => setStep(1)}>返回录入</Button>
        </Space>
        {!canApply3D && <Typography.Text type="secondary">选择一个 calibration.handEye3D 节点后即可回写参数。</Typography.Text>}
        <Space wrap>
          <Input addonBefore="资产ID" placeholder="如 handeye3d-cell1" value={assetId} onChange={(e) => setAssetId(e.target.value)} style={{ width: 260 }} />
          <Input addonBefore="资产名" placeholder="资产名称" value={assetName} onChange={(e) => setAssetName(e.target.value)} style={{ width: 240 }} />
          <Button loading={busy} onClick={() => void saveAsset()}>保存为版本化资产</Button>
          {pinnedAssetId && <Tag color="blue">已锁定 {pinnedAssetId} V{pinnedVersion}</Tag>}
        </Space>
        {assets.length > 0 && (
          <Table size="small" pagination={false} rowKey="id" dataSource={assets}
            columns={[
              { title: '资产', dataIndex: 'id' },
              { title: '名称', dataIndex: 'name' },
              {
                title: '版本', width: 110, render: (_: unknown, r: HandEye3DCalibrationAssetDescriptor) => (
                  <Select size="small" style={{ width: 90 }} value={assetVersions[r.id] ?? r.latestVersion}
                    onChange={(v) => setAssetVersions((prev) => ({ ...prev, [r.id]: v }))}
                    options={r.versions.map((v) => ({ value: v.version, label: `V${v.version}${v.published ? '（已发布）' : ''}` }))} />
                  )
              },
              { title: '已发布', width: 80, render: (_: unknown, r: HandEye3DCalibrationAssetDescriptor) => r.publishedVersion != null ? `V${r.publishedVersion}` : '-' },
              {
                title: '', width: 220, render: (_: unknown, r: HandEye3DCalibrationAssetDescriptor) => {
                  const version = assetVersions[r.id] ?? r.latestVersion;
                  return (
                    <Space size={4}>
                      <Button size="small" onClick={() => void loadAssetVersion(r.id, version)}>加载</Button>
                      <Button size="small" onClick={() => void publishAssetVersion(r.id, version)}>发布</Button>
                      <Button size="small" danger onClick={() => void publishAssetVersion(r.id, version, true)}>回滚</Button>
                    </Space>
                  );
                }
              }
            ]} />
        )}
      </Space>}

      {step === 3 && <Space direction="vertical" style={{ width: '100%' }}>
        <Alert type="success" showIcon title="三维手眼参数已写入节点"
          description={pinnedAssetId ? `附带资产锁定 ${pinnedAssetId} V${pinnedVersion}：发布/产线校验会复核求解结果与该版本一致。` : '节点保存了本次样本和质量门限，可在工作流运行时重新计算并校验。'} />
        <Input.TextArea rows={12} value={toSamplesJson(samples)} readOnly spellCheck={false} />
        <Button onClick={() => setStep(1)}>继续编辑样本</Button>
      </Space>}
    </div>
  );
}