import { useEffect, useState } from 'react';
import { Alert, Button, Modal, Popconfirm, Space, Statistic, Switch, Table, Tag, Typography, message } from 'antd';
import type { SchemaMigrationStatus, StorageBackupDescriptor, StorageCapacityStatus, StorageRestoreResult } from '../types';
import { localizeStatus } from '../i18n';

type Props = { open: boolean; onClose: () => void };

const mb = (bytes?: number) => ((bytes ?? 0) / 1024 / 1024).toFixed(1);
const gb = (bytes?: number) => ((bytes ?? 0) / 1024 / 1024 / 1024).toFixed(2);

async function readProblem(response: Response) {
  try {
    const data = await response.json();
    return data.detail ?? data.title ?? `HTTP ${response.status}`;
  } catch { return `HTTP ${response.status}`; }
}

export default function StoragePanel({ open, onClose }: Props) {
  const [capacity, setCapacity] = useState<StorageCapacityStatus>();
  const [schema, setSchema] = useState<SchemaMigrationStatus>();
  const [backups, setBackups] = useState<StorageBackupDescriptor[]>([]);
  const [includeArtifacts, setIncludeArtifacts] = useState(true);
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();
  const [messageApi, contextHolder] = message.useMessage();

  const refresh = async () => {
    setError(undefined);
    const capacityResponse = await fetch('/api/storage/capacity');
    if (capacityResponse.ok) setCapacity(await capacityResponse.json());

    const [schemaResponse, backupResponse] = await Promise.all([
      fetch('/api/storage/schema'), fetch('/api/storage/backups')
    ]);
    if (schemaResponse.ok) setSchema(await schemaResponse.json());
    if (backupResponse.ok) setBackups(await backupResponse.json());
    else if (backupResponse.status === 403) setError('Administrator role is required for backup and restore operations.');
    else setError(await readProblem(backupResponse));
  };

  useEffect(() => { if (open) void refresh(); }, [open]);

  const createBackup = async () => {
    setBusy('create');
    try {
      const response = await fetch('/api/storage/backups', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ includeArtifacts })
      });
      if (!response.ok) throw new Error(await readProblem(response));
      const data: StorageBackupDescriptor = await response.json();
      messageApi.success(`Backup ${data.backupId} created`);
      await refresh();
    } catch (e) { messageApi.error(e instanceof Error ? e.message : 'Backup failed'); }
    finally { setBusy(undefined); }
  };

  const deleteBackup = async (backupId: string) => {
    setBusy(`delete:${backupId}`);
    try {
      const response = await fetch(`/api/storage/backups/${encodeURIComponent(backupId)}`, { method: 'DELETE' });
      if (!response.ok) throw new Error(await readProblem(response));
      messageApi.success('Backup deleted');
      await refresh();
    } catch (e) { messageApi.error(e instanceof Error ? e.message : 'Delete failed'); }
    finally { setBusy(undefined); }
  };

  const restore = (backup: StorageBackupDescriptor) => {
    Modal.confirm({
      title: `Restore ${backup.backupId}?`,
      content: 'Production Runtime must be stopped. The API enters maintenance mode, restores SQLite with the Online Backup API, and optionally restores preview artifacts. Restart the host after a successful restore.',
      okText: 'Restore backup', okButtonProps: { danger: true },
      onOk: async () => {
        setBusy(`restore:${backup.backupId}`);
        try {
          const response = await fetch('/api/storage/restore', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ backupId: backup.backupId, restoreArtifacts: backup.includesArtifacts })
          });
          if (!response.ok) throw new Error(await readProblem(response));
          const result: StorageRestoreResult = await response.json();
          messageApi.success(`已还原备份 ${result.backupId}。建议重启程序。`);
          await refresh();
        } catch (e) { messageApi.error(e instanceof Error ? e.message : '还原失败'); }
        finally { setBusy(undefined); }
      }
    });
  };

  const levelColor = capacity?.level === 'Critical' ? 'red' : capacity?.level === 'Warning' ? 'orange' : 'green';
  return <Modal open={open} onCancel={onClose} footer={null} width={1120} title="存储维护 · 架构 / 容量 / 备份 / 还原" destroyOnHidden>
    {contextHolder}
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      {error && <Alert type="warning" showIcon message={error} />}
      {capacity && <>
        <Alert type={capacity.level === 'Critical' ? 'error' : capacity.level === 'Warning' ? 'warning' : 'success'} showIcon
          message={<Space>存储容量 <Tag color={levelColor}>{localizeStatus(capacity.level)}</Tag></Space>}
          description={`生产启动${capacity.productionStartAllowed ? '允许' : '已阻止'} · 预览文件写入${capacity.artifactWritesAllowed ? '允许' : '已阻止'}`} />
        <div className="trace-stats">
          <Statistic title="可用空间" value={gb(capacity.availableFreeBytes)} suffix="GB" />
          <Statistic title="数据库" value={mb(capacity.databaseBytes)} suffix="MB" />
          <Statistic title="文件产物" value={gb(capacity.artifactBytes)} suffix="GB" />
          <Statistic title="备份" value={gb(capacity.backupBytes)} suffix="GB" />
        </div>
      </>}

      {schema && <Alert type={schema.upToDate ? 'success' : 'warning'} showIcon
        message={`SQLite 数据库架构 v${schema.currentVersion} / 目标 v${schema.targetVersion}`}
        description={`${schema.history.length} 条迁移记录 · ${schema.history.filter(x => x.baselined).length} 条来自 V0.28 之前安装的基线记录`} />}

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Typography.Title level={5} style={{ margin: 0 }}>本地备份</Typography.Title>
        <Space>
          <span>包含预览文件</span><Switch checked={includeArtifacts} onChange={setIncludeArtifacts} />
          <Button type="primary" loading={busy === 'create'} onClick={() => void createBackup()}>创建备份</Button>
          <Button onClick={() => void refresh()}>刷新</Button>
        </Space>
      </div>

      <Table size="small" rowKey="backupId" dataSource={backups} pagination={{ pageSize: 6 }} columns={[
        { title: '创建时间', dataIndex: 'createdAt', width: 180, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
        { title: '备份 ID', dataIndex: 'backupId', render: (v: string) => <Typography.Text code>{v}</Typography.Text> },
        { title: '架构版本', dataIndex: 'schemaVersion', width: 90, render: (v: number) => `v${v}` },
        { title: '备份大小', dataIndex: 'archiveBytes', width: 100, render: (v: number) => `${mb(v)} MB` },
        { title: '文件产物', width: 130, render: (_: unknown, row: StorageBackupDescriptor) => row.includesArtifacts ? `${row.artifactCount} 个 · ${mb(row.artifactBytes)} MB` : '仅元数据' },
        { title: '操作', width: 190, render: (_: unknown, row: StorageBackupDescriptor) => <Space>
          <Button size="small" danger loading={busy === `restore:${row.backupId}`} onClick={() => restore(row)}>还原</Button>
          <Popconfirm title="确定删除此备份吗？" okText="删除" cancelText="取消" onConfirm={() => void deleteBackup(row.backupId)}>
            <Button size="small" loading={busy === `delete:${row.backupId}`}>删除</Button>
          </Popconfirm>
        </Space> }
      ]} />

      <Typography.Text type="secondary">备份包含 SQLite 元数据和可选的追溯预览文件。插件二进制文件和外部控制器程序通过运行时依赖清单进行漂移校验，不会复制到备份中。</Typography.Text>
    </Space>
  </Modal>;
}
