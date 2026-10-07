import { useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';

type Drag = { pointer: number; base: number; current: number };

type Props = {
  axis: 'x' | 'y';
  label: string;
  value: number;
  min: number;
  max: number;
  /** 每个像素对应的数值增量（图像区按百分比时传 100 / 中央高度） */
  scale?: number;
  /** 反向：向右 / 向下拖动为减小（右栏、下栏、图像区） */
  invert?: boolean;
  collapsed: boolean;
  collapsible?: boolean;
  collapseGlyph: string;
  expandGlyph: string;
  maximize?: { active: boolean; title: string; onToggle: () => void };
  /** 拖动中：只改 CSS 变量，不触发 React 重渲染 */
  preview: (value: number) => void;
  /** 松手 / 键盘操作：写入状态并持久化 */
  commit: (value: number) => void;
  reset: () => void;
  onToggleCollapse: () => void;
};

export default function SplitHandle({
  axis, label, value, min, max, scale = 1, invert = false, collapsed, collapsible = true,
  collapseGlyph, expandGlyph, maximize, preview, commit, reset, onToggleCollapse
}: Props) {
  const dragRef = useRef<Drag | undefined>(undefined);
  const [dragging, setDragging] = useState(false);
  const sign = invert ? -1 : 1;
  const clampValue = (next: number) => Math.round(Math.min(Math.max(next, min), max));

  const endDrag = (target: HTMLElement, pointerId: number) => {
    const drag = dragRef.current;
    dragRef.current = undefined;
    setDragging(false);
    document.body.classList.remove('resizing-x', 'resizing-y');
    if (target.hasPointerCapture(pointerId)) target.releasePointerCapture(pointerId);
    if (drag && drag.current !== value) commit(drag.current);
  };

  return (
    <div
      className={`split-handle axis-${axis}${dragging ? ' active' : ''}${collapsed ? ' collapsed' : ''}`}
      role="separator"
      tabIndex={0}
      aria-label={label}
      aria-orientation={axis === 'x' ? 'vertical' : 'horizontal'}
      aria-valuenow={Math.round(value)}
      aria-valuemin={min}
      aria-valuemax={max}
      title={`${label}：拖动调整 · 双击复位`}
      onPointerDown={(event: PointerEvent<HTMLDivElement>) => {
        if (event.button !== 0) return;
        event.currentTarget.focus();
        event.currentTarget.setPointerCapture(event.pointerId);
        dragRef.current = { pointer: axis === 'x' ? event.clientX : event.clientY, base: value, current: value };
        setDragging(true);
        document.body.classList.add(axis === 'x' ? 'resizing-x' : 'resizing-y');
      }}
      onPointerMove={(event: PointerEvent<HTMLDivElement>) => {
        const drag = dragRef.current;
        if (!drag) return;
        const pointer = axis === 'x' ? event.clientX : event.clientY;
        const next = clampValue(drag.base + (pointer - drag.pointer) * sign * scale);
        drag.current = next;
        preview(next);
      }}
      onPointerUp={(event: PointerEvent<HTMLDivElement>) => endDrag(event.currentTarget, event.pointerId)}
      onPointerCancel={(event: PointerEvent<HTMLDivElement>) => endDrag(event.currentTarget, event.pointerId)}
      onDoubleClick={() => reset()}
      onKeyDown={(event: KeyboardEvent<HTMLDivElement>) => {
        const step = event.shiftKey ? 32 : 8;
        if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') { event.preventDefault(); commit(clampValue(value - step * sign * scale)); }
        else if (event.key === 'ArrowRight' || event.key === 'ArrowDown') { event.preventDefault(); commit(clampValue(value + step * sign * scale)); }
        else if (event.key === 'Home') { event.preventDefault(); commit(clampValue(min)); }
        else if (event.key === 'End') { event.preventDefault(); commit(clampValue(max)); }
      }}
    >
      {collapsible && (
        <button
          type="button"
          className="split-button"
          title={collapsed ? '展开' : '折叠'}
          aria-label={collapsed ? '展开' : '折叠'}
          onPointerDown={(event) => event.stopPropagation()}
          onDoubleClick={(event) => event.stopPropagation()}
          onClick={(event) => { event.stopPropagation(); onToggleCollapse(); }}
        >
          {collapsed ? expandGlyph : collapseGlyph}
        </button>
      )}
      {maximize && (
        <button
          type="button"
          className={`split-button${maximize.active ? ' active' : ''}`}
          title={maximize.title}
          aria-label={maximize.title}
          onPointerDown={(event) => event.stopPropagation()}
          onDoubleClick={(event) => event.stopPropagation()}
          onClick={(event) => { event.stopPropagation(); maximize.onToggle(); }}
        >
          {maximize.active ? '⤡' : '⤢'}
        </button>
      )}
    </div>
  );
}