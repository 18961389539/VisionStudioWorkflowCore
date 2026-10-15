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
});
