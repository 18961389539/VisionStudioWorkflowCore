/**
 * NodeConfigurationDialog 的参数可见性与耗时提示（巨型组件拆分，2026-11-13）。
 * 纯函数：只读节点目录/参数草稿，不依赖组件树。
 */
import type { NodeParameters } from '../nodeConfigurationState';
import { caliperCircleQuickParameterNames } from './CaliperCircleQuickSettings';
import { caliperEdgeQuickParameterNames } from './CaliperEdgeQuickSettings';

export type NodeDurationHint = {
  samples: number;
  p95Ms: number;
  suggestedTimeoutMs?: number;
  timeoutCount: number;
  tooTight: boolean;
};

export function parseNodeDuration(value: unknown): NodeDurationHint | undefined {
  if (typeof value !== 'object' || value === null) return undefined;
  const record = value as Record<string, unknown>;
  const samples = typeof record.samples === 'number' && Number.isFinite(record.samples) ? record.samples : undefined;
  const p95Ms = typeof record.p95Ms === 'number' && Number.isFinite(record.p95Ms) ? record.p95Ms : undefined;
  if (samples === undefined || p95Ms === undefined) return undefined;
  return {
    samples,
    p95Ms,
    suggestedTimeoutMs: typeof record.suggestedTimeoutMs === 'number' && Number.isFinite(record.suggestedTimeoutMs) ? record.suggestedTimeoutMs : undefined,
    timeoutCount: typeof record.timeoutCount === 'number' && Number.isFinite(record.timeoutCount) ? record.timeoutCount : 0,
    tooTight: record.timeoutTooTight === true
  };
}

export type ParameterVisibility = {
  portHiddenParameterNames: string[];
  numericHiddenParameterNames: string[];
  editorHiddenParameterNames: string[];
  unsupportedCropParameterNames: string[];
};

/** 数值条件参数、编辑器内嵌 JSON、以及不支持裁剪重映射的推理参数——按节点类型隐藏避免无效控件。 */
export function parameterVisibility(params: {
  type: string;
  draft: NodeParameters;
  connectedInputPorts: string[];
  isMetrology: boolean;
  isCaliperCircle: boolean;
  isCaliperEdge: boolean;
}): ParameterVisibility {
  const { type, draft, connectedInputPorts, isMetrology, isCaliperCircle, isCaliperEdge } = params;
  const draftNumber = (key: string, fallback = 0) => {
    const value = draft[key];
    const number = typeof value === 'number' ? value : Number(value ?? fallback);
    return Number.isFinite(number) ? number : fallback;
  };
  const numericHiddenParameterNames = [
    type === 'match.template' && draftNumber('scaleMinPercent', 100) === 100
      && draftNumber('scaleMaxPercent', 100) === 100 ? 'scaleStepPercent' : undefined,
    type === 'measure.regionIntensity' && draftNumber('meanNominal') === 0 ? 'tolerance' : undefined,
    type === 'measure.wallThickness' && draftNumber('nominalThickness') === 0 ? 'tolerance' : undefined,
    type === 'measure.lineSpacing' && draftNumber('nominalDistance') === 0 ? 'tolerance' : undefined,
    type === 'defect.referenceCompare' && draftNumber('variationK') === 0 ? 'variationWindow' : undefined,
    type === 'output.httpRequest' && draftNumber('retries') === 0 ? 'retryDelayMs' : undefined,
    type === 'flow.try' && draftNumber('maxRetries') === 0 ? 'retryDelayMs' : undefined,
    type === 'robot.executeTarget' && draftNumber('maxRetries') === 0 ? 'retryDelayMs' : undefined,
    type === 'match.template' && (draftNumber('searchAngleStartDeg') !== 0 || draftNumber('searchAngleEndDeg') !== 0)
      ? 'angleRangeDeg' : undefined,
    type === 'measure.caliperWidth' && String(draft.direction ?? 'strongest') !== 'closestwidth'
      ? 'nominalWidth' : undefined,
    type === 'flow.if' && !connectedInputPorts.includes('previous') ? 'hysteresis' : undefined
  ].filter((name): name is string => Boolean(name));
  const editorHiddenParameterNames = type === 'flow.switch' ? ['casesJson'] : [];
  /** 这些推理节点的输出坐标无法经过居中裁剪重映射，运行时会拒绝 crop=true。 */
  const unsupportedCropParameterNames = ['inference.detection', 'inference.segmentation', 'inference.obb', 'inference.instanceSegmentation', 'inference.pose', 'inference.anomaly'].includes(type)
    ? ['crop'] : [];
  const portHiddenParameterNames = [...((['coordinate.point', 'coordinate.point3D', 'coordinate.pose'].includes(type) ? connectedInputPorts.filter(port => ['x', 'y', 'z', 'thetaDeg'].includes(port))
    : type === 'device.writeTag' && connectedInputPorts.includes('value') ? ['valueType', 'fallbackValue']
    : type === 'text.match' || type === 'text.validate' ? connectedInputPorts.filter(port => ['text', 'expected'].includes(port))
    : type === 'jlvision.matrixOp' ? connectedInputPorts.filter(port => ['matrixA', 'matrixB'].includes(port))
    : type === 'output.saveImage' ? connectedInputPorts.filter(port => ['path', 'partId'].includes(port))
    : type === 'output.httpRequest' ? [...connectedInputPorts.filter(port => ['url', 'body', 'idempotencyKey'].includes(port)),
      ...(connectedInputPorts.includes('value') ? ['body', 'contentType'] : [])]
    : type === 'jlvision.restoration' && ['psfMotion', 'psfDefocus'].includes(String(draft.operator)) && connectedInputPorts.includes('image') ? ['width', 'height']
    : isMetrology ? ['objects'] : isCaliperCircle ? caliperCircleQuickParameterNames : isCaliperEdge ? caliperEdgeQuickParameterNames : undefined) ?? [])];
  return { portHiddenParameterNames, numericHiddenParameterNames, editorHiddenParameterNames, unsupportedCropParameterNames };
}
