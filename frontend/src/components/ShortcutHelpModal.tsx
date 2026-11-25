/**
 * 键盘快捷键一览弹窗（工具菜单 → 键盘快捷键…）。
 * 从 App.tsx 拆出（巨型组件拆分，2026-10-23）。
 * 与 App.tsx 的 keydown 处理器保持同步，改快捷键时两处一起改。
 */
import { Button, Modal } from 'antd';
import { useI18n } from '../i18n/provider';

const SHORTCUT_ROWS: { combos: string[][]; desc: string; en: string }[] = [
  { combos: [['Ctrl/Cmd', 'S']], desc: '保存工作流（节点配置弹窗内为“应用暂存参数”）', en: 'Save workflow (or apply staged parameters in node dialog)' },
  { combos: [['Ctrl/Cmd', 'Shift', 'Enter']], desc: '运行整个工作流（需处于非调试且空闲状态）', en: 'Run full workflow (when idle and not debugging)' },
  { combos: [['Ctrl/Cmd', 'Enter']], desc: '节点配置弹窗内：运行此节点（使用暂存参数测试，不写回流程）', en: 'In node dialog: test-run this node with staged parameters' },
  { combos: [['Ctrl/Cmd', 'F']], desc: '在画布中查找节点（聚焦左上搜索框）', en: 'Find nodes on canvas (focus search box)' },
  { combos: [['Ctrl/Cmd', 'K']], desc: '打开命令面板（搜命令、面板与节点）', en: 'Open command palette (search commands, panels, nodes)' },
  { combos: [['F2']], desc: '重命名选中的节点', en: 'Rename the selected node' },
  { combos: [['F5']], desc: '调试会话暂停时继续执行', en: 'Continue a paused debug session' },
  { combos: [['F10']], desc: '使用缓存输入运行选中的节点（调试会话）', en: 'Run the selected node with cached inputs (debug session)' },
  { combos: [['Shift', 'F5']], desc: '结束当前调试会话', en: 'End the current debug session' },
  { combos: [['Ctrl/Cmd', '0']], desc: '适应画布（缩放并居中显示整个流程）', en: 'Fit workflow to view' },
  { combos: [['Ctrl/Cmd', 'A']], desc: '全选画布节点', en: 'Select all nodes on canvas' },
  { combos: [['Ctrl/Cmd', 'Z']], desc: '撤销', en: 'Undo' },
  { combos: [['Ctrl/Cmd', 'Shift', 'Z'], ['Ctrl', 'Y']], desc: '重做', en: 'Redo' },
  { combos: [['Ctrl/Cmd', 'C']], desc: '复制选中节点（含集合内部连线）', en: 'Copy selected nodes (including internal edges)' },
  { combos: [['Ctrl/Cmd', 'V']], desc: '粘贴节点（整体偏移，连续粘贴逐次错位）', en: 'Paste nodes (offset successively)' },
  { combos: [['Ctrl/Cmd', 'D']], desc: '快速复制选中节点（原地生成副本）', en: 'Duplicate selected nodes in place' },
  { combos: [['Delete'], ['Backspace']], desc: '删除选中的节点或连线（可撤销）', en: 'Delete selected nodes or edges (undoable)' },
  { combos: [['Alt', 'Delete']], desc: '删除单个中间节点并桥接前后连线（仅端口兼容时）', en: 'Delete and bridge a single intermediate node when ports are compatible' },
  { combos: [['Shift', '拖拽']], desc: '框选多个节点（Ctrl/Cmd/Shift + 点击为追加选择）', en: 'Box-select nodes (Ctrl/Cmd/Shift + click to multi-select)' },
  { combos: [['双击节点']], desc: '打开节点配置弹窗（参数 / 图像 / 说明）', en: 'Open node configuration dialog (double-click node)' },
  { combos: [['双击分隔条']], desc: '复位该面板尺寸（工具箱 / 结果区）', en: 'Reset panel size (double-click split handle)' },
  { combos: [['Esc']], desc: '关闭弹层；清空画布查找框；连线选择菜单中取消', en: 'Close overlay; clear search; cancel connection picker' }
];

export default function ShortcutHelpModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { lang } = useI18n();
  const isEn = lang === 'en';
  return (
    <Modal
      open={open}
      title={isEn ? 'Keyboard shortcuts' : '键盘快捷键'}
      width={560}
      footer={<Button onClick={onClose}>{isEn ? 'Close' : '关闭'}</Button>}
      onCancel={onClose}
    >
      <div className="shortcut-list">
        {SHORTCUT_ROWS.map((row) => (
          <div className="shortcut-row" key={row.desc}>
            <span className="shortcut-keys">
              {row.combos.map((combo, comboIndex) => (
                <span key={combo.join('+')}>
                  {comboIndex > 0 && <span className="shortcut-alt">{isEn ? 'or' : '或'}</span>}
                  {combo.map((key) => <kbd key={key}>{key}</kbd>)}
                </span>
              ))}
            </span>
            <span className="shortcut-desc">{isEn ? row.en : row.desc}</span>
          </div>
        ))}
      </div>
    </Modal>
  );
}
