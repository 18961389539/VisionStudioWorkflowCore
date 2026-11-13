import { describe, expect, it } from 'vitest';
import { statusNames } from '../i18n/catalog.zh';

/**
 * 状态词表键形态守卫（2026-11-12）。
 *
 * `catalogLabels.ts` 已迁到稳定键（节点类型 / 参数名 / 选项值），但 `statusNames` 仍以
 * 后端状态原文为键——这是合理的（状态是线上协议的一部分），风险在于有人图省事把整句
 * 英文描述当键加进来：后端改一个字，中文就静默消失，而且没人能从类型上发现。
 *
 * 这里只钉两件事：
 * 1. 键必须是"标识符形态"（无空格、长度受限），把英文句子挡在门外；
 * 2. 关键生命周期状态必须始终有中文——删掉一条比改错一条更难被发现。
 */
describe('状态词表', () => {
  it('键是后端标识符，不是英文句子', () => {
    for (const key of Object.keys(statusNames)) {
      expect(key, `状态键 "${key}" 不是标识符形态`).toMatch(/^[A-Za-z][A-Za-z0-9_.-]{0,39}$/);
    }
  });

  it('每条状态都有非空中文', () => {
    for (const [key, value] of Object.entries(statusNames)) {
      expect(value.trim(), `状态 ${key} 的中文为空`).not.toBe('');
    }
  });

  it('运行与配方生命周期状态不会被悄悄删除', () => {
    for (const key of ['Running', 'Succeeded', 'Failed', 'Cancelled', 'Queued', 'Interrupted', 'Breakpoint']) {
      expect(statusNames, `缺少状态 ${key}`).toHaveProperty(key);
    }
  });
});
