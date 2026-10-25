/**
 * 编辑器全局键盘快捷键（App.tsx `Editor` 的 keydown 处理器）。
 * 从 App.tsx 拆出（巨型组件拆分，2026-10-23）；行为与原内联 effect 完全一致。
 *
 * 快捷键：Ctrl/Cmd+S 保存、Ctrl/Cmd+F 查找节点、Ctrl/Cmd+Z 撤销、Ctrl/Cmd+Shift+Z（或 Ctrl+Y）重做、
 * Ctrl/Cmd+C / V 复制粘贴选中集合、Delete/Backspace 删除选中的节点或连线；
 * 焦点在输入控件内时只保留原生文本编辑行为（查找与保存除外）。捕获阶段监听，避免画布吞键。
 */
import { useEffect } from 'react';
import type { Edge, Node } from '@xyflow/react';
import type { InputRef } from 'antd';

export type EditorClipboard = { nodes: Node[]; edges: Edge[] };

/** 焦点在输入类控件内：只保留原生编辑行为，画布快捷键不响应。 */
const INPUT_LIKE_SELECTOR = 'input, textarea, select, [contenteditable="true"], .toolbox, .ant-select-dropdown';
/** 面板/弹窗内的快捷键属于面板自身，不能穿透到画布。 */
const PANEL_SELECTOR = '.ant-modal, .ant-modal-wrap, .ant-drawer, [role="dialog"], .ant-popover';

/** 焦点是否在输入类控件内（保存与查找不受此限制，与拆分前行为一致）。 */
export function isEditableTarget(target: Element | null): boolean {
  return Boolean(target?.closest?.(INPUT_LIKE_SELECTOR));
}

/**
 * 事件焦点是否落在面板/弹窗内部。
 *
 * 注意：**不要**用 `document.querySelector('.ant-modal-wrap')` 判断"有没有面板打开"——
 * antd 关闭弹窗后仍把 wrapper 留在 DOM 里（关闭瞬间也不带 display:none 的内联样式），
 * 这样判断会让画布快捷键在第一次打开过面板之后永久失效。打开状态必须由调用方显式传入。
 */
export function isInsidePanel(target: Element | null): boolean {
  return Boolean(target?.closest?.(PANEL_SELECTOR));
}

type EditorShortcutHandlers = {
  /** 节点配置弹窗打开时的目标节点 ID；弹窗内快捷键不穿透（仅保存/查找 preventDefault） */
  nodeConfigurationId: string | undefined;
  /** 是否有面板/弹窗（抽屉、配方、追溯、设置…）打开。焦点落在蒙层或 body 时 target 判断不出来，必须显式传入。 */
  overlayOpen?: boolean;
  save: () => void;
  undo: () => void;
  redo: () => void;
  /** 复制当前选中集合；返回是否真的复制了内容（决定是否 preventDefault） */
  copySelection: () => boolean;
  deleteElements: (nodeIds: string[], edgeIds: string[]) => void;
  pushHistory: (label: string) => void;
  setNodes: (updater: (current: Node[]) => Node[]) => void;
  setEdges: (updater: (current: Edge[]) => Edge[]) => void;
  setSelectedId: (value: string | ((current: string) => string)) => void;
  messageApi: { success: (content: string) => void };
  nodes: Node[];
  selectedNodeIds: string[];
  selectedEdgeIds: string[];
  /** 画布左上查找框（Ctrl/Cmd+F 聚焦并全选） */
  searchInputRef: { current: InputRef | null };
  clipboardRef: { current: EditorClipboard | null };
  pasteIndexRef: { current: number };
};

export function useEditorShortcuts(handlers: EditorShortcutHandlers): void {
  const {
    nodeConfigurationId, overlayOpen, save, undo, redo, copySelection, deleteElements, pushHistory,
    setNodes, setEdges, setSelectedId, messageApi, nodes, selectedNodeIds, selectedEdgeIds,
    searchInputRef, clipboardRef, pasteIndexRef
  } = handlers;

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const comboKey = event.key.toLowerCase();
      const mod = event.ctrlKey || event.metaKey;
      // 弹窗内的参数尚未应用，保存/查找/删除等画布快捷键不能穿透。
      if (nodeConfigurationId) {
        if (mod && (comboKey === 's' || comboKey === 'f')) event.preventDefault();
        return;
      }
      // 面板/弹窗打开时，画布级快捷键整体让位：保存/查找/撤销/删除都由面板自己决定语义。
      // 焦点常在蒙层或 body 上，单看 target 判断不出来，所以打开状态由调用方显式传入；
      // 焦点落在面板内部时再兜一层（覆盖未登记的第三方弹层）。
      if (overlayOpen || isInsidePanel(event.target as Element | null)) return;
      if (mod && comboKey === 's') {
        event.preventDefault();
        void save();
        return;
      }
      if (mod && comboKey === 'f') {
        event.preventDefault();
        searchInputRef.current?.focus();
        searchInputRef.current?.select();
        return;
      }
      // 输入控件内只保留原生编辑行为（保存与查找已在上面处理）。
      if (isEditableTarget(event.target as Element | null)) return;
      if (mod && !event.altKey) {
        if (comboKey === 'z' && !event.shiftKey) { event.preventDefault(); undo(); return; }
        if ((comboKey === 'z' && event.shiftKey) || comboKey === 'y') { event.preventDefault(); redo(); return; }
        if (comboKey === 'c' && !event.shiftKey) {
          if (!copySelection()) return;
          event.preventDefault();
          return;
        }
        if (comboKey === 'v' && !event.shiftKey) {
          const clip = clipboardRef.current;
          if (!clip || clip.nodes.length === 0) return;
          event.preventDefault();
          pasteIndexRef.current += 1;
          const offset = 40 * pasteIndexRef.current;
          const idMap = new Map<string, string>();
          const pastedNodes: Node[] = clip.nodes.map((node) => {
            const id = `${String(node.data.typeKey ?? 'node')}-${crypto.randomUUID().slice(0, 8)}`;
            idMap.set(node.id, id);
            return {
              ...node,
              id,
              selected: true,
              position: { x: node.position.x + offset, y: node.position.y + offset },
              measured: undefined,
              // 运行态字段不随粘贴继承
              data: { ...node.data, status: 'idle', breakpoint: false, durationMs: undefined, error: undefined, disposition: undefined, summary: undefined }
            };
          });
          const pastedEdges: Edge[] = clip.edges.map((edge) => ({
            ...edge,
            id: crypto.randomUUID(),
            source: idMap.get(edge.source) ?? edge.source,
            target: idMap.get(edge.target) ?? edge.target
          }));
          pushHistory('粘贴节点');
          setNodes((current) => [...current.map((node) => (node.selected ? { ...node, selected: false } : node)), ...pastedNodes]);
          setEdges((current) => [...current, ...pastedEdges]);
          setSelectedId(pastedNodes[0].id);
          void messageApi.success(`已粘贴 ${pastedNodes.length} 个节点`);
          return;
        }
      }
      if (event.key !== 'Delete' && event.key !== 'Backspace' && event.key !== 'Del' && event.code !== 'Delete' && event.code !== 'Backspace') return;
      const explicitlySelectedNodeIds = nodes.filter((node) => node.selected).map((node) => node.id);
      // 选中连线时忽略 selectedId 的单节点兼容回退，避免误删上次选中的节点。
      const nodeIdsToDelete = explicitlySelectedNodeIds.length > 0
        ? explicitlySelectedNodeIds
        : selectedEdgeIds.length > 0 ? [] : selectedNodeIds;
      if (nodeIdsToDelete.length === 0 && selectedEdgeIds.length === 0) return;
      event.preventDefault();
      deleteElements(nodeIdsToDelete, selectedEdgeIds);
    };
    // 捕获阶段监听：避免画布内部对按键的 stopPropagation 把删除快捷键吃掉
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [copySelection, deleteElements, messageApi, nodeConfigurationId, overlayOpen, nodes, pushHistory, redo, save, selectedEdgeIds, selectedNodeIds, setEdges, setNodes, undo, searchInputRef, clipboardRef, pasteIndexRef]);
}
