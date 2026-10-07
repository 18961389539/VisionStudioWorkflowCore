import { useCallback, useEffect, useMemo, useState } from 'react';
import { Button, Checkbox, Input, InputNumber, message, Modal, Select, Space, Statistic, Table, Tag } from 'antd';
import type {
  CalibrationAssetDescriptor,
  CalibrationResidual,
  CalibrationVersionSnapshot,
  CalibrationWorkspacePoint,
  CalibrationWorkspaceRequest,
  CalibrationWorkspaceResult
} from '../types';

type Props = {
  open: boolean;
  onClose: () => void;
  canApplyToSelectedNode: boolean;
  onApplyToSelectedNode: (parameters: Record<string, unknown>) => void;
};

function ninePointGrid(source = 'Manual'): CalibrationWorkspacePoint[] {
  const xs = [0, 22, 44];
  const ys = [0, 14, 28];
  let index = 1;
  return ys.flatMap((worldY) => xs.map((worldX) => ({
    index: index++,
    imageX: 100 + worldX * 10,
    imageY: 100 + worldY * 10,
    worldX,
    worldY,
    enabled: true,
    source
  })));
}

function verificationGrid(): CalibrationWorkspacePoint[] {
  return [
    { index: 101, worldX: 11, worldY: 7 },
    { index: 102, worldX: 33, worldY: 7 },
    { index: 103, worldX: 11, worldY: 21 },
    { index: 104, worldX: 33, worldY: 21 },
    { index: 105, worldX: 22, worldY: 14 }
  ].map((x) => ({ ...x, imageX: 100 + x.worldX * 10, imageY: 100 + x.worldY * 10, enabled: true, source: 'Verification' }));
}

export default function CalibrationPanel({ open, onClose, canApplyToSelectedNode, onApplyToSelectedNode }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [sourceFrame, setSourceFrame] = useState('ImagePixel');
  const [targetFrame, setTargetFrame] = useState('Workpiece');
  const [targetUnit, setTargetUnit] = useState('mm');
  const [points, setPoints] = useState<CalibrationWorkspacePoint[]>(ninePointGrid());
  const [verificationPoints, setVerificationPoints] = useState<CalibrationWorkspacePoint[]>(verificationGrid());
  const [result, setResult] = useState<CalibrationWorkspaceResult>();
  const [assets, setAssets] = useState<CalibrationAssetDescriptor[]>([]);
  const [selectedAssetId, setSelectedAssetId] = useState<string>();
  const [loadedAssetVersion, setLoadedAssetVersion] = useState<number>(0);
  const [workspaceDirty, setWorkspaceDirty] = useState(true);
  const [assetId, setAssetId] = useState('calib-workpiece-1');
  const [assetName, setAssetName] = useState('Workpiece Calibration');
  const [busy, setBusy] = useState(false);

  const workspace = useMemo<CalibrationWorkspaceRequest>(() => ({
    sourceFrame, targetFrame, targetUnit, points, verificationPoints, heatmapColumns: 5, heatmapRows: 5
  }), [sourceFrame, targetFrame, targetUnit, points, verificationPoints]);

  const refreshAssets = useCallback(async () => {
    try {
      const response = await fetch('/api/calibrations');
      if (response.ok) setAssets(await response.json());
    } catch { setAssets([]); }
  }, []);

  useEffect(() => { if (open) refreshAssets(); }, [open, refreshAssets]);

  const solve = async () => {
    setBusy(true);
    try {
      const response = await fetch('/api/calibration/solve', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(workspace)
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '标定计算失败');
      setResult(data);
      messageApi.success(`已使用 ${data.usedPointCount} 个点完成计算 · RMSE ${data.rmse.toFixed(4)} ${targetUnit}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '标定计算失败'); }
    finally { setBusy(false); }
  };

  const autoCapture = async () => {
    setBusy(true);
    try {
      const response = await fetch('/api/calibration/capture-grid', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ providerId: 'virtual', columns: 3, rows: 3, originX: 0, originY: 0, stepX: 22, stepY: 14 })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '自动采集失败');
      setPoints(data);
      setWorkspaceDirty(true);
      setResult(undefined);
      messageApi.success('虚拟采集器已采集 3×3 标定点');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '自动采集失败'); }
    finally { setBusy(false); }
  };

  const saveVersion = async () => {
    setBusy(true);
    try {
      const existing = assets.find((x) => x.id === assetId);
      const url = existing ? `/api/calibrations/${encodeURIComponent(assetId)}/versions` : '/api/calibrations';
      const body = existing
        ? { workspace, note: `Workspace save ${new Date().toISOString()}` }
        : { id: assetId, name: assetName, description: 'VisionStudio calibration workspace asset', workspace, note: 'Initial workspace version' };
      const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '保存标定版本失败');
      await refreshAssets();
      setSelectedAssetId(assetId);
      setLoadedAssetVersion(existing ? Number(data.version ?? existing.latestVersion + 1) : Number(data.latestVersion ?? 1));
      setWorkspaceDirty(false);
      messageApi.success(existing ? '已创建新的不可变标定版本' : '标定资产已创建');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '保存失败'); }
    finally { setBusy(false); }
  };

  const publish = async (id: string, version: number, rollback = false) => {
    const url = rollback
      ? `/api/calibrations/${encodeURIComponent(id)}/rollback/${version}`
      : `/api/calibrations/${encodeURIComponent(id)}/versions/${version}/publish`;
    const response = await fetch(url, { method: 'POST' });
    const data = await response.json();
    if (!response.ok) return messageApi.error(data.error ?? '发布失败');
    await refreshAssets();
    messageApi.success(`${rollback ? '已回滚' : '已发布'}标定资产 ${id} V${version}`);
  };

  const loadVersion = async (id: string, version: number) => {
    const response = await fetch(`/api/calibrations/${encodeURIComponent(id)}/versions/${version}`);
    const data: CalibrationVersionSnapshot & { error?: string } = await response.json();
    if (!response.ok) return messageApi.error(data.error ?? '加载标定版本失败');
    setSourceFrame(data.workspace.sourceFrame);
    setTargetFrame(data.workspace.targetFrame);
    setTargetUnit(data.workspace.targetUnit);
    setPoints(data.workspace.points);
    setVerificationPoints(data.workspace.verificationPoints ?? []);
    setResult(data.result);
    setAssetId(id);
    setSelectedAssetId(id);
    setLoadedAssetVersion(version);
    setWorkspaceDirty(false);
    const descriptor = assets.find((x) => x.id === id);
    if (descriptor) setAssetName(descriptor.name);
    messageApi.success(`已加载 ${id} V${version}`);
  };

  const updatePoint = (verification: boolean, index: number, patch: Partial<CalibrationWorkspacePoint>) => {
    const setter = verification ? setVerificationPoints : setPoints;
    setter((current) => current.map((p) => p.index === index ? { ...p, ...patch } : p));
    setWorkspaceDirty(true);
    setResult(undefined);
  };

  const addPoint = (verification: boolean) => {
    const current = verification ? verificationPoints : points;
    const nextIndex = Math.max(verification ? 100 : 0, ...current.map((x) => x.index)) + 1;
    const next: CalibrationWorkspacePoint = { index: nextIndex, imageX: 0, imageY: 0, worldX: 0, worldY: 0, enabled: true, source: verification ? 'Verification' : 'Manual' };
    (verification ? setVerificationPoints : setPoints)((items) => [...items, next]);
    setWorkspaceDirty(true);
    setResult(undefined);
  };

  const removePoint = (verification: boolean, index: number) => {
    (verification ? setVerificationPoints : setPoints)((items) => items.filter((x) => x.index !== index));
    setWorkspaceDirty(true);
    setResult(undefined);
  };

  const pointColumns = (verification: boolean) => [
    { title: '#', dataIndex: 'index', width: 48 },
    { title: 'Use', width: 50, render: (_: unknown, row: CalibrationWorkspacePoint) => <Checkbox checked={row.enabled} onChange={(e) => updatePoint(verification, row.index, { enabled: e.target.checked })} /> },
    { title: 'Image X', width: 94, render: (_: unknown, row: CalibrationWorkspacePoint) => <InputNumber size="small" value={row.imageX} step={0.01} onChange={(v) => updatePoint(verification, row.index, { imageX: Number(v ?? 0) })} /> },
    { title: 'Image Y', width: 94, render: (_: unknown, row: CalibrationWorkspacePoint) => <InputNumber size="small" value={row.imageY} step={0.01} onChange={(v) => updatePoint(verification, row.index, { imageY: Number(v ?? 0) })} /> },
    { title: `World X (${targetUnit})`, width: 108, render: (_: unknown, row: CalibrationWorkspacePoint) => <InputNumber size="small" value={row.worldX} step={0.001} onChange={(v) => updatePoint(verification, row.index, { worldX: Number(v ?? 0) })} /> },
    { title: `World Y (${targetUnit})`, width: 108, render: (_: unknown, row: CalibrationWorkspacePoint) => <InputNumber size="small" value={row.worldY} step={0.001} onChange={(v) => updatePoint(verification, row.index, { worldY: Number(v ?? 0) })} /> },
    { title: 'Source', dataIndex: 'source', width: 90 },
    { title: '', width: 52, render: (_: unknown, row: CalibrationWorkspacePoint) => <Button size="small" danger onClick={() => removePoint(verification, row.index)}>×</Button> }
  ];

  const residualColumns = [
    { title: '#', dataIndex: 'index', width: 42 },
    { title: 'dX', dataIndex: 'dx', width: 82, render: (v: number) => v.toFixed(4) },
    { title: 'dY', dataIndex: 'dy', width: 82, render: (v: number) => v.toFixed(4) },
    { title: `Error (${targetUnit})`, dataIndex: 'error', width: 105, render: (v: number) => <Tag color={v <= 0.05 ? 'green' : v <= 0.1 ? 'gold' : 'red'}>{v.toFixed(4)}</Tag> },
    { title: 'Source', dataIndex: 'source', width: 100 }
  ];

  const selectedAsset = assets.find((x) => x.id === selectedAssetId);
  const heatMax = Math.max(0.000001, ...(result?.heatmap.map((x) => x.error) ?? [0.000001]));

  const applyToNode = () => {
    onApplyToSelectedNode({
      sourceFrame,
      targetFrame,
      targetUnit,
      assetId: workspaceDirty ? '' : (selectedAssetId ?? ''),
      assetVersion: workspaceDirty ? 0 : loadedAssetVersion,
      pairsJson: JSON.stringify(points.filter((x) => x.enabled).map((x) => ({ imageX: x.imageX, imageY: x.imageY, worldX: x.worldX, worldY: x.worldY })))
    });
    messageApi.success('标定工作区已应用到所选平面标定节点');
  };

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={1480} title="标定工作区 · V0.20" destroyOnHidden>
      {contextHolder}
      <div className="calibration-toolbar">
        <Space wrap>
          <Input value={sourceFrame} onChange={(e) => { setSourceFrame(e.target.value); setWorkspaceDirty(true); setResult(undefined); }} addonBefore="源坐标系" style={{ width: 205 }} />
          <Input value={targetFrame} onChange={(e) => { setTargetFrame(e.target.value); setWorkspaceDirty(true); setResult(undefined); }} addonBefore="目标坐标系" style={{ width: 205 }} />
          <Select value={targetUnit} onChange={(v) => { setTargetUnit(v); setWorkspaceDirty(true); setResult(undefined); }} style={{ width: 88 }} options={[{ value: 'mm' }, { value: 'cm' }, { value: 'm' }]} />
          <Button onClick={() => { setPoints(ninePointGrid()); setVerificationPoints(verificationGrid()); setWorkspaceDirty(true); setResult(undefined); }}>重置 9 点</Button>
          <Button onClick={autoCapture} loading={busy}>自动采集 9 点</Button>
          <Button type="primary" onClick={solve} loading={busy}>计算</Button>
          <Tag color={workspaceDirty ? 'gold' : 'green'}>{workspaceDirty ? '已修改 / 未保存版本' : `资产 V${loadedAssetVersion}`}</Tag>
          <Button disabled={!canApplyToSelectedNode || !result} onClick={applyToNode}>应用到节点</Button>
        </Space>
      </div>

      <div className="calibration-metrics">
        <Statistic title="标定点数" value={result?.usedPointCount ?? points.filter((x) => x.enabled).length} />
        <Statistic title={`均方根误差 (${targetUnit})`} value={result?.rmse ?? 0} precision={4} />
        <Statistic title={`最大误差 (${targetUnit})`} value={result?.maxError ?? 0} precision={4} />
        <Statistic title={`验证均方根误差 (${targetUnit})`} value={result?.verificationRmse ?? 0} precision={4} />
        <Statistic title={`验证最大误差 (${targetUnit})`} value={result?.verificationMaxError ?? 0} precision={4} />
      </div>

      <div className="calibration-grid">
        <section className="calibration-editor">
          <div className="panel-title">标定点</div>
          <Table size="small" pagination={false} rowKey="index" dataSource={points} columns={pointColumns(false)} scroll={{ y: 250 }} />
          <div className="calibration-actions"><Button size="small" onClick={() => addPoint(false)}>+ 添加标定点</Button></div>

          <div className="panel-title calibration-section-title">验证点</div>
          <Table size="small" pagination={false} rowKey="index" dataSource={verificationPoints} columns={pointColumns(true)} scroll={{ y: 160 }} />
          <div className="calibration-actions"><Button size="small" onClick={() => addPoint(true)}>+ 添加验证点</Button></div>
        </section>

        <section className="calibration-analysis">
          <div className="panel-title">残差 / 误差热力图</div>
          <Table<CalibrationResidual> size="small" pagination={false} rowKey="index" dataSource={result?.verificationResiduals.length ? result.verificationResiduals : result?.residuals ?? []} columns={residualColumns} scroll={{ y: 210 }} />
          <div className="calibration-heatmap" style={{ gridTemplateColumns: `repeat(${Math.max(1, Math.max(...(result?.heatmap.map((x) => x.column) ?? [0])) + 1)},1fr)` }}>
            {(result?.heatmap ?? []).map((cell) => {
              const ratio = Math.min(1, cell.error / heatMax);
              return <div key={`${cell.column}-${cell.row}`} className="heat-cell" title={`(${cell.x.toFixed(2)}, ${cell.y.toFixed(2)}) · ${cell.error.toFixed(4)} ${targetUnit}`} style={{ background: `rgba(255,77,79,${0.10 + ratio * 0.78})` }}><b>{cell.error.toFixed(3)}</b><span>{cell.x.toFixed(0)},{cell.y.toFixed(0)}</span></div>;
            })}
          </div>
          {result && <pre className="calibration-matrix">{[
            [result.transform.h00, result.transform.h01, result.transform.h02],
            [result.transform.h10, result.transform.h11, result.transform.h12],
            [result.transform.h20, result.transform.h21, result.transform.h22]
          ].map((row) => row.map((v) => v.toFixed(7).padStart(12)).join(' ')).join('\n')}</pre>}
        </section>
      </div>

      <div className="calibration-assets">
        <div className="panel-title">标定资产版本</div>
        <Space wrap className="calibration-asset-form">
          <Input value={assetId} onChange={(e) => setAssetId(e.target.value)} addonBefore="ID" style={{ width: 270 }} />
          <Input value={assetName} onChange={(e) => setAssetName(e.target.value)} addonBefore="名称" style={{ width: 300 }} />
          <Button onClick={saveVersion} disabled={!result} loading={busy}>保存版本</Button>
          <Button onClick={refreshAssets}>刷新</Button>
        </Space>
        <Table
          size="small"
          pagination={false}
          rowKey="id"
          dataSource={assets}
          rowClassName={(row) => row.id === selectedAssetId ? 'calibration-selected-row' : ''}
          onRow={(row) => ({ onClick: () => setSelectedAssetId(row.id) })}
          columns={[
            { title: '资产', dataIndex: 'name', width: 210, render: (v: string, row: CalibrationAssetDescriptor) => <><b>{v}</b><div className="muted-code">{row.id}</div></> },
            { title: '最新版本', dataIndex: 'latestVersion', width: 70, render: (v: number) => `V${v}` },
            { title: '已发布', dataIndex: 'publishedVersion', width: 90, render: (v?: number) => v ? <Tag color="green">V{v}</Tag> : <Tag>无</Tag> },
            { title: '版本', render: (_: unknown, row: CalibrationAssetDescriptor) => <Space wrap>{row.versions.map((v) => <Button key={v.version} size="small" type={v.published ? 'primary' : 'default'} onClick={(e) => { e.stopPropagation(); loadVersion(row.id, v.version); }}>V{v.version}</Button>)}</Space> },
            { title: '操作', width: 210, render: (_: unknown, row: CalibrationAssetDescriptor) => <Space>{row.latestVersion > 0 && <Button size="small" onClick={(e) => { e.stopPropagation(); publish(row.id, row.latestVersion); }}>发布最新版本</Button>}{row.publishedVersion && row.publishedVersion > 1 && <Button size="small" onClick={(e) => { e.stopPropagation(); publish(row.id, 1, true); }}>回滚到 V1</Button>}</Space> }
          ]}
        />
        {selectedAsset && <div className="calibration-publication-history">{selectedAsset.publicationHistory.map((x, i) => <Tag key={`${x.at}-${i}`}>{x.action} V{x.version}</Tag>)}</div>}
      </div>
    </Modal>
  );
}
