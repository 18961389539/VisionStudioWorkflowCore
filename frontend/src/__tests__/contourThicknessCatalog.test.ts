import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { contourThicknessDemo } from '../demos';
import { localizeCatalog } from '../i18n/catalog';
import { configurationIssues } from '../nodeConfigurationState';
import { searchTools } from '../toolboxModel';
import { validateWorkflow } from '../validation';

const raw = fallbackCatalogRaw.find(item => item.type === 'measure.contourThickness')!;
const defaults = makeDefaultParameters(raw);

describe('real contour thickness', () => {
  it('provides localized modes, inputs, measurements and help', () => {
    const catalog = localizeCatalog(fallbackCatalogRaw, 'zh');
    const item = catalog.find(entry => entry.type === raw.type)!;
    expect(item.displayName).toBe('真实轮廓壁厚');
    expect(searchTools(catalog, '最薄')[0].type).toBe(raw.type);
    expect(item.description).toContain('法向');
    expect(item.description).toContain('弧长加权');
    for (const parameter of item.parameters) {
      expect(parameter.label).toMatch(/[\u4e00-\u9fff]/);
      for (const option of parameter.options ?? []) expect(option.label).toMatch(/[\u4e00-\u9fff]/);
    }
    expect(item.inputs.find(port => port.name === 'innerContours')?.label).toBe('内轮廓');
    for (const name of ['minThickness', 'meanThickness', 'maxThickness', 'withinTolerance', 'minLine'])
      expect(item.outputs.find(port => port.name === name)?.required).toBe(false);
  });

  it('rejects inverted tolerance bounds and fractional sample budgets', () => {
    expect(configurationIssues(raw, { ...defaults, minThickness: 20, maxThickness: 10 })).toContain('最大合格壁厚必须为 0（不限）或不小于最小合格壁厚。');
    expect(configurationIssues(raw, { ...defaults, maxSamplesPerBoundary: 10.5 })).toContain('每侧最大采样数必须为整数。');
    expect(configurationIssues(raw, { ...defaults, minThickness: 20, maxThickness: 0 })).toEqual([]);
  });

  it('ships an image-based extraction template guarding absent measurements', () => {
    const graph = contourThicknessDemo();
    const nodes = graph.nodes.map(node => {
      const item = fallbackCatalogRaw.find(entry => entry.type === node.data.typeKey)!;
      return { ...node, data: { ...node.data, inputs: item.inputs, outputs: item.outputs,
        parameters: { ...makeDefaultParameters(item), ...node.data.parameters as Record<string, unknown> } } };
    });
    expect(validateWorkflow(nodes, fallbackCatalogRaw, graph.edges)).toEqual([]);
    expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'found', sourceHandle: 'true', target: 'result' }));
    expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'found', sourceHandle: 'false', target: 'missing' }));
    expect(graph.nodes.find(node => node.id === 'extract')?.data.typeKey).toBe('contour.extract');
  });
});
