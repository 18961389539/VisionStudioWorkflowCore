/**
 * 标称圆圆选构建、撤销与选区编辑（自 imageViewerMeasurementGeometry 拆分，2026-11-13）
 *
 * 共享夹具见 ./helpers/imageViewerHarness，Konva 替身见 ./helpers/konvaMock。
 */
import { cleanup, render, screen, fireEvent } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import ImageViewer, { type MeasurementGeometry } from '../components/ImageViewer';
import type { VisionRoi } from '../types';
import { diagnosticManifest } from './fixtures/caliperDiagnostics';
import { circleGeometry, pickCirclePoint, startCircleDrawing, imageView, overlays, emptyRoi, stubResizeObserver } from './helpers/imageViewerHarness';

vi.mock('react-konva', async () => await import('./helpers/konvaMock'));

beforeEach(() => { stubResizeObserver(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('ImageViewer measurement geometry editing', () => {
  it('picks a captured spoke without editing the circle or ROI and highlights the selected run point', () => {
    const manifest = diagnosticManifest('run-a', 7, 4);
    const onSelect = vi.fn(), onGeometryChange = vi.fn(), onRoiChange = vi.fn();
    const selection = { center: manifest.center, radius: manifest.nominalRadius, spokes: manifest.spokes, index: 0, onSelect };
    const geometry: MeasurementGeometry = { type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 4 };
    const view = render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roiEnabled={false} onRoiChange={onRoiChange} measurementGeometry={geometry} measurementGeometryEditable
      onMeasurementGeometryChange={onGeometryChange} caliperDiagnosticSelection={selection} />);
    fireEvent.click(screen.getByRole('button', { name: '原始比例' }));
    fireEvent.mouseDown(screen.getByTestId('stage'), { clientX: 220, clientY: 130 });
    fireEvent.mouseUp(document.body);
    expect(onSelect).toHaveBeenCalledExactlyOnceWith(0);
    expect(onGeometryChange).not.toHaveBeenCalled(); expect(onRoiChange).not.toHaveBeenCalled();
    view.rerender(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roiEnabled={false} onRoiChange={onRoiChange} caliperDiagnosticSelection={{ ...selection, index: 1 }} />);
    const edge = screen.getByTestId('caliper-diagnostic-selected-edge');
    expect(Number(edge.dataset.x)).toBeCloseTo(50); expect(Number(edge.dataset.y)).toBeCloseTo(70);
    const points = screen.getByTestId('caliper-diagnostic-selected-spoke').getAttribute('data-points')!.split(',').map(Number);
    expect(points[0]).toBeCloseTo(50); expect(points[1]).toBeCloseTo(66); expect(points[3]).toBeCloseTo(74);
  });

  it('pans from diagnostic background without selecting a spoke or editing geometry', () => {
    const manifest = diagnosticManifest(), onSelect = vi.fn(), onRoiChange = vi.fn();
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roiEnabled={false} onRoiChange={onRoiChange}
      caliperDiagnosticSelection={{ center: manifest.center, radius: manifest.nominalRadius, spokes: manifest.spokes, index: 0, onSelect }} />);
    fireEvent.click(screen.getByRole('button', { name: '原始比例' }));
    fireEvent.mouseDown(screen.getByTestId('stage'), { clientX: 160, clientY: 90 });
    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 180, clientY: 110 });
    fireEvent.mouseUp(document.body);
    expect(onSelect).not.toHaveBeenCalled(); expect(onRoiChange).not.toHaveBeenCalled();
  });



  it('redraws from diameter endpoints with a live size preview and preserves caliper settings', () => {
    const onChange = vi.fn();
    imageView(circleGeometry, onChange);
    startCircleDrawing('直径两端');
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 170, clientY: 130 }); // image (20,50)
    fireEvent.mouseMove(stage, { clientX: 230, clientY: 130 }); // image (80,50)
    expect(screen.getByLabelText('圈选尺寸').textContent).toContain('圆心 (50.00, 50.00) · 半径 30.00 px');
    fireEvent.mouseUp(document.body);
    expect(onChange).toHaveBeenCalledExactlyOnceWith({ ...circleGeometry, radius: 30 });
    expect(screen.queryByLabelText('标称圆圈选提示')).toBeNull();
  });

  it('previews and constructs a circle through three edge points without guessing its center', () => {
    const onChange = vi.fn();
    imageView(circleGeometry, onChange);
    startCircleDrawing('三点定圆');
    pickCirclePoint(20, 50);
    pickCirclePoint(50, 20);
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: 230, clientY: 130 });
    expect(screen.getByLabelText('圈选尺寸').textContent).toContain('半径 30.00 px');
    pickCirclePoint(80, 50);
    expect(onChange).toHaveBeenCalledExactlyOnceWith({ ...circleGeometry, radius: 30 });
  });

  it.each([false, true])('constructs and previews the upper search arc regardless of endpoint pick order (reversed=%s)', reverse => {
    const onChange = vi.fn();
    imageView(circleGeometry, onChange);
    startCircleDrawing('三点选弧');
    pickCirclePoint(reverse ? 80 : 20, 50);
    expect(screen.getByText(/点选圆弧第 2\/3 点/)).toBeTruthy();
    pickCirclePoint(50, 20);
    fireEvent.mouseMove(screen.getByTestId('stage'), { clientX: (reverse ? 20 : 80) + 150, clientY: 130 });
    expect(screen.getByLabelText('圈选尺寸').textContent).toContain('圆弧 180.0°');
    pickCirclePoint(reverse ? 20 : 80, 50);
    expect(onChange).toHaveBeenCalledExactlyOnceWith({ ...circleGeometry, radius: 30, angleStartDeg: -180, angleEndDeg: 0 });
  });

  it('undoes a three-point arc and its circle together', () => {
    function Harness() {
      const [geometry, setGeometry] = useState<MeasurementGeometry>(circleGeometry);
      return <><ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100} overlays={overlays}
        roiEnabled={false} onRoiChange={emptyRoi} measurementGeometry={geometry} measurementGeometryEditable onMeasurementGeometryChange={setGeometry} />
        <output data-testid="geometry-state">{JSON.stringify(geometry)}</output></>;
    }
    render(<Harness />);
    startCircleDrawing('三点选弧');
    pickCirclePoint(20, 50); pickCirclePoint(50, 20); pickCirclePoint(80, 50);
    expect(JSON.parse(screen.getByTestId('geometry-state').textContent!)).toMatchObject({ radius: 30, angleStartDeg: -180, angleEndDeg: 0 });
    fireEvent.click(screen.getByRole('button', { name: '撤销测量编辑' }));
    expect(JSON.parse(screen.getByTestId('geometry-state').textContent!)).toEqual(circleGeometry);
  });

  it('retains the first two points after a collinear third pick and accepts a corrected pick', () => {
    const onChange = vi.fn();
    imageView(circleGeometry, onChange);
    startCircleDrawing('三点定圆');
    pickCirclePoint(20, 50);
    pickCirclePoint(50, 50);
    pickCirclePoint(80, 50);
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole('alert').textContent).toContain('重新选择第 3 点');
    pickCirclePoint(50, 20);
    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0]).toMatchObject({ center: { x: 35, y: 35 }, radius: Math.hypot(15, 15) });
  });

  it('supports retracting picks and cancelling without changing the saved geometry', () => {
    const onChange = vi.fn();
    imageView(circleGeometry, onChange);
    startCircleDrawing('三点定圆');
    pickCirclePoint(20, 50);
    pickCirclePoint(20, 50);
    expect(screen.getByRole('alert').textContent).toContain('第 2 点距离第 1 点过近');
    pickCirclePoint(50, 20);
    fireEvent.click(screen.getByRole('button', { name: '撤回上一点' }));
    expect(screen.getByText(/沿目标圆边缘点选第 2\/3 点/)).toBeTruthy();
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByLabelText('标称圆圈选提示')).toBeNull();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('undoes a three-point circle construction as one measurement edit', () => {
    function Harness() {
      const [geometry, setGeometry] = useState<MeasurementGeometry>(circleGeometry);
      return <><ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100} overlays={overlays}
        roiEnabled={false} onRoiChange={emptyRoi} measurementGeometry={geometry} measurementGeometryEditable onMeasurementGeometryChange={setGeometry} />
        <output data-testid="geometry-state">{JSON.stringify(geometry)}</output></>;
    }
    render(<Harness />);
    startCircleDrawing('三点定圆');
    pickCirclePoint(20, 50); pickCirclePoint(50, 20); pickCirclePoint(80, 50);
    expect(JSON.parse(screen.getByTestId('geometry-state').textContent!).radius).toBe(30);
    fireEvent.click(screen.getByRole('button', { name: '撤销测量编辑' }));
    expect(JSON.parse(screen.getByTestId('geometry-state').textContent!)).toEqual(circleGeometry);
  });

  it('accepts three touch picks without treating them as circle drags', () => {
    const onChange = vi.fn();
    imageView(circleGeometry, onChange);
    startCircleDrawing('三点定圆');
    for (const [x, y] of [[20, 50], [50, 20], [80, 50]]) {
      fireEvent.touchStart(screen.getByTestId('stage'), { touches: [{ clientX: x + 150, clientY: y + 80 }] });
      fireEvent.touchEnd(document.body);
    }
    expect(onChange).toHaveBeenCalledExactlyOnceWith({ ...circleGeometry, radius: 30 });
  });

  it('clears unfinished point picks when switching construction methods', () => {
    const onChange = vi.fn();
    imageView(circleGeometry, onChange);
    startCircleDrawing('三点定圆');
    pickCirclePoint(20, 50); pickCirclePoint(50, 20);
    fireEvent.click(screen.getByRole('radio', { name: '直径两端' }));
    fireEvent.click(screen.getByRole('radio', { name: '三点定圆' }));
    expect(screen.getByText(/沿目标圆边缘点选第 1\/3 点/)).toBeTruthy();
    expect((screen.getByRole('button', { name: '撤回上一点' }) as HTMLButtonElement).disabled).toBe(true);
    pickCirclePoint(80, 50);
    expect(onChange).not.toHaveBeenCalled();
  });

  it('consumes Escape during construction before a containing dialog can close', () => {
    const dialogKeyDown = vi.fn();
    render(<div onKeyDown={dialogKeyDown}><ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roiEnabled={false} onRoiChange={emptyRoi} measurementGeometry={circleGeometry}
      measurementGeometryEditable onMeasurementGeometryChange={vi.fn()} /></div>);
    startCircleDrawing('三点定圆');
    pickCirclePoint(20, 50);
    fireEvent.keyDown(screen.getByTestId('stage'), { key: 'Escape' });
    expect(screen.queryByLabelText('标称圆圈选提示')).toBeNull();
    expect(dialogKeyDown).not.toHaveBeenCalled();
    fireEvent.keyDown(screen.getByTestId('stage'), { key: 'Escape' });
    expect(dialogKeyDown).toHaveBeenCalledOnce();
  });

  it('keeps the image canvas usable when a legacy ROI has malformed shape data', () => {
    const invalidRoi = { type: 'Polygon' } as unknown as VisionRoi;
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={invalidRoi} roiEnabled={false} onRoiChange={emptyRoi} />);

    expect(screen.getByTestId('stage')).toBeTruthy();
  });

  it.each([
    ['double-clicking the final vertex', [[10, 10], [60, 10], [60, 60], [10, 60], [10, 60]]],
    ['double-clicking the first vertex to close', [[10, 10], [60, 10], [60, 60], [10, 60], [10, 10], [10, 10]]]
  ])('removes the duplicate terminal vertex when %s', (_description, clicks) => {
    const RoiHarness = () => {
      const [roi, setRoi] = useState<VisionRoi | undefined>();
      return <>
        <ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
          overlays={overlays} roi={roi} roiEnabled onRoiChange={setRoi} />
        <output data-testid="roi-state">{JSON.stringify(roi)}</output>
      </>;
    };
    render(<RoiHarness />);
    fireEvent.click(screen.getByRole('button', { name: '原始比例' }));
    fireEvent.click(screen.getByRole('radio', { name: '多边形' }));
    const stage = screen.getByTestId('stage');
    for (const [clientX, clientY] of clicks)
      fireEvent.click(stage, { clientX: clientX + 150, clientY: clientY + 80 });
    const [clientX, clientY] = clicks.at(-1)!;
    fireEvent.doubleClick(stage, { clientX: clientX + 150, clientY: clientY + 80 });

    const roi = JSON.parse(screen.getByTestId('roi-state').textContent!) as VisionRoi;
    expect(roi.type).toBe('Polygon');
    if (roi.type === 'Polygon') {
      expect(roi.points).toHaveLength(4);
      expect(roi.points[0]).not.toEqual(roi.points.at(-1));
      expect(new Set(roi.points.map(point => `${point.x},${point.y}`)).size).toBe(4);
    }
  });

  it('undoes the last canvas ROI edit and hides stale undo after an external edit', () => {
    const RoiHarness = () => {
      const [roi, setRoi] = useState<VisionRoi | undefined>();
      return <>
        <ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
          overlays={overlays} roi={roi} roiEnabled onRoiChange={setRoi} />
        <output data-testid="roi-state">{JSON.stringify(roi)}</output>
        <button onClick={() => setRoi({ type: 'Rectangle', x: 1, y: 1, width: 10, height: 10 })}>外部修改</button>
      </>;
    };
    render(<RoiHarness />);
    fireEvent.click(screen.getByRole('radio', { name: '矩形' }));
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 });
    fireEvent.mouseMove(stage, { clientX: 152.8, clientY: 82.8 });
    fireEvent.mouseUp(document.body, { clientX: 152.8, clientY: 82.8 });

    const drawn = JSON.parse(screen.getByTestId('roi-state').textContent!);
    expect(drawn.type).toBe('Rectangle');
    expect(drawn.x).toBeCloseTo(10);
    expect(drawn.y).toBeCloseTo(10);
    expect(drawn.width).toBeCloseTo(20);
    expect(drawn.height).toBeCloseTo(20);
    fireEvent.click(screen.getByRole('button', { name: '撤销选区编辑' }));
    expect(screen.getByTestId('roi-state').textContent).toBe('');
    expect(screen.queryByRole('button', { name: '撤销选区编辑' })).toBeNull();

    fireEvent.click(screen.getByRole('radio', { name: '矩形' }));
    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 });
    fireEvent.mouseMove(stage, { clientX: 152.8, clientY: 82.8 });
    fireEvent.mouseUp(document.body, { clientX: 152.8, clientY: 82.8 });
    expect(screen.getByRole('button', { name: '撤销选区编辑' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '外部修改' }));
    expect(screen.getByTestId('roi-state').textContent).toContain('"x":1');
    expect(screen.queryByRole('button', { name: '撤销选区编辑' })).toBeNull();
  });

  it('draws a replacement rectangle from inside the existing ROI when the rectangle tool is active', () => {
    const roi: VisionRoi = { type: 'Rectangle', x: 20, y: 20, width: 40, height: 40 };
    const onRoiChange = vi.fn();
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={onRoiChange} />);

    fireEvent.click(screen.getByRole('radio', { name: '矩形' }));
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 164.6, clientY: 94.6 }); // image (35,35), inside the old ROI
    fireEvent.mouseMove(stage, { clientX: 200, clientY: 130 }); // image (50,50)
    fireEvent.mouseUp(document.body, { clientX: 200, clientY: 130 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    expect(onRoiChange.mock.calls[0][0]).toEqual({ type: 'Rectangle', x: 35, y: 35, width: 15, height: 15 });
  });

  it('continues drawing a crop ROI when the pointer moves outside the viewer', () => {
    const onRoiChange = vi.fn();
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roiEnabled onRoiChange={onRoiChange} />);

    fireEvent.click(screen.getByRole('radio', { name: '矩形' }));
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 }); // image (10,10)
    fireEvent.mouseLeave(stage);
    fireEvent.mouseMove(document.body, { clientX: 450, clientY: 130 }); // beyond canvas; clamps to image edge (100,50)
    fireEvent.mouseUp(document.body, { clientX: 450, clientY: 130 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const drawn = onRoiChange.mock.calls[0][0] as Extract<VisionRoi, { type: 'Rectangle' }>;
    expect(drawn.type).toBe('Rectangle');
    expect(drawn.x).toBeCloseTo(10);
    expect(drawn.y).toBeCloseTo(10);
    expect(drawn.width).toBeCloseTo(90);
    expect(drawn.height).toBeCloseTo(40);
  });

  it('continues dragging caliper geometry when the pointer leaves the viewer', () => {
    const onChange = vi.fn();
    imageView({ type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } }, onChange);
    const stage = screen.getByTestId('stage');

    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 }); // start handle
    fireEvent.mouseLeave(stage);
    fireEvent.mouseMove(document.body, { clientX: 450, clientY: 35.6 }); // continue dragging outside the canvas
    fireEvent.mouseUp(document.body, { clientX: 450, clientY: 35.6 });

    expect(onChange).toHaveBeenCalledOnce();
    const edited = onChange.mock.calls[0][0] as Extract<MeasurementGeometry, { type: 'line' }>;
    expect(edited.start.x).toBeGreaterThan(100);
    expect(edited.start.y).toBeCloseTo(10);
  });

  it('undoes the last caliper geometry drag', () => {
    const initial: MeasurementGeometry = { type: 'line', start: { x: 10, y: 10 }, end: { x: 50, y: 10 } };
    const GeometryHarness = () => {
      const [geometry, setGeometry] = useState<MeasurementGeometry>(initial);
      return <>
        <ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
          overlays={overlays} roiEnabled={false} onRoiChange={emptyRoi} measurementGeometry={geometry}
          measurementGeometryEditable onMeasurementGeometryChange={setGeometry} />
        <output data-testid="geometry-state">{JSON.stringify(geometry)}</output>
        <button onClick={() => setGeometry({ ...initial, start: { x: 5, y: 5 } })}>外部修改测量几何</button>
      </>;
    };
    render(<GeometryHarness />);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 });
    fireEvent.mouseMove(stage, { clientX: 129.2, clientY: 35.6 });
    fireEvent.mouseUp(document.body, { clientX: 129.2, clientY: 35.6 });

    expect(JSON.parse(screen.getByTestId('geometry-state').textContent!).start.x).toBeCloseTo(20);
    fireEvent.click(screen.getByRole('button', { name: '撤销测量编辑' }));
    expect(JSON.parse(screen.getByTestId('geometry-state').textContent!)).toEqual(initial);
    expect(screen.queryByRole('button', { name: '撤销测量编辑' })).toBeNull();

    fireEvent.mouseDown(stage, { clientX: 105.6, clientY: 35.6 });
    fireEvent.mouseMove(stage, { clientX: 129.2, clientY: 35.6 });
    fireEvent.mouseUp(document.body, { clientX: 129.2, clientY: 35.6 });
    expect(screen.getByRole('button', { name: '撤销测量编辑' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '外部修改测量几何' }));
    expect(screen.queryByRole('button', { name: '撤销测量编辑' })).toBeNull();
  });

  it('undoes circle edits after the arc angles are canonicalized', () => {
    const initial: MeasurementGeometry = { type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
      angleStartDeg: 350, angleEndDeg: 370, samplingMode: 'count', caliperCount: 12 };
    const CircleHarness = () => {
      const [geometry, setGeometry] = useState<MeasurementGeometry>(initial);
      const updateGeometry = (next: MeasurementGeometry, options?: { preserveValues?: boolean }) => {
        if (next.type !== 'circle' || options?.preserveValues) { setGeometry(next); return; }
        const start = next.angleStartDeg ?? -180;
        const end = next.angleEndDeg ?? 180;
        const turns = Math.round(((start + end) / 2) / 360);
        setGeometry({ ...next, angleStartDeg: start - turns * 360, angleEndDeg: end - turns * 360,
          band: Math.max(1, Math.min(500, next.band ?? 1)) });
      };
      return <>
        <ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
          overlays={overlays} roiEnabled={false} onRoiChange={emptyRoi} measurementGeometry={geometry}
          measurementGeometryEditable onMeasurementGeometryChange={updateGeometry} />
        <output data-testid="geometry-state">{JSON.stringify(geometry)}</output>
      </>;
    };
    render(<CircleHarness />);
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 233.375, clientY: 163.375 }); // radius handle at (64.14, 64.14)
    fireEvent.mouseMove(stage, { clientX: 241.669, clientY: 171.669 }); // increase radius from 20 to 25 px
    fireEvent.mouseUp(document.body, { clientX: 241.669, clientY: 171.669 });

    const edited = JSON.parse(screen.getByTestId('geometry-state').textContent!);
    expect(edited.radius).toBeCloseTo(25, 1);
    expect(edited.angleStartDeg).toBeCloseTo(-10);
    expect(edited.angleEndDeg).toBeCloseTo(10);
    fireEvent.click(screen.getByRole('button', { name: '撤销测量编辑' }));
    expect(JSON.parse(screen.getByTestId('geometry-state').textContent!)).toEqual(initial);
  });

  it('can focus a polygon ROI with hundreds of thousands of vertices', () => {
    const points = Array.from({ length: 200_000 }, (_, index) => {
      const angle = index * 2 * Math.PI / 200_000;
      return { x: 50 + 20 * Math.cos(angle), y: 50 + 20 * Math.sin(angle) };
    });
    const roi: VisionRoi = { type: 'Polygon', points };
    const onRoiChange = vi.fn();
    const view = render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={onRoiChange} focusTick={0} />);
    expect(screen.getAllByTestId('roi-handle').length).toBeLessThanOrEqual(33);
    expect(Number(screen.getByTestId('roi-polygon-preview').getAttribute('data-point-count'))).toBeLessThanOrEqual(8192);
    expect(screen.getByText('复杂多边形 · 预览与控制点抽样')).toBeTruthy();

    view.rerender(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={onRoiChange} focusTick={1} />);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 200, clientY: 130 }); // image center after focus
    fireEvent.mouseMove(stage, { clientX: 225.5, clientY: 130 }); // move 5 image pixels
    fireEvent.mouseUp(document.body, { clientX: 225.5, clientY: 130 });

    expect(screen.getByTestId('stage')).toBeTruthy();
    expect(onRoiChange).toHaveBeenCalledOnce();
    const moved = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(moved.type).toBe('Polygon');
    if (moved.type === 'Polygon') {
      expect(moved.points).toHaveLength(200_000);
      expect(moved.points[0].x).toBeCloseTo(75);
    }
  });

  it('can move a complex polygon from a real ROI area omitted by the display sample', () => {
    const points = [
      ...Array.from({ length: 2048 }, () => ({ x: 20, y: 20 })),
      ...Array.from({ length: 2048 }, () => ({ x: 60, y: 20 })),
      { x: 60, y: 20 }, // even index: retained by the display sample
      { x: 90, y: 45 }, // odd index: omitted by the display sample
      ...Array.from({ length: 2048 }, () => ({ x: 60, y: 70 })),
      ...Array.from({ length: 2048 }, () => ({ x: 20, y: 70 }))
    ];
    expect(points).toHaveLength(8194);
    const roi: VisionRoi = { type: 'Polygon', points };
    const onRoiChange = vi.fn();
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    // (75,45) lies inside the full triangular extension, but outside the sampled square preview.
    fireEvent.mouseDown(stage, { clientX: 259, clientY: 118.2 });
    fireEvent.mouseMove(stage, { clientX: 270.8, clientY: 118.2 });
    fireEvent.mouseUp(document.body, { clientX: 270.8, clientY: 118.2 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const moved = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(moved.type).toBe('Polygon');
    if (moved.type === 'Polygon') {
      expect(moved.points).toHaveLength(8194);
      expect(moved.points[0].x).toBeCloseTo(25);
      expect(moved.points[4097].x).toBeCloseTo(95);
    }
  });

});
