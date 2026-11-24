import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ShortcutHelpModal from '../components/ShortcutHelpModal';

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({
    matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {},
  }));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('快捷键帮助', () => {
  it('列出命令面板的 Ctrl/Cmd+K —— 最容易在加功能时漏登记的一条', () => {
    render(<ShortcutHelpModal open onClose={vi.fn()} />);
    expect(screen.getByText(/打开命令面板/)).toBeInTheDocument();
    expect(screen.getAllByText('K').length).toBeGreaterThan(0);
  });

  it('Esc 的说明覆盖"关闭弹层"，不只写清空查找框', () => {
    render(<ShortcutHelpModal open onClose={vi.fn()} />);
    expect(screen.getByText(/关闭弹层/)).toBeInTheDocument();
  });

  it('画布快捷键的核心项都在表里', () => {
    render(<ShortcutHelpModal open onClose={vi.fn()} />);
    ['保存工作流', '撤销', '重做', '复制选中节点', '粘贴节点'].forEach((label) => {
      // “撤销”等词也会出现在别的描述里（如“可撤销”），这里只要求至少命中一处
      expect(screen.getAllByText(new RegExp(label)).length).toBeGreaterThan(0);
    });
  });
});
