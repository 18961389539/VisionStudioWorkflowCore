import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import PropertyPanel from '../components/PropertyPanel';
import type { NodeCatalogItem, VisionRoi } from '../types';

// 脚本编辑器的预览面板逻辑与 CodeMirror 无关：用 textarea 替身避免 jsdom 缺少编辑器的
// DOM 测量 API（ResizeObserver/布局），同时保留 value/onChange 契约。
vi.mock('@uiw/react-codemirror', () => ({
  default: ({ value, onChange, editable }: { value?: string; onChange?: (value: string) => void; editable?: boolean }) => (
    <textarea aria-label="脚本代码" readOnly={editable === false} value={value ?? ''}
      onChange={(event) => onChange?.(event.target.value)} />
  )
}));

const catalog: NodeCatalogItem = {
  type: 'image.crop', displayName: '图像裁剪', category: 'Preprocess', pluginId: 'builtin',
  inputs: [], outputs: [], parameters: []
};

beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it('displays omitted optional numeric parameters as empty instead of zero', () => {
  const node: Node = { id: 'resize', position: { x: 0, y: 0 }, data: { parameters: {} } };
  const resize: NodeCatalogItem = { ...catalog, type: 'image.resize', parameters: [
    { name: 'width', label: '输出宽度', type: 'number', defaultValue: null, optional: true, min: 1 }
  ] };
  const onChange = vi.fn();
  const view = render(<PropertyPanel node={node} catalogItem={resize} onChange={onChange} />);

  const field = screen.getByRole('spinbutton', { name: '输出宽度' }) as HTMLInputElement;
  expect(field.value).toBe('');
  expect(field.placeholder).toBe('留空自动');
  fireEvent.change(field, { target: { value: '320' } });
  expect(onChange).toHaveBeenCalledWith('width', 320);
  view.rerender(<PropertyPanel node={{ ...node, data: { parameters: { width: 320 } } }} catalogItem={resize} onChange={onChange} />);
  fireEvent.change(field, { target: { value: '' } });
  expect(onChange).toHaveBeenLastCalledWith('width', null);
});

describe('PropertyPanel parameter filtering', () => {
  const manyParameters: NodeCatalogItem = {
    ...catalog, type: 'test.many', parameters: [
      { name: 'exposure', label: '曝光时间', type: 'number', defaultValue: 10, group: '采集' },
      { name: 'gain', label: '增益', type: 'number', defaultValue: 1, group: '采集' },
      { name: 'trigger', label: '触发方式', type: 'select', defaultValue: 'soft', group: '采集',
        options: Array.from({ length: 10 }, (_, index) => ({ label: `模式 ${index + 1}`, value: `m${index + 1}` })) },
      { name: 'modelPath', label: '模型路径', type: 'text', defaultValue: '', group: '模型' },
      { name: 'threshold', label: '阈值', type: 'number', defaultValue: 128, group: '处理' },
      { name: 'blur', label: '模糊核', type: 'number', defaultValue: 3, group: '处理' },
      { name: 'invert', label: '反相', type: 'boolean', defaultValue: false, group: '处理' },
      { name: 'save', label: '保存结果', type: 'boolean', defaultValue: true, group: '输出' },
      { name: 'prefix', label: '文件名前缀', type: 'text', defaultValue: '', group: '输出' }
    ]
  };

  it('filters long parameter lists by label, group or description and reports the match count', () => {
    const node: Node = { id: 'many', position: { x: 0, y: 0 }, data: { parameters: {} } };
    render(<PropertyPanel node={node} catalogItem={manyParameters} onChange={vi.fn()} />);

    const filter = screen.getByRole('textbox', { name: '筛选参数' });
    expect(screen.getByText('共 9 个参数')).toBeTruthy();
    fireEvent.change(filter, { target: { value: '曝光' } });
    expect(screen.getByText('1/9 个参数')).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: '曝光时间' })).toBeTruthy();
    expect(screen.queryByRole('spinbutton', { name: '增益' })).toBeNull();

    fireEvent.change(filter, { target: { value: '处理' } });
    expect(screen.getByRole('spinbutton', { name: '阈值' })).toBeTruthy();
    expect(screen.getByRole('switch', { name: '反相' })).toBeTruthy();
    expect(screen.getByText('3/9 个参数')).toBeTruthy();

    fireEvent.change(filter, { target: { value: '不存在的参数' } });
    expect(screen.getByText('没有匹配“不存在的参数”的参数')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '清除筛选' }));
    expect(screen.getByText('共 9 个参数')).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: '增益' })).toBeTruthy();
  });

  it('does not add a filter box to short parameter lists', () => {
    const node: Node = { id: 'resize', position: { x: 0, y: 0 }, data: { parameters: {} } };
    const short: NodeCatalogItem = { ...catalog, type: 'test.short', parameters: [
      { name: 'width', label: '宽度', type: 'number', defaultValue: 10 },
      { name: 'height', label: '高度', type: 'number', defaultValue: 10 }
    ] };
    render(<PropertyPanel node={node} catalogItem={short} onChange={vi.fn()} />);
    expect(screen.queryByRole('textbox', { name: '筛选参数' })).toBeNull();
  });

  it('enables search for dropdowns with many options but not for short option lists', () => {
    const node: Node = { id: 'many', position: { x: 0, y: 0 }, data: { parameters: {} } };
    const { container, rerender } = render(<PropertyPanel node={node} catalogItem={manyParameters} onChange={vi.fn()} />);
    expect(container.querySelector('.ant-select-show-search')).not.toBeNull();

    const short: NodeCatalogItem = { ...catalog, type: 'test.short', parameters: [
      { name: 'mode', label: '模式', type: 'select', defaultValue: 'a', options: [
        { label: '甲', value: 'a' }, { label: '乙', value: 'b' }
      ] }
    ] };
    rerender(<PropertyPanel node={node} catalogItem={short} onChange={vi.fn()} />);
    expect(container.querySelector('.ant-select-show-search')).toBeNull();
  });
});

describe('PropertyPanel polygon ROI editing', () => {
  it('inserts a midpoint on the selected edge, including the closing edge', () => {
    const roi: VisionRoi = { type: 'Polygon', points: [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 }] };
    const node: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: 'image.crop', parameters: { roi } } };
    const onChange = vi.fn();
    const view = render(<PropertyPanel node={node} catalogItem={catalog} onChange={onChange} roiFields={[{ key: 'roi', label: '裁剪区域' }]} />);

    fireEvent.click(screen.getByRole('button', { name: '在点 1 后插入顶点' }));
    const inserted = onChange.mock.calls[0][1] as Extract<VisionRoi, { type: 'Polygon' }>;
    expect(inserted.points).toEqual([{ x: 0, y: 0 }, { x: 5, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 }]);

    const updatedNode: Node = { ...node, data: { ...node.data, parameters: { roi: inserted } } };
    view.rerender(<PropertyPanel node={updatedNode} catalogItem={catalog} onChange={onChange} roiFields={[{ key: 'roi', label: '裁剪区域' }]} />);
    fireEvent.click(screen.getByRole('button', { name: '在点 4 后插入顶点' }));
    const closingEdge = onChange.mock.calls[1][1] as Extract<VisionRoi, { type: 'Polygon' }>;
    expect(closingEdge.points).toHaveLength(5);
    expect(closingEdge.points[4]).toEqual({ x: 5, y: 5 });
  });

  // 120 顶点多边形的渲染 + 分页交互本身约 4.7s。开覆盖率（v8 插桩 + sourcemap 映射）后
  // 同一用例实测超过 15s——这是插桩开销而非逻辑退化，故给到 30s；断言未放宽。
  it('pages large polygons and keeps edits on the correct full-list vertex', { timeout: 30_000 }, () => {
    const points = Array.from({ length: 120 }, (_, index) => {
      const angle = 2 * Math.PI * index / 120;
      return { x: 100 + 50 * Math.cos(angle), y: 100 + 50 * Math.sin(angle) };
    });
    const roi: VisionRoi = { type: 'Polygon', points };
    const node: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: 'image.crop', parameters: { roi } } };
    const onChange = vi.fn();
    render(<PropertyPanel node={node} catalogItem={catalog} onChange={onChange} roiFields={[{ key: 'roi', label: '裁剪区域' }]} />);

    expect(screen.getByText(/当前编辑 1–50/)).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: '点 1 X' })).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: '点 50 X' })).toBeTruthy();
    expect(screen.queryByRole('spinbutton', { name: '点 51 X' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '下一组' }));
    expect(screen.getByRole('spinbutton', { name: '点 51 X' })).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: '点 100 X' })).toBeTruthy();
    expect(screen.queryByRole('spinbutton', { name: '点 101 X' })).toBeNull();

    fireEvent.change(screen.getByRole('spinbutton', { name: '顶点组' }), { target: { value: '3' } });
    expect(screen.getByRole('spinbutton', { name: '点 101 X' })).toBeTruthy();
    expect(screen.getByRole('spinbutton', { name: '点 120 Y' })).toBeTruthy();
    fireEvent.change(screen.getByRole('spinbutton', { name: '点 101 X' }), { target: { value: '888.5' } });
    const edited = onChange.mock.calls[0][1] as Extract<VisionRoi, { type: 'Polygon' }>;
    expect(edited.points[100].x).toBe(888.5);
    expect(edited.points[0]).toEqual(points[0]);

    fireEvent.click(screen.getByRole('button', { name: '删除点 101' }));

    const updated = onChange.mock.calls[1][1] as Extract<VisionRoi, { type: 'Polygon' }>;
    expect(updated.points).toHaveLength(119);
    expect(updated.points[100]).toEqual(points[101]);
  });
});

describe('PropertyPanel JSON fields', () => {
  it('renders JSON-named textareas with a format action', () => {
    const node: Node = { id: 'calibration', position: { x: 0, y: 0 }, data: { parameters: { samplesJson: '{"samples":[]}' } } };
    const calibration: NodeCatalogItem = { ...catalog, type: 'calibration.handEye2D', parameters: [
      { name: 'samplesJson', label: '位姿对 JSON', type: 'textarea', defaultValue: '{}', description: '位姿字段说明' }
    ] };
    render(<PropertyPanel node={node} catalogItem={calibration} onChange={vi.fn()} />);
    expect(screen.getByRole('button', { name: '格式化 JSON' })).toBeTruthy();
    expect(screen.getByRole('textbox', { name: '位姿对 JSON' })).toBeTruthy();
    expect(screen.getByText('位姿字段说明')).toBeTruthy();
  });
});

describe('PropertyPanel script editor preview', () => {
  it('runs the node preview and renders inputs, outputs, logs and budgets inline', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, status: 200, json: async () => ({ valid: true, diagnostics: [] }) })));
    const scriptCatalog: NodeCatalogItem = { ...catalog, type: 'script.csharp', parameters: [
      { name: 'source', label: '脚本源码', type: 'textarea', defaultValue: 'Output.Number = 2;' }
    ] };
    const node: Node = { id: 'script', position: { x: 0, y: 0 }, data: { parameters: { source: 'Output.Number = 2;' } } };
    const onRunPreview = vi.fn();
    render(<PropertyPanel node={node} catalogItem={scriptCatalog} onChange={vi.fn()} connectedInputPorts={['a']}
      onRunPreview={onRunPreview} previewBusy={false} scriptPreview={{
        success: true, durationMs: 12.5, receivedAt: '2026-10-27T09:00:00Z', stale: false, draft: true,
        summary: { compileMs: 8.25, executeMs: 1.5, ticks: 42, sourceHash: 'abcdef0123456789', outputImage: 'none', logs: ['info: ok'] },
        inputs: { a: { type: 'Double', display: '1.5' } },
        outputs: { number: { type: 'Double', display: '2' } }
      }} />);

    expect(await screen.findByRole('textbox', { name: '脚本代码' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '运行预览' }));
    expect(onRunPreview).toHaveBeenCalledTimes(1);

    const panel = screen.getByRole('region', { name: '脚本运行预览' });
    expect(within(panel).getByText('本次输入')).toBeTruthy();
    expect(within(panel).getByText('脚本输出')).toBeTruthy();
    expect(within(panel).getByText(/8.25 ms/)).toBeTruthy();
    expect(within(panel).getByText(/42/)).toBeTruthy();
    expect(within(panel).getByText(/abcdef012345/)).toBeTruthy();
    expect(within(panel).getByText('info: ok')).toBeTruthy();
    expect(within(panel).queryByText('图像')).toBeNull();
  });

  it('keeps the source and shows diagnostics when formatting finds syntax errors', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/scripts/format')) {
        return { ok: true, status: 200, json: async () => ({ valid: false, formatted: 'var broken = ;', diagnostics: [
          { line: 1, column: 1, severity: 'Error', code: 'CS1002', message: '; expected' }
        ] }) };
      }
      return { ok: true, status: 200, json: async () => ({ valid: true, diagnostics: [] }) };
    });
    vi.stubGlobal('fetch', fetchMock);
    const onChange = vi.fn();
    const scriptCatalog: NodeCatalogItem = { ...catalog, type: 'script.csharp', parameters: [
      { name: 'source', label: '脚本源码', type: 'textarea', defaultValue: 'var broken = ;' }
    ] };
    render(<PropertyPanel node={{ id: 'script', position: { x: 0, y: 0 }, data: { parameters: { source: 'var broken = ;' } } }}
      catalogItem={scriptCatalog} onChange={onChange} />);

    expect(await screen.findByRole('textbox', { name: '脚本代码' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '格式化' }));
    expect(await screen.findByText('脚本存在语法错误，已保留原内容。')).toBeTruthy();
    expect(screen.getByText('CS1002')).toBeTruthy();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('offers a jump action when a runtime error carries a mapped line', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, status: 200, json: async () => ({ valid: true, diagnostics: [] }) })));
    const scriptCatalog: NodeCatalogItem = { ...catalog, type: 'script.csharp', parameters: [
      { name: 'source', label: '脚本源码', type: 'textarea', defaultValue: 'Output.Number = 1;' }
    ] };
    render(<PropertyPanel node={{ id: 'script', position: { x: 0, y: 0 }, data: { parameters: { source: 'Output.Number = 1;' } } }}
      catalogItem={scriptCatalog} onChange={vi.fn()} scriptPreview={{
        success: false, error: 'C# script failed at line 7: boom', durationMs: 2, summary: {}
      }} />);

    expect(await screen.findByRole('textbox', { name: '脚本代码' })).toBeTruthy();
    expect(screen.getByRole('button', { name: /跳转到第 7 行/ })).toBeTruthy();
  });

  it('disables the preview action with the blocked reason from the dialog', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, status: 200, json: async () => ({ valid: true, diagnostics: [] }) })));
    const scriptCatalog: NodeCatalogItem = { ...catalog, type: 'script.csharp', parameters: [
      { name: 'source', label: '脚本源码', type: 'textarea', defaultValue: 'Output.Number = 2;' }
    ] };
    render(<PropertyPanel node={{ id: 'script', position: { x: 0, y: 0 }, data: { parameters: {} } }}
      catalogItem={scriptCatalog} onChange={vi.fn()} onRunPreview={vi.fn()}
      scriptPreviewBlockedReason="请先结束当前调试会话，再测试暂存参数。" />);

    expect(await screen.findByRole('textbox', { name: '脚本代码' })).toBeTruthy();
    expect((screen.getByRole('button', { name: '运行预览' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
