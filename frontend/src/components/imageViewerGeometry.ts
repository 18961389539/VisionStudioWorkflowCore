/**
 * ImageViewer 的纯几何计算与缓存工具（无 React / Konva 依赖）。
 */
import type { Point, ViewState, RoiDragMode, CaliperDiagnosticSelection, RoiMoveBounds, MeasurementGeometry } from './imageViewerTypes';
import type { VisionRoi, OverlayPoint } from '../types';
import { circleArcSpan, normalizeCaliperAngle } from '../caliperCircleSampling';

export { buildCaliperCircleAngles } from '../caliperCircleSampling';
export type { MeasurementGeometry } from './imageViewerTypes';

export function nearestDiagnosticSpoke(selection: CaliperDiagnosticSelection, point: Point, scale: number) {
  let nearest: number | undefined;
  let distance = 12 / scale;
  for (const spoke of selection.spokes) {
    const start = spoke.profileStart ?? spoke.start, end = spoke.profileEnd ?? spoke.end;
    const dx = end.x - start.x, dy = end.y - start.y;
    const fraction = Math.max(0, Math.min(1, ((point.x - start.x) * dx + (point.y - start.y) * dy) / Math.max(1e-12, dx * dx + dy * dy)));
    const current = Math.hypot(point.x - start.x - fraction * dx, point.y - start.y - fraction * dy);
    if (current < distance) { distance = current; nearest = spoke.index; }
  }
  return nearest;
}

/** 像素化显示偏好：默认开启——放大时按真实像素方块渲染（最近邻），便于逐像素核对；
 *  关闭则使用画布默认的平滑插值（缩放总览时更顺眼）。与其它显示偏好一样本地持久化。 */
export const PIXELATED_KEY = 'visionstudio.viewer-pixelated';
export const readPixelated = () => {
  try { return window.localStorage.getItem(PIXELATED_KEY) !== '0'; } catch { return true; }
};
export const persistPixelated = (value: boolean) => {
  try { window.localStorage.setItem(PIXELATED_KEY, value ? '1' : '0'); } catch { /* 忽略存储失败 */ }
};

export const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));

export function caliperLineProfileSpanHandles(geometry: Extract<MeasurementGeometry, { type: 'line' }>,
  width: number, height: number): { start: Point; end: Point } | undefined {
  if (geometry.profileCount === undefined || width <= 0 || height <= 0) return undefined;
  const dx = geometry.end.x - geometry.start.x, dy = geometry.end.y - geometry.start.y;
  const length = Math.hypot(dx, dy);
  if (length <= 0) return undefined;
  const span = geometry.profileSpan && geometry.profileSpan > 0 ? geometry.profileSpan : length;
  const tangent = { x: -dy / length, y: dx / length };
  const midpoint = { x: (geometry.start.x + geometry.end.x) / 2, y: (geometry.start.y + geometry.end.y) / 2 };
  return clipSegmentToImage(
    { x: midpoint.x - tangent.x * span / 2, y: midpoint.y - tangent.y * span / 2 },
    { x: midpoint.x + tangent.x * span / 2, y: midpoint.y + tangent.y * span / 2 }, width, height);
}

export function caliperLineProfileWidthHandle(geometry: Extract<MeasurementGeometry, { type: 'line' }>,
  width: number, height: number): Point | undefined {
  if (geometry.profileWidth === undefined || width <= 0 || height <= 0) return undefined;
  const dx = geometry.end.x - geometry.start.x, dy = geometry.end.y - geometry.start.y;
  const length = Math.hypot(dx, dy);
  if (length <= 0) return undefined;
  const count = geometry.profileCount === undefined ? 1 : Math.round(clamp(geometry.profileCount, 3, 101));
  const span = geometry.profileSpan && geometry.profileSpan > 0 ? geometry.profileSpan : length;
  const tangent = { x: -dy / length, y: dx / length };
  const halfWidth = Math.max(0, geometry.profileWidth) / 2;
  let selected: { point: Point; margin: number } | undefined;
  for (let index = 0; index < count; index++) {
    const offset = count === 1 ? 0 : (index / (count - 1) - 0.5) * span;
    const offsetX = tangent.x * offset, offsetY = tangent.y * offset;
    const clipped = clipSegmentToImage(
      { x: geometry.start.x + offsetX, y: geometry.start.y + offsetY },
      { x: geometry.end.x + offsetX, y: geometry.end.y + offsetY }, width, height);
    if (!clipped) continue;
    const midpoint = { x: (clipped.start.x + clipped.end.x) / 2, y: (clipped.start.y + clipped.end.y) / 2 };
    const point = { x: clamp(midpoint.x + tangent.x * halfWidth, 0, width - 1),
      y: clamp(midpoint.y + tangent.y * halfWidth, 0, height - 1) };
    const margin = Math.min(point.x, width - 1 - point.x, point.y, height - 1 - point.y);
    if (!selected || margin > selected.margin) selected = { point, margin };
  }
  return selected?.point;
}

export function clipSegmentToImage(start: Point, end: Point, width: number, height: number): { start: Point; end: Point } | undefined {
  if (width <= 0 || height <= 0) return undefined;
  const maxX = width - 1, maxY = height - 1;
  const dx = end.x - start.x, dy = end.y - start.y;
  let enter = 0, leave = 1;
  for (const [p, q] of [[-dx, start.x], [dx, maxX - start.x], [-dy, start.y], [dy, maxY - start.y]]) {
    if (p === 0) { if (q < 0) return undefined; continue; }
    const ratio = q / p;
    if (p < 0) enter = Math.max(enter, ratio); else leave = Math.min(leave, ratio);
    if (enter > leave) return undefined;
  }
  return { start: { x: clamp(start.x + dx * enter, 0, maxX), y: clamp(start.y + dy * enter, 0, maxY) },
    end: { x: clamp(start.x + dx * leave, 0, maxX), y: clamp(start.y + dy * leave, 0, maxY) } };
}

export function caliperCircleProfileWidthHandle(geometry: Extract<MeasurementGeometry, { type: 'circle' }>, angles: readonly number[],
  width: number, height: number): { point: Point; angle: number } | undefined {
  if (geometry.profileWidth === undefined || angles.length === 0 || width <= 0 || height <= 0) return undefined;
  const inner = Math.max(0, geometry.radius - Math.max(0, geometry.band ?? 0));
  const outer = geometry.radius + Math.max(0, geometry.band ?? 0);
  const halfWidth = Math.max(0, geometry.profileWidth) / 2;
  let selected: { point: Point; angle: number; margin: number } | undefined;
  for (const angle of angles) {
    if (!Number.isFinite(angle)) continue;
    const radians = normalizeCaliperAngle(angle) * Math.PI / 180;
    const direction = { x: Math.cos(radians), y: Math.sin(radians) };
    const normal = { x: -direction.y, y: direction.x };
    const center = geometry.center;
    const clipped = clipSegmentToImage(
      { x: center.x + inner * direction.x, y: center.y + inner * direction.y },
      { x: center.x + outer * direction.x, y: center.y + outer * direction.y }, width, height);
    if (!clipped) continue;
    const midpoint = { x: (clipped.start.x + clipped.end.x) / 2, y: (clipped.start.y + clipped.end.y) / 2 };
    const point = { x: clamp(midpoint.x + normal.x * halfWidth, 0, width - 1),
      y: clamp(midpoint.y + normal.y * halfWidth, 0, height - 1) };
    const margin = Math.min(point.x, width - 1 - point.x, point.y, height - 1 - point.y);
    if (!selected || margin > selected.margin) selected = { point, angle, margin };
  }
  return selected && { point: selected.point, angle: selected.angle };
}

export const flatten = (points?: readonly OverlayPoint[] | null) => (points ?? []).flatMap((p) => [p.x, p.y]);
export const brushPathCache = new WeakMap<readonly OverlayPoint[], number[]>();
export function brushPath(points: readonly OverlayPoint[]): number[] {
  const cached = brushPathCache.get(points);
  if (cached) return cached;
  const path = flatten(points);
  brushPathCache.set(points, path);
  return path;
}
export const MAX_POLYGON_PREVIEW_POINTS = 8192;
export const polygonPreviewCache = new WeakMap<readonly Point[], readonly Point[]>();
export function polygonPreviewPoints(points: readonly Point[]): readonly Point[] {
  if (points.length <= MAX_POLYGON_PREVIEW_POINTS) return points;
  const cached = polygonPreviewCache.get(points);
  if (cached) return cached;
  const stride = Math.ceil(points.length / MAX_POLYGON_PREVIEW_POINTS);
  const sampled = points.filter((_, index) => index % stride === 0);
  if (sampled.at(-1) !== points.at(-1)) sampled.push(points[points.length - 1]);
  polygonPreviewCache.set(points, sampled);
  return sampled;
}
export const maxInteractiveCircleRadius = (center: Point, width: number, height: number) => Math.max(
  Math.hypot(center.x, center.y), Math.hypot(width - center.x, center.y),
  Math.hypot(center.x, height - center.y), Math.hypot(width - center.x, height - center.y)
);
export const clampCircleCenter = (value: number, radius: number, limit: number) => radius * 2 <= limit
  ? clamp(value, radius, limit - radius)
  : clamp(value, 0, limit);
export function clampCaliperCircleCenter(center: Point, radius: number, band: number, width: number, height: number): Point {
  const searchableRadius = Math.min(10_000_000, Math.max(0, radius + band));
  const x = clamp(center.x, -searchableRadius, width + searchableRadius);
  const y = clamp(center.y, -searchableRadius, height + searchableRadius);
  const nearest = { x: clamp(x, 0, width), y: clamp(y, 0, height) };
  const dx = x - nearest.x;
  const dy = y - nearest.y;
  const distance = Math.hypot(dx, dy);
  if (distance <= searchableRadius || distance === 0) return { x, y };
  const fraction = searchableRadius / distance;
  return { x: nearest.x + dx * fraction, y: nearest.y + dy * fraction };
}

/** 控制点命中半径（屏幕像素），缩放后仍保持同样的手感 */
export const HANDLE_HIT = 10;
export const MAX_POLYGON_HANDLES = 32;
export const MIN_EDGE_HANDLE_SPAN = 18;
export const MAX_MEASUREMENT_COORDINATE = 10_000_000;

export function boundedMeasurementPoint(point: Point): Point {
  return { x: clamp(point.x, -MAX_MEASUREMENT_COORDINATE, MAX_MEASUREMENT_COORDINATE),
    y: clamp(point.y, -MAX_MEASUREMENT_COORDINATE, MAX_MEASUREMENT_COORDINATE) };
}

export function closestHandle<T extends string>(candidates: readonly (readonly [T, Point])[], screen: Point, view: ViewState): T | undefined {
  let selected: T | undefined;
  let closestDistance = Infinity;
  for (const [mode, point] of candidates) {
    const distance = Math.hypot(screen.x - (view.x + point.x * view.scale), screen.y - (view.y + point.y * view.scale));
    if (distance < closestDistance) { closestDistance = distance; selected = mode; }
  }
  return closestDistance <= HANDLE_HIT ? selected : undefined;
}

export function polygonHandleIndices(pointCount: number): number[] {
  const stride = Math.max(1, Math.ceil(pointCount / MAX_POLYGON_HANDLES));
  const indices = Array.from({ length: Math.ceil(pointCount / stride) }, (_, index) => index * stride);
  if (pointCount > 0 && indices.at(-1) !== pointCount - 1) indices.push(pointCount - 1);
  return indices;
}

export function caliperCircleArcHandles(geometry: Extract<MeasurementGeometry, { type: 'circle' }>, scale: number, width: number, height: number)
  : { start?: Point; end?: Point } | undefined {
  const start = geometry.angleStartDeg ?? -180;
  const { span, fullCircle } = circleArcSpan(start, geometry.angleEndDeg ?? 180);
  if (width <= 0 || height <= 0 || !Number.isFinite(start) || !Number.isFinite(span)) return undefined;
  const normalizedStart = normalizeCaliperAngle(start);
  const normalizedEnd = normalizedStart + span;
  const offset = (HANDLE_HIT + 4) / scale;
  const smallCircle = geometry.radius * scale < HANDLE_HIT * 3;
  const band = Math.max(0, geometry.band ?? 0);
  const innerRadius = Math.max(0, geometry.radius - band);
  const outerRadius = Math.max(0, geometry.radius + band);
  const atAngle = (angleDeg: number, radius: number): Point | undefined => {
    const radians = normalizeCaliperAngle(angleDeg) * Math.PI / 180;
    const direction = { x: Math.cos(radians), y: Math.sin(radians) };
    const desiredRadius = Math.max(0, radius);
    const clipped = clipSegmentToImage(
      { x: geometry.center.x + Math.min(desiredRadius, innerRadius) * direction.x,
        y: geometry.center.y + Math.min(desiredRadius, innerRadius) * direction.y },
      { x: geometry.center.x + Math.max(desiredRadius, outerRadius) * direction.x,
        y: geometry.center.y + Math.max(desiredRadius, outerRadius) * direction.y }, width, height);
    if (!clipped) return undefined;
    const radialDistance = (point: Point) => (point.x - geometry.center.x) * direction.x
      + (point.y - geometry.center.y) * direction.y;
    const visibleStart = radialDistance(clipped.start), visibleEnd = radialDistance(clipped.end);
    const handleRadius = clamp(desiredRadius, Math.min(visibleStart, visibleEnd), Math.max(visibleStart, visibleEnd));
    return { x: geometry.center.x + handleRadius * direction.x, y: geometry.center.y + handleRadius * direction.y };
  };
  if (fullCircle || span === 0) {
    const innerBandHandleRadius = Math.max(innerRadius, innerRadius * scale < HANDLE_HIT * 3 ? offset : 0);
    const outerBandHandleRadius = Math.max(outerRadius, outerRadius * scale < HANDLE_HIT * 3 ? offset * 2 : 0);
    const innerArcRadius = Math.max(0, innerBandHandleRadius - offset);
    const hasInnerSpace = innerArcRadius * scale >= HANDLE_HIT * 1.5;
    return {
      start: atAngle(normalizedStart, hasInnerSpace ? innerArcRadius : outerBandHandleRadius + offset),
      end: atAngle(normalizedEnd, hasInnerSpace ? outerBandHandleRadius + offset : outerBandHandleRadius + offset * 2)
    };
  }
  return {
    start: atAngle(normalizedStart, geometry.radius + (smallCircle ? offset : -offset)),
    end: atAngle(normalizedEnd, geometry.radius + offset * (smallCircle ? 2 : 1))
  };
}

export function caliperCircleBandHandles(geometry: Extract<MeasurementGeometry, { type: 'circle' }>, scale: number, width: number, height: number) {
  if (width <= 0 || height <= 0) return undefined;
  const band = Math.max(0, geometry.band ?? 0);
  const innerRadius = Math.max(0, geometry.radius - band);
  const outerRadius = geometry.radius + band;
  const offset = (HANDLE_HIT + 4) / scale;
  const innerHandleRadius = Math.max(innerRadius, innerRadius * scale < HANDLE_HIT * 3 ? offset : 0);
  const outerHandleRadius = Math.max(outerRadius, outerRadius * scale < HANDLE_HIT * 3 ? offset * 2 : 0);
  const atAngle = (angleDeg: number, radius: number): Point => {
    const radians = angleDeg * Math.PI / 180;
    return {
      x: clamp(geometry.center.x + Math.max(0, radius) * Math.cos(radians), 0, width),
      y: clamp(geometry.center.y + Math.max(0, radius) * Math.sin(radians), 0, height)
    };
  };
  return {
    inner: atAngle(-90, innerHandleRadius),
    outer: atAngle(90, outerHandleRadius)
  };
}

export function circleRadiusHandle(center: Point, radius: number, width: number, height: number): Point {
  const corners = [{ x: 0, y: 0 }, { x: width, y: 0 }, { x: 0, y: height }, { x: width, y: height }];
  const corner = corners.reduce((farthest, candidate) =>
    Math.hypot(candidate.x - center.x, candidate.y - center.y)
      >= Math.hypot(farthest.x - center.x, farthest.y - center.y) ? candidate : farthest);
  const dx = corner.x - center.x;
  const dy = corner.y - center.y;
  const cornerDistance = Math.hypot(dx, dy);
  if (cornerDistance === 0) return { x: 0, y: 0 };
  return {
    x: clamp(center.x + dx / cornerDistance * radius, 0, width),
    y: clamp(center.y + dy / cornerDistance * radius, 0, height)
  };
}

export function radialResizeCursor(center: Point, handle: Point): string {
  const dx = handle.x - center.x;
  const dy = handle.y - center.y;
  if (Math.abs(dx) > Math.abs(dy) * 1.5) return 'ew-resize';
  if (Math.abs(dy) > Math.abs(dx) * 1.5) return 'ns-resize';
  return dx * dy >= 0 ? 'nwse-resize' : 'nesw-resize';
}

export function normalizeRect(start: Point, end: Point): VisionRoi {
  return {
    type: 'Rectangle',
    x: Math.min(start.x, end.x),
    y: Math.min(start.y, end.y),
    width: Math.abs(end.x - start.x),
    height: Math.abs(end.y - start.y)
  };
}

/** ROI 包围盒（图像坐标），用于“定位选区”的视野适配 */
export function roiBounds(roi: VisionRoi) {
  if (roi.type === 'Rectangle') return { x: roi.x, y: roi.y, width: Math.max(1, roi.width), height: Math.max(1, roi.height) };
  if (roi.type === 'Circle') return { x: roi.x - roi.radius, y: roi.y - roi.radius, width: Math.max(1, roi.radius * 2), height: Math.max(1, roi.radius * 2) };
  if (roi.type === 'Brush') {
    let minX = Infinity; let minY = Infinity; let maxX = -Infinity; let maxY = -Infinity;
    roi.strokes.filter((stroke) => stroke.mode === 'Add').forEach((stroke) => stroke.points.forEach((point) => {
      const radius = stroke.width / 2;
      minX = Math.min(minX, point.x - radius); minY = Math.min(minY, point.y - radius);
      maxX = Math.max(maxX, point.x + radius); maxY = Math.max(maxY, point.y + radius);
    }));
    if (!Number.isFinite(minX)) return { x: 0, y: 0, width: 1, height: 1 };
    return { x: minX, y: minY, width: Math.max(1, maxX - minX), height: Math.max(1, maxY - minY) };
  }
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const point of roi.points) {
    minX = Math.min(minX, point.x);
    minY = Math.min(minY, point.y);
    maxX = Math.max(maxX, point.x);
    maxY = Math.max(maxY, point.y);
  }
  return { x: minX, y: minY, width: Math.max(1, maxX - minX), height: Math.max(1, maxY - minY) };
}

export function pointInsideRoi(roi: VisionRoi, point: Point): boolean {
  if (roi.type === 'Rectangle') return point.x >= roi.x && point.x <= roi.x + roi.width && point.y >= roi.y && point.y <= roi.y + roi.height;
  if (roi.type === 'Circle') return Math.hypot(point.x - roi.x, point.y - roi.y) <= roi.radius;
  if (roi.type === 'Brush') {
    let inside = false;
    for (const stroke of roi.strokes) {
      const radiusSquared = (stroke.width / 2) ** 2;
      const segmentCount = Math.max(1, stroke.points.length - 1);
      for (let index = 0; index < segmentCount; index++) {
        const start = stroke.points[stroke.points.length === 1 ? 0 : index];
        const end = stroke.points[stroke.points.length === 1 ? 0 : index + 1];
        const dx = end.x - start.x;
        const dy = end.y - start.y;
        const lengthSquared = dx * dx + dy * dy;
        const fraction = lengthSquared > 0
          ? clamp(((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared, 0, 1)
          : 0;
        const nearestX = start.x + fraction * dx;
        const nearestY = start.y + fraction * dy;
        if ((point.x - nearestX) ** 2 + (point.y - nearestY) ** 2 <= radiusSquared)
          inside = stroke.mode === 'Add';
      }
    }
    return inside;
  }
  return pointInsidePolygon(roi.points, point);
}

export function pointInsidePolygon(points: readonly Point[], point: Point): boolean {
  let inside = false;
  const pts = points;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const xi = pts[i].x; const yi = pts[i].y; const xj = pts[j].x; const yj = pts[j].y;
    if ((yi > point.y) !== (yj > point.y) && point.x < ((xj - xi) * (point.y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

export function brushRoiBounds(roi: Extract<VisionRoi, { type: 'Brush' }>): RoiMoveBounds | undefined {
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const stroke of roi.strokes) {
    if (stroke.mode !== 'Add') continue;
    const radius = stroke.width / 2;
    for (const point of stroke.points) {
      minX = Math.min(minX, point.x - radius);
      maxX = Math.max(maxX, point.x + radius);
      minY = Math.min(minY, point.y - radius);
      maxY = Math.max(maxY, point.y + radius);
    }
  }
  return Number.isFinite(minX) ? { minX, maxX, minY, maxY } : undefined;
}

export function polygonRoiBounds(roi: Extract<VisionRoi, { type: 'Polygon' }>): RoiMoveBounds | undefined {
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const point of roi.points) {
    minX = Math.min(minX, point.x);
    maxX = Math.max(maxX, point.x);
    minY = Math.min(minY, point.y);
    maxY = Math.max(maxY, point.y);
  }
  return Number.isFinite(minX) ? { minX, maxX, minY, maxY } : undefined;
}

export function clampRoiMove(bounds: RoiMoveBounds, delta: Point, maxW: number, maxH: number): Point {
  const clampAxis = (min: number, max: number, offset: number, limit: number) => {
    const minOffset = -min, maxOffset = limit - max;
    if (minOffset <= maxOffset) return clamp(offset, minOffset, maxOffset);
    const center = (min + max) / 2;
    return clamp(offset, -center, limit - center);
  };
  return {
    x: clampAxis(bounds.minX, bounds.maxX, delta.x, maxW),
    y: clampAxis(bounds.minY, bounds.maxY, delta.y, maxH)
  };
}

/** 按拖动模式编辑 ROI；矩形缩放受图像边界约束，圆形可越界，复杂选区整体移动会保留与画布的交集。 */
export function applyRoiDrag(origin: VisionRoi, mode: RoiDragMode, start: Point, current: Point, maxW: number, maxH: number,
  moveBounds?: RoiMoveBounds): VisionRoi | undefined {
  const dx = current.x - start.x;
  const dy = current.y - start.y;
  if (origin.type === 'Rectangle') {
    if (mode === 'move') {
      const { x: moveX, y: moveY } = clampRoiMove({
        minX: origin.x, maxX: origin.x + origin.width,
        minY: origin.y, maxY: origin.y + origin.height
      }, { x: dx, y: dy }, maxW, maxH);
      return { ...origin, x: origin.x + moveX, y: origin.y + moveY };
    }
    let x1 = origin.x; let y1 = origin.y; let x2 = origin.x + origin.width; let y2 = origin.y + origin.height;
    if (mode.includes('w')) x1 = clamp(origin.x + dx, 0, x2 - 2);
    if (mode.includes('n')) y1 = clamp(origin.y + dy, 0, y2 - 2);
    if (mode.includes('e')) x2 = clamp(origin.x + origin.width + dx, x1 + 2, maxW);
    if (mode.includes('s')) y2 = clamp(origin.y + origin.height + dy, y1 + 2, maxH);
    return { type: 'Rectangle', x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
  }
  if (origin.type === 'Circle') {
    if (mode === 'move') {
      return { ...origin, x: clampCircleCenter(origin.x + dx, origin.radius, maxW),
        y: clampCircleCenter(origin.y + dy, origin.radius, maxH) };
    }
    const startDistance = Math.hypot(start.x - origin.x, start.y - origin.y);
    const currentDistance = Math.hypot(current.x - origin.x, current.y - origin.y);
    return { ...origin, radius: clamp(origin.radius + currentDistance - startDistance, 1, 10_000_000) };
  }
  if (origin.type === 'Brush') {
    if (mode !== 'move') return undefined;
    const bounds = moveBounds ?? brushRoiBounds(origin);
    if (!bounds) return undefined;
    const { x: moveX, y: moveY } = clampRoiMove(bounds, { x: dx, y: dy }, maxW, maxH);
    return { type: 'Brush', strokes: origin.strokes.map(stroke => ({
      ...stroke, points: stroke.points.map(point => ({ x: point.x + moveX, y: point.y + moveY }))
    })) };
  }
  if (mode.startsWith('vertex:')) {
    const index = Number(mode.slice('vertex:'.length));
    if (!Number.isInteger(index) || index < 0 || index >= origin.points.length) return undefined;
    return { type: 'Polygon', points: origin.points.map((point, pointIndex) => pointIndex === index
      ? { x: clamp(current.x, 0, maxW), y: clamp(current.y, 0, maxH) }
      : point) };
  }
  if (mode === 'move') {
    const bounds = moveBounds ?? polygonRoiBounds(origin);
    if (!bounds) return undefined;
    const { x: moveX, y: moveY } = clampRoiMove(bounds, { x: dx, y: dy }, maxW, maxH);
    return { type: 'Polygon', points: origin.points.map(point => ({ x: point.x + moveX, y: point.y + moveY })) };
  }
  return undefined;
}
