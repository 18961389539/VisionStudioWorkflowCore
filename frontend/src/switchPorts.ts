import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, PortDescriptor } from './types';

export const SWITCH_CASE_PORTS = Array.from({ length: 16 }, (_, index) => `case${index + 1}`);

type SwitchCase = { port: string; label: string };
type SwitchCasesResult = { cases: SwitchCase[]; legacy: boolean; issue?: undefined }
  | { cases?: undefined; legacy?: undefined; issue: string };

export function readEditableSwitchCases(value: unknown): SwitchCasesResult {
  let parsed = value;
  if (typeof value === 'string') {
    try { parsed = JSON.parse(value); } catch { parsed = undefined; }
  }
  if (!Array.isArray(parsed)) return { issue: '分支列表必须是 JSON 字符串数组或分支对象数组。' };
  if (parsed.length < 1 || parsed.length > SWITCH_CASE_PORTS.length) return { issue: '分支列表需要 1–16 个标签。' };
  if (parsed.every((label): label is string => typeof label === 'string'))
    return { cases: parsed.map((label, index) => ({ port: SWITCH_CASE_PORTS[index], label })), legacy: true };
  if (!parsed.every((entry): entry is SwitchCase => Boolean(entry && typeof entry === 'object'
    && typeof entry.port === 'string' && typeof entry.label === 'string')))
    return { issue: '分支列表必须是 JSON 字符串数组或分支对象数组。' };
  const ports = parsed.map(entry => entry.port);
  if (new Set(ports).size !== ports.length || ports.some(port => !SWITCH_CASE_PORTS.includes(port)))
    return { issue: '分支端口必须是互不重复的 case1–case16。' };
  return { cases: parsed, legacy: false };
}

export function readSwitchCases(value: unknown): SwitchCasesResult {
  const parsed = readEditableSwitchCases(value);
  if (!parsed.cases) return parsed;
  const labels = parsed.cases.map(entry => entry.label);
  if (labels.some(label => !label.trim())) return { issue: '分支标签不能为空。' };
  if (new Set(labels).size !== labels.length) return { issue: '分支标签必须唯一。' };
  return parsed;
}

/** Only expose assigned ports, in display order. Handles keep their IDs when rows move. */
export function switchPorts(outputs: PortDescriptor[], parameters: Record<string, unknown> | undefined): PortDescriptor[] {
  const cases = readSwitchCases(parameters?.casesJson).cases;
  const byName = new Map(outputs.map((port) => [port.name, port]));
  const branches = cases?.flatMap(({ port: name, label }) => {
    const port = byName.get(name);
    return port ? [{ ...port, label }] : [];
  }) ?? [];
  let inserted = false;
  return outputs.flatMap((port) => {
    if (!SWITCH_CASE_PORTS.includes(port.name)) return [port];
    if (inserted) return [];
    inserted = true;
    return branches;
  });
}

export function withSwitchPorts<T extends NodeCatalogItem>(item: T, parameters: Record<string, unknown> | undefined): T {
  if (item.type !== 'flow.switch') return item;
  const casesJson = parameters?.casesJson === undefined
    ? item.parameters.find(parameter => parameter.name === 'casesJson')?.defaultValue
    : parameters.casesJson;
  return { ...item, outputs: switchPorts(item.outputs, { casesJson }) };
}

/** Find the edges a case owns at its switch and, when identifiable, at its matching merge. */
export function switchBranchConnections(nodeId: string, nodes: readonly Node[], edges: readonly Edge[]) {
  const byId = new Map(nodes.map(node => [node.id, node]));
  const label = (id: string) => String(byId.get(id)?.data.label ?? id);
  const control = edges.filter(edge => {
    const kind = edge.data?.kind;
    if (kind === 'control' || kind === 'data') return kind === 'control';
    const outputs = byId.get(edge.source)?.data.outputs as PortDescriptor[] | undefined;
    return outputs?.some(port => port.name === edge.sourceHandle && port.dataType === 'Control');
  });
  const outgoing = new Map<string, Edge[]>();
  for (const edge of control) {
    const list = outgoing.get(edge.source) ?? [];
    list.push(edge);
    outgoing.set(edge.source, list);
  }
  const starts = (outgoing.get(nodeId) ?? []).filter(edge =>
    edge.sourceHandle === 'default' || SWITCH_CASE_PORTS.includes(edge.sourceHandle ?? ''));
  const reachable = (start: string, stop: string) => {
    const visited = new Set([nodeId, stop]);
    const frontier = [start];
    for (const id of frontier) {
      if (visited.has(id)) continue;
      visited.add(id);
      for (const edge of outgoing.get(id) ?? []) frontier.push(edge.target);
    }
    return visited;
  };
  let mergeEdges: Edge[] | undefined;
  if (starts.length >= 2 && new Set(starts.map(edge => edge.sourceHandle)).size === starts.length) {
    for (const merge of nodes.filter(node => node.data.typeKey === 'flow.merge')) {
      const incoming = control.filter(edge => edge.target === merge.id);
      const reach = starts.map(edge => reachable(edge.target, merge.id));
      const matches = starts.map((start, index) => incoming.filter(edge => edge.targetHandle === start.sourceHandle && (
        edge.id === start.id || (edge.source !== nodeId && edge.source !== merge.id && reach[index].has(edge.source)
          && reach.every((visited, other) => other === index || !visited.has(edge.source)))
      )));
      if (!matches.every(group => group.length === 1)) continue;
      if (mergeEdges) { mergeEdges = undefined; break; }
      mergeEdges = matches.flat();
    }
  }
  const result = new Map<string, { edgeIds: string[]; affectedLabels: string[]; mergeResolved: boolean }>();
  for (const [index, edge] of starts.entries()) {
    const port = edge.sourceHandle;
    if (!port || port === 'default') continue;
    const exit = mergeEdges?.[index];
    const branch = result.get(port) ?? { edgeIds: [], affectedLabels: [], mergeResolved: Boolean(exit) };
    branch.edgeIds.push(edge.id);
    branch.affectedLabels.push(label(edge.target));
    if (exit && exit.id !== edge.id) {
      branch.edgeIds.push(exit.id);
      branch.affectedLabels.push(`${label(exit.source)} → ${label(exit.target)}`);
    }
    result.set(port, branch);
  }
  return result;
}

export function removeSwitchBranchEdges(nodeId: string, nodes: readonly Node[], edges: Edge[],
  previousOutputs: readonly PortDescriptor[], nextOutputs: readonly PortDescriptor[]): Edge[] {
  const active = new Set(nextOutputs.filter(port => SWITCH_CASE_PORTS.includes(port.name)).map(port => port.name));
  if (!active.size) return edges;
  const removed = previousOutputs.filter(port => SWITCH_CASE_PORTS.includes(port.name) && !active.has(port.name));
  if (!removed.length) return edges;
  const connections = switchBranchConnections(nodeId, nodes, edges);
  const removedEdges = new Set(removed.flatMap(port => connections.get(port.name)?.edgeIds ?? []));
  return removedEdges.size ? edges.filter(edge => !removedEdges.has(edge.id)) : edges;
}
