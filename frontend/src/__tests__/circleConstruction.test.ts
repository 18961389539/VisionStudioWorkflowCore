import { describe, expect, it } from 'vitest';
import { circleArcThroughThreePoints, circleThroughThreePoints } from '../circleConstruction';
import { circleArcSpan, normalizeCaliperAngle } from '../caliperCircleSampling';

describe('nominal circle construction', () => {
  it.each([
    [350, 0, 10, 20], [10, 0, 350, 20],
    [10, 180, 350, 340], [350, 180, 10, 340],
    [-170, 180, 170, 20], [0, -90, 180, 180]
  ])('selects the arc through the middle pick, including wraparound and reversed endpoints', (first, middle, last, expectedSpan) => {
    const point = (angle: number) => ({ x: 50 + 30 * Math.cos(angle * Math.PI / 180), y: 50 + 30 * Math.sin(angle * Math.PI / 180) });
    const arc = circleArcThroughThreePoints(point(first), point(middle), point(last))!;
    expect(arc.center.x).toBeCloseTo(50, 8);
    expect(arc.center.y).toBeCloseTo(50, 8);
    expect(arc.radius).toBeCloseTo(30, 8);
    expect(circleArcSpan(arc.angleStartDeg, arc.angleEndDeg).span).toBeCloseTo(expectedSpan, 8);
    expect(normalizeCaliperAngle(middle - arc.angleStartDeg)).toBeLessThan(expectedSpan);
  });

  it('does not create a search arc from degenerate edge picks', () => {
    expect(circleArcThroughThreePoints({ x: 10, y: 10 }, { x: 20, y: 10 }, { x: 30, y: 10 })).toBeUndefined();
  });

  it('keeps subpixel coordinates when the center lies outside the image', () => {
    const center = { x: -12.25, y: 50.5 }, radius = 40.75;
    const points = [-60, 0, 60].map(degrees => ({ x: center.x + radius * Math.cos(degrees * Math.PI / 180),
      y: center.y + radius * Math.sin(degrees * Math.PI / 180) }));
    const circle = circleThroughThreePoints(points[0], points[1], points[2])!;
    expect(circle.center.x).toBeCloseTo(center.x, 8);
    expect(circle.center.y).toBeCloseTo(center.y, 8);
    expect(circle.radius).toBeCloseTo(radius, 8);
  });

  it('solves in local coordinates near the maximum coordinate limit', () => {
    const circle = circleThroughThreePoints({ x: 9_999_970, y: 9_999_950 },
      { x: 9_999_950, y: 9_999_970 }, { x: 9_999_930, y: 9_999_950 });
    expect(circle).toEqual({ center: { x: 9_999_950, y: 9_999_950 }, radius: 20 });
  });

  it.each([
    [{ x: 10, y: 10 }, { x: 10, y: 10 }, { x: 20, y: 30 }],
    [{ x: 10, y: 10 }, { x: 20, y: 10 }, { x: 30, y: 10 }],
    [{ x: 10, y: 10 }, { x: 20, y: 10 }, { x: 30, y: 10.00001 }],
    [{ x: NaN, y: 10 }, { x: 20, y: 20 }, { x: 30, y: 10 }],
    [{ x: 0, y: 0 }, { x: 0.1, y: 0 }, { x: 0, y: 0.1 }],
    [{ x: 1e9, y: 0 }, { x: 0, y: 1e9 }, { x: -1e9, y: 0 }]
  ])('rejects repeated, collinear, nonfinite or out-of-range geometry', (a, b, c) => {
    expect(circleThroughThreePoints(a, b, c)).toBeUndefined();
  });
});
