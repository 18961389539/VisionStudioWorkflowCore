import { useEffect, useMemo, useState } from 'react';
import type { Edge, Node } from '@xyflow/react';
import type { PortDescriptor, RunResult } from './types';
import { portDisplayName } from './i18n/catalog';
import { responseErrorMessage } from './apiErrors';

/** 运行期逐节点、逐图像端口的图像清单条目（/api/runs/{runId}/images）。 */
export type RunImageEntry = { portName: string; width: number; height: number };
export type RunImageCatalog = { runId: string; images: Record<string, Record<string, RunImageEntry>> };

type CatalogRequest = { runId: string; attempt: number } & (
  | { status: 'loading' }
  | { status: 'ready'; catalog: RunImageCatalog }
  | { status: 'error'; error: string }
);

const object = (value: unknown): value is Record<string, unknown> => Boolean(value && typeof value === 'object' && !Array.isArray(value));
const nonBlank = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0;
const imageDimension = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value) && value > 0;

function parseImageCatalog(data: unknown, runId: string): RunImageCatalog {
  if (!object(data) || !Array.isArray(data.images)) throw new Error('响应不是有效的节点图像目录');
  if ('runId' in data && data.runId !== runId) throw new Error('节点图像目录与当前运行不匹配');
  const images: RunImageCatalog['images'] = Object.create(null);
  for (const item of data.images) {
    if (!object(item) || !nonBlank(item.nodeId) || !imageDimension(item.width) || !imageDimension(item.height)) {
      throw new Error('节点图像目录包含无效标识或图像尺寸');
    }
    const portName = item.portName ?? 'image';
    if (!nonBlank(portName)) throw new Error('节点图像目录包含无效端口名称');
    const outputs: Record<string, RunImageEntry> = images[item.nodeId] ?? (images[item.nodeId] = Object.create(null));
    if (Object.values(outputs).some((entry) => entry.portName.toLowerCase() === portName.toLowerCase())) {
      throw new Error(`节点图像目录包含重复端口：${item.nodeId} / ${portName}`);
    }
    outputs[portName] = { portName, width: item.width, height: item.height };
  }
  return { runId, images };
}

export function runImagePortLabel(nodes: Node[], nodeId: string | undefined, portName: string): string {
  const outputs = nodes.find((node) => node.id === nodeId)?.data?.outputs;
  const port = (Array.isArray(outputs) ? outputs : [] as PortDescriptor[])
    .find((candidate) => candidate.name.toLowerCase() === portName.toLowerCase());
  return portDisplayName(portName, port?.label);
}

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
 * - 目录只在 runId 和请求批次均一致且响应有效时生效；
 * - 切换、重试、超时和卸载均使旧请求作废，失败保留最终预览而不伪造空目录。
 */
export function useRunImages({ result, selectedId, nodes, edges, nodeLabels }: Params) {
  const [imageSource, setImageSource] = useState<'preview' | 'output' | 'input'>('preview');
  const [requestedOutputPortName, setRequestedOutputPortName] = useState<string>();
  const [catalogRequest, setCatalogRequest] = useState<CatalogRequest>();
  const [catalogAttempt, setCatalogAttempt] = useState(0);

  const runId = result?.runId;

  // 运行结束后拉取逐节点图像清单（未捕获 / 已过期时为空表，查看器自动回退最终预览）
  useEffect(() => {
    if (!runId) {
      setImageSource('preview');
      setRequestedOutputPortName(undefined);
      setCatalogRequest(undefined);
      return;
    }
    let cancelled = false;
    const controller = new AbortController();
    const request = { runId, attempt: catalogAttempt };
    const timeout = window.setTimeout(() => {
      controller.abort();
      if (!cancelled) setCatalogRequest({ ...request, status: 'error', error: '请求超时（15 秒）' });
    }, 15_000);
    setCatalogRequest({ ...request, status: 'loading' });
    void (async () => {
      try {
        const response = await fetch(`/api/runs/${encodeURIComponent(runId)}/images`, { signal: controller.signal });
        if (cancelled || controller.signal.aborted) return;
        if (!response.ok) throw new Error(await responseErrorMessage(response, `HTTP ${response.status}`));
        const data: unknown = await response.json();
        if (cancelled || controller.signal.aborted) return;
        setCatalogRequest({ ...request, status: 'ready', catalog: parseImageCatalog(data, runId) });
      } catch (reason) {
        if (!cancelled && !controller.signal.aborted) {
          setCatalogRequest({ ...request, status: 'error', error: reason instanceof Error ? reason.message : '网络请求失败' });
        }
      } finally { window.clearTimeout(timeout); }
    })();
    return () => { cancelled = true; controller.abort(); window.clearTimeout(timeout); };
  }, [runId, catalogAttempt]);

  const currentRequest = catalogRequest && catalogRequest.runId === runId && catalogRequest.attempt === catalogAttempt ? catalogRequest : undefined;
  const imageCatalogLoading = Boolean(runId && (!currentRequest || currentRequest.status === 'loading'));
  const imageCatalogError = currentRequest?.status === 'error' ? currentRequest.error : undefined;
  const catalog = currentRequest?.status === 'ready' ? currentRequest.catalog : undefined;

  // 最终预览 URL 派生自结果：无预览的新结果不会保留旧图（cache-busting 时间戳随结果换代）
  const previewUrl = useMemo(
    () => (result && result.previewAvailable ? `/api/runs/${encodeURIComponent(result.runId)}/preview?t=${Date.now()}` : undefined),
    [result]);

  // 节点输入图像精确解析为边连接的上游输出端口，而不只是上游节点的默认图像端口。
  const imageInputConnection = useMemo(() => {
    const node = nodes.find((x) => x.id === selectedId);
    if (!node) return undefined;
    const imagePorts = new Set(((node.data?.inputs ?? []) as PortDescriptor[])
      .filter((port) => ['image', 'domainimage'].includes(String(port.dataType ?? '').toLowerCase()))
      .map((port) => port.name.toLowerCase()));
    if (imagePorts.size === 0) return undefined;
    const edge = edges.find((item) => item.target === selectedId && item.targetHandle
      && imagePorts.has(item.targetHandle.toLowerCase()));
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
        ? { url: `/api/runs/${encodeURIComponent(catalog.runId)}/nodes/${encodeURIComponent(nodeId)}/image?portName=${encodeURIComponent(entry.portName)}`, width: entry.width, height: entry.height, portName: entry.portName }
        : undefined;
    };
    if (imageSource === 'output' && selectedId) {
      const resolved = nodeImage(selectedId, requestedOutputPortName);
      if (resolved) return { ...resolved, label: `输出 · ${label(selectedId)}${resolved.portName === 'image' ? '' : ` · ${runImagePortLabel(nodes, selectedId, resolved.portName)}`}`, nodeId: selectedId as string | undefined };
      if (catalog || result) return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: `最终预览（${imageCatalogLoading ? '节点图像加载中' : imageCatalogError ? '节点图像暂不可用' : `${label(selectedId)} 无输出图像`}）`, nodeId: undefined as string | undefined };
    }
    if (imageSource === 'input') {
      const resolved = imageInputConnection ? nodeImage(imageInputConnection.nodeId, imageInputConnection.portName) : undefined;
      if (resolved && imageInputConnection) return { ...resolved, label: `输入 ← ${label(imageInputConnection.nodeId)}${resolved.portName === 'image' ? '' : ` · ${runImagePortLabel(nodes, imageInputConnection.nodeId, resolved.portName)}`}`, nodeId: imageInputConnection.nodeId as string | undefined };
      if (catalog || result) return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: selectedId ? `最终预览（${imageCatalogLoading ? '节点图像加载中' : imageCatalogError ? '节点图像暂不可用' : `${label(selectedId)} 无输入图像`}）` : '最终预览', nodeId: undefined as string | undefined };
    }
    return { url: previewUrl, width: result?.previewWidth, height: result?.previewHeight, label: '最终预览', nodeId: undefined as string | undefined };
  }, [catalog, imageInputConnection, imageSource, nodeLabels, nodes, previewUrl, result, selectedId, imageCatalogLoading, imageCatalogError, requestedOutputPortName]);

  // 查看节点图像时只显示该节点自己的覆盖层，避免与上游/下游标注混淆
  const viewerOverlays = useMemo(
    () => (imageViewerSource.nodeId ? (result?.overlays ?? []).filter((overlay) => overlay.nodeId === imageViewerSource.nodeId) : (result?.overlays ?? [])),
    [imageViewerSource.nodeId, result]);

  // 点击节点即检查其中间结果：选中节点有输出图像时自动切到“输出”；“输入”模式在可解析时保留以便上下游对比
  useEffect(() => {
    if (!selectedId || !selectedOutputAvailable) return;
    setImageSource((current) => (current === 'input' && selectedInputAvailable ? 'input' : 'output'));
  }, [selectedId, selectedOutputAvailable, selectedInputAvailable]);

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
