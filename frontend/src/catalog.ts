import generatedCatalog from './generated/catalog.generated.json';
import type { NodeCatalogItem } from './types';
import { localizeCatalog } from './i18n';

type GeneratedCatalogDocument = {
  schemaVersion: number;
  hash: string;
  items: NodeCatalogItem[];
};

const document = generatedCatalog as unknown as GeneratedCatalogDocument;

/**
 * Offline/bootstrap-only catalog generated from backend BuiltInNodeCatalog.
 * Runtime /api/catalog is authoritative and may add plugin nodes.
 *
 * `fallbackCatalogRaw` keeps the backend's original English form so the language switch can
 * re-localize it; `fallbackCatalog` is the legacy Chinese-localized alias kept for callers
 * that have not migrated yet.
 */
export const fallbackCatalogRaw: NodeCatalogItem[] = document.items;
export const fallbackCatalog: NodeCatalogItem[] = localizeCatalog(document.items, 'zh');
export const fallbackCatalogHash = document.hash;
export const fallbackCatalogSchemaVersion = document.schemaVersion;

/** 配置面板可见参数：internal 参数由画布/宿主维护，不出现在表单与默认值里。 */
export const editableParameters = (item: NodeCatalogItem) => item.parameters.filter((parameter) => !parameter.internal);

export const catalogNodeData = (item: NodeCatalogItem) => ({
  inputs: item.inputs,
  outputs: item.outputs,
  parameterDescriptors: item.parameters
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
