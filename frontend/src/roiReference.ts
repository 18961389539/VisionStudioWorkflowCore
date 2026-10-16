import type { NodeParameters } from './nodeConfigurationState';
import { parameterFingerprint } from './nodeConfigurationState';
import type { OverlayPoint, VisionRoi } from './types';
import type { Edge, Node } from '@xyflow/react';

const coordinateParameters: Record<string, string[]> = {
  'image.crop': ['roi', 'x', 'y', 'width', 'height'],
  'image.resize': ['width', 'height'],
  'image.rotate': ['angleDeg', 'expand'],
  'image.perspective': ['x1', 'y1', 'x2', 'y2', 'x3', 'y3', 'x4', 'y4', 'width', 'height'],
  'roi.transform': ['roi', 'baseX', 'baseY', 'baseThetaDeg', 'anglePeriodDeg'],
  'match.shape': ['trainedModelHash', 'trainedOriginX', 'trainedOriginY', 'trainedPoseOffsetX', 'trainedPoseOffsetY',
    'trainingOriginMode', 'trainingOriginX', 'trainingOriginY']
};

/** Fingerprint the coordinate-changing parts of an image/ROI source chain for saved ROI references. */
export function imageFrameFingerprint(sourceNodeId: string | undefined, nodes: Node[], edges: Edge[]): string {
  if (!sourceNodeId) return parameterFingerprint(null);
  const byId = new Map(nodes.map(node => [node.id, node]));
  const active = new Set<string>();
  const cache = new Map<string, unknown>();

  const visit = (nodeId: string): unknown => {
    if (active.has(nodeId)) return { cycle: nodeId };
    const cached = cache.get(nodeId);
    if (cached !== undefined) return cached;
    const node = byId.get(nodeId);
    if (!node) return { nodeId, missing: true };
    active.add(nodeId);
    const type = typeof node.data.typeKey === 'string' ? node.data.typeKey : '';
    const parameters = (node.data.parameters ?? {}) as NodeParameters;
    const keys = coordinateParameters[type] ?? [];
    const nodeInputs = Array.isArray(node.data.inputs) ? node.data.inputs as { name?: string; dataType?: string }[] : [];
    const imagePorts = new Set(nodeInputs.filter(port => port.dataType === 'Image' || port.dataType === 'DomainImage')
      .map(port => port.name).filter((name): name is string => typeof name === 'string'));
    if (imagePorts.size === 0) imagePorts.add('image');
    const relevantPorts = new Set(imagePorts);
    if (type === 'image.crop') relevantPorts.add('roi');
    if (type === 'roi.transform') { relevantPorts.add('roi'); relevantPorts.add('currentPose'); }
    const parents = edges.filter(edge => edge.target === nodeId && relevantPorts.has(edge.targetHandle ?? ''))
      .sort((a, b) => `${a.targetHandle}:${a.source}:${a.sourceHandle}`.localeCompare(`${b.targetHandle}:${b.source}:${b.sourceHandle}`))
      .map(edge => ({ port: edge.targetHandle, sourcePort: edge.sourceHandle, frame: visit(edge.source) }));
    const signature = {
      nodeId,
      type,
      parameters: keys.map(key => [key, parameters[key] ?? null]),
      parents
    };
    active.delete(nodeId);
    cache.set(nodeId, signature);
    return signature;
  };

  return parameterFingerprint(visit(sourceNodeId));
}

/** Re-author an existing region in a new reference frame without applying the motion twice. */
export function rebaseRoi(roi: VisionRoi, previous: NodeParameters, x: number, y: number, thetaDeg: number): VisionRoi {
  const angle = (thetaDeg - Number(previous.baseThetaDeg ?? 0)) * Math.PI / 180;
  const cos = Math.cos(angle), sin = Math.sin(angle);
  const apply = (point: OverlayPoint): OverlayPoint => {
    const dx = point.x - Number(previous.baseX ?? 0), dy = point.y - Number(previous.baseY ?? 0);
    return { x: cos * dx + sin * dy + x, y: -sin * dx + cos * dy + y };
  };
  switch (roi.type) {
    case 'Circle': return { ...roi, ...apply(roi) };
    case 'Polygon': return { ...roi, points: roi.points.map(apply) };
    case 'Brush': return { ...roi, strokes: roi.strokes.map(stroke => ({ ...stroke, points: stroke.points.map(apply) })) };
    case 'Rectangle': {
      if (Math.abs(Math.sin(angle)) < 1e-10 && Math.cos(angle) > 0)
        return { ...roi, ...apply(roi) };
      return { type: 'Polygon', points: [
        { x: roi.x, y: roi.y }, { x: roi.x + roi.width, y: roi.y },
        { x: roi.x + roi.width, y: roi.y + roi.height }, { x: roi.x, y: roi.y + roi.height }
      ].map(apply) };
    }
  }
}

export function capturedReference(parameters: NodeParameters, summary: Record<string, unknown>, bindings: string, capturedAt: string): NodeParameters {
  const image = summary.referenceImage;
  const x = summary.referenceX, y = summary.referenceY, theta = summary.referenceThetaDeg;
  const width = summary.referenceWidth, height = summary.referenceHeight;
  if (summary.referenceCaptured !== true || typeof image !== 'string' || !image
    || ![x, y, theta, width, height].every(value => typeof value === 'number' && Number.isFinite(value))
    || Number(width) <= 0 || Number(height) <= 0)
    throw new Error('没有获取到完整的基准图像与有效位姿，请检查图像及定位结果。');
  return {
    ...parameters,
    ...(parameters.roi ? { roi: rebaseRoi(parameters.roi as VisionRoi, parameters, Number(x), Number(y), Number(theta)) } : {}),
    baseX: x, baseY: y, baseThetaDeg: theta,
    referenceImage: image, referenceWidth: width, referenceHeight: height,
    referenceBindings: bindings, referenceCapturedAt: capturedAt
  };
}

/** Commit reference changes and parameter-authored measurement geometry in one undoable edit. */
export function applyReferenceParameters(nodes: Node[], edges: Edge[], nodeId: string, parameters: NodeParameters): Node[] {
  const source = nodes.find(node => node.id === nodeId);
  const previous = (source?.data.parameters ?? {}) as NodeParameters;
  const rebase = source?.data.typeKey === 'roi.transform'
    && ['baseX', 'baseY', 'baseThetaDeg'].some(key => Number(previous[key] ?? 0) !== Number(parameters[key] ?? 0));
  const targets = new Set(rebase ? edges.filter(edge => edge.source === nodeId && edge.sourceHandle === 'transform' && edge.targetHandle === 'transform').map(edge => edge.target) : []);
  return nodes.map(node => {
    if (node.id === nodeId) return { ...node, data: { ...node.data, stale: true, parameters } };
    if (!targets.has(node.id)) return node;
    const type = node.data.typeKey;
    const current = (node.data.parameters ?? {}) as NodeParameters;
    const move = (x: number, y: number) => rebaseRoi({ type: 'Circle', x, y, radius: 1 }, previous,
      Number(parameters.baseX ?? 0), Number(parameters.baseY ?? 0), Number(parameters.baseThetaDeg ?? 0)) as Extract<VisionRoi, { type: 'Circle' }>;
    let geometry: NodeParameters;
    if (type === 'measure.caliperCircle' && !edges.some(edge => edge.target === node.id && edge.targetHandle === 'circle')) {
      const center = move(Number(current.centerX ?? 320), Number(current.centerY ?? 240));
      const rotation = Number(previous.baseThetaDeg ?? 0) - Number(parameters.baseThetaDeg ?? 0);
      const start = Number(current.angleStartDeg ?? -180) + rotation;
      const end = Number(current.angleEndDeg ?? 180) + rotation;
      const turns = Math.round((start + end) / 720) * 360;
      geometry = { centerX: center.x, centerY: center.y,
        angleStartDeg: start - turns, angleEndDeg: end - turns };
    } else if (['measure.caliper', 'measure.caliperLine', 'feature.edgeSubpixel'].includes(String(type))
      && !edges.some(edge => edge.target === node.id && edge.targetHandle === 'searchLine')) {
      const start = move(Number(current.startX ?? 120), Number(current.startY ?? 240));
      const end = move(Number(current.endX ?? 520), Number(current.endY ?? 240));
      geometry = { startX: start.x, startY: start.y, endX: end.x, endY: end.y };
    } else return node; // Connected geometry is authored by its own source node.
    return { ...node, data: { ...node.data, stale: true, parameters: { ...current, ...geometry } } };
  });
}
