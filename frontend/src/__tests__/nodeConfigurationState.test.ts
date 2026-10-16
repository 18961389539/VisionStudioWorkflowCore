import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { cloneParameters, configurationIssues, hydrateNodeParameters, parameterFingerprint, withParameter, workflowWithNodeParameters } from '../nodeConfigurationState';
import type { NodeCatalogItem, WorkflowPayload } from '../types';

describe('node configuration snapshots', () => {
  it('preserves legacy shape nodes across catalog hydration while new nodes stay in trained mode', () => {
    const shape = fallbackCatalogRaw.find((item) => item.type === 'match.shape')!;
    const defaults = makeDefaultParameters(shape);
    const loadedLegacy = hydrateNodeParameters({ minScore: 0.7 }, shape, defaults);
    expect(loadedLegacy.modelMode).toBe('legacy');
    expect(loadedLegacy.minScore).toBe(0.7);
    expect(hydrateNodeParameters(loadedLegacy, shape, defaults).modelMode).toBe('legacy');
    expect(makeDefaultParameters(shape).modelMode).toBe('trained');
  });

  it('preserves legacy caliper-circle angle-step sampling while new nodes use a direct count', () => {
    const caliper = fallbackCatalogRaw.find((item) => item.type === 'measure.caliperCircle')!;
    const defaults = makeDefaultParameters(caliper);
    expect(defaults.samplingMode).toBe('count');
    expect(defaults.maxFitOutliers).toBe(2);
    expect(hydrateNodeParameters({ angleStepDeg: 2.5 }, caliper, defaults).samplingMode).toBe('step');
    expect(hydrateNodeParameters({ angleStepDeg: 2.5, samplingMode: 'count' }, caliper, defaults).samplingMode).toBe('count');
    expect(hydrateNodeParameters({ angleStepDeg: 2.5 }, caliper, defaults).maxFitOutliers).toBe(0);
  });

  it('compares nested ROI values without depending on object key order', () => {
    expect(parameterFingerprint({ roi: { x: 1, y: 2 }, threshold: 128 })).toBe(parameterFingerprint({ threshold: 128, roi: { y: 2, x: 1 } }));
    expect(parameterFingerprint({ roi: { x: 1 } })).not.toBe(parameterFingerprint({ roi: { x: 2 } }));
  });

  it('clones nested values and removes a cleared ROI without changing the source', () => {
    const source = { roi: { type: 'Rectangle', x: 1, y: 2, width: 3, height: 4 }, threshold: 128 };
    const draft = cloneParameters(source);
    (draft.roi as { width: number }).width = 9;
    expect(source.roi.width).toBe(3);
    expect(withParameter(draft, 'roi', undefined)).toEqual({ threshold: 128 });
  });

  it('constructs an isolated testing workflow for only the configured node', () => {
    const workflow: WorkflowPayload = { id: 'wf', name: 'test', nodes: [
      { id: 'a', type: 'image.synthetic', parameters: { radius: 80 } },
      { id: 'b', type: 'image.threshold', parameters: { threshold: 128 } }
    ], edges: [] };
    const parameters = { threshold: 200, roi: { x: 1 } };
    const preview = workflowWithNodeParameters(workflow, 'b', parameters);
    expect(preview.nodes[0]).toBe(workflow.nodes[0]);
    expect(preview.nodes[1].parameters).toEqual(parameters);
    (preview.nodes[1].parameters!.roi as { x: number }).x = 10;
    expect(parameters.roi.x).toBe(1);
    expect(workflow.nodes[1].parameters).toEqual({ threshold: 128 });
    expect(() => workflowWithNodeParameters(workflow, 'missing', {})).toThrow('不存在');
  });

  it('rejects degenerate ROI geometry as well as invalid active parameters', () => {
    const catalog: NodeCatalogItem = { type: 'image.threshold', displayName: '阈值', category: '图像', inputs: [], outputs: [], parameters: [{ name: 'threshold', label: '阈值', type: 'number', min: 0, max: 255 }] };
    expect(configurationIssues(catalog, { threshold: 100, roi: { type: 'Rectangle', x: 0, y: 0, width: 0, height: 10 } })).toHaveLength(1);
    expect(configurationIssues(catalog, { threshold: 300 })).toHaveLength(1);
    expect(configurationIssues(catalog, { threshold: 100, roi: { type: 'Circle', x: 5, y: 6, radius: 2 } })).toEqual([]);
  });

  it('preflights polygon, coordinate, clutter, and node-specific brush limits before preview', () => {
    const crop = fallbackCatalogRaw.find(item => item.type === 'image.crop')!;
    const polygonIssue = configurationIssues(crop, { roi: { type: 'Polygon', points: [
      { x: 1, y: 1 }, { x: 2, y: 2 }, { x: 3, y: 3 }
    ] } });
    expect(polygonIssue.join(' ')).toContain('围成非零面积');

    const coordinateIssue = configurationIssues(crop, { roi: { type: 'Rectangle', x: 10_000_001, y: 0, width: 10, height: 10 } });
    expect(coordinateIssue.join(' ')).toContain('坐标在 ±10000000 px 范围内');

    const shape = fallbackCatalogRaw.find(item => item.type === 'match.shape')!;
    const clutterIssue = configurationIssues(shape, { ...makeDefaultParameters(shape), clutterRoi: {
      type: 'Rectangle', x: 0, y: 0, width: 0, height: 10
    } });
    expect(clutterIssue.join(' ')).toContain('杂乱区域矩形');

    const brush = { type: 'Brush', strokes: [{ mode: 'Add', width: 600, points: [{ x: 1, y: 1 }] }] };
    expect(configurationIssues(crop, { roi: brush }).join(' ')).toContain('笔宽须在 0..512 px');
    const transform = fallbackCatalogRaw.find(item => item.type === 'roi.transform')!;
    expect(configurationIssues(transform, { ...makeDefaultParameters(transform), roi: brush })).toEqual([]);
  });
});
