import { useEffect, useState } from 'react';
import { Alert, Button, Input, Modal, Space, Typography } from 'antd';
import { isValidWorkflowId, suggestWorkflowId } from '../workflowDocument';
import { useI18n } from '../i18n/provider';

type Props = {
  open: boolean;
  currentId: string;
  currentName: string;
  busy?: boolean;
  onClose: () => void;
  onConfirm: (id: string, name: string) => void;
};

export default function SaveAsWorkflowModal({ open, currentId, currentName, busy = false, onClose, onConfirm }: Props) {
  const { lang } = useI18n();
  const text = (zh: string, en: string) => (lang === 'en' ? en : zh);
  const [name, setName] = useState('');
  const [id, setId] = useState('');
  const [idTouched, setIdTouched] = useState(false);

  useEffect(() => {
    if (!open) return;
    const baseName = currentName.trim() || '未命名工作流';
    const nextName = baseName.endsWith('副本') ? baseName : `${baseName} 副本`;
    setName(nextName);
    setId(suggestWorkflowId(`${currentId}-copy`, 'workflow'));
    setIdTouched(false);
  }, [open, currentId, currentName]);

  const trimmedName = name.trim();
  const trimmedId = id.trim();
  const idValid = isValidWorkflowId(trimmedId);
  const canSubmit = Boolean(trimmedName) && idValid && !busy;

  const submit = () => {
    if (!canSubmit) return;
    onConfirm(trimmedId, trimmedName);
  };

  return (
    <Modal
      open={open}
      title={text('工作流另存为', 'Save workflow as')}
      width={460}
      onCancel={() => { if (!busy) onClose(); }}
      closable={!busy}
      maskClosable={!busy}
      destroyOnHidden
      footer={(
        <Space>
          <Button disabled={busy} onClick={onClose}>{text('取消', 'Cancel')}</Button>
          <Button type="primary" loading={busy} disabled={!canSubmit} onClick={submit}>{text('保存为新工作流', 'Save as new workflow')}</Button>
        </Space>
      )}
    >
      <div style={{ display: 'grid', gap: 12 }}>
        <div>
          <label htmlFor="save-as-workflow-name" style={{ display: 'block', marginBottom: 4, fontSize: 12 }}>工作流名称</label>
          <Input
            id="save-as-workflow-name"
            value={name}
            disabled={busy}
            autoFocus
            placeholder="例如：产线 A 尺寸检测"
            onChange={(event) => {
              const nextName = event.target.value;
              setName(nextName);
              if (!idTouched) {
                const candidate = suggestWorkflowId(nextName, 'workflow');
                if (candidate && candidate !== currentId) setId(candidate);
              }
            }}
            onPressEnter={submit}
          />
        </div>
        <div>
          <label htmlFor="save-as-workflow-id" style={{ display: 'block', marginBottom: 4, fontSize: 12 }}>
            工作流 ID <Typography.Text type="secondary">（仅支持字母、数字、连字符和下划线）</Typography.Text>
          </label>
          <Input
            id="save-as-workflow-id"
            value={id}
            disabled={busy}
            status={trimmedId && !idValid ? 'error' : undefined}
            placeholder="例如：line-a-measurement"
            onChange={(event) => {
              setIdTouched(true);
              setId(event.target.value);
            }}
            onPressEnter={submit}
          />
          {trimmedId && !idValid && (
            <div style={{ color: 'var(--vs-text-danger)', fontSize: 12, marginTop: 4 }}>
              工作流 ID 只能包含字母、数字、连字符（-）和下划线（_），长度 1–128。
            </div>
          )}
        </div>
        {trimmedId === currentId && (
          <Alert
            type="info"
            showIcon
            title="与当前工作流 ID 相同"
            description="使用相同 ID 将直接保存到当前文档；如需保留原流程并创建副本，请修改 ID。"
          />
        )}
      </div>
    </Modal>
  );
}
