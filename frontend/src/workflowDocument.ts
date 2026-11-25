import type { WorkflowPayload } from './types';

export const WORKFLOW_ID_PATTERN = /^[A-Za-z0-9_-]{1,128}$/;

export function isValidWorkflowId(id: string): boolean {
  return WORKFLOW_ID_PATTERN.test(id);
}

export function suggestWorkflowId(name: string, fallbackPrefix = 'workflow'): string {
  const asciiSlug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 64);
  if (asciiSlug && isValidWorkflowId(asciiSlug)) return asciiSlug;
  return `${fallbackPrefix}-${crypto.randomUUID().slice(0, 8)}`;
}

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

export function parseWorkflowImportJson(raw: string): { workflow?: WorkflowPayload; error?: string } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { error: 'JSON 格式无效，请检查文件内容。' };
  }
  if (!isWorkflowDocument(parsed)) {
    return { error: '文件不是有效的工作流文档（需包含 id、name、nodes、edges）。' };
  }
  const normalizedId = isValidWorkflowId(parsed.id) ? parsed.id : suggestWorkflowId(parsed.name || parsed.id);
  const normalizedName = parsed.name.trim() || normalizedId;
  return { workflow: { ...parsed, id: normalizedId, name: normalizedName } };
}

export function downloadWorkflowJson(workflow: WorkflowPayload): void {
  const safeName = isValidWorkflowId(workflow.id) ? workflow.id : suggestWorkflowId(workflow.name || 'workflow');
  const blob = new Blob([JSON.stringify(workflow, null, 2)], { type: 'application/json;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = `${safeName}.json`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}
