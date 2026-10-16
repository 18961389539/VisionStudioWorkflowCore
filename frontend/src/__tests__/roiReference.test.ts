import { describe, expect, it } from 'vitest';
import { applyReferenceParameters, capturedReference, rebaseRoi } from '../roiReference';

describe('ROI reference capture', () => {
  it('captures pixels and the actual pose together and rebases a legacy ROI without mutating it', () => {
    const previous = { baseX: 100, baseY: 50, baseThetaDeg: 0, roi: { type: 'Circle' as const, x: 130, y: 60, radius: 8 } };
    const next = capturedReference(previous, { referenceCaptured: true, referenceImage: 'png-bytes', referenceWidth: 640,
      referenceHeight: 480, referenceX: 200, referenceY: 70, referenceThetaDeg: 0 }, 'binding', '2026-10-16T00:00:00Z');
    expect(next.roi).toEqual({ type: 'Circle', x: 230, y: 80, radius: 8 });
    expect(next).toMatchObject({ baseX: 200, baseY: 70, referenceImage: 'png-bytes', referenceBindings: 'binding' });
    expect(previous.roi.x).toBe(130);
  });

  it('rotates rectangles and brush erasers in the same direction as the backend correction', () => {
    expect(rebaseRoi({ type: 'Rectangle', x: 10, y: 0, width: 20, height: 10 }, {}, 100, 100, 90))
      .toEqual({ type: 'Polygon', points: [{ x: 100, y: 90 }, { x: 100, y: 70 }, { x: 110, y: 70 }, { x: 110, y: 90 }] });
    const brush = rebaseRoi({ type: 'Brush', strokes: [
      { mode: 'Add', width: 8, points: [{ x: 10, y: 20 }] },
      { mode: 'Erase', width: 4, points: [{ x: 10, y: 30 }] }
    ] }, {}, 100, 100, 90);
    expect(brush).toEqual({ type: 'Brush', strokes: [
      { mode: 'Add', width: 8, points: [{ x: 120, y: 90 }] },
      { mode: 'Erase', width: 4, points: [{ x: 130, y: 90 }] }
    ] });
  });

  it('refuses partial and failed captures instead of replacing a valid reference', () => {
    for (const summary of [
      { referenceCaptured: false },
      { referenceCaptured: true, referenceImage: 'png', referenceX: NaN, referenceY: 0, referenceThetaDeg: 0, referenceWidth: 10, referenceHeight: 10 },
      { referenceCaptured: true, referenceImage: 'png', referenceX: 0, referenceY: 0, referenceThetaDeg: 0, referenceWidth: 0, referenceHeight: 10 }
    ]) expect(() => capturedReference({}, summary, '', '')).toThrow('完整的基准图像');
  });

  it('moves connected measurement parameters atomically when the reference changes', () => {
    const nodes = [
      { id: 'roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', parameters: { baseX: 100, baseY: 50 } } },
      { id: 'measure', position: { x: 0, y: 0 }, data: { typeKey: 'measure.caliperCircle', parameters: { centerX: 130, centerY: 60, radius: 8 } } }
    ];
    const moved = applyReferenceParameters(nodes, [{ id: 't', source: 'roi', sourceHandle: 'transform', target: 'measure', targetHandle: 'transform' }],
      'roi', { baseX: 200, baseY: 70, baseThetaDeg: 0 });
    expect(moved[1].data.parameters).toMatchObject({ centerX: 230, centerY: 80, radius: 8 });
    expect(nodes[1].data.parameters).toMatchObject({ centerX: 130, centerY: 60 });
    expect(moved[0].data.parameters).toEqual({ baseX: 200, baseY: 70, baseThetaDeg: 0 });
  });
});
