import { describe, expect, it } from 'vitest';
import {
  parameterVisibility,
  parseNodeDuration,
  type ParameterVisibility
} from '../components/nodeConfigurationDialogModel';
import { caliperCircleQuickParameterNames } from '../components/CaliperCircleQuickSettings';
import { caliperEdgeQuickParameterNames } from '../components/CaliperEdgeQuickSettings';

/**
 * 拆分自 NodeConfigurationDialog 的参数可见性规则（2026-11-13）。
 * 这些分支决定"表单里显示哪些控件"，此前只在组件集成测试里被顺带覆盖；
 * 抽成纯函数后直接按节点类型断言，避免拆分导致分支覆盖率下降。
 */
const base = {
  draft: {},
  connectedInputPorts: [] as string[],
  isMetrology: false,
  isCaliperCircle: false,
  isCaliperEdge: false
};

function visibility(type: string, overrides: Partial<typeof base> = {}): ParameterVisibility {
  return parameterVisibility({ ...base, type, ...overrides });
}

describe('parseNodeDuration', () => {
  it('rejects non-objects and records without samples/p95', () => {
    expect(parseNodeDuration(null)).toBeUndefined();
    expect(parseNodeDuration(42)).toBeUndefined();
    expect(parseNodeDuration({ samples: 3 })).toBeUndefined();
    expect(parseNodeDuration({ p95Ms: 12 })).toBeUndefined();
  });

  it('rejects non-finite numbers instead of showing NaN', () => {
    expect(parseNodeDuration({ samples: Number.NaN, p95Ms: 12 })).toBeUndefined();
    expect(parseNodeDuration({ samples: 3, p95Ms: Number.POSITIVE_INFINITY })).toBeUndefined();
  });

  it('keeps optional fields and defaults timeoutCount', () => {
    expect(parseNodeDuration({ samples: 20, p95Ms: 12.5 })).toEqual({
      samples: 20, p95Ms: 12.5, suggestedTimeoutMs: undefined, timeoutCount: 0, tooTight: false
    });
    expect(parseNodeDuration({ samples: 20, p95Ms: 12.5, suggestedTimeoutMs: 40, timeoutCount: 2, timeoutTooTight: true })).toEqual({
      samples: 20, p95Ms: 12.5, suggestedTimeoutMs: 40, timeoutCount: 2, tooTight: true
    });
  });
});

describe('parameterVisibility numeric conditions', () => {
  it('hides scaleStep only for a fixed-scale template search', () => {
    expect(visibility('match.template').numericHiddenParameterNames).toEqual(['scaleStepPercent']);
    expect(visibility('match.template', { draft: { scaleMinPercent: 90, scaleMaxPercent: 110 } }).numericHiddenParameterNames)
      .toEqual([]);
  });

  it('hides search angle range once an explicit angle window is set', () => {
    expect(visibility('match.template', { draft: { searchAngleEndDeg: 30 } }).numericHiddenParameterNames)
      .toContain('angleRangeDeg');
  });

  it.each([
    ['measure.regionIntensity', 'meanNominal', 'tolerance'],
    ['measure.wallThickness', 'nominalThickness', 'tolerance'],
    ['measure.lineSpacing', 'nominalDistance', 'tolerance'],
    ['defect.referenceCompare', 'variationK', 'variationWindow'],
    ['output.httpRequest', 'retries', 'retryDelayMs'],
    ['flow.try', 'maxRetries', 'retryDelayMs'],
    ['robot.executeTarget', 'maxRetries', 'retryDelayMs']
  ])('hides %s dependent field when %s is zero', (type, zeroParameter, hidden) => {
    expect(visibility(type, { draft: { [zeroParameter]: 0 } }).numericHiddenParameterNames).toContain(hidden);
    expect(visibility(type, { draft: { [zeroParameter]: 2 } }).numericHiddenParameterNames).not.toContain(hidden);
  });

  it('accepts numeric strings and falls back on invalid drafts', () => {
    expect(visibility('measure.wallThickness', { draft: { nominalThickness: '0' } }).numericHiddenParameterNames)
      .toContain('tolerance');
    // 非法数字按 fallback 0 处理，与"未配置"同义——仍然隐藏依赖字段，而不是显示一个无效控件。
    expect(visibility('measure.wallThickness', { draft: { nominalThickness: 'not-a-number' } }).numericHiddenParameterNames)
      .toContain('tolerance');
  });

  it('keeps nominalWidth only for closest-width calipers', () => {
    expect(visibility('measure.caliperWidth', { draft: { direction: 'strongest' } }).numericHiddenParameterNames)
      .toContain('nominalWidth');
    expect(visibility('measure.caliperWidth', { draft: { direction: 'closestwidth' } }).numericHiddenParameterNames)
      .not.toContain('nominalWidth');
  });

  it('hides hysteresis only when flow.if has no previous control port', () => {
    expect(visibility('flow.if').numericHiddenParameterNames).toContain('hysteresis');
    expect(visibility('flow.if', { connectedInputPorts: ['previous'] }).numericHiddenParameterNames)
      .not.toContain('hysteresis');
  });
});

describe('parameterVisibility port/editor/crop rules', () => {
  it('hides switch cases JSON in the editor', () => {
    expect(visibility('flow.switch').editorHiddenParameterNames).toEqual(['casesJson']);
    expect(visibility('flow.if').editorHiddenParameterNames).toEqual([]);
  });

  it.each([
    'inference.detection',
    'inference.segmentation',
    'inference.obb',
    'inference.instanceSegmentation',
    'inference.pose',
    'inference.anomaly'
  ])('hides crop for %s because output coordinates cannot be remapped', (type) => {
    expect(visibility(type).unsupportedCropParameterNames).toEqual(['crop']);
  });

  it('maps connected ports to the parameters they replace', () => {
    expect(visibility('coordinate.point', { connectedInputPorts: ['x', 'thetaDeg', 'unrelated'] }).portHiddenParameterNames)
      .toEqual(['x', 'thetaDeg']);
    expect(visibility('device.writeTag', { connectedInputPorts: ['value'] }).portHiddenParameterNames)
      .toEqual(['valueType', 'fallbackValue']);
    expect(visibility('text.match', { connectedInputPorts: ['text'] }).portHiddenParameterNames).toEqual(['text']);
    expect(visibility('text.validate', { connectedInputPorts: ['expected'] }).portHiddenParameterNames).toEqual(['expected']);
    expect(visibility('jlvision.matrixOp', { connectedInputPorts: ['matrixA'] }).portHiddenParameterNames).toEqual(['matrixA']);
    expect(visibility('output.saveImage', { connectedInputPorts: ['path', 'partId'] }).portHiddenParameterNames)
      .toEqual(['path', 'partId']);
  });

  it('expands httpRequest value connections into body/contentType and filters path ports', () => {
    expect(visibility('output.httpRequest', { connectedInputPorts: ['url', 'value'] }).portHiddenParameterNames)
      .toEqual(['url', 'body', 'contentType']);
  });

  it('hides width/height for PSF restoration with a connected image', () => {
    expect(visibility('jlvision.restoration', {
      draft: { operator: 'psfMotion' }, connectedInputPorts: ['image']
    }).portHiddenParameterNames).toEqual(['width', 'height']);
    expect(visibility('jlvision.restoration', {
      draft: { operator: 'median' }, connectedInputPorts: ['image']
    }).portHiddenParameterNames).toEqual([]);
  });

  it('falls back to domain quick-setting lists', () => {
    expect(visibility('measure.metrology', { isMetrology: true }).portHiddenParameterNames).toEqual(['objects']);
    expect(visibility('measure.caliperCircle', { isCaliperCircle: true }).portHiddenParameterNames)
      .toEqual([...caliperCircleQuickParameterNames]);
    expect(visibility('measure.caliperLine', { isCaliperEdge: true }).portHiddenParameterNames)
      .toEqual([...caliperEdgeQuickParameterNames]);
    expect(visibility('image.threshold').portHiddenParameterNames).toEqual([]);
  });
});
