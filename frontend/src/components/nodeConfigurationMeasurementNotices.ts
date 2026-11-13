/**
 * NodeConfigurationDialog 的测量几何提示（巨型组件拆分，2026-11-13）。
 *
 * 纯函数：输入已解析的几何、图像边界与参数读取器，输出可直接渲染的提示对象；
 * 不读写组件状态，也不依赖 React 组件树。语义与拆出前逐行一致。
 */
import { buildCaliperCircleAngles } from './ImageViewer';
import type { MeasurementGeometry } from './ImageViewer';
import { clipLineToImage } from './nodeConfigurationUtils';

/** 从草稿参数里读数值的读取器（对话框内的 geometryNumber）。 */
export type GeometryNumber = (key: string, fallback: number) => number;

export type MeasurementGeometryNotice = { type: 'warning' | 'info'; title: string; description: string };

export type CircleSamplingModel = {
  mode: string;
  geometry: Extract<MeasurementGeometry, { type: 'circle' }> | undefined;
  angles: number[];
  samplingNotice?: string;
  fitConditionNotice?: string;
};

/** 圆卡尺采样：数量/步长模式、实际角度列表，以及步长被上限截断、圆弧接近直线的提示。 */
export function circleSamplingModel(
  draft: Record<string, unknown>, isCaliperCircle: boolean, geometryNumber: GeometryNumber
): CircleSamplingModel {
  const mode = String(draft.samplingMode ?? (draft.angleStepDeg !== undefined ? 'step' : 'count')).toLowerCase();
  const geometry: Extract<MeasurementGeometry, { type: 'circle' }> | undefined = isCaliperCircle ? {
    type: 'circle', center: { x: geometryNumber('centerX', 320), y: geometryNumber('centerY', 240) },
    radius: geometryNumber('radius', 100),
    angleStartDeg: geometryNumber('angleStartDeg', -180), angleEndDeg: geometryNumber('angleEndDeg', 180),
    samplingMode: mode,
    caliperCount: geometryNumber('caliperCount', 72), angleStepDeg: geometryNumber('angleStepDeg', 5)
  } : undefined;
  const angles = geometry ? buildCaliperCircleAngles(geometry) : [];
  const samplingNotice = (() => {
    if (!geometry || mode !== 'step') return undefined;
    const requestedStep = Math.max(0.5, Math.min(45, geometryNumber('angleStepDeg', 5)));
    if (angles.length < 2) return undefined;
    const effectiveStep = angles[1] - angles[0];
    if (effectiveStep <= requestedStep + 1e-9) return undefined;
    const stepText = Number(effectiveStep.toFixed(3)).toString();
    return `此圆弧按 ${requestedStep}° 步长会超过卡尺数量上限；预览和执行将使用 ${angles.length} 条卡尺，实际步长约 ${stepText}°。如需精确控制数量，可切换到计数模式。`;
  })();
  const fitConditionNotice = (() => {
    if (!geometry || angles.length < 3) return undefined;
    const points = angles.map(angle => angle * Math.PI / 180);
    const centerX = points.reduce((sum, angle) => sum + Math.cos(angle), 0) / points.length;
    const centerY = points.reduce((sum, angle) => sum + Math.sin(angle), 0) / points.length;
    let xx = 0; let yy = 0; let xy = 0;
    for (const angle of points) {
      const dx = Math.cos(angle) - centerX;
      const dy = Math.sin(angle) - centerY;
      xx += dx * dx; yy += dy * dy; xy += dx * dy;
    }
    const spread = xx + yy;
    const normalizedDeterminant = spread > 0 ? (xx * yy - xy * xy) / (spread * spread) : 0;
    // Keep the authoring preview aligned with the backend's minimum normalized circle-fit spread.
    if (normalizedDeterminant > 2e-3) return undefined;
    return '当前圆弧的理论测点分布接近直线，可能触发退化圆拟合。请扩大角度覆盖；单纯增加卡尺数量不能替代更宽的圆弧。';
  })();
  return { mode, geometry, angles, samplingNotice, fitConditionNotice };
}

export type MeasurementBoundsNotices = {
  circleBoundsNotice?: MeasurementGeometryNotice;
  lineBoundsNotice?: MeasurementGeometryNotice;
  searchLineBoundsNotice?: MeasurementGeometryNotice;
};

/** 图像边界预检：圆卡尺 / 多卡尺剖面 / 搜索线在图像外或裁切后过短的提示。 */
export function measurementBoundsNotices(params: {
  catalogType: string;
  geometry: MeasurementGeometry | undefined;
  imageFrameReady: boolean;
  width: number | undefined;
  height: number | undefined;
  boundsContext: string;
  geometryNumber: GeometryNumber;
}): MeasurementBoundsNotices {
  const { catalogType, geometry, imageFrameReady, boundsContext, geometryNumber } = params;
  const { width, height } = params;

  const circleBoundsNotice = (() => {
    if (catalogType !== 'measure.caliperCircle' || geometry?.type !== 'circle'
      || !imageFrameReady || width === undefined || height === undefined
      || width <= 0 || height <= 0
      || !Number.isFinite(geometry.center.x) || !Number.isFinite(geometry.center.y)
      || Math.abs(geometry.center.x) > 10_000_000 || Math.abs(geometry.center.y) > 10_000_000
      || !Number.isFinite(geometry.radius) || geometry.radius <= 0 || geometry.radius > 10_000_000) return undefined;
    const band = Math.max(1, Math.min(500, geometryNumber('radialBand', 8)));
    const innerRadius = Math.max(0, geometry.radius - band);
    const outerRadius = geometry.radius + band;
    const angles = buildCaliperCircleAngles(geometry);
    let outside = 0;
    let tooShortAfterClip = 0;
    for (const angle of angles) {
      const radians = angle * Math.PI / 180;
      const direction = { x: Math.cos(radians), y: Math.sin(radians) };
      const start = { x: geometry.center.x + direction.x * innerRadius,
        y: geometry.center.y + direction.y * innerRadius };
      const end = { x: geometry.center.x + direction.x * outerRadius,
        y: geometry.center.y + direction.y * outerRadius };
      const clipped = clipLineToImage(start, end, width, height);
      if (!clipped) { outside++; continue; }
      if (Math.hypot(clipped.end.x - clipped.start.x, clipped.end.y - clipped.start.y) < 2) tooShortAfterClip++;
    }
    if (outside === 0 && tooShortAfterClip === 0) return undefined;
    const minimum = Math.max(3, Math.min(361, Math.floor(geometryNumber('minValidCalipers', 3) + 0.5)));
    const potentiallyAvailable = angles.length - outside - tooShortAfterClip;
    const detail = [
      outside > 0 ? `${outside}/${angles.length} 条径向剖面完全位于图像外` : '',
      tooShortAfterClip > 0 ? `${tooShortAfterClip} 条径向剖面裁切后短于 2 px` : ''
    ].filter(Boolean).join('，');
    return {
      type: potentiallyAvailable < minimum ? 'warning' as const : 'info' as const,
      title: potentiallyAvailable < minimum ? '图像边界可能导致圆卡尺不足' : '部分圆卡尺位于图像边界外',
      description: `${boundsContext}${detail}；仅按图像边界估算，最多剩余 ${potentiallyAvailable} 条可采样，当前拟合至少需要 ${minimum} 条有效卡尺。ROI 掩膜、对比度阈值和边缘方向还可能继续减少有效点。请调整标称圆位置、半径或搜索带。`
    };
  })();

  const lineBoundsNotice = (() => {
    if (!['measure.caliperLine', 'measure.caliperArray'].includes(catalogType) || geometry?.type !== 'line'
      || !imageFrameReady || width === undefined || height === undefined
      || width <= 0 || height <= 0) return undefined;
    const dx = geometry.end.x - geometry.start.x;
    const dy = geometry.end.y - geometry.start.y;
    const length = Math.hypot(dx, dy);
    if (length < 2) return undefined;
    const minimumCount = catalogType === 'measure.caliperArray' ? 1 : 3;
    const count = Math.max(minimumCount, Math.min(101, Math.floor(geometryNumber('caliperCount', 9) + 0.5)));
    const requestedSpan = geometryNumber('edgeLength', 0);
    const span = requestedSpan > 0 ? Math.max(0, Math.min(10000, requestedSpan)) : length;
    const tangent = { x: -dy / length, y: dx / length };
    let outside = 0;
    let tooShortAfterClip = 0;
    for (let index = 0; index < count; index++) {
      const offset = (index / (count - 1) - 0.5) * span;
      const delta = { x: tangent.x * offset, y: tangent.y * offset };
      const clipped = clipLineToImage(
        { x: geometry.start.x + delta.x, y: geometry.start.y + delta.y },
        { x: geometry.end.x + delta.x, y: geometry.end.y + delta.y },
        width, height
      );
      if (!clipped) { outside++; continue; }
      if (Math.hypot(clipped.end.x - clipped.start.x, clipped.end.y - clipped.start.y) < 2) tooShortAfterClip++;
    }
    if (outside === 0 && tooShortAfterClip === 0) return undefined;
    const minimum = Math.max(3, Math.min(101, Math.floor(geometryNumber('minValidCalipers', 3) + 0.5)));
    const potentiallyAvailable = count - outside - tooShortAfterClip;
    const detail = [
      outside > 0 ? `${outside}/${count} 条剖面完全位于图像外` : '',
      tooShortAfterClip > 0 ? `${tooShortAfterClip} 条剖面裁切后短于 2 px` : ''
    ].filter(Boolean).join('，');
    return {
      type: potentiallyAvailable < minimum ? 'warning' as const : 'info' as const,
      title: potentiallyAvailable < minimum ? '图像边界可能导致卡尺数量不足' : '部分卡尺位于图像边界外',
      description: `${boundsContext}${detail}；仅按图像边界估算，最多剩余 ${potentiallyAvailable} 条可采样，当前拟合至少需要 ${minimum} 条有效卡尺。ROI 掩膜、对比度阈值和边缘方向还可能继续减少有效点。请缩短跨度或调整搜索线位置。`
    };
  })();

  const searchLineBoundsNotice = (() => {
    if (!['measure.caliper', 'measure.caliperWidth'].includes(catalogType) || geometry?.type !== 'line'
      || !imageFrameReady || width === undefined || height === undefined
      || width <= 0 || height <= 0) return undefined;
    const length = Math.hypot(geometry.end.x - geometry.start.x, geometry.end.y - geometry.start.y);
    if (length < 2) return undefined;
    const clipped = clipLineToImage(geometry.start, geometry.end, width, height);
    if (!clipped) return {
      type: 'warning' as const,
      title: '搜索线完全位于图像外',
      description: `${boundsContext}当前卡尺不会采到图像像素，运行会返回“未找到”而不是检测边缘。请将搜索线移入图像，或检查上游坐标变换。`
    };
    const clippedLength = Math.hypot(clipped.end.x - clipped.start.x, clipped.end.y - clipped.start.y);
    if (clippedLength < 2) return {
      type: 'warning' as const,
      title: '搜索线裁切后过短',
      description: `${boundsContext}搜索线与图像仅相交 ${clippedLength.toFixed(1)} px，短于卡尺要求的 2 px。请调整搜索线位置或方向。`
    };
    if (clippedLength >= length - 1e-6) return undefined;
    return {
      type: 'info' as const,
      title: '搜索线将按图像边界裁切',
      description: `${boundsContext}搜索线约有 ${(length - clippedLength).toFixed(1)} px 位于图像外；运行时只会在图像内的 ${clippedLength.toFixed(1)} px 段上采样。`
    };
  })();

  return { circleBoundsNotice, lineBoundsNotice, searchLineBoundsNotice };
}
