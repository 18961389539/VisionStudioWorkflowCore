/**
 * 「打开已保存的工作流」选择弹窗——受控展示组件。
 * 列表数据、选中态与加载动作由宿主（App.tsx）提供；从 App.tsx 拆出（巨型组件拆分，2026-10-23）。
 */
import { Alert, Button, Modal, Space, Table, Typography } from 'antd';
import type { WorkflowDocumentSummary } from '../appEditorShared';

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
  /** 主按钮「加载选中流程」点击 */
  onLoad: () => void;
  /** 选中流程正在加载（主按钮 loading 态） */
  applying?: boolean;
};

export default function WorkflowPickerModal({ open, onClose, workflows, loading, error, selected, onSelect, onLoad, applying = false }: Props) {
  return (
    <Modal
      open={open}
      title="打开已保存的工作流"
      width={760}
      onCancel={onClose}
      footer={(
        <Space>
          <Button onClick={onClose}>取消</Button>
          <Button type="primary" loading={applying} disabled={!selected || loading} onClick={onLoad}>加载选中流程</Button>
        </Space>
      )}
    >
      {error && <Alert type="error" showIcon message="工作流列表加载失败" description={error} style={{ marginBottom: 12 }} />}
      <Table<WorkflowDocumentSummary>
        rowKey="id"
        size="small"
        loading={loading}
        dataSource={workflows}
        pagination={{ pageSize: 7, showSizeChanger: false }}
        rowSelection={{
          type: 'radio',
          selectedRowKeys: selected ? [selected] : [],
          onChange: (keys) => onSelect(keys[0] == null ? undefined : String(keys[0]))
        }}
        locale={{ emptyText: loading ? '正在读取列表…' : '还没有已保存的工作流。先保存当前流程后可从这里打开。' }}
        columns={[
          { title: '名称', dataIndex: 'name', ellipsis: true },
          { title: 'ID', dataIndex: 'id', width: 170, render: (value: string) => <Typography.Text code>{value}</Typography.Text> },
          { title: '节点 / 连线', width: 112, render: (_, item) => `${item.nodeCount} / ${item.edgeCount}` },
          { title: '更新时间', dataIndex: 'updatedAt', width: 175, render: (value: string) => new Date(value).toLocaleString() }
        ]}
      />
    </Modal>
  );
}
