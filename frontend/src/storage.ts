/**
 * 浏览器本地偏好的统一入口（2026-10-26）。
 *
 * 此前 12 个键散落在 9 个文件里，每个调用点各自写一遍 try/catch——
 * 结果是：键名靠字符串约定（拼错不会报错）、隐私模式下行为不一致、要加版本/迁移时无处下手。
 * 这里集中两件事：
 *  1. `STORAGE_KEYS` 是唯一的键名清单（含命名空间前缀），新增偏好必须在此登记；
 *  2. 读写统一吞掉 storage 不可用（隐私模式、配额满）异常，调用方不再关心。
 *
 * 注意：这里只承载"界面偏好"。业务数据（草稿）仍在 draft.ts 里，因为它有自己的过期与校验语义。
 */

export const STORAGE_PREFIX = 'visionstudio.';

export const STORAGE_KEYS = {
  themeMode: 'theme-mode',
  lang: 'lang',
  layout: 'layout',
  nodeCompact: 'node-compact',
  recentNodeTypes: 'recent-node-types',
  minimap: 'minimap',
  viewerPixelated: 'viewer-pixelated',
  toolboxCollapsed: 'toolbox-collapsed',
  toolboxFavorites: 'toolbox-favorites',
  autoLoginSkipped: 'auto-login-skipped',
  draftWorkflow: 'draft.workflow'
} as const;

export type StorageKey = (typeof STORAGE_KEYS)[keyof typeof STORAGE_KEYS];

const fullKey = (key: StorageKey) => `${STORAGE_PREFIX}${key}`;

/** 读字符串；storage 不可用时返回 fallback。 */
export function readStored(key: StorageKey, fallback?: string): string | undefined {
  try {
    const value = window.localStorage.getItem(fullKey(key));
    return value ?? fallback;
  } catch {
    return fallback;
  }
}

/** 写字符串；storage 不可用（隐私模式/配额）时静默失败。 */
export function writeStored(key: StorageKey, value: string): void {
  try { window.localStorage.setItem(fullKey(key), value); } catch { /* 忽略存储失败 */ }
}

export function removeStored(key: StorageKey): void {
  try { window.localStorage.removeItem(fullKey(key)); } catch { /* 忽略 */ }
}

/**
 * 读 JSON 并校验形状：解析失败或形状不符时返回 fallback。
 * `parse` 必须做完整校验（例如 `Array.isArray` + 元素类型），不要直接 `as T`——
 * 旧版本写进去的数据形状可能已经变了。
 */
export function readStoredJson<T>(key: StorageKey, parse: (value: unknown) => T | undefined, fallback: T): T {
  const raw = readStored(key);
  if (!raw) return fallback;
  try {
    const parsed = parse(JSON.parse(raw));
    return parsed === undefined ? fallback : parsed;
  } catch {
    return fallback;
  }
}

export function writeStoredJson(key: StorageKey, value: unknown): void {
  try { writeStored(key, JSON.stringify(value)); } catch { /* 忽略序列化失败 */ }
}
