import { describe, expect, it } from 'vitest';
import { legacyNodeReplacement, legacyNodeReplacements } from '../legacyNodes';
import { fallbackCatalog } from '../catalog';

describe('legacy node replacements', () => {
  it('maps every merged legacy type to a node that still exists in the catalog', () => {
    for (const [type, replacement] of Object.entries(legacyNodeReplacements)) {
      expect(fallbackCatalog.some((item) => item.type.toLowerCase() === type), type).toBe(true);
      expect(fallbackCatalog.some((item) => item.type.toLowerCase() === replacement.toLowerCase()), `${type} → ${replacement}`).toBe(true);
      expect(legacyNodeReplacement(type.toUpperCase())).toBe(replacement);
    }
  });

  it('leaves current nodes unmarked', () => {
    expect(legacyNodeReplacement('image.threshold')).toBeUndefined();
    expect(legacyNodeReplacement(undefined)).toBeUndefined();
  });
});
