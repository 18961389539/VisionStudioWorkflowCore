import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { isPortRequired } from '../portRequirements';
import { localizeCatalog } from '../i18n/catalog';

describe('operation dependent input requirements', () => {
  it.each([
    ['geometry.intersections', 'lineCircle', 'line', true],
    ['geometry.intersections', 'circles', 'line', false],
    ['geometry.intersections', 'circles', 'circle2', true],
    ['jlvision.restoration', 'psfMotion', 'image', false],
    ['jlvision.restoration', 'wiener', 'filteredImage', true],
    ['jlvision.segmentation', 'watershedsMarker', 'markers', true],
    ['jlvision.segmentation', 'watersheds', 'markers', false],
    ['jlvision.worldPlane', 'applyMap', 'mapRows', true],
    ['jlvision.worldPlane', 'applyMap', 'mapColumns', true],
    ['jlvision.imageOp', 'compose3', 'image3', true],
    ['jlvision.imageOp', 'abs', 'image3', false],
  ])('%s/%s requires %s: %s', (type, operator, portName, required) => {
    const catalog = fallbackCatalogRaw.find(n => n.type === type)!;
    expect(isPortRequired(catalog.inputs.find(p => p.name === portName)!, { ...makeDefaultParameters(catalog), operator }, catalog.parameters)).toBe(required);
  });

  it('uses the catalog operation default when an old graph omits the parameter', () => {
    const catalog = fallbackCatalogRaw.find(n => n.type === 'geometry.intersections')!;
    expect(isPortRequired(catalog.inputs.find(p => p.name === 'line')!, {}, catalog.parameters)).toBe(true);
  });

  it.each([
    ['logic.boolean', 'and', true], ['logic.boolean', 'or', true], ['logic.boolean', 'xor', true], ['logic.boolean', 'not', false],
    ['value.arithmetic', 'add', true], ['value.arithmetic', 'pow', true], ['value.arithmetic', 'min', true],
    ['value.arithmetic', 'abs', false], ['value.arithmetic', 'sqrt', false], ['value.arithmetic', 'clamp', false]
  ])('%s/%s requires the second operand: %s', (type, op, required) => {
    const catalog = fallbackCatalogRaw.find(item => item.type === type)!;
    expect(isPortRequired(catalog.inputs.find(port => port.name === 'b')!, { op }, catalog.parameters)).toBe(required);
  });

  it('matches conditional inputs case-insensitively and supports boolean controller defaults', () => {
    const catalog = fallbackCatalogRaw.find(n => n.type === 'jlvision.imageOp')!;
    const port = catalog.inputs.find(p => p.name === 'image3')!;
    expect(isPortRequired(port, { operator: 'COMPOSE3' }, catalog.parameters)).toBe(true);
    const flagPort = { name: 'reference', dataType: 'Image', required: false, requiredWhen: { parameter: 'enabled', values: ['true'] } };
    expect(isPortRequired(flagPort, {}, [{ name: 'enabled', label: 'Enabled', type: 'boolean', defaultValue: true }])).toBe(true);
    expect(isPortRequired(flagPort, { enabled: false })).toBe(false);
  });

  it('localizes all new node ports', () => {
    const types = new Set(['jlvision.featureExtract', 'jlvision.segmentation', 'jlvision.regionAnalysis', 'measure.fitRectangle',
      'geometry.intersections', 'jlvision.dxf', 'jlvision.worldPlane', 'jlvision.restoration', 'jlvision.surface']);
    for (const item of localizeCatalog(fallbackCatalogRaw).filter(n => types.has(n.type)))
      for (const port of [...item.inputs, ...item.outputs]) expect(port.label, `${item.type}.${port.name}`).toMatch(/[\u3400-\u9fff]/);
  });
});
