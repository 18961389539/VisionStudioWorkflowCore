import type { NodeCatalogItem, VisionRoi, WorkflowPayload } from './types';
import { validateParameterValues } from './validation';

export type NodeParameters = Record<string, unknown>;

export function cloneParameters(parameters: NodeParameters = {}): NodeParameters {
  return JSON.parse(JSON.stringify(parameters)) as NodeParameters;
}

export function configurationParameters(parameters: NodeParameters, catalog: NodeCatalogItem): NodeParameters {
  const defaults = Object.fromEntries(catalog.parameters.filter((p) => p.defaultValue !== undefined).map((p) => [p.name, p.defaultValue]));
  return cloneParameters({ ...defaults, ...parameters });
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

export function configurationIssues(catalog: NodeCatalogItem, parameters: NodeParameters): string[] {
  const issues = validateParameterValues(catalog.parameters, parameters).map((issue) => `${catalog.parameters.find((p) => p.name === issue.name)?.label ?? issue.name}：${issue.message}`);
  const roi = parameters.roi as VisionRoi | undefined;
  if (roi) {
    const finite = (...values: number[]) => values.every((value) => typeof value === 'number' && Number.isFinite(value));
    if (roi.type === 'Rectangle' && (!finite(roi.x, roi.y, roi.width, roi.height) || roi.width <= 0 || roi.height <= 0)) issues.push('ROI 的位置须有效，宽和高须大于 0。');
    else if (roi.type === 'Circle' && (!finite(roi.x, roi.y, roi.radius) || roi.radius <= 0)) issues.push('ROI 的位置须有效，半径须大于 0。');
    else if (roi.type === 'Polygon' && (!Array.isArray(roi.points) || roi.points.length < 3 || roi.points.some((p) => !p || !finite(p.x, p.y)))) issues.push('多边形 ROI 需要至少三个有效顶点。');
    else if (!['Rectangle', 'Circle', 'Polygon'].includes(roi.type)) issues.push('ROI 类型无效。');
  }
  return issues;
}

/** 测试使用独立流程快照，不能把弹窗草稿写入设计器状态。 */
export function workflowWithNodeParameters(workflow: WorkflowPayload, nodeId: string, parameters: NodeParameters): WorkflowPayload {
  if (!workflow.nodes.some((node) => node.id === nodeId)) throw new Error('待测试节点已不存在。');
  return { ...workflow, nodes: workflow.nodes.map((node) => node.id === nodeId ? { ...node, parameters: cloneParameters(parameters) } : node) };
}
