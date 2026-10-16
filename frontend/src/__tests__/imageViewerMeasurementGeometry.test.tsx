import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ImageViewer, { type MeasurementGeometry } from '../components/ImageViewer';
import type { VisionOverlay } from '../types';

vi.mock('react-konva', async () => {
  const React = await import('react');
  return {
    Stage: React.forwardRef((props: Record<string, any>, ref: React.ForwardedRef<any>) => {
      const pointer = React.useRef<{ x: number; y: number } | undefined>(undefined);
      React.useImperativeHandle(ref, () => ({ getPointerPosition: () => pointer.current }));
      const dispatch = (handler?: (event: unknown) => void) => (event: React.MouseEvent<HTMLDivElement>) => {
        pointer.current = { x: event.clientX, y: event.clientY };
        handler?.(event);
      };
      return <div data-testid="stage" onMouseDown={dispatch(props.onMouseDown)} onMouseMove={dispatch(props.onMouseMove)}
        onMouseUp={dispatch(props.onMouseUp)} onClick={dispatch(props.onClick)}>{props.children}</div>;
    }),
    Layer: ({ children }: React.PropsWithChildren) => <>{children}</>,
    Group: ({ children }: React.PropsWithChildren) => <>{children}</>,
    Image: () => null,
    Line: () => null,
    Circle: () => null,
    Rect: () => null,
    Text: () => null
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

  it('lets the operator drag one search-line endpoint without moving the other', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 });
    fireEvent.mouseMove(stage, { clientX: 129.2, clientY: 82.8 });
    fireEvent.mouseUp(stage, { clientX: 129.2, clientY: 82.8 });

    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type !== 'line') throw new Error('Expected a line geometry.');
    expect(result.start.x).toBeCloseTo(20, 4);
    expect(result.start.y).toBeCloseTo(30, 4);
    expect(result.end).toEqual({ x: 50, y: 10 });
  });
});
