import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { caliperWidthDemo } from '../demos';
import { localizeCatalog } from '../i18n/catalog';
import { configurationIssues } from '../nodeConfigurationState';
import { searchTools } from '../toolboxModel';
import { validateWorkflow } from '../validation';

const width = fallbackCatalogRaw.find(item => item.type === 'measure.caliperWidth')!;
const defaults = makeDefaultParameters(width);

describe('paired-edge caliper width', () => {
  it('ships a runnable tolerance template that guards missing widths', () => {
    const graph = caliperWidthDemo();
    const nodes = graph.nodes.map(node => {
      const item = fallbackCatalogRaw.find(entry => entry.type === node.data.typeKey)!;
      return { ...node, data: { ...node.data, inputs: item.inputs, outputs: item.outputs,
        parameters: { ...makeDefaultParameters(item), ...node.data.parameters as Record<string, unknown> } } };
    });
    expect(validateWorkflow(nodes, fallbackCatalogRaw, graph.edges)).toEqual([]);
    expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'found', sourceHandle: 'true', target: 'tolerance' }));
    expect(graph.edges).toContainEqual(expect.objectContaining({ source: 'found', sourceHandle: 'false', target: 'missing' }));
  });
  it('provides a discoverable localized tool with optional measurement outputs', () => {
    const catalog = localizeCatalog(fallbackCatalogRaw, 'zh');
    const localized = catalog.find(item => item.type === width.type)!;
    expect(localized.displayName).toBe('双边缘卡尺测宽');
    expect(searchTools(catalog, '槽宽')[0].type).toBe(width.type);
    expect(localized.description).toContain('ROI 缺口');
    for (const parameter of localized.parameters.filter(item => !item.internal)) {
      expect(parameter.label).toMatch(/[\u4e00-\u9fff]/);
      for (const option of parameter.options ?? []) expect(option.label).toMatch(/[\u4e00-\u9fff]/);
    }
    for (const name of ['width', 'point1', 'point2', 'center', 'strength'])
      expect(width.outputs.find(port => port.name === name)?.required).toBe(false);
  });

  it('rejects inverted width bounds and missing nominal width before running', () => {
    expect(configurationIssues(width, { ...defaults, minWidth: 30, maxWidth: 20 })).toContain('最大宽度必须为 0（不限）或不小于最小宽度。');
    expect(configurationIssues(width, { ...defaults, direction: 'closestwidth', nominalWidth: 0 })).toContain('按标称宽度选择时，标称宽度必须大于 0。');
    expect(configurationIssues(width, { ...defaults, minWidth: 30, maxWidth: 0 })).toEqual([]);
    expect(configurationIssues(width, { ...defaults, direction: 'closestwidth', nominalWidth: 25 })).toEqual([]);
  });

  it('uses a connected search line ahead of stale endpoint parameters', () => {
    const stale = { ...defaults, startX: 'stale', endX: 'stale', startY: 0, endY: 0 };
    expect(configurationIssues(width, stale).length).toBeGreaterThan(0);
    expect(configurationIssues(width, stale, { connectedInputPorts: ['searchLine'] })).toEqual([]);
    expect(configurationIssues(width, { ...defaults, startX: 10, startY: 20, endX: 10, endY: 20 }).join(' ')).toContain('搜索线长度至少需要 2 px');
  });
});
