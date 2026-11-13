/**
 * ImageViewer 系列测试的共享夹具。
 *
 * 三个测试文件共用同一套：视口尺寸桩、无 ROI 的渲染入口、标称圆的常用几何与点击换算。
 * 尺寸固定为 400×260（对应 `ResizeObserver` 桩），测试里的屏幕坐标都基于它推算。
 */
import { fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import ImageViewer, { type MeasurementGeometry } from '../../components/ImageViewer';
import type { VisionOverlay } from '../../types';

/** 视口固定 400×260：ImageViewer 的缩放/适配全部按这个尺寸计算。 */
export const VIEWER_WIDTH = 400;
export const VIEWER_HEIGHT = 260;

/**
 * jsdom 没有 ResizeObserver。这里让它立刻回调一次固定尺寸，
 * 这样组件在首次布局后就能拿到可用视口（否则所有几何都画在 0×0 里）。
 */
export function stubResizeObserver() {
  vi.stubGlobal('ResizeObserver', class {
    constructor(private readonly callback: ResizeObserverCallback) {}
    observe() {
      this.callback([{ contentRect: { width: VIEWER_WIDTH, height: VIEWER_HEIGHT } } as ResizeObserverEntry],
        this as unknown as ResizeObserver);
    }
    unobserve() {}
    disconnect() {}
  });
}

export const overlays: VisionOverlay[] = [];
export const emptyRoi = () => {};

/** 只读图像 + 可编辑测量几何的最小渲染入口。 */
export const imageView = (geometry: MeasurementGeometry, onChange: (geometry: MeasurementGeometry) => void) => render(
  <ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100} overlays={overlays}
    roiEnabled={false} onRoiChange={emptyRoi} measurementGeometry={geometry} measurementGeometryEditable onMeasurementGeometryChange={onChange} />
);

/** 常用的标称圆：半径 20 的半圆搜索（-90°~90°），与多数用例的预期值对齐。 */
export const circleGeometry: Extract<MeasurementGeometry, { type: 'circle' }> = {
  type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
  angleStartDeg: -90, angleEndDeg: 90, caliperCount: 24, profileWidth: 5
};

/** 图像坐标 → 视口坐标的偏移，来自「原始比例」下的居中摆放。 */
const ORIGIN = { x: 150, y: 80 };

/** 在图像坐标 (x,y) 处点一下（按下 + 全局松开）。 */
export const pickCirclePoint = (x: number, y: number) => {
  fireEvent.mouseDown(screen.getByTestId('stage'), { clientX: x + ORIGIN.x, clientY: y + ORIGIN.y });
  fireEvent.mouseUp(document.body);
};

/** 进入「重绘标称圆」并按指定方式圈选。 */
export const startCircleDrawing = (mode: string) => {
  fireEvent.click(screen.getByRole('button', { name: '原始比例' }));
  fireEvent.click(screen.getByRole('button', { name: '重绘标称圆' }));
  fireEvent.click(screen.getByRole('radio', { name: mode }));
};
