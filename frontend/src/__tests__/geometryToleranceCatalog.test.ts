import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { circularToleranceDemo, edgeToleranceDemo } from '../demos';
import { localizeCatalog } from '../i18n/catalog';
import { nodeCategory, searchTools } from '../toolboxModel';
import { validateWorkflow } from '../validation';

describe('geometry tolerance tools', () => {
  it('provides eight localized tools with Chinese parameters and descriptions', () => {
    const catalog = localizeCatalog(fallbackCatalogRaw, 'zh');
    for (const type of ['contour.fromPoints', 'measure.wallThickness', 'measure.concentricity', 'measure.roundness',
      'measure.straightness', 'measure.contourDeviation', 'measure.parallelism', 'measure.perpendicularity']) {
      const item = catalog.find(entry => entry.type === type)!;
      expect(item).toBeDefined(); expect(item.displayName).toMatch(/[\u4e00-\u9fff]/);
      expect(item.description).toMatch(/[\u4e00-\u9fff]/); expect(nodeCategory(type)).not.toBe('plugin');
      for (const parameter of item.parameters) expect(parameter.label).toMatch(/[\u4e00-\u9fff]/);
    }
    expect(searchTools(catalog, '圆环壁厚')[0].type).toBe('measure.wallThickness');
  });
  it.each([['circular', circularToleranceDemo], ['edges', edgeToleranceDemo]] as const)('validates the %s template and its missing-fit branch', (_name, demo) => {
    const graph = demo();
    const nodes = graph.nodes.map(node => {
      const item = fallbackCatalogRaw.find(entry => entry.type === node.data.typeKey)!;
      return { ...node, data: { ...node.data, inputs: item.inputs, outputs: item.outputs,
        parameters: { ...makeDefaultParameters(item), ...node.data.parameters as Record<string, unknown> } } };
    });
    expect(validateWorkflow(nodes, fallbackCatalogRaw, graph.edges)).toEqual([]);
    expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'gateFit', sourceHandle: 'false', target: 'missingFit' }));
  });
});
