/**
 * `react-konva` 的测试替身（ImageViewer 系列测试共用）。
 *
 * Konva 需要真实 canvas，jsdom 里跑不了。这里把每种图形降级成一个带 `data-testid` 的
 * `<span>`，测试通过读 `data-*` 属性断言几何，而不是断言渲染结果。
 *
 * 用法（每个测试文件各写一行，因为 `vi.mock` 只对所在模块图生效）：
 * ```ts
 * vi.mock('react-konva', async () => await import('./helpers/konvaMock'));
 * ```
 *
 * `Stage` 必须实现 `getPointerPosition` 与 `setPointersPositions`：Imageviewer 的手势
 * 状态机靠前者读指针、靠后者在「拖出画布后在别处松开」时补一次位置。
 */
import type { ForwardedRef, PropsWithChildren } from 'react';
import React from 'react';

/** 笔刷路径的渲染序号：测试用它证明「已完成笔迹在绘制过程中不被重建」。 */
let brushStrokeRenderId = 0;

type Pointer = { x: number; y: number };

export const Stage = React.forwardRef((props: Record<string, any>, ref: ForwardedRef<any>) => {
  const pointer = React.useRef<Pointer | undefined>(undefined);
  const setPointer = (event: any) => {
    const touch = event.touches?.[0] ?? event.changedTouches?.[0];
    pointer.current = { x: touch?.clientX ?? event.clientX, y: touch?.clientY ?? event.clientY };
  };
  React.useImperativeHandle(ref, () => ({
    getPointerPosition: () => pointer.current,
    setPointersPositions: setPointer
  }));
  const dispatch = (handler?: (event: unknown) => void) => (event: any) => {
    setPointer(event);
    handler?.(event);
  };
  return <div data-testid="stage" onMouseDown={dispatch(props.onMouseDown)} onMouseMove={dispatch(props.onMouseMove)}
    onMouseUp={dispatch(props.onMouseUp)} onTouchStart={dispatch(props.onTouchStart)} onTouchMove={dispatch(props.onTouchMove)}
    onTouchEnd={dispatch(props.onTouchEnd)} onClick={dispatch(props.onClick)} onDoubleClick={dispatch(props.onDblClick)}
    onMouseLeave={props.onMouseLeave}>{props.children}</div>;
});

export const Layer = ({ children }: PropsWithChildren) => <>{children}</>;
export const Group = ({ children }: PropsWithChildren) => <>{children}</>;

export const Arrow = ({ points, pointerAtBeginning, name }: { points?: number[]; pointerAtBeginning?: boolean; name?: string }) =>
  <span data-testid={name === 'caliper-diagnostic-selected-spoke' ? name : 'search-arrow'}
    data-points={points?.join(',')} data-pointer-at-beginning={pointerAtBeginning} />;

export const Image = () => null;

export const Line = ({ points, stroke, name }: { points?: number[]; stroke?: string; name?: string }) =>
  name?.startsWith('measurement-profile-band-') || name?.startsWith('measurement-circle-profile-band-')
    ? <span data-testid="measurement-profile-band" data-band-index={name.slice(name.indexOf('profile-band-') + 'profile-band-'.length)}
      data-points={points?.join(',')} />
    : name?.startsWith('roi-brush-stroke-')
      ? <span data-testid="roi-brush-stroke" data-mode={name.slice('roi-brush-stroke-'.length)}
        data-render-id={++brushStrokeRenderId} data-point-count={points?.length ? points.length / 2 : 0} />
      : stroke === '#ff4d4f'
        ? <span data-testid="roi-polygon-preview" data-point-count={points?.length ? points.length / 2 : 0} /> : null;

export const Circle = ({ name, x, y }: { name?: string; x?: number; y?: number }) =>
  name?.startsWith('measurement-profile-span-') || name === 'measurement-profile-width'
    || name?.startsWith('caliper-circle-angle-') || name === 'caliper-diagnostic-selected-edge'
    ? <span data-testid={name} data-x={x} data-y={y} /> : null;

export const Rect = ({ name }: { name?: string }) => name === 'roi-handle' ? <span data-testid={name} /> : null;

export const Text = ({ text }: { text?: string }) => <span data-testid="konva-text">{text}</span>;
