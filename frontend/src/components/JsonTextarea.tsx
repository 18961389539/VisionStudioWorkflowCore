import { useState } from 'react';
import { Button, Input } from 'antd';

type Props = {
  label: string;
  value: string;
  description?: string;
  example?: string;
  disabled?: boolean;
  status?: 'error' | 'warning';
  onChange: (value: string) => void;
};

/** 标定/样本等原始 JSON 字段：保留手工编辑，并提供格式化与解析错误反馈。 */
export default function JsonTextarea({ label, value, description, example, disabled = false, status, onChange }: Props) {
  const [error, setError] = useState('');
  const format = () => {
    try {
      const parsed = JSON.parse(value);
      onChange(JSON.stringify(parsed, null, 2));
      setError('');
    } catch (exception) {
      setError(`JSON 无效：${exception instanceof Error ? exception.message : '无法解析'}`);
    }
  };
  return <div className="json-textarea">
    <Input.TextArea
      size="small"
      aria-label={label}
      status={status}
      disabled={disabled}
      autoSize={{ minRows: 3, maxRows: 12 }}
      value={value}
      onChange={(event) => { setError(''); onChange(event.target.value); }}
    />
    <div className="json-textarea-tools">
      <Button size="small" type="link" disabled={disabled} onClick={format}>格式化 JSON</Button>
      {example && ['', '{}', '[]'].includes(value.trim()) && <Button size="small" type="link" disabled={disabled}
        title="插入示例后请替换为现场数据" onClick={() => { setError(''); onChange(example); }}>插入示例</Button>}
      {error ? <span className="json-textarea-error" role="alert">{error}</span>
        : <span className="json-textarea-hint">JSON</span>}
    </div>
    {description && <p className="json-textarea-description">{description}</p>}
  </div>;
}
