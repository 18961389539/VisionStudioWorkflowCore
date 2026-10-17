export type CaliperCircleSamplingInput = {
  angleStartDeg?: number;
  angleEndDeg?: number;
  samplingMode?: string;
  angleStepDeg?: number;
  caliperCount?: number;
};

export function circleArcSpan(start: number, end: number) {
  if (start === end) return { span: 0, fullCircle: false };
  const rawSpan = end - start;
  if (Number.isFinite(rawSpan) && Math.abs(rawSpan) < 1e-9) return { span: 0, fullCircle: false };
  let span = Number.isFinite(rawSpan)
    ? rawSpan % 360
    : ((end % 360) - (start % 360)) % 360;
  if (Math.abs(span) < 1e-9) return { span: 360, fullCircle: true };
  if (span < 0) span += 360;
  return { span, fullCircle: false };
}

/** Keep normal angles unchanged, but reduce angles whose precision would distort sample spacing. */
function stableSamplingStart(start: number, samplingStep: number) {
  const estimatedUlp = Number.EPSILON * Math.max(1, Math.abs(start));
  if (Number.isFinite(estimatedUlp) && estimatedUlp <= Math.abs(samplingStep) * 0.01) return start;
  const normalized = start % 360;
  return normalized < 0 ? normalized + 360 : normalized;
}

export function normalizeCaliperAngle(angle: number) {
  const normalized = angle % 360;
  return normalized < 0 ? normalized + 360 : normalized;
}

export function effectiveSampleWidth(value: number, fallback = 5) {
  const finiteValue = Number.isFinite(value) ? value : fallback;
  const rounded = Math.max(1, Math.min(99, Math.floor(finiteValue + 0.5)));
  return rounded % 2 === 0 ? Math.min(99, rounded + 1) : rounded;
}

/** Return an equivalent, bounded arc while preserving its clockwise span. */
export function normalizeCaliperCircleArc(start: number, end: number) {
  if (!Number.isFinite(start) || !Number.isFinite(end)) return { start, end };
  const { span, fullCircle } = circleArcSpan(start, end);
  const positiveStart = normalizeCaliperAngle(start);
  const boundedStart = positiveStart >= 180 ? positiveStart - 360 : positiveStart;
  if (span === 0 && !fullCircle) return { start: boundedStart, end: boundedStart };
  return { start: boundedStart, end: boundedStart + span };
}

/** Match backend caliper-circle sampling rules so previews and configuration checks agree with execution. */
export function buildCaliperCircleAngles(geometry: CaliperCircleSamplingInput): number[] {
  const start = geometry.angleStartDeg ?? -180;
  const end = geometry.angleEndDeg ?? 180;
  if (!Number.isFinite(start) || !Number.isFinite(end)) return [];
  const { span, fullCircle } = circleArcSpan(start, end);
  if (span === 0) return [start];

  let count: number;
  let effectiveStep: number;
  if (geometry.samplingMode === 'step') {
    const requestedStep = geometry.angleStepDeg ?? 5;
    effectiveStep = Math.min(45, Math.max(0.5, Number.isFinite(requestedStep) ? requestedStep : 5));
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
    const requestedCount = geometry.caliperCount ?? 72;
    count = Math.round(Math.min(361, Math.max(3, Number.isFinite(requestedCount) ? requestedCount : 72)));
    effectiveStep = fullCircle ? span / count : span / (count - 1);
  }

  const samplingStart = stableSamplingStart(start, effectiveStep);
  return Array.from({ length: count }, (_, index) => samplingStart + index * effectiveStep);
}
