import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw } from '../catalog';
import { localizeCatalog } from '../i18n/catalog';
import { searchTools } from '../toolboxModel';
import { isParameterVisible, validateParameterValues } from '../validation';

const catalog = localizeCatalog(fallbackCatalogRaw, 'zh');

describe('merged node entries', () => {
  it('shows only the canonical entries; merged legacy types no longer exist in the catalog', () => {
    const allTools = searchTools(catalog, '');
    const visibleTypes = allTools.map((item) => item.type);
    expect(visibleTypes).toContain('image.filter');
    expect(visibleTypes).toContain('image.morphology');
    expect(visibleTypes).toContain('text.validate');
    expect(visibleTypes).toContain('image.threshold');
    expect(visibleTypes).toContain('image.channel');
    expect(visibleTypes).toContain('region.fromRange');
    expect(visibleTypes).toContain('measure.blobs');
    expect(visibleTypes).not.toContain('image.blur.gaussian');
    expect(visibleTypes).not.toContain('image.blur.median');
    expect(visibleTypes).not.toContain('image.topHat');
    expect(visibleTypes).not.toContain('text.match');
    expect(visibleTypes).not.toContain('text.regex');
    for (const type of ['image.threshold.adaptive', 'image.threshold.otsu', 'image.grayscale', 'region.fromThreshold', 'region.fromColorRange', 'measure.blob']) {
      expect(visibleTypes).not.toContain(type);
    }
    // 13 个兼容节点已全部物理合并：目录里不再有旧类型，旧流程靠加载期迁移升级。
    for (const type of ['text.match', 'text.regex', 'text.validate', 'measure.blob', 'region.fromColorRange']) {
      if (type === 'text.validate') continue;
      expect(catalog.some((item) => item.type === type), type).toBe(false);
    }
    expect(searchTools(catalog, '中值滤波')[0].type).toBe('image.filter');
    expect(searchTools(catalog, '文本校验')[0].type).toBe('text.validate');
    expect(searchTools(catalog, 'Otsu')[0].type).toBe('image.threshold');
    expect(searchTools(catalog, '灰度范围')[0].type).toBe('region.fromRange');
  });

  it('shows and validates only the parameters for the selected mode', () => {
    const filter = catalog.find((item) => item.type === 'image.filter')!;
    const sigmaX = filter.parameters.find((parameter) => parameter.name === 'sigmaX')!;
    expect(isParameterVisible(sigmaX, filter.parameters, { method: 'gaussian' })).toBe(true);
    expect(isParameterVisible(sigmaX, filter.parameters, { method: 'median' })).toBe(false);
    expect(validateParameterValues(filter.parameters, { method: 'median', kernelSize: 5, sigmaX: 'inactive' }))
      .not.toContainEqual(expect.objectContaining({ name: 'sigmaX' }));

    const text = catalog.find((item) => item.type === 'text.validate')!;
    const pattern = text.parameters.find((parameter) => parameter.name === 'pattern')!;
    const expected = text.parameters.find((parameter) => parameter.name === 'expected')!;
    expect(isParameterVisible(pattern, text.parameters, { mode: 'regex' })).toBe(true);
    expect(isParameterVisible(expected, text.parameters, { mode: 'regex' })).toBe(false);
    expect(isParameterVisible(pattern, text.parameters, { mode: 'contains' })).toBe(false);

    const threshold = catalog.find((item) => item.type === 'image.threshold')!;
    const manualCutoff = threshold.parameters.find((parameter) => parameter.name === 'threshold')!;
    const blockSize = threshold.parameters.find((parameter) => parameter.name === 'blockSize')!;
    expect(isParameterVisible(manualCutoff, threshold.parameters, { mode: 'manual' })).toBe(true);
    expect(isParameterVisible(manualCutoff, threshold.parameters, { mode: 'otsu' })).toBe(false);
    expect(isParameterVisible(blockSize, threshold.parameters, { mode: 'adaptive' })).toBe(true);

    const range = catalog.find((item) => item.type === 'region.fromRange')!;
    const minGray = range.parameters.find((parameter) => parameter.name === 'minGray')!;
    const min1 = range.parameters.find((parameter) => parameter.name === 'min1')!;
    expect(isParameterVisible(minGray, range.parameters, { mode: 'gray' })).toBe(true);
    expect(isParameterVisible(minGray, range.parameters, { mode: 'color' })).toBe(false);
    expect(isParameterVisible(min1, range.parameters, { mode: 'color' })).toBe(true);

    const blobs = catalog.find((item) => item.type === 'measure.blobs')!;
    const maxArea = blobs.parameters.find((parameter) => parameter.name === 'maxArea')!;
    expect(isParameterVisible(maxArea, blobs.parameters, { mode: 'all' })).toBe(true);
    expect(isParameterVisible(maxArea, blobs.parameters, { mode: 'largest' })).toBe(false);
  });
});
