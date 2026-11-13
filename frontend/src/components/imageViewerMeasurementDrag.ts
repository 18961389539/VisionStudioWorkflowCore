/**
 * 卡尺几何拖动的位置解算（纯函数）。
 *
 * 每个控制点只改自己那一两个字段，但边界条件各不相同：圆弧角要绕回、
 * 搜索带要夹住内外半径、裁剪过的搜索线整体平移时还得留一部分在图像内。
 * 这些规则和 React 无关，提出来才能单测「拖到极限」这类用 e2e 很难覆盖的情形。
 */
import { boundedMeasurementPoint, clamp, clampCaliperCircleCenter, clampCircleCenter } from './imageViewerUtils';
import { circleArcSpan, effectiveSampleWidth, normalizeCaliperAngle, normalizeCaliperCircleArc } from '../caliperCircleSampling';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { MeasurementDragMode, Point } from './imageViewerTypes';

export function applyMeasurementDrag({
  origin,
  mode,
  start,
  pointer,
  current,
  profileWidthAngle,
  imageWidth,
  imageHeight
}: {
  origin: MeasurementGeometry;
  mode: MeasurementDragMode;
  /** 手势起始点（图像坐标）。 */
  start: Point;
  /** 原始指针位置（图像坐标，未夹取）。搜索线端点允许落在图像外。 */
  pointer: Point;
  /** 当前指针位置，已夹到图像范围内。 */
  current: Point;
  /** 圆型「投影宽度」控制点的法线角度；缺省时退回圆弧起始角。 */
  profileWidthAngle?: number;
  imageWidth: number;
  imageHeight: number;
}): MeasurementGeometry | undefined {
  if (origin.type === 'line') {
    if (mode === 'start' || mode === 'end') {
      return { ...origin, [mode]: boundedMeasurementPoint(pointer) };
    }
    if (mode === 'profileWidth') {
      const dx = origin.end.x - origin.start.x, dy = origin.end.y - origin.start.y;
      const length = Math.hypot(dx, dy);
      if (length <= 0) return undefined;
      const normal = { x: -dy / length, y: dx / length };
      const delta = (current.x - start.x) * normal.x + (current.y - start.y) * normal.y;
      return { ...origin, profileWidth: effectiveSampleWidth(clamp((origin.profileWidth ?? 1) + 2 * delta, 1, 99)) };
    }
    if (mode === 'profileStart' || mode === 'profileEnd') {
      const dx = origin.end.x - origin.start.x, dy = origin.end.y - origin.start.y;
      const length = Math.hypot(dx, dy);
      if (length <= 0) return undefined;
      const tangent = { x: -dy / length, y: dx / length };
      const delta = (current.x - start.x) * tangent.x + (current.y - start.y) * tangent.y;
      const direction = mode === 'profileStart' ? -1 : 1;
      const initialSpan = origin.profileSpan && origin.profileSpan > 0 ? origin.profileSpan : length;
      const profileSpan = Math.abs(delta) < 1e-9 ? origin.profileSpan
        : clamp(initialSpan + direction * 2 * delta, 1, 10_000);
      return { ...origin, profileSpan };
    }
    // Keep clipped search lines draggable: constrain their bounds to keep some
    // overlap with the image, while allowing endpoints to remain outside.
    const minX = Math.min(origin.start.x, origin.end.x), maxX = Math.max(origin.start.x, origin.end.x);
    const minY = Math.min(origin.start.y, origin.end.y), maxY = Math.max(origin.start.y, origin.end.y);
    const dx = clamp(current.x - start.x, -maxX, imageWidth - minX);
    const dy = clamp(current.y - start.y, -maxY, imageHeight - minY);
    return { ...origin, start: { x: origin.start.x + dx, y: origin.start.y + dy }, end: { x: origin.end.x + dx, y: origin.end.y + dy } };
  }

  if (mode === 'profileWidth') {
    const angle = profileWidthAngle ?? origin.angleStartDeg ?? -180;
    const radians = normalizeCaliperAngle(angle) * Math.PI / 180;
    const normal = { x: -Math.sin(radians), y: Math.cos(radians) };
    const delta = (current.x - start.x) * normal.x + (current.y - start.y) * normal.y;
    return { ...origin, profileWidth: effectiveSampleWidth(clamp((origin.profileWidth ?? 1) + 2 * delta, 1, 99)) };
  }
  if (mode === 'center' || mode === 'moveCircle') {
    const center = mode === 'center' ? current : {
      x: origin.center.x + current.x - start.x,
      y: origin.center.y + current.y - start.y
    };
    return { ...origin, center: mode === 'center' ? {
      x: clampCircleCenter(center.x, origin.radius, imageWidth),
      y: clampCircleCenter(center.y, origin.radius, imageHeight)
    } : clampCaliperCircleCenter(center, origin.radius, origin.band ?? 0, imageWidth, imageHeight) };
  }
  if (mode === 'radius') {
    const startDistance = Math.hypot(start.x - origin.center.x, start.y - origin.center.y);
    const currentDistance = Math.hypot(current.x - origin.center.x, current.y - origin.center.y);
    return { ...origin, radius: clamp(origin.radius + currentDistance - startDistance, 1, 10_000_000) };
  }
  if (mode === 'angleStart' || mode === 'angleEnd') {
    const rawStartAngle = origin.angleStartDeg ?? -180;
    const rawEndAngle = origin.angleEndDeg ?? 180;
    const normalizedArc = normalizeCaliperCircleArc(rawStartAngle, rawEndAngle);
    const startAngle = Math.abs(rawStartAngle) <= 1_000_000 ? rawStartAngle : normalizedArc.start;
    const arcEndAngle = startAngle + circleArcSpan(rawStartAngle, rawEndAngle).span;
    const pointerAngle = Math.atan2(current.y - origin.center.y, current.x - origin.center.x) * 180 / Math.PI;
    const lower = mode === 'angleStart' ? arcEndAngle - 359.5 : startAngle + 0.5;
    const upper = mode === 'angleStart' ? arcEndAngle - 0.5 : startAngle + 359.5;
    let adjustedAngle = pointerAngle + 360 * Math.round(((lower + upper) / 2 - pointerAngle) / 360);
    if (adjustedAngle < lower) adjustedAngle += 360;
    if (adjustedAngle > upper) adjustedAngle -= 360;
    adjustedAngle = clamp(adjustedAngle, lower, upper);
    return mode === 'angleStart' ? { ...origin, angleStartDeg: adjustedAngle } : { ...origin, angleEndDeg: adjustedAngle };
  }
  if (mode === 'innerBand' || mode === 'outerBand') {
    const initialBand = Math.max(0, Math.min(500, origin.band ?? 0));
    const startDistance = Math.hypot(start.x - origin.center.x, start.y - origin.center.y);
    const currentDistance = Math.hypot(current.x - origin.center.x, current.y - origin.center.y);
    const initialInner = Math.max(0, origin.radius - initialBand);
    const band = mode === 'innerBand'
      ? clamp(origin.radius - Math.max(0, initialInner + currentDistance - startDistance), 1, 500)
      : clamp(initialBand + currentDistance - startDistance, 1, 500);
    return { ...origin, band };
  }
  return undefined;
}
