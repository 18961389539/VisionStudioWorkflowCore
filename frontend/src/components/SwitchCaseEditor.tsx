import { Button, Input, Popconfirm, Space, Tag } from 'antd';
import { useI18n } from '../i18n/provider';
import { readEditableSwitchCases, SWITCH_CASE_PORTS } from '../switchPorts';

type Props = {
  value: unknown;
  connections?: ReadonlyMap<string, { affectedLabels: readonly string[]; mergeResolved: boolean }>;
  disabled?: boolean;
  onChange: (value: string) => void;
};

type Branch = { label: string; port: string };

function editableJson(value: unknown): string {
  return typeof value === 'string' ? value : value === undefined ? '' : JSON.stringify(value);
}

export default function SwitchCaseEditor({ value, connections, disabled = false, onChange }: Props) {
  const { lang } = useI18n();
  const zh = lang !== 'en';
  const text = (cn: string, en: string) => zh ? cn : en;
  const { cases: branches, legacy } = readEditableSwitchCases(value);

  if (!branches) {
    return <div className="switch-case-editor">
      <div className="switch-case-heading">
        <strong>{text('分支列表', 'Switch cases')}</strong>
        <Tag color="red">{text('分支配置无效', 'Invalid case configuration')}</Tag>
      </div>
      <p className="roi-polygon-note">{text('可直接修正分支 JSON。设有固定端口的分支须包含 label 和唯一的 port（case1 到 case16）。', 'Repair the case JSON here. Cases with fixed ports require a label and a unique port from case1 to case16.')}</p>
      <Input.TextArea size="small" aria-label={text('分支 JSON', 'Cases JSON')} disabled={disabled}
        autoSize={{ minRows: 3, maxRows: 10 }} value={editableJson(value)} onChange={(event) => onChange(event.target.value)} />
      {!connections?.size && <div className="switch-case-actions">
        <Button size="small" disabled={disabled} onClick={() => onChange('["OK","NG"]')}>{text('恢复默认分支', 'Restore defaults')}</Button>
      </div>}
    </div>;
  }

  const committed = (next: Branch[]) => {
    const usesLegacyOrder = legacy && next.every(({ port }, index) => port === SWITCH_CASE_PORTS[index]);
    onChange(JSON.stringify(usesLegacyOrder ? next.map(({ label }) => label) : next));
  };
  const labels = branches.map(({ label }) => label);
  const ports = branches.map(({ port }) => port);
  const duplicate = new Set(labels.filter((label, index) => labels.indexOf(label) !== index));
  const hasEmpty = labels.some((label) => !label.trim());
  const availablePort = SWITCH_CASE_PORTS.find((port) => !ports.includes(port) && !connections?.has(port));
  const removedConnections = [...(connections?.keys() ?? [])].filter((port) => !ports.includes(port));
  const addCase = () => {
    if (!availablePort) return;
    let index = labels.length + 1;
    let label: string;
    do { label = text(`分支 ${index}`, `Case ${index}`); index += 1; } while (labels.includes(label));
    committed([...branches, { label, port: availablePort }]);
  };
  const moveCase = (from: number, to: number) => {
    const next = [...branches];
    [next[from], next[to]] = [next[to], next[from]];
    committed(next);
  };
  return <div className="switch-case-editor">
    <div className="switch-case-heading">
      <strong>{text('分支列表', 'Switch cases')}</strong>
      <span className="roi-polygon-note">{branches.length}/{SWITCH_CASE_PORTS.length} · {text('每个分支的端口固定，default 处理未匹配值', 'Each case keeps its port. Default handles unmatched values.')}</span>
    </div>
    {branches.map(({ label, port }, index) => {
      const invalid = !label.trim() || duplicate.has(label);
      const connection = connections?.get(port);
      const removeCase = () => committed(branches.filter((branch) => branch.port !== port));
      const removeDisabled = disabled || branches.length <= 1;
      const removeLabel = text(`删除分支 ${index + 1}`, `Remove case ${index + 1}`);
      return <div className="switch-case-row" key={port}>
        <span className="switch-case-index">{port}</span>
        <Input size="small" aria-label={text(`分支 ${index + 1} 标签`, `Case ${index + 1} label`)}
          status={invalid ? 'error' : undefined} disabled={disabled} value={label}
          placeholder={text('分支标签', 'Case label')}
          onChange={(event) => committed(branches.map((branch) => branch.port === port ? { ...branch, label: event.target.value } : branch))} />
        <Space size={0}>
          <Button size="small" type="text" disabled={disabled || index === 0} aria-label={text(`上移分支 ${index + 1}`, `Move case ${index + 1} up`)}
            onClick={() => moveCase(index, index - 1)}>↑</Button>
          <Button size="small" type="text" disabled={disabled || index === branches.length - 1} aria-label={text(`下移分支 ${index + 1}`, `Move case ${index + 1} down`)}
            onClick={() => moveCase(index, index + 1)}>↓</Button>
          {connection ? <Popconfirm disabled={removeDisabled}
            title={text(`删除分支「${label}」？`, `Remove case “${label}”?`)}
            description={<>
              <p>{text('应用后将断开以下连接', 'Applying removes the following connections')}</p>
              <ul>{connection.affectedLabels.map((target) => <li key={target}>{target}</li>)}</ul>
              {!connection.mergeResolved && <p>{text('请手动检查此分支的汇合连线。', 'Check this case’s merge connections manually.')}</p>}
              <p>{text('分支内的节点仍在画布上，请重新接线或移除闲置节点。', 'Case nodes remain on the canvas. Reconnect or remove any unused nodes.')}</p>
            </>}
            okText={text('确认删除', 'Remove case')} cancelText={text('取消', 'Cancel')} okButtonProps={{ danger: true }} onConfirm={removeCase}>
            <Button size="small" type="text" danger disabled={removeDisabled} aria-label={removeLabel}>×</Button>
          </Popconfirm> : <Button size="small" type="text" danger disabled={removeDisabled} aria-label={removeLabel} onClick={removeCase}>×</Button>}
        </Space>
      </div>;
    })}
    {(hasEmpty || duplicate.size > 0) && <p className="switch-case-error" role="alert">
      {hasEmpty ? text('分支标签不能为空。', 'Case labels cannot be empty.') : ''}
      {duplicate.size > 0 ? text(`分支标签必须唯一：${[...duplicate].join('、')}。`, `Case labels must be unique: ${[...duplicate].join(', ')}.`) : ''}
    </p>}
    <div className="switch-case-actions">
      <Button size="small" type="dashed" disabled={disabled || !availablePort}
        onClick={addCase}>
        {text('添加分支', 'Add case')}
      </Button>
      {removedConnections.length > 0 && <span className="roi-polygon-note" role="status">
        {text(`应用后将断开已删除分支 ${removedConnections.join('、')} 的连接。`, `Applying disconnects the removed cases ${removedConnections.join(', ')}.`)}
      </span>}
    </div>
  </div>;
}
