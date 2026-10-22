import { useMemo, useState } from 'react';
import { Alert, Button, Input, InputNumber, Select, Space, Steps, Table, Tag, Typography, message } from 'antd';
import { apiFetch } from '../apiClient';
import { apiUrl } from '../endpoints';

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
  const [samples, setSamples] = useState<SampleRow[]>([]);
  const [result, setResult] = useState<Record<string, unknown>>();
  const [residuals, setResiduals] = useState<Residual[]>([]);

  const sampleErrors = useMemo(() => samples.map((row, index) => ({
    key: row.key,
    robot: matrixError(row, 'robotToBase', `样本 ${index + 1} 机器人位姿`),
    target: matrixError(row, 'targetToCamera', `样本 ${index + 1} 标定板位姿`)
  })), [samples]);
  const firstError = sampleErrors.flatMap((x) => [x.robot, x.target]).find(Boolean);
  const canSolve = samples.length >= 6 && !firstError && samples.length <= 1000;
  const canApply3D = canApplyToSelectedNode && selectedNodeType === 'calibration.handEye3D';

  const updateSample = (key: string, field: 'robotToBase' | 'targetToCamera', value: string) => {
    setSamples((current) => current.map((row) => row.key === key ? { ...row, [field]: value } : row));
    setResult(undefined);
    setResiduals([]);
  };

  const buildSamplesJson = () => JSON.stringify({
    samples: samples.map((row, index) => ({
      robotToBase: parseMatrix(row.robotToBase, `样本 ${index + 1} 机器人位姿`),
      targetToCamera: parseMatrix(row.targetToCamera, `样本 ${index + 1} 标定板位姿`)
    }))
  }, null, 2);

  const addSample = () => {
    setSamples((current) => [...current, { key: `${Date.now()}-${current.length}`, robotToBase: identityText, targetToCamera: identityText }]);
    setResult(undefined);
    setResiduals([]);
  };

  const removeSample = (key: string) => {
    setSamples((current) => current.filter((row) => row.key !== key));
    setResult(undefined);
    setResiduals([]);
  };

  const solve = async () => {
    let samplesJson: string;
    try { samplesJson = buildSamplesJson(); }
    catch (error) { messageApi.error(error instanceof Error ? error.message : '矩阵格式无效'); return; }
    setBusy(true);
    try {
      const response = await apiFetch(apiUrl('calibration/wizard/handeye3d/solve'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ samplesJson, mode, method, maxTranslationError, maxRotationErrorDeg, rotationWeight, sourceFrame, targetFrame, unit })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '三维手眼求解失败');
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

  const apply = () => {
    onApplyToSelectedNode({
      samplesJson: buildSamplesJson(), mode, method, maxTranslationError, maxRotationErrorDeg, rotationWeight,
      sourceFrame, ...(targetFrame ? { targetFrame } : {}), unit
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

  return (
    <div>
      {contextHolder}
      <Steps size="small" current={step} items={['配置', '录入矩阵对', '求解与残差', '应用'].map((title) => ({ title }))} style={{ marginBottom: 16 }} />
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message="三维手眼向导（SE(3)）"
        description="逐样本录入机器人基座到工具、标定板到相机的刚性 4×4 变换。至少六组样本，并覆盖两个非平行旋转轴；求解后可查看逐样本三维残差并回写 handEye3D 节点。" />

      {step === 0 && <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        <Space wrap>
          <Select style={{ width: 210 }} value={mode} onChange={(value) => { setMode(value); setResult(undefined); setResiduals([]); }} options={[
            { value: 'eyeInHand', label: '眼在手上：相机安装于工具' },
            { value: 'eyeToHand', label: '眼在手外：相机固定、标定板随工具' }
          ]} />
          <Select style={{ width: 210 }} value={method} onChange={(value) => { setMethod(value); setResult(undefined); setResiduals([]); }} options={[
            { value: 'DANIILIDIS', label: 'Daniilidis（双四元数）' }, { value: 'TSAI', label: 'Tsai' },
            { value: 'PARK', label: 'Park' }, { value: 'HORAUD', label: 'Horaud' }, { value: 'ANDREFF', label: 'Andreff' }
          ]} />
          <Input addonBefore="sourceFrame" value={sourceFrame} onChange={(event) => { setSourceFrame(event.target.value); setResult(undefined); setResiduals([]); }} style={{ width: 220 }} />
          <Input addonBefore="targetFrame" placeholder="默认 RobotTool/Base" value={targetFrame} onChange={(event) => { setTargetFrame(event.target.value); setResult(undefined); setResiduals([]); }} style={{ width: 250 }} />
          <Select style={{ width: 130 }} value={unit} onChange={(value) => { setUnit(value); setResult(undefined); setResiduals([]); }} options={[
            { value: 'mm', label: '毫米 mm' }, { value: 'cm', label: '厘米 cm' }, { value: 'm', label: '米 m' }
          ]} />
          <InputNumber addonBefore="旋转权重" min={0.001} max={1000000} value={rotationWeight} onChange={(value) => { setRotationWeight(Number(value ?? 50)); setResult(undefined); setResiduals([]); }} />
        </Space>
        <Space wrap>
          <InputNumber addonBefore={`平移门限 ${unit}`} min={0} value={maxTranslationError} onChange={(value) => { setMaxTranslationError(Number(value ?? 0)); setResult(undefined); setResiduals([]); }} />
          <InputNumber addonBefore="旋转门限°" min={0} max={180} value={maxRotationErrorDeg} onChange={(value) => { setMaxRotationErrorDeg(Number(value ?? 0)); setResult(undefined); setResiduals([]); }} />
          <Typography.Text type="secondary">门限填 0 表示关闭。输入矩阵的平移量和所选单位必须一致。</Typography.Text>
        </Space>
        <Button type="primary" onClick={() => setStep(1)}>下一步：录入样本</Button>
      </Space>}

      {step === 1 && <Space direction="vertical" style={{ width: '100%' }} size="middle">
        <Alert type={samples.length < 6 || firstError ? 'warning' : 'success'} showIcon
          message={samples.length < 6 ? `当前 ${samples.length} 组，至少需要 6 组` : firstError ?? `样本数 ${samples.length}，矩阵格式有效`}
          description="每个矩阵表示坐标从源框架映射到目标框架。可粘贴 4×4 嵌套数组或 16 个行优先数值；机器人矩阵方向为 RobotTool→RobotBase，视觉矩阵方向为 Target→Camera。" />
        <Button onClick={addSample} disabled={samples.length >= 1000}>添加位姿对</Button>
        <Table size="small" pagination={false} rowKey="key" dataSource={samples} scroll={{ x: 1050 }}
          columns={[
            { title: '#', width: 48, render: (_: unknown, __: SampleRow, index: number) => index },
            { title: 'robotToBase · 工具→基座', width: 450, render: matrixEditor('robotToBase', '机器人矩阵') },
            { title: 'targetToCamera · 标定板→相机', width: 450, render: matrixEditor('targetToCamera', '视觉矩阵') },
            { title: '', width: 72, fixed: 'right', render: (_: unknown, row: SampleRow) => <Button size="small" danger onClick={() => removeSample(row.key)}>删除</Button> }
          ]} />
        <Space>
          <Button onClick={() => setStep(0)}>上一步</Button>
          <Button type="primary" loading={busy} disabled={!canSolve} onClick={() => void solve()}>求解三维手眼（{samples.length} 组）</Button>
        </Space>
      </Space>}

      {step === 2 && <Space direction="vertical" style={{ width: '100%' }} size="middle">
        {result && <Alert type="success" showIcon
          message={`平移 RMS ${Number(result.translationRms ?? 0).toFixed(4)} ${unit} · 旋转 RMS ${Number(result.rotationRmsDeg ?? 0).toFixed(3)}°`}
          description={`最差样本 #${String(result.worstSampleIndex ?? '-')} · 最大平移 ${Number(result.maxTranslationError ?? 0).toFixed(4)} ${unit} · 最大旋转 ${Number(result.maxRotationErrorDeg ?? 0).toFixed(3)}° · 机器人位移跨度 ${Number(result.robotTranslationSpan ?? 0).toFixed(3)} ${unit}`} />}
        {transformJson && <Input.TextArea rows={6} value={transformJson} readOnly spellCheck={false} />}
        <Table size="small" pagination={false} rowKey="index" dataSource={residuals}
          rowClassName={(row) => Number(result?.worstSampleIndex) === row.index ? 'calibration-selected-row' : ''}
          columns={[
            { title: '#', dataIndex: 'index', width: 48 },
            { title: `平移残差 ${unit}`, render: (_: unknown, row: Residual) => row.translationError.toFixed(4) },
            { title: '旋转残差°', render: (_: unknown, row: Residual) => row.rotationErrorDeg.toFixed(3) },
            { title: 'Robot 平移', render: (_: unknown, row: Residual) => `${row.robotTranslation.x}, ${row.robotTranslation.y}, ${row.robotTranslation.z}` },
            { title: 'Target 平移', render: (_: unknown, row: Residual) => `${row.targetTranslation.x}, ${row.targetTranslation.y}, ${row.targetTranslation.z}` },
            { title: '', width: 72, render: (_: unknown, row: Residual) => Number(result?.worstSampleIndex) === row.index ? <Tag color="red">最差</Tag> : null }
          ]} />
        <Space wrap>
          <Button danger onClick={removeWorst} disabled={!residuals.length || samples.length <= 6}>剔除综合残差最大样本</Button>
          <Button loading={busy} onClick={() => void solve()}>重新求解</Button>
          <Button type="primary" disabled={!canApply3D || !result} onClick={apply}>应用到 calibration.handEye3D 节点</Button>
          <Button onClick={() => setStep(1)}>返回录入</Button>
        </Space>
        {!canApply3D && <Typography.Text type="secondary">选择一个 calibration.handEye3D 节点后即可回写参数。</Typography.Text>}
      </Space>}

      {step === 3 && <Space direction="vertical" style={{ width: '100%' }}>
        <Alert type="success" showIcon message="三维手眼参数已写入节点" description="节点保存了本次样本和质量门限，可在工作流运行时重新计算并校验。" />
        <Input.TextArea rows={12} value={buildSamplesJson()} readOnly spellCheck={false} />
        <Button onClick={() => setStep(1)}>继续编辑样本</Button>
      </Space>}
    </div>
  );
}
