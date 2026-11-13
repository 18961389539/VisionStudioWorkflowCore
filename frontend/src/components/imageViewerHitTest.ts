/**
 * ImageViewer 的命中检测（纯函数）。
 *
 * 从 `ImageViewer.tsx` 的闭包里提出来，只为让这些判定可以脱离 React 与 Konva 单测：
 * 输入是「几何 + 视图变换 + 屏幕点」，输出是「拖动模式」，不读任何组件状态。
 *
 * 屏幕距离判定（而不是图像坐标距离）是有意的：控制点的视觉大小不随缩放变化，
 * 用图像坐标判定会让放大后的控制点变得极难点中、缩小后又互相抢占。
 */
import { clamp } from './imageViewerUtils';
import {
  caliperCircleArcHandles,
  caliperCircleBandHandles,
  caliperCircleProfileWidthHandle,
  caliperLineProfileSpanHandles,
  caliperLineProfileWidthHandle,
  circleRadiusHandle,
  closestHandle,
  HANDLE_HIT,
  MIN_EDGE_HANDLE_SPAN,
  polygonHandleIndices
} from './imageViewerUtils';
import { buildCaliperCircleAngles } from '../caliperCircleSampling';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { MeasurementDragMode, Point, RoiDragMode, ViewState } from './imageViewerTypes';
import type { VisionRoi } from '../types';

/** 屏幕坐标 → 图像坐标。 */
export function screenToImage(screen: Point, view: ViewState): Point {
  return { x: (screen.x - view.x) / view.scale, y: (screen.y - view.y) / view.scale };
}

/**
 * 命中选区控制点。返回拖动模式；`undefined` 表示没点到控制点。
 *
 * 边中点控制点只在对应边长（屏幕像素）足够长时出现——短边上放控制点会让
 * 「拖角」和「拖边」互相抢占。
 */
export function hitRoiHandle(
  target: VisionRoi,
  screen: Point,
  view: ViewState,
  imageWidth: number,
  imageHeight: number
): RoiDragMode | undefined {
  if (target.type === 'Rectangle') {
    const handles: [RoiDragMode, Point][] = [
      ['nw', { x: target.x, y: target.y }], ['ne', { x: target.x + target.width, y: target.y }],
      ['sw', { x: target.x, y: target.y + target.height }], ['se', { x: target.x + target.width, y: target.y + target.height }]
    ];
    if (target.height * view.scale >= MIN_EDGE_HANDLE_SPAN) {
      handles.push(['w', { x: target.x, y: target.y + target.height / 2 }],
        ['e', { x: target.x + target.width, y: target.y + target.height / 2 }]);
    }
    if (target.width * view.scale >= MIN_EDGE_HANDLE_SPAN) {
      handles.push(['n', { x: target.x + target.width / 2, y: target.y }],
        ['s', { x: target.x + target.width / 2, y: target.y + target.height }]);
    }
    return closestHandle(handles, screen, view);
  }
  if (target.type === 'Circle') {
    const handle = circleRadiusHandle({ x: target.x, y: target.y }, target.radius, imageWidth, imageHeight);
    return closestHandle([['e', handle]], screen, view);
  }
  if (target.type === 'Brush') return undefined;
  return closestHandle(polygonHandleIndices(target.points.length)
    .map((vertexIndex): [RoiDragMode, Point] => [`vertex:${vertexIndex}`, target.points[vertexIndex]]), screen, view);
}

/**
 * 命中卡尺几何控制点。返回拖动模式；`undefined` 表示没点到。
 *
 * 线型优先判端点/跨度/宽度控制点，再退化为「点在线段上」判定整体拖动；
 * 圆型先判显式控制点，再退化为「点落在圆周上」。
 */
export function hitMeasurementHandle(
  target: MeasurementGeometry,
  screen: Point,
  view: ViewState,
  imageWidth: number,
  imageHeight: number
): MeasurementDragMode | undefined {
  const near = (point: Point) => Math.hypot(screen.x - (view.x + point.x * view.scale), screen.y - (view.y + point.y * view.scale)) <= HANDLE_HIT;
  if (target.type === 'line') {
    const profileHandles = target.profileCount !== undefined
      ? caliperLineProfileSpanHandles(target, imageWidth, imageHeight) : undefined;
    const candidates: [MeasurementDragMode, Point][] = [['start', target.start], ['end', target.end]];
    if (profileHandles) candidates.push(['profileStart', profileHandles.start], ['profileEnd', profileHandles.end]);
    const widthHandle = caliperLineProfileWidthHandle(target, imageWidth, imageHeight);
    if (widthHandle) candidates.push(['profileWidth', widthHandle]);
    const endpoint = closestHandle(candidates, screen, view);
    if (endpoint) return endpoint;
    const dx = target.end.x - target.start.x, dy = target.end.y - target.start.y;
    const lengthSquared = dx * dx + dy * dy;
    const cursor = screenToImage(screen, view);
    const t = lengthSquared > 0 ? clamp(((cursor.x - target.start.x) * dx + (cursor.y - target.start.y) * dy) / lengthSquared, 0, 1) : 0;
    const closest = { x: target.start.x + t * dx, y: target.start.y + t * dy };
    if (near(closest)) return 'moveLine';
    return undefined;
  }
  const arcHandles = caliperCircleArcHandles(target, view.scale, imageWidth, imageHeight);
  const bandHandles = caliperCircleBandHandles(target, view.scale, imageWidth, imageHeight);
  const candidates: [MeasurementDragMode, Point][] = [];
  const angles = buildCaliperCircleAngles(target);
  const widthHandle = caliperCircleProfileWidthHandle(target, angles, imageWidth, imageHeight);
  if (target.centerEditable !== false) candidates.push(['center', target.center]);
  if (target.radiusEditable !== false) candidates.push(['radius', circleRadiusHandle(target.center, target.radius, imageWidth, imageHeight)]);
  if (widthHandle) candidates.push(['profileWidth', widthHandle.point]);
  if (arcHandles?.start) candidates.push(['angleStart', arcHandles.start]);
  if (arcHandles?.end) candidates.push(['angleEnd', arcHandles.end]);
  if (bandHandles) candidates.push(['innerBand', bandHandles.inner], ['outerBand', bandHandles.outer]);
  const closest = closestHandle(candidates, screen, view);
  if (closest) return closest;
  const cursor = screenToImage(screen, view);
  if (target.centerEditable !== false
    && Math.abs(Math.hypot(cursor.x - target.center.x, cursor.y - target.center.y) - target.radius) * view.scale <= HANDLE_HIT)
    return 'moveCircle';
  return undefined;
}
