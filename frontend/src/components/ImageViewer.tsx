import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Button, InputNumber, Segmented, Select, Space, Switch, Tooltip } from 'antd';
import { Arrow, Circle, Group, Image as KonvaImage, Layer, Line, Rect, Stage, Text } from 'react-konva';
import type { OverlayPoint, VisionOverlay, VisionRoi, VisionRoiBrushStroke } from '../types';
import { roiConfigurationIssue } from '../nodeConfigurationState';
import { createPixelBuffer, readPixel } from '../imagePixels';

type ToolMode = 'pan' | 'rect' | 'circle' | 'polygon' | 'brush' | 'eraser';
type ViewState = { scale: number; x: number; y: number };
type Point = { x: number; y: number };
type RoiMoveBounds = { minX: number; maxX: number; minY: number; maxY: number };
type RoiDragMode = 'move' | 'nw' | 'ne' | 'sw' | 'se' | 'e' | 'w' | 'n' | 's' | `vertex:${number}`;
type MeasurementDragMode = 'start' | 'end' | 'profileStart' | 'profileEnd' | 'moveLine' | 'center' | 'moveCircle' | 'radius' | 'angleStart' | 'angleEnd' | 'innerBand' | 'outerBand';

export type MeasurementGeometry =
  | { type: 'line'; start: Point; end: Point; profileSpan?: number; profileCount?: number }
  | { type: 'circle'; center: Point; radius: number; band?: number; angleStartDeg?: number; angleEndDeg?: number;
      caliperCount?: number; samplingMode?: string; angleStepDeg?: number; centerEditable?: boolean; radiusEditable?: boolean };

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
function caliperLineProfileSpanHandles(geometry: Extract<MeasurementGeometry, { type: 'line' }>): { start: Point; end: Point } | undefined {
  if (geometry.profileCount === undefined) return undefined;
  const dx = geometry.end.x - geometry.start.x, dy = geometry.end.y - geometry.start.y;
  const length = Math.hypot(dx, dy);
  if (length <= 0) return undefined;
  const span = geometry.profileSpan && geometry.profileSpan > 0 ? geometry.profileSpan : length;
  const tangent = { x: -dy / length, y: dx / length };
  const midpoint = { x: (geometry.start.x + geometry.end.x) / 2, y: (geometry.start.y + geometry.end.y) / 2 };
  return {
    start: { x: midpoint.x - tangent.x * span / 2, y: midpoint.y - tangent.y * span / 2 },
    end: { x: midpoint.x + tangent.x * span / 2, y: midpoint.y + tangent.y * span / 2 }
  };
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

function circleArcSpan(start: number, end: number) {
  const rawSpan = end - start;
  if (Math.abs(rawSpan) < 1e-9) return { span: 0, fullCircle: false };
  let span = rawSpan % 360;
  if (Math.abs(span) < 1e-9) return { span: 360, fullCircle: true };
  if (span < 0) span += 360;
  return { span, fullCircle: false };
}

/** Match the backend caliper-circle sampling rules so the editable preview shows the actual search rays. */
export function buildCaliperCircleAngles(geometry: Extract<MeasurementGeometry, { type: 'circle' }>): number[] {
  const start = geometry.angleStartDeg ?? -180;
  const end = geometry.angleEndDeg ?? 180;
  const { span, fullCircle } = circleArcSpan(start, end);
  if (span === 0) return [start];

  let count: number;
  let effectiveStep: number;
  if (geometry.samplingMode === 'step') {
    effectiveStep = clamp(geometry.angleStepDeg ?? 5, 0.5, 45);
    const intervalCount = span / effectiveStep;
    const nearestIntervalCount = Math.round(intervalCount);
    const reachesEnd = nearestIntervalCount > 0
      && Math.abs(nearestIntervalCount * effectiveStep - span) <= 1e-9;
    count = fullCircle && reachesEnd ? nearestIntervalCount : Math.floor(intervalCount) + 1;
    const maximumCount = fullCircle ? 360 : 361;
    if (count > maximumCount) {
      effectiveStep = span / (fullCircle ? maximumCount : maximumCount - 1);
      count = maximumCount;
    }
  } else {
    count = Math.round(clamp(geometry.caliperCount ?? 72, 3, 361));
    effectiveStep = fullCircle ? span / count : span / (count - 1);
  }

  return Array.from({ length: count }, (_, index) => start + index * effectiveStep);
}

/** 控制点命中半径（屏幕像素），缩放后仍保持同样的手感 */
const HANDLE_HIT = 10;
const MAX_POLYGON_HANDLES = 32;
const MIN_EDGE_HANDLE_SPAN = 18;

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

function caliperCircleArcHandles(geometry: Extract<MeasurementGeometry, { type: 'circle' }>, scale: number, width: number, height: number) {
  const start = geometry.angleStartDeg ?? -180;
  const { span, fullCircle } = circleArcSpan(start, geometry.angleEndDeg ?? 180);
  if (fullCircle || width <= 0 || height <= 0) return undefined;
  const offset = (HANDLE_HIT + 4) / scale;
  const smallCircle = geometry.radius * scale < HANDLE_HIT * 3;
  const atAngle = (angleDeg: number, radius: number): Point => {
    const radians = angleDeg * Math.PI / 180;
    return {
      x: clamp(geometry.center.x + Math.max(0, radius) * Math.cos(radians), 0, width),
      y: clamp(geometry.center.y + Math.max(0, radius) * Math.sin(radians), 0, height)
    };
  };
  return {
    start: atAngle(start, geometry.radius + (smallCircle ? offset : -offset)),
    end: atAngle(start + span, geometry.radius + offset * (smallCircle ? 2 : 1))
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
  let inside = false;
  const pts = polygonPreviewPoints(roi.points);
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const xi = pts[i].x; const yi = pts[i].y; const xj = pts[j].x; const yj = pts[j].y;
    if ((yi > point.y) !== (yj > point.y) && point.x < ((xj - xi) * (point.y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

function brushRoiBounds(roi: Extract<VisionRoi, { type: 'Brush' }>): RoiMoveBounds | undefined {
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const stroke of roi.strokes) {
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
    const dx = geometry.end.x - geometry.start.x, dy = geometry.end.y - geometry.start.y;
    const length = Math.hypot(dx, dy);
    const directionX = length > 0 ? dx / length : 1, directionY = length > 0 ? dy / length : 0;
    const labelOffset = 7 / scale;
    const labelNormalX = -directionY * labelOffset, labelNormalY = directionX * labelOffset;
    const count = geometry.profileCount === undefined ? 0 : Math.round(clamp(geometry.profileCount, 3, 101));
    const span = geometry.profileSpan && geometry.profileSpan > 0 ? geometry.profileSpan : length;
    const tangent = length > 0 ? { x: -dy / length, y: dx / length } : { x: 0, y: 0 };
    const profileHandles = editable && count >= 3 ? caliperLineProfileSpanHandles(geometry) : undefined;
    return <Group listening={false}>
      {count > 0 && Array.from({ length: count }, (_, index) => {
        const offset = count === 1 ? 0 : (index / (count - 1) - 0.5) * span;
        const x = tangent.x * offset, y = tangent.y * offset;
        return <Line key={index} points={[geometry.start.x + x, geometry.start.y + y, geometry.end.x + x, geometry.end.y + y]}
          stroke="#91d5ff" opacity={0.6} strokeWidth={0.8 / scale} />;
      })}
      <Arrow points={[geometry.start.x, geometry.start.y, geometry.end.x, geometry.end.y]} stroke="#fa8c16" fill="#fa8c16"
        pointerLength={8 / scale} pointerWidth={8 / scale} strokeWidth={2.5 / scale} />
      <Text x={geometry.start.x - directionX * 15 / scale + labelNormalX} y={geometry.start.y - directionY * 15 / scale + labelNormalY} text="起点" fill="#1677ff"
        fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} listening={false} />
      <Text x={geometry.end.x + directionX * 7 / scale + labelNormalX} y={geometry.end.y + directionY * 7 / scale + labelNormalY} text="终点" fill="#722ed1"
        fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} listening={false} />
      {editable && <>
        <Circle x={geometry.start.x} y={geometry.start.y} radius={handleRadius} fill="#1677ff" stroke="#fff" strokeWidth={1.5 / scale} />
        <Circle x={geometry.end.x} y={geometry.end.y} radius={handleRadius} fill="#722ed1" stroke="#fff" strokeWidth={1.5 / scale} />
        {profileHandles && <>
          <Circle name="measurement-profile-span-start" x={profileHandles.start.x} y={profileHandles.start.y} radius={handleRadius}
            fill="#13c2c2" stroke="#fff" strokeWidth={1.5 / scale} />
          <Circle name="measurement-profile-span-end" x={profileHandles.end.x} y={profileHandles.end.y} radius={handleRadius}
            fill="#13c2c2" stroke="#fff" strokeWidth={1.5 / scale} />
        </>}
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
  const arcHandles = editable ? caliperCircleArcHandles(geometry, scale, imageWidth, imageHeight) : undefined;
  const bandHandles = editable ? caliperCircleBandHandles(geometry, scale, imageWidth, imageHeight) : undefined;
  const radiusHandle = editable && geometry.radiusEditable !== false
    ? circleRadiusHandle(geometry.center, geometry.radius, imageWidth, imageHeight) : undefined;
  const boundary = (angleDeg: number) => {
    const radians = angleDeg * Math.PI / 180;
    return [geometry.center.x + inner * Math.cos(radians), geometry.center.y + inner * Math.sin(radians),
      geometry.center.x + outer * Math.cos(radians), geometry.center.y + outer * Math.sin(radians)];
  };
  const firstSpoke = boundary(angles[0] ?? start);
  const firstSpokeAngle = (angles[0] ?? start) * Math.PI / 180;
  const labelOffset = 7 / scale;
  const labelNormalX = -Math.sin(firstSpokeAngle) * labelOffset;
  const labelNormalY = Math.cos(firstSpokeAngle) * labelOffset;
  const labelDirectionX = Math.cos(firstSpokeAngle), labelDirectionY = Math.sin(firstSpokeAngle);
  return <Group listening={false}>
    {band > 0 && <>
      <Circle x={geometry.center.x} y={geometry.center.y} radius={inner} stroke="#fa8c16" strokeWidth={1 / scale} dash={[4 / scale, 4 / scale]} />
      <Circle x={geometry.center.x} y={geometry.center.y} radius={outer} stroke="#fa8c16" strokeWidth={1 / scale} dash={[4 / scale, 4 / scale]} />
    </>}
    <Circle x={geometry.center.x} y={geometry.center.y} radius={geometry.radius} stroke="#fa8c16" strokeWidth={2.5 / scale} />
    {angles.map((angleDeg, index) => {
      const radians = angleDeg * Math.PI / 180;
      const points = [geometry.center.x + inner * Math.cos(radians), geometry.center.y + inner * Math.sin(radians),
        geometry.center.x + outer * Math.cos(radians), geometry.center.y + outer * Math.sin(radians)];
      return index === 0
        ? <Arrow key={`spoke-${index}`} points={points} stroke="#fa8c16" fill="#fa8c16" opacity={0.9}
          pointerLength={6 / scale} pointerWidth={6 / scale} strokeWidth={1.5 / scale} />
        : <Line key={`spoke-${index}`} points={points} stroke="#91d5ff" opacity={0.55} strokeWidth={0.7 / scale} />;
    })}
    <Text x={firstSpoke[0] - labelDirectionX * 15 / scale + labelNormalX} y={firstSpoke[1] - labelDirectionY * 15 / scale + labelNormalY} text="起点" fill="#1677ff"
      fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} listening={false} />
    <Text x={firstSpoke[2] + labelDirectionX * 7 / scale + labelNormalX} y={firstSpoke[3] + labelDirectionY * 7 / scale + labelNormalY} text="终点" fill="#722ed1"
      fontSize={12 / scale} fontStyle="bold" stroke="#fff" strokeWidth={2 / scale} listening={false} />
    {!fullCircle && span > 0 && <>
      <Line points={boundary(start)} stroke="#fa8c16" strokeWidth={1.5 / scale} />
      <Line points={boundary(start + span)} stroke="#fa8c16" strokeWidth={1.5 / scale} />
    </>}
    <Line points={[geometry.center.x - handleRadius * 1.7, geometry.center.y, geometry.center.x + handleRadius * 1.7, geometry.center.y]} stroke="#fa8c16" strokeWidth={1.5 / scale} />
    <Line points={[geometry.center.x, geometry.center.y - handleRadius * 1.7, geometry.center.x, geometry.center.y + handleRadius * 1.7]} stroke="#fa8c16" strokeWidth={1.5 / scale} />
    {editable && <>
      {geometry.centerEditable !== false && <Circle x={geometry.center.x} y={geometry.center.y} radius={handleRadius} fill="#fa8c16" stroke="#fff" strokeWidth={1.5 / scale} />}
      {radiusHandle && <Circle x={radiusHandle.x} y={radiusHandle.y} radius={handleRadius} fill="#fa8c16" stroke="#fff" strokeWidth={1.5 / scale} />}
      {arcHandles && <>
        <Circle x={arcHandles.start.x} y={arcHandles.start.y} radius={handleRadius} fill="#1677ff" stroke="#fff" strokeWidth={1.5 / scale} />
        <Circle x={arcHandles.end.x} y={arcHandles.end.y} radius={handleRadius} fill="#722ed1" stroke="#fff" strokeWidth={1.5 / scale} />
      </>}
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
  roiTargetLabel,
  loading,
  onRoiChange,
  measurementGeometry,
  measurementGeometryEditable = false,
  onMeasurementGeometryChange,
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
  roiTargetLabel?: string;
  loading?: boolean;
  onRoiChange: (roi?: VisionRoi) => void;
  /** Search geometry authored in the displayed image coordinate frame. */
  measurementGeometry?: MeasurementGeometry;
  measurementGeometryEditable?: boolean;
  onMeasurementGeometryChange?: (geometry: MeasurementGeometry) => void;
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
  // 像素化（最近邻）默认开：放大即真实像素方块；开关可切回平滑插值
  const [pixelated, setPixelated] = useState(readPixelated);
  useEffect(() => { persistPixelated(pixelated); }, [pixelated]);
  const [draft, setDraft] = useState<VisionRoi>();
  const gestureStart = useRef<Point | undefined>(undefined);
  const geometryGestureStart = useRef<Point | undefined>(undefined);
  const geometryDrag = useRef<{ mode: MeasurementDragMode; origin: MeasurementGeometry; start: Point } | undefined>(undefined);
  const panStart = useRef<{ pointer: Point; view: ViewState } | undefined>(undefined);
  const roiDrag = useRef<{ mode: RoiDragMode; origin: VisionRoi; start: Point; moveBounds?: RoiMoveBounds } | undefined>(undefined);
  const [roiDragOffset, setRoiDragOffset] = useState<Point>();
  const polygonPoints = useRef<OverlayPoint[]>([]);
  const brushStroke = useRef<{ baseStrokes: VisionRoiBrushStroke[]; activeStroke: VisionRoiBrushStroke } | undefined>(undefined);
  const [brushPreview, setBrushPreview] = useState<VisionRoiBrushStroke>();
  const lastFocusTick = useRef(focusTick ?? 0);
  const pointerUpHandler = useRef<() => void>(() => undefined);
  const pointerCancelHandler = useRef<() => void>(() => undefined);
  const { image, failed } = useHtmlImage(previewUrl);
  const actualWidth = imageWidth || image?.naturalWidth || 0;
  const actualHeight = imageHeight || image?.naturalHeight || 0;
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
    }
  }, [roiEnabled]);
  useEffect(() => {
    setOverlayFilter([]);
    setCursor(undefined);
    setDraft(undefined);
    setBrushPreview(undefined);
    setMeasurementDraft(undefined);
    setGeometryDrawMode(false);
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
    polygonPoints.current = [];
    brushStroke.current = undefined;
    roiDrag.current = undefined;
    setRoiDragOffset(undefined);
  }, [previewUrl]);
  useEffect(() => {
    if (measurementGeometryEditable) return;
    setGeometryDrawMode(false);
    setMeasurementDraft(undefined);
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
  }, [measurementGeometryEditable]);

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
      const profileHandles = target.profileCount !== undefined ? caliperLineProfileSpanHandles(target) : undefined;
      const candidates: [MeasurementDragMode, Point][] = [['start', target.start], ['end', target.end]];
      if (profileHandles) candidates.push(['profileStart', profileHandles.start], ['profileEnd', profileHandles.end]);
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
    if (target.centerEditable !== false) candidates.push(['center', target.center]);
    if (target.radiusEditable !== false) candidates.push(['radius', circleRadiusHandle(target.center, target.radius, actualWidth, actualHeight)]);
    if (arcHandles) candidates.push(['angleStart', arcHandles.start], ['angleEnd', arcHandles.end]);
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
    onRoiChange({ type: 'Brush', strokes: [...active.baseStrokes, active.activeStroke] });
  };

  const undoBrushStroke = () => {
    if (roi?.type !== 'Brush') return;
    const strokes = roi.strokes.slice(0, -1);
    onRoiChange(strokes.length > 0 ? { type: 'Brush', strokes } : undefined);
  };

  const onPointerDown = () => {
    const p = pointer();
    if (!p) return;
    setCursor(p);
    const ip = imagePoint(p);
    if (geometryDrawMode && measurementGeometryEditable && measurementGeometry) {
      if (!inside(ip)) return;
      geometryGestureStart.current = ip;
      setMeasurementDraft(measurementGeometry.type === 'line'
        ? { type: 'line', start: ip, end: ip }
        : { ...measurementGeometry, center: ip, radius: 0 });
      return;
    }
    if (measurementGeometryEditable && measurementGeometry) {
      const handle = hitMeasurementHandle(measurementGeometry, p);
      if (handle) {
        geometryDrag.current = { mode: handle, origin: measurementGeometry, start: ip };
        return;
      }
    }
    // 已有选区时优先响应拖动：控制点缩放 > 选区内部平移 > 平移画布 / 新建选区
    if (roi && roiEnabled && tool !== 'brush' && tool !== 'eraser') {
      const handle = hitRoiHandle(roi, p);
      if (handle) {
        roiDrag.current = { mode: handle, origin: roi, start: imagePoint(p) };
        return;
      }
      if (tool !== 'polygon' && pointInsideRoi(roi, imagePoint(p))) {
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
    if (geometryGestureStart.current && measurementGeometry) {
      const start = geometryGestureStart.current;
      const end = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
      setMeasurementDraft(measurementGeometry.type === 'line'
        ? { type: 'line', start, end }
        : { ...measurementGeometry, center: start, radius: Math.min(Math.hypot(end.x - start.x, end.y - start.y), maxInteractiveCircleRadius(start, actualWidth, actualHeight)) });
      return;
    }
    if (geometryDrag.current) {
      const { mode, origin, start } = geometryDrag.current;
      const current = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
      if (origin.type === 'line') {
        if (mode === 'start' || mode === 'end') {
          setMeasurementDraft({ ...origin, [mode]: current });
        } else if (mode === 'profileStart' || mode === 'profileEnd') {
          const dx = origin.end.x - origin.start.x, dy = origin.end.y - origin.start.y;
          const length = Math.hypot(dx, dy);
          if (length > 0) {
            const tangent = { x: -dy / length, y: dx / length };
            const midpoint = { x: (origin.start.x + origin.end.x) / 2, y: (origin.start.y + origin.end.y) / 2 };
            const halfSpan = Math.abs((current.x - midpoint.x) * tangent.x + (current.y - midpoint.y) * tangent.y);
            setMeasurementDraft({ ...origin, profileSpan: clamp(halfSpan * 2, 1, 10_000) });
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
      } else if (mode === 'center' || mode === 'moveCircle') {
        const center = mode === 'center' ? current : {
          x: origin.center.x + current.x - start.x,
          y: origin.center.y + current.y - start.y
        };
        setMeasurementDraft({ ...origin, center: {
          x: clampCircleCenter(center.x, origin.radius, actualWidth),
          y: clampCircleCenter(center.y, origin.radius, actualHeight)
        } });
      } else if (mode === 'radius') {
        const startDistance = Math.hypot(start.x - origin.center.x, start.y - origin.center.y);
        const currentDistance = Math.hypot(current.x - origin.center.x, current.y - origin.center.y);
        setMeasurementDraft({ ...origin, radius: clamp(origin.radius + currentDistance - startDistance, 1, 10_000_000) });
      } else if (mode === 'angleStart' || mode === 'angleEnd') {
        const startAngle = origin.angleStartDeg ?? -180;
        const arcEndAngle = startAngle + circleArcSpan(startAngle, origin.angleEndDeg ?? 180).span;
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
        : next.radius >= 1)) onMeasurementGeometryChange(next);
      setMeasurementDraft(undefined);
      setGeometryDrawMode(false);
      return;
    }
    if (geometryDrag.current) {
      geometryDrag.current = undefined;
      const next = measurementDraft;
      if (next && onMeasurementGeometryChange && (next.type === 'line'
        ? Math.hypot(next.end.x - next.start.x, next.end.y - next.start.y) >= 2
        : next.radius >= 1)) onMeasurementGeometryChange(next);
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
        onRoiChange(next);
      }
      setRoiDragOffset(undefined);
      setDraft(undefined);
      return;
    }
    if (gestureStart.current && draft) {
      if (draft.type === 'Rectangle' && draft.width >= 2 && draft.height >= 2) onRoiChange(draft);
      if (draft.type === 'Circle' && draft.radius >= 1) onRoiChange(draft);
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
  };

  useEffect(() => { pointerUpHandler.current = onPointerUp; });
  useEffect(() => { pointerCancelHandler.current = onPointerCancel; });
  useEffect(() => {
    const releaseInteraction = () => pointerUpHandler.current();
    const cancelInteraction = () => pointerCancelHandler.current();
    // Konva's stage only receives mouse/touch end while the pointer is over the viewer.
    // Listen on document as well so drags released over a toolbar or outside the canvas commit.
    document.addEventListener('mouseup', releaseInteraction);
    document.addEventListener('touchend', releaseInteraction);
    document.addEventListener('touchcancel', cancelInteraction);
    return () => {
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
    setDraft({ type: 'Polygon', points: polygonPoints.current });
  };

  const commitPolygon = () => {
    if (tool !== 'polygon' || polygonPoints.current.length < 3) return;
    onRoiChange({ type: 'Polygon', points: polygonPoints.current });
    polygonPoints.current = [];
    setDraft(undefined);
  };

  const clearRoi = () => {
    polygonPoints.current = [];
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    onRoiChange(undefined);
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
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
        setTool('pan');
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const cursorStyle = useMemo(() => {
    if (geometryDrawMode) return 'crosshair';
    if (panStart.current || roiDrag.current || geometryDrag.current) return 'grabbing';
    if (cursorScreen) {
      if (measurementGeometryEditable && measurementGeometry) {
        const handle = hitMeasurementHandle(measurementGeometry, cursorScreen);
        if (handle === 'radius') return 'nwse-resize';
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
      if (roi && roiEnabled && tool !== 'brush' && tool !== 'eraser') {
        const handle = hitRoiHandle(roi, cursorScreen);
        if (handle === 'nw' || handle === 'se') return 'nwse-resize';
        if (handle === 'ne' || handle === 'sw') return 'nesw-resize';
        if (handle === 'w' || handle === 'e') return 'ew-resize';
        if (handle === 'n' || handle === 's') return 'ns-resize';
        if (handle?.startsWith('vertex:')) return 'move';
        if (tool === 'pan' && (roi.type === 'Rectangle' || roi.type === 'Circle')
          && pointInsideRoi(roi, imagePoint(cursorScreen))) return 'move';
      }
    }
    return tool === 'pan' ? 'grab' : 'crosshair';
  }, [actualHeight, actualWidth, cursorScreen, geometryDrawMode, measurementGeometry, measurementGeometryEditable,
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

  return (
    <div className="vision-viewer">
      <div className="viewer-toolbar">
        <Space size={5} wrap>
          {showRoiTools && <Segmented
            size="small"
            value={tool}
            onChange={(value) => { polygonPoints.current = []; brushStroke.current = undefined; setBrushPreview(undefined); gestureStart.current = undefined;
              geometryGestureStart.current = undefined; geometryDrag.current = undefined; setGeometryDrawMode(false); setMeasurementDraft(undefined);
              roiDrag.current = undefined; setRoiDragOffset(undefined); setDraft(undefined); setTool(value as ToolMode); }}
            options={[
              { value: 'pan', label: <Tooltip title="平移">平移</Tooltip> },
              { value: 'rect', disabled: !roiEnabled, label: <Tooltip title="矩形选区">矩形</Tooltip> },
              { value: 'circle', disabled: !roiEnabled, label: <Tooltip title="圆形选区">圆形</Tooltip> },
              { value: 'polygon', disabled: !roiEnabled, label: <Tooltip title="多边形选区 · 双击完成">多边形</Tooltip> },
              { value: 'brush', disabled: !roiEnabled, label: <Tooltip title="笔刷：按住鼠标拖动绘制选区">笔刷</Tooltip> },
              { value: 'eraser', disabled: !roiEnabled || roi?.type !== 'Brush', label: <Tooltip title="橡皮：按住鼠标拖动擦除">橡皮</Tooltip> }
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
          {showRoiTools && roi?.type === 'Brush' && <Button size="small" disabled={!roiEnabled} onClick={undoBrushStroke}>撤销笔迹</Button>}
          {measurementGeometry && measurementGeometryEditable && (measurementGeometry.type === 'line'
            || measurementGeometry.centerEditable !== false && measurementGeometry.radiusEditable !== false) && <>
            <span className="viewer-toolbar-divider" />
            <Button size="small" type={geometryDrawMode ? 'primary' : 'default'}
              onClick={() => { setMeasurementDraft(undefined); geometryGestureStart.current = undefined; setGeometryDrawMode(value => !value); setTool('pan'); }}
              title={geometryDrawMode ? '退出绘制模式' : measurementGeometry.type === 'circle'
                ? '径向卡尺由内侧起点指向外侧终点；拖动重绘圆，橙色调整圆心/半径，蓝色/紫色调整圆弧起止角，青色/绿色调整搜索带'
                : measurementGeometry.profileCount !== undefined
                  ? '箭头由起点指向终点；蓝色起点、紫色终点，青色控制点对称调整沿边缘的采样跨度。拖动端点或线段可微调搜索线'
                  : '箭头由起点指向终点；蓝色起点、紫色终点。拖动端点或线段可微调搜索线'}>
              {geometryDrawMode ? '取消绘制' : measurementGeometry.type === 'line' ? '重绘搜索线' : '重绘标称圆'}
            </Button>
          </>}
          <Button size="small" onClick={fit}>适应窗口</Button>
          <Button size="small" onClick={oneToOne}>原始比例</Button>
          {showRoiTools && <Button size="small" danger disabled={!roi || !roiEnabled} onClick={clearRoi}>清除选区</Button>}
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
            onMouseLeave={() => { setCursor(undefined); panStart.current = undefined; if (brushStroke.current) finishBrushStroke(); }}
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
                <MeasurementGeometryShape geometry={measurementDraft ?? measurementGeometry} scale={view.scale} editable={measurementGeometryEditable}
                  imageWidth={actualWidth} imageHeight={actualHeight} />
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
                {roi && roiEnabled && !draft && !brushPreview && !roiDragOffset && <RoiHandles roi={roi} scale={view.scale} imageWidth={actualWidth} imageHeight={actualHeight} />}
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
