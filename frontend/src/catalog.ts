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

export const makeDefaultParameters = (item: NodeCatalogItem) =>
  Object.fromEntries(item.parameters.map((p) => [p.name, p.defaultValue]));

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
  }

  return value as NodeCatalogItem[];
};
