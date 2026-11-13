import type { NodeCatalogItem } from './types';
import { localizeCatalog } from './i18n';

export type GeneratedCatalogDocument = {
  schemaVersion: number;
  hash: string;
  items: NodeCatalogItem[];
};

/**
 * Offline/bootstrap-only catalog generated from backend BuiltInNodeCatalog.
 * Runtime /api/catalog is authoritative and may add plugin nodes.
 *
 * 2026-11-13：词表文件不再静态 import——1.65 MB 的目录快照占主包约 1 MB，
 * 而正常路径始终由 /api/catalog 提供。fallback 只在后端不可用时按需加载
 * （见 `loadFallbackCatalog`），首屏不再下载这份重复数据。
 *
 * `fallbackCatalogRaw` keeps the backend's original English form so the language switch can
 * re-localize it; `fallbackCatalog` is the legacy Chinese-localized alias kept for callers
 * that have not migrated yet.
 */
let document: GeneratedCatalogDocument | undefined;
export let fallbackCatalogRaw: NodeCatalogItem[] = [];
export let fallbackCatalogHash = '';
export let fallbackCatalogSchemaVersion = 0;
/**
 * 旧的中文目录别名（测试/未迁移调用方专用）。
 * 安装时按当前词表本地化；生产路径需要时用 `localizeCatalog(fallbackCatalogRaw, lang)`。
 */
export let fallbackCatalog: NodeCatalogItem[] = [];

/** 供测试 setup 同步安装生成快照（vitest 的 setupFiles 先于测试模块求值）。 */
export function installFallbackCatalog(next: GeneratedCatalogDocument): void {
  document = next;
  fallbackCatalogRaw = next.items;
  fallbackCatalog = localizeCatalog(next.items, 'zh');
  fallbackCatalogHash = next.hash;
  fallbackCatalogSchemaVersion = next.schemaVersion;
}

let pending: Promise<GeneratedCatalogDocument> | undefined;

/** 按需加载离线目录快照；重复调用共享同一次动态 import。 */
export function loadFallbackCatalog(): Promise<GeneratedCatalogDocument> {
  if (document) return Promise.resolve(document);
  pending ??= import('./generated/catalog.generated.json').then((module) => {
    installFallbackCatalog(module.default as unknown as GeneratedCatalogDocument);
    return document!;
  });
  return pending;
}

/** 配置面板可见参数：internal 参数由画布/宿主维护，不出现在表单与默认值里。 */
export const editableParameters = (item: NodeCatalogItem) => item.parameters.filter((parameter) => !parameter.internal);

export const catalogNodeData = (item: NodeCatalogItem) => ({
  inputs: item.inputs,
  outputs: item.outputs,
  parameterDescriptors: item.parameters,
  // 坐标帧声明随节点数据下发：画布的 ROI 沿革不再只靠类型集合猜测（插件也能声明）。
  frames: item.frames ?? null
});

export const makeDefaultParameters = (item: NodeCatalogItem) =>
  Object.fromEntries(editableParameters(item)
    .filter((parameter) => parameter.defaultValue !== undefined)
    .map((parameter) => [parameter.name, parameter.defaultValue]));

export const validateCatalog = (value: unknown): NodeCatalogItem[] => {
  if (!Array.isArray(value) || value.length === 0) throw new Error('Catalog is empty or invalid.');

  const seen = new Set<string>();
  for (const item of value as NodeCatalogItem[]) {
    if (!item || typeof item.type !== 'string' || typeof item.displayName !== 'string' || typeof item.category !== 'string') {
      throw new Error('Catalog item is missing identity metadata.');
    }
    if (!Array.isArray(item.inputs) || !Array.isArray(item.outputs) || !Array.isArray(item.parameters)) {
      throw new Error(`Catalog item '${item.type}' has invalid ports/parameters.`);
    }
    const key = item.type.toLowerCase();
    if (seen.has(key)) throw new Error(`Duplicate catalog node type '${item.type}'.`);
    seen.add(key);
    const parameterNames = new Set<string>();
    for (const parameter of item.parameters) {
      const name = parameter.name.toLowerCase();
      if (parameterNames.has(name)) throw new Error(`Catalog node '${item.type}' has duplicate parameter '${parameter.name}'.`);
      parameterNames.add(name);
    }
  }

  return value as NodeCatalogItem[];
};
