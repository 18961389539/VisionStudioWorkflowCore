import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import PropertyPanel from '../components/PropertyPanel';
import type { NodeCatalogItem, VisionRoi } from '../types';

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

  // 120 顶点多边形的渲染 + 分页交互本身约 4.7s，5s 默认期限在负载下会偶发超时。
  it('pages large polygons and keeps edits on the correct full-list vertex', { timeout: 15_000 }, () => {
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
