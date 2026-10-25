import { cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { isInsidePanel, useEditorShortcuts } from '../useEditorShortcuts';
import type { Node } from '@xyflow/react';

/**
 * 2026-10-25 回归：画布快捷键不能在面板/弹窗打开时穿透。
 * 事故场景——在追溯/配方面板里按 Delete 想清空筛选或删行，画布上选中的节点被静默删除。
 */

type Options = { nodeConfigurationId?: string; overlayOpen?: boolean; nodes?: Node[]; selectedNodeIds?: string[] };

function mount({ nodeConfigurationId, overlayOpen = false, nodes = [], selectedNodeIds = [] }: Options = {}) {
  const handlers = {
    nodeConfigurationId,
    overlayOpen,
    save: vi.fn(),
    undo: vi.fn(),
    redo: vi.fn(),
    copySelection: vi.fn(() => false),
    deleteElements: vi.fn(),
    pushHistory: vi.fn(),
    setNodes: vi.fn(),
    setEdges: vi.fn(),
    setSelectedId: vi.fn(),
    messageApi: { success: vi.fn() },
    nodes,
    selectedNodeIds,
    selectedEdgeIds: [],
    searchInputRef: { current: null },
    clipboardRef: { current: null },
    pasteIndexRef: { current: 0 }
  };
  renderHook(() => useEditorShortcuts(handlers));
  return handlers;
}

function pressOn(target: Element, init: KeyboardEventInit) {
  target.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }));
}

const selectedNode = () => [{ id: 'n1', selected: true, data: {} } as Node];

afterEach(() => {
  cleanup();
  document.body.innerHTML = '';
});

describe('画布快捷键与面板上下文', () => {
  it('画布聚焦时 Delete 删除选中节点（基线行为不变）', () => {
    const handlers = mount({ nodes: selectedNode(), selectedNodeIds: ['n1'] });
    pressOn(document.body, { key: 'Delete' });
    expect(handlers.deleteElements).toHaveBeenCalledWith(['n1'], []);
  });

  it('面板打开、焦点落在 body 上时 Delete 不穿透', () => {
    const handlers = mount({ overlayOpen: true, nodes: selectedNode(), selectedNodeIds: ['n1'] });
    pressOn(document.body, { key: 'Delete' });
    expect(handlers.deleteElements).not.toHaveBeenCalled();
  });

  it('面板打开时保存与查找也不穿透', () => {
    const handlers = mount({ overlayOpen: true });
    pressOn(document.body, { key: 's', ctrlKey: true });
    expect(handlers.save).not.toHaveBeenCalled();
  });

  it('焦点在弹窗内部时 Delete 不穿透（未登记弹层的兜底）', () => {
    const modal = document.createElement('div');
    modal.className = 'ant-modal';
    const button = document.createElement('button');
    modal.appendChild(button);
    document.body.appendChild(modal);
    const handlers = mount({ nodes: selectedNode(), selectedNodeIds: ['n1'] });
    pressOn(button, { key: 'Delete' });
    expect(handlers.deleteElements).not.toHaveBeenCalled();
  });

  it('已关闭弹窗残留的 DOM 不会让快捷键永久失效', () => {
    // antd 关闭弹窗后 wrapper 仍留在 DOM 里（且没有 display:none 内联样式）；
    // 用 document 全局查询判断"面板是否打开"会在这里误判——必须由调用方传状态。
    const wrap = document.createElement('div');
    wrap.className = 'ant-modal-wrap';
    document.body.appendChild(wrap);
    const handlers = mount({ nodes: selectedNode(), selectedNodeIds: ['n1'] });
    pressOn(document.body, { key: 'Delete' });
    expect(handlers.deleteElements).toHaveBeenCalledTimes(1);
  });

  it('输入框内仍然可以用 Ctrl+S 保存（不误伤原有例外）', () => {
    const input = document.createElement('input');
    document.body.appendChild(input);
    const handlers = mount();
    pressOn(input, { key: 's', ctrlKey: true });
    expect(handlers.save).toHaveBeenCalledTimes(1);
  });

  it('输入框内按 Delete 只做文本编辑，不删节点', () => {
    const input = document.createElement('input');
    document.body.appendChild(input);
    const handlers = mount({ nodes: selectedNode(), selectedNodeIds: ['n1'] });
    pressOn(input, { key: 'Delete' });
    expect(handlers.deleteElements).not.toHaveBeenCalled();
  });

  it('isInsidePanel 只认焦点所在的面板，不看全局残留', () => {
    const wrap = document.createElement('div');
    wrap.className = 'ant-modal-wrap';
    document.body.appendChild(wrap);
    expect(isInsidePanel(document.body)).toBe(false);
    expect(isInsidePanel(wrap)).toBe(true);
  });
});
