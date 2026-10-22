/**
 * NodeConfigurationDialog 的纯逻辑工具函数。
 * 这些函数不依赖 React 组件树，只操作图数据（nodes/edges）、目录项与类型。
 */
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, NodeFrameRule, ParameterDescriptor, VisionRoi, VisionValueSnapshot } from '../types';
import type { MeasurementGeometry } from './ImageViewer';
import type { NodeParameters } from '../nodeConfigurationState';
import { catalogItemChangesImageFrame, declaredFrameChange, legacyFrameChangingNodeTypes } from '../frameSemantics';

export type { MeasurementGeometry };

export function imageFrameChanges(node: Node | undefined) {
  const data = node?.data as {
    typeKey?: unknown;
    parameters?: NodeParameters;
    frames?: NodeFrameRule[] | null;
    parameterDescriptors?: ParameterDescriptor[];
  } | undefined;
  const type = String(data?.typeKey ?? '').toLowerCase();

  // 目录声明优先：内置节点与插件走同一条路径，宿主不再按类型名猜测空间语义。
  const declared = declaredFrameChange(data?.frames, data?.parameters, data?.parameterDescriptors);
  if (declared !== undefined) return declared;

  // 旧目录/旧节点数据没有 frames 时的兼容回退（迁移期保留）。
  const parameters = data?.parameters;
  return legacyFrameChangingNodeTypes.has(type) || type === 'jlvision.worldplane'
    || type === 'jlvision.restoration' && ['psfMotion', 'psfDefocus'].includes(String(parameters?.operator))
    || type === 'jlvision.imageop'
    && ['cropPart', 'cropRectangle2', 'cropDomainRel', 'rotateImage', 'zoomImageFactor', 'addBorder', 'changeFormat']
      .includes(String(parameters?.operator ?? 'abs'));
}

export function inputEdge(edges: Edge[], nodeId: string, portName: string) {
  const expected = portName.toLowerCase();
  return edges.find((edge) => edge.target === nodeId && edge.targetHandle?.toLowerCase() === expected);
}

export function hasInputEdge(edges: Edge[], nodeId: string, portName: string) {
  return Boolean(inputEdge(edges, nodeId, portName));
}

export function inputSnapshotValue(inputs: Record<string, VisionValueSnapshot> | null | undefined, portName: string) {
  const expected = portName.toLowerCase();
  return Object.entries(inputs ?? {}).find(([name]) => name.toLowerCase() === expected)?.[1].value;
}

export function connectedRoiGeometry(value: unknown): VisionRoi | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const roi = value as Record<string, unknown>;
  const finiteCoordinate = (item: unknown): item is number => typeof item === 'number' && Number.isFinite(item) && Math.abs(item) <= 10_000_000;
  const point = (item: unknown) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) return undefined;
    const coordinates = item as Record<string, unknown>;
    const x = coordinates.x ?? coordinates.X, y = coordinates.y ?? coordinates.Y;
    return finiteCoordinate(x) && finiteCoordinate(y) ? { x, y } : undefined;
  };

  if (roi.type === 'Rectangle' && finiteCoordinate(roi.x) && finiteCoordinate(roi.y)
    && typeof roi.width === 'number' && Number.isFinite(roi.width) && roi.width > 0 && roi.width <= 10_000_000
    && typeof roi.height === 'number' && Number.isFinite(roi.height) && roi.height > 0 && roi.height <= 10_000_000)
    return { type: 'Rectangle', x: roi.x, y: roi.y, width: roi.width, height: roi.height };
  if (roi.type === 'Circle' && finiteCoordinate(roi.x) && finiteCoordinate(roi.y)
    && typeof roi.radius === 'number' && Number.isFinite(roi.radius) && roi.radius > 0 && roi.radius <= 10_000_000)
    return { type: 'Circle', x: roi.x, y: roi.y, radius: roi.radius };
  if (roi.type === 'Polygon' && Array.isArray(roi.points) && roi.points.length >= 3 && roi.points.length <= 500_000) {
    const points = roi.points.map(point);
    return points.every((item): item is { x: number; y: number } => Boolean(item)) ? { type: 'Polygon', points } : undefined;
  }
  if (roi.type === 'Brush' && Array.isArray(roi.strokes) && roi.strokes.length > 0 && roi.strokes.length <= 100_000) {
    let totalPoints = 0;
    const strokes = [];
    for (const value of roi.strokes) {
      if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
      const stroke = value as Record<string, unknown>;
      if ((stroke.mode !== 'Add' && stroke.mode !== 'Erase') || typeof stroke.width !== 'number'
        || !Number.isFinite(stroke.width) || stroke.width <= 0 || stroke.width > 1024
        || !Array.isArray(stroke.points) || stroke.points.length === 0) return undefined;
      totalPoints += stroke.points.length;
      if (totalPoints > 500_000) return undefined;
      const points = stroke.points.map(point);
      if (!points.every((item): item is { x: number; y: number } => Boolean(item))) return undefined;
      strokes.push({ mode: stroke.mode, width: stroke.width, points });
    }
    return { type: 'Brush', strokes };
  }
  return undefined;
}

export function legacyCropRectangle(parameters: NodeParameters, imageWidth: number, imageHeight: number): VisionRoi | undefined {
  if (!Number.isFinite(imageWidth) || imageWidth < 1 || !Number.isFinite(imageHeight) || imageHeight < 1) return undefined;
  const read = (key: string, fallback: number) => {
    const value = parameters[key];
    if (value === undefined || value === null) return fallback;
    if (typeof value === 'number') return Number.isFinite(value) ? value : undefined;
    if (typeof value === 'string' && value.trim()) {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : fallback;
    }
    return fallback;
  };
  const x = read('x', 0), y = read('y', 0);
  const width = read('width', imageWidth), height = read('height', imageHeight);
  if (x === undefined || y === undefined || width === undefined || height === undefined
    || Math.abs(x) > 10_000_000 || Math.abs(y) > 10_000_000
    || width <= 0 || width > 10_000_000 || height <= 0 || height > 10_000_000) return undefined;
  return { type: 'Rectangle', x, y, width, height };
}

export function outputPortMatches(edge: Edge | undefined, nodeId: string, portName: string) {
  return edge?.source === nodeId && edge.sourceHandle?.toLowerCase() === portName.toLowerCase();
}

export function imageSourceMatchesPort(image: { nodeId?: string; portName?: string }, nodeId: string | undefined, portName?: string | null) {
  return Boolean(nodeId && image.nodeId === nodeId && image.portName?.toLowerCase() === (portName ?? 'image').toLowerCase());
}

export function imageFramePreservingNodeTypes(catalogItems: NodeCatalogItem[]) {
  // Mirror the workflow compiler: image-to-image nodes keep the pixel frame unless they
  // are an explicit spatial transform. Deriving this from the active catalog also covers plugins.
  const imageTypes = new Set(['image', 'domainimage']);
  return new Set(catalogItems.filter((item) => {
    const type = item.type.toLowerCase();
    if (type === 'roi.transform' || catalogItemChangesImageFrame(item)) return false;
    return item.inputs.some((port) => port.name.toLowerCase() === 'image' && imageTypes.has(port.dataType.toLowerCase()))
      && item.outputs.some((port) => imageTypes.has(port.dataType.toLowerCase()));
  }).map((item) => item.type.toLowerCase()));
}

export function imageFrameDescendsFrom(imageNodeId: string | undefined, sourceNodeId: string | undefined, nodes: Node[], edges: Edge[], preservingTypes: ReadonlySet<string>) {
  if (!imageNodeId || !sourceNodeId) return false;
  let currentId: string | undefined = imageNodeId;
  const visited = new Set<string>();
  while (currentId && !visited.has(currentId)) {
    if (currentId === sourceNodeId) return true;
    visited.add(currentId);
    const current = nodes.find((item) => item.id === currentId);
    const type = String(current?.data.typeKey ?? '').toLowerCase();
    const upstreamHandle = type === 'roi.transform' ? 'debugImage'
      : preservingTypes.has(type) && !imageFrameChanges(current) ? 'image' : undefined;
    if (!upstreamHandle) return false;
    currentId = inputEdge(edges, currentId, upstreamHandle)?.source;
  }
  return false;
}

export function imageFramesSharePixelPreservingAncestor(firstImageNodeId: string | undefined, secondImageNodeId: string | undefined,
  nodes: Node[], edges: Edge[], preservingTypes: ReadonlySet<string>) {
  const ancestors = (sourceNodeId: string | undefined) => {
    const visited = new Set<string>();
    let currentId = sourceNodeId;
    while (currentId && !visited.has(currentId)) {
      visited.add(currentId);
      const current = nodes.find((item) => item.id === currentId);
      const type = String(current?.data.typeKey ?? '').toLowerCase();
      const upstreamHandle = type === 'roi.transform' ? 'debugImage'
        : preservingTypes.has(type) && !imageFrameChanges(current) ? 'image' : undefined;
      if (!upstreamHandle) break;
      currentId = inputEdge(edges, currentId, upstreamHandle)?.source;
    }
    return visited;
  };
  const first = ancestors(firstImageNodeId);
  return [...ancestors(secondImageNodeId)].some((nodeId) => first.has(nodeId));
}

export function clipLineToImage(start: { x: number; y: number }, end: { x: number; y: number }, width: number, height: number) {
  if (width <= 0 || height <= 0) return undefined;
  const dx = end.x - start.x;
  const dy = end.y - start.y;
  let t0 = 0;
  let t1 = 1;
  const clip = (p: number, q: number) => {
    if (Math.abs(p) < 1e-15) return q >= 0;
    const r = q / p;
    if (p < 0) {
      if (r > t1) return false;
      t0 = Math.max(t0, r);
    } else {
      if (r < t0) return false;
      t1 = Math.min(t1, r);
    }
    return t0 <= t1;
  };
  if (!clip(-dx, start.x) || !clip(dx, width - 1 - start.x)
    || !clip(-dy, start.y) || !clip(dy, height - 1 - start.y) || t0 > t1) return undefined;
  return {
    start: { x: Math.max(0, Math.min(width - 1, start.x + t0 * dx)), y: Math.max(0, Math.min(height - 1, start.y + t0 * dy)) },
    end: { x: Math.max(0, Math.min(width - 1, start.x + t1 * dx)), y: Math.max(0, Math.min(height - 1, start.y + t1 * dy)) }
  };
}

