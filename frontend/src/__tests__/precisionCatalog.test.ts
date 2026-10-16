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

  it('explains that disabling parabolic refinement does not guarantee integer image coordinates', () => {
    const translated = localizeCatalog(fallbackCatalogRaw, 'zh');
    for (const type of ['measure.caliper', 'measure.caliperLine', 'feature.edgeSubpixel']) {
      const refine = translated.find(item => item.type === type)?.parameters.find(parameter => parameter.name === 'refine');
      expect(refine?.options?.find(option => option.value === 'none')?.label).toBe('不做抛物线细化');
      expect(refine?.description).toContain('图像坐标仍可能是小数');
    }
  });

  it('localizes parameter groups for ROI and caliper workspaces while preserving English catalogs', () => {
    const english = localizeCatalog(fallbackCatalogRaw, 'en');
    const chinese = localizeCatalog(fallbackCatalogRaw, 'zh');
    const expected: Record<string, string[]> = {
      'measure.caliper': ['搜索线', '采样'],
      'measure.caliperLine': ['搜索线', '采样', '拟合'],
      'measure.caliperCircle': ['标称圆', '圆弧', '采样', '拟合'],
      'roi.transform': ['基准位姿', '当前位姿']
    };
    for (const [type, groups] of Object.entries(expected)) {
      const englishItem = english.find(item => item.type === type)!;
      const chineseItem = chinese.find(item => item.type === type)!;
      expect([...new Set(chineseItem.parameters.map(parameter => parameter.group).filter(Boolean))]).toEqual(groups);
      expect([...new Set(englishItem.parameters.map(parameter => parameter.group).filter(Boolean))]).not.toEqual(groups);
    }
  });

  it('lets users place clipped caliper search geometry outside the image bounds', () => {
    const types = ['measure.caliper', 'feature.edgeSubpixel', 'measure.caliperLine'];
    for (const type of types) {
      const item = fallbackCatalogRaw.find(entry => entry.type === type)!;
      for (const name of ['startX', 'startY', 'endX', 'endY'])
        expect(item.parameters.find(parameter => parameter.name === name)).toMatchObject({ min: -100000, max: 100000 });
    }
    const circle = fallbackCatalogRaw.find(item => item.type === 'measure.caliperCircle')!;
    for (const name of ['centerX', 'centerY'])
      expect(circle.parameters.find(parameter => parameter.name === name)).toMatchObject({ min: -100000, max: 100000 });
    expect(circle.parameters.find(parameter => parameter.name === 'radius')).toMatchObject({ min: 1, max: 100000 });
  });

  it('validates caliper geometry across the full supported high-resolution range', () => {
    const image = fallbackCatalogRaw.find(item => item.type === 'image.synthetic')!;
    const imageNode = { id: 'image', position: { x: 0, y: 0 }, data: { typeKey: image.type,
      inputs: image.inputs, outputs: image.outputs, parameters: makeDefaultParameters(image) } };
    for (const type of ['measure.caliper', 'feature.edgeSubpixel', 'measure.caliperLine']) {
      const item = fallbackCatalogRaw.find(entry => entry.type === type)!;
      const parameters = { ...makeDefaultParameters(item), startX: 12_000, startY: 8_000, endX: 16_000, endY: 8_000 };
      expect(validateWorkflow([
        imageNode,
        { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: type, inputs: item.inputs, outputs: item.outputs, parameters } }
      ], fallbackCatalogRaw, [
        { id: 'control', source: 'image', sourceHandle: 'next', target: 'caliper', targetHandle: 'exec', data: { kind: 'control' } },
        { id: 'image', source: 'image', sourceHandle: 'image', target: 'caliper', targetHandle: 'image', data: { kind: 'data' } }
      ])).toEqual([]);
    }

    const circle = fallbackCatalogRaw.find(item => item.type === 'measure.caliperCircle')!;
    const circleParameters = { ...makeDefaultParameters(circle), centerX: 12_000, centerY: 8_000, radius: 8_000 };
    expect(validateWorkflow([
      imageNode,
      { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: circle.type, inputs: circle.inputs, outputs: circle.outputs, parameters: circleParameters } }
    ], fallbackCatalogRaw, [
      { id: 'control', source: 'image', sourceHandle: 'next', target: 'caliper', targetHandle: 'exec', data: { kind: 'control' } },
      { id: 'image', source: 'image', sourceHandle: 'image', target: 'caliper', targetHandle: 'image', data: { kind: 'data' } }
    ])).toEqual([]);
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
