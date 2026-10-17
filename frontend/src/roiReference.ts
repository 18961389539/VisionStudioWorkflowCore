import type { NodeParameters } from './nodeConfigurationState';
import { parameterFingerprint, roiConfigurationIssue } from './nodeConfigurationState';
import type { OverlayPoint, VisionRoi } from './types';
import type { Edge, Node } from '@xyflow/react';
import { normalizeCaliperCircleArc } from './caliperCircleSampling';

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

function portNameIs(actual: string | undefined, expected: string): boolean {
  return actual?.toLowerCase() === expected.toLowerCase();
}

function coordinateNamesForType<T>(catalog: Record<string, T>, type: string): T | undefined {
  const key = Object.keys(catalog).find(candidate => candidate.toLowerCase() === type.toLowerCase());
  return key === undefined ? undefined : catalog[key];
}

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
    const normalizedType = type.toLowerCase();
    const parameters = (node.data.parameters ?? {}) as NodeParameters;
    const nodeEdges = edges.filter(edge => edge.target === nodeId);
    const connectedPorts = new Set(nodeEdges.map(edge => edge.targetHandle?.toLowerCase() ?? ''));
    const keys = (includeAllParameters ? Object.keys(parameters).filter(key =>
      // Trained model bytes are large; the persisted model hash identifies them. Training samples are
      // authoring inputs and do not affect matching until a new trained model is produced.
      key !== 'trainedModelData' && key !== 'trainingSamples')
      : (coordinateNamesForType(coordinateParameters, normalizedType) ?? [])).filter(key => {
      if (normalizedType === 'image.crop' && (connectedPorts.has('roi') || (parameters.roi != null && key !== 'roi'))) return false;
      if (normalizedType === 'image.undistort' && connectedPorts.has('calibrationjson') && key === 'calibrationJson') return false;
      if (normalizedType === 'calibration.cameraintrinsics' && connectedPorts.has('calibrationdatajson') && key === 'calibrationDataJson') return false;
      if (normalizedType === 'calibration.detectboard' && connectedPorts.has('calibrationdatajson') && key === 'calibrationDataJson') return false;
      if (normalizedType === 'roi.transform' && connectedPorts.has('roi') && key === 'roi') return false;
      return true;
    });
    const nodeInputs = Array.isArray(node.data.inputs) ? node.data.inputs as { name?: string; dataType?: string }[] : [];
    const imagePorts = new Set(nodeInputs.filter(port => ['image', 'domainimage'].includes(String(port.dataType ?? '').toLowerCase()))
      .map(port => port.name?.toLowerCase()).filter((name): name is string => typeof name === 'string'));
    if (imagePorts.size === 0) imagePorts.add('image');
    const valueInputPorts = new Set(nodeInputs.filter(port => typeof port.name === 'string'
      && !['control', 'exec'].includes(String(port.dataType ?? '').toLowerCase()))
      .map(port => port.name!.toLowerCase()));
    if (includeAllParameters && valueInputPorts.size === 0) {
      for (const edge of nodeEdges) {
        const port = edge.targetHandle?.toLowerCase() ?? '';
        if (port && !['exec', 'next', 'control', 'execution'].includes(port.toLowerCase())) valueInputPorts.add(port);
      }
    }
    const relevantPorts = includeAllParameters
      ? valueInputPorts
      : new Set([...imagePorts, ...(coordinateNamesForType(coordinateInputPorts, normalizedType) ?? []).map(port => port.toLowerCase())]);
    if (normalizedType === 'image.crop') relevantPorts.add('roi');
    if (normalizedType === 'roi.transform') { relevantPorts.add('roi'); relevantPorts.add('currentpose'); }
    const parents = nodeEdges.filter(edge => relevantPorts.has(edge.targetHandle?.toLowerCase() ?? ''))
      .sort((a, b) => `${a.targetHandle?.toLowerCase()}:${a.source}:${a.sourceHandle?.toLowerCase()}`
        .localeCompare(`${b.targetHandle?.toLowerCase()}:${b.source}:${b.sourceHandle?.toLowerCase()}`))
      .map(edge => ({ port: edge.targetHandle?.toLowerCase(), sourcePort: edge.sourceHandle?.toLowerCase(), frame: visit(edge.source) }));
    const signature = {
      nodeId,
      type: normalizedType,
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
  const angleDeltaDeg = referenceAngleDelta(previous, thetaDeg);
  const angle = angleDeltaDeg * Math.PI / 180;
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
      if (Math.abs(angleDeltaDeg) < 1e-9 && Math.cos(angle) > 0)
        return { ...roi, ...apply(roi) };
      // Match RoiTransformNode: a rectangle is a half-open pixel region, so rotate
      // its selected pixel centers instead of its geometric outer edges. This keeps
      // reference migration from adding a row or column to the downstream mask.
      const left = Math.ceil(roi.x);
      const top = Math.ceil(roi.y);
      const right = Math.ceil(roi.x + roi.width) - 1;
      const bottom = Math.ceil(roi.y + roi.height) - 1;
      if (right < left || bottom < top)
        throw new Error('矩形 ROI 当前没有覆盖任何像素，无法旋转并迁移基准。请先增大区域。');
      let supportLeft = left, supportTop = top, supportRight = right, supportBottom = bottom;
      // Pixel-center corners collapse for a one-pixel-wide ROI. A subpixel
      // support below half a pixel keeps its polygon valid and rasterizes to
      // the same single-pixel support as RoiTransformNode.
      if (right === left) { supportLeft -= 0.49; supportRight += 0.49; }
      if (bottom === top) { supportTop -= 0.49; supportBottom += 0.49; }
      return { type: 'Polygon', points: [
        { x: supportLeft, y: supportTop }, { x: supportRight, y: supportTop },
        { x: supportRight, y: supportBottom }, { x: supportLeft, y: supportBottom }
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
  const rebase = String(source?.data.typeKey ?? '').toLowerCase() === 'roi.transform'
    && ['baseX', 'baseY', 'baseThetaDeg'].some(key => Number(previous[key] ?? 0) !== Number(parameters[key] ?? 0));
  const targets = new Set<string>();
  if (rebase) {
    const nodesById = new Map(nodes.map(node => [node.id, node]));
    const transformPath = new Set([nodeId]);
    const pending = [nodeId];
    while (pending.length > 0) {
      const currentId = pending.pop()!;
      for (const edge of edges) {
        if (edge.source !== currentId || !portNameIs(edge.sourceHandle, 'transform')) continue;
        const target = nodesById.get(edge.target);
        if (!target) continue;
        const type = String(target.data.typeKey ?? '').toLowerCase();
        if (type === 'coordinate.transformcompose' && portNameIs(edge.targetHandle, 'transformA')) {
          if (!transformPath.has(target.id)) {
            transformPath.add(target.id);
            pending.push(target.id);
          }
        } else if (portNameIs(edge.targetHandle, 'transform')
          && ['feature.edgesubpixel', 'measure.caliper', 'measure.caliperline', 'measure.calipercircle'].includes(type)) {
          targets.add(target.id);
        }
      }
    }
  }
  return nodes.map(node => {
    if (node.id === nodeId) return { ...node, data: { ...node.data, stale: true, parameters } };
    if (!targets.has(node.id)) return node;
    const type = String(node.data.typeKey ?? '').toLowerCase();
    const current = (node.data.parameters ?? {}) as NodeParameters;
    const move = (x: number, y: number) => rebaseRoi({ type: 'Circle', x, y, radius: 1 }, previous,
      Number(parameters.baseX ?? 0), Number(parameters.baseY ?? 0), Number(parameters.baseThetaDeg ?? 0)) as Extract<VisionRoi, { type: 'Circle' }>;
    let geometry: NodeParameters;
    if (type === 'measure.calipercircle' && !edges.some(edge => edge.target === node.id && portNameIs(edge.targetHandle, 'circle'))) {
      const center = move(Number(current.centerX ?? 320), Number(current.centerY ?? 240));
      const rotation = -referenceAngleDelta(previous, Number(parameters.baseThetaDeg ?? 0));
      const start = Number(current.angleStartDeg ?? -180) + rotation;
      const end = Number(current.angleEndDeg ?? 180) + rotation;
      const normalizedArc = normalizeCaliperCircleArc(start, end);
      geometry = { centerX: center.x, centerY: center.y,
        angleStartDeg: normalizedArc.start, angleEndDeg: normalizedArc.end };
    } else if (['measure.caliper', 'measure.caliperline', 'feature.edgesubpixel'].includes(type)
      && !edges.some(edge => edge.target === node.id && portNameIs(edge.targetHandle, 'searchLine'))) {
      const start = move(Number(current.startX ?? 120), Number(current.startY ?? 240));
      const end = move(Number(current.endX ?? 520), Number(current.endY ?? 240));
      geometry = { startX: start.x, startY: start.y, endX: end.x, endY: end.y };
    } else return node; // Connected geometry is authored by its own source node.
    return { ...node, data: { ...node.data, stale: true, parameters: { ...current, ...geometry } } };
  });
}
