import { useEffect, useState } from 'react';
import { Alert, Button, Card, Empty, Input, Modal, Popconfirm, Space, Table, Tag, Typography, message } from 'antd';
import { apiFetch } from '../apiClient';
import { responseErrorMessage } from '../apiErrors';
import { modelAssets } from '../endpoints';
import DataErrorAlert from './DataErrorAlert';

type VersionInfo = {
  version: number; sha256: string; sizeBytes: number; trainingConfig: string;
  sourceImageSha256: string; note: string; createdAt: string; createdBy: string;
};
type AssetDescriptor = {
  id: string; name: string; description: string; createdAt: string; updatedAt: string;
  latestVersion: number; versions: VersionInfo[];
};
type AssetReference = { workflowId: string; nodeId: string };

type Props = { open: boolean; onClose: () => void };

const formatBytes = (value: number) => value >= 1024 * 1024
  ? `${(value / 1024 / 1024).toFixed(2)} MB`
  : value >= 1024 ? `${(value / 1024).toFixed(1)} KB` : `${value} B`;

/**
 * 形状模型资产管理（P1）。
 *
 * 只做"看得到、改得动、删得安全"：版本字节不可变，重命名只改元数据；删除前由后端扫描工作流引用，
 * 被引用时返回 409 并把引用节点列出来——这里直接展示服务端文案，不在前端重复实现引用判断。
 */
export default function ModelAssetPanel({ open, onClose }: Props) {
  const [assets, setAssets] = useState<AssetDescriptor[]>([]);
  const [busy, setBusy] = useState(false);
  const [loadError, setLoadError] = useState<string>();
  const [renaming, setRenaming] = useState<{ asset: AssetDescriptor; name: string; description: string } | null>(null);
  const [references, setReferences] = useState<{ asset: AssetDescriptor; items: AssetReference[] } | null>(null);
  const [messageApi, holder] = message.useMessage();

  const load = async () => {
    setBusy(true);
    try {
      const response = await apiFetch(modelAssets.list);
      if (!response.ok) {
        setLoadError(await responseErrorMessage(response, `读取模型资产失败（HTTP ${response.status}）`));
        return;
      }
      setAssets(await response.json());
      setLoadError(undefined);
    } catch (error) {
      setLoadError(error instanceof Error ? `读取模型资产失败：${error.message}` : '读取模型资产失败：网络请求未完成');
    } finally { setBusy(false); }
  };

  useEffect(() => { if (open) void load(); }, [open]);

  const rename = async () => {
    if (!renaming) return;
    setBusy(true);
    try {
      const response = await apiFetch(modelAssets.byId(renaming.asset.id), {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: renaming.name.trim(), description: renaming.description })
      });
      if (!response.ok) throw new Error(await responseErrorMessage(response, `重命名失败（HTTP ${response.status}）`));
      messageApi.success('已更新资产名称');
      setRenaming(null);
      await load();
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '重命名失败');
    } finally { setBusy(false); }
  };

  const remove = async (asset: AssetDescriptor) => {
    setBusy(true);
    try {
      const response = await apiFetch(modelAssets.byId(asset.id), { method: 'DELETE' });
      if (response.status === 404) { messageApi.warning('资产已不存在，正在刷新列表'); await load(); return; }
      if (!response.ok) throw new Error(await responseErrorMessage(response, '删除失败'));
      messageApi.success(`已删除资产 ${asset.id}`);
      await load();
    } catch (error) {
      // 409 的文案里带引用节点清单：直接展示，避免"删不掉但不知道为什么"。
      messageApi.error(error instanceof Error ? error.message : '删除失败');
    } finally { setBusy(false); }
  };

  const showReferences = async (asset: AssetDescriptor) => {
    setBusy(true);
    try {
      const response = await apiFetch(modelAssets.references(asset.id));
      if (!response.ok) throw new Error(await responseErrorMessage(response, '读取引用失败'));
      setReferences({ asset, items: await response.json() });
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '读取引用失败');
    } finally { setBusy(false); }
  };

  return (
    <>
      {holder}
      <Modal open={open} onCancel={onClose} footer={null} width={1000} title="模型资产">
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          <Space wrap>
            <Button size="small" onClick={() => void load()} loading={busy}>刷新</Button>
            <Typography.Text type="secondary">
              模型字节按 SHA256 内容寻址保存；版本不可变，节点通过 modelAssetId/modelAssetVersion 引用。
            </Typography.Text>
          </Space>
          {loadError && <DataErrorAlert scope="模型资产" error={loadError} onRetry={() => void load()} retrying={busy} />}
          <Table<AssetDescriptor>
            size="small"
            rowKey="id"
            dataSource={assets}
            loading={busy}
            locale={{ emptyText: <Empty description="还没有模型资产：在形状匹配训练完成后点「保存为资产」" /> }}
            expandable={{
              expandedRowRender: (asset) => (
                <Table<VersionInfo>
                  size="small"
                  rowKey="version"
                  pagination={false}
                  dataSource={[...asset.versions].sort((a, b) => b.version - a.version)}
                  columns={[
                    { title: '版本', dataIndex: 'version', width: 70, render: (value: number) => <Tag>v{value}</Tag> },
                    { title: 'SHA-256', dataIndex: 'sha256', width: 180, render: (value: string) => <Typography.Text code>{value.slice(0, 16)}…</Typography.Text> },
                    { title: '大小', dataIndex: 'sizeBytes', width: 100, render: (value: number) => formatBytes(value) },
                    { title: '训练配置指纹', dataIndex: 'trainingConfig', render: (value: string) => <Typography.Text type="secondary">{value || '—'}</Typography.Text> },
                    { title: '说明', dataIndex: 'note', render: (value: string) => value || '—' },
                    { title: '创建时间', dataIndex: 'createdAt', width: 180, render: (value: string) => new Date(value).toLocaleString() }
                  ]}
                />
              )
            }}
            columns={[
              { title: '资产', dataIndex: 'name', render: (_: string, asset) => (
                <Space direction="vertical" size={0}>
                  <Typography.Text strong>{asset.name}</Typography.Text>
                  <Typography.Text type="secondary" code>{asset.id}</Typography.Text>
                </Space>
              ) },
              { title: '最新版本', dataIndex: 'latestVersion', width: 100, render: (value: number) => <Tag color="blue">v{value}</Tag> },
              { title: '大小', width: 100, render: (_: unknown, asset) => formatBytes(asset.versions.find((item) => item.version === asset.latestVersion)?.sizeBytes ?? 0) },
              { title: '版本数', width: 80, render: (_: unknown, asset) => asset.versions.length },
              { title: '更新时间', dataIndex: 'updatedAt', width: 180, render: (value: string) => new Date(value).toLocaleString() },
              { title: '操作', width: 220, render: (_: unknown, asset) => (
                <Space size="small">
                  <Button size="small" onClick={() => setRenaming({ asset, name: asset.name, description: asset.description })}>重命名</Button>
                  <Button size="small" onClick={() => void showReferences(asset)}>引用</Button>
                  <Popconfirm title={`删除资产 ${asset.id}？`} description="版本字节会保留在库里，但资产对节点不再可见。"
                    okText="删除" cancelText="取消" onConfirm={() => void remove(asset)}>
                    <Button size="small" danger>删除</Button>
                  </Popconfirm>
                </Space>
              ) }
            ]}
          />
          <Alert type="info" showIcon message="删除被引用的资产会被拒绝"
            description="后端会扫描工作流引用并返回 409 与节点清单；先把节点改到其他资产或清空 modelAssetId，再删除。" />
        </Space>
      </Modal>

      <Modal open={Boolean(renaming)} onCancel={() => setRenaming(null)} title="重命名模型资产"
        onOk={() => void rename()} okText="保存" cancelText="取消" confirmLoading={busy}>
        <Space direction="vertical" style={{ width: '100%' }}>
          <Input value={renaming?.name ?? ''} maxLength={200} placeholder="资产名称"
            onChange={(event) => setRenaming((previous) => previous ? { ...previous, name: event.target.value } : previous)} />
          <Input.TextArea value={renaming?.description ?? ''} rows={3} placeholder="描述（可选）"
            onChange={(event) => setRenaming((previous) => previous ? { ...previous, description: event.target.value } : previous)} />
        </Space>
      </Modal>

      <Modal open={Boolean(references)} onCancel={() => setReferences(null)} footer={null} title="被哪些节点引用">
        {references && references.items.length === 0
          ? <Empty description="没有任何工作流节点引用该资产，可以安全删除" />
          : <Table<AssetReference> size="small" rowKey={(row) => `${row.workflowId}/${row.nodeId}`}
              pagination={false} dataSource={references?.items ?? []}
              columns={[
                { title: '工作流', dataIndex: 'workflowId' },
                { title: '节点', dataIndex: 'nodeId' }
              ]} />}
      </Modal>
    </>
  );
}
