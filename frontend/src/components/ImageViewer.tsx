import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Button, InputNumber, Segmented, Select, Space, Switch, Tooltip } from 'antd';
import { Arrow, Circle, Group, Image as KonvaImage, Layer, Line, Rect, Stage, Text } from 'react-konva';
import type { OverlayPoint, VisionOverlay, VisionRoi, VisionRoiBrushStroke } from '../types';
import { parameterFingerprint, roiConfigurationIssue } from '../nodeConfigurationState';
import { createPixelBuffer, readPixel } from '../imagePixels';
import { buildCaliperCircleAngles, circleArcSpan, effectiveSampleWidth, normalizeCaliperAngle, normalizeCaliperCircleArc } from '../caliperCircleSampling';
import { circleArcThroughThreePoints, circleThroughThreePoints } from '../circleConstruction';
import type { CaliperSpokeDiagnostic } from '../caliperCircleDiagnostics';

export { buildCaliperCircleAngles } from '../caliperCircleSampling';

type ToolMode = 'pan' | 'rect' | 'circle' | 'polygon' | 'brush' | 'eraser';
type CircleDrawMode = 'center' | 'diameter' | 'threePoints' | 'threePointArc';
type ViewState = { scale: number; x: number; y: number };
type Point = { x: number; y: number };
type RoiMoveBounds = { minX: number; maxX: number; minY: number; maxY: number };
type RoiDragMode = 'move' | 'nw' | 'ne' | 'sw' | 'se' | 'e' | 'w' | 'n' | 's' | `vertex:${number}`;
type MeasurementDragMode = 'start' | 'end' | 'profileStart' | 'profileEnd' | 'profileWidth' | 'moveLine' | 'center' | 'moveCircle' | 'radius' | 'angleStart' | 'angleEnd' | 'innerBand' | 'outerBand';

export type MeasurementGeometry =
  | { type: 'line'; start: Point; end: Point; profileSpan?: number; profileCount?: number; profileWidth?: number; direction?: string }
  | { type: 'circle'; center: Point; radius: number; band?: number; angleStartDeg?: number; angleEndDeg?: number;
      caliperCount?: number; samplingMode?: string; angleStepDeg?: number; profileWidth?: number;
      searchDirection?: string; centerEditable?: boolean; radiusEditable?: boolean };

type CaliperDiagnosticSelection = {
  center: Point; radius: number; spokes: CaliperSpokeDiagnostic[]; index: number; onSelect: (index: number) => void;
};

function nearestDiagnosticSpoke(selection: CaliperDiagnosticSelection, point: Point, scale: number) {
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

function CaliperDiagnosticShape({ selection, scale }: { selection: CaliperDiagnosticSelection; scale: number }) {
  const selected = selection.spokes[selection.index];
  const start = selected?.profileStart ?? selected?.start, end = selected?.profileEnd ?? selected?.end;
  return <Group listening={false}>
    <Circle x={selection.center.x} y={selection.center.y} radius={selection.radius} stroke="#597ef7" strokeWidth={1 / scale} opacity={.5} />
    {selection.spokes.map(spoke => <Line key={spoke.index}
      points={[(spoke.profileStart ?? spoke.start).x, (spoke.profileStart ?? spoke.start).y,
        (spoke.profileEnd ?? spoke.end).x, (spoke.profileEnd ?? spoke.end).y]}
      stroke={spoke.status === 'used' ? '#38bdf8' : '#bfbfbf'} strokeWidth={1 / scale} opacity={.7} />)}
    {selected && start && end && <>
      <Line points={[start.x, start.y, end.x, end.y]} stroke="#101b2e" strokeWidth={6 / scale} />
      <Arrow name="caliper-diagnostic-selected-spoke" points={[start.x, start.y, end.x, end.y]}
        stroke="#ffd666" fill="#ffd666" strokeWidth={2.5 / scale} pointerLength={6 / scale} pointerWidth={5 / scale} />
      <Text x={end.x + 8 / scale} y={end.y - 15 / scale} text={`#${selected.index + 1}`} fontSize={13 / scale}
        fontStyle="bold" fill="#ffd666" stroke="#101b2e" strokeWidth={3 / scale} fillAfterStrokeEnabled />
      {selected.edgePoint && <Circle name="caliper-diagnostic-selected-edge" x={selected.edgePoint.x} y={selected.edgePoint.y}
        radius={5 / scale} fill={selected.status === 'used' ? '#52c41a' : '#faad14'} stroke="#fff" strokeWidth={1.5 / scale} />}
    </>}
  </Group>;
}

/** 像素化显示偏好：默认开启——放大时按真实像素方块渲染（最近邻），便于逐像素核对；
 *  关闭则使用画布默认的平滑插值（缩放总览时更顺眼）。与其它显示偏好一样本地持久化。 */
const PIXELATED_KEY = 'visionstudio.viewer-pixelated';
const readPixelated = () => {
  try { return window.localStorage.getItem(PIXELATED_KEY) !== '0'; } catch { return true; }
};
const persistPixelated = (value: boolean) => {
  try { window.localStorage.setItem(PIXELATED_KEY, value ? '1' : '0'); } catch { /* 忽略存储失败 */ }
};

const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));
function caliperLineProfileSpanHandles(geometry: Extract<MeasurementGeometry, { type: 'line' }>,
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
function caliperLineProfileWidthHandle(geometry: Extract<MeasurementGeometry, { type: 'line' }>,
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

function clipSegmentToImage(start: Point, end: Point, width: number, height: number): { start: Point; end: Point } | undefined {
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

function caliperCircleProfileWidthHandle(geometry: Extract<MeasurementGeometry, { type: 'circle' }>, angles: readonly number[],
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
const flatten = (points?: readonly OverlayPoint[] | null) => (points ?? []).flatMap((p) => [p.x, p.y]);
const brushPathCache = new WeakMap<readonly OverlayPoint[], number[]>();
function brushPath(points: readonly OverlayPoint[]): number[] {
  const cached = brushPathCache.get(points);
  if (cached) return cached;
  const path = flatten(points);
  brushPathCache.set(points, path);
  return path;
}
const MAX_POLYGON_PREVIEW_POINTS = 8192;
const polygonPreviewCache = new WeakMap<readonly Point[], readonly Point[]>();
function polygonPreviewPoints(points: readonly Point[]): readonly Point[] {
  if (points.length <= MAX_POLYGON_PREVIEW_POINTS) return points;
  const cached = polygonPreviewCache.get(points);
  if (cached) return cached;
  const stride = Math.ceil(points.length / MAX_POLYGON_PREVIEW_POINTS);
  const sampled = points.filter((_, index) => index % stride === 0);
  if (sampled.at(-1) !== points.at(-1)) sampled.push(points[points.length - 1]);
  polygonPreviewCache.set(points, sampled);
  return sampled;
}
const maxInteractiveCircleRadius = (center: Point, width: number, height: number) => Math.max(
  Math.hypot(center.x, center.y), Math.hypot(width - center.x, center.y),
  Math.hypot(center.x, height - center.y), Math.hypot(width - center.x, height - center.y)
);
const clampCircleCenter = (value: number, radius: number, limit: number) => radius * 2 <= limit
  ? clamp(value, radius, limit - radius)
  : clamp(value, 0, limit);
function clampCaliperCircleCenter(center: Point, radius: number, band: number, width: number, height: number): Point {
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
const HANDLE_HIT = 10;
const MAX_POLYGON_HANDLES = 32;
const MIN_EDGE_HANDLE_SPAN = 18;
const MAX_MEASUREMENT_COORDINATE = 10_000_000;

function boundedMeasurementPoint(point: Point): Point {
  return { x: clamp(point.x, -MAX_MEASUREMENT_COORDINATE, MAX_MEASUREMENT_COORDINATE),
    y: clamp(point.y, -MAX_MEASUREMENT_COORDINATE, MAX_MEASUREMENT_COORDINATE) };
}

function closestHandle<T extends string>(candidates: readonly (readonly [T, Point])[], screen: Point, view: ViewState): T | undefined {
  let selected: T | undefined;
  let closestDistance = Infinity;
  for (const [mode, point] of candidates) {
    const distance = Math.hypot(screen.x - (view.x + point.x * view.scale), screen.y - (view.y + point.y * view.scale));
    if (distance < closestDistance) { closestDistance = distance; selected = mode; }
  }
  return closestDistance <= HANDLE_HIT ? selected : undefined;
}

function polygonHandleIndices(pointCount: number): number[] {
  const stride = Math.max(1, Math.ceil(pointCount / MAX_POLYGON_HANDLES));
  const indices = Array.from({ length: Math.ceil(pointCount / stride) }, (_, index) => index * stride);
  if (pointCount > 0 && indices.at(-1) !== pointCount - 1) indices.push(pointCount - 1);
  return indices;
}

function caliperCircleArcHandles(geometry: Extract<MeasurementGeometry, { type: 'circle' }>, scale: number, width: number, height: number)
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
    // Coincident arc endpoints need distinct handles. Keep them clear of both radial-band
    // controls and the center marker so a full circle can be changed into an arc directly.
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

function caliperCircleBandHandles(geometry: Extract<MeasurementGeometry, { type: 'circle' }>, scale: number, width: number, height: number) {
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

function circleRadiusHandle(center: Point, radius: number, width: number, height: number): Point {
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

function radialResizeCursor(center: Point, handle: Point): string {
  const dx = handle.x - center.x;
  const dy = handle.y - center.y;
  if (Math.abs(dx) > Math.abs(dy) * 1.5) return 'ew-resize';
  if (Math.abs(dy) > Math.abs(dx) * 1.5) return 'ns-resize';
  return dx * dy >= 0 ? 'nwse-resize' : 'nesw-resize';
}

function useHtmlImage(url?: string) {
  const [loaded, setLoaded] = useState<{ url: string; image: HTMLImageElement }>();
  const [failedUrl, setFailedUrl] = useState<string>();
  useEffect(() => {
    if (!url) { setLoaded(undefined); setFailedUrl(undefined); return; }
    setFailedUrl(undefined);
    const img = new window.Image();
    img.onload = () => setLoaded({ url, image: img });
    img.onerror = () => { setLoaded(undefined); setFailedUrl(url); };
    img.src = url;
    return () => { img.onload = null; img.onerror = null; };
  }, [url]);
  return { image: loaded?.url === url ? loaded?.image : undefined, failed: Boolean(url && failedUrl === url) };
}

function normalizeRect(start: Point, end: Point): VisionRoi {
  return {
    type: 'Rectangle',
    x: Math.min(start.x, end.x),
    y: Math.min(start.y, end.y),
    width: Math.abs(end.x - start.x),
    height: Math.abs(end.y - start.y)
  };
}

/** ROI 包围盒（图像坐标），用于“定位选区”的视野适配 */
function roiBounds(roi: VisionRoi) {
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

function pointInsideRoi(roi: VisionRoi, point: Point): boolean {
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
  // Drawing may downsample a very large contour for responsiveness, but selection
  // hit-testing must use the actual ROI. Otherwise thin features omitted from the
  // preview sample cannot be grabbed to move the region used by execution.
  return pointInsidePolygon(roi.points, point);
}

function pointInsidePolygon(points: readonly Point[], point: Point): boolean {
  let inside = false;
  const pts = points;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const xi = pts[i].x; const yi = pts[i].y; const xj = pts[j].x; const yj = pts[j].y;
    if ((yi > point.y) !== (yj > point.y) && point.x < ((xj - xi) * (point.y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

function brushRoiBounds(roi: Extract<VisionRoi, { type: 'Brush' }>): RoiMoveBounds | undefined {
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const stroke of roi.strokes) {
    // Erase strokes can only remove selected pixels; a remote eraser outside every add stroke
    // must not shrink the range in which the effective ROI can be moved.
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

function polygonRoiBounds(roi: Extract<VisionRoi, { type: 'Polygon' }>): RoiMoveBounds | undefined {
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const point of roi.points) {
    minX = Math.min(minX, point.x);
    maxX = Math.max(maxX, point.x);
    minY = Math.min(minY, point.y);
    maxY = Math.max(maxY, point.y);
  }
  return Number.isFinite(minX) ? { minX, maxX, minY, maxY } : undefined;
}

function clampRoiMove(bounds: RoiMoveBounds, delta: Point, maxW: number, maxH: number): Point {
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
function applyRoiDrag(origin: VisionRoi, mode: RoiDragMode, start: Point, current: Point, maxW: number, maxH: number,
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

function OverlayShape({ overlay, scale }: { overlay: VisionOverlay; scale: number }) {
  const stroke = overlay.stroke ?? '#52c41a';
  const strokeWidth = (overlay.strokeWidth ?? 2) / scale;
  const x = overlay.x ?? 0;
  const y = overlay.y ?? 0;
  switch (overlay.type) {
    case 'Point': {
      const arm = 9 / scale;
      return <Group listening={false}>
        <Line points={[x - arm, y, x + arm, y]} stroke={stroke} strokeWidth={strokeWidth} />
        <Line points={[x, y - arm, x, y + arm]} stroke={stroke} strokeWidth={strokeWidth} />
      </Group>;
    }
    case 'Line':
      return <Line listening={false} points={[x, y, overlay.x2 ?? x, overlay.y2 ?? y]} stroke={stroke} strokeWidth={strokeWidth} />;
    case 'Circle':
      return <Circle listening={false} x={x} y={y} radius={overlay.radius ?? 0} stroke={stroke} strokeWidth={strokeWidth} fill={overlay.fill ?? undefined} />;
    case 'Rectangle':
      return <Rect listening={false} x={x} y={y} width={overlay.width ?? 0} height={overlay.height ?? 0} rotation={overlay.angleDeg ?? 0} stroke={stroke} strokeWidth={strokeWidth} fill={overlay.fill ?? undefined} />;
    case 'Polyline':
      return <Line listening={false} points={flatten(overlay.points)} stroke={stroke} strokeWidth={strokeWidth} lineCap="round" lineJoin="round" />;
    case 'Polygon':
    case 'Contour':
      return <Line listening={false} points={flatten(overlay.points)} closed={true} stroke={stroke} strokeWidth={strokeWidth} fill={overlay.fill ?? undefined} />;
    case 'Text':
      return <Text listening={false} x={x} y={y} text={overlay.label ?? ''} fill={stroke} fontSize={14 / scale} fontFamily="monospace" />;
    default:
      return null;
  }
}

const BrushStrokeLine = memo(function BrushStrokeLine({ item, stroke }: { item: VisionRoiBrushStroke; stroke: string }) {
  return <Line name={`roi-brush-stroke-${item.mode.toLowerCase()}`} points={brushPath(item.points)} stroke={stroke}
    strokeWidth={item.width} lineCap="round" lineJoin="round" opacity={item.mode === 'Add' ? 0.28 : 1}
    globalCompositeOperation={item.mode === 'Erase' ? 'destination-out' : 'source-over'} />;
});

const BrushStrokeList = memo(function BrushStrokeList({ strokes, stroke }: {
  strokes: readonly VisionRoiBrushStroke[]; stroke: string;
}) {
  return <>{strokes.map((item, index) => <BrushStrokeLine key={index} item={item} stroke={stroke} />)}</>;
});

const BrushRoiShape = memo(function BrushRoiShape({ strokes, activeStroke, stroke = '#ff4d4f' }: {
  strokes: readonly VisionRoiBrushStroke[]; activeStroke?: VisionRoiBrushStroke; stroke?: string;
}) {
  return <Group listening={false}>
    <BrushStrokeList strokes={strokes} stroke={stroke} />
    {activeStroke && <BrushStrokeLine item={activeStroke} stroke={stroke} />}
  </Group>;
});

function RoiShape({ roi, scale, draft = false }: { roi?: VisionRoi; scale: number; draft?: boolean }) {
  if (!roi) return null;
  const stroke = draft ? '#ffd666' : '#ff4d4f';
  const sw = 2 / scale;
  if (roi.type === 'Rectangle')
    return <Rect listening={false} x={roi.x} y={roi.y} width={roi.width} height={roi.height} stroke={stroke} strokeWidth={sw} dash={[7 / scale, 4 / scale]} />;
  if (roi.type === 'Circle')
    return <Circle listening={false} x={roi.x} y={roi.y} radius={roi.radius} stroke={stroke} strokeWidth={sw} dash={[7 / scale, 4 / scale]} />;
  if (roi.type === 'Brush') return <BrushRoiShape strokes={roi.strokes} stroke={stroke} />;
  const previewPoints = polygonPreviewPoints(roi.points);
  return <Line listening={false} points={flatten(previewPoints)} closed={roi.points.length >= 3 && !draft} stroke={stroke} strokeWidth={sw} dash={[7 / scale, 4 / scale]} />;
}

/** 选区控制点：大型多边形均匀抽样，避免编辑复杂 ROI 时创建数十万个控制点。 */
function RoiHandles({ roi, scale, imageWidth, imageHeight }: { roi: VisionRoi; scale: number; imageWidth: number; imageHeight: number }) {
  if (roi.type === 'Brush') return null;
  const size = 9 / scale;
  let handles: { index: number; point: Point }[];
  if (roi.type === 'Rectangle') {
    handles = [
      { index: 0, point: { x: roi.x, y: roi.y } }, { index: 1, point: { x: roi.x + roi.width, y: roi.y } },
      { index: 2, point: { x: roi.x, y: roi.y + roi.height } }, { index: 3, point: { x: roi.x + roi.width, y: roi.y + roi.height } }
    ];
    if (roi.height * scale >= MIN_EDGE_HANDLE_SPAN) handles.push(
      { index: 4, point: { x: roi.x, y: roi.y + roi.height / 2 } },
      { index: 5, point: { x: roi.x + roi.width, y: roi.y + roi.height / 2 } });
    if (roi.width * scale >= MIN_EDGE_HANDLE_SPAN) handles.push(
      { index: 6, point: { x: roi.x + roi.width / 2, y: roi.y } },
      { index: 7, point: { x: roi.x + roi.width / 2, y: roi.y + roi.height } });
  } else if (roi.type === 'Circle') {
    handles = [{ index: 0, point: circleRadiusHandle({ x: roi.x, y: roi.y }, roi.radius, imageWidth, imageHeight) }];
  } else {
    handles = polygonHandleIndices(roi.points.length).map(index => ({ index, point: roi.points[index] }));
  }
  if (handles.length === 0) return null;
  return <Group listening={false}>
    {handles.map(({ index, point }) => (
      <Rect key={index} name="roi-handle" x={point.x - size / 2} y={point.y - size / 2} width={size} height={size} fill="#ff4d4f" stroke="#ffffff" strokeWidth={1 / scale} />
    ))}
  </Group>;
}

function MeasurementGeometryShape({ geometry, scale, editable, imageWidth, imageHeight }: {
  geometry: MeasurementGeometry; scale: number; editable: boolean; imageWidth: number; imageHeight: number
}) {
  const handleRadius = 5 / scale;
  if (geometry.type === 'line') {
    const searchFromEnd = geometry.direction?.toLowerCase() === 'firstfromend';
    const searchStrongest = !geometry.direction || geometry.direction.toLowerCase() === 'strongest';
    const searchStart = searchFromEnd ? geometry.end : geometry.start;
    const searchEnd = searchFromEnd ? geometry.start : geometry.end;
    const dx = searchEnd.x - searchStart.x, dy = searchEnd.y - searchStart.y;
    const length = Math.hypot(dx, dy);
    const directionX = length > 0 ? dx / length : 1, directionY = length > 0 ? dy / length : 0;
    const endpointDirectionX = searchFromEnd ? -directionX : directionX;
    const endpointDirectionY = searchFromEnd ? -directionY : directionY;
    const labelOffset = 7 / scale;
    const labelNormalX = -endpointDirectionY * labelOffset, labelNormalY = endpointDirectionX * labelOffset;
    const count = geometry.profileCount === undefined ? 0 : Math.round(clamp(geometry.profileCount, 3, 101));
    const span = geometry.profileSpan && geometry.profileSpan > 0 ? geometry.profileSpan : length;
    const tangent = length > 0 ? { x: -dy / length, y: dx / length } : { x: 0, y: 0 };
    const apertureCount = geometry.profileWidth === undefined ? 0 : Math.max(1, count);
    const halfProfileWidth = Math.max(0, geometry.profileWidth ?? 0) / 2;
    const profileHandles = editable && count >= 3 ? caliperLineProfileSpanHandles(geometry, imageWidth, imageHeight) : undefined;
    const profileWidthHandle = editable ? caliperLineProfileWidthHandle(geometry, imageWidth, imageHeight) : undefined;
    return <Group listening={false}>
      {apertureCount > 0 && Array.from({ length: apertureCount }, (_, index) => {
        const offset = apertureCount === 1 ? 0 : (index / (apertureCount - 1) - 0.5) * span;
        const offsetX = tangent.x * offset, offsetY = tangent.y * offset;
        const halfX = tangent.x * halfProfileWidth, halfY = tangent.y * halfProfileWidth;
        return <Line key={`profile-band-${index}`} name={`measurement-profile-band-${index}`} closed
          points={[geometry.start.x + offsetX + halfX, geometry.start.y + offsetY + halfY,
            geometry.end.x + offsetX + halfX, geometry.end.y + offsetY + halfY,
            geometry.end.x + offsetX - halfX, geometry.end.y + offsetY - halfY,
            geometry.start.x + offsetX - halfX, geometry.start.y + offsetY - halfY]}
          fill="#91d5ff" opacity={0.16} stroke="#91d5ff" strokeWidth={0.5 / scale} />;
      })}
      {count > 0 && Array.from({ length: count }, (_, index) => {
        const offset = count === 1 ? 0 : (index / (count - 1) - 0.5) * span;
        const x = tangent.x * offset, y = tangent.y * offset;
        return <Line key={index} points={[geometry.start.x + x, geometry.start.y + y, geometry.end.x + x, geometry.end.y + y]}
          stroke="#91d5ff" opacity={0.6} strokeWidth={0.8 / scale} />;
      })}
      <Arrow points={[searchStart.x, searchStart.y, searchEnd.x, searchEnd.y]} stroke="#fa8c16" fill="#fa8c16"
        pointerAtBeginning={searchStrongest} pointerLength={8 / scale} pointerWidth={8 / scale} strokeWidth={2.5 / scale} />
      <Text x={geometry.start.x - endpointDirectionX * 15 / scale + labelNormalX} y={geometry.start.y - endpointDirectionY * 15 / scale + labelNormalY} text="起点" fill="#1677ff"
        fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} fillAfterStrokeEnabled listening={false} />
      <Text x={geometry.end.x + endpointDirectionX * 7 / scale + labelNormalX} y={geometry.end.y + endpointDirectionY * 7 / scale + labelNormalY} text="终点" fill="#722ed1"
        fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} fillAfterStrokeEnabled listening={false} />
      {editable && <>
        <Circle x={geometry.start.x} y={geometry.start.y} radius={handleRadius} fill="#1677ff" stroke="#fff" strokeWidth={1.5 / scale} />
        <Circle x={geometry.end.x} y={geometry.end.y} radius={handleRadius} fill="#722ed1" stroke="#fff" strokeWidth={1.5 / scale} />
        {profileHandles && <>
          <Circle name="measurement-profile-span-start" x={profileHandles.start.x} y={profileHandles.start.y} radius={handleRadius}
            fill="#13c2c2" stroke="#fff" strokeWidth={1.5 / scale} />
          <Circle name="measurement-profile-span-end" x={profileHandles.end.x} y={profileHandles.end.y} radius={handleRadius}
            fill="#13c2c2" stroke="#fff" strokeWidth={1.5 / scale} />
        </>}
        {profileWidthHandle && <Circle name="measurement-profile-width" x={profileWidthHandle.x} y={profileWidthHandle.y} radius={handleRadius}
          fill="#eb2f96" stroke="#fff" strokeWidth={1.5 / scale} />}
      </>}
    </Group>;
  }

  const band = Math.max(0, geometry.band ?? 0);
  const inner = Math.max(0, geometry.radius - band);
  const outer = geometry.radius + band;
  const start = geometry.angleStartDeg ?? -180;
  const end = geometry.angleEndDeg ?? 180;
  const { span, fullCircle } = circleArcSpan(start, end);
  const angles = buildCaliperCircleAngles(geometry);
  const profileWidthHandle = editable ? caliperCircleProfileWidthHandle(geometry, angles, imageWidth, imageHeight) : undefined;
  const arcHandles = editable ? caliperCircleArcHandles(geometry, scale, imageWidth, imageHeight) : undefined;
  const bandHandles = editable ? caliperCircleBandHandles(geometry, scale, imageWidth, imageHeight) : undefined;
  const radiusHandle = editable && geometry.radiusEditable !== false
    ? circleRadiusHandle(geometry.center, geometry.radius, imageWidth, imageHeight) : undefined;
  const boundary = (angleDeg: number) => {
    const radians = normalizeCaliperAngle(angleDeg) * Math.PI / 180;
    return [geometry.center.x + inner * Math.cos(radians), geometry.center.y + inner * Math.sin(radians),
      geometry.center.x + outer * Math.cos(radians), geometry.center.y + outer * Math.sin(radians)];
  };
  const firstSpoke = boundary(angles[0] ?? start);
  const firstSpokeAngle = normalizeCaliperAngle(angles[0] ?? start) * Math.PI / 180;
  const searchFromEnd = geometry.searchDirection?.toLowerCase() === 'firstfromend';
  const searchStrongest = !geometry.searchDirection || geometry.searchDirection.toLowerCase() === 'strongest';
  const firstSearchStart = searchFromEnd ? firstSpoke.slice(2, 4) : firstSpoke.slice(0, 2);
  const firstSearchEnd = searchFromEnd ? firstSpoke.slice(0, 2) : firstSpoke.slice(2, 4);
  const searchDirectionX = searchFromEnd ? -Math.cos(firstSpokeAngle) : Math.cos(firstSpokeAngle);
  const searchDirectionY = searchFromEnd ? -Math.sin(firstSpokeAngle) : Math.sin(firstSpokeAngle);
  const profileHalfWidth = Math.max(0, geometry.profileWidth ?? 0) / 2;
  const labelOffset = 7 / scale;
  const labelNormalX = -searchDirectionY * labelOffset;
  const labelNormalY = searchDirectionX * labelOffset;
  return <Group listening={false}>
    {profileHalfWidth > 0 && angles.map((angleDeg, index) => {
      const radians = normalizeCaliperAngle(angleDeg) * Math.PI / 180;
      const direction = { x: Math.cos(radians), y: Math.sin(radians) };
      const normal = { x: -direction.y, y: direction.x };
      const innerPoint = { x: geometry.center.x + inner * direction.x, y: geometry.center.y + inner * direction.y };
      const outerPoint = { x: geometry.center.x + outer * direction.x, y: geometry.center.y + outer * direction.y };
      const offsetX = normal.x * profileHalfWidth, offsetY = normal.y * profileHalfWidth;
      return <Line key={`profile-band-${index}`} name={`measurement-circle-profile-band-${index}`} closed
        points={[innerPoint.x + offsetX, innerPoint.y + offsetY,
          outerPoint.x + offsetX, outerPoint.y + offsetY,
          outerPoint.x - offsetX, outerPoint.y - offsetY,
          innerPoint.x - offsetX, innerPoint.y - offsetY]}
        fill="#91d5ff" opacity={0.1} stroke="#91d5ff" strokeWidth={0.35 / scale} />;
    })}
    {band > 0 && <>
      <Circle x={geometry.center.x} y={geometry.center.y} radius={inner} stroke="#fa8c16" strokeWidth={1 / scale} dash={[4 / scale, 4 / scale]} />
      <Circle x={geometry.center.x} y={geometry.center.y} radius={outer} stroke="#fa8c16" strokeWidth={1 / scale} dash={[4 / scale, 4 / scale]} />
    </>}
    <Circle x={geometry.center.x} y={geometry.center.y} radius={geometry.radius} stroke="#fa8c16"
      strokeWidth={(fullCircle ? 2.5 : 1) / scale} opacity={fullCircle ? 1 : 0.35} />
    {!fullCircle && span > 0 && <Line name="caliper-circle-selected-arc"
      points={Array.from({ length: Math.max(2, Math.ceil(span / 3)) + 1 }, (_, index) => {
        const radians = (normalizeCaliperAngle(start) + span * index / Math.max(2, Math.ceil(span / 3))) * Math.PI / 180;
        return [geometry.center.x + geometry.radius * Math.cos(radians), geometry.center.y + geometry.radius * Math.sin(radians)];
      }).flat()} stroke="#fa8c16" strokeWidth={2.5 / scale} />}
    {angles.map((angleDeg, index) => {
      const radians = normalizeCaliperAngle(angleDeg) * Math.PI / 180;
      const points = [geometry.center.x + inner * Math.cos(radians), geometry.center.y + inner * Math.sin(radians),
        geometry.center.x + outer * Math.cos(radians), geometry.center.y + outer * Math.sin(radians)];
      return index === 0
        ? <Arrow key={`spoke-${index}`} points={searchFromEnd ? [...points.slice(2), ...points.slice(0, 2)] : points}
          pointerAtBeginning={searchStrongest} stroke="#fa8c16" fill="#fa8c16" opacity={0.9}
          pointerLength={6 / scale} pointerWidth={6 / scale} strokeWidth={1.5 / scale} />
        : <Line key={`spoke-${index}`} points={points} stroke="#91d5ff" opacity={0.55} strokeWidth={0.7 / scale} />;
    })}
    <Text x={firstSearchStart[0] - searchDirectionX * 15 / scale + labelNormalX} y={firstSearchStart[1] - searchDirectionY * 15 / scale + labelNormalY} text="起点" fill="#1677ff"
      fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} fillAfterStrokeEnabled listening={false} />
    <Text x={firstSearchEnd[0] + searchDirectionX * 7 / scale + labelNormalX} y={firstSearchEnd[1] + searchDirectionY * 7 / scale + labelNormalY} text="终点" fill="#722ed1"
      fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} fillAfterStrokeEnabled listening={false} />
    {!fullCircle && span > 0 && <>
      <Line points={boundary(normalizeCaliperAngle(start))} stroke="#fa8c16" strokeWidth={1.5 / scale} />
      <Line points={boundary(normalizeCaliperAngle(start) + span)} stroke="#fa8c16" strokeWidth={1.5 / scale} />
    </>}
    <Line points={[geometry.center.x - handleRadius * 1.7, geometry.center.y, geometry.center.x + handleRadius * 1.7, geometry.center.y]} stroke="#fa8c16" strokeWidth={1.5 / scale} />
    <Line points={[geometry.center.x, geometry.center.y - handleRadius * 1.7, geometry.center.x, geometry.center.y + handleRadius * 1.7]} stroke="#fa8c16" strokeWidth={1.5 / scale} />
    {editable && <>
      {geometry.centerEditable !== false && <Circle x={geometry.center.x} y={geometry.center.y} radius={handleRadius} fill="#fa8c16" stroke="#fff" strokeWidth={1.5 / scale} />}
      {radiusHandle && <Circle x={radiusHandle.x} y={radiusHandle.y} radius={handleRadius} fill="#fa8c16" stroke="#fff" strokeWidth={1.5 / scale} />}
      {profileWidthHandle && <Circle name="measurement-profile-width" x={profileWidthHandle.point.x} y={profileWidthHandle.point.y} radius={handleRadius}
        fill="#eb2f96" stroke="#fff" strokeWidth={1.5 / scale} />}
      {arcHandles?.start && <Circle name="caliper-circle-angle-start" x={arcHandles.start.x} y={arcHandles.start.y} radius={handleRadius} fill="#1677ff" stroke="#fff" strokeWidth={1.5 / scale} />}
      {arcHandles?.end && <Circle name="caliper-circle-angle-end" x={arcHandles.end.x} y={arcHandles.end.y} radius={handleRadius} fill="#722ed1" stroke="#fff" strokeWidth={1.5 / scale} />}
      {bandHandles && <>
        <Circle x={bandHandles.inner.x} y={bandHandles.inner.y} radius={handleRadius} fill="#13c2c2" stroke="#fff" strokeWidth={1.5 / scale} />
        <Circle x={bandHandles.outer.x} y={bandHandles.outer.y} radius={handleRadius} fill="#52c41a" stroke="#fff" strokeWidth={1.5 / scale} />
      </>}
    </>}
  </Group>;
}

export default function ImageViewer({
  previewUrl,
  imageWidth,
  imageHeight,
  overlays,
  roi: inputRoi,
  roiEnabled,
  showRoiTools = true,
  roiPointLimit,
  roiClearable = true,
  roiTargetLabel,
  loading,
  onRoiChange,
  measurementGeometry,
  measurementGeometryEditable = false,
  onMeasurementGeometryChange,
  caliperDiagnosticSelection,
  measurementMarkers = [],
  nodeLabels,
  focusTick,
  emptyHint = '运行工作流以显示图像和矢量覆盖层',
  emptyAction
}: {
  previewUrl?: string;
  imageWidth?: number;
  imageHeight?: number;
  overlays: VisionOverlay[];
  roi?: VisionRoi;
  roiEnabled: boolean;
  /** 纯图像预览时隐藏无关的绘图操作。 */
  showRoiTools?: boolean;
  /** 多边形点数上限；设置后只保留平移/多边形工具，达到点数立即闭合（如透视四角）。 */
  roiPointLimit?: number;
  /** 固定结构 ROI 无法用“清除”表达时关闭清除按钮（如透视四角始终需要四个点）。 */
  roiClearable?: boolean;
  roiTargetLabel?: string;
  loading?: boolean;
  onRoiChange: (roi?: VisionRoi) => void;
  /** Search geometry authored in the displayed image coordinate frame. */
  measurementGeometry?: MeasurementGeometry;
  measurementGeometryEditable?: boolean;
  onMeasurementGeometryChange?: (geometry: MeasurementGeometry, options?: { preserveValues?: boolean }) => void;
  /** Read-only samples in the displayed run image frame. Picking never changes measurement geometry. */
  caliperDiagnosticSelection?: CaliperDiagnosticSelection;
  /** Diagnostic cursor and candidates stay visible above the search geometry. */
  measurementMarkers?: VisionOverlay[];
  /** 覆盖层按节点筛选时的显示名映射 */
  nodeLabels?: Record<string, string>;
  /** 递增信号：来自属性面板“定位选区”，把视图适配到当前 ROI 区域 */
  focusTick?: number;
  /** 无图像时的提示文案（默认面向主画布流程运行；弹窗内可改为“运行此节点”） */
  emptyHint?: string;
  /** 无图像时的操作入口（如“运行此节点”）：空态不再只有一行灰字 */
  emptyAction?: { label: string; onClick: () => void; disabled?: boolean; title?: string };
}) {
  // Imported workflows can contain malformed legacy ROI JSON. Ignore it in the canvas so
  // the parameter editor can explain and clear it without crashing the whole configuration UI.
  const roi = useMemo(() => inputRoi && !roiConfigurationIssue(inputRoi, 'ROI', 1024) ? inputRoi : undefined, [inputRoi]);
  const hostRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<any>(null);
  const [size, setSize] = useState({ width: 400, height: 260 });
  const [view, setView] = useState<ViewState>({ scale: 1, x: 0, y: 0 });
  const [tool, setTool] = useState<ToolMode>('pan');
  const [cursorScreen, setCursor] = useState<Point>();
  const [showOverlay, setShowOverlay] = useState(true);
  const [overlayFilter, setOverlayFilter] = useState<string[]>([]);
  const [brushWidth, setBrushWidth] = useState(20);
  const [measurementDraft, setMeasurementDraft] = useState<MeasurementGeometry>();
  const [geometryDrawMode, setGeometryDrawMode] = useState(false);
  const [circleDrawMode, setCircleDrawMode] = useState<CircleDrawMode>('center');
  const isThreePointDrawing = circleDrawMode === 'threePoints' || circleDrawMode === 'threePointArc';
  const [circleConstructionPoints, setCircleConstructionPoints] = useState<Point[]>([]);
  const [circleConstructionIssue, setCircleConstructionIssue] = useState<string>();
  const [roiUndoEntry, setRoiUndoEntry] = useState<{ previous?: VisionRoi; afterFingerprint: string }>();
  const [measurementUndoEntry, setMeasurementUndoEntry] = useState<{ previous: MeasurementGeometry; afterFingerprint: string }>();
  // 像素化（最近邻）默认开：放大即真实像素方块；开关可切回平滑插值
  const [pixelated, setPixelated] = useState(readPixelated);
  useEffect(() => { persistPixelated(pixelated); }, [pixelated]);
  const [draft, setDraft] = useState<VisionRoi>();
  const gestureStart = useRef<Point | undefined>(undefined);
  const geometryGestureStart = useRef<Point | undefined>(undefined);
  const geometryDrag = useRef<{ mode: MeasurementDragMode; origin: MeasurementGeometry; start: Point; profileWidthAngle?: number } | undefined>(undefined);
  const panStart = useRef<{ pointer: Point; view: ViewState } | undefined>(undefined);
  const roiDrag = useRef<{ mode: RoiDragMode; origin: VisionRoi; start: Point; moveBounds?: RoiMoveBounds } | undefined>(undefined);
  const [roiDragOffset, setRoiDragOffset] = useState<Point>();
  const polygonPoints = useRef<OverlayPoint[]>([]);
  const brushStroke = useRef<{ baseStrokes: VisionRoiBrushStroke[]; activeStroke: VisionRoiBrushStroke } | undefined>(undefined);
  const [brushPreview, setBrushPreview] = useState<VisionRoiBrushStroke>();
  const lastFocusTick = useRef(focusTick ?? 0);
  const pointerUpHandler = useRef<() => void>(() => undefined);
  const pointerCancelHandler = useRef<() => void>(() => undefined);
  const pointerMoveHandler = useRef<() => void>(() => undefined);
  const { image, failed } = useHtmlImage(previewUrl);
  const actualWidth = imageWidth || image?.naturalWidth || 0;
  const actualHeight = imageHeight || image?.naturalHeight || 0;
  const canDrawMeasurementGeometry = measurementGeometryEditable && measurementGeometry && (measurementGeometry.type === 'line'
    || measurementGeometry.centerEditable !== false && measurementGeometry.radiusEditable !== false);
  const cursorPoint = cursorScreen ? { x: (cursorScreen.x - view.x) / view.scale, y: (cursorScreen.y - view.y) / view.scale } : undefined;
  const cursor = cursorPoint && cursorPoint.x >= 0 && cursorPoint.y >= 0 && cursorPoint.x < actualWidth && cursorPoint.y < actualHeight ? cursorPoint : undefined;
  const pixelBuffer = useMemo(() => {
    if (!image) return undefined;
    try {
      const canvas = document.createElement('canvas');
      canvas.width = image.naturalWidth;
      canvas.height = image.naturalHeight;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) return null;
      context.drawImage(image, 0, 0);
      return createPixelBuffer(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height);
    } catch { return null; }
  }, [image]);
  const pixel = cursor && pixelBuffer ? readPixel(pixelBuffer, cursor.x, cursor.y, actualWidth, actualHeight) : undefined;

  useEffect(() => {
    if (!roiEnabled) {
      setTool('pan');
      setDraft(undefined);
      setBrushPreview(undefined);
      polygonPoints.current = [];
      brushStroke.current = undefined;
      gestureStart.current = undefined;
      roiDrag.current = undefined;
      setRoiDragOffset(undefined);
      setRoiUndoEntry(undefined);
    }
  }, [roiEnabled]);
  useEffect(() => {
    setOverlayFilter([]);
    setCursor(undefined);
    setDraft(undefined);
    setBrushPreview(undefined);
    setMeasurementDraft(undefined);
    setGeometryDrawMode(false);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    setRoiUndoEntry(undefined);
    setMeasurementUndoEntry(undefined);
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
    polygonPoints.current = [];
    brushStroke.current = undefined;
    roiDrag.current = undefined;
    setRoiDragOffset(undefined);
  }, [previewUrl]);
  useEffect(() => {
    if (measurementGeometryEditable && canDrawMeasurementGeometry) return;
    setGeometryDrawMode(false);
    setMeasurementDraft(undefined);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    if (!measurementGeometryEditable) setMeasurementUndoEntry(undefined);
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
  }, [measurementGeometryEditable, Boolean(canDrawMeasurementGeometry)]);

  const commitRoiEdit = (next?: VisionRoi) => {
    if (parameterFingerprint(roi) === parameterFingerprint(next)) return;
    const entry = { previous: roi, afterFingerprint: parameterFingerprint(next) };
    setRoiUndoEntry(entry);
    onRoiChange(next);
  };

  const undoRoiEdit = () => {
    if (!roiUndoEntry) return;
    setRoiUndoEntry(undefined);
    if (roiUndoEntry.afterFingerprint === parameterFingerprint(roi)) onRoiChange(roiUndoEntry.previous);
  };

  const committedMeasurementGeometry = (geometry: MeasurementGeometry): MeasurementGeometry => {
    if (geometry.type === 'line') return geometry.profileWidth === undefined ? geometry
      : { ...geometry, profileWidth: effectiveSampleWidth(geometry.profileWidth) };
    const start = geometry.angleStartDeg ?? -180;
    const end = geometry.angleEndDeg ?? 180;
    const normalizedArc = normalizeCaliperCircleArc(start, end);
    return { ...geometry, angleStartDeg: normalizedArc.start, angleEndDeg: normalizedArc.end,
      band: clamp(geometry.band ?? 1, 1, 500),
      ...(geometry.profileWidth === undefined ? {} : { profileWidth: effectiveSampleWidth(geometry.profileWidth) }) };
  };

  const commitMeasurementEdit = (next: MeasurementGeometry) => {
    if (!measurementGeometry || !onMeasurementGeometryChange) return;
    const expected = committedMeasurementGeometry(next);
    const entry = { previous: measurementGeometry, afterFingerprint: parameterFingerprint(expected) };
    setMeasurementUndoEntry(entry);
    onMeasurementGeometryChange(next);
  };

  const undoMeasurementEdit = () => {
    if (!measurementUndoEntry || !measurementGeometry || !onMeasurementGeometryChange) return;
    setMeasurementUndoEntry(undefined);
    if (measurementUndoEntry.afterFingerprint === parameterFingerprint(measurementGeometry))
      onMeasurementGeometryChange(measurementUndoEntry.previous, { preserveValues: true });
  };

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const observer = new ResizeObserver(([entry]) => {
      const r = entry.contentRect;
      setSize({ width: Math.max(10, r.width), height: Math.max(10, r.height) });
    });
    observer.observe(host);
    return () => observer.disconnect();
  }, []);

  // 视图适配目标：fit=全图，定位选区=ROI 包围盒。容器尺寸变化时保持同一目标重适配，
  // 避免“适应窗口”式重置覆盖刚触发的定位意图
  const fitTargetRef = useRef<{ x: number; y: number; width: number; height: number } | undefined>(undefined);
  const setViewToTarget = (target: { x: number; y: number; width: number; height: number }, pad: number) => {
    // 留白按容器尺寸自适应：容器很矮（如底部面板展开时）时固定留白会挤掉大部分可用空间
    const effPad = Math.min(pad, Math.min(size.width, size.height) * 0.12);
    const scale = clamp(Math.min((size.width - effPad * 2) / target.width, (size.height - effPad * 2) / target.height), 0.02, 30);
    setView({
      scale,
      x: size.width / 2 - (target.x + target.width / 2) * scale,
      y: size.height / 2 - (target.y + target.height / 2) * scale
    });
  };

  const fit = () => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setViewToTarget(fitTargetRef.current, 12);
  };

  const oneToOne = () => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setView({ scale: 1, x: (size.width - actualWidth) / 2, y: (size.height - actualHeight) / 2 });
  };

  const focusSearchLine = () => {
    if (measurementGeometry?.type !== 'line') return;
    const segment = clipSegmentToImage(measurementGeometry.start, measurementGeometry.end, actualWidth, actualHeight);
    if (!segment) return;
    const margin = 48 + (measurementGeometry.profileWidth ?? 0) / 2;
    const x = Math.max(0, Math.min(segment.start.x, segment.end.x) - margin);
    const y = Math.max(0, Math.min(segment.start.y, segment.end.y) - margin);
    fitTargetRef.current = { x, y,
      width: Math.max(2, Math.min(actualWidth, Math.max(segment.start.x, segment.end.x) + margin) - x),
      height: Math.max(2, Math.min(actualHeight, Math.max(segment.start.y, segment.end.y) + margin) - y) };
    setViewToTarget(fitTargetRef.current, 24);
  };

  // 图像（尺寸）变化：目标重置为全图并适配
  useEffect(() => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setViewToTarget(fitTargetRef.current, 12);
  }, [actualWidth, actualHeight]);

  // 容器尺寸变化：按当前目标（全图或 ROI）重新适配
  useEffect(() => {
    if (!fitTargetRef.current) return;
    setViewToTarget(fitTargetRef.current, 12);
  }, [size.width, size.height]);

  // 定位选区：属性面板每次触发递增 focusTick，这里把视图适配到当前 ROI 包围盒
  useEffect(() => {
    if (focusTick === undefined || focusTick === lastFocusTick.current) return;
    lastFocusTick.current = focusTick;
    if (!roi) return;
    fitTargetRef.current = roiBounds(roi);
    setViewToTarget(fitTargetRef.current, 28);
  }, [focusTick, roi, size.width, size.height]);

  const imagePoint = useCallback((screen: Point): Point => ({
    x: (screen.x - view.x) / view.scale,
    y: (screen.y - view.y) / view.scale
  }), [view]);

  const inside = useCallback((p: Point) => p.x >= 0 && p.y >= 0 && p.x <= actualWidth && p.y <= actualHeight,
    [actualHeight, actualWidth]);

  const pointer = (): Point | undefined => {
    const p = stageRef.current?.getPointerPosition();
    return p ? { x: p.x, y: p.y } : undefined;
  };

  /** 命中选区控制点（屏幕距离判定）：返回拖动模式 */
  const hitRoiHandle = (target: VisionRoi, screen: Point): RoiDragMode | undefined => {
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
      const handle = circleRadiusHandle({ x: target.x, y: target.y }, target.radius, actualWidth, actualHeight);
      return closestHandle([['e', handle]], screen, view);
    }
    if (target.type === 'Brush') return undefined;
    return closestHandle(polygonHandleIndices(target.points.length)
      .map((vertexIndex): [RoiDragMode, Point] => [`vertex:${vertexIndex}`, target.points[vertexIndex]]), screen, view);
  };

  const hitMeasurementHandle = (target: MeasurementGeometry, screen: Point): MeasurementDragMode | undefined => {
    const near = (point: Point) => Math.hypot(screen.x - (view.x + point.x * view.scale), screen.y - (view.y + point.y * view.scale)) <= HANDLE_HIT;
    if (target.type === 'line') {
      const profileHandles = target.profileCount !== undefined
        ? caliperLineProfileSpanHandles(target, actualWidth, actualHeight) : undefined;
      const candidates: [MeasurementDragMode, Point][] = [['start', target.start], ['end', target.end]];
      if (profileHandles) candidates.push(['profileStart', profileHandles.start], ['profileEnd', profileHandles.end]);
      const widthHandle = caliperLineProfileWidthHandle(target, actualWidth, actualHeight);
      if (widthHandle) candidates.push(['profileWidth', widthHandle]);
      const endpoint = closestHandle(candidates, screen, view);
      if (endpoint) return endpoint;
      const dx = target.end.x - target.start.x, dy = target.end.y - target.start.y;
      const lengthSquared = dx * dx + dy * dy;
      const cursor = imagePoint(screen);
      const t = lengthSquared > 0 ? clamp(((cursor.x - target.start.x) * dx + (cursor.y - target.start.y) * dy) / lengthSquared, 0, 1) : 0;
      const closest = { x: target.start.x + t * dx, y: target.start.y + t * dy };
      if (near(closest)) return 'moveLine';
      return undefined;
    }
    const arcHandles = caliperCircleArcHandles(target, view.scale, actualWidth, actualHeight);
    const bandHandles = caliperCircleBandHandles(target, view.scale, actualWidth, actualHeight);
    const candidates: [MeasurementDragMode, Point][] = [];
    const angles = buildCaliperCircleAngles(target);
    const widthHandle = caliperCircleProfileWidthHandle(target, angles, actualWidth, actualHeight);
    if (target.centerEditable !== false) candidates.push(['center', target.center]);
    if (target.radiusEditable !== false) candidates.push(['radius', circleRadiusHandle(target.center, target.radius, actualWidth, actualHeight)]);
    if (widthHandle) candidates.push(['profileWidth', widthHandle.point]);
    if (arcHandles?.start) candidates.push(['angleStart', arcHandles.start]);
    if (arcHandles?.end) candidates.push(['angleEnd', arcHandles.end]);
    if (bandHandles) candidates.push(['innerBand', bandHandles.inner], ['outerBand', bandHandles.outer]);
    const closest = closestHandle(candidates, screen, view);
    if (closest) return closest;
    const cursor = imagePoint(screen);
    if (target.centerEditable !== false
      && Math.abs(Math.hypot(cursor.x - target.center.x, cursor.y - target.center.y) - target.radius) * view.scale <= HANDLE_HIT)
      return 'moveCircle';
    return undefined;
  };

  const onWheel = (event: any) => {
    event.evt.preventDefault();
    const p = pointer();
    if (!p || !actualWidth) return;
    const before = imagePoint(p);
    const factor = event.evt.deltaY > 0 ? 0.9 : 1.1;
    const scale = clamp(view.scale * factor, 0.02, 30);
    setView({ scale, x: p.x - before.x * scale, y: p.y - before.y * scale });
  };

  const beginBrushStroke = (point: Point, mode: VisionRoiBrushStroke['mode']) => {
    const activeStroke = { mode, width: brushWidth, points: [point, point] };
    brushStroke.current = { baseStrokes: roi?.type === 'Brush' ? roi.strokes : [], activeStroke };
    setDraft(undefined);
    setBrushPreview(activeStroke);
  };

  const extendBrushStroke = (point: Point) => {
    const active = brushStroke.current;
    if (!active) return;
    const previous = active.activeStroke.points[active.activeStroke.points.length - 1];
    if (Math.hypot(point.x - previous.x, point.y - previous.y) < 0.35) return;
    const activeStroke = { ...active.activeStroke, points: [...active.activeStroke.points, point] };
    brushStroke.current = { ...active, activeStroke };
    setBrushPreview(activeStroke);
  };

  const finishBrushStroke = () => {
    const active = brushStroke.current;
    if (!active) return;
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    commitRoiEdit({ type: 'Brush', strokes: [...active.baseStrokes, active.activeStroke] });
  };

  const onPointerDown = () => {
    const p = pointer();
    if (!p) return;
    setCursor(p);
    const ip = imagePoint(p);
    if (caliperDiagnosticSelection) {
      const index = nearestDiagnosticSpoke(caliperDiagnosticSelection, ip, view.scale);
      if (index !== undefined) caliperDiagnosticSelection.onSelect(index);
      else panStart.current = { pointer: p, view };
      return;
    }
    if (geometryDrawMode && canDrawMeasurementGeometry && measurementGeometry) {
      if (!inside(ip)) return;
      if (measurementGeometry.type === 'circle' && isThreePointDrawing) {
        const picked = [...circleConstructionPoints, ip];
        setCircleConstructionIssue(undefined);
        if (picked.length === 2 && Math.hypot(ip.x - picked[0].x, ip.y - picked[0].y) < 1) {
          setCircleConstructionIssue('第 2 点距离第 1 点过近，请换一个边缘点。');
          return;
        }
        if (picked.length < 3) {
          setCircleConstructionPoints(picked);
        } else {
          const circle = circleDrawMode === 'threePointArc'
            ? circleArcThroughThreePoints(picked[0], picked[1], picked[2])
            : circleThroughThreePoints(picked[0], picked[1], picked[2]);
          if (!circle) {
            setCircleConstructionIssue('这三个点过近或接近共线，无法确定有效圆。请重新选择第 3 点。');
            setMeasurementDraft(undefined);
            return;
          }
          commitMeasurementEdit({ ...measurementGeometry, ...circle });
          setCircleConstructionPoints([]);
          setMeasurementDraft(undefined);
          setGeometryDrawMode(false);
        }
        return;
      }
      geometryGestureStart.current = ip;
      setMeasurementDraft(measurementGeometry.type === 'line'
        ? { ...measurementGeometry, start: ip, end: ip }
        : { ...measurementGeometry, center: ip, radius: 0 });
      return;
    }
    if (measurementGeometryEditable && measurementGeometry) {
      const handle = hitMeasurementHandle(measurementGeometry, p);
      if (handle) {
        const profileWidthAngle = handle === 'profileWidth' && measurementGeometry.type === 'circle'
          ? caliperCircleProfileWidthHandle(measurementGeometry, buildCaliperCircleAngles(measurementGeometry), actualWidth, actualHeight)?.angle
          : undefined;
        geometryDrag.current = { mode: handle, origin: measurementGeometry, start: ip, profileWidthAngle };
        return;
      }
    }
    // 平移模式负责编辑已有 ROI；绘制工具始终新建/替换选区，避免 ROI 内部点击被吞成移动。
    if (roi && roiEnabled && tool === 'pan') {
      const handle = hitRoiHandle(roi, p);
      if (handle) {
        roiDrag.current = { mode: handle, origin: roi, start: imagePoint(p) };
        return;
      }
      if (pointInsideRoi(roi, imagePoint(p))) {
        const moveBounds = roi.type === 'Brush' ? brushRoiBounds(roi)
          : roi.type === 'Polygon' ? polygonRoiBounds(roi) : undefined;
        roiDrag.current = { mode: 'move', origin: roi, start: imagePoint(p), moveBounds };
        if (roi.type === 'Brush' || roi.type === 'Polygon') setRoiDragOffset({ x: 0, y: 0 });
        return;
      }
    }
    if (tool === 'pan') {
      panStart.current = { pointer: p, view };
      return;
    }
    if (!roiEnabled || tool === 'polygon') return;
    if (!inside(ip)) return;
    if (tool === 'brush') {
      beginBrushStroke(ip, 'Add');
      return;
    }
    if (tool === 'eraser') {
      if (roi?.type !== 'Brush') return;
      beginBrushStroke(ip, 'Erase');
      return;
    }
    gestureStart.current = ip;
    if (tool === 'rect') setDraft(normalizeRect(ip, ip));
    if (tool === 'circle') setDraft({ type: 'Circle', x: ip.x, y: ip.y, radius: 0 });
  };

  const onPointerMove = () => {
    const p = pointer();
    if (!p) return;
    const ip = imagePoint(p);
    setCursor(p);
    if (geometryDrawMode && isThreePointDrawing && measurementGeometry?.type === 'circle') {
      const circle = circleConstructionPoints.length === 2 && inside(ip)
        ? (circleDrawMode === 'threePointArc' ? circleArcThroughThreePoints : circleThroughThreePoints)(circleConstructionPoints[0], circleConstructionPoints[1], ip)
        : undefined;
      setMeasurementDraft(circle ? { ...measurementGeometry, ...circle } : undefined);
      return;
    }
    if (geometryGestureStart.current && measurementGeometry) {
      const start = geometryGestureStart.current;
      const end = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
      setMeasurementDraft(measurementGeometry.type === 'line'
        ? { ...measurementGeometry, start, end: boundedMeasurementPoint(ip) }
        : circleDrawMode === 'diameter'
          ? { ...measurementGeometry, center: { x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 },
            radius: Math.hypot(end.x - start.x, end.y - start.y) / 2 }
          : { ...measurementGeometry, center: start,
            radius: Math.min(Math.hypot(end.x - start.x, end.y - start.y), maxInteractiveCircleRadius(start, actualWidth, actualHeight)) });
      return;
    }
    if (geometryDrag.current) {
      const { mode, origin, start, profileWidthAngle } = geometryDrag.current;
      const current = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
      if (origin.type === 'line') {
        if (mode === 'start' || mode === 'end') {
          setMeasurementDraft({ ...origin, [mode]: boundedMeasurementPoint(ip) });
        } else if (mode === 'profileWidth') {
          const dx = origin.end.x - origin.start.x, dy = origin.end.y - origin.start.y;
          const length = Math.hypot(dx, dy);
          if (length > 0) {
            const normal = { x: -dy / length, y: dx / length };
            const delta = (current.x - start.x) * normal.x + (current.y - start.y) * normal.y;
            setMeasurementDraft({ ...origin, profileWidth: effectiveSampleWidth(clamp((origin.profileWidth ?? 1) + 2 * delta, 1, 99)) });
          }
        } else if (mode === 'profileStart' || mode === 'profileEnd') {
          const dx = origin.end.x - origin.start.x, dy = origin.end.y - origin.start.y;
          const length = Math.hypot(dx, dy);
          if (length > 0) {
            const tangent = { x: -dy / length, y: dx / length };
            const delta = (current.x - start.x) * tangent.x + (current.y - start.y) * tangent.y;
            const direction = mode === 'profileStart' ? -1 : 1;
            const initialSpan = origin.profileSpan && origin.profileSpan > 0 ? origin.profileSpan : length;
            const profileSpan = Math.abs(delta) < 1e-9 ? origin.profileSpan
              : clamp(initialSpan + direction * 2 * delta, 1, 10_000);
            setMeasurementDraft({ ...origin, profileSpan });
          }
        } else {
          // Keep clipped search lines draggable: constrain their bounds to keep some
          // overlap with the image, while allowing endpoints to remain outside.
          const minX = Math.min(origin.start.x, origin.end.x), maxX = Math.max(origin.start.x, origin.end.x);
          const minY = Math.min(origin.start.y, origin.end.y), maxY = Math.max(origin.start.y, origin.end.y);
          const dx = clamp(current.x - start.x, -maxX, actualWidth - minX);
          const dy = clamp(current.y - start.y, -maxY, actualHeight - minY);
          setMeasurementDraft({ ...origin, start: { x: origin.start.x + dx, y: origin.start.y + dy }, end: { x: origin.end.x + dx, y: origin.end.y + dy } });
        }
      } else if (mode === 'profileWidth') {
        const angle = profileWidthAngle ?? origin.angleStartDeg ?? -180;
        const radians = normalizeCaliperAngle(angle) * Math.PI / 180;
        const normal = { x: -Math.sin(radians), y: Math.cos(radians) };
        const delta = (current.x - start.x) * normal.x + (current.y - start.y) * normal.y;
        setMeasurementDraft({ ...origin, profileWidth: effectiveSampleWidth(clamp((origin.profileWidth ?? 1) + 2 * delta, 1, 99)) });
      } else if (mode === 'center' || mode === 'moveCircle') {
        const center = mode === 'center' ? current : {
          x: origin.center.x + current.x - start.x,
          y: origin.center.y + current.y - start.y
        };
        setMeasurementDraft({ ...origin, center: mode === 'center' ? {
          x: clampCircleCenter(center.x, origin.radius, actualWidth),
          y: clampCircleCenter(center.y, origin.radius, actualHeight)
        } : clampCaliperCircleCenter(center, origin.radius, origin.band ?? 0, actualWidth, actualHeight) });
      } else if (mode === 'radius') {
        const startDistance = Math.hypot(start.x - origin.center.x, start.y - origin.center.y);
        const currentDistance = Math.hypot(current.x - origin.center.x, current.y - origin.center.y);
        setMeasurementDraft({ ...origin, radius: clamp(origin.radius + currentDistance - startDistance, 1, 10_000_000) });
      } else if (mode === 'angleStart' || mode === 'angleEnd') {
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
        setMeasurementDraft(mode === 'angleStart'
          ? { ...origin, angleStartDeg: adjustedAngle }
          : { ...origin, angleEndDeg: adjustedAngle });
      } else if (mode === 'innerBand' || mode === 'outerBand') {
        const initialBand = Math.max(0, Math.min(500, origin.band ?? 0));
        const startDistance = Math.hypot(start.x - origin.center.x, start.y - origin.center.y);
        const currentDistance = Math.hypot(current.x - origin.center.x, current.y - origin.center.y);
        const initialInner = Math.max(0, origin.radius - initialBand);
        const band = mode === 'innerBand'
          ? clamp(origin.radius - Math.max(0, initialInner + currentDistance - startDistance), 1, 500)
          : clamp(initialBand + currentDistance - startDistance, 1, 500);
        setMeasurementDraft({ ...origin, band });
      }
      return;
    }
    if (brushStroke.current) {
      extendBrushStroke({ x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) });
      return;
    }
    if (roiDrag.current) {
      const active = roiDrag.current;
      if ((active.origin.type === 'Brush' || active.origin.type === 'Polygon') && active.moveBounds) {
        setRoiDragOffset(clampRoiMove(active.moveBounds,
          { x: ip.x - active.start.x, y: ip.y - active.start.y }, actualWidth, actualHeight));
        return;
      }
      const next = applyRoiDrag(active.origin, active.mode, active.start, ip, actualWidth, actualHeight, active.moveBounds);
      if (next) setDraft(next);
      return;
    }
    if (tool === 'pan' && panStart.current) {
      const start = panStart.current;
      setView({ ...start.view, x: start.view.x + p.x - start.pointer.x, y: start.view.y + p.y - start.pointer.y });
      return;
    }
    if (!gestureStart.current || !roiEnabled) return;
    const end = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
    if (tool === 'rect') setDraft(normalizeRect(gestureStart.current, end));
    if (tool === 'circle') {
      const dx = end.x - gestureStart.current.x;
      const dy = end.y - gestureStart.current.y;
      const center = gestureStart.current;
      setDraft({ type: 'Circle', x: center.x, y: center.y, radius: Math.min(Math.hypot(dx, dy), maxInteractiveCircleRadius(center, actualWidth, actualHeight)) });
    }
  };

  const onPointerUp = () => {
    panStart.current = undefined;
    if (geometryGestureStart.current) {
      geometryGestureStart.current = undefined;
      const next = measurementDraft;
      if (next && onMeasurementGeometryChange && (next.type === 'line'
        ? Math.hypot(next.end.x - next.start.x, next.end.y - next.start.y) >= 2
        : next.radius >= 1)) commitMeasurementEdit(next);
      setMeasurementDraft(undefined);
      setGeometryDrawMode(false);
      return;
    }
    if (geometryDrag.current) {
      geometryDrag.current = undefined;
      const next = measurementDraft;
      if (next && onMeasurementGeometryChange && (next.type === 'line'
        ? Math.hypot(next.end.x - next.start.x, next.end.y - next.start.y) >= 2
        : next.radius >= 1)) commitMeasurementEdit(next);
      setMeasurementDraft(undefined);
      return;
    }
    if (brushStroke.current) {
      finishBrushStroke();
      return;
    }
    if (roiDrag.current) {
      const active = roiDrag.current;
      roiDrag.current = undefined;
      const next = active.origin.type === 'Brush' || active.origin.type === 'Polygon' && active.mode === 'move'
        ? applyRoiDrag(active.origin, active.mode, { x: 0, y: 0 }, roiDragOffset ?? { x: 0, y: 0 },
          actualWidth, actualHeight, active.moveBounds)
        : draft;
      if (next && (next.type !== 'Rectangle' || (next.width >= 2 && next.height >= 2)) && (next.type !== 'Circle' || next.radius >= 1)) {
        commitRoiEdit(next);
      }
      setRoiDragOffset(undefined);
      setDraft(undefined);
      return;
    }
    if (gestureStart.current && draft) {
      if (draft.type === 'Rectangle' && draft.width >= 2 && draft.height >= 2) commitRoiEdit(draft);
      if (draft.type === 'Circle' && draft.radius >= 1) commitRoiEdit(draft);
    }
    gestureStart.current = undefined;
    setDraft(undefined);
  };

  const onPointerCancel = () => {
    panStart.current = undefined;
    gestureStart.current = undefined;
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
    roiDrag.current = undefined;
    setRoiDragOffset(undefined);
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    setMeasurementDraft(undefined);
    setGeometryDrawMode(false);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
  };

  useEffect(() => { pointerUpHandler.current = onPointerUp; });
  useEffect(() => { pointerCancelHandler.current = onPointerCancel; });
  useEffect(() => { pointerMoveHandler.current = onPointerMove; });
  useEffect(() => {
    const releaseInteraction = () => pointerUpHandler.current();
    const cancelInteraction = () => pointerCancelHandler.current();
    const moveOutsideViewer = (event: MouseEvent) => {
      if (!(panStart.current || roiDrag.current || brushStroke.current || geometryDrag.current
        || gestureStart.current || geometryGestureStart.current)) return;
      if (event.target instanceof Element && hostRef.current?.contains(event.target)) return;
      stageRef.current?.setPointersPositions?.(event);
      pointerMoveHandler.current();
    };
    // Konva's stage only receives mouse/touch end while the pointer is over the viewer.
    // Track mouse drags outside the canvas and listen for release over a toolbar or elsewhere.
    document.addEventListener('mousemove', moveOutsideViewer);
    document.addEventListener('mouseup', releaseInteraction);
    document.addEventListener('touchend', releaseInteraction);
    document.addEventListener('touchcancel', cancelInteraction);
    return () => {
      document.removeEventListener('mousemove', moveOutsideViewer);
      document.removeEventListener('mouseup', releaseInteraction);
      document.removeEventListener('touchend', releaseInteraction);
      document.removeEventListener('touchcancel', cancelInteraction);
    };
  }, []);

  const onClick = () => {
    if (tool !== 'polygon' || !roiEnabled) return;
    const p = pointer();
    if (!p) return;
    const ip = imagePoint(p);
    if (!inside(ip)) return;
    polygonPoints.current = [...polygonPoints.current, ip];
    if (roiPointLimit && polygonPoints.current.length >= roiPointLimit) {
      const points = polygonPoints.current.slice(0, roiPointLimit);
      polygonPoints.current = [];
      setDraft(undefined);
      commitRoiEdit({ type: 'Polygon', points });
      return;
    }
    setDraft({ type: 'Polygon', points: polygonPoints.current });
  };

  const commitPolygon = () => {
    if (tool !== 'polygon') return;
    if (roiPointLimit && polygonPoints.current.length !== roiPointLimit) return;
    if (polygonPoints.current.length < 3) return;
    const points = polygonPoints.current.filter((point, index, all) => index === 0
      || point.x !== all[index - 1].x || point.y !== all[index - 1].y);
    // Browsers emit two click events before dblclick. Trim the terminal click (and
    // an optional repeated first point used to close the polygon) from the ROI.
    if (points.length > 1 && points.at(-1)!.x === points[0].x && points.at(-1)!.y === points[0].y)
      points.pop();
    if (points.length >= 3) commitRoiEdit({ type: 'Polygon', points });
    polygonPoints.current = [];
    setDraft(undefined);
  };

  const clearRoi = () => {
    polygonPoints.current = [];
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    commitRoiEdit(undefined);
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        // Cancel the unfinished construction before the containing modal handles Escape.
        if (geometryDrawMode) { e.preventDefault(); e.stopPropagation(); }
        gestureStart.current = undefined;
        geometryGestureStart.current = undefined;
        geometryDrag.current = undefined;
        roiDrag.current = undefined;
        setRoiDragOffset(undefined);
        polygonPoints.current = [];
        brushStroke.current = undefined;
        setBrushPreview(undefined);
        setDraft(undefined);
        setMeasurementDraft(undefined);
        setGeometryDrawMode(false);
        setCircleConstructionPoints([]);
        setCircleConstructionIssue(undefined);
        setTool('pan');
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [geometryDrawMode]);

  const cursorStyle = useMemo(() => {
    if (caliperDiagnosticSelection) return panStart.current ? 'grabbing' : 'crosshair';
    if (geometryDrawMode) return 'crosshair';
    if (panStart.current || roiDrag.current || geometryDrag.current) return 'grabbing';
    if (cursorScreen) {
      if (measurementGeometryEditable && measurementGeometry) {
        const handle = hitMeasurementHandle(measurementGeometry, cursorScreen);
        if (handle === 'radius' && measurementGeometry.type === 'circle')
          return radialResizeCursor(measurementGeometry.center,
            circleRadiusHandle(measurementGeometry.center, measurementGeometry.radius, actualWidth, actualHeight));
        if (handle === 'profileWidth') return 'move';
        if (handle === 'innerBand' || handle === 'outerBand') return 'ns-resize';
        if ((handle === 'profileStart' || handle === 'profileEnd') && measurementGeometry.type === 'line') {
          const dx = measurementGeometry.end.x - measurementGeometry.start.x;
          const dy = measurementGeometry.end.y - measurementGeometry.start.y;
          if (Math.abs(dy) > Math.abs(dx) * 1.5) return 'ew-resize';
          if (Math.abs(dx) > Math.abs(dy) * 1.5) return 'ns-resize';
          return (-dy) * dx >= 0 ? 'nwse-resize' : 'nesw-resize';
        }
        if (handle) return 'move';
      }
      if (roi && roiEnabled && tool === 'pan') {
        const handle = hitRoiHandle(roi, cursorScreen);
        if (handle && roi.type === 'Circle')
          return radialResizeCursor({ x: roi.x, y: roi.y }, circleRadiusHandle({ x: roi.x, y: roi.y }, roi.radius, actualWidth, actualHeight));
        if (handle === 'nw' || handle === 'se') return 'nwse-resize';
        if (handle === 'ne' || handle === 'sw') return 'nesw-resize';
        if (handle === 'w' || handle === 'e') return 'ew-resize';
        if (handle === 'n' || handle === 's') return 'ns-resize';
        if (handle?.startsWith('vertex:')) return 'move';
        const imageCursor = imagePoint(cursorScreen);
        if ((roi.type === 'Rectangle' || roi.type === 'Circle') && pointInsideRoi(roi, imageCursor)) return 'move';
        // Match the visible, cached preview contour without ray-casting every source vertex
        // on every pointer move; pointer-down still checks the full ROI for exact movement.
        if (roi.type === 'Polygon' && pointInsidePolygon(polygonPreviewPoints(roi.points), imageCursor)) return 'move';
      }
    }
    return tool === 'pan' ? 'grab' : 'crosshair';
  }, [actualHeight, actualWidth, cursorScreen, geometryDrawMode, measurementGeometry, measurementGeometryEditable, caliperDiagnosticSelection,
    roi, roiEnabled, tool, view]);

  // 覆盖层按节点筛选：不选=显示全部；选了=只显示所选节点的覆盖层
  const overlayNodeIds = useMemo(() => {
    const ids: string[] = [];
    overlays.forEach((overlay) => {
      if (overlay.nodeId && !ids.includes(overlay.nodeId)) ids.push(overlay.nodeId);
    });
    return ids;
  }, [overlays]);
  const visibleOverlays = useMemo(
    () => (overlayFilter.length === 0 ? overlays : overlays.filter((overlay) => overlay.nodeId && overlayFilter.includes(overlay.nodeId))),
    [overlays, overlayFilter]);
  const roiUndoAvailable = Boolean(roiUndoEntry && roiUndoEntry.afterFingerprint === parameterFingerprint(roi));
  const measurementUndoAvailable = Boolean(measurementUndoEntry && measurementGeometry
    && measurementUndoEntry.afterFingerprint === parameterFingerprint(measurementGeometry));

  return (
    <div className={'vision-viewer' + (geometryDrawMode && measurementGeometry?.type === 'circle' ? ' is-drawing-circle' : '')}>
      <div className="viewer-toolbar">
        <Space size={5} wrap>
          {showRoiTools && <Segmented
            size="small"
            value={tool}
            onChange={(value) => { polygonPoints.current = []; brushStroke.current = undefined; setBrushPreview(undefined); gestureStart.current = undefined;
              geometryGestureStart.current = undefined; geometryDrag.current = undefined; setGeometryDrawMode(false); setMeasurementDraft(undefined);
              setCircleConstructionPoints([]); setCircleConstructionIssue(undefined);
              roiDrag.current = undefined; setRoiDragOffset(undefined); setDraft(undefined); setTool(value as ToolMode); }}
            options={[
              { value: 'pan', label: <Tooltip title="平移画布；拖动已有选区可移动，矩形/圆形可缩放，多边形可编辑顶点">平移</Tooltip> },
              ...(roiPointLimit ? [
                { value: 'polygon', disabled: !roiEnabled, label: <Tooltip title={`依次点击 ${roiPointLimit} 个角点；最后一个点自动闭合`}>多边形</Tooltip> }
              ] : [
                { value: 'rect', disabled: !roiEnabled, label: <Tooltip title="拖动绘制矩形，并替换当前选区">矩形</Tooltip> },
                { value: 'circle', disabled: !roiEnabled, label: <Tooltip title="拖动绘制圆形，并替换当前选区">圆形</Tooltip> },
                { value: 'polygon', disabled: !roiEnabled, label: <Tooltip title="逐点绘制多边形，并替换当前选区；双击完成">多边形</Tooltip> },
                { value: 'brush', disabled: !roiEnabled, label: <Tooltip title="笔刷：按住鼠标拖动绘制选区">笔刷</Tooltip> },
                { value: 'eraser', disabled: !roiEnabled || roi?.type !== 'Brush', label: <Tooltip title="橡皮：按住鼠标拖动擦除">橡皮</Tooltip> }
              ])
            ]}
          />}
          {showRoiTools && roi?.type === 'Polygon' && roi.points.length > MAX_POLYGON_HANDLES && <Tooltip
            title="复杂多边形轮廓最多抽样 8192 个点预览，控制点最多抽样 32 个；在预览选区内部拖动可整体移动，执行仍使用完整 ROI。全部顶点可在属性面板分页编辑。">
            <span className="viewer-roi-handle-hint">复杂多边形 · 预览与控制点抽样</span>
          </Tooltip>}
          {showRoiTools && <span className="viewer-toolbar-divider" />}
          {showRoiTools && (tool === 'brush' || tool === 'eraser') && <span className="viewer-brush-size">
            <span title="笔刷直径（图像像素）">笔宽</span>
            <InputNumber aria-label="笔刷直径" size="small" min={1} max={512} step={1} value={brushWidth}
              disabled={!roiEnabled} onChange={(value) => setBrushWidth(clamp(Number(value ?? 1), 1, 512))} />
            <span>px</span>
          </span>}
          {showRoiTools && roiUndoAvailable && <Button size="small" onClick={undoRoiEdit}
            title="撤销最近一次在画布上完成的选区编辑">{roi?.type === 'Brush' ? '撤销笔迹' : '撤销选区编辑'}</Button>}
          {canDrawMeasurementGeometry && <>
            <span className="viewer-toolbar-divider" />
            <Button size="small" type={geometryDrawMode ? 'primary' : 'default'}
              onClick={() => { setMeasurementDraft(undefined); geometryGestureStart.current = undefined; setCircleConstructionPoints([]);
                setCircleConstructionIssue(undefined); setGeometryDrawMode(value => !value); setTool('pan'); }}
              title={geometryDrawMode ? '退出绘制模式' : measurementGeometry.type === 'circle'
                ? '可选择圆心+半径、直径两端、三点定圆或三点选弧；三点选弧会同时确定圆和测量角度范围。橙色调整圆心/半径，蓝色/紫色调整圆弧起止角，青色/绿色调整搜索带，粉色调整投影宽度'
                : measurementGeometry.profileCount !== undefined
                  ? '蓝色起点、紫色终点固定表示参数端点；箭头表示选边顺序（最强为双向）。青色控制点调整采样跨度，粉色控制点调整投影宽度。拖动端点或线段可微调搜索线'
                  : '蓝色起点、紫色终点固定表示参数端点；箭头表示选边顺序（最强为双向）。粉色控制点调整投影宽度。拖动端点或线段可微调搜索线'}>
              {geometryDrawMode ? '取消绘制' : measurementGeometry.type === 'line' ? '重绘搜索线' : '重绘标称圆'}
            </Button>
          </>}
          {geometryDrawMode && measurementGeometry?.type === 'circle' && <Segmented size="small" aria-label="标称圆圈选方式"
            value={circleDrawMode} options={[{ label: '圆心+半径', value: 'center' },
              { label: '直径两端', value: 'diameter' }, { label: '三点定圆', value: 'threePoints' },
              { label: '三点选弧', value: 'threePointArc' }]}
            onChange={value => {
              setCircleDrawMode(value as CircleDrawMode); setCircleConstructionPoints([]); setCircleConstructionIssue(undefined);
              setMeasurementDraft(undefined); geometryGestureStart.current = undefined;
            }} />}
          {measurementUndoAvailable && <Button size="small" onClick={undoMeasurementEdit}
            title="撤销最近一次在画布上完成的卡尺几何编辑">撤销测量编辑</Button>}
          {measurementGeometry?.profileWidth !== undefined && <span className="viewer-profile-width-hint"
            title="浅蓝色窄带表示每条卡尺沿垂直于搜索方向的实际灰度投影宽度">浅蓝色采样带 · {measurementGeometry.profileWidth} px</span>}
          {measurementGeometry?.type === 'line' && <Button size="small" onClick={focusSearchLine}
            disabled={!clipSegmentToImage(measurementGeometry.start, measurementGeometry.end, actualWidth, actualHeight)}
            title="放大并居中显示图像内的搜索线与投影带，不改变测量参数">定位搜索线</Button>}
          <Button size="small" onClick={fit}>适应窗口</Button>
          <Button size="small" onClick={oneToOne}>原始比例</Button>
          {showRoiTools && roiClearable && <Button size="small" danger disabled={!roi || !roiEnabled} onClick={clearRoi}>清除选区</Button>}
          <span className="viewer-toolbar-divider" />
          <span className="viewer-toggle" title="像素化（默认）：放大时按真实像素方块显示，便于逐像素核对；关闭则使用平滑插值（缩小总览更顺眼）">像素化 <Switch size="small" checked={pixelated} onChange={setPixelated} /></span>
          <span className="viewer-toggle">显示覆盖层 <Switch size="small" checked={showOverlay} onChange={setShowOverlay} /></span>
          {/* 覆盖层按节点筛选：仅当存在多个节点来源时才有意义（单节点输出视图 App 已按节点过滤） */}
          {overlayNodeIds.length > 1 && (
            <Select
              size="small"
              mode="multiple"
              allowClear
              maxTagCount={1}
              style={{ minWidth: 118, maxWidth: 190 }}
              placeholder="覆盖层：全部节点"
              title="按节点筛选覆盖层（可多选）"
              value={overlayFilter}
              onChange={setOverlayFilter}
              options={overlayNodeIds.map((id) => ({ value: id, label: nodeLabels?.[id] ?? id }))}
            />
          )}
        </Space>
      </div>
      {geometryDrawMode && measurementGeometry?.type === 'line' && <div className="viewer-drawing-hint" aria-label="搜索线绘制提示">
        <div className="viewer-drawing-instructions"><span role="status">从起点拖到终点，松开完成；搜索线至少 2 px，Esc 取消。</span></div>
        <div className="viewer-drawing-feedback">{measurementDraft?.type === 'line' ? <output aria-label="搜索线绘制尺寸" aria-live="off">
          长度 {Math.hypot(measurementDraft.end.x - measurementDraft.start.x, measurementDraft.end.y - measurementDraft.start.y).toFixed(2)} px
          {' · '}方向 {(Math.atan2(measurementDraft.end.y - measurementDraft.start.y, measurementDraft.end.x - measurementDraft.start.x) * 180 / Math.PI).toFixed(1)}°
        </output> : <span>极性按起点 → 终点解释；投影长度和选边顺序在重绘后保留。</span>}</div>
      </div>}
      {geometryDrawMode && measurementGeometry?.type === 'circle' && <div className="viewer-drawing-hint" aria-label="标称圆圈选提示">
        <div className="viewer-drawing-instructions"><span role="status">{isThreePointDrawing
          ? circleDrawMode === 'threePointArc'
            ? `点选圆弧第 ${circleConstructionPoints.length + 1}/3 点：端点 → 经过点 → 另一端；Esc 取消。`
            : `沿目标圆边缘点选第 ${circleConstructionPoints.length + 1}/3 点；尽量分散选点，Esc 取消。`
          : circleDrawMode === 'diameter'
            ? '从圆边缘拖到直径另一端，松开完成；Esc 取消。'
            : '从圆心拖到目标边缘，松开完成；Esc 取消。'}</span>
        {isThreePointDrawing && <Button size="small" disabled={circleConstructionPoints.length === 0} onClick={() => {
          setCircleConstructionPoints(points => points.slice(0, -1)); setMeasurementDraft(undefined); setCircleConstructionIssue(undefined);
        }}>撤回上一点</Button>}</div>
        <div className="viewer-drawing-feedback">{circleConstructionIssue ? <span role="alert">{circleConstructionIssue}</span>
        : measurementDraft?.type === 'circle' ? <output aria-label="圈选尺寸" aria-live="off">
          圆心 ({measurementDraft.center.x.toFixed(2)}, {measurementDraft.center.y.toFixed(2)}) · 半径 {measurementDraft.radius.toFixed(2)} px
          {circleDrawMode === 'threePointArc' && ` · 圆弧 ${circleArcSpan(measurementDraft.angleStartDeg ?? -180, measurementDraft.angleEndDeg ?? 180).span.toFixed(1)}°`}
        </output> : <span>{circleDrawMode === 'threePointArc' ? '自动选择经过第 2 点的圆弧；完成后可旋转或反选圆弧。' : '完成圈选后可继续拖动圆心、半径和搜索带。'}</span>}</div>
      </div>}

      <div ref={hostRef} className={`viewer-stage ${loading ? 'is-loading' : ''}`} style={{ cursor: cursorStyle }}>
        {!previewUrl && !failed && <div className="image-placeholder">
          <span>{emptyHint}</span>
          {emptyAction && <Button size="small" type="primary" disabled={emptyAction.disabled} title={emptyAction.title} onClick={emptyAction.onClick}>{emptyAction.label}</Button>}
        </div>}
        {failed && <div className="image-placeholder error">图像加载失败：预览不可用或已被清理</div>}
        {previewUrl && !failed && (
          <Stage
            ref={stageRef}
            width={size.width}
            height={size.height}
            onWheel={onWheel}
            onMouseDown={onPointerDown}
            onTouchStart={onPointerDown}
            onMouseMove={onPointerMove}
            onTouchMove={onPointerMove}
            onMouseUp={onPointerUp}
            onTouchEnd={onPointerUp}
            onClick={onClick}
            onDblClick={commitPolygon}
            onMouseLeave={() => { setCursor(undefined); }}
          >
            <Layer imageSmoothingEnabled={!pixelated}>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                {image && <KonvaImage image={image} width={actualWidth} height={actualHeight} listening={false} />}
              </Group>
            </Layer>
            <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                {showOverlay && visibleOverlays.map((o) => <OverlayShape key={`${o.nodeId ?? ''}-${o.id}`} overlay={o} scale={view.scale} />)}
              </Group>
            </Layer>
            {measurementGeometry && <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                <MeasurementGeometryShape geometry={measurementDraft ?? measurementGeometry} scale={view.scale} editable={measurementGeometryEditable && !geometryDrawMode}
                  imageWidth={actualWidth} imageHeight={actualHeight} />
                {circleConstructionPoints.length > 0 && <Group listening={false}>
                  <Line points={[...circleConstructionPoints, ...(cursor ? [cursor] : [])].flatMap(point => [point.x, point.y])}
                    stroke="#fa8c16" strokeWidth={1 / view.scale} dash={[4 / view.scale, 4 / view.scale]} />
                  {circleConstructionPoints.map((point, index) => <Group key={index}>
                    <Circle name={`circle-construction-point-${index}`} x={point.x} y={point.y} radius={4 / view.scale}
                      fill="#fa8c16" stroke="#fff" strokeWidth={1.5 / view.scale} />
                    <Text x={point.x + 7 / view.scale} y={point.y - 14 / view.scale}
                      text={circleDrawMode === 'threePointArc' ? ['端点 1', '经过点', '端点 2'][index] : `${index + 1}`}
                      fill="#fa8c16" fontSize={12 / view.scale} />
                  </Group>)}
                </Group>}
              </Group>
            </Layer>}
            {showOverlay && measurementMarkers.length > 0 && <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                {measurementMarkers.map(marker => <OverlayShape key={marker.id} overlay={marker} scale={view.scale} />)}
              </Group>
            </Layer>}
            {caliperDiagnosticSelection && <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                <CaliperDiagnosticShape selection={caliperDiagnosticSelection} scale={view.scale} />
              </Group>
            </Layer>}
            <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                {roiDragOffset && (roi?.type === 'Brush' || roi?.type === 'Polygon')
                  ? <Group x={roiDragOffset.x} y={roiDragOffset.y}>
                    <RoiShape roi={roi} scale={view.scale} />
                    {roi.type === 'Polygon' && roiEnabled && <RoiHandles roi={roi} scale={view.scale} imageWidth={actualWidth} imageHeight={actualHeight} />}
                  </Group>
                  : <>
                    {brushPreview || roi?.type === 'Brush'
                      ? <>
                        <BrushRoiShape strokes={roi?.type === 'Brush' ? roi.strokes : []} activeStroke={brushPreview} />
                        {draft?.type !== 'Brush' && <RoiShape roi={draft} scale={view.scale} draft />}
                      </>
                      : <>
                        <RoiShape roi={roi} scale={view.scale} />
                        <RoiShape roi={draft} scale={view.scale} draft />
                      </>}
                  </>}
                {roi && roiEnabled && tool === 'pan' && !draft && !brushPreview && !roiDragOffset
                  && <RoiHandles roi={roi} scale={view.scale} imageWidth={actualWidth} imageHeight={actualHeight} />}
              </Group>
            </Layer>
          </Stage>
        )}
        {loading && <div className="viewer-loading">运行中…</div>}
      </div>
      <div className="viewer-readout" role="group" aria-label="图像状态栏">
        <span className="viewer-image-size">{actualWidth > 0 ? `${actualWidth} × ${actualHeight}` : '无图像'}</span>
        <span className="viewer-zoom">{Math.round(view.scale * 100)}%</span>
        <span className="viewer-pixel-readout">{cursor && image ? <>
          <span>X {Math.floor(cursor.x)} · Y {Math.floor(cursor.y)}</span>
          <span>{pixel ? `${pixel.grayscale ? `灰度 ${pixel.r}` : `RGB (${pixel.r}, ${pixel.g}, ${pixel.b})`}${pixel.a < 255 ? ` · A ${pixel.a}` : ''}` : pixelBuffer === null ? '像素值不可用' : ''}</span>
        </> : <span className="viewer-pixel-placeholder">移动鼠标查看像素</span>}</span>
        <span className="viewer-roi-target" title={roiEnabled ? `选区编辑作用于：${roiTargetLabel || '当前图像'}` : '只读：该节点/该图像不支持编辑选区（未开启 ROI 能力、正在查看预览图像或图像尚未产出）'}>{roiEnabled ? `选区 → ${roiTargetLabel || '当前图像'}` : '只读 · 不可编辑选区'}</span>
      </div>
    </div>
  );
}
