import { memo } from 'react';
import { Alert, Button } from 'antd';
import { useEditorLayout, useEditorRun } from '../editorContexts';
import { useI18n } from '../i18n/provider';

export default memo(function CanvasDraftRecovery() {
  const { lang } = useI18n();
  const text = (zh: string, en: string) => lang === 'en' ? en : zh;
  const { locale } = useEditorLayout();
  const { draftNotice, restoreDraft, discardDraft } = useEditorRun();
  if (!draftNotice) return null;
  return <div className="draft-recovery" style={{ position: 'absolute', top: 10, left: '50%', transform: 'translateX(-50%)', zIndex: 50, width: 'min(640px, calc(100% - 24px))' }}>
    <Alert type="warning" showIcon title={text('检测到未保存的本地草稿', 'Unsaved local draft detected')}
      description={`${draftNotice.workflowName || draftNotice.workflowId || text('未命名工作流', 'Untitled workflow')} · ${text('保存于', 'Saved')} ${new Date(draftNotice.savedAt).toLocaleString(locale)}`}
      action={<div style={{ display: 'flex', gap: 8 }}>
        <Button size="small" type="primary" onClick={restoreDraft}>{text('恢复草稿', 'Restore')}</Button>
        <Button size="small" onClick={discardDraft}>{text('丢弃', 'Discard')}</Button>
      </div>} />
  </div>;
});
