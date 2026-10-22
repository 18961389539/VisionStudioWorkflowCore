import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import ImageViewer, { buildCaliperCircleAngles, type MeasurementGeometry } from '../components/ImageViewer';
import { circleArcSpan, effectiveSampleWidth, normalizeCaliperCircleArc, normalizeCaliperAngle } from '../caliperCircleSampling';
import type { VisionOverlay, VisionRoi } from '../types';
import { diagnosticManifest } from './fixtures/caliperDiagnostics';

vi.mock('react-konva', async () => {
  const React = await import('react');
  let brushStrokeRenderId = 0;
  return {
    Stage: React.forwardRef((props: Record<string, any>, ref: React.ForwardedRef<any>) => {
      const pointer = React.useRef<{ x: number; y: number } | undefined>(undefined);
      const setPointer = (event: any) => {
        const touch = event.touches?.[0] ?? event.changedTouches?.[0];
        pointer.current = { x: touch?.clientX ?? event.clientX, y: touch?.clientY ?? event.clientY };
      };
      React.useImperativeHandle(ref, () => ({ getPointerPosition: () => pointer.current, setPointersPositions: setPointer }));
      const dispatch = (handler?: (event: unknown) => void) => (event: any) => {
        setPointer(event);
        handler?.(event);
      };
      return <div data-testid="stage" onMouseDown={dispatch(props.onMouseDown)} onMouseMove={dispatch(props.onMouseMove)}
        onMouseUp={dispatch(props.onMouseUp)} onTouchStart={dispatch(props.onTouchStart)} onTouchMove={dispatch(props.onTouchMove)}
        onTouchEnd={dispatch(props.onTouchEnd)} onClick={dispatch(props.onClick)} onDoubleClick={dispatch(props.onDblClick)}
        onMouseLeave={props.onMouseLeave}>{props.children}</div>;
    }),
    Layer: ({ children }: React.PropsWithChildren) => <>{children}</>,
    Group: ({ children }: React.PropsWithChildren) => <>{children}</>,
    Arrow: ({ points, pointerAtBeginning, name }: { points?: number[]; pointerAtBeginning?: boolean; name?: string }) =>
      <span data-testid={name === 'caliper-diagnostic-selected-spoke' ? name : 'search-arrow'} data-points={points?.join(',')} data-pointer-at-beginning={pointerAtBeginning} />,
    Image: () => null,
    Line: ({ points, stroke, name }: { points?: number[]; stroke?: string; name?: string }) => name?.startsWith('measurement-profile-band-')
      || name?.startsWith('measurement-circle-profile-band-')
      ? <span data-testid="measurement-profile-band" data-band-index={name.slice(name.indexOf('profile-band-') + 'profile-band-'.length)} data-points={points?.join(',')} />
      : name?.startsWith('roi-brush-stroke-')
      ? <span data-testid="roi-brush-stroke" data-mode={name.slice('roi-brush-stroke-'.length)}
        data-render-id={++brushStrokeRenderId} data-point-count={points?.length ? points.length / 2 : 0} />
      : stroke === '#ff4d4f'
        ? <span data-testid="roi-polygon-preview" data-point-count={points?.length ? points.length / 2 : 0} /> : null,
    Circle: ({ name, x, y }: { name?: string; x?: number; y?: number }) => name?.startsWith('measurement-profile-span-') || name === 'measurement-profile-width'
      || name?.startsWith('caliper-circle-angle-') || name === 'caliper-diagnostic-selected-edge' ? <span data-testid={name} data-x={x} data-y={y} /> : null,
    Rect: ({ name }: { name?: string }) => name === 'roi-handle' ? <span data-testid={name} /> : null,
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

  const circleGeometry: Extract<MeasurementGeometry, { type: 'circle' }> = {
    type: 'circle', center: { x: 50, y: 50 }, radius: 20, band: 5,
    angleStartDeg: -90, angleEndDeg: 90, caliperCount: 24, profileWidth: 5
  };
  const pickCirclePoint = (x: number, y: number) => {
    fireEvent.mouseDown(screen.getByTestId('stage'), { clientX: x + 150, clientY: y + 80 });
    fireEvent.mouseUp(document.body);
  };
  const startCircleDrawing = (mode: string) => {
    fireEvent.click(screen.getByRole('button', { name: '原始比例' }));
    fireEvent.click(screen.getByRole('button', { name: '重绘标称圆' }));
    fireEvent.click(screen.getByRole('radio', { name: mode }));
  };

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

  it('moves a brush ROI by dragging a painted pixel and clamps its strokes to the image', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Brush', strokes: [
      { mode: 'Add', width: 10, points: [{ x: 10, y: 10 }, { x: 30, y: 10 }] },
      { mode: 'Erase', width: 4, points: [{ x: 20, y: 10 }] }
    ] };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    // Drag from a painted, unerased area to request x=0; the brush bounds should stop at x=0.
    fireEvent.mouseDown(stage, { clientX: 117.4, clientY: 35.6 }); // image (15, 10)
    fireEvent.mouseMove(stage, { clientX: 82, clientY: 35.6 }); // image (0, 10)
    fireEvent.mouseUp(document.body, { clientX: 82, clientY: 35.6 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    expect(onRoiChange).toHaveBeenCalledWith({ type: 'Brush', strokes: [
      { mode: 'Add', width: 10, points: [{ x: 5, y: 10 }, { x: 25, y: 10 }] },
      { mode: 'Erase', width: 4, points: [{ x: 15, y: 10 }] }
    ] });
  });

  it('does not let a remote eraser stroke restrict moving the selected brush region', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Brush', strokes: [
      { mode: 'Add', width: 4, points: [{ x: 10, y: 10 }, { x: 14, y: 10 }] },
      { mode: 'Erase', width: 4, points: [{ x: 90, y: 10 }] }
    ] };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    // Move the painted area from x=10..14 to x=94..98, flush with the image's right edge.
    fireEvent.mouseDown(stage, { clientX: 110.32, clientY: 35.6 }); // image (12, 10)
    fireEvent.mouseMove(stage, { clientX: 308.56, clientY: 35.6 }); // image (96, 10)
    fireEvent.mouseUp(document.body, { clientX: 308.56, clientY: 35.6 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const moved = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(moved.type).toBe('Brush');
    if (moved.type === 'Brush') {
      expect(moved.strokes[0].points[0].x).toBeCloseTo(94);
      expect(moved.strokes[0].points[1].x).toBeCloseTo(98);
      expect(moved.strokes[1].points[0].x).toBeCloseTo(174);
    }
  });

  it('keeps completed brush paths stable while drawing and commits the active stroke losslessly', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Brush', strokes: [
      { mode: 'Add', width: 6, points: [{ x: 10, y: 10 }, { x: 15, y: 10 }] },
      { mode: 'Erase', width: 2, points: [{ x: 12, y: 10 }] }
    ] };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    const completedAdd = screen.getAllByTestId('roi-brush-stroke')[0];
    const initialRenderId = completedAdd.getAttribute('data-render-id');
    fireEvent.click(screen.getByText('笔刷'));
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 129.2, clientY: 59.2 }); // image (20, 20)
    expect(screen.getAllByTestId('roi-brush-stroke')).toHaveLength(3);
    expect(screen.getAllByTestId('roi-brush-stroke')[0].getAttribute('data-render-id')).toBe(initialRenderId);

    fireEvent.mouseMove(stage, { clientX: 152.8, clientY: 59.2 }); // image (30, 20)
    expect(screen.getAllByTestId('roi-brush-stroke')[0].getAttribute('data-render-id')).toBe(initialRenderId);
    expect(screen.getAllByTestId('roi-brush-stroke')[2].getAttribute('data-point-count')).toBe('3');
    fireEvent.mouseUp(document.body, { clientX: 152.8, clientY: 59.2 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const committed = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(committed.type).toBe('Brush');
    if (committed.type === 'Brush') {
      expect(committed.strokes.slice(0, 2)).toEqual(roi.strokes);
      expect(committed.strokes).toHaveLength(3);
      const active = committed.strokes[2];
      expect(active.mode).toBe('Add');
      expect(active.width).toBe(20);
      expect(active.points).toHaveLength(3);
      expect(active.points[0].x).toBeCloseTo(20);
      expect(active.points[0].y).toBeCloseTo(20);
      expect(active.points[1]).toEqual(active.points[0]);
      expect(active.points[2].x).toBeCloseTo(30);
      expect(active.points[2].y).toBeCloseTo(20);
    }
  });

  it('continues a brush stroke outside the viewer and commits it on release', () => {
    const onRoiChange = vi.fn();
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    fireEvent.click(screen.getByText('笔刷'));
    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 129.2, clientY: 59.2 }); // image (20,20)
    fireEvent.mouseLeave(stage);
    fireEvent.mouseMove(document.body, { clientX: 152.8, clientY: 59.2 }); // image (30,20)
    fireEvent.mouseUp(document.body, { clientX: 152.8, clientY: 59.2 });

    expect(onRoiChange).toHaveBeenCalledOnce();
    const committed = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(committed.type).toBe('Brush');
    if (committed.type === 'Brush') {
      expect(committed.strokes).toHaveLength(1);
      expect(committed.strokes[0].points.at(-1)?.x).toBeCloseTo(30);
      expect(committed.strokes[0].points.at(-1)?.y).toBeCloseTo(20);
    }
  });

  it('does not start a brush move from a pixel erased by a later stroke', () => {
    const onRoiChange = vi.fn();
    const roi: VisionRoi = { type: 'Brush', strokes: [
      { mode: 'Add', width: 10, points: [{ x: 10, y: 10 }, { x: 30, y: 10 }] },
      { mode: 'Erase', width: 4, points: [{ x: 20, y: 10 }] }
    ] };
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roi={roi} showRoiTools roiEnabled onRoiChange={onRoiChange} />);

    const stage = screen.getByTestId('stage');
    fireEvent.mouseDown(stage, { clientX: 129.2, clientY: 35.6 }); // image (20, 10), erased
    fireEvent.mouseMove(stage, { clientX: 176.4, clientY: 35.6 }); // image (40, 10)
    fireEvent.mouseUp(document.body, { clientX: 176.4, clientY: 35.6 });

    expect(onRoiChange).not.toHaveBeenCalled();
  });
});

describe('ImageViewer fixed-point ROI', () => {
  it('only offers polygon editing and commits a four-corner ROI on the fourth click', () => {
    const onRoiChange = vi.fn();
    render(<ImageViewer previewUrl="https://example.test/image.png" imageWidth={100} imageHeight={100}
      overlays={overlays} roiEnabled onRoiChange={onRoiChange} roiPointLimit={4} />);
    fireEvent.click(screen.getByRole('button', { name: '原始比例' }));
    expect(screen.queryByText('矩形')).toBeNull();
    expect(screen.queryByText('圆形')).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: /多边形/ }));
    const stage = screen.getByTestId('stage');
    for (const [clientX, clientY] of [[160, 90], [240, 90], [240, 170], [160, 170]])
      fireEvent.click(stage, { clientX, clientY });
    expect(onRoiChange).toHaveBeenCalledTimes(1);
    const roi = onRoiChange.mock.calls[0][0] as VisionRoi;
    expect(roi.type).toBe('Polygon');
    expect(roi.type === 'Polygon' ? roi.points : []).toEqual([
      { x: 10, y: 10 }, { x: 90, y: 10 }, { x: 90, y: 90 }, { x: 10, y: 90 }
    ]);
  });
});
