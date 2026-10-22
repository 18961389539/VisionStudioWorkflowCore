import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { colorEnhancementDemo, textValidationDemo } from '../demos';
import { localizeCatalog } from '../i18n/catalog';
import { nodeCategory, searchTools } from '../toolboxModel';
import { validateWorkflow } from '../validation';

describe('color, enhancement and text tools', () => {
  it('provides localized merged tools with translated parameters and options', () => {
    const translated = localizeCatalog(fallbackCatalogRaw, 'zh');
    for (const type of ['image.filter', 'image.colorConvert', 'region.fromColorRange', 'measure.colorDifference', 'image.contrastNormalize',
      'image.illuminationNormalize', 'image.sharpen', 'text.validate']) {
      const item = translated.find(entry => entry.type === type)!;
      expect(item).toBeDefined(); expect(item.displayName).toMatch(/[\u4e00-\u9fff]/);
      expect(item.description).toMatch(/[\u4e00-\u9fff]/); expect(nodeCategory(type)).not.toBe('plugin');
      // internal 参数（ROI 对象/画布资产）不进配置面板，不参与 UI 文案覆盖率。
      for (const parameter of item.parameters.filter((entry) => !entry.internal)) {
        expect(parameter.label).toMatch(/[\u4e00-\u9fff]/);
        for (const option of parameter.options ?? []) expect(option.label).toMatch(/[\u4e00-\u9fff]/);
      }
    }
    expect(translated.find(entry => entry.type === 'text.match')?.displayName).toBe('文本匹配');
    expect(translated.find(entry => entry.type === 'text.regex')?.displayName).toBe('正则校验');
    expect(searchTools(translated, '正则校验')[0].type).toBe('text.validate');
  });

  it.each([['colors', colorEnhancementDemo], ['text', textValidationDemo]] as const)('validates the %s template', (_name, demo) => {
    const graph = demo();
    const nodes = graph.nodes.map(node => {
      const catalog = fallbackCatalogRaw.find(item => item.type === node.data.typeKey)!;
      return { ...node, data: { ...node.data, inputs: catalog.inputs, outputs: catalog.outputs,
        parameters: { ...makeDefaultParameters(catalog), ...node.data.parameters as Record<string, unknown> } } };
    });
    expect(validateWorkflow(nodes, fallbackCatalogRaw, graph.edges)).toEqual([]);
    if (_name === 'colors') {
      for (const target of ['channels', 'range', 'difference'])
        expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'source', sourceHandle: 'image', target, targetHandle: 'image' }));
    }
  });
});
