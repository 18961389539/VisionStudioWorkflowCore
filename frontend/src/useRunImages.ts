import { useEffect, useMemo, useState } from 'react';
import type { Edge, Node } from '@xyflow/react';
import type { PortDescriptor, RunResult } from './types';

/** 运行期逐节点图像清单条目（/api/runs/{runId}/images）。 */
export type RunImageEntry = { portName: string; width: number; height: number };
export type RunImageCatalog = { runId: string; images: Record<string, RunImageEntry> };

type Params = {
  result?: RunResult;
  selectedId: string;
  nodes: Node[];
  edges: Edge[];
  nodeLabels: Record<string, string>;
};

/**
 * 图像资源管理（从 Editor 抽出的独立模块）：最终预览与逐节点图像统一以 result.runId 作为代际。
 * - 预览由 result 直接派生：新结果没有预览时旧预览自然消失，不再残留上一次的图；
 * - 目录只在 runId 与当前结果一致时生效：连续运行/切换流程时迟到的旧响应不会被消费；
 * - 目录请求随 runId 换代作废（effect cleanup），旧请求无法覆盖新运行的目录。
 */
export function useRunImages({ result, selectedId, nodes, edges, nodeLabels }: Params) {
  const [runImageCatalog, setRunImageCatalog] = useState<RunImageCatalog>();
  const [imageSource, setImageSource] = useState<'preview' | 'output' | 'input'>('preview');

  const runId = result?.runId;

  // 运行结束后拉取逐节点图像清单（未捕获 / 已过期时为空表，查看器自动回退最终预览）
  useEffect(() => {
    if (!runId) {
      setRunImageCatalog(undefined);
      setImageSource('preview');
      return;
    }
    let cancelled = false;
    void (async () => {
      let catalog: RunImageCatalog = { runId, images: {} };
      try {
        const response = await fetch(`/api/runs/${runId}/images`);
        if (response.ok) {
          const data = await response.json();
          const images: Record<string, RunImageEntry> = {};
          for (const item of data.images ?? []) {
            if (typeof item?.nodeId === 'string')
              images[item.nodeId] = { portName: String(item.portName ?? 'image'), width: Number(item.width ?? 0), height: Number(item.height ?? 0) };
          }
          catalog = { runId, images };
        }
      } catch { /* 网络失败保持空表：查看器回退最终预览 */ }
      if (!cancelled) setRunImageCatalog(catalog);
    })();
    return () => { cancelled = true; };
  }, [runId]);

  // 目录与结果必须属于同一次运行：runId 不一致一律不采用（请求竞态的双重保险）
  const catalog = useMemo(
    () => (result && runImageCatalog && runImageCatalog.runId === result.runId ? runImageCatalog : undefined),
    [result, runImageCatalog]);

  // 最终预览 URL 派生自结果：无预览的新结果不会保留旧图（cache-busting 时间戳随结果换代）
  const previewUrl = useMemo(
    () => (result && result.previewAvailable ? `/api/runs/${result.runId}/preview?t=${Date.now()}` : undefined),
    [result]);

  // 输入图像解析：选中节点的 Image 输入端所连的上游节点，其输出图像即本节点的输入
  const imageInputSourceId = useMemo(() => {
    const node = nodes.find((x) => x.id === selectedId);
    if (!node) return undefined;
    const imagePorts = new Set(((node.data?.inputs ?? []) as PortDescriptor[])
      .filter((port) => port.dataType === 'Image').map((port) => port.name));
    if (imagePorts.size === 0) return undefined;
    const edge = edges.find((item) => item.target === selectedId && item.targetHandle && imagePorts.has(item.targetHandle));
    return edge?.source;
  }, [edges, nodes, selectedId]);

  const selectedOutputAvailable = Boolean(selectedId && catalog?.images[selectedId]);
  const selectedInputAvailable = Boolean(imageInputSourceId && catalog?.images[imageInputSourceId]);

  // 查看器图像来源解析：最终预览 / 选中节点输出 / 选中节点输入（不可用时回退预览并明示原因）
  const imageViewerSource = useMemo(() => {
    const label = (id: string) => nodeLabels[id] ?? id;
    const nodeImage = (nodeId: string) => {
      const entry = catalog?.images[nodeId];
      return entry && catalog
        ? { url: `/api/runs/${catalog.runId}/nodes/${nodeId}/image`, width: entry.width, height: entry.height }
        : undefined;
    };
    if (imageSource === 'output' && selectedId) {
      const resolved = nodeImage(selectedId);
      if (resolved) return { ...resolved, label: `输出 · ${label(selectedId)}`, nodeId: selectedId as string | undefined };
      if (catalog || result) return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: `最终预览（${label(selectedId)} 无输出图像）`, nodeId: undefined as string | undefined };
    }
    if (imageSource === 'input') {
      const resolved = imageInputSourceId ? nodeImage(imageInputSourceId) : undefined;
      if (resolved && imageInputSourceId) return { ...resolved, label: `输入 ← ${label(imageInputSourceId)}`, nodeId: imageInputSourceId as string | undefined };
      if (catalog || result) return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: selectedId ? `最终预览（${label(selectedId)} 无输入图像）` : '最终预览', nodeId: undefined as string | undefined };
    }
    return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: '最终预览', nodeId: undefined as string | undefined };
  }, [catalog, imageInputSourceId, imageSource, nodeLabels, previewUrl, result, selectedId]);

  // 查看节点图像时只显示该节点自己的覆盖层，避免与上游/下游标注混淆
  const viewerOverlays = useMemo(
    () => (imageViewerSource.nodeId ? (result?.overlays ?? []).filter((overlay) => overlay.nodeId === imageViewerSource.nodeId) : (result?.overlays ?? [])),
    [imageViewerSource.nodeId, result]);

  // 点击节点即检查其中间结果：选中节点有输出图像时自动切到“输出”；“输入”模式在可解析时保留以便上下游对比
  useEffect(() => {
    if (!selectedId || !catalog?.images[selectedId]) return;
    setImageSource((current) => (current === 'input' && imageInputSourceId ? 'input' : 'output'));
  }, [catalog, imageInputSourceId, selectedId]);

  return {
    runImageCatalog: catalog,
    imageSource,
    setImageSource,
    imageViewerSource,
    viewerOverlays,
    selectedOutputAvailable,
    selectedInputAvailable
  };
}
