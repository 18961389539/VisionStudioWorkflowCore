import { describe, expect, it } from 'vitest';
import { isMigratedNodeType, migrateNodeType } from '../legacyNodes';
import { fallbackCatalog } from '../catalog';

/** 已物理合并的旧节点：前端加载期必须把它们改写成规范类型 + 参数。 */
const mergedTypes = [
  'image.blur.gaussian',
  'image.blur.median',
  'image.topHat',
  'image.bottomHat',
  'image.morphGradient',
  'region.fromThreshold',
  'region.fromColorRange',
  'measure.perpendicularity',
  'image.threshold.adaptive',
  'image.threshold.otsu',
  'text.match',
  'text.regex',
  'measure.blob',
  'image.grayscale'
];

describe('legacy node migration', () => {
  it('maps every merged legacy type to a node that exists in the catalog', () => {
    for (const type of mergedTypes) {
      const migrated = migrateNodeType(type, {});
      expect(migrated.type, type).not.toBe(type);
      expect(fallbackCatalog.some((item) => item.type === migrated.type), `${type} → ${migrated.type}`).toBe(true);
      expect(isMigratedNodeType(type.toUpperCase())).toBe(true);
    }
  });

  it('fills the merged parameter without overwriting explicit values', () => {
    expect(migrateNodeType('image.blur.median', { kernelSize: 5 }).parameters).toMatchObject({ method: 'median', kernelSize: 5 });
    expect(migrateNodeType('image.blur.median', { method: 'gaussian' }).parameters.method).toBe('gaussian');
    expect(migrateNodeType('measure.blob', {}).parameters.mode).toBe('largest');
    expect(migrateNodeType('text.regex', {}).parameters).toMatchObject({ mode: 'regex', emptyInputPolicy: 'fail' });
  });

  it('remaps the old grayscale mode onto the channel parameter', () => {
    expect(migrateNodeType('image.grayscale', { mode: 'luminance' }).parameters).toMatchObject({ channel: 'gray' });
    expect(migrateNodeType('image.grayscale', { mode: 'average' }).parameters).toMatchObject({ channel: 'average' });
    expect(migrateNodeType('image.grayscale', { mode: 'max' }).parameters).toMatchObject({ channel: 'max' });
    // 取值映射成功时旧键不再保留，避免残留未知参数。
    expect(migrateNodeType('image.grayscale', { mode: 'max' }).parameters.mode).toBeUndefined();
  });

  it('leaves current nodes untouched', () => {
    const migrated = migrateNodeType('image.threshold', { mode: 'otsu' });
    expect(migrated.type).toBe('image.threshold');
    expect(migrated.parameters).toMatchObject({ mode: 'otsu' });
    expect(isMigratedNodeType('image.threshold')).toBe(false);
    expect(isMigratedNodeType(undefined)).toBe(false);
  });
});
