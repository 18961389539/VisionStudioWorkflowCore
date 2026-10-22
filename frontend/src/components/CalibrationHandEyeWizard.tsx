import { useMemo, useState } from 'react';
import { Alert, Button, Input, InputNumber, Select, Space, Steps, Table, Tag, Typography, message } from 'antd';
import type { CalibrationHandEyeResidual } from '../types';
import { apiFetch } from '../apiClient';

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

const steps = ['配置', '录入位姿对', '求解与残差', '导出'];

export default function CalibrationHandEyeWizard({ canApplyToSelectedNode, selectedNodeType, onApplyToSelectedNode }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [mode, setMode] = useState('eyeInHand');
  const [sourceFrame, setSourceFrame] = useState('Camera');
  const [targetFrame, setTargetFrame] = useState('');
  const [unit, setUnit] = useState('mm');
  const [maxTranslationError, setMaxTranslationError] = useState(0);
  const [maxAngleErrorDeg, setMaxAngleErrorDeg] = useState(0);
  const [samples, setSamples] = useState<SampleRow[]>([
    { key: '0', robotX: 0, robotY: 0, robotThetaDeg: -20, visionX: 0, visionY: 0, visionThetaDeg: 10 },
    { key: '1', robotX: 40, robotY: 10, robotThetaDeg: -5, visionX: 12, visionY: -3, visionThetaDeg: 25 },
    { key: '2', robotX: 80, robotY: -20, robotThetaDeg: 15, visionX: 28, visionY: 8, visionThetaDeg: 45 },
    { key: '3', robotX: -10, robotY: 50, robotThetaDeg: 30, visionX: -5, visionY: 20, visionThetaDeg: 60 }
  ]);
  const [draft, setDraft] = useState({ robotX: 0, robotY: 0, robotThetaDeg: 0, visionX: 0, visionY: 0, visionThetaDeg: 0 });
  const [result, setResult] = useState<Record<string, unknown>>();
  const [residuals, setResiduals] = useState<CalibrationHandEyeResidual[]>([]);

  const samplesJson = useMemo(() => JSON.stringify({
    samples: samples.map((s) => ({
      robot: { x: s.robotX, y: s.robotY, thetaDeg: s.robotThetaDeg },
      vision: { x: s.visionX, y: s.visionY, thetaDeg: s.visionThetaDeg }
    }))
  }, null, 2), [samples]);

  const addSample = () => {
    setSamples((prev) => [...prev, { key: String(Date.now()), ...draft }]);
    messageApi.success('已添加位姿对');
  };

  const removeSample = (key: string) => setSamples((prev) => prev.filter((s) => s.key !== key));

  const removeWorst = () => {
    if (!residuals.length) return;
    const worst = [...residuals].sort((a, b) => b.translationError - a.translationError)[0];
    if (!worst) return;
    setSamples((prev) => prev.filter((_, i) => i !== worst.index));
    setResiduals([]);
    setResult(undefined);
    setStep(1);
    messageApi.info(`已移除残差最大样本 #${worst.index}，请重新求解`);
  };

  const solve = async () => {
    setBusy(true);
    try {
      const response = await apiFetch('/api/calibration/wizard/handeye/solve', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          samplesJson,
          mode,
          maxTranslationError,
          maxAngleErrorDeg,
          sourceFrame,
          targetFrame,
          unit,
          sourceUnit: unit
        })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '手眼求解失败');
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

  const apply = () => {
    onApplyToSelectedNode({
      samplesJson,
      mode,
      maxTranslationError,
      maxAngleErrorDeg,
      sourceFrame,
      ...(targetFrame ? { targetFrame } : {}),
      unit,
      sourceUnit: unit
    });
    messageApi.success(selectedNodeType === 'calibration.handEye2D'
      ? '已写入 handEye2D 节点参数'
      : '已导出参数（请选中 calibration.handEye2D 节点以精确写入）');
    setStep(3);
  };

  return (
    <div>
      {contextHolder}
      <Steps size="small" current={step} items={steps.map((title) => ({ title }))} style={{ marginBottom: 16 }}
        onChange={setStep} />
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="二维手眼向导（平面 SE(2)）"
        description="录入机器人位姿 + 视觉位姿对，求解后查看逐样本残差表，可剔除最差样本后重算，并导出到 calibration.handEye2D。仅平面手眼，非 3D。" />

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
            <InputNumber addonBefore="maxTrans" min={0} value={maxTranslationError} onChange={(v) => setMaxTranslationError(Number(v ?? 0))} />
            <InputNumber addonBefore="maxAngle°" min={0} value={maxAngleErrorDeg} onChange={(v) => setMaxAngleErrorDeg(Number(v ?? 0))} />
          </Space>
          <Button type="primary" onClick={() => setStep(1)}>下一步：录入样本</Button>
        </Space>
      )}

      {step === 1 && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          <Typography.Text type="secondary">至少 4 对互异机器人位姿；需同时覆盖平移与足够角度变化（≥8°）。</Typography.Text>
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
          <Space>
            <Button onClick={() => setStep(0)}>上一步</Button>
            <Button type="primary" loading={busy} disabled={samples.length < 4} onClick={() => void solve()}>求解手眼（{samples.length} 对）</Button>
          </Space>
        </Space>
      )}

      {step === 2 && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          {result && (
            <Alert type="success" showIcon
              message={`平移 RMS ${Number(result.translationRms ?? 0).toFixed(4)} · 角度 RMS ${Number(result.angleRmsDeg ?? 0).toFixed(3)}°`}
              description={`最差样本 #${String(result.worstSampleIndex ?? '-')} · maxTrans ${Number(result.maxTranslationError ?? 0).toFixed(4)} · maxAngle ${Number(result.maxAngleErrorDeg ?? 0).toFixed(3)}°`} />
          )}
          <Table size="small" pagination={false} rowKey="index" dataSource={residuals}
            rowClassName={(row) => (result && Number(result.worstSampleIndex) === row.index ? 'calibration-selected-row' : '')}
            columns={[
              { title: '#', dataIndex: 'index', width: 48 },
              { title: '平移残差', render: (_: unknown, r) => r.translationError.toFixed(4) },
              { title: '角度残差°', render: (_: unknown, r) => r.angleErrorDeg.toFixed(3) },
              { title: 'Robot', render: (_: unknown, r) => `${r.robot.x}, ${r.robot.y}, ${r.robot.thetaDeg}` },
              { title: 'Vision', render: (_: unknown, r) => `${r.vision.x}, ${r.vision.y}, ${r.vision.thetaDeg}` },
              { title: '', width: 90, render: (_: unknown, r) => Number(result?.worstSampleIndex) === r.index ? <Tag color="red">最差</Tag> : null }
            ]} />
          <Space>
            <Button danger onClick={removeWorst} disabled={!residuals.length}>剔除最差样本</Button>
            <Button loading={busy} onClick={() => void solve()}>重新求解</Button>
            <Button type="primary" disabled={!canApplyToSelectedNode} onClick={apply}>
              导出到节点{selectedNodeType ? `（${selectedNodeType}）` : ''}
            </Button>
            <Button onClick={() => setStep(1)}>返回录入</Button>
          </Space>
        </Space>
      )}

      {step === 3 && (
        <Space direction="vertical" style={{ width: '100%' }}>
          <Alert type="info" showIcon message="参数已导出" description="可在手眼节点中再次运行验证。下方为当前 samplesJson。" />
          <Input.TextArea rows={10} value={samplesJson} readOnly />
          <Button onClick={() => setStep(1)}>继续编辑样本</Button>
        </Space>
      )}
    </div>
  );
}
