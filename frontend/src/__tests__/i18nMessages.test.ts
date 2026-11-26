/**
 * 词表质量守卫（2026-11-26）。
 *
 * 背景：`translate()` 在缺 key 时回落到 key 本身，英文值漏填时不会报错、只会静默显示中文。
 * 手工新增几十条词条时，最容易犯的三类错误正好都是静默的：
 *   1. 只填了中文，英文空着或复制了中文；
 *   2. 英文里混入中文标点/汉字（复制粘贴时会带过来）；
 *   3. 占位符只在一边写了——`{count}` 漏在英文里，英文界面就少一个数字。
 * 这三类都在这里拦成编译期之外的显式失败，避免等到切英文才被发现。
 */
import { describe, expect, it } from 'vitest';
import { messages, type MessageKey } from '../i18n/messages';

const entries = Object.entries(messages) as Array<[MessageKey, { zh: string; en: string }]>;
const CJK = /[\u3400-\u4dbf\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]/;

const placeholders = (template: string): string[] =>
  [...template.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

describe('i18n 词表完整性', () => {
  it('每条词条都有非空的 zh 与 en', () => {
    const broken = entries
      .filter(([, entry]) => !entry.zh.trim() || !entry.en.trim())
      .map(([key]) => key);
    expect(broken).toEqual([]);
  });

  it('英文值不含中文汉字与全角标点', () => {
    const offenders = entries
      .filter(([, entry]) => CJK.test(entry.en))
      .map(([key, entry]) => `${key}: ${entry.en}`);
    expect(offenders).toEqual([]);
  });

  it('中英占位符集合一致', () => {
    const mismatched = entries
      .filter(([, entry]) => placeholders(entry.zh).join(',') !== placeholders(entry.en).join(','))
      .map(([key, entry]) => `${key}: zh={${placeholders(entry.zh)}} en={${placeholders(entry.en)}}`);
    expect(mismatched).toEqual([]);
  });

  it('key 使用点分命名空间，便于按模块审计', () => {
    const offenders = entries
      .filter(([key]) => !/^[a-z][a-zA-Z0-9]*(\.[a-zA-Z0-9]+)+$/.test(key))
      .map(([key]) => key);
    expect(offenders).toEqual([]);
  });

  it('中文词条的英文不是逐字复制', () => {
    // 纯符号/缩写的词条（Cp、Cpk、UCL、LCL）两种语言本就相同，不在此列；
    // 只看含中文的词条，避免漏翻时把中文原样填到 en。
    const copied = entries
      .filter(([, entry]) => CJK.test(entry.zh) && entry.zh === entry.en)
      .map(([key]) => key);
    expect(copied).toEqual([]);
  });
});
