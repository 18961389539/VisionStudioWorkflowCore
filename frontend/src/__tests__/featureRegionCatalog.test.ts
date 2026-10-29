import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { smallFeatureDemo, regionShapeDemo } from '../demos';
import { localizeCatalog } from '../i18n/catalog';
import { nodeCategory, searchTools } from '../toolboxModel';
import { validateWorkflow } from '../validation';

describe('small features and region shapes', () => {
  it('provides five localized tools, parameters and enum choices', () => {
    const catalog = localizeCatalog(fallbackCatalogRaw, 'zh');
    for (const type of ['measure.grayHistogram',
      'region.fromRoi', 'region.convexHull', 'region.boundary', 'region.skeleton']) {
      const item = catalog.find(entry => entry.type === type)!;
      expect(item).toBeDefined(); expect(item.displayName).toMatch(/[\u4e00-\u9fff]/);
      expect(item.description).toMatch(/[\u4e00-\u9fff]/); expect(nodeCategory(type)).not.toBe('plugin');
      // internal 参数（ROI 对象/画布资产）不进配置面板，不参与 UI 文案覆盖率。
      for (const parameter of item.parameters.filter((entry) => !entry.internal)) {
        expect(parameter.label).toMatch(/[\u4e00-\u9fff]/);
        for (const option of parameter.options ?? []) expect(option.label).toMatch(/[\u4e00-\u9fff]/);
      }
    }
    expect(searchTools(catalog, '区域骨架')[0].type).toBe('region.skeleton');
  });
  it.each([['features', smallFeatureDemo], ['regions', regionShapeDemo]] as const)('validates the %s template', (_name, demo) => {
    const graph = demo();
    const nodes = graph.nodes.map(node => {
      const item = fallbackCatalogRaw.find(entry => entry.type === node.data.typeKey)!;
      return { ...node, data: { ...node.data, inputs: item.inputs, outputs: item.outputs,
        parameters: { ...makeDefaultParameters(item), ...node.data.parameters as Record<string, unknown> } } };
    });
    expect(validateWorkflow(nodes, fallbackCatalogRaw, graph.edges)).toEqual([]);
  });
});
