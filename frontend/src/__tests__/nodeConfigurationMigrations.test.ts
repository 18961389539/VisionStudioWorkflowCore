import { describe, expect, it } from 'vitest';
import { configurationParameters, hydrateNodeParameters, roiConfigurationIssue } from '../nodeConfigurationState';
import type { NodeCatalogItem } from '../types';

/**
 * 旧配方参数迁移与 ROI 校验的边界用例（2026-11-13）。
 * 这些分支决定"打开老流程时看到什么"，此前主要靠组件集成测试顺带覆盖；
 * 拆分 NodeConfigurationDialog 后单独锁住，避免迁移规则回归。
 */
const catalog = (type: string, parameters: NodeCatalogItem['parameters'] = []): NodeCatalogItem => ({
  type, displayName: type, category: 'test', inputs: [], outputs: [], parameters
});

describe('configurationParameters legacy migrations', () => {
  it('exposes legacy crop scalars as a rectangle ROI without dropping the original fields', () => {
    const merged = configurationParameters({ x: 5, y: 6, width: 7, height: 8 }, catalog('image.crop'));
    expect(merged.roi).toEqual({ type: 'Rectangle', x: 5, y: 6, width: 7, height: 8 });
    expect(merged).toMatchObject({ x: 5, y: 6, width: 7, height: 8 });
    // 零尺寸仍会映射出来，由 ROI 校验报"正尺寸"；非数值才不回填。
    expect(configurationParameters({ x: 5, y: 6, width: 0, height: 8 }, catalog('image.crop')).roi)
      .toMatchObject({ width: 0 });
    expect(configurationParameters({ x: '5', y: 6, width: 7, height: 8 }, catalog('image.crop')).roi).toBeUndefined();
  });

  it('keeps historical shape nodes in legacy mode unless a trained model exists', () => {
    expect(configurationParameters({}, catalog('match.shape')).modelMode).toBe('legacy');
    expect(configurationParameters({ trainedModelData: 'abc' }, catalog('match.shape')).modelMode).toBeUndefined();
    expect(configurationParameters({ modelMode: 'trained' }, catalog('match.shape')).modelMode).toBe('trained');
  });

  it('restores training angle coverage from shape-v1/v2 configs', () => {
    expect(configurationParameters({ trainedModelConfig: 'shape-v2|x', angleRangeDeg: 45 }, catalog('match.shape')).trainingAngleRangeDeg)
      .toBe(45);
    expect(configurationParameters({ trainedModelConfig: 'shape-v1|x' }, catalog('match.shape')).trainingAngleRangeDeg)
      .toBe(180);
    expect(configurationParameters({ trainedModelConfig: 'other|x', angleRangeDeg: 45 }, catalog('match.shape')).trainingAngleRangeDeg)
      .toBeUndefined();
  });

  it('migrates caliper sampling, refine, difference, retry and storage policies', () => {
    expect(configurationParameters({ angleStepDeg: 2 }, catalog('measure.caliperCircle')))
      .toMatchObject({ samplingMode: 'step', maxFitOutliers: 0 });
    expect(configurationParameters({}, catalog('measure.caliperCircle')).samplingMode).toBeUndefined();
    expect(configurationParameters({}, catalog('measure.caliperLine')).maxFitOutliers).toBe(0);
    expect(configurationParameters({ refine: 'subpixel', edgeThreshold: 10 }, catalog('measure.caliper')))
      .toMatchObject({ refine: 'subpixel' });
    expect(configurationParameters({ edgeThreshold: 10 }, catalog('measure.caliper')).refine).toBe('none');
    expect(configurationParameters({ diffMode: 'abs' }, catalog('image.difference')).mode).toBe('abs');
    expect(configurationParameters({ retryPolicy: 'backoff' }, catalog('output.httpRequest')).retries).toBe(2);
    expect(configurationParameters({ retryPolicy: 'once' }, catalog('output.httpRequest')).retries).toBe(1);
    expect(configurationParameters({ retryPolicy: 'never' }, catalog('output.httpRequest')).retries).toBe(0);
  });

  it('drops debug-only metrology parameters and migrates region/save policies', () => {
    expect(configurationParameters({ debugImagePreviewOnly: true }, catalog('measure.metrology')))
      .not.toHaveProperty('debugImagePreviewOnly');
    expect(configurationParameters({ requireNonEmpty: 'true' }, catalog('measure.regionIntensity')))
      .toMatchObject({ emptyPolicy: 'fail' });
    expect(configurationParameters({ requireNonEmpty: false }, catalog('measure.regionIntensity')))
      .toMatchObject({ emptyPolicy: 'zeros' });
    expect(configurationParameters({ overwrite: true }, catalog('output.saveImage')))
      .toMatchObject({ namingMode: 'fixed', conflictPolicy: 'overwrite' });
    expect(configurationParameters({}, catalog('output.saveImage')))
      .toMatchObject({ namingMode: 'fixed', conflictPolicy: 'fail' });
    expect(configurationParameters({ overwrite: false }, catalog('output.exportResult')).conflictPolicy).toBe('fail');
    expect(configurationParameters({ overwrite: true }, catalog('output.exportResult')).conflictPolicy).toBe('overwrite');
  });

  it('keeps explicit parameters and excludes internal catalog defaults', () => {
    const item = catalog('image.threshold', [
      { name: 'threshold', label: '阈值', type: 'number', defaultValue: 128 },
      { name: '__roi', label: 'ROI', type: 'text', internal: true, defaultValue: '{"x":1}' }
    ]);
    const merged = configurationParameters({ threshold: 200 }, item);
    expect(merged.threshold).toBe(200);
    expect(merged).not.toHaveProperty('__roi');
  });
});

describe('hydrateNodeParameters', () => {
  it('applies the same legacy migrations when the catalog is refreshed', () => {
    const shape = catalog('match.shape');
    expect(hydrateNodeParameters({}, shape, {})).toMatchObject({ modelMode: 'legacy' });
    expect(hydrateNodeParameters({ trainedModelData: 'x' }, shape, {})).not.toHaveProperty('modelMode');
    expect(hydrateNodeParameters({ trainedModelConfig: 'shape-v2|x', angleRangeDeg: 30 }, shape, {}).trainingAngleRangeDeg)
      .toBe(30);
    expect(hydrateNodeParameters({ angleStepDeg: 2 }, catalog('measure.caliperCircle'), {}))
      .toMatchObject({ samplingMode: 'step', maxFitOutliers: 0 });
    expect(hydrateNodeParameters({}, catalog('measure.caliperLine'), {}).maxFitOutliers).toBe(0);
    expect(hydrateNodeParameters({ edgeThreshold: 9 }, catalog('measure.caliper'), {}).refine).toBe('none');
    expect(hydrateNodeParameters({ diffMode: 'abs' }, catalog('image.difference'), {}).mode).toBe('abs');
    expect(hydrateNodeParameters({ retryPolicy: 'once' }, catalog('output.httpRequest'), {}).retries).toBe(1);
    expect(hydrateNodeParameters({ debugImagePreviewOnly: true }, catalog('measure.metrology'), {}))
      .not.toHaveProperty('debugImagePreviewOnly');
    expect(hydrateNodeParameters({ requireNonEmpty: 'true' }, catalog('measure.regionIntensity'), {}).emptyPolicy)
      .toBe('fail');
    expect(hydrateNodeParameters({ overwrite: false }, catalog('output.saveImage'), {}).conflictPolicy).toBe('fail');
    expect(hydrateNodeParameters({ overwrite: true }, catalog('output.exportResult'), {}).conflictPolicy).toBe('overwrite');
  });
});

describe('roiConfigurationIssue', () => {
  it('rejects non-object and unknown ROI types', () => {
    expect(roiConfigurationIssue(null, 'ROI', 10)).toContain('结构无效');
    expect(roiConfigurationIssue([], 'ROI', 10)).toContain('结构无效');
    expect(roiConfigurationIssue({ type: 'hexagon' }, 'ROI', 10)).toContain('类型无效');
  });

  it('validates rectangle bounds and dimensions', () => {
    expect(roiConfigurationIssue({ type: 'Rectangle', x: 0, y: 0, width: 10, height: 10 }, 'ROI', 10)).toBeUndefined();
    expect(roiConfigurationIssue({ type: 'Rectangle', x: 0, y: 0, width: 0, height: 10 }, 'ROI', 10)).toContain('正尺寸');
    expect(roiConfigurationIssue({ type: 'Rectangle', x: 0, y: 0, width: Number.NaN, height: 10 }, 'ROI', 10)).toContain('正尺寸');
    expect(roiConfigurationIssue({ type: 'Rectangle', x: 9_999_999, y: 0, width: 10, height: 10 }, 'ROI', 10))
      .toContain('范围');
  });

  it('validates circle and polygon geometry', () => {
    expect(roiConfigurationIssue({ type: 'Circle', x: 5, y: 5, radius: 2 }, 'ROI', 10)).toBeUndefined();
    expect(roiConfigurationIssue({ type: 'Circle', x: 5, y: 5, radius: 0 }, 'ROI', 10)).toContain('正半径');
    expect(roiConfigurationIssue({ type: 'Polygon', points: 'nope' }, 'ROI', 10)).toContain('三个有效顶点');
    expect(roiConfigurationIssue({ type: 'Polygon', points: [{ x: 0, y: 0 }, { x: 1, y: 1 }] }, 'ROI', 10))
      .toContain('三个有效顶点');
    expect(roiConfigurationIssue({ type: 'Polygon', points: [{ x: 0, y: 0 }, { x: 1, y: 1 }, { x: 2, y: 2 }] }, 'ROI', 10))
      .toContain('非零面积');
    expect(roiConfigurationIssue({ type: 'Polygon', points: [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 0, y: 10 }] }, 'ROI', 10))
      .toBeUndefined();
  });

  it('validates brush strokes and requires at least one add stroke', () => {
    const point = [{ x: 1, y: 1 }];
    expect(roiConfigurationIssue({ type: 'Brush', strokes: [] }, 'ROI', 10)).toContain('1..10000');
    expect(roiConfigurationIssue({ type: 'Brush', strokes: [{ mode: 'erase', width: 2, points: point }] }, 'ROI', 10))
      .toContain('至少一笔添加');
    expect(roiConfigurationIssue({ type: 'Brush', strokes: [{ mode: 'add', width: 0, points: point }] }, 'ROI', 10))
      .toContain('笔宽');
    expect(roiConfigurationIssue({ type: 'Brush', strokes: [{ mode: 'add', width: 2, points: [] }] }, 'ROI', 10))
      .toContain('至少一笔添加');
    expect(roiConfigurationIssue({ type: 'Brush', strokes: [{ mode: 'add', width: 2, points: point }] }, 'ROI', 10))
      .toBeUndefined();
  });
});
