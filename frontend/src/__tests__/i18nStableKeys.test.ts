import { describe, expect, it } from 'vitest';
import catalogRaw from '../generated/catalog.generated.json?raw';
import type { NodeCatalogItem } from '../types';
import {
  categories, descriptions, nodeNames, parameterDescriptions, parameterGroups
} from '../i18n/catalog.zh';
import { optionLabels, parameterLabels, variantLabels } from '../i18n/catalogLabels';
import { canonicalParameterGroup, localizeCatalog } from '../i18n/catalog';

/**
 * i18n 稳定键守卫（2026-11-12）。
 *
 * 旧的目录翻译表用**英文原文当键**：后端把描述里的一个词改了，中文就静默消失，
 * 而类型上完全看不出来（Record<string,string> 接受任何字符串）。2026-11-10 起目录标签、
 * 节点描述、参数描述陆续迁到语义键（节点类型 / `类型.参数名`），本轮清掉最后 11 条
 * 以整句英文说明为键的残留——它们本来就是死条目：查表只走 `descriptions[item.type]`，
 * 整句键永远不会命中，而且对应的语义键条目早已存在。
 *
 * 拆完只是第一半；没有守卫，下一次赶工又会把一句英文粘回键位上。这里钉四件事：
 *   1. 描述类词表的键必须是语义键形态（禁止英文句子）；
 *   2. 枚举类词表（分类 / 分组 / 状态）的键必须是短契约 token，不能是自由文案；
 *   3. 语义键必须与真实目录对得上，悬空条目只许减不许增（棘轮）；
 *   4. 目录里实际出现的分类与分组必须有中文，不能悄悄回落英文。
 */
type CatalogItem = {
  type: string;
  category: string;
  parameters?: Array<{ name: string; group?: string | null }>;
};
const catalog = JSON.parse(catalogRaw) as { items: CatalogItem[] };

const nodeTypes = new Set(catalog.items.map((item) => item.type));
const parameterKeys = new Set(
  catalog.items.flatMap((item) => (item.parameters ?? []).map((parameter) => `${item.type}.${parameter.name}`))
);
const usedCategories = new Set(catalog.items.map((item) => item.category));
const usedGroups = new Set(
  catalog.items.flatMap((item) => (item.parameters ?? []).map((parameter) => parameter.group).filter((group): group is string => !!group))
);

/** `image.acquire` / `measure.blobs.maxArea`：点分段语义键，不含空格与标点。 */
const SEMANTIC_KEY = /^[a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z0-9_]+)+$/;
/** `Acquisition` / `Device Adjust`：后端契约值，最多三个单词，不允许句子标点。 */
const TOKEN_KEY = /^[A-Za-z][A-Za-z0-9_-]*(?: [A-Za-z][A-Za-z0-9_-]*){0,2}$/;

/** 悬空语义键棘轮：只许减。删掉旧节点的翻译时要顺手下调这里。
 *  2026-11-12 实测 126 条：多数是被合并/下线节点的历史翻译，留着无害（不会命中），
 *  但它们会掩盖"新键拼错"，所以数量锁死，只允许往下走。 */
const DANGLING_BASELINE = 126;

function danglingKeys(): string[] {
  return [
    ...Object.keys(descriptions).filter((key) => !nodeTypes.has(key)).map((key) => `descriptions:${key}`),
    ...Object.keys(parameterDescriptions).filter((key) => !parameterKeys.has(key)).map((key) => `parameterDescriptions:${key}`)
  ];
}

describe('i18n 稳定键', () => {
  it('节点描述与参数描述表不再以英文原文为键', () => {
    const tables: Array<[string, Record<string, string>]> = [
      ['nodeNames', nodeNames], ['descriptions', descriptions], ['parameterDescriptions', parameterDescriptions]
    ];
    for (const [name, table] of tables) {
      for (const key of Object.keys(table)) {
        expect(key, `${name} 的键 "${key.slice(0, 80)}" 不是语义键，请改用节点类型 / 类型.参数名`).toMatch(SEMANTIC_KEY);
      }
    }
  });

  it('分类与分组表只使用短契约 token', () => {
    for (const [name, table] of [['categories', categories], ['parameterGroups', parameterGroups]] as const) {
      for (const key of Object.keys(table)) {
        expect(key, `${name} 的键 "${key}" 看起来是自由文案，不是后端契约值`).toMatch(TOKEN_KEY);
      }
    }
  });

  it('参数标签/选项/变体表使用语义键', () => {
    // 这三张表是最早迁移到稳定键的（2026-11-10）；守住形态，防止后续把英文文案粘回键位。
    for (const key of Object.keys(parameterLabels)) {
      expect(key, `parameterLabels 的键 "${key}" 不是类型.参数名`).toMatch(SEMANTIC_KEY);
    }
    for (const [name, table] of [['optionLabels', optionLabels], ['variantLabels', variantLabels]] as const) {
      for (const nodeType of Object.keys(table)) {
        expect(nodeType, `${name} 的节点键 "${nodeType}" 不是节点类型`).toMatch(SEMANTIC_KEY);
      }
    }
  });

  it('每条中文翻译都非空', () => {
    for (const [name, table] of Object.entries({ nodeNames, categories, parameterGroups, descriptions, parameterDescriptions })) {
      for (const [key, value] of Object.entries(table)) {
        expect(value.trim(), `${name} 的 ${key} 中文为空`).not.toBe('');
      }
    }
  });

  it('悬空语义键只减不增（相对生成的目录金样）', () => {
    const dangling = danglingKeys();
    expect(
      dangling.length,
      `悬空语义键 ${dangling.length} 条（基线 ${DANGLING_BASELINE}）：\n${dangling.slice(0, 12).join('\n')}`
    ).toBeLessThanOrEqual(DANGLING_BASELINE);
  });

  it('目录实际用到的分类与分组都有中文', () => {
    // 后端已有少量分组名本身就是中文；它们不需要翻译，也不算"回落英文"。
    const isAlreadyChinese = (value: string) => /[\u3400-\u9fff]/.test(value);
    const missing = [
      ...[...usedCategories].filter((name) => !categories[name] && !isAlreadyChinese(name)).map((name) => `category:${name}`),
      ...[...usedGroups].filter((name) => !parameterGroups[name] && !isAlreadyChinese(name)).map((name) => `group:${name}`)
    ];
    expect(missing, `以下契约值在中文界面会静默回落英文：\n${missing.join('\n')}`).toEqual([]);
  });

  it('分组中文值唯一（反查不能写回错误的契约值）', () => {
    // canonicalParameterGroup 用「中文 -> 英文」反查。两个分组译成同一个中文时，
    // Object.fromEntries 里后者覆盖前者，保存配方会把错的 group 写回后端，而且没有报错。
    const byChinese = new Map<string, string>();
    for (const [source, chinese] of Object.entries(parameterGroups)) {
      const previous = byChinese.get(chinese);
      expect(previous, `分组中文 "${chinese}" 同时对应 ${previous} 与 ${source}`).toBeUndefined();
      byChinese.set(chinese, source);
    }
  });

  it('真实目录双语本地化：中文登记生效、英文直通', () => {
    const items = catalog.items as unknown as NodeCatalogItem[];
    const zh = localizeCatalog(items, 'zh');
    const en = localizeCatalog(items, 'en');
    expect(zh).toHaveLength(items.length);

    let translatedGroups = 0;
    for (let i = 0; i < items.length; i++) {
      // 英文模式必须原样返回，不能把中文带进英文界面。
      expect(en[i].category).toBe(items[i].category);
      expect(en[i].description).toBe(items[i].description);
      // 中文模式：已登记的分类/描述/分组都要真的替换掉英文原文。
      if (categories[items[i].category]) expect(zh[i].category).toBe(categories[items[i].category]);
      if (descriptions[items[i].type]) expect(zh[i].description).toBe(descriptions[items[i].type]);
      for (let p = 0; p < items[i].parameters.length; p++) {
        const sourceGroup = items[i].parameters[p].group;
        if (!sourceGroup || !parameterGroups[sourceGroup]) continue;
        translatedGroups++;
        expect(zh[i].parameters[p].group).toBe(parameterGroups[sourceGroup]);
        // 反向解析要能回到后端契约值，否则保存配方时会把中文分组名写回后端。
        expect(canonicalParameterGroup(zh[i].parameters[p].group)).toBe(sourceGroup);
      }
    }
    expect(translatedGroups, '没有任何分组被本地化，说明登记表没接上').toBeGreaterThan(100);
  });
});
