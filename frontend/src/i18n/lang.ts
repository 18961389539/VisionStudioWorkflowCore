/**
 * Language runtime primitives for the VisionStudio UI.
 *
 * Chinese stays the deterministic product default (matches `index.html lang="zh-CN"`).
 * A stored user preference always wins, so switching languages is sticky across sessions.
 */
export type Lang = 'zh' | 'en';

export const LANGS: Lang[] = ['zh', 'en'];
export const DEFAULT_LANG: Lang = 'zh';
export const LANG_STORAGE_KEY = 'visionstudio.lang';

const isLang = (value: unknown): value is Lang => value === 'zh' || value === 'en';

export function getStoredLang(): Lang | undefined {
  try {
    const stored = window.localStorage.getItem(LANG_STORAGE_KEY);
    return isLang(stored) ? stored : undefined;
  } catch {
    return undefined;
  }
}

export function getInitialLang(): Lang {
  return getStoredLang() ?? DEFAULT_LANG;
}

export function persistLang(lang: Lang) {
  try { window.localStorage.setItem(LANG_STORAGE_KEY, lang); } catch { /* storage may be blocked */ }
}

export const dateLocale = (lang: Lang) => (lang === 'zh' ? 'zh-CN' : 'en-US');
