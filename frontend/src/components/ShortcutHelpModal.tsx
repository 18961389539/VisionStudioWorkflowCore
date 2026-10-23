/**
 * 键盘快捷键一览弹窗（工具菜单 → 键盘快捷键…）。
 * 从 App.tsx 拆出（巨型组件拆分，2026-10-23）。
 * 与 App.tsx 的 keydown 处理器保持同步，改快捷键时两处一起改。
 */
import { Button, Modal } from 'antd';

const SHORTCUT_ROWS: { combos: string[][]; desc: string }[] = [
  { combos: [['Ctrl/Cmd', 'S']], desc: '保存工作流（节点配置弹窗内为“应用暂存参数”）' },
  { combos: [['Ctrl/Cmd', 'Enter']], desc: '节点配置弹窗内：运行此节点（使用暂存参数测试，不写回流程）' },
  { combos: [['Ctrl/Cmd', 'F']], desc: '在画布中查找节点（聚焦左上搜索框）' },
  { combos: [['Ctrl/Cmd', 'Z']], desc: '撤销' },
  { combos: [['Ctrl/Cmd', 'Shift', 'Z'], ['Ctrl', 'Y']], desc: '重做' },
  { combos: [['Ctrl/Cmd', 'C']], desc: '复制选中节点（含集合内部连线）' },
  { combos: [['Ctrl/Cmd', 'V']], desc: '粘贴节点（整体偏移，连续粘贴逐次错位）' },
  { combos: [['Delete'], ['Backspace']], desc: '删除选中的节点或连线（可撤销）' },
  { combos: [['Shift', '拖拽']], desc: '框选多个节点（Ctrl/Cmd/Shift + 点击为追加选择）' },
  { combos: [['双击节点']], desc: '打开节点配置弹窗（参数 / 图像 / 说明）' },
  { combos: [['双击分隔条']], desc: '复位该面板尺寸（工具箱 / 结果区）' },
  { combos: [['Esc']], desc: '清空画布查找框；连线选择菜单中取消' }
];

export default function ShortcutHelpModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Modal
      open={open}
      title="键盘快捷键"
      width={560}
      footer={<Button onClick={onClose}>关闭</Button>}
      onCancel={onClose}
    >
      <div className="shortcut-list">
        {SHORTCUT_ROWS.map((row) => (
          <div className="shortcut-row" key={row.desc}>
            <span className="shortcut-keys">
              {row.combos.map((combo, comboIndex) => (
                <span key={combo.join('+')}>
                  {comboIndex > 0 && <span className="shortcut-alt">或</span>}
                  {combo.map((key) => <kbd key={key}>{key}</kbd>)}
                </span>
              ))}
            </span>
            <span className="shortcut-desc">{row.desc}</span>
          </div>
        ))}
      </div>
    </Modal>
  );
}
