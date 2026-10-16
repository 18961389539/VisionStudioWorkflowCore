import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ImageViewer, { buildCaliperCircleAngles, type MeasurementGeometry } from '../components/ImageViewer';
import type { VisionOverlay } from '../types';

vi.mock('react-konva', async () => {
  const React = await import('react');
  return {
    Stage: React.forwardRef((props: Record<string, any>, ref: React.ForwardedRef<any>) => {
      const pointer = React.useRef<{ x: number; y: number } | undefined>(undefined);
      React.useImperativeHandle(ref, () => ({ getPointerPosition: () => pointer.current }));
      const dispatch = (handler?: (event: unknown) => void) => (event: any) => {
        const touch = event.touches?.[0] ?? event.changedTouches?.[0];
        pointer.current = { x: touch?.clientX ?? event.clientX, y: touch?.clientY ?? event.clientY };
        handler?.(event);
      };
      return <div data-testid="stage" onMouseDown={dispatch(props.onMouseDown)} onMouseMove={dispatch(props.onMouseMove)}
        onMouseUp={dispatch(props.onMouseUp)} onTouchStart={dispatch(props.onTouchStart)} onTouchMove={dispatch(props.onTouchMove)}
        onTouchEnd={dispatch(props.onTouchEnd)} onClick={dispatch(props.onClick)}>{props.children}</div>;
    }),
    Layer: ({ children }: React.PropsWithChildren) => <>{children}</>,
    Group: ({ children }: React.PropsWithChildren) => <>{children}</>,
    Arrow: () => <span data-testid="search-arrow" />,
    Image: () => null,
    Line: () => null,
    Circle: () => null,
    Rect: () => null,
    Text: ({ text }: { text?: string }) => <span data-testid="konva-text">{text}</span>
  };
});

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class {
    constructor(private readonly callback: ResizeObserverCallback) {}
    observe() { this.callback([{ contentRect: { width: 400, height: 260 } } as ResizeObserverEntry], this as unknown as ResizeObserver); }
    unobserve() {}
    disconnect() {}
  });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const overlays: VisionOverlay[] = [];
const emptyRoi = () => {};
const imageView = (geometry: MeasurementGeometry, onChange: (geometry: MeasurementGeometry) => void) => render(
  <ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100} overlays={overlays}
    roiEnabled={false} onRoiChange={emptyRoi} measurementGeometry={geometry} measurementGeometryEditable onMeasurementGeometryChange={onChange} />
);

describe('ImageViewer measurement geometry editing', () => {
  it('shows the search-line start, end, and direction in the geometry preview', () => {
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, vi.fn());

    expect(screen.getByText('起点')).toBeTruthy();
    expect(screen.getByText('终点')).toBeTruthy();
    expect(screen.getByTestId('search-arrow')).toBeTruthy();
  });

  it('shows radial search direction from the inner to outer end of a circle caliper', () => {
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: 0, angleEndDeg: 90 }, vi.fn());

    expect(screen.getByText('起点')).toBeTruthy();
    expect(screen.getByText('终点')).toBeTruthy();
    expect(screen.getByTestId('search-arrow')).toBeTruthy();
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

  it('drags the start angle while keeping the circle arc endpoint fixed', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 6,
      angleStartDeg: 0, angleEndDeg: 90, samplingMode: 'count', caliperCount: 10 }, onChange);
    const stage = screen.getByTestId('stage');
    // The blue start handle sits inside the nominal radius; drag it from 0° to 30°.
    fireEvent.mouseDown(stage, { clientX: 233.2, clientY: 130 });
    fireEvent.mouseMove(stage, { clientX: 233.0, clientY: 148.9 });
    fireEvent.mouseUp(stage, { clientX: 233.0, clientY: 148.9 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected circle geometry.');
    expect(result.angleStartDeg).toBeCloseTo(30, 0);
    expect(result.angleEndDeg).toBe(90);
  });

  it('allows editing an upstream circle arc while keeping its center and radius locked', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 6,
      angleStartDeg: 350, angleEndDeg: 10, centerEditable: false, radiusEditable: false }, onChange);
    expect(screen.queryByRole('button', { name: '重绘标称圆' })).toBeNull();
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 260.2, clientY: 140.6 });
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 191.36 });
    fireEvent.mouseUp(stage, { clientX: 200, clientY: 191.36 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected circle geometry.');
    expect(result.center).toEqual({ x: 50, y: 50 });
    expect(result.radius).toBe(20);
    expect(result.angleStartDeg).toBe(350);
    expect(result.angleEndDeg).toBeCloseTo(450, 0);
  });

  it('keeps upstream center and radius handles locked while allowing search-band edits', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 6,
      angleStartDeg: 30, angleEndDeg: 120, centerEditable: false, radiusEditable: false }, onChange);
    const stage = screen.getByTestId('stage');
    // The nominal-radius handle is visible only for locally editable circles.
    fireEvent.mouseDown(stage, { clientX: 247.2, clientY: 130 });
    fireEvent.mouseUp(stage, { clientX: 247.2, clientY: 130 });
    expect(onChange).not.toHaveBeenCalled();

    // The cyan inner-band handle remains draggable with the source circle locked.
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 97 });
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 106.4 });
    fireEvent.mouseUp(stage, { clientX: 200, clientY: 106.4 });
    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected circle geometry.');
    expect(result.center).toEqual({ x: 50, y: 50 });
    expect(result.radius).toBe(20);
    expect(result.band).toBeCloseTo(10, 0);
  });

  it('allows arc edits on a valid one-pixel-radius circle', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 1,
      angleStartDeg: 0, angleEndDeg: 90, samplingMode: 'count', caliperCount: 10 }, onChange);
    const stage = screen.getByTestId('stage');
    // Small-circle arc handles sit outside the nominal radius to stay separate from center/radius handles.
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 160.4 });
    fireEvent.mouseMove(stage, { clientX: 152.8, clientY: 130 });
    fireEvent.mouseUp(stage, { clientX: 152.8, clientY: 130 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected circle geometry.');
    expect(result.angleEndDeg).toBe(180);
    expect(result.radius).toBe(1);
  });

  it('widens the radial search band when its inner handle is dragged inward', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 6,
      angleStartDeg: 0, angleEndDeg: 60 }, onChange);
    const stage = screen.getByTestId('stage');
    // The cyan handle marks the inner radius (14 px); dragging it to 10 px widens the band to 10 px.
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 97 });
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 106.4 });
    fireEvent.mouseUp(stage, { clientX: 200, clientY: 106.4 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected circle geometry.');
    expect(result.band).toBeCloseTo(10, 0);
  });

  it('sets a nonzero radial band by dragging its outer handle', () => {
    const onChange = vi.fn();
    imageView({ type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 0,
      angleStartDeg: 0, angleEndDeg: 60 }, onChange);
    const stage = screen.getByTestId('stage');
    // With a zero-width band, the green handle stays separate from the nominal-radius handle.
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 177.2 });
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 186.6 });
    fireEvent.mouseUp(stage, { clientX: 200, clientY: 186.6 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('circle');
    if (result.type !== 'circle') throw new Error('Expected circle geometry.');
    expect(result.band).toBeCloseTo(4, 0);
  });

  it('commits a search-line endpoint drag released outside the viewer', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 });
    fireEvent.mouseMove(stage, { clientX: 129.2, clientY: 82.8 });
    fireEvent.mouseUp(document.body, { clientX: 129.2, clientY: 82.8 });

    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type !== 'line') throw new Error('Expected a line geometry.');
    expect(result.start.x).toBeCloseTo(20, 4);
    expect(result.start.y).toBeCloseTo(30, 4);
    expect(result.end).toEqual({ x: 50, y: 10 });
  });

  it('commits a touch-drawn search line when touch ends outside the viewer', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    fireEvent.click(screen.getByRole('button', { name: '重绘搜索线' }));
    const stage = screen.getByTestId('stage');
    fireEvent.touchStart(stage, { touches: [{ clientX: 105.6, clientY: 35.6 }] });
    fireEvent.touchMove(stage, { touches: [{ clientX: 129.2, clientY: 82.8 }] });
    fireEvent.touchEnd(document.body, { changedTouches: [{ clientX: 129.2, clientY: 82.8 }] });

    expect(onChange).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: '重绘搜索线' })).toBeTruthy();
  });

  it('discards an interrupted touch redraw instead of committing partial geometry', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    fireEvent.click(screen.getByRole('button', { name: '重绘搜索线' }));
    const stage = screen.getByTestId('stage');
    fireEvent.touchStart(stage, { touches: [{ clientX: 105.6, clientY: 35.6 }] });
    fireEvent.touchMove(stage, { touches: [{ clientX: 129.2, clientY: 82.8 }] });
    fireEvent.touchCancel(document.body, { changedTouches: [{ clientX: 129.2, clientY: 82.8 }] });

    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: '重绘搜索线' })).toBeTruthy();
  });

  it('moves a polygon ROI rigidly at image boundaries and commits when released outside the viewer', () => {
    const onRoiChange = vi.fn();
    const roi = { type: 'Polygon' as const, points: [
      { x: 10, y: 10 }, { x: 30, y: 10 }, { x: 30, y: 30 }, { x: 10, y: 30 }
    ] };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    // The image view uses a 2.36x scale with a screen offset of (82, 12).
    fireEvent.mouseDown(stage, { clientX: 129.2, clientY: 59.2 }); // image (20, 20), inside polygon
    fireEvent.mouseMove(stage, { clientX: 93.8, clientY: 59.2 }); // request image x=5; polygon should stop at x=0
    fireEvent.mouseUp(document.body, { clientX: 93.8, clientY: 59.2 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    expect(onRoiChange).toHaveBeenCalledWith({ type: 'Polygon', points: [
      { x: 0, y: 10 }, { x: 20, y: 10 }, { x: 20, y: 30 }, { x: 0, y: 30 }
    ] });
  });
});
