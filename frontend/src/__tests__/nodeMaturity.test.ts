import { describe, expect, it } from 'vitest';
import catalog from '../generated/catalog.generated.json';
import { maturityLabel, maturityLevel, maturityTitle } from '../nodeMaturity';
import type { NodeCatalogItem } from '../types';

const items = catalog.items as unknown as NodeCatalogItem[];

describe('node maturity level', () => {
  it('treats an absent field as stable because the backend omits the default', () => {
    // 后端只在非 Stable 时写字段（JsonIgnore WhenWritingDefault）。用 === 'Stable' 判断会把
    // 99% 的节点判成非稳定，所以缺省必须是 stable。
    expect(maturityLevel({ maturity: null })).toBe('stable');
    expect(maturityLevel({})).toBe('stable');
    expect(maturityLevel(undefined)).toBe('stable');
    expect(maturityLevel({ maturity: '' as never })).toBe('stable');
  });

  it('normalises the PascalCase enum emitted by the API', () => {
    expect(maturityLevel({ maturity: 'Preview' })).toBe('preview');
    expect(maturityLevel({ maturity: 'Experimental' })).toBe('experimental');
    expect(maturityLevel({ maturity: 'preview' })).toBe('preview');
    expect(maturityLevel({ maturity: 'Stable' })).toBe('stable');
  });

  it('marks exactly the audited nodes in the generated catalog', () => {
    const experimental = items.filter((item) => maturityLevel(item) === 'experimental').map((item) => item.type).sort();
    // 这三个推理节点没有模型包可跑，只有错误路径测试——补了真实模型基线后再从这里移除。
    expect(experimental).toEqual(['inference.instanceSegmentation', 'inference.obb', 'inference.pose']);

    const preview = items.filter((item) => maturityLevel(item) === 'preview').map((item) => item.type);
    expect(preview).toHaveLength(15);
    expect(preview).toContain('measure.thread');
    expect(preview).toContain('value.counter');

    // Stable 节点不写字段：目录 JSON 形状没有膨胀。
    const stableWithField = items.filter((item) => maturityLevel(item) === 'stable' && item.maturity != null);
    expect(stableWithField).toEqual([]);
  });

  it('keeps labels short and tooltips explanatory in both languages', () => {
    expect(maturityLabel('stable', 'zh')).toBe('');
    expect(maturityLabel('preview', 'zh')).toBe('预览');
    expect(maturityLabel('preview', 'en')).toBe('Preview');
    expect(maturityLabel('experimental', 'zh')).toBe('实验');
    expect(maturityLabel('experimental', 'en')).toBe('Experimental');

    // 悬停说明要讲"为什么降级"，不能只是重复标签。
    expect(maturityTitle('preview', 'zh')).toContain('适用范围');
    expect(maturityTitle('experimental', 'zh')).toContain('行为基线');
    expect(maturityTitle('experimental', 'en')).toContain('behavioural baseline');
    expect(maturityTitle('stable', 'zh')).toContain('稳定');
  });
});
