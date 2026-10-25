import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { NodeCatalogItem } from '../types';
import CommandPalette, { type PaletteCommand } from '../components/CommandPalette';

/**
 * 命令面板（2026-10-25）：一个键盘入口收拢"节点 / 面板 / 命令"。
 * 关键行为：匹配节点后 Enter 直接加节点；命令按分组执行；上下键移动选中项。
 */

const catalog = [
  { type: 'image.threshold', displayName: '阈值分割', category: '图像', parameters: [], inputs: [], outputs: [], description: '' },
  { type: 'image.blur', displayName: '高斯模糊', category: '图像', parameters: [], inputs: [], outputs: [], description: '' }
] as unknown as NodeCatalogItem[];

const targets: PaletteCommand[] = [
  { key: 'run', title: '运行流程', group: '命令', hint: 'Run', keywords: 'run' },
  { key: 'panel:traces', title: '运行追溯', group: '面板', keywords: 'trace' }
];

function mount(overrides: Partial<{ onRunCommand: (key: string) => void; onAddNode: (type: string) => void; onClose: () => void }> = {}) {
  const onRunCommand = overrides.onRunCommand ?? vi.fn();
  const onAddNode = overrides.onAddNode ?? vi.fn();
  const onClose = overrides.onClose ?? vi.fn();
  render(<CommandPalette open onClose={onClose} catalog={catalog} targets={targets}
    onRunCommand={onRunCommand} onAddNode={onAddNode} />);
  return { onRunCommand, onAddNode, onClose };
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('命令面板', () => {
  it('打开即列出命令与面板分组', () => {
    mount();
    expect(screen.getByText('运行流程')).toBeTruthy();
    expect(screen.getByText('运行追溯')).toBeTruthy();
    expect(screen.getByRole('option', { selected: true })).toBeTruthy();
  });

  it('搜索节点后回车直接加入画布', () => {
    const { onAddNode } = mount();
    const input = screen.getByLabelText('命令面板搜索');
    fireEvent.change(input, { target: { value: '阈值' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onAddNode).toHaveBeenCalledWith('image.threshold');
  });

  it('方向键移动后回车执行对应命令', () => {
    const { onRunCommand } = mount();
    const input = screen.getByLabelText('命令面板搜索');
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onRunCommand).toHaveBeenCalledWith('panel:traces');
  });

  it('Esc 关闭面板', () => {
    const { onClose } = mount();
    fireEvent.keyDown(screen.getByLabelText('命令面板搜索'), { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });
});
