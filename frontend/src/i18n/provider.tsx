/**
 * React glue for the i18n layer: `I18nProvider` + `useI18n()`.
 *
 * `t` and `localizeStatus` intentionally keep a stable identity across language switches
 * (they read the current language through a ref). That keeps data-loading callbacks such as
 * `refreshCatalog` from being recreated — and therefore re-fired — on every language toggle,
 * while all consumers still re-render because `lang` / `dateLocale` are part of the context value.
 */
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { dateLocale, getInitialLang, localizeStatus, persistLang, translate, type Lang, type MessageKey, type MessageParams } from './index';

export type I18nContextValue = {
  lang: Lang;
  setLang: (lang: Lang) => void;
  toggleLang: () => void;
  /** Translate a UI message key for the active language. */
  t: (key: MessageKey, params?: MessageParams) => string;
  /** Translate a backend status word (role, health, lifecycle, ...) for the active language. */
  localizeStatus: (value: string | null | undefined) => string | null | undefined;
  /** `zh-CN` / `en-US` for `toLocaleString` / `toLocaleTimeString`. */
  dateLocale: string;
};

/**
 * 默认值 = 按已存储偏好静态解析（与 Provider 未接线时的行为一致）。
 * 这样组件可以在 Provider 之外单独渲染（单元测试、面板独立预览），不会因为缺 Provider 直接抛错；
 * 代价是"忘了挂 Provider"不会报错——由 `main.tsx` 的接线测试与 e2e 覆盖。
 */
const fallbackLang = getInitialLang();
const I18nContext = createContext<I18nContextValue>({
  lang: fallbackLang,
  setLang: () => undefined,
  toggleLang: () => undefined,
  t: (key, params) => translate(fallbackLang, key, params),
  localizeStatus: (value) => localizeStatus(value, fallbackLang),
  dateLocale: dateLocale(fallbackLang)
});

export function I18nProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(() => getInitialLang());
  const langRef = useRef<Lang>(lang);
  langRef.current = lang; // render-phase update: `t()` must be correct in the same render that switches language

  const setLang = useCallback((next: Lang) => {
    setLangState(next);
    persistLang(next);
  }, []);

  const t = useCallback((key: MessageKey, params?: MessageParams) => translate(langRef.current, key, params), []);
  const localizeStatusStable = useCallback((value: string | null | undefined) => localizeStatus(value, langRef.current), []);

  useEffect(() => {
    document.documentElement.lang = lang === 'zh' ? 'zh-CN' : 'en';
  }, [lang]);

  const value = useMemo<I18nContextValue>(() => ({
    lang,
    setLang,
    toggleLang: () => setLang(lang === 'zh' ? 'en' : 'zh'),
    t,
    localizeStatus: localizeStatusStable,
    dateLocale: dateLocale(lang)
  }), [lang, setLang, t, localizeStatusStable]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nContextValue {
  return useContext(I18nContext);
}
