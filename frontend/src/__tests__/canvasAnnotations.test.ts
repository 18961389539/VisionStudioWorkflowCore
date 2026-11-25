import { beforeEach, describe, expect, it } from 'vitest';
import type { Node } from '@xyflow/react';
import { buildWorkflowPayload } from '../appEditorShared';
import {
  createCanvasGroup, createCanvasNote, restoreCanvasAnnotations, saveCanvasAnnotations
} from '../canvasAnnotations';

const workflowNodes: Node[] = [
  { id: 'threshold', type: 'vision', position: { x: 120, y: 180 }, data: { typeKey: 'image.threshold', label: '阈值' } },
  { id: 'output', type: 'vision', position: { x: 420, y: 180 }, data: { typeKey: 'output.saveImage', label: '保存' } }
];

beforeEach(() => window.localStorage.clear());

describe('canvas-only groups and notes', () => {
  it('keeps annotation nodes out of the workflow payload and preserves grouped node positions', () => {
    const grouped = createCanvasGroup(workflowNodes, ['threshold', 'output']);
    expect(grouped.groupId).toBeTruthy();
    const note = createCanvasNote({ x: 700, y: 250 });
    const canvasNodes = [...grouped.nodes, note];
    const payload = buildWorkflowPayload('workflow-a', '示例', canvasNodes, []);

    expect(payload.nodes).toHaveLength(2);
    expect(payload.nodes.map((node) => node.position)).toEqual(workflowNodes.map((node) => node.position));

    saveCanvasAnnotations('workflow-a', canvasNodes);
    const restored = restoreCanvasAnnotations('workflow-a', workflowNodes.map((node) => ({ ...node, position: { ...node.position } })));
    expect(restored.some((node) => node.type === 'canvasGroup')).toBe(true);
    expect(restored.some((node) => node.type === 'canvasNote')).toBe(true);
    expect(restored.find((node) => node.id === 'threshold')?.parentId).toBe(grouped.groupId);
    expect(buildWorkflowPayload('workflow-a', '示例', restored, []).nodes.map((node) => node.position))
      .toEqual(workflowNodes.map((node) => node.position));
  });

  it('ungroup state can be saved without leaving stale local annotations', () => {
    const grouped = createCanvasGroup(workflowNodes, ['threshold']);
    saveCanvasAnnotations('workflow-a', grouped.nodes);
    saveCanvasAnnotations('workflow-a', workflowNodes);

    expect(restoreCanvasAnnotations('workflow-a', workflowNodes)).toEqual(workflowNodes);
  });
});
