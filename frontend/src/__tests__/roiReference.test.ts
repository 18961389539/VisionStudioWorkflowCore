import { describe, expect, it } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { applyReferenceParameters, capturedReference, imageFrameFingerprint, poseSourceFingerprint, rebaseRoi } from '../roiReference';

describe('ROI reference capture', () => {
  it('captures pixels and the actual pose together and rebases a legacy ROI without mutating it', () => {
    const previous = { baseX: 100, baseY: 50, baseThetaDeg: 0, roi: { type: 'Circle' as const, x: 130, y: 60, radius: 8 } };
    const next = capturedReference(previous, { referenceCaptured: true, referenceImage: 'png-bytes', referenceWidth: 640,
      referenceHeight: 480, referenceX: 200, referenceY: 70, referenceThetaDeg: 0 }, 'binding', '2026-10-16T00:00:00Z');
    expect(next.roi).toEqual({ type: 'Circle', x: 230, y: 80, radius: 8 });
    expect(next).toMatchObject({ baseX: 200, baseY: 70, referenceImage: 'png-bytes', referenceBindings: 'binding' });
    expect(previous.roi.x).toBe(130);
  });

  it('preserves an invalid ROI draft so a new reference can still be captured and the region replaced', () => {
    const invalidRoi = { type: 'Polygon' };
    const next = capturedReference({ roi: invalidRoi }, { referenceCaptured: true, referenceImage: 'png-bytes',
      referenceWidth: 640, referenceHeight: 480, referenceX: 200, referenceY: 70, referenceThetaDeg: 0 }, 'binding', 'captured-at');

    expect(next.roi).toEqual(invalidRoi);
    expect(next).toMatchObject({ baseX: 200, baseY: 70, referenceImage: 'png-bytes', referenceBindings: 'binding' });
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

  it('invalidates a saved reference when its camera or synchronized frame source changes', () => {
    const acquisition = (cameraId: string): Node => ({ id: 'camera', position: { x: 0, y: 0 }, data: {
      typeKey: 'image.acquire', parameters: { cameraId }
    } });
    const cameraA = imageFrameFingerprint('camera', [acquisition('camera-a')], []);
    const cameraB = imageFrameFingerprint('camera', [acquisition('camera-b')], []);
    expect(cameraA).not.toBe(cameraB);

    const frameImage: Node = { id: 'frame-image', position: { x: 0, y: 0 }, data: {
      typeKey: 'frameset.image', inputs: [{ name: 'frameSet', dataType: 'FrameSet' }], parameters: { cameraId: 'camera-a' }
    } };
    const sync = (groupId: string): Node => ({ id: 'sync', position: { x: 0, y: 0 }, data: {
      typeKey: 'camera.syncCapture', parameters: { groupId }
    } });
    const edge: Edge = { id: 'frame-set', source: 'sync', sourceHandle: 'frameSet', target: 'frame-image', targetHandle: 'frameSet' };
    const frameSetA = imageFrameFingerprint('frame-image', [frameImage, sync('line-a')], [edge]);
    const frameSetB = imageFrameFingerprint('frame-image', [frameImage, sync('line-b')], [edge]);
    expect(frameSetA).not.toBe(frameSetB);
    const selectedCameraB: Node = { ...frameImage, data: { ...frameImage.data, parameters: { cameraId: 'camera-b' } } };
    expect(imageFrameFingerprint('frame-image', [selectedCameraB, sync('line-a')], [edge])).not.toBe(frameSetA);
  });

  it('invalidates a saved reference when synthetic image geometry or placement settings change', () => {
    const synthetic = (parameters: Record<string, unknown>): Node => ({ id: 'synthetic', position: { x: 0, y: 0 }, data: {
      typeKey: 'image.synthetic', parameters
    } });
    const base = { width: 640, height: 480, centerX: 320, centerY: 240, radius: 80, placementMode: 'fixed' };
    const baseline = imageFrameFingerprint('synthetic', [synthetic(base)], []);
    expect(imageFrameFingerprint('synthetic', [synthetic({ ...base, centerX: 321 })], [])).not.toBe(baseline);
    expect(imageFrameFingerprint('synthetic', [synthetic({ ...base, placementMode: 'sequence', sampleIndex: 2 })], [])).not.toBe(baseline);
    expect(imageFrameFingerprint('synthetic', [synthetic(base)], [])).toBe(baseline);
  });

  it('tracks the complete pose-producing graph while excluding training-only sample payloads', () => {
    const matcher = (parameters: Record<string, unknown>): Node => ({ id: 'matcher', position: { x: 0, y: 0 }, data: {
      typeKey: 'match.shape', inputs: [
        { name: 'image', dataType: 'Image' }, { name: 'template', dataType: 'Image' }
      ], parameters
    } });
    const scene = (angleDeg: number): Node => ({ id: 'scene', position: { x: 0, y: 0 }, data: {
      typeKey: 'image.rotate', inputs: [{ name: 'image', dataType: 'Image' }], parameters: { angleDeg, expand: false }
    } });
    const source: Node = { id: 'camera', position: { x: 0, y: 0 }, data: {
      typeKey: 'image.acquire', parameters: { cameraId: 'camera-a' }
    } };
    const edges: Edge[] = [
      { id: 'scene-input', source: 'camera', sourceHandle: 'image', target: 'scene', targetHandle: 'image' },
      { id: 'matcher-image', source: 'scene', sourceHandle: 'image', target: 'matcher', targetHandle: 'image' }
    ];
    const baseline = poseSourceFingerprint('matcher', [matcher({
      trainedModelHash: 'model-a', searchAngleStartDeg: -30, trainingSamples: [{ data: 'sample-a' }]
    }), scene(0), source], edges);
    expect(poseSourceFingerprint('matcher', [matcher({
      trainedModelHash: 'model-a', searchAngleStartDeg: -20, trainingSamples: [{ data: 'sample-a' }]
    }), scene(0), source], edges)).not.toBe(baseline);
    expect(poseSourceFingerprint('matcher', [matcher({
      trainedModelHash: 'model-b', searchAngleStartDeg: -30, trainingSamples: [{ data: 'sample-a' }]
    }), scene(0), source], edges)).not.toBe(baseline);
    expect(poseSourceFingerprint('matcher', [matcher({
      trainedModelHash: 'model-a', searchAngleStartDeg: -30, trainingSamples: [{ data: 'sample-b' }]
    }), scene(0), source], edges)).toBe(baseline);
    expect(poseSourceFingerprint('matcher', [matcher({
      trainedModelHash: 'model-a', searchAngleStartDeg: -30, trainingSamples: [{ data: 'sample-a' }]
    }), scene(12), source], edges)).not.toBe(baseline);
    const changedCamera = { ...source, data: { ...source.data, parameters: { cameraId: 'camera-b' } } };
    expect(poseSourceFingerprint('matcher', [matcher({
      trainedModelHash: 'model-a', searchAngleStartDeg: -30, trainingSamples: [{ data: 'sample-a' }]
    }), scene(0), changedCamera], edges)).not.toBe(baseline);
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

  it.each(['measure.caliperCircle', 'measure.caliperLine', 'measure.caliper', 'feature.edgeSubpixel'])(
    'rebases %s geometry through an ROI-then-crop transform composition', type => {
      const geometry = type === 'measure.caliperCircle'
        ? { centerX: 130, centerY: 60, radius: 8 }
        : { startX: 130, startY: 60, endX: 150, endY: 70 };
      const nodes = [
        { id: 'roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', parameters: { baseX: 100, baseY: 50 } } },
        { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: 'image.crop', parameters: {} } },
        { id: 'compose', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformCompose', parameters: {} } },
        { id: 'measure', position: { x: 0, y: 0 }, data: { typeKey: type, parameters: geometry } }
      ];
      const edges: Edge[] = [
        { id: 'roi-compose', source: 'roi', sourceHandle: 'transform', target: 'compose', targetHandle: 'transformA' },
        { id: 'crop-compose', source: 'crop', sourceHandle: 'transform', target: 'compose', targetHandle: 'transformB' },
        { id: 'compose-measure', source: 'compose', sourceHandle: 'transform', target: 'measure', targetHandle: 'transform' }
      ];

      const moved = applyReferenceParameters(nodes, edges, 'roi', { baseX: 200, baseY: 70, baseThetaDeg: 0 });
      const movedGeometry = moved.find(node => node.id === 'measure')!.data.parameters;

      if (type === 'measure.caliperCircle') expect(movedGeometry).toMatchObject({ centerX: 230, centerY: 80, radius: 8 });
      else expect(movedGeometry).toMatchObject({ startX: 230, startY: 80, endX: 250, endY: 90 });
    });

  it('does not rebase geometry when the ROI transform is the second composition input', () => {
    const nodes = [
      { id: 'roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', parameters: { baseX: 100, baseY: 50 } } },
      { id: 'compose', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformCompose', parameters: {} } },
      { id: 'measure', position: { x: 0, y: 0 }, data: { typeKey: 'measure.caliperLine', parameters: { startX: 130, startY: 60, endX: 150, endY: 70 } } }
    ];
    const edges: Edge[] = [
      { id: 'roi-compose', source: 'roi', sourceHandle: 'transform', target: 'compose', targetHandle: 'transformB' },
      { id: 'compose-measure', source: 'compose', sourceHandle: 'transform', target: 'measure', targetHandle: 'transform' }
    ];

    const moved = applyReferenceParameters(nodes, edges, 'roi', { baseX: 200, baseY: 70, baseThetaDeg: 0 });

    expect(moved[2].data.parameters).toEqual(nodes[2].data.parameters);
  });
});
