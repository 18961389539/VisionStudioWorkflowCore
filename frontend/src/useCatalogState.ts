/**
 * 节点目录加载（巨型组件拆分，2026-11-13）：运行时目录 / 离线快照回退与插件清单。
 * 逐行搬运自 App 的 Editor 组件；状态与刷新入口语义不变。
 */
import { useCallback, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import { message } from 'antd';
import type { Node } from '@xyflow/react';
import { apiFetch } from './apiClient';
import { apiUrl, plugins as pluginEndpoints } from './endpoints';
import { loadFallbackCatalog, validateCatalog } from './catalog';
import { localizeCatalog } from './i18n';
import type { Lang } from './i18n/lang';
import { hydrateNodesFromCatalog } from './appEditorShared';
import type { NodeCatalogItem, PluginLoadInfo } from './types';

type MessageApi = ReturnType<typeof message.useMessage>[0];

export function useCatalogState(options: {
  lang: Lang;
  langRef: { current: Lang };
  messageApi: MessageApi;
  setNodes: Dispatch<SetStateAction<Node[]>>;
  setPlugins: Dispatch<SetStateAction<PluginLoadInfo[]>>;
}) {
  const { lang, langRef, messageApi, setNodes, setPlugins } = options;
  const [rawCatalog, setRawCatalog] = useState<NodeCatalogItem[] | undefined>(undefined);
  // 2026-11-13：生成快照不再静态进主包。首屏先渲染未灌目录的节点，运行目录（通常本地毫秒级）
  // 或后端不可用时的动态 fallback 到达后再 hydrate——网络总量省掉约 1 MB 的重复目录。
  const catalog = useMemo(
    () => localizeCatalog(rawCatalog ?? [], lang),
    [rawCatalog, lang]
  );
  const [catalogSource, setCatalogSource] = useState<'runtime' | 'fallback'>('fallback');
  const [catalogSchemaVersion, setCatalogSchemaVersion] = useState(0);
  const [catalogError, setCatalogError] = useState<string>();

  const catalogGenerationRef = useRef(0);
  const refreshCatalog = useCallback(async () => {
    const generation = ++catalogGenerationRef.current;
    try {
      const response = await apiFetch(apiUrl('catalog'));
      if (!response.ok) throw new Error(`Catalog request failed (${response.status})`);
      const raw = validateCatalog(await response.json());
      if (generation !== catalogGenerationRef.current) return;
      setRawCatalog(raw);
      // 只在目录真的来自后端时重命名画布节点（沿用既有行为）；语言切换不触发本函数。
      setNodes((current) => hydrateNodesFromCatalog(current, localizeCatalog(raw, langRef.current)));
      setCatalogSource('runtime');
      setCatalogSchemaVersion(Number(response.headers.get('X-VisionStudio-Catalog-Schema') ?? 0));
      setCatalogError(undefined);
    } catch (error) {
      if (generation !== catalogGenerationRef.current) return;
      const reason = error instanceof Error ? error.message : '节点目录不可用';
      setCatalogError(reason);
      // 只有后端真的不可用时才付这 1.65 MB：按需加载生成快照并灌入画布。
      let fallbackLoaded = false;
      try {
        const fallback = await loadFallbackCatalog();
        if (generation !== catalogGenerationRef.current) return;
        setRawCatalog(fallback.items);
        setNodes((current) => hydrateNodesFromCatalog(current, localizeCatalog(fallback.items, langRef.current)));
        setCatalogSource('fallback');
        setCatalogSchemaVersion(fallback.schemaVersion);
        fallbackLoaded = true;
      } catch {
        // 快照也加载失败：保留空目录，画布仍可用（节点退化为类型键显示）。
      }
      messageApi.warning(fallbackLoaded
        ? '后端 Catalog 不可用；已加载离线目录快照'
        : '后端 Catalog 不可用；离线目录快照也加载失败，画布以空目录运行');
    }

    apiFetch(pluginEndpoints.list)
      .then((r) => (r.ok ? r.json() : []))
      // 契约漂移防御：网关错误体、代理返回的 JSON 对象都会走到这里。
      // 非数组一旦进入 plugins 状态，工具栏标签的 plugins.filter 就会抛错，
      // 而它在面板级错误边界之外——整棵编辑器会被顶层边界替换成错误页。
      .then((data) => setPlugins(Array.isArray(data) ? data : []))
      .catch(() => setPlugins([]));
  }, [langRef, messageApi, setNodes, setPlugins]);

  return { catalog, catalogSource, catalogSchemaVersion, catalogError, refreshCatalog };
}
