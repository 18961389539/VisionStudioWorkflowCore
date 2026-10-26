import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { STORAGE_KEYS, STORAGE_PREFIX, readStored, readStoredJson, removeStored, writeStored, writeStoredJson } from '../storage';

/**
 * 本地偏好存储层（2026-10-26）：把 12 个散落的键收敛到一处。
 * 关键契约：键名带统一前缀、storage 不可用时不抛错、JSON 形状不符时回退默认值。
 */

beforeEach(() => window.localStorage.clear());
afterEach(() => vi.restoreAllMocks());

describe('存储层', () => {
  it('键名带统一前缀', () => {
    expect(`${STORAGE_PREFIX}${STORAGE_KEYS.lang}`).toBe('visionstudio.lang');
    expect(`${STORAGE_PREFIX}${STORAGE_KEYS.themeMode}`).toBe('visionstudio.theme-mode');
  });

  it('读写字符串并按 fallback 兜底', () => {
    expect(readStored(STORAGE_KEYS.lang, 'zh')).toBe('zh');
    writeStored(STORAGE_KEYS.lang, 'en');
    expect(readStored(STORAGE_KEYS.lang)).toBe('en');
    expect(removeStored(STORAGE_KEYS.lang)).toBeUndefined();
    expect(readStored(STORAGE_KEYS.lang, 'zh')).toBe('zh');
  });

  it('JSON 形状不符时回退，不把脏数据带进界面', () => {
    window.localStorage.setItem(`${STORAGE_PREFIX}${STORAGE_KEYS.toolboxFavorites}`, '{"not":"an array"}');
    expect(readStoredJson(STORAGE_KEYS.toolboxFavorites, (value) => Array.isArray(value) ? value as string[] : undefined, [])).toEqual([]);

    window.localStorage.setItem(`${STORAGE_PREFIX}${STORAGE_KEYS.toolboxFavorites}`, 'not json');
    expect(readStoredJson(STORAGE_KEYS.toolboxFavorites, (value) => Array.isArray(value) ? value as string[] : undefined, [])).toEqual([]);
  });

  it('JSON 往返保留内容', () => {
    writeStoredJson(STORAGE_KEYS.recentNodeTypes, ['image.threshold', 'image.blur']);
    expect(readStoredJson(STORAGE_KEYS.recentNodeTypes, (value) => Array.isArray(value) ? value as string[] : undefined, [])).toEqual(['image.threshold', 'image.blur']);
  });

  it('storage 不可用（隐私模式/配额满）时静默降级', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(() => writeStored(STORAGE_KEYS.lang, 'en')).not.toThrow();
    expect(() => writeStoredJson(STORAGE_KEYS.recentNodeTypes, [])).not.toThrow();
    expect(readStored(STORAGE_KEYS.lang, 'zh')).toBe('zh');
    expect(readStoredJson(STORAGE_KEYS.recentNodeTypes, () => undefined, [])).toEqual([]);
  });
});
