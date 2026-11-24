import { useState } from 'react';
import { Alert, Button, Checkbox, Input, message, Modal, Select, Space } from 'antd';
import type { ProductionJobItem } from '../useProductionMonitor';
import { useProductionActions } from '../useProductionActions';
import { production } from '../endpoints';
import { isLayout, useOperatorLayout, widgetLabels, type Layout, type Widget } from './OperatorDashboard';

export default function OperatorLayoutDialog({ jobs, initialJobId, onClose, onSaved }: {
  jobs: ProductionJobItem[];
  initialJobId?: string;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [jobId, setJobId] = useState(initialJobId ?? jobs[0]?.id);
  const [saving, setSaving] = useState(false);
  return <Modal open title="配置操作员界面" footer={null} onCancel={onClose} closable={!saving}
    keyboard={!saving} mask={{ closable: !saving }} width={570}>
    <Space orientation="vertical" size="middle" className="operator-layout-form">
      <label htmlFor="operator-layout-job">配方</label>
      <Select id="operator-layout-job" aria-label="布局配方" value={jobId} disabled={saving} onChange={setJobId}
        options={jobs.map((job) => ({ value: job.id, label: job.name }))} />
      {jobId && <OperatorLayoutEditor key={jobId} jobId={jobId} onBusy={setSaving} onSaved={() => { onSaved(); onClose(); }} />}
    </Space>
  </Modal>;
}

function OperatorLayoutEditor({ jobId, onBusy, onSaved }: { jobId: string; onBusy: (busy: boolean) => void; onSaved: () => void }) {
  const { snapshot, error, reload } = useOperatorLayout(jobId);
  if (error) return <Alert type="error" showIcon title={error} action={<Button onClick={reload}>重新加载</Button>} />;
  if (!snapshot) return <span role="status">正在读取布局…</span>;
  return <OperatorLayoutFields key={snapshot.revision} jobId={jobId} snapshot={snapshot} reload={reload} onBusy={onBusy} onSaved={onSaved} />;
}

function OperatorLayoutFields({ jobId, snapshot, reload, onBusy, onSaved }: {
  jobId: string;
  snapshot: { layout: Layout; revision: string };
  reload: () => void;
  onBusy: (busy: boolean) => void;
  onSaved: () => void;
}) {
  const [draft, setDraft] = useState(snapshot.layout);
  const [messages, holder] = message.useMessage();
  const { busy, error, call } = useProductionActions(true, messages, () => undefined);
  const saving = busy.has('save-layout');
  const changed = JSON.stringify(draft) !== JSON.stringify(snapshot.layout);

  const move = (index: number, offset: number) => {
    const widgets = [...draft.widgets];
    [widgets[index], widgets[index + offset]] = [widgets[index + offset]!, widgets[index]!];
    setDraft({ ...draft, widgets });
  };
  const save = async () => {
    if (!changed || saving || draft.widgets.length === 0) return;
    onBusy(true);
    try {
      const result = await call({
        url: production.operatorLayout(jobId), action: 'save-layout', method: 'PUT', body: draft,
        headers: { 'If-Match': snapshot.revision }, successMessage: '操作员布局已保存',
        onSuccess: (data, response) => {
          if (!isLayout(data) || !response.headers.get('ETag')) throw new Error('布局保存响应无效');
        },
      });
      if (result.ok) onSaved();
    } finally { onBusy(false); }
  };
  return <>
    {holder}
    <label htmlFor="operator-layout-title">面板标题</label>
    <Input id="operator-layout-title" maxLength={60} disabled={saving} value={draft.title ?? ''}
      placeholder="操作员工作台" onChange={(event) => setDraft({ ...draft, title: event.target.value || null })} />
    <fieldset className="operator-layout-widgets" disabled={saving}>
      <legend>显示组件</legend>
      <Checkbox.Group<Widget> value={draft.widgets}
        options={Object.entries(widgetLabels).map(([value, label]) => ({ value: value as Widget, label }))}
        onChange={(next) => setDraft({ ...draft, widgets: [...draft.widgets.filter((widget) => next.includes(widget)), ...next.filter((widget) => !draft.widgets.includes(widget))] })} />
    </fieldset>
    <ol className="operator-layout-order" aria-label="显示顺序">
      {draft.widgets.map((widget, index) => <li key={widget}>
        <span>{widgetLabels[widget]}</span>
        <Space>
          <Button size="small" aria-label={'上移' + widgetLabels[widget]} disabled={saving || index === 0} onClick={() => move(index, -1)}>上移</Button>
          <Button size="small" aria-label={'下移' + widgetLabels[widget]} disabled={saving || index === draft.widgets.length - 1} onClick={() => move(index, 1)}>下移</Button>
        </Space>
      </li>)}
    </ol>
    {error && <Alert type="error" showIcon title={error}
      action={<Button size="small" onClick={reload} disabled={saving}>放弃草稿并重载</Button>} />}
    <Button type="primary" disabled={!changed || draft.widgets.length === 0} loading={saving} onClick={() => void save()}>保存布局</Button>
  </>;
}
