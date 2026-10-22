/**
 * i18n entry point — language primitives, message lookup and catalog localization.
 *
 * Usage inside components: `const { t, lang, localizeStatus, dateLocale } = useI18n();`
 * from './i18n/provider' (or '../i18n/provider').
 */
import { messages, type MessageKey, type MessageParams } from './messages';
import { dateLocale, getInitialLang, persistLang, type Lang } from './lang';

export { messages };
export type { MessageKey, MessageParams, Lang };
export { dateLocale, getInitialLang, persistLang };
export { localizeCatalog, localizeStatus } from './catalog';

/**
 * Resolve a message key for a language. Missing keys fall back to the key itself so a gap is
 * immediately visible instead of rendering an empty string.
 * `{name}` placeholders are replaced from `params`; unknown placeholders are left untouched.
 */
export function translate(lang: Lang, key: MessageKey, params?: MessageParams): string {
  const entry: { zh: string; en: string } | undefined = messages[key];
  const template = entry ? entry[lang] : String(key);
  if (!params) return template;
  return template.replace(/\{(\w+)\}/g, (match, name: string) => (name in params ? String(params[name]) : match));
}
