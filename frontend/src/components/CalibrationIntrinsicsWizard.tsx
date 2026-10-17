import { useMemo, useState } from 'react';
import { Alert, Button, Input, InputNumber, Select, Space, Steps, Table, Tag, Typography, Upload, message } from 'antd';
import type { CalibrationWizardSession } from '../types';

type Props = {
  canApplyToSelectedNode: boolean;
  selectedNodeType?: string;
  onApplyToSelectedNode: (parameters: Record<string, unknown>) => void;
};

const stepTitles = ['标定板设置', '采集 / 导入视图', '审核视图', '求解内参', '导出应用'];

export default function CalibrationIntrinsicsWizard({ canApplyToSelectedNode, selectedNodeType, onApplyToSelectedNode }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
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
  const [solveSummary, setSolveSummary] = useState<Record<string, unknown>>();
  const [maxViewRms, setMaxViewRms] = useState(0);
  const [maxRms, setMaxRms] = useState(0);

  const enabledCount = session?.enabledCount ?? 0;
  const guidance = useMemo(() => {
    if (!session?.views.length) return '上传标定板图像或粘贴 detectBoard 累积的 calibrationDataJson。目标：8–20 张、覆盖视场边缘并带倾角变化。';
    const fails = session.views.filter((v) => v.quality.verdict === 'Fail').length;
    const warns = session.views.filter((v) => v.quality.verdict === 'Warn').length;
    return `已收录 ${session.views.length} 张（启用 ${enabledCount}）。失败 ${fails} · 警告 ${warns}。启用满 3 张即可求解。`;
  }, [session, enabledCount]);

  const createSession = async () => {
    setBusy(true);
    try {
      const body: Record<string, unknown> = { pattern, columns, rows, spacingMm, minBlurVariance, minCoverage };
      if (imageWidth && imageHeight) { body.imageWidth = imageWidth; body.imageHeight = imageHeight; }
      const response = await fetch('/api/calibration/wizard/sessions', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '创建向导会话失败');
      setSession(data);
      setSolveSummary(undefined);
      setStep(1);
      messageApi.success('内参标定向导会话已创建');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '创建失败'); }
    finally { setBusy(false); }
  };

  const refresh = async (next: CalibrationWizardSession) => {
    setSession(next);
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
        const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(current.sessionId)}/views/from-image`, {
          method: 'POST', body: form
        });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error ?? `图像 ${file.name} 检测失败`);
        current = data;
      }
      await refresh(current);
      setStep(2);
      messageApi.success(`已处理 ${files.length} 张图像`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '上传失败'); }
    finally { setBusy(false); }
  };

  const importCalibrationJson = async () => {
    if (!session) return messageApi.warning('请先创建会话');
    setBusy(true);
    try {
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/views`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ calibrationDataJson: importJson, label: 'import' })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '导入失败');
      await refresh(data);
      setStep(2);
      messageApi.success('已导入标定视图 JSON');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导入失败'); }
    finally { setBusy(false); }
  };

  const setEnabled = async (index: number, enabled: boolean) => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/views/${index}/enabled`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enabled })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '更新失败');
      await refresh(data);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '更新失败'); }
    finally { setBusy(false); }
  };

  const removeView = async (index: number) => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/views/${index}`, { method: 'DELETE' });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '删除失败');
      await refresh(data);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '删除失败'); }
    finally { setBusy(false); }
  };

  const calibrate = async () => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/calibrate`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ maxViewRms, maxRms })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '标定求解失败');
      setSession(data.session);
      setSolveSummary(data.result);
      setStep(4);
      messageApi.success(`求解完成 · RMS ${Number(data.result?.rms ?? 0).toFixed(4)} px`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '求解失败'); }
    finally { setBusy(false); }
  };

  const applyModel = async () => {
    if (!session) return;
    setBusy(true);
    try {
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/export`);
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '导出失败');
      if (selectedNodeType === 'calibration.cameraIntrinsics') {
        onApplyToSelectedNode({ calibrationDataJson: data.calibrationDataJson ?? '' });
        messageApi.success('已将视图 JSON 应用到相机内参节点');
      } else if (selectedNodeType === 'image.undistort') {
        onApplyToSelectedNode({ calibrationJson: data.calibrationJson ?? '' });
        messageApi.success('已将内参模型应用到去畸变节点');
      } else {
        onApplyToSelectedNode({
          calibrationDataJson: data.calibrationDataJson ?? '',
          calibrationJson: data.calibrationJson ?? ''
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
      <Steps size="small" current={step} items={stepTitles.map((title) => ({ title }))} style={{ marginBottom: 16 }}
        onChange={(value) => setStep(value)} />
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="相机内参向导（实用切片）"
        description="质量门控（清晰度 / 覆盖率 / 角点完整性）→ 接受/拒绝视图 → 求解 RMS / 最差视图 / 离群视图 → 导出到节点。不是完整 HALCON Calibration Assistant（无实时相机预览、无鱼眼、无自动姿态规划）。" />
      <Typography.Paragraph type="secondary">{guidance}</Typography.Paragraph>

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
        </Space>
      )}

      {step === 1 && session && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          <Typography.Text code>session {session.sessionId}</Typography.Text>
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
                  {row.enabled ? '是' : '否'}
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
            <InputNumber addonBefore="单视图 RMS 上限" min={0} step={0.1} value={maxViewRms} onChange={(v) => setMaxViewRms(Number(v ?? 0))} />
            <InputNumber addonBefore="模型 RMS 上限" min={0} step={0.1} value={maxRms} onChange={(v) => setMaxRms(Number(v ?? 0))} />
            <Button type="primary" loading={busy} disabled={enabledCount < 3} onClick={calibrate}>运行内参标定</Button>
          </Space>
          <Typography.Text type="secondary">0 = 关闭门控。求解使用与节点 calibration.cameraIntrinsics 相同的 JLVision 后端。</Typography.Text>
        </Space>
      )}

      {step === 4 && (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          {solveSummary && (
            <Alert type="success" showIcon message={`RMS ${Number(solveSummary.rms ?? 0).toFixed(4)} px · 启用视图 ${String(solveSummary.enabledViews ?? enabledCount)}`}
              description={`最差视图索引: ${String(solveSummary.worstViewIndex ?? '—')} · 离群: ${String((solveSummary.nodeSummary as Record<string, unknown> | undefined)?.outlierViewIndex ?? '无')}`} />
          )}
          <Space>
            <Button type="primary" loading={busy} disabled={!canApplyToSelectedNode} onClick={applyModel}>
              应用到所选节点{selectedNodeType ? `（${selectedNodeType}）` : ''}
            </Button>
            <Button onClick={() => setStep(2)}>返回审核</Button>
          </Space>
        </Space>
      )}
    </div>
  );
}
