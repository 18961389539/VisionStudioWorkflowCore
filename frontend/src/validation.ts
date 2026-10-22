import { isPortRequired } from './portRequirements';
import { validateParameterValues } from './parameterValidation';
import { validateNodeConfiguration } from './nodeConfigurationState';
export { validateParameterValues, isParameterVisible, parameterPresentation } from './parameterValidation';
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, ParameterDescriptor, PortDescriptor } from './types';
import { localizePortName, portDisplayName } from './i18n/catalog';

/**
 * 校验问题：节点/连线问题列表与端口兼容高亮共用的一套判定逻辑。
 * `nodeId` / `edgeId` 存在时前端可一键定位；服务端编译错误尽量解析出引用对象。
 */
export type WorkflowIssue = {
  id: string;
  severity: 'error';
  source: 'client' | 'server';
  message: string;
  nodeId?: string;
  edgeId?: string;
  parameter?: string;
};

/** 端口类型兼容规则（连线校验 / 连接中的端口高亮 / 边类型预检共用同一套）。
 *  控制端口与数据端口严格隔离：Any 只通配数据端口，不再允许 Any ↔ Control。 */
export function portsCompatible(sourceType?: string, targetType?: string): boolean {
  if (!sourceType || !targetType) return false;
  const sourceControl = sourceType === 'Control';
  const targetControl = targetType === 'Control';
  if (sourceControl || targetControl) return sourceControl && targetControl;
  return sourceType === targetType
    || sourceType === 'Any'
    || targetType === 'Any'
    || (sourceType === 'Integer' && targetType === 'Double');
}

/** 连线裁决结果：allowed=false 时 reason 直接给用户看（高亮 tooltip / 拖放报错 / 预检问题共用同一句）。 */
export type ConnectionVerdict = { allowed: boolean; reason?: string };

/** 唯一的连线裁决入口：类型规则 + 控制/数据隔离 + 数据输入单一来源。
 *  端口高亮、isValidConnection、onConnect、validateWorkflow 全部走这里，避免“界面允许、校验拒绝”。 */
export function connectionVerdict(args: {
  sourceType?: string;
  targetType?: string;
  sameNode?: boolean;
  /** 目标数据输入上已有的数据连线数（控制端口不参与，允许多条入边） */
  targetDataSources?: number;
}): ConnectionVerdict {
  const { sourceType, targetType, sameNode, targetDataSources = 0 } = args;
  if (!sourceType || !targetType) return { allowed: false, reason: '端口类型未知' };
  if (sameNode) return { allowed: false, reason: '不能连接节点自身' };
  const sourceControl = sourceType === 'Control';
  const targetControl = targetType === 'Control';
  if (sourceControl !== targetControl)
    return { allowed: false, reason: `控制端口与数据端口不能互连（${sourceType} → ${targetType}）` };
  if (sourceControl) return { allowed: true };
  if (targetDataSources > 0)
    return { allowed: false, reason: '该数据输入已有来源（每个数据输入只允许 1 条连线）' };
  if (!portsCompatible(sourceType, targetType))
    return { allowed: false, reason: `类型不匹配：${sourceType} → ${targetType}` };
  return { allowed: true };
}

type NodeLookup = (id: string) => Pick<Node, 'data'> | undefined;

function portsOf(node: Pick<Node, 'data'> | undefined, key: 'inputs' | 'outputs'): PortDescriptor[] {
  return ((node?.data?.[key] ?? []) as PortDescriptor[]);
}

function edgeKind(edge: Edge, getNode: NodeLookup): 'control' | 'data' {
  const declared = String(edge.data?.kind ?? '');
  if (declared === 'control' || declared === 'data') return declared;
  const sourcePort = portsOf(getNode(edge.source), 'outputs').find(port => port.name === edge.sourceHandle);
  return sourcePort?.dataType === 'Control' ? 'control' : 'data';
}

export function graphConnectionVerdict(connection: Pick<Edge, 'source' | 'target' | 'sourceHandle' | 'targetHandle'>,
  getNode: NodeLookup, edges: readonly Edge[]): ConnectionVerdict {
  const source = portsOf(getNode(connection.source), 'outputs').find(port => port.name === connection.sourceHandle);
  const target = portsOf(getNode(connection.target), 'inputs').find(port => port.name === connection.targetHandle);
  return connectionVerdict({
    sourceType: source?.dataType, targetType: target?.dataType, sameNode: connection.source === connection.target,
    targetDataSources: target?.dataType === 'Control' ? 0 : edges.filter(edge => edge.target === connection.target
      && edge.targetHandle === connection.targetHandle && edgeKind(edge, getNode) === 'data').length
  });
}

export function connectedInputNames(nodeId: string, inputs: readonly PortDescriptor[], edges: readonly Edge[], getNode: NodeLookup): Set<string> {
  const connected = new Set<string>();
  for (const edge of edges) {
    if (edge.target !== nodeId || edge.source === nodeId) continue;
    const source = portsOf(getNode(edge.source), 'outputs').find(port => port.name === edge.sourceHandle);
    const target = inputs.find(port => port.name === edge.targetHandle);
    if (!source || !target || !portsCompatible(source.dataType, target.dataType)
      || (edgeKind(edge, getNode) === 'control') !== (target.dataType === 'Control')) continue;
    connected.add(target.name);
  }
  return connected;
}

/** 全图客户端预检：类型缺失 / 参数值 / 必需输入 / 重复数据源 / 边的端点、端口与类型。 */
export function validateWorkflow(nodes: Node[], catalog: NodeCatalogItem[], edges: Edge[]): WorkflowIssue[] {
  const issues: WorkflowIssue[] = [];
  const byType = new Map(catalog.map((item) => [item.type.toLowerCase(), item]));
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const nodeLabel = (id: string) => String(byId.get(id)?.data.label ?? id);
  const getNode: NodeLookup = (id) => byId.get(id);
  const incomingOf = (nodeId: string) => edges.filter((edge) => edge.target === nodeId);

  for (const node of nodes) {
    const typeKey = String(node.data.typeKey ?? '');
    const isModuleCall = typeKey === 'module.call';
    const item = isModuleCall ? undefined : byType.get(typeKey.toLowerCase());
    if (!item && !isModuleCall) {
      issues.push({
        id: `node-type-${node.id}`,
        severity: 'error',
        source: 'client',
        nodeId: node.id,
        message: `节点「${nodeLabel(node.id)}」的类型 ${typeKey} 不在当前目录中（插件未加载或版本不匹配）`
      });
      continue;
    }

    const parameters = (node.data.parameters ?? {}) as Record<string, unknown>;
    const descriptors = item?.parameters
      ?? ((node.data.moduleParameters ?? []) as ParameterDescriptor[]);
    const incoming = incomingOf(node.id);
    const dataEdges = incoming.filter((edge) => edgeKind(edge, getNode) === 'data');
    const connectedInputs = connectedInputNames(node.id, portsOf(node, 'inputs'), incoming, getNode);
    const connectedInputPorts = [...connectedInputs].filter(name => portsOf(node, 'inputs')
      .some(port => port.name === name && port.dataType !== 'Control'));
    const configurationIssues = item
      ? validateNodeConfiguration(item, parameters, { connectedInputPorts })
      : validateParameterValues(descriptors, parameters).map(issue => ({ parameter: issue.name,
        message: `${descriptors.find(descriptor => descriptor.name === issue.name)?.label ?? issue.name}：${issue.message}` }));
    for (const [index, issue] of configurationIssues.entries()) {
      issues.push({
        id: `param-${node.id}-${issue.parameter ?? index}`,
        severity: 'error',
        source: 'client',
        nodeId: node.id,
        parameter: issue.parameter,
        message: `节点「${nodeLabel(node.id)}」参数 ${issue.message}`
      });
    }

    // 必需语义按端口种类分别判定：控制必需口只认控制线，数据必需口只认数据线
    // （与 VisionNode 的缺口提示、connectionVerdict 保持一致，避免“接了控制线仍报未连接”）
    for (const port of portsOf(node, 'inputs')) {
      if (!isPortRequired(port, parameters, descriptors)) continue;
      const isControl = port.dataType === 'Control';
      if (connectedInputs.has(port.name)) continue;
      issues.push({
        id: `port-missing-${node.id}-${port.name}`,
        severity: 'error',
        source: 'client',
        nodeId: node.id,
        message: `节点「${nodeLabel(node.id)}」的必需${isControl ? '控制' : '数据'}输入 ${portDisplayName(port.name, port.label)}（${port.dataType}）未连接`
      });
    }
    const counts = new Map<string, number>();
    for (const edge of dataEdges) counts.set(edge.targetHandle ?? '', (counts.get(edge.targetHandle ?? '') ?? 0) + 1);
    for (const [port, count] of counts) {
      if (count > 1) {
        const descriptor = portsOf(node, 'inputs').find((item) => item.name === port);
        issues.push({
          id: `port-dup-${node.id}-${port}`,
          severity: 'error',
          source: 'client',
          nodeId: node.id,
          message: `节点「${nodeLabel(node.id)}」的输入 ${descriptor ? portDisplayName(descriptor.name, descriptor.label) : localizePortName(port)} 连接了 ${count} 条数据线（只允许 1 条）`
        });
      }
    }
  }

  for (const edge of edges) {
    const source = byId.get(edge.source);
    const target = byId.get(edge.target);
    if (!source || !target) {
      issues.push({
        id: `edge-endpoint-${edge.id}`,
        severity: 'error',
        source: 'client',
        edgeId: edge.id,
        message: `连线 ${edge.id} 引用了不存在的节点`
      });
      continue;
    }
    if (edge.source === edge.target) {
      issues.push({
        id: `edge-self-${edge.id}`,
        severity: 'error',
        source: 'client',
        edgeId: edge.id,
        message: `连线 ${edge.id} 连接了同一个节点`
      });
      continue;
    }
    const sourcePort = portsOf(source, 'outputs').find((port) => port.name === edge.sourceHandle);
    const targetPort = portsOf(target, 'inputs').find((port) => port.name === edge.targetHandle);
    if (!sourcePort || !targetPort) {
      issues.push({
        id: `edge-port-${edge.id}`,
        severity: 'error',
        source: 'client',
        edgeId: edge.id,
        message: `连线 ${edge.id} 的端口不存在（${localizePortName(edge.sourceHandle ?? '?')} → ${localizePortName(edge.targetHandle ?? '?')}）`
      });
      continue;
    }
    // 与界面高亮 / 拖放裁决共用 connectionVerdict：重复数据源由上面的端口级问题单独报，这里只判连线本身
    const verdict = connectionVerdict({
      sourceType: sourcePort.dataType,
      targetType: targetPort.dataType,
      sameNode: edge.source === edge.target
    });
    if (!verdict.allowed) {
      issues.push({
        id: `edge-invalid-${edge.id}`,
        severity: 'error',
        source: 'client',
        edgeId: edge.id,
        message: `连线「${nodeLabel(edge.source)}.${portDisplayName(sourcePort.name, sourcePort.label)} → ${nodeLabel(edge.target)}.${portDisplayName(targetPort.name, targetPort.label)}」非法：${verdict.reason}`
      });
    }
  }

  return issues;
}

/** 把 /api/validate 的单条编译错误转成可定位的问题项（尽量解析引用的节点/连线）。 */
export function serverIssueFromMessage(message: string): WorkflowIssue {
  const nodeMatch = /\bnode\s+'([^']+)'/i.exec(message);
  const edgeMatch = /\bedge\s+'([^']+)'/i.exec(message);
  const inputMatch = /\binput\s+'([^'.]+)\.[^']+'/i.exec(message);
  return {
    id: `server-${Date.now()}`,
    severity: 'error',
    source: 'server',
    message,
    nodeId: nodeMatch?.[1] ?? inputMatch?.[1],
    edgeId: edgeMatch?.[1]
  };
}
