/**
 * NodeConfigurationDialog 的测量几何构建与适配（纯函数）。
 *
 * 从 `useNodeConfigurationDialogModel` 拆出（2026-11-13）。三个职责：
 *  1. 从草稿参数 + 已连接的上游几何拼出画布要编辑的 `MeasurementGeometry`；
 *  2. 把画布编辑结果写回参数（`measurementGeometryParameters`）——与 1 互为逆过程，
 *     放在一起才能一眼看出「读写字段是否对称」；
 *  3. 「适应窗口」的几何计算（`fitGeometryToBounds`）。
 *
 * 不依赖 React。回写与撤销的 setState 编排仍留在 hook 里。
 */
import { effectiveSampleWidth, normalizeCaliperCircleArc } from '../caliperCircleSampling';
import type { NodeParameters } from '../nodeConfigurationState';
import type { MeasurementGeometry } from './ImageViewer';
import type { Point } from './imageViewerTypes';

/**
 * 参数里读数值：非有限值一律回落，避免 NaN 传进画布后整条几何消失。
 * 导出是为了让 hook 里的其他消费方（采样提示、边界提示）共用同一套回落语义。
 */
export function parameterNumber(draft: NodeParameters, key: string, fallback: number): number {
  return Number.isFinite(Number(draft[key])) ? Number(draft[key]) : fallback;
}

/** 带跨度/条数的卡尺（线阵列、圆弧阵列）：只有这两类需要 `profileSpan` 与 `profileCount`。 */
const SPAN_CALIPER_TYPES = new Set(['measure.caliperLine', 'measure.caliperArray']);

export function buildMeasurementGeometry({
  draft,
  catalogType,
  measurementType,
  editable,
  displayableConnectedSearchLine,
  imageFrameReady,
  connectedSearchLine,
  connectedCircle,
  circleInputConnected
}: {
  draft: NodeParameters;
  /** 节点类型（决定是否带跨度参数）。 */
  catalogType: string;
  /** 该节点编辑线型还是圆型几何；`undefined` 表示不参与测量编辑。 */
  measurementType: 'line' | 'circle' | undefined;
  /** 编辑权限：可编辑几何，或至少能叠加显示已连接的搜索线。 */
  editable: boolean;
  displayableConnectedSearchLine: boolean;
  /** 图像帧已就绪：帧没对上时不能把几何叠到一张错误的图上。 */
  imageFrameReady: boolean;
  connectedSearchLine?: { start: Point; end: Point };
  connectedCircle?: { x: number; y: number; radius: number };
  /** 圆来自上游端口时，圆心/半径不可编辑（改的是上游节点）。 */
  circleInputConnected: boolean;
}): MeasurementGeometry | undefined {
  if (!(editable || displayableConnectedSearchLine) || !imageFrameReady) return undefined;
  if (measurementType === 'line') {
    return {
      type: 'line',
      start: connectedSearchLine?.start ?? { x: parameterNumber(draft, 'startX', 120), y: parameterNumber(draft, 'startY', 240) },
      end: connectedSearchLine?.end ?? { x: parameterNumber(draft, 'endX', 520), y: parameterNumber(draft, 'endY', 240) },
      profileWidth: effectiveSampleWidth(parameterNumber(draft, 'sampleWidth', 5)),
      direction: String(draft.direction ?? 'strongest'),
      ...(SPAN_CALIPER_TYPES.has(catalogType) ? {
        profileSpan: Math.max(0, parameterNumber(draft, 'edgeLength', 0)),
        profileCount: parameterNumber(draft, 'caliperCount', 9)
      } : {})
    };
  }
  if (measurementType !== 'circle') return undefined;
  return {
    type: 'circle',
    center: connectedCircle ? { x: connectedCircle.x, y: connectedCircle.y }
      : { x: parameterNumber(draft, 'centerX', 320), y: parameterNumber(draft, 'centerY', 240) },
    radius: connectedCircle?.radius ?? Math.max(0, parameterNumber(draft, 'radius', 100)),
    band: Math.max(0, parameterNumber(draft, 'radialBand', 8)),
    angleStartDeg: parameterNumber(draft, 'angleStartDeg', -180),
    angleEndDeg: parameterNumber(draft, 'angleEndDeg', 180),
    caliperCount: parameterNumber(draft, 'caliperCount', 72),
    samplingMode: String(draft.samplingMode ?? 'count'),
    angleStepDeg: parameterNumber(draft, 'angleStepDeg', 5),
    profileWidth: effectiveSampleWidth(parameterNumber(draft, 'sampleWidth', 5)),
    searchDirection: String(draft.searchDirection ?? 'strongest'),
    centerEditable: !circleInputConnected,
    radiusEditable: !circleInputConnected
  };
}

/**
 * 画布几何 → 参数。与 `buildMeasurementGeometry` 是逆过程：
 * 那边读哪些键，这边就写哪些键。改一处务必同步另一处。
 *
 * `preserveValues` 用于撤销：撤销时要恢复用户填的原始角度/带宽，不能在这里再归一化一遍，
 * 否则「撤销」本身又改了一次值。
 */
export function measurementGeometryParameters(
  current: NodeParameters,
  geometry: MeasurementGeometry,
  options: { catalogType: string; circleInputConnected: boolean; preserveValues?: boolean }
): NodeParameters {
  if (geometry.type === 'line') {
    return {
      ...current,
      startX: geometry.start.x, startY: geometry.start.y,
      endX: geometry.end.x, endY: geometry.end.y,
      ...(geometry.profileWidth !== undefined ? { sampleWidth: geometry.profileWidth } : {}),
      ...(SPAN_CALIPER_TYPES.has(options.catalogType) && geometry.profileSpan !== undefined
        ? { edgeLength: geometry.profileSpan } : {})
    };
  }
  const startAngle = geometry.angleStartDeg ?? -180;
  const endAngle = geometry.angleEndDeg ?? 180;
  const normalizedArc = normalizeCaliperCircleArc(startAngle, endAngle);
  return {
    ...current,
    ...(options.circleInputConnected ? {} : { centerX: geometry.center.x, centerY: geometry.center.y, radius: geometry.radius }),
    ...(geometry.profileWidth !== undefined ? { sampleWidth: geometry.profileWidth } : {}),
    angleStartDeg: options.preserveValues ? startAngle : normalizedArc.start,
    angleEndDeg: options.preserveValues ? endAngle : normalizedArc.end,
    radialBand: options.preserveValues ? geometry.band ?? 0 : Math.max(1, Math.min(500, geometry.band ?? 1))
  };
}

/**
 * 「适应窗口」：把几何居中并缩放到画面的约 62.5%。
 *
 * 保留原方向而不是强制水平/垂直——用户可能是按某条边缘画的，强制摆正会丢掉意图。
 * 圆型按短边的 1/4.8 取半径，并给搜索带留出余量，避免带外圈被裁掉。
 */
export function fitGeometryToBounds(geometry: MeasurementGeometry, width: number, height: number): MeasurementGeometry {
  if (geometry.type === 'line') {
    const dx = geometry.end.x - geometry.start.x;
    const dy = geometry.end.y - geometry.start.y;
    const currentLength = Math.hypot(dx, dy);
    const direction = currentLength > 1e-6 ? { x: dx / currentLength, y: dy / currentLength } : { x: 1, y: 0 };
    const availableLength = Math.min(width / Math.max(Math.abs(direction.x), 1e-6),
      height / Math.max(Math.abs(direction.y), 1e-6));
    const length = Math.max(2, availableLength * 0.625);
    const center = { x: width / 2, y: height / 2 };
    const tangent = { x: -direction.y, y: direction.x };
    const availableSpan = Math.min(width / Math.max(Math.abs(tangent.x), 1e-6),
      height / Math.max(Math.abs(tangent.y), 1e-6)) * 0.625;
    const profileSpan = geometry.profileSpan !== undefined && geometry.profileSpan > 0
      ? Math.min(geometry.profileSpan, availableSpan) : geometry.profileSpan;
    return {
      ...geometry,
      start: { x: center.x - direction.x * length / 2, y: center.y - direction.y * length / 2 },
      end: { x: center.x + direction.x * length / 2, y: center.y + direction.y * length / 2 },
      ...(profileSpan === undefined ? {} : { profileSpan })
    };
  }
  const minDimension = Math.min(width, height);
  const band = Math.max(1, Math.min(geometry.band ?? 8, minDimension * 0.15));
  const maxRadius = Math.max(1, minDimension / 2 - band - 1);
  const radius = Math.max(1, Math.min(minDimension / 4.8, maxRadius));
  return { ...geometry, center: { x: width / 2, y: height / 2 }, radius, band };
}
