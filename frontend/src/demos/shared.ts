import type { Edge, Node } from '@xyflow/react';

export const node = (
  id: string,
  type: string,
  x: number,
  y: number,
  parameters: Record<string, unknown> = {}
): Node => ({
  id,
  type: 'vision',
  position: { x, y },
  data: {
    typeKey: type,
    label: type,
    parameters,
    inputs: [],
    outputs: [],
    status: 'idle'
  }
});

export const control = (
  id: string,
  source: string,
  sourceHandle: string,
  target: string,
  targetHandle: string
): Edge => ({
  id,
  source,
  sourceHandle,
  target,
  targetHandle,
  animated: true,
  data: { kind: 'control' },
  style: { strokeDasharray: '7 5' }
});

export const data = (
  id: string,
  source: string,
  sourceHandle: string,
  target: string,
  targetHandle: string
): Edge => ({ id, source, sourceHandle, target, targetHandle, data: { kind: 'data' } });

export type DemoGraph = { nodes: Node[]; edges: Edge[] };
