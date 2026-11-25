/**
 * 「打开已保存的工作流」选择弹窗——受控展示组件。
 * 列表数据、选中态与加载/删除动作由宿主（App.tsx）提供；从 App.tsx 拆出（巨型组件拆分，2026-10-23）。
 */
import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Input, Modal, Popconfirm, Space, Table, Typography } from 'antd';
import type { WorkflowDocumentSummary } from '../appEditorShared';
import { useI18n } from '../i18n/provider';

type Props = {
  open: boolean;
  onClose: () => void;
  /** 已保存工作流列表（宿主在打开时拉取） */
  workflows: WorkflowDocumentSummary[];
  /** 列表加载中 */
  loading: boolean;
  /** 列表加载失败的错误信息 */
  error?: string;
  /** 当前选中的工作流 ID */
  selected?: string;
  onSelect: (id: string | undefined) => void;
  /** 主按钮「加载选中流程」点击（双击行时传入该行 ID） */
  onLoad: (id?: string) => void;
  /** 删除已保存流程 */
  onDelete?: (id: string) => Promise<boolean> | void;
  /** 当前正在删除的流程 ID */
  deletingId?: string;
  /** 从本地 JSON 文件导入 */
  onImportJson?: () => void;
  /** 选中流程正在加载（主按钮 loading 态） */
  applying?: boolean;
};

export default function WorkflowPickerModal({
  open, onClose, workflows, loading, error, selected, onSelect, onLoad, onDelete, deletingId, onImportJson, applying = false
}: Props) {
  const { lang, dateLocale } = useI18n();
  const text = (zh: string, en: string) => (lang === 'en' ? en : zh);
  const [query, setQuery] = useState('');

  useEffect(() => {
    if (open) setQuery('');
  }, [open]);

  const filtered = useMemo(() => {
    const keyword = query.trim().toLowerCase();
    if (!keyword) return workflows;
    return workflows.filter((item) => item.name.toLowerCase().includes(keyword) || item.id.toLowerCase().includes(keyword));
  }, [query, workflows]);

  const busy = applying || Boolean(deletingId);

  return (
    <Modal
      open={open}
      title={text('打开已保存的工作流', 'Open saved workflow')}
      width="min(840px, calc(100vw - 32px))"
      onCancel={onClose}
      footer={(
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
          <div>
            {onImportJson && <Button disabled={busy} onClick={() => { onClose(); onImportJson(); }}>{text('导入 JSON…', 'Import JSON…')}</Button>}
          </div>
          <Space>
            <Button onClick={onClose}>{text('取消', 'Cancel')}</Button>
            <Button type="primary" loading={applying} disabled={!selected || loading || Boolean(deletingId)} onClick={() => onLoad()}>{text('加载选中流程', 'Load workflow')}</Button>
          </Space>
        </div>
      )}
    >
      {error && <Alert type="error" showIcon title={text('工作流列表加载失败', 'Failed to load workflow list')} description={error} style={{ marginBottom: 12 }} />}
      <Input
        allowClear
        value={query}
        onChange={(event) => setQuery(event.target.value)}
        aria-label="搜索已保存工作流"
        placeholder={text('按工作流名称或 ID 搜索…', 'Search by workflow name or ID…')}
        style={{ marginBottom: 10 }}
      />
      <Table<WorkflowDocumentSummary>
        rowKey="id"
        size="small"
        loading={loading}
        dataSource={filtered}
        pagination={{ pageSize: 7, showSizeChanger: false }}
        rowSelection={{
          type: 'radio',
          selectedRowKeys: selected ? [selected] : [],
          onChange: (keys) => onSelect(keys[0] == null ? undefined : String(keys[0]))
        }}
        onRow={(item) => ({
          onDoubleClick: () => {
            if (loading || busy) return;
            onSelect(item.id);
            onLoad(item.id);
          }
        })}
        locale={{
          emptyText: loading
            ? '正在读取列表…'
            : workflows.length > 0
              ? '没有匹配的工作流，请调整搜索关键词。'
              : '还没有已保存的工作流。先保存当前流程后可从这里打开。'
        }}
        columns={[
          { title: '名称', dataIndex: 'name', ellipsis: true },
          { title: 'ID', dataIndex: 'id', width: 170, render: (value: string) => <Typography.Text code>{value}</Typography.Text> },
          {
            title: '节点 / 连线',
            width: 112,
            render: (_, item) => `${item.nodeCount} / ${item.edgeCount}`
          },
          {
            title: '更新时间',
            dataIndex: 'updatedAt',
            width: 175,
            sorter: (a, b) => new Date(a.updatedAt).getTime() - new Date(b.updatedAt).getTime(),
            defaultSortOrder: 'descend',
            render: (value: string) => new Date(value).toLocaleString(dateLocale)
          },
          ...(onDelete ? [{
            title: '操作',
            key: 'actions',
            width: 76,
            render: (_: unknown, item: WorkflowDocumentSummary) => (
              <span onClick={(event) => event.stopPropagation()}>
                <Popconfirm
                  title={`删除工作流「${item.name}」？`}
                  description={`将永久删除后端文件 ${item.id}.json，操作不可撤销。`}
                  okText="确认删除"
                  cancelText="取消"
                  okButtonProps={{ danger: true }}
                  onConfirm={() => void onDelete(item.id)}
                >
                  <Button size="small" type="link" danger loading={deletingId === item.id} disabled={busy && deletingId !== item.id}>删除</Button>
                </Popconfirm>
              </span>
            )
          }] : [])
        ]}
      />
    </Modal>
  );
}
