import { Button, Input, Tag } from 'antd';
import { useI18n } from '../i18n/provider';

type Props = {
  value: unknown;
  disabled?: boolean;
  onChange: (value: string) => void;
};

function parseEditable(value: unknown): { labels?: string[]; raw: string } {
  const raw = typeof value === 'string' ? value : value === undefined || value === null ? '' : JSON.stringify(value);
  try {
    const parsed = typeof value === 'string' ? JSON.parse(value) : value;
    if (Array.isArray(parsed) && parsed.every((label) => typeof label === 'string')) return { labels: parsed, raw };
  } catch { /* keep raw JSON for repair below */ }
  return { raw };
}

export default function SwitchCaseEditor({ value, disabled = false, onChange }: Props) {
  const { lang } = useI18n();
  const zh = lang !== 'en';
  const text = (cn: string, en: string) => zh ? cn : en;
  const { labels, raw } = parseEditable(value);
  const committed = (next: string[]) => onChange(JSON.stringify(next));

  if (!labels) {
    return <div className="switch-case-editor">
      <div className="switch-case-heading">
        <strong>{text('分支列表', 'Switch cases')}</strong>
        <Tag color="red">{text('JSON 无法解析', 'Invalid JSON')}</Tag>
      </div>
      <p className="roi-polygon-note">{text('当前 casesJson 不是字符串数组。可直接修正，或恢复为默认两个分支。', 'casesJson is not a string array. Repair it or restore the default two cases.')}</p>
      <Input.TextArea size="small" aria-label={text('分支 JSON', 'Cases JSON')} disabled={disabled}
        autoSize={{ minRows: 3, maxRows: 10 }} value={raw} onChange={(event) => onChange(event.target.value)} />
      <div className="switch-case-actions">
        <Button size="small" disabled={disabled} onClick={() => committed(['OK', 'NG'])}>{text('恢复默认分支', 'Restore defaults')}</Button>
      </div>
    </div>;
  }

  const duplicate = new Set(labels.filter((label, index) => labels.indexOf(label) !== index));
  const hasEmpty = labels.some((label) => !label.trim());
  const canAdd = labels.length < 16;
  const addCase = () => {
    let index = labels.length + 1;
    let label: string;
    do { label = text(`分支 ${index}`, `Case ${index}`); index += 1; } while (labels.includes(label));
    committed([...labels, label]);
  };
  return <div className="switch-case-editor">
    <div className="switch-case-heading">
      <strong>{text('分支列表', 'Switch cases')}</strong>
      <span className="roi-polygon-note">{labels.length}/16 · {text('按顺序对应 case1..caseN，default 处理未匹配值', 'Maps in order to case1..caseN; default handles unmatched values')}</span>
    </div>
    {labels.map((label, index) => {
      const invalid = !label.trim() || duplicate.has(label);
      return <div className="switch-case-row" key={index}>
        <span className="switch-case-index">case{index + 1}</span>
        <Input size="small" aria-label={text(`分支 ${index + 1} 标签`, `Case ${index + 1} label`)}
          status={invalid ? 'error' : undefined} disabled={disabled} value={label}
          placeholder={text('分支标签', 'Case label')}
          onChange={(event) => committed(labels.map((current, currentIndex) => currentIndex === index ? event.target.value : current))} />
        <Button size="small" type="text" danger disabled={disabled || labels.length <= 1}
          aria-label={text(`删除分支 ${index + 1}`, `Remove case ${index + 1}`)}
          onClick={() => committed(labels.filter((_, currentIndex) => currentIndex !== index))}>×</Button>
      </div>;
    })}
    {(hasEmpty || duplicate.size > 0) && <p className="switch-case-error" role="alert">
      {hasEmpty ? text('分支标签不能为空。', 'Case labels cannot be empty.') : ''}
      {duplicate.size > 0 ? text(`分支标签必须唯一：${[...duplicate].join('、')}。`, `Case labels must be unique: ${[...duplicate].join(', ')}.`) : ''}
    </p>}
    <div className="switch-case-actions">
      <Button size="small" type="dashed" disabled={disabled || !canAdd}
        onClick={addCase}>
        {text('添加分支', 'Add case')}
      </Button>
      <span className="roi-polygon-note">{text('删除中间项会改变后续 caseN 的含义，请检查连线。', 'Removing a middle case shifts later caseN meanings; recheck the connections.')}</span>
    </div>
  </div>;
}
