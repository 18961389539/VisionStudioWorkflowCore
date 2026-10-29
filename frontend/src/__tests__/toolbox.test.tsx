import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import Toolbox from '../components/Toolbox';
import type { NodeCatalogItem } from '../types';

const catalog: NodeCatalogItem[] = [
  { type: 'image.synthetic', displayName: '生成测试图像', category: '图像采集', inputs: [], outputs: [], parameters: [], description: '生成测试图，用于验证流程。支持配置尺寸。' },
  { type: 'image.threshold', displayName: '图像阈值分割', category: '图像处理', inputs: [], outputs: [], parameters: [], description: '按阈值提取明亮区域。支持反向分割。' },
  { type: 'measure.blobs', displayName: '斑点分析', category: '测量', inputs: [], outputs: [], parameters: [], description: '检测连通目标并测量面积。' }
];
beforeEach(() => { Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: vi.fn() }); });
afterEach(() => { cleanup(); localStorage.clear(); });

describe('toolbox shortcuts and discovery', () => {
  it('keeps favorites and recent tools accessible while all categories are collapsed', () => {
    localStorage.setItem('visionstudio.toolbox-favorites', JSON.stringify(['image.threshold']));
    render(<Toolbox catalog={catalog} recentTypes={['image.synthetic']} onAddNode={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: '折叠全部分类' }));
    expect(within(screen.getByRole('region', { name: '常用收藏' })).getByRole('button', { name: '图像阈值分割', })).toBeTruthy();
    expect(within(screen.getByRole('region', { name: '最近使用' })).getByRole('button', { name: '生成测试图像', })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '斑点分析', })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '展开全部分类' }));
    expect(screen.getByRole('button', { name: '斑点分析', })).toBeTruthy();
  });

  it('groups nodes by their catalog category instead of the shared presentation colour', () => {
    const mixed: NodeCatalogItem[] = [
      { type: 'defect.referenceCompare', displayName: '参考缺陷比较', category: '缺陷检测', inputs: [], outputs: [], parameters: [], description: '与参考图比较。' },
      { type: 'measure.blobs', displayName: '斑点分析', category: '测量', inputs: [], outputs: [], parameters: [], description: '测量斑点。' }
    ];
    render(<Toolbox catalog={mixed} recentTypes={[]} onAddNode={vi.fn()} />);
    expect(screen.getByRole('button', { name: /缺陷检测/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /测量/ })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /缺陷检测/ })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: /测量/ })).toHaveLength(1);
  });

  it('shows concise purposes in search and preserves Enter-to-add after categories are collapsed', () => {
    const onAddNode = vi.fn();
    render(<Toolbox catalog={catalog} recentTypes={[]} onAddNode={onAddNode} />);
    fireEvent.click(screen.getByRole('button', { name: '折叠全部分类' }));
    const input = screen.getByRole('textbox', { name: '搜索节点' });
    fireEvent.change(input, { target: { value: '阈值' } });
    expect(screen.getByText('按阈值提取明亮区域')).toBeTruthy();
    expect(screen.queryByText('支持反向分割。')).toBeNull();
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onAddNode).toHaveBeenCalledWith('image.threshold', undefined);
  });

  it('shows a maturity badge only for preview and experimental nodes', () => {
    const withMaturity: NodeCatalogItem[] = [
      { type: 'measure.thread', displayName: '螺纹测量', category: '测量', inputs: [], outputs: [], parameters: [], description: '轮廓法螺纹测量。', maturity: 'Preview' },
      { type: 'inference.obb', displayName: '旋转框检测', category: '推理', inputs: [], outputs: [], parameters: [], description: '旋转框。', maturity: 'Experimental' },
      { type: 'image.synthetic', displayName: '生成测试图像', category: '图像采集', inputs: [], outputs: [], parameters: [], description: '生成测试图。' }
    ];

    render(<Toolbox catalog={withMaturity} recentTypes={[]} onAddNode={vi.fn()} />);

    const preview = screen.getByRole('button', { name: '螺纹测量' });
    const experimental = screen.getByRole('button', { name: '旋转框检测' });
    const stable = screen.getByRole('button', { name: '生成测试图像' });

    expect(within(preview).getByText('预览').getAttribute('data-maturity')).toBe('preview');
    expect(within(experimental).getByText('实验').getAttribute('data-maturity')).toBe('experimental');
    // Stable 是缺省值，不画徽标——否则 90% 的条目都会带一个没有信息量的标签。
    expect(within(stable).queryByText('预览')).toBeNull();
    expect(within(stable).queryByText('实验')).toBeNull();
    // 徽标必须带悬停说明，讲清"为什么降级"。
    expect(within(preview).getByText('预览').getAttribute('title')).toContain('适用范围');
  });
});
