import { describe, expect, it } from 'vitest';
import catalog from '../generated/catalog.generated.json';
import { localizeCatalog } from '../i18n/catalog';
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
  'region.fromThreshold',
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
});
