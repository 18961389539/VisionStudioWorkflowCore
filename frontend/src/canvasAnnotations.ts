import type { Node } from '@xyflow/react';
import { readStoredJson, STORAGE_KEYS, writeStoredJson } from './storage';

export const CANVAS_GROUP_NODE_TYPE = 'canvasGroup';
export const CANVAS_NOTE_NODE_TYPE = 'canvasNote';
// These values identify colors in existing local annotations; themes supply their display colors.
export const CANVAS_GROUP_COLORS = [
  { value: '#1677ff', token: 'blue', zh: '蓝色', en: 'blue' },
  { value: '#13a8a8', token: 'teal', zh: '青色', en: 'teal' },
  { value: '#52c41a', token: 'green', zh: '绿色', en: 'green' },
  { value: '#fa8c16', token: 'orange', zh: '橙色', en: 'orange' },
  { value: '#eb2f96', token: 'pink', zh: '粉色', en: 'pink' }
] as const;
const GROUP_PADDING = 28;
const DEFAULT_GROUP_SIZE = { width: 240, height: 140 };
const DEFAULT_NODE_SIZE = { width: 220, height: 90 };

type StoredAnnotation = {
  id: string;
  type: typeof CANVAS_GROUP_NODE_TYPE | typeof CANVAS_NOTE_NODE_TYPE;
  position: { x: number; y: number };
  width?: number;
  height?: number;
  title?: string;
  content?: string;
  color?: string;
  memberIds?: string[];
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function parseAnnotations(value: unknown): Record<string, StoredAnnotation[]> | undefined {
  if (!isRecord(value)) return undefined;
  const result: Record<string, StoredAnnotation[]> = {};
  for (const [workflowId, entries] of Object.entries(value)) {
    if (!Array.isArray(entries)) return undefined;
    const parsed: StoredAnnotation[] = [];
    for (const entry of entries) {
      if (!isRecord(entry) || typeof entry.id !== 'string'
        || (entry.type !== CANVAS_GROUP_NODE_TYPE && entry.type !== CANVAS_NOTE_NODE_TYPE)
        || !isRecord(entry.position)
        || typeof entry.position.x !== 'number' || !Number.isFinite(entry.position.x)
        || typeof entry.position.y !== 'number' || !Number.isFinite(entry.position.y)) return undefined;
      parsed.push({
        id: entry.id,
        type: entry.type,
        position: { x: entry.position.x, y: entry.position.y },
        ...(typeof entry.width === 'number' && Number.isFinite(entry.width) ? { width: entry.width } : {}),
        ...(typeof entry.height === 'number' && Number.isFinite(entry.height) ? { height: entry.height } : {}),
        ...(typeof entry.title === 'string' ? { title: entry.title } : {}),
        ...(typeof entry.content === 'string' ? { content: entry.content } : {}),
        ...(typeof entry.color === 'string' ? { color: entry.color } : {}),
        ...(Array.isArray(entry.memberIds) && entry.memberIds.every((id) => typeof id === 'string') ? { memberIds: entry.memberIds as string[] } : {})
      });
    }
    result[workflowId] = parsed;
  }
  return result;
}

function annotationsForWorkflow(workflowId: string): StoredAnnotation[] {
  const all = readStoredJson(STORAGE_KEYS.canvasAnnotations, parseAnnotations, {});
  return all[workflowId] ?? [];
}

export function isCanvasAnnotation(node: Node): boolean {
  return node.type === CANVAS_GROUP_NODE_TYPE || node.type === CANVAS_NOTE_NODE_TYPE;
}

export function isCanvasGroup(node: Node): boolean {
  return node.type === CANVAS_GROUP_NODE_TYPE;
}

export function absoluteNodePosition(node: Node, nodesById: ReadonlyMap<string, Node>): { x: number; y: number } {
  let x = node.position.x;
  let y = node.position.y;
  let parentId = node.parentId;
  const seen = new Set<string>();
  while (parentId && !seen.has(parentId)) {
    seen.add(parentId);
    const parent = nodesById.get(parentId);
    if (!parent) break;
    x += parent.position.x;
    y += parent.position.y;
    parentId = parent.parentId;
  }
  return { x, y };
}

function toStoredAnnotation(node: Node): StoredAnnotation | undefined {
  if (!isCanvasAnnotation(node)) return undefined;
  const style = node.style ?? {};
  const data = node.data;
  if (node.type === CANVAS_GROUP_NODE_TYPE) {
    return {
      id: node.id, type: CANVAS_GROUP_NODE_TYPE, position: node.position,
      width: typeof style.width === 'number' ? style.width : DEFAULT_GROUP_SIZE.width,
      height: typeof style.height === 'number' ? style.height : DEFAULT_GROUP_SIZE.height,
      title: typeof data.title === 'string' ? data.title : '流程分组',
      color: typeof data.color === 'string' ? data.color : CANVAS_GROUP_COLORS[0].value,
      memberIds: Array.isArray(data.memberIds) ? data.memberIds.filter((id): id is string => typeof id === 'string') : []
    };
  }
  return {
    id: node.id, type: CANVAS_NOTE_NODE_TYPE, position: node.position,
    width: typeof style.width === 'number' ? style.width : DEFAULT_NODE_SIZE.width,
    height: typeof style.height === 'number' ? style.height : DEFAULT_NODE_SIZE.height,
    content: typeof data.content === 'string' ? data.content : ''
  };
}

export function saveCanvasAnnotations(workflowId: string, nodes: Node[]): void {
  const all = readStoredJson(STORAGE_KEYS.canvasAnnotations, parseAnnotations, {});
  const annotations = nodes.map(toStoredAnnotation).filter((node): node is StoredAnnotation => node !== undefined);
  if (annotations.length) all[workflowId] = annotations;
  else delete all[workflowId];
  writeStoredJson(STORAGE_KEYS.canvasAnnotations, all);
}

/** Canvas-only annotations are hydrated from local UI storage and never enter WorkflowPayload. */
export function restoreCanvasAnnotations(workflowId: string, workflowNodes: Node[]): Node[] {
  const annotations = annotationsForWorkflow(workflowId);
  if (!annotations.length) return workflowNodes;
  const groups = annotations.filter((item) => item.type === CANVAS_GROUP_NODE_TYPE);
  const annotationIds = new Set(annotations.map((item) => item.id));
  const sourceNodesById = new Map(workflowNodes.map((node) => [node.id, node]));
  const existing = new Map(workflowNodes.filter((node) => !annotationIds.has(node.id))
    .map((node) => [node.id, { ...node, position: { ...node.position } }]));
  const groupNodes: Node[] = groups.map((item) => ({
    id: item.id, type: CANVAS_GROUP_NODE_TYPE, position: item.position, zIndex: -1,
    style: { width: item.width ?? DEFAULT_GROUP_SIZE.width, height: item.height ?? DEFAULT_GROUP_SIZE.height },
    data: { title: item.title ?? '流程分组', color: item.color ?? CANVAS_GROUP_COLORS[0].value, memberIds: item.memberIds ?? [] }
  }));
  for (const group of groupNodes) {
    const members = Array.isArray(group.data.memberIds) ? group.data.memberIds as string[] : [];
    for (const memberId of members) {
      const node = existing.get(memberId);
      if (!node) continue;
      const position = absoluteNodePosition(node, sourceNodesById);
      node.position = { x: position.x - group.position.x, y: position.y - group.position.y };
      node.parentId = group.id;
      node.extent = 'parent';
    }
  }
  const noteNodes: Node[] = annotations.filter((item) => item.type === CANVAS_NOTE_NODE_TYPE).map((item) => ({
    id: item.id, type: CANVAS_NOTE_NODE_TYPE, position: item.position,
    style: { width: item.width ?? DEFAULT_NODE_SIZE.width, minHeight: item.height ?? DEFAULT_NODE_SIZE.height },
    data: { content: item.content ?? '' }
  }));
  return [...groupNodes, ...noteNodes, ...existing.values()];
}

export function createCanvasGroup(nodes: Node[], selectedIds: string[]): { nodes: Node[]; groupId?: string } {
  const selected = nodes.filter((node) => selectedIds.includes(node.id) && !isCanvasAnnotation(node) && !node.parentId);
  if (!selected.length) return { nodes };
  const nodesById = new Map(nodes.map((node) => [node.id, node]));
  const boxes = selected.map((node) => {
    const position = absoluteNodePosition(node, nodesById);
    return {
      node, position,
      width: node.measured?.width ?? DEFAULT_NODE_SIZE.width,
      height: node.measured?.height ?? DEFAULT_NODE_SIZE.height
    };
  });
  const left = Math.min(...boxes.map((box) => box.position.x)) - GROUP_PADDING;
  const top = Math.min(...boxes.map((box) => box.position.y)) - GROUP_PADDING - 24;
  const right = Math.max(...boxes.map((box) => box.position.x + box.width)) + GROUP_PADDING;
  const bottom = Math.max(...boxes.map((box) => box.position.y + box.height)) + GROUP_PADDING;
  const groupId = `canvas-group-${crypto.randomUUID().slice(0, 8)}`;
  const memberIds = boxes.map((box) => box.node.id);
  const group: Node = {
    id: groupId, type: CANVAS_GROUP_NODE_TYPE, position: { x: left, y: top }, zIndex: -1, selected: true,
    style: { width: Math.max(DEFAULT_GROUP_SIZE.width, right - left), height: Math.max(DEFAULT_GROUP_SIZE.height, bottom - top) },
    data: { title: '流程分组', color: CANVAS_GROUP_COLORS[0].value, memberIds }
  };
  const selectedSet = new Set(memberIds);
  const nextNodes = nodes.map((node) => {
    if (!selectedSet.has(node.id)) return { ...node, selected: false };
    const box = boxes.find((item) => item.node.id === node.id)!;
    return {
      ...node, selected: false, parentId: groupId, extent: 'parent' as const,
      position: { x: box.position.x - left, y: box.position.y - top }
    };
  });
  return { nodes: [group, ...nextNodes], groupId };
}

export function createCanvasNote(position: { x: number; y: number }): Node {
  return {
    id: `canvas-note-${crypto.randomUUID().slice(0, 8)}`, type: CANVAS_NOTE_NODE_TYPE, position, selected: true,
    style: { width: DEFAULT_NODE_SIZE.width, minHeight: DEFAULT_NODE_SIZE.height },
    data: { content: '' }
  };
}
