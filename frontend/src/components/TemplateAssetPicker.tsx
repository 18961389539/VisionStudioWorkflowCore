import { useEffect, useState } from 'react';
import { Alert, Button, Modal, Space, Table, Tag, Typography, message } from 'antd';
import { apiFetch } from '../apiClient';
import { responseErrorMessage } from '../apiErrors';
import { templateAssets } from '../endpoints';

type TemplateAssetVersion = {
  version: number; sha256: string; sizeBytes: number; width: number; height: number;
  contentType: string; note: string; createdAt: string; createdBy: string;
};
type TemplateAsset = {
  id: string; name: string; description: string; createdAt: string; updatedAt: string;
  latestVersion: number; versions: TemplateAssetVersion[];
};

type Props = {
  assetId: string;
  version: number;
  disabled?: boolean;
  /** 选定资产：version 传该资产的 latest；清除时传 ('', 0)。 */
  onChange: (assetId: string, version: number) => void;
};

const formatBytes = (value: number) => value >= 1024 * 1024
  ? `${(value / 1024 / 1024).toFixed(2)} MB`
  : value >= 1024 ? `${(value / 1024).toFixed(1)} KB` : `${value} B`;

/**
 * match.template 的模板来源选择器（P2）。
 *
 * 只负责"选哪个资产"，不改变节点执行优先级（端口 > 资产 > 模板文件）。
 * 选择困难点在于用户拿不到资产 id，所以这里把列表、尺寸与最新版本一起显示出来。
 */
export default function TemplateAssetPicker({ assetId, version, disabled, onChange }: Props) {
  const [open, setOpen] = useState(false);
  const [assets, setAssets] = useState<TemplateAsset[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [messageApi, holder] = message.useMessage();

  const load = async () => {
    setBusy(true);
    try {
      const response = await apiFetch(templateAssets.list);
      if (!response.ok) {
        setError(await responseErrorMessage(response, `读取模板图资产失败（HTTP ${response.status}）`));
        return;
      }
      setAssets(await response.json());
      setError(undefined);
    } catch (loadError) {
      setError(loadError instanceof Error ? `读取模板图资产失败：${loadError.message}` : '读取模板图资产失败：网络请求未完成');
    } finally { setBusy(false); }
  };

  useEffect(() => { if (open) void load(); }, [open]);

  return (
    <div className="template-asset-picker">
      {holder}
      <Space wrap size="small">
        <Typography.Text type="secondary">模板来源：</Typography.Text>
        {assetId
          ? <Tag color="blue">{assetId} · {version > 0 ? `v${version}` : '最新版'}</Tag>
          : <Typography.Text type="secondary">未绑定资产（使用模板端口或模板文件）</Typography.Text>}
        <Button size="small" disabled={disabled} onClick={() => setOpen(true)}>从资产选择</Button>
        {assetId && <Button size="small" disabled={disabled} onClick={() => { onChange('', 0); messageApi.success('已清除模板资产，节点将按端口或模板文件取图'); }}>清除</Button>}
      </Space>
      <Modal open={open} onCancel={() => setOpen(false)} footer={null} width={900} title="选择模板图资产">
        <Space direction="vertical" style={{ width: '100%' }}>
          {error && <Alert type="error" showIcon title="读取模板图资产失败" description={error} />}
          <Table<TemplateAsset>
            size="small"
            rowKey="id"
            loading={busy}
            dataSource={assets}
            pagination={false}
            locale={{ emptyText: '还没有模板图资产；先用 POST /api/template-assets 上传一张模板图' }}
            columns={[
              { title: '资产', dataIndex: 'name', render: (_: string, asset) => (
                <Space direction="vertical" size={0}>
                  <Typography.Text strong>{asset.name}</Typography.Text>
                  <Typography.Text type="secondary" code>{asset.id}</Typography.Text>
                </Space>
              ) },
              { title: '最新版本', dataIndex: 'latestVersion', width: 100, render: (value: number) => <Tag>v{value}</Tag> },
              { title: '尺寸', width: 130, render: (_: unknown, asset) => {
                const latest = asset.versions.find((item) => item.version === asset.latestVersion);
                return latest ? `${latest.width} × ${latest.height}` : '—';
              } },
              { title: '大小', width: 100, render: (_: unknown, asset) => {
                const latest = asset.versions.find((item) => item.version === asset.latestVersion);
                return latest ? formatBytes(latest.sizeBytes) : '—';
              } },
              { title: '操作', width: 90, render: (_: unknown, asset) => (
                <Button size="small" type="primary" onClick={() => {
                  onChange(asset.id, asset.latestVersion);
                  setOpen(false);
                  messageApi.success(`已选择模板资产 ${asset.id} v${asset.latestVersion}`);
                }}>选用</Button>
              ) }
            ]}
          />
          <Alert type="info" showIcon
            description="选定后节点优先使用该资产（早于模板文件），但模板端口连接时仍以端口为准。" />
        </Space>
      </Modal>
    </div>
  );
}
