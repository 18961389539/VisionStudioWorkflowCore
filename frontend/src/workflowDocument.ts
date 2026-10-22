import type { WorkflowPayload } from './types';

const object = (value: unknown): value is Record<string, unknown> => Boolean(value && typeof value === 'object' && !Array.isArray(value));
const nonBlank = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0;
/** 只检查可安全载入的文档结构；算法、连线语义仍由工作流校验负责。 */
export function isWorkflowDocument(value: unknown): value is WorkflowPayload {
  const nodeIds = new Set<string>();
  const edgeIds = new Set<string>();
  const uniqueId = (id: unknown, ids: Set<string>) => {
    if (!nonBlank(id) || ids.has(id)) return false;
    ids.add(id);
    return true;
  };
  return object(value) && nonBlank(value.id) && typeof value.name === 'string'
    && Array.isArray(value.nodes) && value.nodes.every((node) => object(node) && uniqueId(node.id, nodeIds) && nonBlank(node.type)
      && (node.name == null || typeof node.name === 'string') && (node.parameters == null || object(node.parameters))
      && (node.position == null || (object(node.position) && typeof node.position.x === 'number' && Number.isFinite(node.position.x)
        && typeof node.position.y === 'number' && Number.isFinite(node.position.y))))
    && Array.isArray(value.edges) && value.edges.every((edge) => object(edge)
      && uniqueId(edge.id, edgeIds)
      && ['sourceNodeId', 'sourcePort', 'targetNodeId', 'targetPort'].every((key) => nonBlank(edge[key]))
      && (edge.kind == null || edge.kind === 'data' || edge.kind === 'control'));
}
