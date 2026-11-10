import { useEffect, useRef, useState } from 'react';
import { Button, Empty, Popconfirm, Space, Table, Tag, Typography, message } from 'antd';
import { apiFetch } from '../apiClient';
import { responseErrorMessage } from '../apiErrors';
import { templateAssets } from '../endpoints';
import { bytesToBase64 } from '../modelAssets';
import DataErrorAlert from './DataErrorAlert';

type TemplateVersion = {
  version: number; sha256: string; sizeBytes: number; width: number; height: number;
  contentType: string; note: string; createdAt: string; createdBy: string;
};
type TemplateAsset = {
  id: string; name: string; description: string; createdAt: string; updatedAt: string;
  latestVersion: number; versions: TemplateVersion[];
};

const formatBytes = (value: number) => value >= 1024 * 1024
  ? `${(value / 1024 / 1024).toFixed(2)} MB`
  : value >= 1024 ? `${(value / 1024).toFixed(1)} KB` : `${value} B`;
const latest = (asset: TemplateAsset) => asset.versions.find((item) => item.version === asset.latestVersion);

/**
 * 模板图资产列表与上传（P2）。
 *
 * 尺寸由服务端解码后确定（前端只传字节），所以这里不读图片宽高——避免浏览器解码失败或声明值与实际不符。
 * 上传即创建新资产；同一张图的不同版本走 append（节点侧仍可 pin 具体版本）。
 */
export default function TemplateAssetTable() {
  const [assets, setAssets] = useState<TemplateAsset[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const inputRef = useRef<HTMLInputElement | null>(null);
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

  useEffect(() => { void load(); }, []);

  const upload = async (file: File) => {
    setBusy(true);
    try {
      const base64 = bytesToBase64(new Uint8Array(await file.arrayBuffer()));
      const response = await apiFetch(templateAssets.create, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: file.name.replace(/\.[^.]+$/, ''),
          imageBase64: base64,
          contentType: file.type || 'image/png',
          width: 0,
          height: 0,
          note: `Uploaded ${file.name}`
        })
      });
      if (!response.ok) throw new Error(await responseErrorMessage(response, `上传失败（HTTP ${response.status}）`));
      const created: TemplateAsset = await response.json();
      const version = latest(created);
      messageApi.success(`已创建模板资产 ${created.id}（${version?.width ?? '?'} × ${version?.height ?? '?'}）`);
      await load();
    } catch (uploadError) {
      messageApi.error(uploadError instanceof Error ? uploadError.message : '上传失败');
    } finally {
      setBusy(false);
      if (inputRef.current) inputRef.current.value = '';
    }
  };

  const remove = async (asset: TemplateAsset) => {
    setBusy(true);
    try {
      const response = await apiFetch(templateAssets.byId(asset.id), { method: 'DELETE' });
      if (!response.ok) throw new Error(await responseErrorMessage(response, '删除失败'));
      messageApi.success(`已删除模板资产 ${asset.id}`);
      await load();
    } catch (removeError) {
      messageApi.error(removeError instanceof Error ? removeError.message : '删除失败');
    } finally { setBusy(false); }
  };

  return (
    <Space direction="vertical" style={{ width: '100%' }}>
      {holder}
      <Space wrap>
        <Button size="small" onClick={() => void load()} loading={busy}>刷新</Button>
        <Button size="small" type="primary" loading={busy} onClick={() => inputRef.current?.click()}>上传模板图</Button>
        <Typography.Text type="secondary">上传后尺寸由服务端解码确定；节点里用「从资产选择」引用。</Typography.Text>
        <input ref={inputRef} type="file" accept="image/png,image/jpeg,image/bmp,image/tiff" style={{ display: 'none' }}
          onChange={(event) => { const file = event.target.files?.[0]; if (file) void upload(file); }} />
      </Space>
      {error && <DataErrorAlert scope="模板图资产" error={error} onRetry={() => void load()} retrying={busy} />}
      <Table<TemplateAsset>
        size="small"
        rowKey="id"
        loading={busy}
        dataSource={assets}
        pagination={false}
        locale={{ emptyText: <Empty description="还没有模板图资产：点「上传模板图」选一张本地图片" /> }}
        columns={[
          { title: '资产', dataIndex: 'name', render: (_: string, asset) => (
            <Space direction="vertical" size={0}>
              <Typography.Text strong>{asset.name}</Typography.Text>
              <Typography.Text type="secondary" code>{asset.id}</Typography.Text>
            </Space>
          ) },
          { title: '最新版本', dataIndex: 'latestVersion', width: 100, render: (value: number) => <Tag>v{value}</Tag> },
          { title: '尺寸', width: 130, render: (_: unknown, asset) => {
            const version = latest(asset);
            return version ? `${version.width} × ${version.height}` : '—';
          } },
          { title: '大小', width: 100, render: (_: unknown, asset) => {
            const version = latest(asset);
            return version ? formatBytes(version.sizeBytes) : '—';
          } },
          { title: '更新时间', dataIndex: 'updatedAt', width: 180, render: (value: string) => new Date(value).toLocaleString() },
          { title: '操作', width: 100, render: (_: unknown, asset) => (
            <Popconfirm title={`删除模板图资产 ${asset.id}？`} description="被工作流节点引用时会被拒绝。"
              okText="删除" cancelText="取消" onConfirm={() => void remove(asset)}>
              <Button size="small" danger>删除</Button>
            </Popconfirm>
          ) }
        ]}
      />
    </Space>
  );
}
