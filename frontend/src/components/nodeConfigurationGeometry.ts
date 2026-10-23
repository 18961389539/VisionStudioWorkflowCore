/**
 * NodeConfigurationDialog 的几何/图纯函数：帧血缘追踪、几何解析、刚体像素变换映射。
 * 不依赖 React 组件树；从 NodeConfigurationDialog.tsx 拆出（巨型组件拆分，2026-10-23）。
 */
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, ParameterDescriptor } from '../types';
import type { MeasurementGeometry } from './ImageViewer';
import { imageFrameChanges, inputEdge } from './nodeConfigurationUtils';

/** 参数说明行的元信息：默认值 / 取值范围 / 步进——目录里都有，此前说明页只有一句兜底文案。 */
export function parameterMeta(parameter: ParameterDescriptor) {
  const bits: string[] = [];
  if (parameter.defaultValue !== undefined && parameter.defaultValue !== null && parameter.defaultValue !== '')
    bits.push(`默认 ${String(parameter.defaultValue)}`);
  if (parameter.min !== undefined || parameter.max !== undefined)
    bits.push(`范围 ${parameter.min ?? '—'} ~ ${parameter.max ?? '—'}`);
  if (parameter.step !== undefined) bits.push(`步进 ${parameter.step}`);
  return bits.join(' · ');
}

export function transformSourceLineage(nodes: Node[], edges: Edge[], sourceId: string | undefined): Node[] {
  const visited = new Set<string>();
  const visit = (id: string): Node[] => {
    if (visited.has(id)) return [];
    visited.add(id);
    const source = nodes.find((item) => item.id === id);
    if (!source) return [];
    const inputHandles = source.data.typeKey === 'coordinate.transformCompose' ? ['transformA', 'transformB'] : [];
    const ancestors = inputHandles.flatMap((handle) => {
      const input = inputEdge(edges, source.id, handle);
      return input ? visit(input.source) : [];
    });
    return [source, ...ancestors];
  };
  return sourceId ? visit(sourceId) : [];
}

export function geometrySourceImageFrameNodeIds(geometryEdge: Edge | undefined, nodes: Node[], edges: Edge[], catalogItems: NodeCatalogItem[], visited = new Set<string>()): string[] {
  if (!geometryEdge || visited.has(geometryEdge.source)) return [];
  visited.add(geometryEdge.source);
  const source = nodes.find((item) => item.id === geometryEdge.source);
  const type = String(source?.data.typeKey ?? '').toLowerCase();
  const sourceCatalog = catalogItems.find((item) => item.type.toLowerCase() === type);
  if (type === 'measure.fitline' || type === 'measure.fitcircle') {
    const pointPorts = new Set(sourceCatalog?.inputs.filter((port) => port.dataType.toLowerCase() === 'point2d')
      .map((port) => port.name.toLowerCase()));
    return edges.filter((edge) => edge.target === geometryEdge.source && pointPorts.has(String(edge.targetHandle ?? '').toLowerCase()))
      .flatMap((edge) => geometrySourceImageFrameNodeIds(edge, nodes, edges, catalogItems, new Set(visited)));
  }
  const output = sourceCatalog?.outputs.find((port) => port.name.toLowerCase() === String(geometryEdge.sourceHandle ?? '').toLowerCase());
  const outputType = output?.dataType.toLowerCase();
  if (type === 'image.crop' && output?.name.toLowerCase() === 'croporigin') {
    const frameSource = edges.find((edge) => edge.target === geometryEdge.source && edge.targetHandle?.toLowerCase() === 'image')?.source;
    return frameSource ? [frameSource] : [];
  }
  if (!['circle', 'line2d', 'point2d', 'rectangle2d', 'pose2d'].includes(outputType ?? '')) return [];
  const hasFrameAwareGeometry = ['roi.transform', 'match.shape', 'feature.line', 'feature.circle', 'feature.edgesubpixel',
    'measure.caliper', 'measure.caliperwidth', 'measure.caliperline', 'measure.calipercircle'].includes(type);
  const hasImageOutput = sourceCatalog?.outputs.some((port) => port.name.toLowerCase() === 'image'
    && ['image', 'domainimage'].includes(port.dataType.toLowerCase()));
  if (!hasFrameAwareGeometry && !hasImageOutput) return [];
  const inputPort = type === 'roi.transform' ? 'debugImage' : 'image';
  const frameSource = edges.find((edge) => edge.target === geometryEdge.source && edge.targetHandle?.toLowerCase() === inputPort.toLowerCase())?.source;
  return frameSource ? [frameSource] : [];
}

export function roiSourceFrameNodeIdsForEdge(roiEdge: Edge | undefined, nodes: Node[], edges: Edge[], catalogItems: NodeCatalogItem[]): string[] {
  if (!roiEdge) return [];
  const source = nodes.find((item) => item.id === roiEdge.source);
  const type = String(source?.data.typeKey ?? '').toLowerCase();
  const sourceCatalog = catalogItems.find((item) => item.type.toLowerCase() === type);
  const output = sourceCatalog?.outputs.find((port) => port.name.toLowerCase() === String(roiEdge.sourceHandle ?? '').toLowerCase());
  if (output?.dataType.toLowerCase() !== 'roi') return [];
  if (type === 'image.crop' && output.name.toLowerCase() === 'croppedroi') return [source.id];
  if (type === 'roi.transform' && output.name.toLowerCase() === 'roi') {
    const frameSource = edges.find((edge) => edge.target === source.id && edge.targetHandle?.toLowerCase() === 'debugimage')?.source;
    return frameSource ? [frameSource] : [];
  }
  return [];
}

export function transformMappedImageFrameNodeIds(transformNodes: Node[], edges: Edge[]) {
  return [...new Set(transformNodes.flatMap((node) => {
    const type = String(node.data.typeKey ?? '').toLowerCase();
    if (type === 'image.crop') return [node.id];
    const targetPort = type === 'roi.transform' ? 'debugImage'
      : type === 'image.register' ? 'reference'
        : type === 'match.shape' ? 'image' : undefined;
    if (!targetPort) return [];
    const target = edges.find((edge) => edge.target === node.id && edge.targetHandle?.toLowerCase() === targetPort.toLowerCase());
    return target ? [target.source] : [];
  }))];
}

export function transformEndpointImageFrameNodeIds(transformNodeId: string | undefined, endpoint: 'source' | 'target', nodes: Node[], edges: Edge[], visited = new Set<string>()): string[] {
  if (!transformNodeId || visited.has(transformNodeId)) return [];
  visited.add(transformNodeId);
  const node = nodes.find((item) => item.id === transformNodeId);
  const type = String(node?.data.typeKey ?? '').toLowerCase();
  const inputSource = (handle: string) => edges.find((edge) => edge.target === transformNodeId
    && edge.targetHandle?.toLowerCase() === handle.toLowerCase())?.source;
  if (type === 'image.crop' || type === 'jlvision.imageop' && imageFrameChanges(node)) {
    const frameId = endpoint === 'source' ? inputSource('image') : transformNodeId;
    return frameId ? [frameId] : [];
  }
  if (type === 'image.register') {
    const frameId = inputSource(endpoint === 'source' ? 'image' : 'reference');
    return frameId ? [frameId] : [];
  }
  if (type === 'roi.transform') {
    // The saved reference is intentionally not graph-traceable; its target is the live debug image.
    const frameId = endpoint === 'target' ? inputSource('debugImage') : undefined;
    return frameId ? [frameId] : [];
  }
  if (type === 'match.shape') {
    const frameId = inputSource(endpoint === 'source' ? 'template' : 'image');
    return frameId ? [frameId] : [];
  }
  if (type === 'coordinate.transformcompose') {
    const handle = endpoint === 'source' ? 'transformA' : 'transformB';
    return transformEndpointImageFrameNodeIds(inputSource(handle), endpoint, nodes, edges, visited);
  }
  if (type === 'coordinate.transforminverse') {
    const inputTransform = inputSource('transform');
    return transformEndpointImageFrameNodeIds(inputTransform, endpoint === 'source' ? 'target' : 'source', nodes, edges, visited);
  }
  return [];
}

export function connectedCircleGeometry(value: unknown): { x: number; y: number; radius: number } | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const circle = value as Record<string, unknown>;
  const x = circle.x ?? circle.X;
  const y = circle.y ?? circle.Y;
  const radius = circle.radius ?? circle.Radius;
  if (typeof x !== 'number' || !Number.isFinite(x)
    || typeof y !== 'number' || !Number.isFinite(y)
    || typeof radius !== 'number' || !Number.isFinite(radius) || radius <= 0) return undefined;
  return { x, y, radius };
}

export function connectedSearchLineGeometry(value: unknown): { start: { x: number; y: number }; end: { x: number; y: number } } | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const line = value as Record<string, unknown>;
  const point = (value: unknown) => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
    const coordinates = value as Record<string, unknown>;
    const x = coordinates.x ?? coordinates.X;
    const y = coordinates.y ?? coordinates.Y;
    return typeof x === 'number' && Number.isFinite(x) && typeof y === 'number' && Number.isFinite(y) ? { x, y } : undefined;
  };
  const start = point(line.start ?? line.Start);
  const end = point(line.end ?? line.End);
  return start && end ? { start, end } : undefined;
}

export type RigidPixelTransform = { h00: number; h01: number; h02: number; h10: number; h11: number; h12: number };

export function rigidPixelTransform(value: unknown): RigidPixelTransform | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const transform = value as Record<string, unknown>;
  const read = (lower: string, upper: string) => transform[lower] ?? transform[upper];
  const numbers = ['h00', 'h01', 'h02', 'h10', 'h11', 'h12', 'h20', 'h21', 'h22']
    .map((key) => read(key, key.toUpperCase()));
  if (!numbers.every((item) => typeof item === 'number' && Number.isFinite(item))) return undefined;
  const [h00, h01, h02, h10, h11, h12, h20, h21, h22] = numbers as number[];
  const sourceUnit = read('sourceUnit', 'SourceUnit');
  const targetUnit = read('targetUnit', 'TargetUnit');
  if ((sourceUnit !== undefined && sourceUnit !== 'px') || (targetUnit !== undefined && targetUnit !== 'px')) return undefined;
  const tolerance = 1e-6;
  if (Math.abs(h20) > tolerance || Math.abs(h21) > tolerance || Math.abs(h22 - 1) > tolerance
    || Math.abs(h00 * h00 + h10 * h10 - 1) > tolerance
    || Math.abs(h01 * h01 + h11 * h11 - 1) > tolerance
    || Math.abs(h00 * h01 + h10 * h11) > tolerance
    || Math.abs(h00 * h11 - h01 * h10 - 1) > tolerance) return undefined;
  return { h00, h01, h02, h10, h11, h12 };
}

export function mapMeasurementGeometry(geometry: MeasurementGeometry, transform: RigidPixelTransform): MeasurementGeometry {
  const mapPoint = (point: { x: number; y: number }) => ({
    x: transform.h00 * point.x + transform.h01 * point.y + transform.h02,
    y: transform.h10 * point.x + transform.h11 * point.y + transform.h12
  });
  if (geometry.type === 'line')
    return { ...geometry, start: mapPoint(geometry.start), end: mapPoint(geometry.end) };
  const rotation = Math.atan2(transform.h10, transform.h00) * 180 / Math.PI;
  return { ...geometry, center: mapPoint(geometry.center),
    angleStartDeg: (geometry.angleStartDeg ?? -180) + rotation,
    angleEndDeg: (geometry.angleEndDeg ?? 180) + rotation };
}
