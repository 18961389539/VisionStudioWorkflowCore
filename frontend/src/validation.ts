import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, ParameterDescriptor, PortDescriptor } from './types';

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

/** 端口类型兼容规则（连线校验 / 连接中的端口高亮 / 边类型预检共用同一套）。 */
export function portsCompatible(sourceType?: string, targetType?: string): boolean {
  if (!sourceType || !targetType) return false;
  return sourceType === targetType
    || sourceType === 'Any'
    || targetType === 'Any'
    || (sourceType === 'Integer' && targetType === 'Double');
}

function portsOf(node: Node | undefined, key: 'inputs' | 'outputs'): PortDescriptor[] {
  return ((node?.data?.[key] ?? []) as PortDescriptor[]);
}

/** 单个节点的参数值预检：数字缺失/非法/越界、选择项越界。 */
export function validateParameterValues(
  descriptors: ParameterDescriptor[] | undefined,
  parameters: Record<string, unknown> | undefined
): Array<{ name: string; message: string }> {
  const issues: Array<{ name: string; message: string }> = [];
  for (const descriptor of descriptors ?? []) {
    const raw = parameters?.[descriptor.name];
    if (descriptor.type === 'number') {
      if (raw === undefined || raw === null || raw === '') {
        issues.push({ name: descriptor.name, message: '需要数字（点 ↺ 可恢复默认值）' });
        continue;
      }
      const value = typeof raw === 'number' ? raw : Number(raw);
      if (!Number.isFinite(value)) {
        issues.push({ name: descriptor.name, message: '不是有效数字' });
        continue;
      }
      if (descriptor.min !== undefined && descriptor.min !== null && value < descriptor.min)
        issues.push({ name: descriptor.name, message: `小于最小值 ${descriptor.min}` });
      else if (descriptor.max !== undefined && descriptor.max !== null && value > descriptor.max)
        issues.push({ name: descriptor.name, message: `大于最大值 ${descriptor.max}` });
    } else if (descriptor.type === 'select') {
      if (raw === undefined || raw === null || raw === '') {
        issues.push({ name: descriptor.name, message: '必选：尚未选择' });
        continue;
      }
      const options = descriptor.options ?? [];
      if (options.length > 0 && !options.some((option) => String(option.value) === String(raw)))
        issues.push({ name: descriptor.name, message: '不在可选范围内' });
    }
  }
  return issues;
}

/** 全图客户端预检：类型缺失 / 参数值 / 必需输入 / 重复数据源 / 边的端点、端口与类型。 */
export function validateWorkflow(nodes: Node[], catalog: NodeCatalogItem[], edges: Edge[]): WorkflowIssue[] {
  const issues: WorkflowIssue[] = [];
  const byType = new Map(catalog.map((item) => [item.type.toLowerCase(), item]));
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const nodeLabel = (id: string) => String(byId.get(id)?.data.label ?? id);

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
    for (const parameterIssue of validateParameterValues(descriptors, parameters)) {
      const label = descriptors?.find((d) => d.name === parameterIssue.name)?.label ?? parameterIssue.name;
      issues.push({
        id: `param-${node.id}-${parameterIssue.name}`,
        severity: 'error',
        source: 'client',
        nodeId: node.id,
        parameter: parameterIssue.name,
        message: `节点「${nodeLabel(node.id)}」参数 ${label}：${parameterIssue.message}`
      });
    }

    const dataEdges = edges.filter((edge) => edge.target === node.id && edge.data?.kind === 'data');
    for (const port of portsOf(node, 'inputs')) {
      if (!port.required) continue;
      if (!dataEdges.some((edge) => edge.targetHandle === port.name)) {
        issues.push({
          id: `port-missing-${node.id}-${port.name}`,
          severity: 'error',
          source: 'client',
          nodeId: node.id,
          message: `节点「${nodeLabel(node.id)}」的必需输入 ${port.name}（${port.dataType}）未连接`
        });
      }
    }
    const counts = new Map<string, number>();
    for (const edge of dataEdges) counts.set(edge.targetHandle ?? '', (counts.get(edge.targetHandle ?? '') ?? 0) + 1);
    for (const [port, count] of counts) {
      if (count > 1) {
        issues.push({
          id: `port-dup-${node.id}-${port}`,
          severity: 'error',
          source: 'client',
          nodeId: node.id,
          message: `节点「${nodeLabel(node.id)}」的输入 ${port} 连接了 ${count} 条数据线（只允许 1 条）`
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
        message: `连线 ${edge.id} 的端口不存在（${edge.sourceHandle ?? '?'} → ${edge.targetHandle ?? '?'}）`
      });
      continue;
    }
    const sourceIsControl = sourcePort.dataType === 'Control';
    const targetIsControl = targetPort.dataType === 'Control';
    if (sourceIsControl !== targetIsControl) {
      issues.push({
        id: `edge-kind-${edge.id}`,
        severity: 'error',
        source: 'client',
        edgeId: edge.id,
        message: `连线 ${edge.id} 混接控制与数据端口（${sourcePort.dataType} → ${targetPort.dataType}）`
      });
    } else if (!sourceIsControl && !portsCompatible(sourcePort.dataType, targetPort.dataType)) {
      issues.push({
        id: `edge-type-${edge.id}`,
        severity: 'error',
        source: 'client',
        edgeId: edge.id,
        message: `连线 ${edge.id} 类型不匹配：${sourcePort.dataType} → ${targetPort.dataType}`
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
