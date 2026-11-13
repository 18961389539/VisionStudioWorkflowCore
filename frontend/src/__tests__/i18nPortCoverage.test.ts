import { describe, expect, it } from 'vitest';
import catalog from '../generated/catalog.generated.json';
import { portNames } from '../i18n/catalog.zh';

/**
 * 端口中文覆盖率棘轮（2026-11-13）。
 *
 * 目录标签与参数描述已迁到语义键，并由 i18nStableKeys / catalogLocalization 守住；
 * 端口名仍有存量未翻译。这里不要求一次补齐，而是锁住"只减不增"：
 * 新增节点/端口必须同时补中文，否则未翻译数量增加会失败。
 *
 * 动态分支/循环端口（case1…、point1…）由 localizePortName 规则生成，不在此表登记。
 */
const UNTRANSLATED_PORT_BASELINE = 157;
const DYNAMIC_PORT = /^(case|point)\d+$/;
const CJK = /[\u3400-\u9fff]/;

type CatalogPort = { name: string; label?: string | null };
type CatalogItem = { inputs?: CatalogPort[]; outputs?: CatalogPort[] };
const items = (catalog as { items: CatalogItem[] }).items;

function missingPortTranslations(): string[] {
  const missing = new Set<string>();
  for (const item of items) {
    for (const port of [...(item.inputs ?? []), ...(item.outputs ?? [])]) {
      if (portNames[port.name] || DYNAMIC_PORT.test(port.name) || CJK.test(port.label ?? '')) continue;
      missing.add(port.name);
    }
  }
  return [...missing].sort();
}

describe('端口中文覆盖率', () => {
  it(`未翻译端口名只减不增（基线 ${UNTRANSLATED_PORT_BASELINE}）`, () => {
    const missing = missingPortTranslations();
    expect(
      missing.length,
      `未翻译端口名 ${missing.length} 条（基线 ${UNTRANSLATED_PORT_BASELINE}）：\n${missing.slice(0, 20).join('\n')}`
    ).toBeLessThanOrEqual(UNTRANSLATED_PORT_BASELINE);
  });

  it('端口翻译键是稳定标识符，不是英文句子', () => {
    for (const key of Object.keys(portNames)) {
      expect(key, `端口键 "${key}" 不是标识符形态`).toMatch(/^[A-Za-z][A-Za-z0-9_]*$/);
    }
  });

  it('端口中文翻译非空', () => {
    for (const [key, value] of Object.entries(portNames)) {
      expect(value.trim(), `端口 ${key} 的中文为空`).not.toBe('');
    }
  });
});
