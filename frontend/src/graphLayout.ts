import type { Edge, Node } from '@xyflow/react';

const START_X = 80;
const START_Y = 80;
const LAYER_GAP_X = 340;
const ROW_GAP_Y = 200;

/**
 * 分层拓扑自动排布（纯函数，无外部依赖）：
 * 按依赖边分配横向层级，同层按父节点平均纵坐标排序以减少连线交叉；
 * 对存在环路的图按原顺序打破环，保证不会死循环。
 */
export function computeAutoLayoutPositions(nodes: Node[], edges: Edge[]): Map<string, { x: number; y: number }> {
  const positions = new Map<string, { x: number; y: number }>();
  if (nodes.length === 0) return positions;

  const nodeIds = new Set(nodes.map((node) => node.id));
  const incoming = new Map<string, string[]>();
  const outgoing = new Map<string, string[]>();
  const inDegree = new Map<string, number>();

  for (const node of nodes) {
    incoming.set(node.id, []);
    outgoing.set(node.id, []);
    inDegree.set(node.id, 0);
  }

  for (const edge of edges) {
    if (!nodeIds.has(edge.source) || !nodeIds.has(edge.target) || edge.source === edge.target) continue;
    const targets = outgoing.get(edge.source)!;
    if (!targets.includes(edge.target)) {
      targets.push(edge.target);
      incoming.get(edge.target)!.push(edge.source);
      inDegree.set(edge.target, (inDegree.get(edge.target) ?? 0) + 1);
    }
  }

  const orderIndex = new Map(nodes.map((node, index) => [node.id, index]));
  const queue = nodes
    .filter((node) => (inDegree.get(node.id) ?? 0) === 0)
    .map((node) => node.id);
  const topoOrder: string[] = [];
  const visited = new Set<string>();

  while (topoOrder.length < nodes.length) {
    if (queue.length === 0) {
      const nextUnvisited = nodes.find((node) => !visited.has(node.id));
      if (!nextUnvisited) break;
      queue.push(nextUnvisited.id);
    }
    const id = queue.shift()!;
    if (visited.has(id)) continue;
    visited.add(id);
    topoOrder.push(id);

    for (const target of outgoing.get(id) ?? []) {
      const remaining = (inDegree.get(target) ?? 1) - 1;
      inDegree.set(target, remaining);
      if (remaining <= 0 && !visited.has(target)) queue.push(target);
    }
  }

  const layerOf = new Map<string, number>();
  for (const id of topoOrder) {
    const parents = (incoming.get(id) ?? []).filter((parentId) => layerOf.has(parentId));
    const layer = parents.length === 0
      ? 0
      : Math.max(...parents.map((parentId) => layerOf.get(parentId)! + 1));
    layerOf.set(id, layer);
  }

  const layers = new Map<number, string[]>();
  for (const id of topoOrder) {
    const layer = layerOf.get(id) ?? 0;
    const bucket = layers.get(layer) ?? [];
    bucket.push(id);
    layers.set(layer, bucket);
  }

  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const sortedLayers = [...layers.keys()].sort((a, b) => a - b);

  for (const layer of sortedLayers) {
    const ids = layers.get(layer)!;
    ids.sort((a, b) => {
      const parentsA = (incoming.get(a) ?? []).filter((pid) => positions.has(pid));
      const parentsB = (incoming.get(b) ?? []).filter((pid) => positions.has(pid));
      const avgA = parentsA.length > 0
        ? parentsA.reduce((sum, pid) => sum + positions.get(pid)!.y, 0) / parentsA.length
        : (nodeById.get(a)?.position.y ?? 0);
      const avgB = parentsB.length > 0
        ? parentsB.reduce((sum, pid) => sum + positions.get(pid)!.y, 0) / parentsB.length
        : (nodeById.get(b)?.position.y ?? 0);
      if (avgA !== avgB) return avgA - avgB;
      return (orderIndex.get(a) ?? 0) - (orderIndex.get(b) ?? 0);
    });

    ids.forEach((id, row) => {
      positions.set(id, {
        x: START_X + layer * LAYER_GAP_X,
        y: START_Y + row * ROW_GAP_Y,
      });
    });
  }

  return positions;
}
