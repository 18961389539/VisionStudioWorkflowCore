import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Input, InputNumber, Select, Space, Steps, Table, Tag, Typography, Upload, message } from 'antd';
import type { CalibrationWizardHint, CalibrationWizardSession } from '../types';

type Props = {
  canApplyToSelectedNode: boolean;
  selectedNodeType?: string;
  onApplyToSelectedNode: (parameters: Record<string, unknown>) => void;
};

type CameraListItem = { id: string; name?: string; acquisition?: { acquisitionState?: string } };

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
  const [documentJson, setDocumentJson] = useState('');
  const [solveSummary, setSolveSummary] = useState<Record<string, unknown>>();
  const [maxViewRms, setMaxViewRms] = useState(0);
  const [maxRms, setMaxRms] = useState(0);
  const [hints, setHints] = useState<CalibrationWizardHint[]>([]);
  const [cameras, setCameras] = useState<CameraListItem[]>([]);
  const [cameraId, setCameraId] = useState<string>();
  const [previewUrl, setPreviewUrl] = useState<string>();
  const [previewQuality, setPreviewQuality] = useState<string>();
  const [viewStatsRows, setViewStatsRows] = useState<Record<string, unknown>[]>([]);

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
        const response = await fetch('/api/cameras');
        if (response.ok) {
          const list = await response.json() as CameraListItem[];
          setCameras(list);
          if (!cameraId && list[0]) setCameraId(list[0].id);
        }
      } catch { setCameras([]); }
    })();
  }, []);

  useEffect(() => () => {
    if (previewUrl) URL.revokeObjectURL(previewUrl);
  }, [previewUrl]);

  const refresh = async (next: CalibrationWizardSession) => {
    setSession(next);
  };

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
        const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(current.sessionId)}/views/from-image`, {
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
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/views`, {
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
      const response = await fetch('/api/calibration/wizard/sessions/import', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ documentJson })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '会话文档导入失败');
      setSession(data);
      setSolveSummary((data.lastSolveSummary as Record<string, unknown> | null) ?? undefined);
      setStep(data.views?.length ? 2 : 1);
      await loadHints(data.sessionId);
      messageApi.success('已从会话文档恢复（含视图与结果）');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导入失败'); }
    finally { setBusy(false); }
  };

  const loadHints = async (sessionId: string) => {
    try {
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(sessionId)}/hints`);
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
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/preview`, {
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
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/views/${index}/enabled`, {
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
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/views/${index}`, { method: 'DELETE' });
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
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/disable-outliers`, {
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
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/calibrate`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ maxViewRms, maxRms })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '标定求解失败');
      setSession(data.session);
      setSolveSummary(data.result);
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
      const response = await fetch(`/api/calibration/wizard/sessions/${encodeURIComponent(session.sessionId)}/export`);
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '导出失败');
      const text = typeof data.documentJson === 'string' ? data.documentJson : JSON.stringify(data.document, null, 2);
      setDocumentJson(text);
      if (Array.isArray(data.hints)) setHints(data.hints);
      await navigator.clipboard?.writeText(text).catch(() => undefined);
      messageApi.success('已导出完整会话文档（含视图点与结果），可复制保存');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '导出失败'); }
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
        message="相机内参向导（Batch F）"
        description="会话落盘 + 导出/导入完整文档 · 相机刷新检测预览 · 规则姿态提示 · 离群一键禁用 · 导出到节点。鱼眼/远心：当前 JLVision 仅支持多项式针孔，已跳过。" />
      <Typography.Paragraph type="secondary">{guidance}</Typography.Paragraph>
      {hints.length > 0 && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }}
          message="建议下一姿态（规则提示，非自动规划）"
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
              description={`最差视图索引 ${String(solveSummary.worstViewIndex ?? '无')} · 离群: ${String((solveSummary.nodeSummary as Record<string, unknown> | undefined)?.outlierViewIndex ?? '无')}`} />
          )}
          {viewStatsRows.length > 0 && (
            <Table size="small" pagination={false} rowKey={(r) => String(r.index)} dataSource={viewStatsRows}
              columns={[
                { title: 'view', dataIndex: 'index', width: 60 },
                { title: 'covX', render: (_: unknown, r) => Number(r.coverageX ?? 0).toFixed(2) },
                { title: 'covY', render: (_: unknown, r) => Number(r.coverageY ?? 0).toFixed(2) },
                { title: 'H-RMS', render: (_: unknown, r) => r.homographyRms == null ? '-' : Number(r.homographyRms).toFixed(3) },
                { title: 'Pose-RMS', render: (_: unknown, r) => r.poseReprojectionRms == null ? '-' : Number(r.poseReprojectionRms).toFixed(3) }
              ]} />
          )}
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
