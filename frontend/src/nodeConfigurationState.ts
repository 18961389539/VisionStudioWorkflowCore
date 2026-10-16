import type { NodeCatalogItem, WorkflowPayload } from './types';
import { validateParameterValues } from './validation';

export type NodeParameters = Record<string, unknown>;

export function cloneParameters(parameters: NodeParameters = {}): NodeParameters {
  return JSON.parse(JSON.stringify(parameters)) as NodeParameters;
}

export function configurationParameters(parameters: NodeParameters, catalog: NodeCatalogItem): NodeParameters {
  const defaults = Object.fromEntries(catalog.parameters.filter((p) => p.defaultValue !== undefined).map((p) => [p.name, p.defaultValue]));
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
const roiPolygonPointLimit = 500_000;

function roiConfigurationIssue(value: unknown, label: string, maxBrushWidth: number): string | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return `${label}结构无效。`;
  const roi = value as Record<string, unknown>;
  const finite = (...values: unknown[]) => values.every(item => typeof item === 'number' && Number.isFinite(item));
  const bounded = (...values: unknown[]) => finite(...values)
    && values.every(item => Math.abs(item as number) <= roiCoordinateLimit);
  if (roi.type === 'Rectangle') {
    const { x, y, width, height } = roi;
    if (!bounded(x, y, width, height) || (width as number) <= 0 || (height as number) <= 0
      || Math.abs((x as number) + (width as number)) > roiCoordinateLimit
      || Math.abs((y as number) + (height as number)) > roiCoordinateLimit)
      return `${label}矩形须为正尺寸，且坐标在 ±${roiCoordinateLimit} px 范围内。`;
    return undefined;
  }
  if (roi.type === 'Circle') {
    const { x, y, radius } = roi;
    if (!bounded(x, y, radius) || (radius as number) <= 0)
      return `${label}圆形须有正半径，且坐标在 ±${roiCoordinateLimit} px 范围内。`;
    return undefined;
  }
  if (roi.type === 'Polygon') {
    const points = roi.points;
    if (!Array.isArray(points)) return `${label}多边形需要至少三个有效顶点，坐标须在 ±${roiCoordinateLimit} px 范围内。`;
    if (points.length > roiPolygonPointLimit)
      return `${label}多边形最多支持 ${roiPolygonPointLimit} 个顶点。`;
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
  if (roi.type === 'Brush') {
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
      return !['Add', 'Erase'].includes(String(item.mode)) || !finite(item.width)
        || (item.width as number) <= 0 || (item.width as number) > maxBrushWidth
        || !Array.isArray(points) || points.length === 0
        || points.some(point => !point || typeof point !== 'object'
          || !bounded((point as Record<string, unknown>).x, (point as Record<string, unknown>).y));
    });
    if (pointCount > 500_000 || invalidStroke || !strokes.some(stroke => stroke && typeof stroke === 'object'
      && (stroke as Record<string, unknown>).mode === 'Add'))
      return `${label}笔刷需至少一笔添加选区；笔宽须在 0..${maxBrushWidth} px 内，坐标有效（最多 10000 笔 / 500000 点）。`;
    return undefined;
  }
  return `${label}类型无效。`;
}

/** Preflight a local crop ROI when the current input image dimensions are known.
 * For curved and rasterized regions this intentionally detects only definite misses,
 * leaving borderline anti-aliased pixels to the backend rasterizer. */
export function cropRoiOutsideImageIssue(value: unknown, imageWidth: number, imageHeight: number): string | undefined {
  if (!Number.isFinite(imageWidth) || !Number.isFinite(imageHeight) || imageWidth < 1 || imageHeight < 1
    || !value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const roi = value as Record<string, unknown>;
  const isFiniteNumber = (item: unknown): item is number => typeof item === 'number' && Number.isFinite(item);
  const finite = (...items: unknown[]) => items.every(isFiniteNumber);
  let minX: number; let minY: number; let maxX: number; let maxY: number;

  if (roi.type === 'Rectangle' && finite(roi.x, roi.y, roi.width, roi.height)
    && (roi.width as number) > 0 && (roi.height as number) > 0) {
    const x = roi.x as number; const y = roi.y as number;
    const width = roi.width as number; const height = roi.height as number;
    const left = Math.max(0, Math.min(imageWidth, Math.ceil(x)));
    const top = Math.max(0, Math.min(imageHeight, Math.ceil(y)));
    const right = Math.max(0, Math.min(imageWidth, Math.ceil(x + width)));
    const bottom = Math.max(0, Math.min(imageHeight, Math.ceil(y + height)));
    if (right > left && bottom > top) return undefined;
    return `裁剪区域与当前输入图像（${imageWidth}×${imageHeight}）没有像素交集；请将 ROI 移入图像范围。`;
  }
  if (roi.type === 'Circle' && finite(roi.x, roi.y, roi.radius) && (roi.radius as number) > 0) {
    const x = roi.x as number; const y = roi.y as number; const radius = roi.radius as number;
    minX = x - radius; minY = y - radius;
    maxX = x + radius; maxY = y + radius;
  } else if (roi.type === 'Polygon' && Array.isArray(roi.points) && roi.points.length >= 3) {
    minX = Infinity; minY = Infinity; maxX = -Infinity; maxY = -Infinity;
    for (const point of roi.points) {
      if (!point || typeof point !== 'object') return undefined;
      const coordinates = point as Record<string, unknown>;
      if (!finite(coordinates.x, coordinates.y)) return undefined;
      minX = Math.min(minX, coordinates.x as number); minY = Math.min(minY, coordinates.y as number);
      maxX = Math.max(maxX, coordinates.x as number); maxY = Math.max(maxY, coordinates.y as number);
    }
  } else if (roi.type === 'Brush' && Array.isArray(roi.strokes)) {
    minX = Infinity; minY = Infinity; maxX = -Infinity; maxY = -Infinity;
    let hasAddPoint = false;
    for (const stroke of roi.strokes) {
      if (!stroke || typeof stroke !== 'object') return undefined;
      const item = stroke as Record<string, unknown>;
      if (item.mode !== 'Add') continue;
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
  const margin = roi.type === 'Brush' ? 0 : 2;
  if (maxX < -margin || maxY < -margin || minX > imageWidth - 1 + margin || minY > imageHeight - 1 + margin)
    return `裁剪区域与当前输入图像（${imageWidth}×${imageHeight}）没有像素交集；请将 ROI 移入图像范围。`;
  return undefined;
}

export function configurationIssues(
  catalog: NodeCatalogItem,
  parameters: NodeParameters,
  options: { connectedInputPorts?: readonly string[] } = {}
): string[] {
  const connectedPorts = new Set((options.connectedInputPorts ?? []).map((port) => port.toLowerCase()));
  const hasConnectedInput = (name: string, dataType: string) => connectedPorts.has(name.toLowerCase())
    && catalog.inputs.some((input) => input.name.toLowerCase() === name.toLowerCase()
      && input.dataType.toLowerCase() === dataType.toLowerCase());
  const overriddenParameters = new Set<string>();
  if (hasConnectedInput('searchLine', 'Line2D'))
    ['startX', 'startY', 'endX', 'endY'].forEach((name) => overriddenParameters.add(name));
  if (hasConnectedInput('circle', 'Circle'))
    ['centerX', 'centerY', 'radius'].forEach((name) => overriddenParameters.add(name));
  const issues = validateParameterValues(catalog.parameters.filter((parameter) => !overriddenParameters.has(parameter.name)), parameters)
    .map((issue) => `${catalog.parameters.find((p) => p.name === issue.name)?.label ?? issue.name}：${issue.message}`);
  const numericParameter = (name: string) => {
    const configured = parameters[name];
    if (configured !== undefined) return typeof configured === 'number' && Number.isFinite(configured) ? configured : undefined;
    const fallback = catalog.parameters.find((parameter) => parameter.name === name)?.defaultValue;
    return typeof fallback === 'number' && Number.isFinite(fallback) ? fallback : undefined;
  };
  const roiInputOverridesParameter = hasConnectedInput('roi', 'Roi');
  const searchLineInputOverridesGeometry = hasConnectedInput('searchLine', 'Line2D');
  if (!searchLineInputOverridesGeometry
    && ['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine'].includes(catalog.type)) {
    const startX = numericParameter('startX');
    const startY = numericParameter('startY');
    const endX = numericParameter('endX');
    const endY = numericParameter('endY');
    if ([startX, startY, endX, endY].every((coordinate) => typeof coordinate === 'number' && Number.isFinite(coordinate))
      && Math.hypot(endX! - startX!, endY! - startY!) < 2)
      issues.push('搜索线长度至少需要 2 px；请调整起点/终点，或连接有效的 searchLine 输入。');
  }
  if (catalog.type === 'measure.caliperCircle') {
    const angleStart = numericParameter('angleStartDeg');
    const angleEnd = numericParameter('angleEndDeg');
    if (angleStart !== undefined && angleEnd !== undefined && Math.abs(angleEnd - angleStart) < 1e-9)
      issues.push('起止角相同会生成单条径向卡尺，无法拟合圆；请扩大搜索弧角范围。');
  }
  for (const key of ['roi', 'searchRoi', 'clutterRoi'] as const) {
    if (key === 'roi' && roiInputOverridesParameter) continue;
    const value = parameters[key];
    if (value === undefined || value === null) continue;
    const label = key === 'roi' ? 'ROI' : key === 'clutterRoi' ? '杂乱区域' : '搜索区域';
    const issue = roiConfigurationIssue(value, label, catalog.type === 'image.crop' ? 512 : 1024);
    if (issue) issues.push(issue);
  }
  return issues;
}

/** 测试使用独立流程快照，不能把弹窗草稿写入设计器状态。 */
export function workflowWithNodeParameters(workflow: WorkflowPayload, nodeId: string, parameters: NodeParameters): WorkflowPayload {
  if (!workflow.nodes.some((node) => node.id === nodeId)) throw new Error('待测试节点已不存在。');
  return { ...workflow, nodes: workflow.nodes.map((node) => node.id === nodeId ? { ...node, parameters: cloneParameters(parameters) } : node) };
}
