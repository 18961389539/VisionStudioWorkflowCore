import type { Connection, Edge, Node } from '@xyflow/react';

type Port = { name: string; dataType: string };

export type ControlFanoutPlan =
  | { kind: 'direct' }
  | { kind: 'manual'; reason: string };

const portsOf = (node: Node | undefined, side: 'inputs' | 'outputs'): Port[] =>
  ((node?.data?.[side] ?? []) as Port[]);

const isControlEdge = (edge: Edge, nodes: readonly Node[]): boolean => {
  if (edge.data?.kind === 'control') return true;
  if (edge.data?.kind === 'data') return false;
  const source = nodes.find((node) => node.id === edge.source);
  return portsOf(source, 'outputs').some((port) => port.name === edge.sourceHandle && port.dataType === 'Control');
};

export function planControlFanout(
  connection: Connection,
  nodes: readonly Node[],
  edges: readonly Edge[]
): ControlFanoutPlan {
  const source = nodes.find((node) => node.id === connection.source);
  const sourcePort = portsOf(source, 'outputs').find((port) => port.name === connection.sourceHandle);
  if (!source) return { kind: 'manual', reason: '源节点已变化，请重新发起连接' };
  if (sourcePort?.dataType !== 'Control') return { kind: 'direct' };

  const outgoing = edges.filter((edge) =>
    edge.source === connection.source
    && edge.sourceHandle === connection.sourceHandle
    && isControlEdge(edge, nodes)
  );
  if (outgoing.length === 0) return { kind: 'direct' };
  if (outgoing.length > 1) {
    return { kind: 'manual', reason: '该控制端口已有多条分支，先删除或重组现有连接后再操作' };
  }

  const existingEdge = outgoing[0]!;
  const branch1 = existingEdge.target;
  const branch2 = connection.target;
  if (branch1 === branch2) {
    return { kind: 'manual', reason: '两个控制分支不能指向同一个节点；请先确认是否需要条件分支或并行分支' };
  }

  const targetIncoming = edges.filter((edge) =>
    edge.target === branch2
    && edge.targetHandle === connection.targetHandle
    && isControlEdge(edge, nodes)
  );
  if (targetIncoming.length > 0) {
    return { kind: 'manual', reason: '目标控制输入已连接，不能再自动并行；请先清理现有控制线' };
  }

  const controlOutputs = portsOf(source, 'outputs').filter((port) => port.dataType === 'Control');
  if (controlOutputs.length !== 1 || sourcePort.name !== 'next' || String(source.data.typeKey) === 'flow.break') {
    return {
      kind: 'manual',
      reason: '此节点具有特殊分支语义，无法安全猜测；请使用现有条件/循环端口或显式 Parallel 节点'
    };
  }

  const branchNodes = [nodes.find((node) => node.id === branch1), nodes.find((node) => node.id === branch2)];
  if (branchNodes.some((node) => !node)) {
    return { kind: 'manual', reason: '分支目标已变化，请重新选择连接目标' };
  }
  const branchHasContinuation = (node: Node | undefined) => edges.some((edge) =>
    edge.source === node?.id && isControlEdge(edge, nodes)
  );
  if (branchNodes.some(branchHasContinuation)) {
    return {
      kind: 'manual',
      reason: '分支目标已经包含后续控制流程，自动插入汇合点可能改变执行顺序；请在需要的位置显式添加 Parallel/Join'
    };
  }
  if (branchNodes.some((node) => !portsOf(node, 'outputs').some((port) => port.name === 'next' && port.dataType === 'Control'))) {
    return {
      kind: 'manual',
      reason: '分支终点没有可连接的“下一步”控制端口，无法自动添加 Join；请显式编排分支'
    };
  }

  return { kind: 'direct' };
}
