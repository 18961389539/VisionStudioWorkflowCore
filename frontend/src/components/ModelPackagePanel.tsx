import { useEffect, useState } from 'react';
import { Alert, Button, Card, Input, Modal, Select, Space, Table, Tag, Typography, message } from 'antd';
import { apiFetch } from '../apiClient';
import { API_BASE, apiUrl } from '../endpoints';

type PackageSummary = {
  id: string; kind: string; name: string; description?: string | null; path: string; importedAt: string; fileCount: number;
};
type SmokeResult = { passed: boolean; packagePath: string; kind: string; checks: string[]; failures: string[]; diagnosticsJson: string };

type Props = { open: boolean; onClose: () => void };

export default function ModelPackagePanel({ open, onClose }: Props) {
  const [packages, setPackages] = useState<PackageSummary[]>([]);
  const [sourcePath, setSourcePath] = useState('');
  const [expectedKind, setExpectedKind] = useState<string | undefined>();
  const [packageId, setPackageId] = useState('');
  const [busy, setBusy] = useState(false);
  const [smoke, setSmoke] = useState<SmokeResult | null>(null);
  const [messageApi, holder] = message.useMessage();

  const load = async () => {
    const response = await apiFetch(apiUrl('model-packages'));
    if (response.ok) setPackages(await response.json());
  };

  useEffect(() => { if (open) void load(); }, [open]);

  const preflight = async () => {
    setBusy(true);
    try {
      const response = await apiFetch(apiUrl('model-packages/preflight'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sourcePath, expectedKind: expectedKind || null }),
      });
      const text = await response.text();
      if (!response.ok) throw new Error(text || response.statusText);
      messageApi.success('预检通过：清单与校验和有效（外部训练包，非本机训练）。');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : String(error));
    } finally { setBusy(false); }
  };

  const importPackage = async () => {
    setBusy(true);
    try {
      const response = await apiFetch(apiUrl('model-packages/import'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sourcePath, packageId: packageId || null, expectedKind: expectedKind || null }),
      });
      const text = await response.text();
      if (!response.ok) throw new Error(text || response.statusText);
      messageApi.success('外部训练包已导入。节点可通过 packagePath 引用。');
      setSourcePath(''); setPackageId('');
      await load();
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : String(error));
    } finally { setBusy(false); }
  };

  const runSmoke = async (id: string) => {
    setBusy(true);
    try {
      const response = await apiFetch(`${API_BASE}/model-packages/${encodeURIComponent(id)}/smoke`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ openOnnxSession: false }),
      });
      const data = await response.json();
      if (!response.ok) throw new Error(JSON.stringify(data));
      setSmoke(data);
      if (data.passed) messageApi.success('冒烟评估通过');
      else messageApi.warning('冒烟评估未通过');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : String(error));
    } finally { setBusy(false); }
  };

  const remove = async (id: string) => {
    setBusy(true);
    try {
      const response = await apiFetch(`${API_BASE}/model-packages/${encodeURIComponent(id)}`, { method: 'DELETE' });
      if (!response.ok) throw new Error(await response.text());
      messageApi.success('已删除');
      await load();
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : String(error));
    } finally { setBusy(false); }
  };

  return (
    <Modal open={open} onCancel={onClose} onOk={onClose} width={960} title="外部训练包（导入 / 校验 / 冒烟）" okText="关闭" cancelButtonProps={{ style: { display: 'none' } }}>
      {holder}
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 12 }}
        message="仅支持外部训练产物导入"
        description="OCR tessdata、ONNX 分类/异常包、缺陷金样参考包。不做本机 SGD/反向传播训练；相对 HALCON 训练闭环仍有能力差距。"
      />
      <Card size="small" title="导入" style={{ marginBottom: 12 }}>
        <Space direction="vertical" style={{ width: '100%' }}>
          <Input placeholder="源目录或 .zip 绝对路径" value={sourcePath} onChange={(e) => setSourcePath(e.target.value)} />
          <Space wrap>
            <Select
              allowClear placeholder="期望类型（可选）" style={{ width: 220 }}
              value={expectedKind} onChange={setExpectedKind}
              options={[
                { value: 'Ocr', label: 'OCR tessdata' },
                { value: 'Classification', label: 'ONNX 分类' },
                { value: 'Anomaly', label: 'ONNX 异常' },
                { value: 'ReferenceCompare', label: '金样参考' },
              ]}
            />
            <Input placeholder="包 ID（可选）" value={packageId} onChange={(e) => setPackageId(e.target.value)} style={{ width: 200 }} />
            <Button disabled={busy || !sourcePath.trim()} onClick={() => void preflight()}>预检</Button>
            <Button type="primary" disabled={busy || !sourcePath.trim()} onClick={() => void importPackage()}>导入</Button>
          </Space>
        </Space>
      </Card>
      <Table
        rowKey="id" size="small" dataSource={packages} pagination={false}
        columns={[
          { title: 'ID', dataIndex: 'id', width: 160 },
          { title: '类型', dataIndex: 'kind', width: 120, render: (v: string) => <Tag>{v}</Tag> },
          { title: '名称', dataIndex: 'name' },
          { title: '文件数', dataIndex: 'fileCount', width: 80 },
          {
            title: '操作', width: 200,
            render: (_, row) => (
              <Space>
                <Button size="small" onClick={() => void runSmoke(row.id)}>冒烟</Button>
                <Button size="small" danger onClick={() => void remove(row.id)}>删除</Button>
              </Space>
            ),
          },
        ]}
      />
      {smoke && (
        <Card size="small" title="最近冒烟结果" style={{ marginTop: 12 }}>
          <Typography.Paragraph>
            <Tag color={smoke.passed ? 'green' : 'red'}>{smoke.passed ? 'PASS' : 'FAIL'}</Tag>
            {smoke.kind} — checks: {smoke.checks.join(', ')}
          </Typography.Paragraph>
          {smoke.failures?.length > 0 && (
            <Typography.Paragraph type="danger">{smoke.failures.join('\n')}</Typography.Paragraph>
          )}
        </Card>
      )}
    </Modal>
  );
}
