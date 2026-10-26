/**
 * Language runtime primitives for the VisionStudio UI.
 *
 * Chinese stays the deterministic product default (matches `index.html lang="zh-CN"`).
 * A stored user preference always wins, so switching languages is sticky across sessions.
 */
export type Lang = 'zh' | 'en';
import { STORAGE_KEYS, STORAGE_PREFIX, readStored, writeStored } from '../storage';

export const LANGS: Lang[] = ['zh', 'en'];
export const DEFAULT_LANG: Lang = 'zh';
export const LANG_STORAGE_KEY = `${STORAGE_PREFIX}${STORAGE_KEYS.lang}`;

const isLang = (value: unknown): value is Lang => value === 'zh' || value === 'en';

export function getStoredLang(): Lang | undefined {
  const stored = readStored(STORAGE_KEYS.lang);
  return isLang(stored) ? stored : undefined;
}

export function getInitialLang(): Lang {
  return getStoredLang() ?? DEFAULT_LANG;
}

export function persistLang(lang: Lang) {
  writeStored(STORAGE_KEYS.lang, lang);
}

export const dateLocale = (lang: Lang) => (lang === 'zh' ? 'zh-CN' : 'en-US');
