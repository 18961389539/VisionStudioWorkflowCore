import { describe, expect, it } from 'vitest';
import type { Connection, Edge, Node } from '@xyflow/react';
import { planControlFanout } from '../smartBranching';

const node = (id: string, typeKey: string, x: number, controls: string[] = ['next']): Node => ({
  id,
  position: { x, y: x / 2 },
  data: {
    typeKey,
    inputs: [{ name: 'exec', dataType: 'Control' }],
    outputs: controls.map((name) => ({ name, dataType: 'Control' }))
  }
});
const controlEdge = (id: string, source: string, target: string, sourceHandle = 'next'): Edge => ({
  id, source, target, sourceHandle, targetHandle: 'exec', data: { kind: 'control' }
});
const connection = (source: string, target: string): Connection => ({
  source, sourceHandle: 'next', target, targetHandle: 'exec'
});

describe('smart control branching', () => {
  it('keeps the first control connection direct', () => {
    const nodes = [node('source', 'image.synthetic', 0), node('branch', 'measure.blobs', 300)];
    expect(planControlFanout(connection('source', 'branch'), nodes, [])).toEqual({ kind: 'direct' });
  });

  it('keeps a direct fanout for two independent leaf branches', () => {
    const nodes = [
      node('source', 'region.fromRange', 100),
      node('branch-a', 'measure.blobs', 500),
      node('branch-b', 'region.morphology', 520)
    ];
    const existing = controlEdge('existing', 'source', 'branch-a');
    const plan = planControlFanout(connection('source', 'branch-b'), nodes, [existing]);

    expect(plan).toEqual({ kind: 'direct' });
  });

  it('requires an explicit choice when a branch already has downstream control flow', () => {
    const nodes = [
      node('source', 'region.fromRange', 0),
      node('branch-a', 'measure.blobs', 300),
      node('after-a', 'flow.result', 600),
      node('branch-b', 'region.morphology', 320)
    ];
    const plan = planControlFanout(connection('source', 'branch-b'), nodes, [
      controlEdge('source-a', 'source', 'branch-a'),
      controlEdge('a-next', 'branch-a', 'after-a')
    ]);

    expect(plan.kind).toBe('manual');
    if (plan.kind === 'manual') expect(plan.reason).toContain('显式添加 Parallel/Join');
  });

  it('does not reinterpret dedicated branch outputs as a normal next fanout', () => {
    const nodes = [
      node('if', 'flow.ifBoolean', 0, ['true', 'false']),
      node('branch-a', 'measure.blobs', 300),
      node('branch-b', 'region.morphology', 320)
    ];
    const plan = planControlFanout({
      source: 'if', sourceHandle: 'true', target: 'branch-b', targetHandle: 'exec'
    }, nodes, [controlEdge('true-a', 'if', 'branch-a', 'true')]);

    expect(plan.kind).toBe('manual');
  });
});
