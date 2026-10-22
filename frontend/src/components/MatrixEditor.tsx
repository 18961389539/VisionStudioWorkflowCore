import { useState } from 'react';
import { Button, Input, InputNumber, Space } from 'antd';

type Props = { value: string; label: string; disabled?: boolean; onChange: (value: string) => void };

function readMatrix(value: string): number[][] | undefined {
  try {
    const data: unknown = JSON.parse(value);
    if (!Array.isArray(data) || data.length === 0 || !Array.isArray(data[0]) || data[0].length === 0) return;
    const columns = data[0].length;
    if (data.length * columns > 1_000_000 || !data.every(row => Array.isArray(row) && row.length === columns
      && row.every(cell => typeof cell === 'number' && Number.isFinite(cell)))) return;
    return data as number[][];
  } catch { return; }
}

/** Keep the persisted JSON contract while providing a bounded table for small matrices. */
export default function MatrixEditor({ value, label, disabled, onChange }: Props) {
  const [showJson, setShowJson] = useState(false);
  const matrix = readMatrix(value);
  const tableFits = matrix && matrix.length <= 20 && matrix[0].length <= 20;
  const resize = (rows: number, columns: number) => {
    if (!matrix || rows < 1 || columns < 1 || rows > 20 || columns > 20) return;
    onChange(JSON.stringify(Array.from({ length: rows }, (_, r) => Array.from({ length: columns }, (_, c) => matrix[r]?.[c] ?? 0))));
  };
  return <div style={{ width: '100%', minWidth: 0 }}>
    <Space wrap size="small" style={{ marginBottom: 6 }}>
      <Button size="small" disabled={disabled || !tableFits} onClick={() => setShowJson(!showJson)}>
        {showJson || !tableFits ? '表格编辑' : 'JSON 编辑'}
      </Button>
      {matrix && <span>{matrix.length} 行 × {matrix[0].length} 列</span>}
    </Space>
    {tableFits && !showJson ? <>
      <Space wrap size="small" style={{ marginBottom: 6 }}>
        <span>行</span><InputNumber size="small" aria-label={`${label}行数`} disabled={disabled} min={1} max={20} precision={0}
          value={matrix.length} onChange={rows => { if (rows !== null) resize(rows, matrix[0].length); }} />
        <span>列</span><InputNumber size="small" aria-label={`${label}列数`} disabled={disabled} min={1} max={20} precision={0}
          value={matrix[0].length} onChange={columns => { if (columns !== null) resize(matrix.length, columns); }} />
      </Space>
      <div style={{ overflowX: 'auto', maxHeight: 320 }}>
        <table aria-label={`${label}数值表`} style={{ borderSpacing: 3 }}><tbody>{matrix.map((row, r) =>
          <tr key={r}>{row.map((cell, c) => <td key={c}><InputNumber size="small" style={{ width: 100 }}
            aria-label={`${label}第${r + 1}行第${c + 1}列`} disabled={disabled} value={cell}
            onChange={next => {
              if (next === null || !Number.isFinite(next)) return;
              onChange(JSON.stringify(matrix.map((values, ri) => ri === r ? values.map((v, ci) => ci === c ? next : v) : values)));
            }} /></td>)}</tr>)}</tbody></table>
      </div>
    </> : <>
      <Input.TextArea aria-label={`${label} JSON`} disabled={disabled} value={value} spellCheck={false}
        autoSize={{ minRows: 3, maxRows: 10 }} status={matrix ? undefined : 'error'} onChange={e => onChange(e.target.value)} />
      <div style={{ fontSize: 12, marginTop: 4, color: matrix ? undefined : '#ff4d4f' }}>
        {matrix ? '超过 20 行或 20 列时使用 JSON 编辑。' : '需要非空、等宽且仅含有限数字的二维数组，例如 [[1,0],[0,1]]。'}
      </div>
    </>}
  </div>;
}
