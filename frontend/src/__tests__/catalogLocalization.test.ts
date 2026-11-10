import { describe, expect, it } from 'vitest';
import catalog from '../generated/catalog.generated.json';
import { localizeCatalog } from '../i18n/catalog';
import { optionLabels, parameterLabels, variantLabels } from '../i18n/catalogLabels';
import type { NodeCatalogItem } from '../types';

const cjk = /[\u4e00-\u9fff]/;

// 这 18 个节点的目录文本曾经直接用中文编写：英文界面会原样显示中文，而中文界面又因为
// 翻译表按英文原文索引而无法覆盖。翻译回英文源后，这些用例锁定“中文界面仍是中文”，
// 防止后续改文案时静默丢掉中文。
const previouslyChineseAuthored = [
  'device.writeVisionResult',
  'geometry.intersections',
  'measure.pointLineDistance',
  'measure.fitRectangle',
  'output.httpRequest',
  'output.saveImage',
  'region.fromRange',
  'jlvision.worldPlane',
  'jlvision.dxf',
  'jlvision.featureExtract',
  'jlvision.matrixOp',
  'jlvision.contourFeature',
  'jlvision.imageOp',
  'jlvision.restoration',
  'jlvision.surface',
  'jlvision.regionAnalysis',
  'jlvision.segmentation'
];

const source = catalog.items as unknown as NodeCatalogItem[];
const localized = new Map(localizeCatalog(source, 'zh').map((item) => [item.type, item]));

describe('catalog localization for previously Chinese-authored nodes', () => {
  it('keeps every node name and description Chinese', () => {
    for (const type of previouslyChineseAuthored) {
      const item = localized.get(type);
      expect(item, type).toBeTruthy();
      expect(cjk.test(item!.displayName), `${type} displayName`).toBe(true);
      expect(cjk.test(item!.description ?? ''), `${type} description`).toBe(true);
    }
  });

  it('keeps every visible parameter label Chinese', () => {
    const missing: string[] = [];
    for (const type of previouslyChineseAuthored) {
      const item = localized.get(type)!;
      for (const parameter of item.parameters) {
        if (parameter.internal) continue;
        if (!cjk.test(parameter.label)) missing.push(`${type}.${parameter.name} label='${parameter.label}'`);
      }
    }
    expect(missing).toEqual([]);
  });

  it('translates every built-in option label', () => {
    // 高级 JLVision 算子选项曾长期回退英文（中文界面显示 "Add images (add)" 等）。
    // 这里对全部节点的枚举选项做覆盖率检查：新增节点/算子忘记补中文会直接失败。
    const missing: string[] = [];
    for (const item of localized.values()) {
      for (const parameter of item.parameters) {
        for (const option of parameter.options ?? []) {
          if (!cjk.test(option.label)) missing.push(`${item.type}.${parameter.name}=${option.label}`);
        }
      }
    }
    expect(missing).toEqual([]);
  });

  it('translates every visible parameter label for every node', () => {
    const missing: string[] = [];
    for (const item of localized.values()) {
      for (const parameter of item.parameters) {
        if (parameter.internal) continue;
        if (!cjk.test(parameter.label)) missing.push(`${item.type}.${parameter.name} label='${parameter.label}'`);
      }
    }
    expect(missing).toEqual([]);
  });

  it('translates every node name, node description and visible parameter description', () => {
    // Labels/options were covered first; descriptions are what a Chinese user reads in the
    // node dialog and the help popovers, so fall back to English is a visible gap there too.
    const missing: string[] = [];
    for (const item of localized.values()) {
      if (!cjk.test(item.displayName)) missing.push(`${item.type} displayName`);
      if (item.description && !cjk.test(item.description)) missing.push(`${item.type} description`);
      for (const parameter of item.parameters) {
        if (parameter.internal) continue;
        if (parameter.description && !cjk.test(parameter.description)) missing.push(`${item.type}.${parameter.name} description`);
      }
    }
    expect(missing).toEqual([]);
  });

  it('translates every category', () => {
    const missing = [...localized.values()].filter((item) => !cjk.test(item.category)).map((item) => `${item.type} category='${item.category}'`);
    expect(missing).toEqual([]);
  });

  it('keeps Chinese labels when the English source text is renamed', () => {
    const corner = source.find((item) => item.type === 'feature.corner')!;
    const renamed: NodeCatalogItem = {
      ...corner,
      parameters: corner.parameters.map((parameter) => parameter.name === 'method'
        ? {
            ...parameter,
            label: 'Detector (renamed in English)',
            options: parameter.options?.map((option) => option.value === 'fast'
              ? { ...option, label: 'FAST (renamed in English)' }
              : option)
          }
        : parameter)
    };

    const [localizedCorner] = localizeCatalog([renamed], 'zh');
    const method = localizedCorner.parameters.find((parameter) => parameter.name === 'method')!;
    expect(method.label).toBe('检测器');
    expect(method.options?.find((option) => option.value === 'fast')?.label).toBe('FAST（阈值检测）');
  });

  it('has no dangling semantic label keys', () => {
    const parameters = new Set<string>();
    const options = new Set<string>();
    const variants = new Set<string>();
    for (const item of source) {
      for (const parameter of item.parameters) {
        parameters.add(`${item.type}.${parameter.name}`);
        for (const option of parameter.options ?? []) options.add(`${item.type}.${parameter.name}.${option.value}`);
        for (const key of Object.keys(parameter.variants ?? {})) variants.add(`${item.type}.${parameter.name}.${key}`);
      }
    }

    expect(Object.keys(parameterLabels).filter((key) => !parameters.has(key))).toEqual([]);
    const danglingOptions: string[] = [];
    for (const [type, byParameter] of Object.entries(optionLabels))
      for (const [parameter, byValue] of Object.entries(byParameter))
        for (const value of Object.keys(byValue))
          if (!options.has(`${type}.${parameter}.${value}`)) danglingOptions.push(`${type}.${parameter}.${value}`);
    expect(danglingOptions).toEqual([]);
    const danglingVariants: string[] = [];
    for (const [type, byParameter] of Object.entries(variantLabels))
      for (const [parameter, byKey] of Object.entries(byParameter))
        for (const key of Object.keys(byKey))
          if (!variants.has(`${type}.${parameter}.${key}`)) danglingVariants.push(`${type}.${parameter}.${key}`);
    expect(danglingVariants).toEqual([]);
  });
});
