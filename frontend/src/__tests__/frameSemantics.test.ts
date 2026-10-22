import { describe, expect, it } from 'vitest';
import { catalogItemChangesImageFrame, declaredFrameChange, resolveFrameRule } from '../frameSemantics';
import { fallbackCatalogRaw } from '../catalog';
import type { NodeFrameRule, ParameterDescriptor } from '../types';

const spatial: NodeFrameRule = { policy: 'NewSpatialFrame', transform: 'Spatial' };
const preserve: NodeFrameRule = { policy: 'PreserveInput' };

describe('frontend frame semantics follow the catalog declaration', () => {
  it('falls back to the legacy type set only when the catalog has no frames', () => {
    expect(declaredFrameChange(null, undefined)).toBeUndefined();
    expect(declaredFrameChange([], undefined)).toBeUndefined();
  });

  it('reports declared spatial and preserved frames', () => {
    expect(declaredFrameChange([spatial], undefined)).toBe(true);
    expect(declaredFrameChange([preserve], undefined)).toBe(false);
  });

  it('resolves conditional rules from parameters and catalog defaults', () => {
    const rules: NodeFrameRule[] = [
      { policy: 'NewFixedFrame', frameSuffix: 'psf', whenParameter: 'operator', whenValues: ['psfMotion', 'psfDefocus'] },
      preserve
    ];
    expect(resolveFrameRule(rules, { operator: 'psfMotion' })?.frameSuffix).toBe('psf');
    expect(declaredFrameChange(rules, { operator: 'psfMotion' })).toBe(true);
    expect(declaredFrameChange(rules, { operator: 'harmonic' })).toBe(false);
    expect(declaredFrameChange(rules, undefined)).toBe(false);
  });

  it('uses the catalog default when the node parameter is absent', () => {
    const descriptors: ParameterDescriptor[] = [{ name: 'operator', label: 'Operator', type: 'select', defaultValue: 'psfMotion' }];
    const rules: NodeFrameRule[] = [
      { policy: 'NewFixedFrame', frameSuffix: 'psf', whenParameter: 'operator', whenValues: ['psfMotion'] },
      preserve
    ];
    expect(declaredFrameChange(rules, undefined, descriptors)).toBe(true);
  });

  it('keeps the legacy fallback for catalogs without frames', () => {
    const crop = fallbackCatalogRaw.find((item) => item.type === 'image.crop')!;
    expect(catalogItemChangesImageFrame(crop)).toBe(true);
    const plugin = { ...crop, type: 'plugin.custom', frames: null };
    expect(catalogItemChangesImageFrame(plugin)).toBe(false);
  });

  it('drives built-in nodes from their generated declarations', () => {
    const types = [
      'image.crop', 'image.resize', 'image.rotate', 'image.perspective', 'image.warpAffine',
      'image.undistort', 'jlvision.worldPlane'
    ];
    for (const type of types) {
      const item = fallbackCatalogRaw.find((candidate) => candidate.type === type)!;
      expect(item.frames?.length, type).toBeGreaterThan(0);
      expect(catalogItemChangesImageFrame(item), type).toBe(true);
    }

    const restoration = fallbackCatalogRaw.find((item) => item.type === 'jlvision.restoration')!;
    expect(catalogItemChangesImageFrame(restoration, { operator: 'psfMotion' })).toBe(true);
    expect(catalogItemChangesImageFrame(restoration, { operator: 'harmonic' })).toBe(false);

    const imageOp = fallbackCatalogRaw.find((item) => item.type === 'jlvision.imageOp')!;
    expect(catalogItemChangesImageFrame(imageOp, { operator: 'cropPart' })).toBe(true);
    expect(catalogItemChangesImageFrame(imageOp, { operator: 'abs' })).toBe(false);
  });
});
