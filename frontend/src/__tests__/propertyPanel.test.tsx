import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { Node } from '@xyflow/react';
import PropertyPanel from '../components/PropertyPanel';
import type { NodeCatalogItem, VisionRoi } from '../types';

const catalog: NodeCatalogItem = {
  type: 'image.crop', displayName: '图像裁剪', category: 'Preprocess', pluginId: 'builtin',
  inputs: [], outputs: [], parameters: []
};

afterEach(cleanup);

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

  it('pages large polygons and keeps edits on the correct full-list vertex', () => {
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
