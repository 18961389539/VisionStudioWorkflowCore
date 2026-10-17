import { useEffect, useMemo, useState } from 'react';
import type { Edge, Node } from '@xyflow/react';
import type { PortDescriptor, RunResult } from './types';

/** 运行期逐节点、逐图像端口的图像清单条目（/api/runs/{runId}/images）。 */
export type RunImageEntry = { portName: string; width: number; height: number };
export type RunImageCatalog = { runId: string; images: Record<string, Record<string, RunImageEntry>> };

/** 有明确端口时只返回该端口，避免把 roiMask 等输入误显示成节点主图。 */
export function runImageEntryForPort(catalog: RunImageCatalog | undefined, nodeId: string | undefined, portName?: string | null) {
  if (!catalog || !nodeId) return undefined;
  const outputs = catalog.images[nodeId];
  if (!outputs) return undefined;
  if (portName) return Object.values(outputs).find((entry) => entry.portName.toLowerCase() === portName.toLowerCase());
  return Object.values(outputs).find((entry) => entry.portName.toLowerCase() === 'image') ?? Object.values(outputs)[0];
}

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
  const [requestedOutputPortName, setRequestedOutputPortName] = useState<string>();
  const [catalogRequest, setCatalogRequest] = useState<{ runId: string; loading: boolean; error?: string }>();
  const [catalogAttempt, setCatalogAttempt] = useState(0);

  const runId = result?.runId;

  // 运行结束后拉取逐节点图像清单（未捕获 / 已过期时为空表，查看器自动回退最终预览）
  useEffect(() => {
    if (!runId) {
      setRunImageCatalog(undefined);
      setImageSource('preview');
      setRequestedOutputPortName(undefined);
      setCatalogRequest(undefined);
      return;
    }
    let cancelled = false;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 15_000);
    setCatalogRequest({ runId, loading: true });
    void (async () => {
      let catalog: RunImageCatalog = { runId, images: {} };
      let error: string | undefined;
      try {
        const response = await fetch(`/api/runs/${runId}/images`, { signal: controller.signal });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const data = await response.json();
        const images: RunImageCatalog['images'] = Object.create(null) as RunImageCatalog['images'];
        for (const item of data.images ?? []) {
          if (typeof item?.nodeId !== 'string') continue;
          const portName = String(item.portName ?? 'image');
          const outputs = images[item.nodeId] ?? (images[item.nodeId] = Object.create(null) as Record<string, RunImageEntry>);
          outputs[portName] = { portName, width: Number(item.width ?? 0), height: Number(item.height ?? 0) };
        }
        catalog = { runId, images };
      } catch (reason) {
        error = controller.signal.aborted ? '请求超时' : reason instanceof Error ? reason.message : '网络请求失败';
      } finally { window.clearTimeout(timeout); }
      if (!cancelled) {
        setRunImageCatalog(catalog);
        setCatalogRequest({ runId, loading: false, error });
      }
    })();
    return () => { cancelled = true; controller.abort(); window.clearTimeout(timeout); };
  }, [runId, catalogAttempt]);

  const imageCatalogLoading = Boolean(runId && (catalogRequest?.runId !== runId || catalogRequest.loading));
  const imageCatalogError = catalogRequest?.runId === runId ? catalogRequest?.error : undefined;

  // 目录与结果必须属于同一次运行：runId 不一致一律不采用（请求竞态的双重保险）
  const catalog = useMemo(
    () => (result && runImageCatalog && runImageCatalog.runId === result.runId ? runImageCatalog : undefined),
    [result, runImageCatalog]);

  // 最终预览 URL 派生自结果：无预览的新结果不会保留旧图（cache-busting 时间戳随结果换代）
  const previewUrl = useMemo(
    () => (result && result.previewAvailable ? `/api/runs/${result.runId}/preview?t=${Date.now()}` : undefined),
    [result]);

  // 节点输入图像精确解析为边连接的上游输出端口，而不只是上游节点的默认图像端口。
  const imageInputConnection = useMemo(() => {
    const node = nodes.find((x) => x.id === selectedId);
    if (!node) return undefined;
    const imagePorts = new Set(((node.data?.inputs ?? []) as PortDescriptor[])
      .filter((port) => port.dataType === 'Image').map((port) => port.name));
    if (imagePorts.size === 0) return undefined;
    const edge = edges.find((item) => item.target === selectedId && item.targetHandle && imagePorts.has(item.targetHandle));
    return edge ? { nodeId: edge.source, portName: edge.sourceHandle } : undefined;
  }, [edges, nodes, selectedId]);

  // 切换节点或运行批次时回到主图，端口选择只影响当前节点的输出检查。
  useEffect(() => { setRequestedOutputPortName(undefined); }, [selectedId, runId]);

  const selectedOutputPorts = useMemo(
    () => Object.values(catalog?.images[selectedId] ?? {}),
    [catalog, selectedId]);
  const outputEntry = runImageEntryForPort(catalog, selectedId, requestedOutputPortName);
  const imageOutputPortName = outputEntry?.portName;
  const selectedOutputAvailable = selectedOutputPorts.length > 0;
  const selectedInputAvailable = Boolean(runImageEntryForPort(catalog, imageInputConnection?.nodeId, imageInputConnection?.portName));

  // 查看器图像来源解析：最终预览 / 选中节点输出 / 选中节点的精确输入端口。
  const imageViewerSource = useMemo(() => {
    const label = (id: string) => nodeLabels[id] ?? id;
    const nodeImage = (nodeId: string, portName?: string | null) => {
      const entry = runImageEntryForPort(catalog, nodeId, portName);
      return entry && catalog
        ? { url: `/api/runs/${catalog.runId}/nodes/${nodeId}/image?portName=${encodeURIComponent(entry.portName)}`, width: entry.width, height: entry.height, portName: entry.portName }
        : undefined;
    };
    if (imageSource === 'output' && selectedId) {
      const resolved = nodeImage(selectedId, requestedOutputPortName);
      if (resolved) return { ...resolved, label: `输出 · ${label(selectedId)}${resolved.portName === 'image' ? '' : ` · ${resolved.portName}`}`, nodeId: selectedId as string | undefined };
      if (catalog || result) return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: `最终预览（${imageCatalogLoading ? '节点图像加载中' : imageCatalogError ? '节点图像暂不可用' : `${label(selectedId)} 无输出图像`}）`, nodeId: undefined as string | undefined };
    }
    if (imageSource === 'input') {
      const resolved = imageInputConnection ? nodeImage(imageInputConnection.nodeId, imageInputConnection.portName) : undefined;
      if (resolved && imageInputConnection) return { ...resolved, label: `输入 ← ${label(imageInputConnection.nodeId)}${resolved.portName === 'image' ? '' : ` · ${resolved.portName}`}`, nodeId: imageInputConnection.nodeId as string | undefined };
      if (catalog || result) return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: selectedId ? `最终预览（${imageCatalogLoading ? '节点图像加载中' : imageCatalogError ? '节点图像暂不可用' : `${label(selectedId)} 无输入图像`}）` : '最终预览', nodeId: undefined as string | undefined };
    }
    return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: '最终预览', nodeId: undefined as string | undefined };
  }, [catalog, imageInputConnection, imageSource, nodeLabels, previewUrl, result, selectedId, imageCatalogLoading, imageCatalogError, requestedOutputPortName]);

  // 查看节点图像时只显示该节点自己的覆盖层，避免与上游/下游标注混淆
  const viewerOverlays = useMemo(
    () => (imageViewerSource.nodeId ? (result?.overlays ?? []).filter((overlay) => overlay.nodeId === imageViewerSource.nodeId) : (result?.overlays ?? [])),
    [imageViewerSource.nodeId, result]);

  // 点击节点即检查其中间结果：选中节点有输出图像时自动切到“输出”；“输入”模式在可解析时保留以便上下游对比
  useEffect(() => {
    if (!selectedId || !selectedOutputAvailable) return;
    setImageSource((current) => (current === 'input' && imageInputConnection ? 'input' : 'output'));
  }, [imageInputConnection, selectedId, selectedOutputAvailable]);

  return {
    runImageCatalog: catalog,
    imageCatalogLoading,
    imageCatalogError,
    retryImageCatalog: () => setCatalogAttempt((attempt) => attempt + 1),
    imageSource,
    setImageSource,
    imageViewerSource,
    viewerOverlays,
    selectedOutputAvailable,
    selectedInputAvailable,
    imageOutputPorts: selectedOutputPorts,
    imageOutputPortName,
    setImageOutputPortName: setRequestedOutputPortName
  };
}
