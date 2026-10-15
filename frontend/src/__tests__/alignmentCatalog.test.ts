import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { alignmentRegionDemo } from '../demos';
import { localizeCatalog } from '../i18n/catalog';
import { nodeCategory, searchTools } from '../toolboxModel';
import { validateWorkflow } from '../validation';

describe('alignment and per-object region tools', () => {
  it('publishes all eight tools with Chinese names, parameters and descriptions', () => {
    const catalog = localizeCatalog(fallbackCatalogRaw, 'zh');
    for (const type of ['image.register', 'image.warpAffine', 'region.morphology', 'region.properties',
      'region.toDetections', 'region.transform', 'measure.lineSpacing', 'calibration.detectBoard']) {
      const item = catalog.find(entry => entry.type === type)!;
      expect(item).toBeDefined();
      expect(item.displayName).toMatch(/[\u4e00-\u9fff]/);
      expect(item.description).toMatch(/[\u4e00-\u9fff]/);
      expect(nodeCategory(type)).not.toBe('plugin');
      for (const parameter of item.parameters) {
        expect(parameter.label).toMatch(/[\u4e00-\u9fff]/);
        for (const option of parameter.options ?? []) expect(option.label).toMatch(/[\u4e00-\u9fff]/);
      }
    }
    expect(searchTools(catalog, '图像配准')[0].type).toBe('image.register');
    expect(fallbackCatalogRaw.find(item => item.type === 'calibration.cameraIntrinsics')!.inputs)
      .toContainEqual({ name: 'calibrationDataJson', dataType: 'String', required: false });
  });

  it('validates the shipped example with a found branch guarding the optional transform', () => {
    const graph = alignmentRegionDemo();
    const nodes = graph.nodes.map(node => {
      const catalog = fallbackCatalogRaw.find(item => item.type === node.data.typeKey)!;
      return { ...node, data: { ...node.data, inputs: catalog.inputs, outputs: catalog.outputs,
        parameters: { ...makeDefaultParameters(catalog), ...node.data.parameters as Record<string, unknown> } } };
    });
    expect(validateWorkflow(nodes, fallbackCatalogRaw, graph.edges)).toEqual([]);
    expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'gate', sourceHandle: 'true', target: 'warp' }));
    expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'gate', sourceHandle: 'false', target: 'missing' }));
  });
});
