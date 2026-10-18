import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw } from '../catalog';
import { localizeCatalog } from '../i18n/catalog';

describe('industrial barcode catalog', () => {
  it('localizes reliability settings, preprocessing options, and usage guidance', () => {
    const barcode = localizeCatalog(fallbackCatalogRaw, 'zh').find(item => item.type === 'recognition.barcode')!;

    expect(barcode).toBeDefined();
    expect(barcode.description).toMatch(/[\u4e00-\u9fff]/);
    for (const parameterName of ['preprocess', 'tryInverted', 'adaptiveBlockSize', 'adaptiveConstant', 'maxResults', 'dedupRadiusPx', 'tileSearch']) {
      const parameter = barcode.parameters.find(candidate => candidate.name === parameterName)!;
      expect(parameter).toBeDefined();
      expect(parameter.label).toMatch(/[\u4e00-\u9fff]/);
      if (parameter.options) {
        for (const option of parameter.options) expect(option.label).toMatch(/[\u4e00-\u9fff]/);
      }
    }
  });
});
