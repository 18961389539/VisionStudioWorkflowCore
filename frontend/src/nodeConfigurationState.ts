import { metrologyObjectsIssue } from './metrologyObjects';
import type { NodeCatalogItem, WorkflowPayload } from './types';
import { validateParameterValues } from './parameterValidation';
import { buildCaliperCircleAngles } from './caliperCircleSampling';
import { readSwitchCases } from './switchPorts';

export type NodeParameters = Record<string, unknown>;

export function cloneParameters(parameters: NodeParameters = {}): NodeParameters {
  return JSON.parse(JSON.stringify(parameters)) as NodeParameters;
}

function migrateRegionIntensityParameters(parameters: NodeParameters, merged: NodeParameters): void {
  if (parameters.emptyPolicy === undefined && parameters.requireNonEmpty !== undefined)
    merged.emptyPolicy = String(parameters.requireNonEmpty).trim().toLowerCase() === 'true' ? 'fail' : 'zeros';
  delete merged.requireNonEmpty;
}

function migrateSaveImageParameters(parameters: NodeParameters, merged: NodeParameters): void {
  if (parameters.namingMode === undefined) merged.namingMode = 'fixed';
  if (parameters.conflictPolicy === undefined) merged.conflictPolicy = parameters.overwrite === true ? 'overwrite' : 'fail';
  delete merged.overwrite;
}

export function configurationParameters(parameters: NodeParameters, catalog: NodeCatalogItem): NodeParameters {
  // internal 参数（ROI 对象、基准图像、回放路径）由画布/宿主维护，不能当作可见默认值回填。
  const defaults = Object.fromEntries(catalog.parameters
    .filter((p) => !p.internal && p.defaultValue !== undefined)
    .map((p) => [p.name, p.defaultValue]));
  const merged = cloneParameters({ ...defaults, ...parameters });
  // Older image.crop workflows stored a rectangular ROI as four scalar parameters.
  // Present that region in the shared ROI editor while preserving the serialized legacy fields until edited.
  if (catalog.type === 'image.crop' && merged.roi === undefined
    && ['x', 'y', 'width', 'height'].every((key) => typeof merged[key] === 'number' && Number.isFinite(merged[key]))) {
    merged.roi = { type: 'Rectangle', x: merged.x as number, y: merged.y as number,
      width: merged.width as number, height: merged.height as number };
  }
  // Older shape workflows have no lifecycle field. Preserve their image-template behavior when editing;
  // newly created nodes receive modelMode=trained from makeDefaultParameters.
  if (catalog.type === 'match.shape' && parameters.modelMode === undefined && !parameters.trainedModelData) merged.modelMode = 'legacy';
  if (catalog.type === 'match.shape' && parameters.trainingAngleRangeDeg === undefined
    && typeof parameters.trainedModelConfig === 'string' && /^shape-v[12]\|/.test(parameters.trainedModelConfig))
    merged.trainingAngleRangeDeg = typeof parameters.angleRangeDeg === 'number' ? parameters.angleRangeDeg : 180;
  if (catalog.type === 'measure.caliperCircle' && parameters.samplingMode === undefined
    && parameters.angleStepDeg !== undefined) merged.samplingMode = 'step';
  if (catalog.type === 'measure.caliperCircle' && parameters.maxFitOutliers === undefined) merged.maxFitOutliers = 0;
  if (catalog.type === 'measure.caliperLine' && parameters.maxFitOutliers === undefined) merged.maxFitOutliers = 0;
  if (catalog.type === 'measure.caliper' && parameters.refine === undefined && parameters.edgeThreshold !== undefined) merged.refine = 'none';
  if (catalog.type === 'image.difference' && parameters.mode === undefined && parameters.diffMode !== undefined) merged.mode = parameters.diffMode;
  if (catalog.type === 'output.httpRequest' && parameters.retries === undefined && parameters.retryPolicy !== undefined)
    merged.retries = parameters.retryPolicy === 'backoff' ? 2 : parameters.retryPolicy === 'once' ? 1 : 0;
  if (catalog.type === 'measure.metrology') delete merged.debugImagePreviewOnly;
  if (catalog.type === 'measure.regionIntensity') migrateRegionIntensityParameters(parameters, merged);
  if (catalog.type === 'output.saveImage') migrateSaveImageParameters(parameters, merged);
  if (catalog.type === 'output.exportResult' && parameters.overwrite !== undefined) {
    merged.conflictPolicy = parameters.overwrite ? 'overwrite' : 'fail'; delete merged.overwrite;
  }
  return merged;
}

/** Merge runtime catalog defaults into a loaded node without changing legacy shape-node behavior. */
export function hydrateNodeParameters(parameters: NodeParameters, catalog: NodeCatalogItem, defaults: NodeParameters): NodeParameters {
  const merged = { ...defaults, ...parameters };
  // A catalog refresh must not turn a historical image-template node into a new trained-model node.
  // New nodes use makeDefaultParameters directly and therefore keep modelMode=trained.
  if (catalog.type === 'match.shape' && parameters.modelMode === undefined && !parameters.trainedModelData)
    merged.modelMode = 'legacy';
  if (catalog.type === 'match.shape' && parameters.trainingAngleRangeDeg === undefined
    && typeof parameters.trainedModelConfig === 'string' && /^shape-v[12]\|/.test(parameters.trainedModelConfig))
    merged.trainingAngleRangeDeg = typeof parameters.angleRangeDeg === 'number' ? parameters.angleRangeDeg : 180;
  // Existing circle-caliper workflows used angleStepDeg as their only sampling control.
  // Keep that behavior when new sampling-mode defaults are hydrated into old nodes.
  if (catalog.type === 'measure.caliperCircle' && parameters.samplingMode === undefined
    && parameters.angleStepDeg !== undefined) merged.samplingMode = 'step';
  if (catalog.type === 'measure.caliperCircle' && parameters.maxFitOutliers === undefined) merged.maxFitOutliers = 0;
  if (catalog.type === 'measure.caliperLine' && parameters.maxFitOutliers === undefined) merged.maxFitOutliers = 0;
  if (catalog.type === 'measure.caliper' && parameters.refine === undefined && parameters.edgeThreshold !== undefined) merged.refine = 'none';
  if (catalog.type === 'image.difference' && parameters.mode === undefined && parameters.diffMode !== undefined) merged.mode = parameters.diffMode;
  if (catalog.type === 'output.httpRequest' && parameters.retries === undefined && parameters.retryPolicy !== undefined)
    merged.retries = parameters.retryPolicy === 'backoff' ? 2 : parameters.retryPolicy === 'once' ? 1 : 0;
  if (catalog.type === 'measure.metrology') delete merged.debugImagePreviewOnly;
  if (catalog.type === 'measure.regionIntensity') migrateRegionIntensityParameters(parameters, merged);
  if (catalog.type === 'output.saveImage') migrateSaveImageParameters(parameters, merged);
  if (catalog.type === 'output.exportResult' && parameters.overwrite !== undefined) {
    merged.conflictPolicy = parameters.overwrite ? 'overwrite' : 'fail'; delete merged.overwrite;
  }
  return merged;
}

/** 属性顺序不影响参数是否相同，ROI 的坐标和顶点也参与比较。 */
export function parameterFingerprint(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(parameterFingerprint).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.entries(value).filter(([, v]) => v !== undefined)
    .sort(([a], [b]) => a.localeCompare(b)).map(([key, v]) => `${JSON.stringify(key)}:${parameterFingerprint(v)}`).join(',')}}`;
  return JSON.stringify(value) ?? 'undefined';
}

export function withParameter(parameters: NodeParameters, key: string, value: unknown): NodeParameters {
  const next = { ...parameters };
  if (value === undefined) delete next[key]; else next[key] = value;
  return next;
}

const roiCoordinateLimit = 10_000_000;
export const MAX_ROI_POLYGON_POINTS = 500_000;

export function roiConfigurationIssue(value: unknown, label: string, maxBrushWidth: number): string | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return `${label}结构无效。`;
  const roi = value as Record<string, unknown>;
  const roiType = typeof roi.type === 'string' ? roi.type.toLowerCase() : '';
  const finite = (...values: unknown[]) => values.every(item => typeof item === 'number' && Number.isFinite(item));
  const bounded = (...values: unknown[]) => finite(...values)
    && values.every(item => Math.abs(item as number) <= roiCoordinateLimit);
  if (roiType === 'rectangle') {
    const { x, y, width, height } = roi;
    if (!bounded(x, y, width, height) || (width as number) <= 0 || (height as number) <= 0
      || Math.abs((x as number) + (width as number)) > roiCoordinateLimit
      || Math.abs((y as number) + (height as number)) > roiCoordinateLimit)
      return `${label}矩形须为正尺寸，且坐标在 ±${roiCoordinateLimit} px 范围内。`;
    return undefined;
  }
  if (roiType === 'circle') {
    const { x, y, radius } = roi;
    if (!bounded(x, y, radius) || (radius as number) <= 0)
      return `${label}圆形须有正半径，且坐标在 ±${roiCoordinateLimit} px 范围内。`;
    return undefined;
  }
  if (roiType === 'polygon') {
    const points = roi.points;
    if (!Array.isArray(points)) return `${label}多边形需要至少三个有效顶点，坐标须在 ±${roiCoordinateLimit} px 范围内。`;
    if (points.length > MAX_ROI_POLYGON_POINTS)
      return `${label}多边形最多支持 ${MAX_ROI_POLYGON_POINTS} 个顶点。`;
    if (points.length < 3 || points.some(point => !point || typeof point !== 'object'
      || !bounded((point as Record<string, unknown>).x, (point as Record<string, unknown>).y)))
      return `${label}多边形需要至少三个有效顶点，坐标须在 ±${roiCoordinateLimit} px 范围内。`;
    const twiceArea = points.reduce((sum: number, point: Record<string, number>, index: number) => {
      const next = points[(index + 1) % points.length] as Record<string, number>;
      return sum + point.x * next.y - next.x * point.y;
    }, 0);
    const bounds = points.reduce((current, point: Record<string, number>) => ({
      minX: Math.min(current.minX, point.x), minY: Math.min(current.minY, point.y),
      maxX: Math.max(current.maxX, point.x), maxY: Math.max(current.maxY, point.y)
    }), { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity });
    if (!Number.isFinite(twiceArea) || Math.abs(twiceArea) < 1e-6
      || bounds.maxX <= bounds.minX || bounds.maxY <= bounds.minY)
      return `${label}多边形顶点须围成非零面积。`;
    return undefined;
  }
  if (roiType === 'brush') {
    const strokes = roi.strokes;
    if (!Array.isArray(strokes) || strokes.length < 1 || strokes.length > 10_000)
      return `${label}笔刷需要 1..10000 笔有效笔迹。`;
    const pointCount = strokes.reduce((count, stroke) => count
      + (stroke && typeof stroke === 'object' && Array.isArray((stroke as Record<string, unknown>).points)
        ? ((stroke as Record<string, unknown>).points as unknown[]).length : 0), 0);
    const invalidStroke = strokes.some(stroke => {
      if (!stroke || typeof stroke !== 'object') return true;
      const item = stroke as Record<string, unknown>;
      const points = item.points;
      return !['add', 'erase'].includes(String(item.mode).toLowerCase()) || !finite(item.width)
        || (item.width as number) <= 0 || (item.width as number) > maxBrushWidth
        || !Array.isArray(points) || points.length === 0
        || points.some(point => !point || typeof point !== 'object'
          || !bounded((point as Record<string, unknown>).x, (point as Record<string, unknown>).y));
    });
    if (pointCount > 500_000 || invalidStroke || !strokes.some(stroke => stroke && typeof stroke === 'object'
      && String((stroke as Record<string, unknown>).mode).toLowerCase() === 'add'))
      return `${label}笔刷需至少一笔添加选区；笔宽须在 0..${maxBrushWidth} px 内，坐标有效（最多 10000 笔 / 500000 点）。`;
    return undefined;
  }
  return `${label}类型无效。`;
}

function brushSelectionIsExactlyErased(strokes: unknown[]): boolean {
  if (strokes.length !== 2) return false;
  const addIndex = strokes.findIndex((stroke) => stroke && typeof stroke === 'object' && String((stroke as Record<string, unknown>).mode).toLowerCase() === 'add');
  const eraseIndex = strokes.findIndex((stroke) => stroke && typeof stroke === 'object' && String((stroke as Record<string, unknown>).mode).toLowerCase() === 'erase');
  if (addIndex < 0 || eraseIndex <= addIndex) return false;
  const add = strokes[addIndex] as Record<string, unknown>;
  const erase = strokes[eraseIndex] as Record<string, unknown>;
  if (!add || !erase || typeof add.width !== 'number' || !Number.isFinite(add.width)
    || typeof erase.width !== 'number' || !Number.isFinite(erase.width)
    || add.width <= 0 || add.width > 1024 || erase.width <= 0 || erase.width > 1024
    || !Array.isArray(add.points) || !Array.isArray(erase.points) || add.points.length === 0
    || add.points.length > 500_000 || add.points.length !== erase.points.length)
    return false;

  // Match System.Math.Round (midpoints to even) used by the backend brush rasterizer.
  const rasterCoordinate = (value: number) => {
    const lower = Math.floor(value);
    const fraction = value - lower;
    if (fraction < 0.5) return lower;
    if (fraction > 0.5) return lower + 1;
    return lower % 2 === 0 ? lower : lower + 1;
  };
  const addThickness = Math.max(1, Math.min(1024, rasterCoordinate(add.width)));
  const eraseThickness = Math.max(1, Math.min(1024, rasterCoordinate(erase.width)));
  if (addThickness !== eraseThickness) return false;
  for (let index = 0; index < add.points.length; index++) {
    const addPoint = add.points[index];
    const erasePoint = erase.points[index];
    if (!addPoint || typeof addPoint !== 'object' || !erasePoint || typeof erasePoint !== 'object') return false;
    const addCoordinates = addPoint as Record<string, unknown>;
    const eraseCoordinates = erasePoint as Record<string, unknown>;
    if (typeof addCoordinates.x !== 'number' || !Number.isFinite(addCoordinates.x)
      || typeof addCoordinates.y !== 'number' || !Number.isFinite(addCoordinates.y)
      || typeof eraseCoordinates.x !== 'number' || !Number.isFinite(eraseCoordinates.x)
      || typeof eraseCoordinates.y !== 'number' || !Number.isFinite(eraseCoordinates.y)
      || Math.abs(addCoordinates.x) > 10_000_000 || Math.abs(addCoordinates.y) > 10_000_000
      || Math.abs(eraseCoordinates.x) > 10_000_000 || Math.abs(eraseCoordinates.y) > 10_000_000
      || rasterCoordinate(addCoordinates.x) !== rasterCoordinate(eraseCoordinates.x)
      || rasterCoordinate(addCoordinates.y) !== rasterCoordinate(eraseCoordinates.y)) return false;
  }
  return true;
}

/** Preflight an ROI when its target image dimensions are known.
 * For curved and rasterized regions this intentionally detects only definite misses,
 * leaving borderline anti-aliased pixels to the backend rasterizer. */
function roiOutsideImageIssue(value: unknown, imageWidth: number, imageHeight: number, purpose: 'crop' | 'measurement'): string | undefined {
  if (!Number.isFinite(imageWidth) || !Number.isFinite(imageHeight) || imageWidth < 1 || imageHeight < 1
    || !value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const roi = value as Record<string, unknown>;
  const roiType = typeof roi.type === 'string' ? roi.type.toLowerCase() : '';
  const noIntersection = () => purpose === 'crop'
    ? `裁剪区域与当前输入图像（${imageWidth}×${imageHeight}）没有像素交集；请将 ROI 移入图像范围。`
    : `测量 ROI 与当前输入图像（${imageWidth}×${imageHeight}）没有像素交集；请将 ROI 移入图像范围。`;
  const noRectanglePixels = () => purpose === 'crop'
    ? `裁剪矩形与当前输入图像（${imageWidth}×${imageHeight}）范围有重叠，但没有覆盖任何像素；请增大 ROI 尺寸或向图像内部移动。`
    : `测量矩形 ROI 与当前输入图像（${imageWidth}×${imageHeight}）范围有重叠，但没有覆盖任何像素；请增大 ROI 尺寸或向图像内部移动。`;
  const isFiniteNumber = (item: unknown): item is number => typeof item === 'number' && Number.isFinite(item);
  const finite = (...items: unknown[]) => items.every(isFiniteNumber);
  let minX: number; let minY: number; let maxX: number; let maxY: number;

  if (roiType === 'rectangle' && finite(roi.x, roi.y, roi.width, roi.height)
    && (roi.width as number) > 0 && (roi.height as number) > 0) {
    const x = roi.x as number; const y = roi.y as number;
    const width = roi.width as number; const height = roi.height as number;
    const left = Math.max(0, Math.min(imageWidth, Math.ceil(x)));
    const top = Math.max(0, Math.min(imageHeight, Math.ceil(y)));
    const right = Math.max(0, Math.min(imageWidth, Math.ceil(x + width)));
    const bottom = Math.max(0, Math.min(imageHeight, Math.ceil(y + height)));
    if (right > left && bottom > top) return undefined;
    const overlapsImage = x < imageWidth && x + width > 0 && y < imageHeight && y + height > 0;
    return overlapsImage ? noRectanglePixels() : noIntersection();
  }
  if (roiType === 'circle' && finite(roi.x, roi.y, roi.radius) && (roi.radius as number) > 0) {
    const x = roi.x as number; const y = roi.y as number; const radius = roi.radius as number;
    minX = x - radius; minY = y - radius;
    maxX = x + radius; maxY = y + radius;
  } else if (roiType === 'polygon' && Array.isArray(roi.points) && roi.points.length >= 3) {
    minX = Infinity; minY = Infinity; maxX = -Infinity; maxY = -Infinity;
    for (const point of roi.points) {
      if (!point || typeof point !== 'object') return undefined;
      const coordinates = point as Record<string, unknown>;
      if (!finite(coordinates.x, coordinates.y)) return undefined;
      minX = Math.min(minX, coordinates.x as number); minY = Math.min(minY, coordinates.y as number);
      maxX = Math.max(maxX, coordinates.x as number); maxY = Math.max(maxY, coordinates.y as number);
    }
  } else if (roiType === 'brush' && Array.isArray(roi.strokes)) {
    minX = Infinity; minY = Infinity; maxX = -Infinity; maxY = -Infinity;
    let hasAddPoint = false;
    for (const stroke of roi.strokes) {
      if (!stroke || typeof stroke !== 'object') return undefined;
      const item = stroke as Record<string, unknown>;
      if (String(item.mode).toLowerCase() !== 'add') continue;
      if (!Array.isArray(item.points) || !finite(item.width) || (item.width as number) <= 0) return undefined;
      const radius = (item.width as number) / 2 + 2;
      for (const point of item.points) {
        if (!point || typeof point !== 'object') return undefined;
        const coordinates = point as Record<string, unknown>;
        if (!finite(coordinates.x, coordinates.y)) return undefined;
        const x = coordinates.x as number; const y = coordinates.y as number;
        minX = Math.min(minX, x - radius); minY = Math.min(minY, y - radius);
        maxX = Math.max(maxX, x + radius); maxY = Math.max(maxY, y + radius);
        hasAddPoint = true;
      }
    }
    if (!hasAddPoint) return undefined;
  } else return undefined;

  // Circle and polygon rasterization rounds coordinates and antialiases edges. Keep a
  // two-pixel margin so this preflight never rejects a region that could touch a pixel.
  const margin = roiType === 'brush' ? 0 : 2;
  if (maxX < -margin || maxY < -margin || minX > imageWidth - 1 + margin || minY > imageHeight - 1 + margin)
    return noIntersection();
  if (roiType === 'brush' && Array.isArray(roi.strokes) && brushSelectionIsExactlyErased(roi.strokes))
    return purpose === 'crop'
      ? '笔刷裁剪区域被同轨迹、同笔宽的擦除笔迹完全覆盖，预览会失败；请调整擦除笔迹或重新添加选区。'
      : '笔刷测量 ROI 被同轨迹、同笔宽的擦除笔迹完全覆盖，卡尺无法采样；请调整擦除笔迹或重新添加选区。';
  return undefined;
}

/** Preflight a local crop ROI when the current input image dimensions are known. */
export function cropRoiOutsideImageIssue(value: unknown, imageWidth: number, imageHeight: number): string | undefined {
  return roiOutsideImageIssue(value, imageWidth, imageHeight, 'crop');
}

/** Preflight a local measurement ROI when the current input image dimensions are known. */
export function measurementRoiOutsideImageIssue(value: unknown, imageWidth: number, imageHeight: number): string | undefined {
  return roiOutsideImageIssue(value, imageWidth, imageHeight, 'measurement');
}

export type NodeConfigurationIssue = { parameter?: string; message: string };

export function configurationIssues(
  catalog: NodeCatalogItem,
  parameters: NodeParameters,
  options: { connectedInputPorts?: readonly string[] } = {}
): string[] {
  return validateNodeConfiguration(catalog, parameters, options).map(issue => issue.message);
}

/** 配置弹窗与整图预检使用同一套接线覆盖、类型、范围及组合参数规则。 */
export function validateNodeConfiguration(
  catalog: NodeCatalogItem,
  parameters: NodeParameters,
  options: { connectedInputPorts?: readonly string[] } = {}
): NodeConfigurationIssue[] {
  const connectedPorts = new Set((options.connectedInputPorts ?? []).map((port) => port.toLowerCase()));
  const hasConnectedInput = (name: string, dataType: string) => connectedPorts.has(name.toLowerCase())
    && catalog.inputs.some((input) => input.name.toLowerCase() === name.toLowerCase()
      && input.dataType.toLowerCase() === dataType.toLowerCase());
  const overriddenParameters = new Set<string>();
  if (catalog.type === 'device.writeTag' && hasConnectedInput('value', 'Any'))
    ['valueType', 'fallbackValue'].forEach(name => overriddenParameters.add(name));
  if (['coordinate.point', 'coordinate.point3D', 'coordinate.pose'].includes(catalog.type))
    ['x', 'y', 'z', 'thetaDeg'].filter(port => hasConnectedInput(port, 'Double')).forEach(port => overriddenParameters.add(port));
  if (catalog.type === 'text.match' || catalog.type === 'text.validate')
    ['text', 'expected'].filter(port => hasConnectedInput(port, 'String')).forEach(port => overriddenParameters.add(port));
  if (catalog.type === 'output.saveImage')
    ['path', 'partId'].filter(port => hasConnectedInput(port, 'String')).forEach(port => overriddenParameters.add(port));
  if (catalog.type === 'output.httpRequest') {
    ['url', 'body', 'idempotencyKey'].filter(port => hasConnectedInput(port, 'String')).forEach(port => overriddenParameters.add(port));
    if (hasConnectedInput('value', 'Any')) ['body', 'contentType'].forEach(name => overriddenParameters.add(name));
  }
  if (catalog.type === 'jlvision.matrixOp')
    ['matrixA', 'matrixB'].filter(port => hasConnectedInput(port, 'String')).forEach(port => overriddenParameters.add(port));
  if (catalog.type === 'jlvision.restoration' && ['psfMotion', 'psfDefocus'].includes(String(parameters.operator)) && hasConnectedInput('image', 'Image'))
    ['width', 'height'].forEach(port => overriddenParameters.add(port));
  if (hasConnectedInput('searchLine', 'Line2D'))
    ['startX', 'startY', 'endX', 'endY'].forEach((name) => overriddenParameters.add(name));
  if (hasConnectedInput('circle', 'Circle'))
    ['centerX', 'centerY', 'radius'].forEach((name) => overriddenParameters.add(name));
  const parameterIssues = validateParameterValues(catalog.parameters.filter((parameter) => !overriddenParameters.has(parameter.name)), parameters)
    .map((issue) => ({ parameter: issue.name, message: `${catalog.parameters.find((p) => p.name === issue.name)?.label ?? issue.name}：${issue.message}` }));
  const issues: string[] = [];
  if (catalog.type === 'measure.fitLine' || catalog.type === 'measure.fitCircle') {
    const minimum = catalog.type === 'measure.fitCircle' ? 3 : 2;
    const count = catalog.inputs.filter(port => hasConnectedInput(port.name, 'Point2D')).length;
    if (count < minimum) issues.push(`至少需要连接 ${minimum} 个点输入，当前已连接 ${count} 个。`);
  }
  if (catalog.type === 'flow.switch') {
    const raw = parameters.casesJson === undefined ? catalog.parameters.find(parameter => parameter.name === 'casesJson')?.defaultValue : parameters.casesJson;
    const issue = readSwitchCases(raw).issue;
    if (issue) issues.push(issue);
  }
  if (catalog.type === 'output.saveImage') {
    if (String(parameters.resultMode ?? 'all') !== 'all' && !connectedPorts.has('pass')) issues.push('OK/NG 分类或筛选保存需要连接 pass。');
    if (String(parameters.namingMode ?? 'fixed') === 'template') {
      const template = String(parameters.filenameTemplate ?? '{timestamp}_{saveId}').trim();
      const matches = [...template.matchAll(/\{([a-zA-Z][a-zA-Z0-9]*)\}/g)];
      const names = new Set(matches.map(match => match[1].toLowerCase()));
      const supported = ['timestamp', 'date', 'runid', 'saveid', 'nodeid', 'result', 'partid', 'sequence'];
      if (!template || template.length > 512 || /[<>:"/\\|?*\x00-\x1f]/.test(template)
        || /[{}]/.test(template.replace(/\{([a-zA-Z][a-zA-Z0-9]*)\}/g, '')) || [...names].some(name => !supported.includes(name)))
        issues.push('文件名模板无效；仅支持 timestamp/date/runId/saveId/nodeId/result/partId/sequence，不含目录和扩展名。');
      if (names.has('result') && !connectedPorts.has('pass')) issues.push('模板使用 {result} 时需要连接 pass。');
      if (names.has('sequence') && !connectedPorts.has('sequence')) issues.push('模板使用 {sequence} 时需要连接非负整数序号。');
      if (names.has('partid') && !connectedPorts.has('partid') && !String(parameters.partId ?? '').trim()) issues.push('模板使用 {partId} 时需要连接或配置产品编号。');
    }
  }
  if (catalog.type === 'output.httpRequest') {
    const method = String(parameters.method ?? 'GET').toUpperCase();
    if (connectedPorts.has('body') && connectedPorts.has('value')) issues.push('HTTP 正文只能连接原文 body 或 JSON 数据 value 之一。');
    if (['GET', 'HEAD'].includes(method) && (connectedPorts.has('body') || connectedPorts.has('value'))) issues.push('GET/HEAD 不发送正文，请断开正文输入或更改方法。');
    if (!['GET', 'HEAD'].includes(method) && parameters.retryWriteRequests && Number(parameters.retries ?? 0) > 0
      && !connectedPorts.has('idempotencykey') && !String(parameters.idempotencyKey ?? '').trim()) issues.push('写请求重试需要提供幂等键，并由服务端支持去重。');
  }
  if (catalog.type === 'device.writeVisionResult') {
    const defaults = Object.fromEntries(catalog.parameters.map(parameter => [parameter.name, parameter.defaultValue]));
    const tag = (key: string) => String(parameters[key] ?? defaults[key] ?? '').trim();
    const activeTags = ['resultReadyTag', 'resultOkTag', 'xTag', 'yTag', 'rTag'].map(tag).filter(Boolean).map(value => value.toLowerCase());
    if (new Set(activeTags).size !== activeTags.length) issues.push('PLC Ready、OK 和坐标标签不能重复。');
    if (String(parameters.missingCoordinatePolicy ?? 'skip') === 'fail')
      for (const field of ['x', 'y', 'r']) if (tag(field + 'Tag') && !connectedPorts.has(field)) issues.push(`${field.toUpperCase()} 标签已启用但坐标未连接；请接线、清空标签或调整缺失坐标策略。`);
  }
  if (catalog.type === 'region.fromThreshold' || catalog.type === 'region.fromRange' && String(parameters.mode ?? 'gray') === 'gray')
    if (Number(parameters.minGray ?? 128) > Number(parameters.maxGray ?? 255)) issues.push('灰度下限不能大于上限。');
  if (catalog.type === 'measure.metrology') {
    const issue = metrologyObjectsIssue(parameters.objects); if (issue) issues.push(issue);
  }
  if (catalog.type === 'image.flatFieldCorrect') {
    const mode = String(parameters.referenceMode ?? 'gainOffset');
    if (mode !== 'offset' && !connectedPorts.has('bright')) issues.push('增益校正需要连接亮场图像。');
    if (mode !== 'gain' && parameters.darkPolicy === 'fail' && !connectedPorts.has('dark')) issues.push('当前暗场策略要求连接暗场图像。');
  }
  const numericParameter = (name: string) => {
    const configured = parameters[name];
    if (configured !== undefined) return typeof configured === 'number' && Number.isFinite(configured) ? configured : undefined;
    const fallback = catalog.parameters.find((parameter) => parameter.name === name)?.defaultValue;
    return typeof fallback === 'number' && Number.isFinite(fallback) ? fallback : undefined;
  };
  const validNumericParameter = (name: string) => {
    const value = numericParameter(name);
    const descriptor = catalog.parameters.find((parameter) => parameter.name === name);
    if (value === undefined
      || (descriptor?.min !== undefined && value < descriptor.min)
      || (descriptor?.max !== undefined && value > descriptor.max)) return undefined;
    return value;
  };
  const roundedParameter = (value: number, minimum: number, maximum: number) =>
    Math.max(minimum, Math.min(maximum, Math.floor(value + 0.5)));
  if (catalog.type === 'device.waitTag') {
    const timeout = validNumericParameter('timeoutMs');
    for (const name of ['timeoutMs', 'pollMs', 'stableMs']) {
      const value = validNumericParameter(name);
      if (value !== undefined && !Number.isInteger(value)) issues.push(`${name} 必须为整数毫秒。`);
      if (name !== 'timeoutMs' && value !== undefined && timeout !== undefined && value > timeout)
        issues.push(`${name === 'pollMs' ? '轮询间隔' : '稳定保持时间'}不能大于超时时间。`);
    }
  }
  if (catalog.type === 'flow.if' && String(parameters.mode ?? 'threshold').toLowerCase() === 'range') {
    const minimum = validNumericParameter('rangeMin'), maximum = validNumericParameter('rangeMax');
    if (minimum !== undefined && maximum !== undefined && minimum > maximum)
      issues.push('区间下限不得大于区间上限。');
  }
  if (catalog.type === 'measure.regionIntensity') {
    const minimum = validNumericParameter('minMean'), maximum = validNumericParameter('maxMean');
    const nominal = validNumericParameter('meanNominal'), tolerance = validNumericParameter('tolerance');
    if (minimum !== undefined && maximum !== undefined && minimum > 0 && maximum > 0 && minimum > maximum)
      issues.push('启用均值上下限时，最低均值不得高于最高均值。');
    if (nominal !== undefined && nominal > 0 && tolerance !== undefined) {
      if (tolerance <= 0) issues.push('启用标称均值判定时，均值容差必须大于 0。');
      else if (minimum !== undefined && minimum > 0 && nominal + tolerance < minimum
        || maximum !== undefined && maximum > 0 && nominal - tolerance > maximum)
        issues.push('标称均值容差区间与均值上下限必须有交集。');
    }
  }
  const roiInputOverridesParameter = hasConnectedInput('roi', 'Roi');
  const searchLineInputOverridesGeometry = hasConnectedInput('searchLine', 'Line2D');
  if (!searchLineInputOverridesGeometry
    && ['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperWidth', 'measure.caliperLine'].includes(catalog.type)) {
    const startX = numericParameter('startX');
    const startY = numericParameter('startY');
    const endX = numericParameter('endX');
    const endY = numericParameter('endY');
    if ([startX, startY, endX, endY].every((coordinate) => typeof coordinate === 'number' && Number.isFinite(coordinate))
      && Math.hypot(endX! - startX!, endY! - startY!) < 2)
      issues.push('搜索线长度至少需要 2 px；请调整起点/终点，或连接有效的 searchLine 输入。');
  }
  if (catalog.type === 'measure.caliperWidth') {
    const minimum = validNumericParameter('minWidth');
    const maximum = validNumericParameter('maxWidth');
    if (minimum !== undefined && maximum !== undefined && maximum > 0 && maximum < minimum)
      issues.push('最大宽度必须为 0（不限）或不小于最小宽度。');
    const nominal = validNumericParameter('nominalWidth');
    if (parameters.direction === 'closestwidth' && nominal !== undefined && nominal <= 0)
      issues.push('按标称宽度选择时，标称宽度必须大于 0。');
  }
  if (catalog.type === 'measure.contourThickness') {
    const minimum = validNumericParameter('minThickness');
    const maximum = validNumericParameter('maxThickness');
    if (minimum !== undefined && maximum !== undefined && maximum > 0 && maximum < minimum)
      issues.push('最大合格壁厚必须为 0（不限）或不小于最小合格壁厚。');
    const budget = validNumericParameter('maxSamplesPerBoundary');
    if (budget !== undefined && !Number.isInteger(budget)) issues.push('每侧最大采样数必须为整数。');
  }
  if (catalog.type === 'measure.caliperCircle') {
    const angleStart = validNumericParameter('angleStartDeg');
    const angleEnd = validNumericParameter('angleEndDeg');
    const zeroSpan = angleStart !== undefined && angleEnd !== undefined && Math.abs(angleEnd - angleStart) < 1e-9;
    if (zeroSpan)
      issues.push('起止角相同会生成单条径向卡尺，无法拟合圆；请扩大搜索弧角范围。');

    const mode = typeof parameters.samplingMode === 'string'
      ? parameters.samplingMode.toLowerCase()
      : parameters.angleStepDeg !== undefined ? 'step' : 'count';
    let availableCalipers: number | undefined;
    if (!zeroSpan && angleStart !== undefined && angleEnd !== undefined && mode === 'count') {
      const count = validNumericParameter('caliperCount');
      if (count !== undefined) availableCalipers = buildCaliperCircleAngles({
        angleStartDeg: angleStart, angleEndDeg: angleEnd, samplingMode: 'count', caliperCount: roundedParameter(count, 3, 361)
      }).length;
    } else if (!zeroSpan && angleStart !== undefined && angleEnd !== undefined && mode === 'step') {
      const requestedStep = validNumericParameter('angleStepDeg');
      if (requestedStep !== undefined) availableCalipers = buildCaliperCircleAngles({
        angleStartDeg: angleStart, angleEndDeg: angleEnd, samplingMode: 'step', angleStepDeg: requestedStep
      }).length;
    }

    const minimumValid = validNumericParameter('minValidCalipers');
    if (availableCalipers !== undefined && availableCalipers < 3)
      issues.push(`当前圆弧与角度步长只会生成 ${availableCalipers} 条径向卡尺；圆拟合至少需要 3 条。请扩大圆弧或减小角度步长。`);
    else if (availableCalipers !== undefined && minimumValid !== undefined
      && roundedParameter(minimumValid, 3, 361) > availableCalipers)
      issues.push(`最少有效卡尺数超过当前设置可生成的 ${availableCalipers} 条卡尺；请降低门槛、扩大圆弧或提高采样密度。`);
  }
  if (catalog.type === 'measure.caliperLine') {
    const count = validNumericParameter('caliperCount');
    const minimum = validNumericParameter('minValidCalipers');
    if (count !== undefined && minimum !== undefined
      && roundedParameter(minimum, 3, 101) > roundedParameter(count, 3, 101))
      issues.push('最少有效卡尺数超过卡尺总数；请降低门槛或增加卡尺数。');
  }
  for (const key of ['roi', 'searchRoi', 'clutterRoi'] as const) {
    if (key === 'roi' && roiInputOverridesParameter) continue;
    const value = parameters[key];
    if (value === undefined || value === null) continue;
    const label = key === 'roi' ? 'ROI' : key === 'clutterRoi' ? '杂乱区域' : '搜索区域';
    const issue = roiConfigurationIssue(value, label, 1024);
    if (issue) issues.push(issue);
  }
  return [...parameterIssues, ...issues.map(message => ({ message }))];
}

/** 测试使用独立流程快照，不能把弹窗草稿写入设计器状态。 */
export function workflowWithNodeParameters(workflow: WorkflowPayload, nodeId: string, parameters: NodeParameters): WorkflowPayload {
  if (!workflow.nodes.some((node) => node.id === nodeId)) throw new Error('待测试节点已不存在。');
  return { ...workflow, nodes: workflow.nodes.map((node) => node.id === nodeId ? { ...node, parameters: cloneParameters(parameters) } : node) };
}
