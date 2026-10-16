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
    if (!Array.isArray(points) || points.length < 3 || points.some(point => !point || typeof point !== 'object'
      || !bounded((point as Record<string, unknown>).x, (point as Record<string, unknown>).y)))
      return `${label}多边形需要至少三个有效顶点，坐标须在 ±${roiCoordinateLimit} px 范围内。`;
    const twiceArea = points.reduce((sum: number, point: Record<string, number>, index: number) => {
      const next = points[(index + 1) % points.length] as Record<string, number>;
      return sum + point.x * next.y - next.x * point.y;
    }, 0);
    const xs = points.map((point: Record<string, number>) => point.x);
    const ys = points.map((point: Record<string, number>) => point.y);
    if (!Number.isFinite(twiceArea) || Math.abs(twiceArea) < 1e-6
      || Math.max(...xs) <= Math.min(...xs) || Math.max(...ys) <= Math.min(...ys))
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
  const roiInputOverridesParameter = hasConnectedInput('roi', 'Roi');
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
