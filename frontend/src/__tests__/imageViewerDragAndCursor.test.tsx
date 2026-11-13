/**
 * 拖动解算与光标反馈：圆弧角度、搜索线拖动、选区移动缩放（自 imageViewerMeasurementGeometry 拆分，2026-11-13）
 *
 * 共享夹具见 ./helpers/imageViewerHarness，Konva 替身见 ./helpers/konvaMock。
 */
import { cleanup, render, screen, fireEvent } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ImageViewer, { type MeasurementGeometry } from '../components/ImageViewer';
import type { VisionRoi } from '../types';
import { imageView, overlays, emptyRoi, stubResizeObserver } from './helpers/imageViewerHarness';

vi.mock('react-konva', async () => await import('./helpers/konvaMock'));

beforeEach(() => { stubResizeObserver(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('ImageViewer measurement geometry editing', () => {
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

  it('drags the nearest endpoint when short search-line handles overlap', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 40, y: 40 }, end: { x: 42, y: 40 } }, onChange);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 181.12, clientY: 106.4 }); // end (42, 40), both handles are within 10 screen px
    fireEvent.mouseMove(stage, { clientX: 204.72, clientY: 106.4 }); // move end to (52, 40)
    fireEvent.mouseUp(document.body, { clientX: 204.72, clientY: 106.4 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type === 'line') {
      expect(result.start.x).toBeCloseTo(40);
      expect(result.start.y).toBeCloseTo(40);
      expect(result.end.x).toBeCloseTo(52);
      expect(result.end.y).toBeCloseTo(40);
    }
  });

  it('lets users drag a search-line endpoint outside the image to define clipped sampling', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 35.6 }); // end (50, 10)
    fireEvent.mouseMove(stage, { clientX: 58.4, clientY: 82.8 }); // end (-10, 30), outside the image but inside the viewer
    fireEvent.mouseUp(document.body, { clientX: 58.4, clientY: 82.8 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type === 'line') {
      expect(result.start).toEqual({ x: 10, y: 10 });
      expect(result.end.x).toBeCloseTo(-10, 4);
      expect(result.end.y).toBeCloseTo(30, 4);
    }
  });

  it('lets users redraw a search line past the image boundary', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    fireEvent.click(screen.getByRole('button', { name: '重绘搜索线' }));
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 130 }); // start (10, 50)
    fireEvent.mouseMove(stage, { clientX: 58.4, clientY: 130 }); // end (-10, 50)
    fireEvent.mouseUp(document.body, { clientX: 58.4, clientY: 130 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type === 'line') {
      expect(result.start.x).toBeCloseTo(10, 4);
      expect(result.start.y).toBeCloseTo(50, 4);
      expect(result.end.x).toBeCloseTo(-10, 4);
      expect(result.end.y).toBeCloseTo(50, 4);
    }
  });

  it('shows an edit cursor over the caliper search-line endpoints', () => {
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, vi.fn());
    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 200, clientY: 35.6 }); // end (50, 10)
    expect(screen.getByTestId('stage').parentElement?.style.cursor).toBe('move');
  });

  it('moves a clipped search line without snapping it fully inside the image', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: -20, y: 50 }, end: { x: 80, y: 50 } }, onChange);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 130 }); // image (50, 50), on the visible line
    fireEvent.mouseMove(stage, { clientX: 176.4, clientY: 130 }); // move left by 10 image pixels
    fireEvent.mouseUp(document.body, { clientX: 176.4, clientY: 130 });

    expect(onChange).toHaveBeenCalledOnce();
    const result = onChange.mock.calls[0][0] as MeasurementGeometry;
    expect(result.type).toBe('line');
    if (result.type === 'line') {
      expect(result.start.x).toBeCloseTo(-30);
      expect(result.end.x).toBeCloseTo(70);
      expect(result.start.y).toBe(50);
      expect(result.end.y).toBe(50);
    }
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

  it('moves an oversized rectangle ROI without reversing the drag direction', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Rectangle', x: -50, y: 20, width: 200, height: 40 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 106.4 }); // image (50, 40), inside rectangle
    fireEvent.mouseMove(stage, { clientX: 223.6, clientY: 118.2 }); // request offset (+10, +5)
    fireEvent.mouseUp(document.body, { clientX: 223.6, clientY: 118.2 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    expect(onRoiChange).toHaveBeenCalledWith({ type: 'Rectangle', x: -40, y: 25, width: 200, height: 40 });
  });

  it('keeps an oversized circle ROI center inside the image when moving it', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Circle', x: 50, y: 50, radius: 60 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 130 }); // image center
    fireEvent.mouseMove(stage, { clientX: 223.6, clientY: 141.8 }); // request image (60, 55)
    fireEvent.mouseUp(document.body, { clientX: 223.6, clientY: 141.8 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const moved = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(moved.type).toBe('Circle');
    if (moved.type === 'Circle') {
      expect(moved.x).toBeCloseTo(60);
      expect(moved.y).toBeCloseTo(55);
      expect(moved.radius).toBe(60);
    }
  });

  it('resizes a tiny rectangle from the nearest corner when handle hit areas overlap', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Rectangle', x: 40, y: 40, width: 2, height: 2 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 181.12, clientY: 111.12 }); // southeast corner (42, 42)
    fireEvent.mouseMove(stage, { clientX: 183.48, clientY: 113.48 }); // enlarge by one image pixel
    fireEvent.mouseUp(document.body, { clientX: 183.48, clientY: 113.48 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const resized = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(resized.type).toBe('Rectangle');
    if (resized.type === 'Rectangle') {
      expect(resized.x).toBe(40);
      expect(resized.y).toBe(40);
      expect(resized.width).toBeCloseTo(3);
      expect(resized.height).toBeCloseTo(3);
    }
  });

  it('resizes a rectangle from its edge midpoint without moving the whole ROI', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Rectangle', x: 30, y: 30, width: 30, height: 24 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={onRoiChange} />);
    expect(screen.getAllByTestId('roi-handle')).toHaveLength(8);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 223.6, clientY: 111.12 }); // east edge midpoint (60, 42)
    fireEvent.mouseMove(stage, { clientX: 233.04, clientY: 111.12 }); // widen by four image pixels
    fireEvent.mouseUp(document.body, { clientX: 233.04, clientY: 111.12 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const resized = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(resized.type).toBe('Rectangle');
    if (resized.type === 'Rectangle') {
      expect(resized.x).toBeCloseTo(30);
      expect(resized.y).toBeCloseTo(30);
      expect(resized.width).toBeCloseTo(34);
      expect(resized.height).toBeCloseTo(24);
    }
  });

  it('shows a resize cursor while hovering a rectangle edge handle', () => {
    const roi: VisionRoi = { type: 'Rectangle', x: 30, y: 30, width: 30, height: 24 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={emptyRoi} />);

    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 223.6, clientY: 111.12 });
    expect(screen.getByTestId('stage').parentElement?.style.cursor).toBe('ew-resize');
  });

  it('shows the circle ROI radius cursor along the visible radius handle', () => {
    const roi: VisionRoi = { type: 'Circle', x: 75, y: 25, radius: 10 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={emptyRoi} />);

    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 242.3, clientY: 87.7 }); // radius handle toward image lower-left

    expect(screen.getByTestId('stage').parentElement?.style.cursor).toBe('nesw-resize');
  });

  it('shows the caliper-circle radius cursor along the visible radius handle', () => {
    imageView({ type: 'circle', center: { x: 75, y: 25 }, radius: 10, band: 0,
      angleStartDeg: -180, angleEndDeg: 180 }, vi.fn());

    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 242.3, clientY: 87.7 }); // radius handle toward image lower-left

    expect(screen.getByTestId('stage').parentElement?.style.cursor).toBe('nesw-resize');
  });

  it('shows a move cursor over the visible interior of a polygon ROI', () => {
    const roi: VisionRoi = { type: 'Polygon', points: [
      { x: 30, y: 30 }, { x: 70, y: 30 }, { x: 70, y: 70 }, { x: 30, y: 70 }
    ] };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={emptyRoi} />);

    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 200, clientY: 130 }); // image center

    expect(screen.getByTestId('stage').parentElement?.style.cursor).toBe('move');
  });

  it('keeps the drawing cursor when a brush tool takes priority over ROI handles', () => {
    const roi: VisionRoi = { type: 'Rectangle', x: 30, y: 30, width: 30, height: 24 };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={emptyRoi} />);
    fireEvent.click(screen.getByText('笔刷'));
    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 223.6, clientY: 111.12 });

    expect(screen.getByTestId('stage').parentElement?.style.cursor).toBe('crosshair');
  });
});
