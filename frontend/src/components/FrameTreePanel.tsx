import { useMemo, useState } from 'react';
import { Button, Input, InputNumber, Modal, Select, Space, Table, Tag, message } from 'antd';

type FrameTransform = {
  key: string;
  sourceFrame: string;
  targetFrame: string;
  unit: string;
  x: number;
  y: number;
  angleDeg: number;
};

type ResolveResult = {
  transform: {
    sourceFrame: string;
    targetFrame: string;
    sourceUnit: string;
    targetUnit: string;
    matrix: number[];
  };
  path: string[];
};

const initialTransforms: FrameTransform[] = [
  { key: '1', sourceFrame: 'Workpiece', targetFrame: 'Fixture', unit: 'mm', x: 100, y: 50, angleDeg: 5 },
  { key: '2', sourceFrame: 'Fixture', targetFrame: 'RobotBase', unit: 'mm', x: 500, y: 200, angleDeg: 0 },
  { key: '3', sourceFrame: 'CameraPlane', targetFrame: 'Tool', unit: 'mm', x: 35, y: -8, angleDeg: 1.5 }
];

export default function FrameTreePanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [rows, setRows] = useState<FrameTransform[]>(initialTransforms);
  const [source, setSource] = useState('Workpiece');
  const [target, setTarget] = useState('RobotBase');
  const [result, setResult] = useState<ResolveResult>();
  const [busy, setBusy] = useState(false);
  const [messageApi, contextHolder] = message.useMessage();

  const frames = useMemo(() => Array.from(new Set(rows.flatMap((r) => [r.sourceFrame, r.targetFrame]))), [rows]);

  const update = (key: string, patch: Partial<FrameTransform>) =>
    setRows((current) => current.map((r) => r.key === key ? { ...r, ...patch } : r));

  const resolve = async () => {
    setBusy(true);
    try {
      const response = await fetch('/api/frames/resolve', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ transforms: rows.map(({ key: _key, ...r }) => r), sourceFrame: source, targetFrame: target })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error ?? '坐标路径解析失败');
      setResult(data);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '坐标路径解析失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal open={open} title="坐标系树" width={1120} footer={null} onCancel={onClose}>
      {contextHolder}
      <div style={{ marginBottom: 12 }}>
        <Space wrap>
          <Tag color="blue">静态坐标系图</Tag>
          <span>源坐标系</span>
          <Select value={source} style={{ width: 150 }} options={frames.map((f) => ({ label: f, value: f }))} onChange={setSource} />
          <span>目标坐标系</span>
          <Select value={target} style={{ width: 150 }} options={frames.map((f) => ({ label: f, value: f }))} onChange={setTarget} />
          <Button type="primary" loading={busy} onClick={resolve}>解析路径</Button>
          <Button onClick={() => setRows((r) => [...r, { key: crypto.randomUUID(), sourceFrame: 'FrameA', targetFrame: 'FrameB', unit: 'mm', x: 0, y: 0, angleDeg: 0 }])}>添加变换</Button>
        </Space>
      </div>

      <Table
        size="small"
        pagination={false}
        rowKey="key"
        dataSource={rows}
        columns={[
          { title: '源坐标系', width: 150, render: (_: unknown, r: FrameTransform) => <Input value={r.sourceFrame} onChange={(e) => update(r.key, { sourceFrame: e.target.value })} /> },
          { title: '目标坐标系', width: 150, render: (_: unknown, r: FrameTransform) => <Input value={r.targetFrame} onChange={(e) => update(r.key, { targetFrame: e.target.value })} /> },
          { title: '单位', width: 90, render: (_: unknown, r: FrameTransform) => <Select value={r.unit} style={{ width: 76 }} options={['mm','cm','m'].map((x) => ({ label: x, value: x }))} onChange={(v) => update(r.key, { unit: v })} /> },
          { title: 'X', width: 120, render: (_: unknown, r: FrameTransform) => <InputNumber value={r.x} onChange={(v) => update(r.key, { x: Number(v ?? 0) })} /> },
          { title: 'Y', width: 120, render: (_: unknown, r: FrameTransform) => <InputNumber value={r.y} onChange={(v) => update(r.key, { y: Number(v ?? 0) })} /> },
          { title: '角度 °', width: 120, render: (_: unknown, r: FrameTransform) => <InputNumber value={r.angleDeg} onChange={(v) => update(r.key, { angleDeg: Number(v ?? 0) })} /> },
          { title: '', width: 70, render: (_: unknown, r: FrameTransform) => <Button danger size="small" onClick={() => setRows((x) => x.filter((i) => i.key !== r.key))}>删除</Button> }
        ]}
      />

      {result && (
        <div style={{ marginTop: 16 }}>
          <Space wrap>
            <strong>解析结果：</strong>
            <Tag color="green">{result.path.join(' → ')}</Tag>
            <span>{result.transform.sourceUnit} → {result.transform.targetUnit}</span>
          </Space>
          <pre className="dsl-view" style={{ marginTop: 10 }}>{JSON.stringify(result.transform.matrix, null, 2)}</pre>
        </div>
      )}
      <div style={{ marginTop: 12, opacity: 0.72 }}>
        眼在手上等动态坐标变换（例如 Tool → RobotBase）会在工作流中根据机器人当前位姿生成，不应添加到此静态列表中。
      </div>
    </Modal>
  );
}
