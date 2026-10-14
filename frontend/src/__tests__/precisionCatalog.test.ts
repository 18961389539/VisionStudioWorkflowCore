import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { precisionDemo } from '../demos';
import { localizeCatalog } from '../i18n/catalog';
import { nodeCategory } from '../toolboxModel';
import { validateWorkflow } from '../validation';

describe('precision workflow catalog', () => {
  it('provides all precision nodes in the offline catalog with Chinese names', () => {
    const translated = localizeCatalog(fallbackCatalogRaw, 'zh');
    for (const type of ['contour.extract', 'contour.filter', 'contour.properties', 'feature.edgeSubpixel',
      'measure.caliperLine', 'measure.caliperCircle', 'measure.fitEllipse', 'match.shape', 'roi.transform']) {
      const item = translated.find(entry => entry.type === type);
      expect(item).toBeDefined();
      expect(item!.displayName).toMatch(/[\u4e00-\u9fff]/);
      expect(nodeCategory(type)).not.toBe('plugin');
    }
  });

  it('can validate the shipped precision template including the image and dynamic ROI connections', () => {
    const graph = precisionDemo();
    const nodes = graph.nodes.map(node => {
      const catalog = fallbackCatalogRaw.find(item => item.type === node.data.typeKey)!;
      return { ...node, data: { ...node.data, inputs: catalog.inputs, outputs: catalog.outputs,
        parameters: { ...makeDefaultParameters(catalog), ...node.data.parameters as Record<string, unknown> } } };
    });
    expect(validateWorkflow(nodes, fallbackCatalogRaw, graph.edges)).toEqual([]);
    expect(graph.edges.find(edge => edge.id === 'd4')?.source).toBe('locate');
  });
});
