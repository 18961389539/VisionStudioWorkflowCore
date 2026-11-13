/**
 * 卡尺控制点：投影宽度带、搜索跨距、圆弧角度与几何绘制（自 imageViewerMeasurementGeometry 拆分，2026-11-13）
 *
 * 共享夹具见 ./helpers/imageViewerHarness，Konva 替身见 ./helpers/konvaMock。
 */
import { cleanup, render, screen, fireEvent } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ImageViewer, { buildCaliperCircleAngles, type MeasurementGeometry } from '../components/ImageViewer';
import { circleArcSpan, effectiveSampleWidth, normalizeCaliperCircleArc, normalizeCaliperAngle } from '../caliperCircleSampling';
import type { VisionRoi } from '../types';
import { imageView, overlays, stubResizeObserver } from './helpers/imageViewerHarness';

vi.mock('react-konva', async () => await import('./helpers/konvaMock'));

beforeEach(() => { stubResizeObserver(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('ImageViewer measurement geometry editing', () => {
  it('shows the search-line start, end, and direction in the geometry preview', () => {
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, vi.fn());

    expect(screen.getByText('起点')).toBeTruthy();
    expect(screen.getByText('终点')).toBeTruthy();
    expect(screen.getByTestId('search-arrow').getAttribute('data-pointer-at-beginning')).toBe('true');
  });

  it('reverses the line search arrow when the first edge is searched from the end', () => {
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 }, direction: 'firstfromend' }, vi.fn());

    expect(screen.getByTestId('search-arrow').getAttribute('data-points')).toBe('50,10,10,10');
    expect(screen.getByTestId('search-arrow').getAttribute('data-pointer-at-beginning')).toBe('false');
  });

  it('shows the effective projection-width band for each line caliper', () => {
    const width = effectiveSampleWidth(4);
    expect(width).toBe(5);
    imageView({ type: 'line', start: { x: 20, y: 50 }, end: { x: 80, y: 50 },
      profileSpan: 20, profileCount: 3, profileWidth: width }, vi.fn());

    const bands = screen.getAllByTestId('measurement-profile-band');
    expect(bands).toHaveLength(3);
    const points = bands[0].getAttribute('data-points')!.split(',').map(Number);
    expect(points).toEqual([20, 42.5, 80, 42.5, 80, 37.5, 20, 37.5]);
  });

  it('shows the effective projection-width band across each radial caliper', () => {
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: 0, angleEndDeg: 90, samplingMode: 'count', caliperCount: 3, profileWidth: effectiveSampleWidth(4) }, vi.fn());

    const bands = screen.getAllByTestId('measurement-profile-band');
    expect(bands).toHaveLength(3);
    const points = bands[0].getAttribute('data-points')!.split(',').map(Number);
    expect(points).toEqual([65, 52.5, 75, 52.5, 75, 47.5, 65, 47.5]);
  });

  it('adjusts a line-caliper projection width from its canvas handle', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 20, y: 50 }, end: { x: 80, y: 50 }, profileWidth: 5 }, onChange);
    expect(screen.getByTestId('measurement-profile-width')).toBeTruthy();
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 135.9 }); // profile-width handle at image (50, 52.5)
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 147.7 }); // widen from 5 px to 15 px
    fireEvent.mouseUp(document.body, { clientX: 200, clientY: 147.7 });

    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0]).toMatchObject({ type: 'line', profileWidth: 15 });
  });

  it('keeps the line-caliper width handle on a visible clipped profile and editable', () => {
    const onChange = vi.fn();
    const geometry: MeasurementGeometry = { type: 'line', start: { x: -20, y: 10 }, end: { x: 20, y: 90 },
      profileSpan: 40, profileCount: 5, profileWidth: 5 };
    imageView(geometry, onChange);

    const handle = screen.getByTestId('measurement-profile-width');
    const handleX = Number(handle.getAttribute('data-x'));
    const handleY = Number(handle.getAttribute('data-y'));
    expect(handleX).toBeGreaterThan(0); // the un-clipped center-profile handle would be outside the image
    expect(handleX).toBeLessThan(99);
    expect(handleY).toBeGreaterThan(0);
    expect(handleY).toBeLessThan(99);

    // This fixture fits a 100x100 image into a 400x260 viewer at scale 2.36, centered at (82, 12).
    const normal = { x: -2 / Math.sqrt(5), y: 1 / Math.sqrt(5) };
    const toScreen = (point: { x: number; y: number }) => ({ x: 82 + point.x * 2.36, y: 12 + point.y * 2.36 });
    const start = toScreen({ x: handleX, y: handleY });
    const end = toScreen({ x: handleX + normal.x * 5, y: handleY + normal.y * 5 });
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: start.x, clientY: start.y });
    fireEvent.mouseMove(stage, { clientX: end.x, clientY: end.y });
    fireEvent.mouseUp(document.body, { clientX: end.x, clientY: end.y });

    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0]).toMatchObject({ type: 'line', profileWidth: 15 });
  });

  it('resizes the caliper profile span from its visible image handles', () => {
    const onChange = vi.fn();
    const geometry: MeasurementGeometry = { type: 'line', start: { x: 20, y: 50 }, end: { x: 80, y: 50 }, profileSpan: 40, profileCount: 5 };
    imageView(geometry, onChange);

    expect(screen.getByTestId('measurement-profile-span-start')).toBeTruthy();
    expect(screen.getByTestId('measurement-profile-span-end')).toBeTruthy();
    const stage = screen.getByTestId('stage');
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 177.2 });
    expect(stage.parentElement?.style.cursor).toBe('ns-resize');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 177.2 }); // lower profile-span handle at image (50, 70)
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 189 }); // expand symmetrically to a 50 px profile span
    fireEvent.mouseUp(document.body, { clientX: 200, clientY: 189 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type === 'line') {
      expect(result.profileSpan).toBeCloseTo(50, 4);
      expect(result.start).toEqual(geometry.start);
      expect(result.end).toEqual(geometry.end);
      expect(result.profileCount).toBe(5);
    }
  });

  it('keeps line-caliper span handles visible and preserves span when dragging from a clipped handle', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 20, y: 50 }, end: { x: 80, y: 50 }, profileSpan: 200, profileCount: 5 }, onChange);

    const startHandle = screen.getByTestId('measurement-profile-span-start');
    const endHandle = screen.getByTestId('measurement-profile-span-end');
    expect(Number(startHandle.getAttribute('data-x'))).toBeCloseTo(50, 5);
    expect(Number(startHandle.getAttribute('data-y'))).toBeCloseTo(0, 5);
    expect(Number(endHandle.getAttribute('data-x'))).toBeCloseTo(50, 5);
    expect(Number(endHandle.getAttribute('data-y'))).toBeCloseTo(99, 5);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 12 }); // clipped start handle at image (50, 0)
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 12 }); // a zero-distance move must not collapse the span
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 23.8 }); // move 5 px toward the center
    fireEvent.mouseUp(document.body, { clientX: 200, clientY: 23.8 });

    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0]).toMatchObject({ type: 'line', profileSpan: 190 });
  });

  it('shows radial search direction from the inner to outer end of a circle caliper', () => {
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: 0, angleEndDeg: 90 }, vi.fn());

    expect(screen.getByText('起点')).toBeTruthy();
    expect(screen.getByText('终点')).toBeTruthy();
    expect(screen.getByTestId('search-arrow').getAttribute('data-pointer-at-beginning')).toBe('true');
  });

  it('reverses the radial search arrow when the first edge is searched from the outer end', () => {
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: 0, angleEndDeg: 90, searchDirection: 'firstfromend' }, vi.fn());

    expect(screen.getByTestId('search-arrow').getAttribute('data-points')).toBe('75,50,65,50');
    expect(screen.getByText('起点')).toBeTruthy();
    expect(screen.getByText('终点')).toBeTruthy();
  });

  it('adjusts a radial-caliper projection width from its canvas handle', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: 0, angleEndDeg: 90, samplingMode: 'count', caliperCount: 3, profileWidth: 5 }, onChange);
    expect(screen.getByTestId('measurement-profile-width')).toBeTruthy();
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 229.6, clientY: 167.6 }); // most visible width handle at 45°
    fireEvent.mouseMove(stage, { clientX: 221.3, clientY: 176 }); // widen from 5 px to 15 px along its normal
    fireEvent.mouseUp(document.body, { clientX: 221.3, clientY: 176 });

    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0]).toMatchObject({ type: 'circle', profileWidth: 15 });
  });

  it('keeps the radial-caliper width handle on a visible spoke when the first caliper is outside the image', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 80 }, radius: 70, band: 20,
      angleStartDeg: 0, angleEndDeg: 270, samplingMode: 'count', caliperCount: 4, profileWidth: 5 }, onChange);
    const handle = screen.getByTestId('measurement-profile-width');
    expect(Number(handle.getAttribute('data-x'))).toBeCloseTo(52.5, 5);
    expect(Number(handle.getAttribute('data-y'))).toBeCloseTo(15, 5);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 206.9, clientY: 47.4 }); // selected visible spoke at 270°
    fireEvent.mouseMove(stage, { clientX: 218.7, clientY: 47.4 }); // drag along its normal to widen to 15 px
    fireEvent.mouseUp(document.body, { clientX: 218.7, clientY: 47.4 });

    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0]).toMatchObject({ type: 'circle', profileWidth: 15 });
  });

  it('clips the radial-caliper width handle to the last pixel center like runtime sampling', () => {
    imageView({ type: 'circle', center: { x: 80, y: 50 }, radius: 30, band: 20,
      angleStartDeg: 0, angleEndDeg: 0, samplingMode: 'count', caliperCount: 3, profileWidth: 5 }, vi.fn());

    const handle = screen.getByTestId('measurement-profile-width');
    expect(Number(handle.getAttribute('data-x'))).toBeCloseTo(94.5, 5);
    expect(Number(handle.getAttribute('data-y'))).toBeCloseTo(52.5, 5);
  });

  it('keeps distinct angle handles on a full circle and lets users drag it into an arc', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: -180, angleEndDeg: 180 }, onChange);

    expect(screen.getByTestId('caliper-circle-angle-start')).toBeTruthy();
    expect(screen.getByTestId('caliper-circle-angle-end')).toBeTruthy();
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 178.8, clientY: 130 }); // blue start handle sits inside the radial search band
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 82.8 }); // move start angle to -90 degrees
    fireEvent.mouseUp(document.body, { clientX: 200, clientY: 82.8 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') {
      expect(result.angleStartDeg).toBeCloseTo(-90, 4);
      expect(result.angleEndDeg).toBe(180);
      expect(result.center).toEqual({ x: 50, y: 50 });
      expect(result.radius).toBe(20);
      expect(result.band).toBe(5);
    }
  });

  it('clips a radial arc handle along its spoke so dragging at the image edge preserves its angle', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 80, y: 50 }, radius: 40, band: 20,
      angleStartDeg: 45, angleEndDeg: 135, samplingMode: 'count', caliperCount: 5 }, onChange);

    const handle = screen.getByTestId('caliper-circle-angle-start');
    expect(Number(handle.getAttribute('data-x'))).toBeCloseTo(99, 5);
    expect(Number(handle.getAttribute('data-y'))).toBeCloseTo(69, 5);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 315.64, clientY: 174.84 }); // clipped 45° spoke at image (99, 69)
    fireEvent.mouseMove(stage, { clientX: 315.64, clientY: 174.84 }); // zero-distance drag must not rotate the arc
    fireEvent.mouseUp(document.body, { clientX: 315.64, clientY: 174.84 });

    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0]).toMatchObject({ type: 'circle', angleStartDeg: 45, angleEndDeg: 135 });
  });

  it('omits radial arc handles when their search spokes do not intersect the image', () => {
    imageView({ type: 'circle', center: { x: 150, y: 50 }, radius: 30, band: 5,
      angleStartDeg: 0, angleEndDeg: 90, samplingMode: 'count', caliperCount: 5 }, vi.fn());

    expect(screen.queryByTestId('caliper-circle-angle-start')).toBeNull();
    expect(screen.queryByTestId('caliper-circle-angle-end')).toBeNull();
  });

  it('keeps a full-circle angle handle separate from the radial-band handle', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: -90, angleEndDeg: 270 }, onChange);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 108.6 }); // inner angle handle, clear of the cyan band handle above it
    fireEvent.mouseMove(stage, { clientX: 247.2, clientY: 130 }); // angleStart to 0°
    fireEvent.mouseUp(document.body, { clientX: 247.2, clientY: 130 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') {
      expect(result.angleStartDeg).toBeCloseTo(0, 4);
      expect(result.angleEndDeg).toBe(270);
      expect(result.band).toBe(5);
    }
  });

  it('matches backend circle-caliper angles for full circles and a single-ray arc', () => {
    const base: Extract<MeasurementGeometry, { type: 'circle' }> = {
      type: 'circle', center: { x: 50, y: 50 }, radius: 20, angleStartDeg: -180, angleEndDeg: 180,
      samplingMode: 'step', angleStepDeg: 5
    };

    const fiveDegreeAngles = buildCaliperCircleAngles(base);
    expect(fiveDegreeAngles).toHaveLength(72);
    expect(fiveDegreeAngles[0]).toBe(-180);
    expect(fiveDegreeAngles.at(-1)).toBe(175);

    const denseAngles = buildCaliperCircleAngles({ ...base, angleStepDeg: 0.5 });
    expect(denseAngles).toHaveLength(360);
    expect(denseAngles.at(-1)).toBe(179);

    expect(buildCaliperCircleAngles({ ...base, angleStartDeg: 0, angleEndDeg: 0 })).toEqual([0]);
  });

  it.each(['count', 'step'])('keeps %s-mode preview rays stable for very large finite angles', samplingMode => {
    const start = 1e18;
    const geometry: Extract<MeasurementGeometry, { type: 'circle' }> = {
      type: 'circle', center: { x: 50, y: 50 }, radius: 20,
      angleStartDeg: start, angleEndDeg: start + 256, samplingMode,
      caliperCount: 72, angleStepDeg: 256 / 71
    };
    const angles = buildCaliperCircleAngles(geometry);
    expect(angles).toHaveLength(72);
    expect(new Set(angles.map(angle => Math.round(normalizeCaliperAngle(angle) * 10_000))).size).toBe(72);

    const overflowing = buildCaliperCircleAngles({ ...geometry, samplingMode: 'count',
      angleStartDeg: -1e308, angleEndDeg: 1e308, caliperCount: 72 });
    expect(overflowing).toHaveLength(72);
    expect(overflowing.every(Number.isFinite)).toBe(true);
    const normalizedArc = normalizeCaliperCircleArc(-1e308, 1e308);
    const originalSpan = circleArcSpan(-1e308, 1e308).span;
    expect(normalizedArc.start).toBeGreaterThanOrEqual(-180);
    expect(normalizedArc.start).toBeLessThan(180);
    expect(normalizedArc.end - normalizedArc.start).toBeCloseTo(originalSpan, 7);
  });

  it('includes both partial-arc endpoints in count mode and stops at the last fixed step in legacy mode', () => {
    const base: Extract<MeasurementGeometry, { type: 'circle' }> = {
      type: 'circle', center: { x: 50, y: 50 }, radius: 20, angleStartDeg: 0, angleEndDeg: 92,
      samplingMode: 'count', caliperCount: 10
    };
    const counted = buildCaliperCircleAngles(base);
    expect(counted).toHaveLength(10);
    expect(counted[0]).toBe(0);
    expect(counted.at(-1)).toBe(92);

    expect(buildCaliperCircleAngles({ ...base, caliperCount: 4.5 })).toHaveLength(5);

    const stepped = buildCaliperCircleAngles({ ...base, samplingMode: 'step', angleStepDeg: 5 });
    expect(stepped).toHaveLength(19);
    expect(stepped.at(-1)).toBe(90);
  });

  it('draws a new search line in image coordinates and emits its endpoints', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    fireEvent.click(screen.getByRole('button', { name: '重绘搜索线' }));

    // The fitted image view is 2.36x with a screen offset of (82, 12).
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 129.2, clientY: 82.8 });
    fireEvent.mouseMove(stage, { clientX: 270.8, clientY: 153.6 });
    fireEvent.mouseUp(stage, { clientX: 270.8, clientY: 153.6 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type !== 'line') throw new Error('Expected a line geometry.');
    expect(result.start.x).toBeCloseTo(20, 4);
    expect(result.start.y).toBeCloseTo(30, 4);
    expect(result.end.x).toBeCloseTo(80, 4);
    expect(result.end.y).toBeCloseTo(60, 4);
  });

  it('draws a nominal circle from center to radius and retains the radial search band', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 6, angleStartDeg: 0, angleEndDeg: 90 }, onChange);
    fireEvent.click(screen.getByRole('button', { name: '重绘标称圆' }));

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 130 });
    fireEvent.mouseMove(stage, { clientX: 247.2, clientY: 130 });
    fireEvent.mouseUp(stage, { clientX: 247.2, clientY: 130 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected a circle geometry.');
    expect(result.center.x).toBeCloseTo(50, 4);
    expect(result.center.y).toBeCloseTo(50, 4);
    expect(result.radius).toBeCloseTo(20, 4);
    expect(result.band).toBe(6);
    expect(result.angleStartDeg).toBe(0);
    expect(result.angleEndDeg).toBe(90);
  });

  it('draws a clipped caliper circle by dragging beyond the nearest image edge', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 20, y: 50 }, radius: 10, band: 6,
      angleStartDeg: 0, angleEndDeg: 90 }, onChange);
    fireEvent.click(screen.getByRole('button', { name: '重绘标称圆' }));

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 129.2, clientY: 130 }); // center (20, 50)
    fireEvent.mouseMove(stage, { clientX: 249.3, clientY: 205.1 }); // radius 60; most of the circle lies outside the frame
    fireEvent.mouseUp(document.body, { clientX: 249.3, clientY: 205.1 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') expect(result.radius).toBeCloseTo(60, 0);
  });

  it('lets users enlarge a caliper circle beyond the inscribed image radius', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 20, y: 50 }, radius: 10, band: 6,
      angleStartDeg: 0, angleEndDeg: 90 }, onChange);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 149.6, clientY: 142.5 }); // radius handle along the far-corner direction
    fireEvent.mouseMove(stage, { clientX: 249.3, clientY: 205.1 }); // expand to radius 60, across the nearest image edge
    fireEvent.mouseUp(document.body, { clientX: 249.3, clientY: 205.1 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') expect(result.radius).toBeCloseTo(60, 0);
  });

  it('lets users resize a caliper circle to the node minimum of one pixel', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 10, band: 6,
      angleStartDeg: 0, angleEndDeg: 90 }, onChange);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 216.7, clientY: 147.7 }); // radius handle at ~57.07, 57.07
    fireEvent.mouseMove(stage, { clientX: 201.7, clientY: 131.7 }); // one image pixel from the center
    fireEvent.mouseUp(document.body, { clientX: 201.7, clientY: 131.7 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') expect(result.radius).toBeCloseTo(1, 0);
  });

  it('lets users draw and resize a clipped circular ROI for image crop', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Circle', x: 20, y: 50, radius: 10 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 149.6, clientY: 142.5 }); // visible radius handle
    fireEvent.mouseMove(stage, { clientX: 249.3, clientY: 205.1 }); // expand to 60 px, clipping the image edge
    fireEvent.mouseUp(document.body, { clientX: 249.3, clientY: 205.1 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const result = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(result.type).toBe('Circle');
    if (result.type === 'Circle') expect(result.radius).toBeCloseTo(60, 0);
  });

  it('lets users draw a one-pixel circular ROI', () => {
    const onRoiChange = vi.fn();
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roiEnabled onRoiChange={onRoiChange} />);
    fireEvent.click(screen.getByText('圆形'));

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 130 }); // center (50, 50)
    fireEvent.mouseMove(stage, { clientX: 202.36, clientY: 130 }); // radius 1 px
    fireEvent.mouseUp(document.body, { clientX: 202.36, clientY: 130 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const result = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(result.type).toBe('Circle');
    if (result.type === 'Circle') expect(result.radius).toBeCloseTo(1, 0);
  });

  it('keeps an oversized caliper circle center inside the image when moving it', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 60, angleStartDeg: 0, angleEndDeg: 90 }, onChange);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 130 }); // center handle
    fireEvent.mouseMove(stage, { clientX: 223.6, clientY: 141.8 }); // request center (60, 55)
    fireEvent.mouseUp(document.body, { clientX: 223.6, clientY: 141.8 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') {
      expect(result.center.x).toBeCloseTo(60);
      expect(result.center.y).toBeCloseTo(55);
      expect(result.radius).toBe(60);
    }
  });

  it('lets users move a caliper circle partly beyond the image edge by dragging its circumference', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 8, angleStartDeg: -180, angleEndDeg: 180 }, onChange);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 247.2, clientY: 130 }); // east circumference (70, 50)
    fireEvent.mouseMove(stage, { clientX: 82, clientY: 130 }); // drag the center to x=-20; the radial band still reaches the image
    fireEvent.mouseUp(document.body, { clientX: 82, clientY: 130 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') {
      expect(result.center.x).toBeCloseTo(-20);
      expect(result.center.y).toBeCloseTo(50);
      expect(result.radius).toBe(20);
    }
  });

  it('lets users move a caliper circle back into view by dragging its visible circumference', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: -10, y: 50 }, radius: 30, angleStartDeg: 30, angleEndDeg: 120 }, onChange);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 93.8, clientY: 68.64 }); // visible circle edge near image (5, 24)
    fireEvent.mouseMove(stage, { clientX: 188.2, clientY: 68.64 }); // drag the circle right
    fireEvent.mouseUp(document.body, { clientX: 188.2, clientY: 68.64 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type === 'circle') {
      expect(result.center.x).toBeCloseTo(30);
      expect(result.center.y).toBeCloseTo(50);
      expect(result.radius).toBe(30);
    }
  });

  it('drags a partial-circle endpoint across zero without reversing the sweep', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 6,
      angleStartDeg: 350, angleEndDeg: 10, samplingMode: 'count', caliperCount: 12 }, onChange);
    const stage = screen.getByTestId('stage');
    // The purple end handle sits outside the nominal radius; drag it from 10° to 90°.
    fireEvent.mouseDown(stage, { clientX: 260.2, clientY: 140.6 });
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 200 });
    fireEvent.mouseUp(stage, { clientX: 200, clientY: 200 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected circle geometry.');
    expect(result.angleStartDeg).toBe(350);
    expect(result.angleEndDeg).toBeCloseTo(450, 0);
    expect(buildCaliperCircleAngles(result).length).toBe(12);
  });
});
