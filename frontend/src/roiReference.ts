import type { NodeParameters } from './nodeConfigurationState';
import { parameterFingerprint, roiConfigurationIssue } from './nodeConfigurationState';
import type { OverlayPoint, VisionRoi } from './types';
import type { Edge, Node } from '@xyflow/react';

const coordinateParameters: Record<string, string[]> = {
  'image.acquire': ['cameraId'],
  'camera.syncCapture': ['groupId'],
  'frameset.image': ['cameraId'],
  'image.synthetic': ['width', 'height', 'centerX', 'centerY', 'radius', 'placementMode', 'sampleIndex',
    'offsetStepX', 'offsetStepY', 'angleStepDeg', 'randomSeed', 'maxOffsetX', 'maxOffsetY', 'maxAngleDeg'],
  'image.crop': ['roi', 'x', 'y', 'width', 'height'],
  'image.resize': ['width', 'height'],
  'image.rotate': ['angleDeg', 'expand'],
  'image.perspective': ['x1', 'y1', 'x2', 'y2', 'x3', 'y3', 'x4', 'y4', 'width', 'height'],
  'image.undistort': ['calibrationJson'],
  'image.warpAffine': ['width', 'height'],
  'image.register': ['x', 'y', 'width', 'height', 'angleRangeDeg', 'angleStepDeg', 'minScore'],
  'calibration.detectBoard': ['pattern', 'columns', 'rows', 'spacingMm', 'threshold', 'darkObjects', 'minArea', 'maxArea', 'minCircularity', 'maxGridError', 'saddleThreshold'],
  'calibration.cameraIntrinsics': ['calibrationDataJson'],
  'calibration.planar': ['sourceFrame', 'targetFrame', 'targetUnit', 'pairsJson'],
  'calibration.handEye2D': ['mode', 'samplesJson', 'sourceFrame', 'targetFrame', 'sourceUnit', 'unit'],
  'coordinate.transformConstant': ['sourceFrame', 'targetFrame', 'unit', 'x', 'y', 'angleDeg'],
  'coordinate.frameTree': ['transformsJson'],
  'coordinate.frameResolve': ['sourceFrame', 'targetFrame'],
  'coordinate.pose': ['x', 'y', 'thetaDeg', 'frame', 'unit'],
  'coordinate.poseToTransform': ['sourceFrame'],
  'roi.transform': ['roi', 'baseX', 'baseY', 'baseThetaDeg', 'anglePeriodDeg'],
  'match.shape': ['trainedModelHash', 'trainedOriginX', 'trainedOriginY', 'trainedPoseOffsetX', 'trainedPoseOffsetY',
    'trainingOriginMode', 'trainingOriginX', 'trainingOriginY']
};

const coordinateInputPorts: Record<string, string[]> = {
  'frameset.image': ['frameSet'],
  'image.undistort': ['calibrationJson'],
  'image.warpAffine': ['transform'],
  'coordinate.transformInverse': ['transform'],
  'coordinate.transformCompose': ['transformA', 'transformB'],
  'coordinate.frameResolve': ['tree'],
  'coordinate.poseToTransform': ['pose'],
  'coordinate.transformPose': ['pose', 'transform'],
  'coordinate.transformCoordinatePose': ['pose', 'transform'],
  'calibration.detectBoard': ['calibrationDataJson'],
  'calibration.cameraIntrinsics': ['calibrationDataJson']
};

function graphSourceFingerprint(sourceNodeId: string | undefined, nodes: Node[], edges: Edge[], includeAllParameters: boolean): string {
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
    const nodeEdges = edges.filter(edge => edge.target === nodeId);
    const connectedPorts = new Set(nodeEdges.map(edge => edge.targetHandle ?? ''));
    const keys = (includeAllParameters ? Object.keys(parameters).filter(key =>
      // Trained model bytes are large; the persisted model hash identifies them. Training samples are
      // authoring inputs and do not affect matching until a new trained model is produced.
      key !== 'trainedModelData' && key !== 'trainingSamples') : (coordinateParameters[type] ?? [])).filter(key => {
      if (type === 'image.crop' && (connectedPorts.has('roi') || (parameters.roi != null && key !== 'roi'))) return false;
      if (type === 'image.undistort' && connectedPorts.has('calibrationJson') && key === 'calibrationJson') return false;
      if (type === 'calibration.cameraIntrinsics' && connectedPorts.has('calibrationDataJson') && key === 'calibrationDataJson') return false;
      if (type === 'calibration.detectBoard' && connectedPorts.has('calibrationDataJson') && key === 'calibrationDataJson') return false;
      if (type === 'roi.transform' && connectedPorts.has('roi') && key === 'roi') return false;
      return true;
    });
    const nodeInputs = Array.isArray(node.data.inputs) ? node.data.inputs as { name?: string; dataType?: string }[] : [];
    const imagePorts = new Set(nodeInputs.filter(port => port.dataType === 'Image' || port.dataType === 'DomainImage')
      .map(port => port.name).filter((name): name is string => typeof name === 'string'));
    if (imagePorts.size === 0) imagePorts.add('image');
    const valueInputPorts = new Set(nodeInputs.filter(port => typeof port.name === 'string'
      && !['control', 'exec'].includes(String(port.dataType ?? '').toLowerCase()))
      .map(port => port.name!));
    if (includeAllParameters && valueInputPorts.size === 0) {
      for (const edge of nodeEdges) {
        const port = edge.targetHandle ?? '';
        if (port && !['exec', 'next', 'control', 'execution'].includes(port.toLowerCase())) valueInputPorts.add(port);
      }
    }
    const relevantPorts = includeAllParameters
      ? valueInputPorts
      : new Set([...imagePorts, ...(coordinateInputPorts[type] ?? [])]);
    if (type === 'image.crop') relevantPorts.add('roi');
    if (type === 'roi.transform') { relevantPorts.add('roi'); relevantPorts.add('currentPose'); }
    const parents = nodeEdges.filter(edge => relevantPorts.has(edge.targetHandle ?? ''))
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

/** Fingerprint image and geometry inputs that define the pixel frame of a saved ROI reference. */
export function imageFrameFingerprint(sourceNodeId: string | undefined, nodes: Node[], edges: Edge[]): string {
  return graphSourceFingerprint(sourceNodeId, nodes, edges, false);
}

/** Fingerprint every value dependency that can change the connected pose used by a saved ROI reference. */
export function poseSourceFingerprint(sourceNodeId: string | undefined, nodes: Node[], edges: Edge[]): string {
  return graphSourceFingerprint(sourceNodeId, nodes, edges, true);
}

function referenceAngleDelta(parameters: NodeParameters, thetaDeg: number): number {
  const baseTheta = Number(parameters.baseThetaDeg ?? 0);
  const configuredPeriod = Number(parameters.anglePeriodDeg ?? 360);
  const period = Number.isFinite(configuredPeriod) && configuredPeriod > 0 && configuredPeriod <= 360 ? configuredPeriod : 360;
  let delta = (thetaDeg - baseTheta) % period;
  if (delta > period / 2) delta -= period;
  else if (delta < -period / 2) delta += period;
  return delta;
}

/** Re-author an existing region in a new reference frame without applying the motion twice. */
export function rebaseRoi(roi: VisionRoi, previous: NodeParameters, x: number, y: number, thetaDeg: number): VisionRoi {
  const angle = referenceAngleDelta(previous, thetaDeg) * Math.PI / 180;
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
  const roi = parameters.roi as VisionRoi | undefined;
  // Reference capture is also the recovery path for an invalid legacy ROI. Keep that draft
  // editable so the user can replace it instead of failing while trying to transform it.
  const migratedRoi = roi && !roiConfigurationIssue(roi, 'ROI', 1024)
    ? rebaseRoi(roi, parameters, Number(x), Number(y), Number(theta))
    : roi;
  return {
    ...parameters,
    ...(roi ? { roi: migratedRoi } : {}),
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
  const targets = new Set<string>();
  if (rebase) {
    const nodesById = new Map(nodes.map(node => [node.id, node]));
    const transformPath = new Set([nodeId]);
    const pending = [nodeId];
    while (pending.length > 0) {
      const currentId = pending.pop()!;
      for (const edge of edges) {
        if (edge.source !== currentId || edge.sourceHandle !== 'transform') continue;
        const target = nodesById.get(edge.target);
        if (!target) continue;
        const type = String(target.data.typeKey ?? '');
        if (type === 'coordinate.transformCompose' && edge.targetHandle === 'transformA') {
          if (!transformPath.has(target.id)) {
            transformPath.add(target.id);
            pending.push(target.id);
          }
        } else if (edge.targetHandle === 'transform'
          && ['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'].includes(type)) {
          targets.add(target.id);
        }
      }
    }
  }
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
      const rotation = -referenceAngleDelta(previous, Number(parameters.baseThetaDeg ?? 0));
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
