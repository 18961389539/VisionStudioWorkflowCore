/**
 * 笔刷选区与固定点数选区（自 imageViewerMeasurementGeometry 拆分，2026-11-13）
 *
 * 共享夹具见 ./helpers/imageViewerHarness，Konva 替身见 ./helpers/konvaMock。
 */
import { cleanup, render, screen, fireEvent } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ImageViewer from '../components/ImageViewer';
import type { VisionRoi } from '../types';
import { overlays, stubResizeObserver } from './helpers/imageViewerHarness';

vi.mock('react-konva', async () => await import('./helpers/konvaMock'));

beforeEach(() => { stubResizeObserver(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('ImageViewer measurement geometry editing', () => {
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
