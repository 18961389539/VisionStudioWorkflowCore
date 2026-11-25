import { describe, expect, it } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { computeAutoLayoutPositions } from '../graphLayout';

const node = (id: string, x = 0, y = 0): Node => ({ id, position: { x, y }, data: {} });
const edge = (id: string, source: string, target: string): Edge => ({ id, source, target });

describe('computeAutoLayoutPositions', () => {
  it('arranges DAG layers from left to right without overlapping', () => {
    const nodes = [node('a', 500, 300), node('b', 100, 50), node('c', 120, 200), node('d', 40, 10)];
    const edges = [edge('e1', 'a', 'b'), edge('e2', 'a', 'c'), edge('e3', 'b', 'd'), edge('e4', 'c', 'd')];
    const pos = computeAutoLayoutPositions(nodes, edges);

    expect(pos.get('a')!.x).toBeLessThan(pos.get('b')!.x);
    expect(pos.get('b')!.x).toBe(pos.get('c')!.x);
    expect(pos.get('b')!.y).not.toBe(pos.get('c')!.y);
    expect(pos.get('b')!.x).toBeLessThan(pos.get('d')!.x);
  });

  it('handles cycles and empty graphs gracefully', () => {
    expect(computeAutoLayoutPositions([], []).size).toBe(0);
    const cyclic = computeAutoLayoutPositions([node('x'), node('y')], [edge('1', 'x', 'y'), edge('2', 'y', 'x')]);
    expect(cyclic.size).toBe(2);
    expect(cyclic.get('x')!.x).toBeLessThan(cyclic.get('y')!.x);
  });
});
