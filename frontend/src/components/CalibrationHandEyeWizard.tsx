import { useEffect, useMemo, useRef, useState } from 'react';
import { apiErrorMessage } from '../apiErrors';
import { Alert, Button, Input, InputNumber, Select, Space, Steps, Table, Tag, Typography, message } from 'antd';
import type { CalibrationHandEyeResidual, HandEyeCalibrationAssetDescriptor, HandEyeCalibrationVersionSnapshot, HandEyeWizardSessionDocument, HandEyeWizardSessionSummary } from '../types';
import { apiFetch } from '../apiClient';
import { calibration, handEyeCalibrations } from '../endpoints';
import { angularCoverage, download, splitNumericRows, translationSpan } from './handEyeWizardShared';

type Props = {
  canApplyToSelectedNode: boolean;
  selectedNodeType?: string;
  onApplyToSelectedNode: (parameters: Record<string, unknown>) => void;
};

type SampleRow = {
  key: string;
  robotX: number;
  robotY: number;
  robotThetaDeg: number;
  visionX: number;
  visionY: number;
  visionThetaDeg: number;
};

type Pose = { x: number; y: number; thetaDeg: number };

const steps = ['配置', '录入位姿对', '求解与残差', '导出'];

const toSamplesJson = (samples: SampleRow[]) => JSON.stringify({
  samples: samples.map((s) => ({
    robot: { x: s.robotX, y: s.robotY, thetaDeg: s.robotThetaDeg },
    vision: { x: s.visionX, y: s.visionY, thetaDeg: s.visionThetaDeg }
  }))
}, null, 2);

const rowsFromSamplesJson = (json: string): SampleRow[] => {
  const parsed = JSON.parse(json) as { samples?: Array<{ robot: Pose; vision: Pose }> };
  if (!Array.isArray(parsed.samples)) throw new Error('JSON 需包含 samples 数组');
  return parsed.samples.map((s, i) => {
    const values = [s.robot?.x, s.robot?.y, s.robot?.thetaDeg, s.vision?.x, s.vision?.y, s.vision?.thetaDeg];
    if (values.some((v) => typeof v !== 'number' || !Number.isFinite(v))) throw new Error(`样本 ${i} 含非有限数值`);
    return { key: String(i), robotX: s.robot.x, robotY: s.robot.y, robotThetaDeg: s.robot.thetaDeg, visionX: s.vision.x, visionY: s.vision.y, visionThetaDeg: s.vision.thetaDeg };
  });
};

const rowsFromCsv = (text: string): SampleRow[] => splitNumericRows(text, 6, /robotx/i)
  .map(([robotX, robotY, robotThetaDeg, visionX, visionY, visionThetaDeg], index) => ({
      key: String(index), robotX, robotY, robotThetaDeg, visionX, visionY, visionThetaDeg
    }));

export default function CalibrationHandEyeWizard({ canApplyToSelectedNode, selectedNodeType, onApplyToSelectedNode }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [mode, setMode] = useState('eyeInHand');
  const [sourceFrame, setSourceFrame] = useState('Camera');
  const [targetFrame, setTargetFrame] = useState('');
  const [unit, setUnit] = useState('mm');
  const [sourceUnit, setSourceUnit] = useState('mm');
  const [maxTranslationError, setMaxTranslationError] = useState(0);
  const [maxAngleErrorDeg, setMaxAngleErrorDeg] = useState(0);
  const [angularWeight, setAngularWeight] = useState(50);
  const [visionScale, setVisionScale] = useState(1);
  const [robustLoss, setRobustLoss] = useState('none');
  const [samples, setSamples] = useState<SampleRow[]>([
    { key: '0', robotX: 0, robotY: 0, robotThetaDeg: -20, visionX: 0, visionY: 0, visionThetaDeg: 10 },
    { key: '1', robotX: 40, robotY: 10, robotThetaDeg: -5, visionX: 12, visionY: -3, visionThetaDeg: 25 },
    { key: '2', robotX: 80, robotY: -20, robotThetaDeg: 15, visionX: 28, visionY: 8, visionThetaDeg: 45 },
    { key: '3', robotX: -10, robotY: 50, robotThetaDeg: 30, visionX: -5, visionY: 20, visionThetaDeg: 60 }
  ]);
  const [draft, setDraft] = useState({ robotX: 0, robotY: 0, robotThetaDeg: 0, visionX: 0, visionY: 0, visionThetaDeg: 0 });
  const [result, setResult] = useState<Record<string, unknown>>();
  const [residuals, setResiduals] = useState<CalibrationHandEyeResidual[]>([]);
  const [sessionId, setSessionId] = useState<string>();
  const [sessionName, setSessionName] = useState('');
  const [sessions, setSessions] = useState<HandEyeWizardSessionSummary[]>([]);
  const [assets, setAssets] = useState<HandEyeCalibrationAssetDescriptor[]>([]);
  const [assetId, setAssetId] = useState('');
  const [assetName, setAssetName] = useState('');
  const [assetVersions, setAssetVersions] = useState<Record<string, number>>({});
  const [pinnedAssetId, setPinnedAssetId] = useState<string>();
  const [pinnedVersion, setPinnedVersion] = useState(0);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const samplesJson = useMemo(() => toSamplesJson(samples), [samples]);

  const precheck = useMemo(() => {
    const robotCoverage = angularCoverage(samples.map((s) => s.robotThetaDeg));
    const visionCoverage = angularCoverage(samples.map((s) => s.visionThetaDeg));
    const robotSpan = translationSpan(samples.map((s) => ({ x: s.robotX, y: s.robotY })));
    const visionSpan = translationSpan(samples.map((s) => ({ x: s.visionX * visionScale, y: s.visionY * visionScale })));
    let duplicate: [number, number] | null = null;
    for (let i = 0; i < samples.length && !duplicate; i += 1) {
      for (let j = i + 1; j < samples.length; j += 1) {
        const a = samples[i];
        const b = samples[j];
        const sameTheta = Math.abs((((a.robotThetaDeg - b.robotThetaDeg) % 360) + 540) % 360 - 180) < 1e-6;
        if (Math.abs(a.robotX - b.robotX) < 1e-9 && Math.abs(a.robotY - b.robotY) < 1e-9 && sameTheta) { duplicate = [i, j]; break; }
      }
    }
    const issues: string[] = [];
    if (samples.length < 4) issues.push(`至少 4 对位姿（当前 ${samples.length}）`);
    if (robotCoverage < 8) issues.push(`机器人角度覆盖 ${robotCoverage.toFixed(1)}° < 8°`);
    if (visionCoverage < 8) issues.push(`视觉角度覆盖 ${visionCoverage.toFixed(1)}° < 8°`);
    if (robotSpan < 1e-3) issues.push('机器人平移跨度不足 0.001');
    if (visionSpan < 1e-3) issues.push('视觉平移跨度不足 0.001（检查 visionScale）');
    if (duplicate) issues.push(`样本 #${duplicate[0]} 与 #${duplicate[1]} 机器人位姿重复`);
    return { robotCoverage, visionCoverage, robotSpan, visionSpan, issues, ok: issues.length === 0 };
  }, [samples, visionScale]);

  const closures = useMemo(() => {
    const pose = result?.pose as Pose | undefined;
    if (!pose || typeof pose.x !== 'number') return [];
    const rad = (deg: number) => deg * Math.PI / 180;
    const mul = (a: { x: number; y: number; theta: number }, b: { x: number; y: number; theta: number }) => ({
      x: a.x + Math.cos(a.theta) * b.x - Math.sin(a.theta) * b.y,
      y: a.y + Math.sin(a.theta) * b.x + Math.cos(a.theta) * b.y,
      theta: a.theta + b.theta
    });
    const inv = (p: { x: number; y: number; theta: number }) => ({
      x: -Math.cos(p.theta) * p.x - Math.sin(p.theta) * p.y,
      y: Math.sin(p.theta) * p.x - Math.cos(p.theta) * p.y,
      theta: -p.theta
    });
    const handEye = { x: pose.x, y: pose.y, theta: rad(pose.thetaDeg) };
    return samples.map((s) => {
      const robot = { x: s.robotX, y: s.robotY, theta: rad(s.robotThetaDeg) };
      const vision = { x: s.visionX * visionScale, y: s.visionY * visionScale, theta: rad(s.visionThetaDeg) };
      const closure = mode === 'eyeToHand' ? mul(mul(inv(robot), handEye), vision) : mul(mul(robot, handEye), vision);
      return { x: closure.x, y: closure.y };
    });
  }, [samples, result, mode, visionScale]);

  const refreshSessions = async () => {
    try {
      const response = await apiFetch(calibration.wizardHandEyeSessions);
      const data = await response.json();
      if (response.ok && Array.isArray(data)) setSessions(data as HandEyeWizardSessionSummary[]);
    } catch { /* session list is best-effort */ }
  };

  const refreshAssets = async () => {
    try {
      const response = await apiFetch(handEyeCalibrations.list);
      const data = await response.json();
      if (response.ok && Array.isArray(data)) setAssets(data as HandEyeCalibrationAssetDescriptor[]);
    } catch { /* asset list is best-effort */ }
  };

  useEffect(() => { void refreshSessions(); void refreshAssets(); }, []);

  const addSample = () => {
    setSamples((prev) => [...prev, { key: String(Date.now()), ...draft }]);
    messageApi.success('已添加位姿对');
  };

  const removeSample = (key: string) => setSamples((prev) => prev.filter((s) => s.key !== key));

  const removeWorst = () => {
    if (!residuals.length || !result) return;
    const worstIndex = Number(result.worstSampleIndex);
    if (!Number.isInteger(worstIndex)) return;
    setSamples((prev) => prev.filter((_, i) => i !== worstIndex));
    setResiduals([]);
    setResult(undefined);
    setStep(1);
    messageApi.info(`已移除综合残差最大样本 #${worstIndex}，请重新求解`);
  };

  const solve = async () => {
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardHandEyeSolve, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          samplesJson, mode, maxTranslationError, maxAngleErrorDeg,
          sourceFrame, targetFrame, unit, sourceUnit, angularWeight, visionScale, robustLoss
        })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(apiErrorMessage(data, '手眼求解失败'));
      setResult(data);
      try {
        const rows = JSON.parse(String(data.sampleResidualsJson ?? '[]')) as CalibrationHandEyeResidual[];
        setResiduals(rows);
      } catch { setResiduals([]); }
      setStep(2);
      messageApi.success(`求解完成 · 平移 RMS ${Number(data.translationRms ?? 0).toFixed(4)} · 角度 RMS ${Number(data.angleRmsDeg ?? 0).toFixed(3)}°`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '求解失败'); }
    finally { setBusy(false); }
  };

  const saveSession = async (): Promise<string | undefined> => {
    const name = sessionName.trim() || `手眼会话 ${new Date().toLocaleString()}`;
    const response = await apiFetch(calibration.wizardHandEyeSessions, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        sessionId, name, samplesJson, mode, angularWeight, visionScale, robustLoss,
        maxTranslationError, maxAngleErrorDeg, sourceFrame, targetFrame, unit, sourceUnit,
        lastSolveSummaryJson: result?.summary ? JSON.stringify(result.summary) : null
      })
    });
    const data = await response.json() as HandEyeWizardSessionDocument & { error?: string };
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

  const applyDocument = (document: HandEyeWizardSessionDocument) => {
    setSamples(rowsFromSamplesJson(document.samplesJson));
    setMode(document.mode);
    setAngularWeight(document.angularWeight);
    setVisionScale(document.visionScale);
    setRobustLoss(document.robustLoss);
    setMaxTranslationError(document.maxTranslationError);
    setMaxAngleErrorDeg(document.maxAngleErrorDeg);
    setSourceFrame(document.sourceFrame);
    setTargetFrame(document.targetFrame);
    setUnit(document.unit);
    setSourceUnit(document.sourceUnit);
    setSessionId(document.sessionId);
    setSessionName(document.name);
    setResult(undefined);
    setResiduals([]);
  };

  const loadSession = async (id: string) => {
    setBusy(true);
    try {
      const response = await apiFetch(calibration.wizardHandEyeSession(id));
      const data = await response.json() as HandEyeWizardSessionDocument & { error?: string };
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
      const response = await apiFetch(calibration.wizardHandEyeSession(id), { method: 'DELETE' });
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
      setResult(undefined);
      setResiduals([]);
      messageApi.success(`已导入 ${rows.length} 对位姿`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导入失败'); }
  };

  const exportCsv = () => {
    const header = 'robotX,robotY,robotThetaDeg,visionX,visionY,visionThetaDeg';
    const lines = samples.map((s) => [s.robotX, s.robotY, s.robotThetaDeg, s.visionX, s.visionY, s.visionThetaDeg].join(','));
    download('handeye-samples.csv', [header, ...lines].join('\n'), 'text/csv');
  };

  const saveAsset = async () => {
    if (!result) return messageApi.warning('请先完成手眼求解');
    if (!assetId.trim() || !assetName.trim()) return messageApi.warning('请填写资产 ID 和名称');
    setBusy(true);
    try {
      const savedSessionId = await saveSession();
      const existing = assets.find((asset) => asset.id === assetId.trim());
      const url = existing ? handEyeCalibrations.versions(existing.id) : handEyeCalibrations.list;
      const body = existing
        ? { sessionId: savedSessionId, note: `Wizard save ${new Date().toISOString()}` }
        : { sessionId: savedSessionId, id: assetId.trim(), name: assetName.trim(), description: 'Planar hand-eye calibration with pose-pair evidence', note: 'Initial hand-eye calibration' };
      const response = await apiFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const data = await response.json() as Record<string, unknown>;
      if (!response.ok) throw new Error(apiErrorMessage(data, '保存手眼资产失败'));
      const version = Number(data.version ?? data.latestVersion ?? 0);
      if (!Number.isInteger(version) || version < 1) throw new Error('服务端未返回有效版本号');
      setPinnedAssetId(assetId.trim());
      setPinnedVersion(version);
      await refreshAssets();
      messageApi.success(`已保存手眼资产 ${assetId.trim()} V${version}，导出时将附带版本锁定`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '保存失败'); }
    finally { setBusy(false); }
  };

  const loadAssetVersion = async (id: string, version: number) => {
    setBusy(true);
    try {
      const response = await apiFetch(handEyeCalibrations.version(id, String(version)));
      const snapshot = await response.json() as HandEyeCalibrationVersionSnapshot & { error?: string };
      if (!response.ok) throw new Error(snapshot.error ?? '加载手眼版本失败');
      const document = JSON.parse(snapshot.sourceDocumentJson) as HandEyeWizardSessionDocument;
      applyDocument(document);
      setAssetId(id);
      setAssetName(assets.find((asset) => asset.id === id)?.name ?? id);
      setPinnedAssetId(id);
      setPinnedVersion(version);
      setStep(1);
      messageApi.success(`已加载手眼资产 ${id} V${version} 的样本与设置，请重新求解验证`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '加载失败'); }
    finally { setBusy(false); }
  };

  const publishAssetVersion = async (id: string, version: number, rollback = false) => {
    setBusy(true);
    try {
      const url = rollback ? handEyeCalibrations.rollback(id, String(version)) : handEyeCalibrations.publish(id, String(version));
      const response = await apiFetch(url, { method: 'POST' });
      const data = await response.json() as { error?: string };
      if (!response.ok) throw new Error(apiErrorMessage(data, '发布手眼版本失败'));
      await refreshAssets();
      messageApi.success(`${rollback ? '已回滚' : '已发布'}手眼资产 ${id} V${version}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '发布失败'); }
    finally { setBusy(false); }
  };

  const apply = () => {
    onApplyToSelectedNode({
      samplesJson, mode, maxTranslationError, maxAngleErrorDeg,
      sourceFrame,
      ...(targetFrame ? { targetFrame } : {}),
      unit, sourceUnit, angularWeight, visionScale, robustLoss,
      ...(pinnedAssetId && pinnedVersion > 0 ? { handEyeAssetId: pinnedAssetId, handEyeAssetVersion: pinnedVersion } : {})
    });
    messageApi.success(selectedNodeType === 'calibration.handEye2D'
      ? '已写入 handEye2D 节点参数'
      : '已导出参数（请选中 calibration.handEye2D 节点以精确写入）');
    setStep(3);
  };

  const closureScatter = (() => {
    if (closures.length === 0) return null;
    const xs = closures.map((p) => p.x);
    const ys = closures.map((p) => p.y);
    const minX = Math.min(...xs); const maxX = Math.max(...xs);
    const minY = Math.min(...ys); const maxY = Math.max(...ys);
    const pad = Math.max(maxX - minX, maxY - minY, 1e-6) * 0.15 + 1e-6;
    const width = 320; const height = 220;
    const sx = (x: number) => ((x - (minX - pad)) / (maxX - minX + 2 * pad)) * width;
    const sy = (y: number) => height - ((y - (minY - pad)) / (maxY - minY + 2 * pad)) * height;
    const meanX = xs.reduce((a, b) => a + b, 0) / xs.length;
    const meanY = ys.reduce((a, b) => a + b, 0) / ys.length;
    const spread = Math.max(...closures.map((p) => Math.hypot(p.x - meanX, p.y - meanY)));
    return (
      <div>
        <Typography.Text type="secondary">闭合散点（求解后各样本的 Robot∘X∘Vision 应聚于一点；散布即残差）</Typography.Text>
        <svg width={width} height={height} style={{ display: 'block', background: '#fafafa', border: '1px solid #f0f0f0', borderRadius: 4 }} role="img" aria-label="闭合散点图">
          {closures.map((p, i) => (
            <circle key={i} cx={sx(p.x)} cy={sy(p.y)} r={3} fill={Number(result?.worstSampleIndex) === i ? '#ff4d4f' : '#1677ff'} />
          ))}
          <line x1={sx(meanX) - 6} y1={sy(meanY)} x2={sx(meanX) + 6} y2={sy(meanY)} stroke="#52c41a" strokeWidth={1.5} />
          <line x1={sx(meanX)} y1={sy(meanY) - 6} x2={sx(meanX)} y2={sy(meanY) + 6} stroke="#52c41a" strokeWidth={1.5} />
        </svg>
        <Typography.Text type="secondary">最大散布 {spread.toFixed(4)} {unit} · 均值 ({meanX.toFixed(3)}, {meanY.toFixed(3)})</Typography.Text>
      </div>
    );
  })();

  return (
    <div>
      {contextHolder}
      <Steps size="small" current={step} items={steps.map((title) => ({ title }))} style={{ marginBottom: 16 }}
        onChange={setStep} />
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        title="二维手眼向导（平面 SE(2)）"
        description="录入机器人位姿 + 视觉位姿对，求解后查看逐样本残差与闭合散点，可剔除最差样本后重算；支持会话持久化、CSV/JSON 导入导出、版本化资产保存，并导出到 calibration.handEye2D。仅平面手眼，非 3D。" />

      {step === 0 && (
        <Space direction="vertical" size="middle">
          <Space wrap>
            <Select style={{ width: 180 }} value={mode} onChange={setMode} options={[
              { value: 'eyeInHand', label: '眼在手上 eyeInHand' },
              { value: 'eyeToHand', label: '眼在手外 eyeToHand' }
            ]} />
            <Input addonBefore="sourceFrame" value={sourceFrame} onChange={(e) => setSourceFrame(e.target.value)} style={{ width: 200 }} />
            <Input addonBefore="targetFrame" placeholder="默认 RobotTool/Base" value={targetFrame} onChange={(e) => setTargetFrame(e.target.value)} style={{ width: 240 }} />
            <Input addonBefore="unit" value={unit} onChange={(e) => setUnit(e.target.value)} style={{ width: 140 }} />
            <Input addonBefore="sourceUnit" value={sourceUnit} onChange={(e) => setSourceUnit(e.target.value)} style={{ width: 160 }} />
          </Space>
          <Space wrap>
            <InputNumber addonBefore="visionScale" min={1e-6} max={1e6} step={0.001} value={visionScale} onChange={(v) => setVisionScale(Number(v ?? 1))} style={{ width: 200 }} />
            <InputNumber addonBefore="angularWeight" min={0.001} max={1e6} value={angularWeight} onChange={(v) => setAngularWeight(Number(v ?? 50))} style={{ width: 220 }} />
            <Select style={{ width: 220 }} value={robustLoss} onChange={setRobustLoss} options={[
              { value: 'none', label: '鲁棒加权：无（最小二乘）' },
              { value: 'tukey', label: '鲁棒加权：Tukey（剔除坏样本）' }
            ]} />
            <InputNumber addonBefore="maxTrans" min={0} value={maxTranslationError} onChange={(v) => setMaxTranslationError(Number(v ?? 0))} />
            <InputNumber addonBefore="maxAngle°" min={0} value={maxAngleErrorDeg} onChange={(v) => setMaxAngleErrorDeg(Number(v ?? 0))} />
          </Space>
          {sourceUnit !== unit && visionScale === 1 && (
            <Alert type="warning" showIcon title="单位不一致" description={`sourceUnit=${sourceUnit} 与 unit=${unit} 不同且 visionScale=1：视觉坐标将按原值参与求解。请设置 visionScale（目标单位/视觉单位，如 mm/px）或预先换算样本。`} />
          )}
          <Button type="primary" onClick={() => setStep(1)}>下一步：录入样本</Button>
        </Space>
      )}

      {step === 1 && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          <Alert type={precheck.ok ? 'success' : 'warning'} showIcon
            title={`样本预检：机器人角度覆盖 ${precheck.robotCoverage.toFixed(1)}° · 视觉角度覆盖 ${precheck.visionCoverage.toFixed(1)}° · 机器人跨度 ${precheck.robotSpan.toFixed(3)} · 视觉跨度 ${precheck.visionSpan.toFixed(3)}`}
            description={precheck.ok ? '满足求解条件（≥4 对互异位姿，角度覆盖 ≥8°，平移跨度 ≥0.001）。' : precheck.issues.join('；')} />
          <Space wrap>
            <InputNumber addonBefore="Rx" value={draft.robotX} onChange={(v) => setDraft((d) => ({ ...d, robotX: Number(v ?? 0) }))} />
            <InputNumber addonBefore="Ry" value={draft.robotY} onChange={(v) => setDraft((d) => ({ ...d, robotY: Number(v ?? 0) }))} />
            <InputNumber addonBefore="Rθ°" value={draft.robotThetaDeg} onChange={(v) => setDraft((d) => ({ ...d, robotThetaDeg: Number(v ?? 0) }))} />
            <InputNumber addonBefore="Vx" value={draft.visionX} onChange={(v) => setDraft((d) => ({ ...d, visionX: Number(v ?? 0) }))} />
            <InputNumber addonBefore="Vy" value={draft.visionY} onChange={(v) => setDraft((d) => ({ ...d, visionY: Number(v ?? 0) }))} />
            <InputNumber addonBefore="Vθ°" value={draft.visionThetaDeg} onChange={(v) => setDraft((d) => ({ ...d, visionThetaDeg: Number(v ?? 0) }))} />
            <Button onClick={addSample}>添加位姿对</Button>
          </Space>
          <Table size="small" pagination={false} rowKey="key" dataSource={samples}
            columns={[
              { title: '#', width: 48, render: (_: unknown, __: SampleRow, i: number) => i },
              { title: 'Robot X/Y/θ', render: (_: unknown, r: SampleRow) => `${r.robotX}, ${r.robotY}, ${r.robotThetaDeg}` },
              { title: 'Vision X/Y/θ', render: (_: unknown, r: SampleRow) => `${r.visionX}, ${r.visionY}, ${r.visionThetaDeg}` },
              { title: '', width: 72, render: (_: unknown, r: SampleRow) => <Button size="small" danger onClick={() => removeSample(r.key)}>删除</Button> }
            ]} />
          <Space wrap>
            <Input addonBefore="会话名" placeholder="保存会话用" value={sessionName} onChange={(e) => setSessionName(e.target.value)} style={{ width: 260 }} />
            <Button loading={busy} onClick={() => void handleSaveSession()}>保存会话</Button>
            <Button onClick={() => fileInputRef.current?.click()}>导入 CSV/JSON</Button>
            <input ref={fileInputRef} type="file" accept=".json,.csv" style={{ display: 'none' }}
              onChange={(e) => { const file = e.target.files?.[0]; if (file) void importFile(file); e.target.value = ''; }} />
            <Button onClick={() => download('handeye-samples.json', samplesJson, 'application/json')}>导出 JSON</Button>
            <Button onClick={exportCsv}>导出 CSV</Button>
          </Space>
          {sessions.length > 0 && (
            <Table size="small" pagination={false} rowKey="sessionId" dataSource={sessions}
              columns={[
                { title: '已保存会话', dataIndex: 'name' },
                { title: '样本数', dataIndex: 'sampleCount', width: 80 },
                { title: '更新时间', dataIndex: 'updatedAt', width: 200, render: (v: string) => v?.replace('T', ' ').slice(0, 19) },
                {
                  title: '', width: 150, render: (_: unknown, r: HandEyeWizardSessionSummary) => (
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
            <Button type="primary" loading={busy} disabled={!precheck.ok} onClick={() => void solve()}>求解手眼（{samples.length} 对）</Button>
          </Space>
        </Space>
      )}

      {step === 2 && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          {result && (
            <Alert type="success" showIcon
              title={`平移 RMS ${Number(result.translationRms ?? 0).toFixed(4)} · 角度 RMS ${Number(result.angleRmsDeg ?? 0).toFixed(3)}° · ${result.converged ? '已收敛' : '未收敛（迭代上限）'}（${String(result.iterations ?? '-')} 次）`}
              description={`最差样本 #${String(result.worstSampleIndex ?? '-')} · maxTrans ${Number(result.maxTranslationError ?? 0).toFixed(4)} · maxAngle ${Number(result.maxAngleErrorDeg ?? 0).toFixed(3)}° · 条件数 ${result.conditionNumber != null ? Number(result.conditionNumber).toExponential(2) : '-'} · σ(x,y,θ) = ${Number(result.stdDevX ?? 0).toFixed(4)}, ${Number(result.stdDevY ?? 0).toFixed(4)}, ${Number(result.angleStdDevDeg ?? 0).toFixed(4)}°` +
                (result.outlierSampleIndex != null ? ` · 疑似坏样本 #${String(result.outlierSampleIndex)}（残差 > 3×中位数）` : '') +
                (result.crossCheckTranslationDelta != null ? ` · 代数交叉验证 Δ平移 ${Number(result.crossCheckTranslationDelta).toFixed(4)} / Δ角度 ${Number(result.crossCheckAngleDeltaDeg ?? 0).toFixed(3)}°` : '')} />
          )}
          {result?.unitWarning != null && <Alert type="warning" showIcon title="单位警告" description={String(result.unitWarning)} />}
          <Table size="small" pagination={false} rowKey="index" dataSource={residuals}
            rowClassName={(row) => (result && Number(result.worstSampleIndex) === row.index ? 'calibration-selected-row' : '')}
            columns={[
              { title: '#', dataIndex: 'index', width: 48 },
              { title: '平移残差', render: (_: unknown, r) => r.translationError.toFixed(4) },
              { title: '角度残差°', render: (_: unknown, r) => r.angleErrorDeg.toFixed(3) },
              { title: 'Robot', render: (_: unknown, r) => `${r.robot.x}, ${r.robot.y}, ${r.robot.thetaDeg}` },
              { title: 'Vision', render: (_: unknown, r) => `${r.vision.x}, ${r.vision.y}, ${r.vision.thetaDeg}` },
              {
                title: '', width: 110, render: (_: unknown, r) => (
                  <Space size={4}>
                    {Number(result?.worstSampleIndex) === r.index && <Tag color="red">最差</Tag>}
                    {result?.outlierSampleIndex != null && Number(result.outlierSampleIndex) === r.index && <Tag color="orange">离群</Tag>}
                  </Space>
                )
              }
            ]} />
          {closureScatter}
          <Space wrap>
            <Button danger onClick={removeWorst} disabled={!residuals.length}>剔除最差样本</Button>
            <Button loading={busy} onClick={() => void solve()}>重新求解</Button>
            <Button type="primary" disabled={!canApplyToSelectedNode} onClick={apply}>
              导出到节点{selectedNodeType ? `（${selectedNodeType}）` : ''}
            </Button>
            <Button onClick={() => setStep(1)}>返回录入</Button>
          </Space>
          <Space wrap>
            <Input addonBefore="资产ID" placeholder="如 handeye-cell1" value={assetId} onChange={(e) => setAssetId(e.target.value)} style={{ width: 240 }} />
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
                  title: '版本', width: 110, render: (_: unknown, r: HandEyeCalibrationAssetDescriptor) => (
                    <Select size="small" style={{ width: 90 }} value={assetVersions[r.id] ?? r.latestVersion}
                      onChange={(v) => setAssetVersions((prev) => ({ ...prev, [r.id]: v }))}
                      options={r.versions.map((v) => ({ value: v.version, label: `V${v.version}${v.published ? '（已发布）' : ''}` }))} />
                  )
                },
                { title: '已发布', width: 80, render: (_: unknown, r: HandEyeCalibrationAssetDescriptor) => r.publishedVersion != null ? `V${r.publishedVersion}` : '-' },
                {
                  title: '', width: 220, render: (_: unknown, r: HandEyeCalibrationAssetDescriptor) => {
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
        </Space>
      )}

      {step === 3 && (
        <Space direction="vertical" style={{ width: '100%' }}>
          <Alert type="info" showIcon title="参数已导出"
            description={pinnedAssetId ? `附带资产锁定 ${pinnedAssetId} V${pinnedVersion}：发布/产线校验会复核求解结果与该版本一致。可在手眼节点中再次运行验证。` : '可在手眼节点中再次运行验证。下方为当前 samplesJson。'} />
          <Input.TextArea rows={10} value={samplesJson} readOnly />
          <Button onClick={() => setStep(1)}>继续编辑样本</Button>
        </Space>
      )}
    </div>
  );
}