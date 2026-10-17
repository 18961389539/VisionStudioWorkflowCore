import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { cloneParameters, configurationIssues, configurationParameters, cropRoiOutsideImageIssue, hydrateNodeParameters, parameterFingerprint, withParameter, workflowWithNodeParameters } from '../nodeConfigurationState';
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

  it('preflights crop ROIs that cannot cover an input pixel without rejecting clipped or uncertain regions', () => {
    expect(cropRoiOutsideImageIssue({ type: 'Rectangle', x: 100, y: 10, width: 8, height: 12 }, 100, 80)).toContain('没有像素交集');
    expect(cropRoiOutsideImageIssue({ type: 'Rectangle', x: 0.1, y: 0.1, width: 0.2, height: 0.2 }, 100, 80)).toContain('没有像素交集');
    expect(cropRoiOutsideImageIssue({ type: 'Rectangle', x: -3, y: 10, width: 8, height: 12 }, 100, 80)).toBeUndefined();
    expect(cropRoiOutsideImageIssue({ type: 'Circle', x: 120, y: 20, radius: 8 }, 100, 80)).toContain('没有像素交集');
    expect(cropRoiOutsideImageIssue({ type: 'Polygon', points: [{ x: 110, y: 10 }, { x: 120, y: 10 }, { x: 115, y: 20 }] }, 100, 80)).toContain('没有像素交集');
    expect(cropRoiOutsideImageIssue({ type: 'Brush', strokes: [{ mode: 'Add', width: 8, points: [{ x: 120, y: 20 }] }] }, 100, 80)).toContain('没有像素交集');
    expect(cropRoiOutsideImageIssue({ type: 'Brush', strokes: [
      { mode: 'Add', width: 4, points: [{ x: 5, y: 5 }, { x: 9, y: 5 }] },
      { mode: 'Erase', width: 4, points: [{ x: 5, y: 5 }, { x: 9, y: 5 }] }
    ] }, 100, 80)).toContain('擦除笔迹完全覆盖');
    expect(cropRoiOutsideImageIssue({ type: 'Brush', strokes: [
      { mode: 'Add', width: 4, points: [{ x: 0.5, y: 5 }] },
      { mode: 'Erase', width: 4, points: [{ x: 0.6, y: 5 }] }
    ] }, 100, 80)).toBeUndefined();
    expect(cropRoiOutsideImageIssue({ type: 'Circle', x: 99, y: 20, radius: 8 }, 100, 80)).toBeUndefined();
    expect(cropRoiOutsideImageIssue({ type: 'Rectangle', x: 120, y: 10, width: 8, height: 12 }, 0, 80)).toBeUndefined();

    const largePolygonPoints = Array.from({ length: 150_000 }, (_, index) => [
      { x: 10, y: 10 }, { x: 80, y: 10 }, { x: 80, y: 70 }, { x: 10, y: 70 }
    ][index % 4]);
    const largePolygon = { type: 'Polygon', points: largePolygonPoints };
    const crop = fallbackCatalogRaw.find(item => item.type === 'image.crop')!;
    expect(configurationIssues(crop, { roi: largePolygon })).toEqual([]);
    expect(cropRoiOutsideImageIssue(largePolygon, 100, 80)).toBeUndefined();

    const excessivePolygon = { type: 'Polygon', points: Array(500_001).fill({ x: 10, y: 10 }) };
    expect(configurationIssues(crop, { roi: excessivePolygon }).join(' ')).toContain('多边形最多支持 500000 个顶点');
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

    const brush = { type: 'Brush', strokes: [{ mode: 'Add', width: 1024, points: [{ x: 1, y: 1 }] }] };
    expect(configurationIssues(crop, { roi: brush })).toEqual([]);
    const oversizedBrush = { type: 'Brush', strokes: [{ mode: 'Add', width: 1025, points: [{ x: 1, y: 1 }] }] };
    expect(configurationIssues(crop, { roi: oversizedBrush }).join(' ')).toContain('笔宽须在 0..1024 px');
    const transform = fallbackCatalogRaw.find(item => item.type === 'roi.transform')!;
    expect(configurationIssues(transform, { ...makeDefaultParameters(transform), roi: oversizedBrush }).join(' ')).toContain('笔宽须在 0..1024 px');
  });

  it('does not validate a local ROI that a connected ROI input overrides', () => {
    const crop = fallbackCatalogRaw.find(item => item.type === 'image.crop')!;
    const invalidRoi = { type: 'Rectangle', x: 0, y: 0, width: 0, height: 10 };

    expect(configurationIssues(crop, { roi: invalidRoi })).toHaveLength(1);
    expect(configurationIssues(crop, { roi: invalidRoi }, { connectedInputPorts: ['roi'] })).toEqual([]);

    const shape = fallbackCatalogRaw.find(item => item.type === 'match.shape')!;
    expect(configurationIssues(shape, { roi: invalidRoi }, { connectedInputPorts: ['roi'] }).join(' ')).toContain('矩形须为正尺寸');
  });

  it('does not validate measurement geometry parameters overridden by connected inputs', () => {
    const line = fallbackCatalogRaw.find(item => item.type === 'measure.caliperLine')!;
    const lineParameters = { ...makeDefaultParameters(line), startX: 'inactive', endY: null };
    expect(configurationIssues(line, lineParameters).length).toBeGreaterThan(0);
    expect(configurationIssues(line, lineParameters, { connectedInputPorts: ['searchLine'] })).toEqual([]);

    const circle = fallbackCatalogRaw.find(item => item.type === 'measure.caliperCircle')!;
    const circleParameters = { ...makeDefaultParameters(circle), centerX: 'inactive', radius: 0 };
    expect(configurationIssues(circle, circleParameters).length).toBeGreaterThan(0);
    expect(configurationIssues(circle, circleParameters, { connectedInputPorts: ['circle'] })).toEqual([]);

    expect(configurationIssues(circle, { ...circleParameters, fitOutlierThreshold: -1 }, { connectedInputPorts: ['circle'] }).length).toBeGreaterThan(0);
  });

  it('preflights a local search line shorter than the measurement minimum', () => {
    const line = fallbackCatalogRaw.find(item => item.type === 'measure.caliperLine')!;
    const parameters = { ...makeDefaultParameters(line), endX: 121.99 };

    expect(configurationIssues(line, parameters).join(' ')).toContain('搜索线长度至少需要 2 px');
    expect(configurationIssues(line, { ...parameters, endX: 122 })).toEqual([]);
    expect(configurationIssues(line, parameters, { connectedInputPorts: ['searchLine'] })).toEqual([]);
  });

  it('rejects a zero-span circle arc while preserving wraparound and full-circle arcs', () => {
    const circle = fallbackCatalogRaw.find(item => item.type === 'measure.caliperCircle')!;
    const defaults = makeDefaultParameters(circle);

    expect(configurationIssues(circle, { ...defaults, angleStartDeg: 30, angleEndDeg: 30 }).join(' ')).toContain('单条径向卡尺，无法拟合圆');
    expect(configurationIssues(circle, { ...defaults, angleStartDeg: 350, angleEndDeg: 10 })).toEqual([]);
    expect(configurationIssues(circle, { ...defaults, angleStartDeg: 0, angleEndDeg: 360 })).toEqual([]);
  });

  it('preflights caliper quality gates against the number of profiles that can be sampled', () => {
    const line = fallbackCatalogRaw.find(item => item.type === 'measure.caliperLine')!;
    const lineDefaults = makeDefaultParameters(line);
    expect(configurationIssues(line, { ...lineDefaults, caliperCount: 5, minValidCalipers: 6 }).join(' '))
      .toContain('最少有效卡尺数超过卡尺总数');
    expect(configurationIssues(line, { ...lineDefaults, caliperCount: 5, minValidCalipers: 5 })).toEqual([]);

    const circle = fallbackCatalogRaw.find(item => item.type === 'measure.caliperCircle')!;
    const circleDefaults = makeDefaultParameters(circle);
    expect(configurationIssues(circle, { ...circleDefaults, caliperCount: 5, minValidCalipers: 6 }).join(' '))
      .toContain('超过当前设置可生成的 5 条卡尺');
    expect(configurationIssues(circle, { ...circleDefaults, samplingMode: 'step', angleStartDeg: 10,
      angleEndDeg: 30, angleStepDeg: 15 }).join(' ')).toContain('只会生成 2 条径向卡尺');
    expect(configurationIssues(circle, { ...circleDefaults, samplingMode: 'step', angleStartDeg: 0,
      angleEndDeg: 90, angleStepDeg: 30, minValidCalipers: 5 }).join(' '))
      .toContain('超过当前设置可生成的 4 条卡尺');
    const legacyParameters: Record<string, unknown> = { ...circleDefaults, angleStartDeg: -180, angleEndDeg: 180, angleStepDeg: 5 };
    delete legacyParameters.samplingMode;
    const legacyCircle = configurationParameters(legacyParameters, circle);
    expect(configurationIssues(circle, legacyCircle)).toEqual([]);
  });
});
