/**
 * ImageViewer 的 Konva 图形组件与图像加载 Hook；纯几何计算统一来自 ./imageViewerGeometry。
 */
import type { Point, CaliperDiagnosticSelection } from './imageViewerTypes';
import type { VisionOverlay, VisionRoi, VisionRoiBrushStroke } from '../types';
import { memo, useEffect, useState } from 'react';
import { Arrow, Circle, Group, Line, Rect, Shape, Text } from 'react-konva';
import { buildCaliperCircleAngles, circleArcSpan, normalizeCaliperAngle } from '../caliperCircleSampling';
import {
  brushPath, caliperCircleArcHandles, caliperCircleBandHandles, caliperCircleProfileWidthHandle,
  caliperLineProfileSpanHandles, caliperLineProfileWidthHandle, circleRadiusHandle, clamp,
  flatten, MIN_EDGE_HANDLE_SPAN, polygonHandleIndices, polygonPreviewPoints,
  type MeasurementGeometry
} from './imageViewerGeometry';

export * from './imageViewerGeometry';

export function CaliperDiagnosticShape({ selection, scale }: { selection: CaliperDiagnosticSelection; scale: number }) {
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

export function useHtmlImage(url?: string) {
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

export function OverlayShape({ overlay, scale }: { overlay: VisionOverlay; scale: number }) {
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
    case 'Region':
      return <Shape listening={false} fill={overlay.fill ?? 'rgba(82, 196, 26, 0.45)'} sceneFunc={(context, shape) => {
        context.beginPath();
        for (const run of overlay.runs ?? []) context.rect(run.startColumn, run.row, run.endColumn - run.startColumn + 1, 1);
        context.fillShape(shape);
      }} />;
    case 'Text':
      return <Text listening={false} x={x} y={y} text={overlay.label ?? ''} fill={stroke} fontSize={14 / scale} fontFamily="monospace" />;
    default:
      return null;
  }
}

export const BrushStrokeLine = memo(function BrushStrokeLine({ item, stroke }: { item: VisionRoiBrushStroke; stroke: string }) {
  return <Line name={`roi-brush-stroke-${item.mode.toLowerCase()}`} points={brushPath(item.points)} stroke={stroke}
    strokeWidth={item.width} lineCap="round" lineJoin="round" opacity={item.mode === 'Add' ? 0.28 : 1}
    globalCompositeOperation={item.mode === 'Erase' ? 'destination-out' : 'source-over'} />;
});

export const BrushStrokeList = memo(function BrushStrokeList({ strokes, stroke }: {
  strokes: readonly VisionRoiBrushStroke[]; stroke: string;
}) {
  return <>{strokes.map((item, index) => <BrushStrokeLine key={index} item={item} stroke={stroke} />)}</>;
});

export const BrushRoiShape = memo(function BrushRoiShape({ strokes, activeStroke, stroke = '#ff4d4f' }: {
  strokes: readonly VisionRoiBrushStroke[]; activeStroke?: VisionRoiBrushStroke; stroke?: string;
}) {
  return <Group listening={false}>
    <BrushStrokeList strokes={strokes} stroke={stroke} />
    {activeStroke && <BrushStrokeLine item={activeStroke} stroke={stroke} />}
  </Group>;
});

export function RoiShape({ roi, scale, draft = false }: { roi?: VisionRoi; scale: number; draft?: boolean }) {
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
export function RoiHandles({ roi, scale, imageWidth, imageHeight }: { roi: VisionRoi; scale: number; imageWidth: number; imageHeight: number }) {
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

export function MeasurementGeometryShape({ geometry, scale, editable, imageWidth, imageHeight }: {
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
