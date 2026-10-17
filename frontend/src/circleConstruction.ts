import { normalizeCaliperAngle, normalizeCaliperCircleArc } from './caliperCircleSampling';

type Point = { x: number; y: number };

/** Construct a nominal circle from edge picks, including circles centered outside the image. */
export function circleThroughThreePoints(a: Point, b: Point, c: Point) {
  if ([a, b, c].some(point => !Number.isFinite(point.x) || !Number.isFinite(point.y))) return undefined;
  const scale = Math.max(Math.hypot(b.x - a.x, b.y - a.y), Math.hypot(c.x - a.x, c.y - a.y));
  if (scale < 1e-6) return undefined;
  // Translate and scale before solving so large image coordinates do not cancel out.
  const ux = (b.x - a.x) / scale, uy = (b.y - a.y) / scale;
  const vx = (c.x - a.x) / scale, vy = (c.y - a.y) / scale;
  const cross = ux * vy - uy * vx;
  const u2 = ux * ux + uy * uy, v2 = vx * vx + vy * vy;
  if (Math.abs(cross) <= 1e-3 * Math.sqrt(u2 * v2)) return undefined;
  const dx = (u2 * vy - v2 * uy) / (2 * cross) * scale;
  const dy = (ux * v2 - vx * u2) / (2 * cross) * scale;
  const center = { x: a.x + dx, y: a.y + dy };
  const radius = Math.hypot(dx, dy);
  if (!Number.isFinite(radius) || radius < 1 || radius > 10_000_000
    || Math.abs(center.x) > 10_000_000 || Math.abs(center.y) > 10_000_000) return undefined;
  return { center, radius };
}

/** Select the arc between the end picks that passes through the middle pick. */
export function circleArcThroughThreePoints(a: Point, through: Point, b: Point) {
  const circle = circleThroughThreePoints(a, through, b);
  if (!circle) return undefined;
  const angle = (point: Point) => Math.atan2(point.y - circle.center.y, point.x - circle.center.x) * 180 / Math.PI;
  const first = angle(a), middle = angle(through), last = angle(b);
  const clockwiseSpan = normalizeCaliperAngle(last - first);
  // The engine samples clockwise. Reverse the endpoints when the selected arc runs the other way.
  const arc = normalizeCaliperAngle(middle - first) < clockwiseSpan
    ? normalizeCaliperCircleArc(first, first + clockwiseSpan)
    : normalizeCaliperCircleArc(last, last + 360 - clockwiseSpan);
  return { ...circle, angleStartDeg: arc.start, angleEndDeg: arc.end };
}
