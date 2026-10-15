import { describe, expect, it } from 'vitest';
import { cloneParameters, configurationIssues, parameterFingerprint, withParameter, workflowWithNodeParameters } from '../nodeConfigurationState';
import type { NodeCatalogItem, WorkflowPayload } from '../types';

describe('node configuration snapshots', () => {
  it('compares nested ROI values without depending on object key order', () => {
    expect(parameterFingerprint({ roi: { x: 1, y: 2 }, threshold: 128 })).toBe(parameterFingerprint({ threshold: 128, roi: { y: 2, x: 1 } }));
    expect(parameterFingerprint({ roi: { x: 1 } })).not.toBe(parameterFingerprint({ roi: { x: 2 } }));
  });

  it('clones nested values and removes a cleared ROI without changing the source', () => {
    const source = { roi: { type: 'Rectangle', x: 1, y: 2, width: 3, height: 4 }, threshold: 128 };
    const draft = cloneParameters(source);
    (draft.roi as { width: number }).width = 9;
    expect(source.roi.width).toBe(3);
    expect(withParameter(draft, 'roi', undefined)).toEqual({ threshold: 128 });
  });

  it('constructs an isolated testing workflow for only the configured node', () => {
    const workflow: WorkflowPayload = { id: 'wf', name: 'test', nodes: [
      { id: 'a', type: 'image.synthetic', parameters: { radius: 80 } },
      { id: 'b', type: 'image.threshold', parameters: { threshold: 128 } }
    ], edges: [] };
    const parameters = { threshold: 200, roi: { x: 1 } };
    const preview = workflowWithNodeParameters(workflow, 'b', parameters);
    expect(preview.nodes[0]).toBe(workflow.nodes[0]);
    expect(preview.nodes[1].parameters).toEqual(parameters);
    (preview.nodes[1].parameters!.roi as { x: number }).x = 10;
    expect(parameters.roi.x).toBe(1);
    expect(workflow.nodes[1].parameters).toEqual({ threshold: 128 });
    expect(() => workflowWithNodeParameters(workflow, 'missing', {})).toThrow('不存在');
  });

  it('rejects degenerate ROI geometry as well as invalid active parameters', () => {
    const catalog: NodeCatalogItem = { type: 'image.threshold', displayName: '阈值', category: '图像', inputs: [], outputs: [], parameters: [{ name: 'threshold', label: '阈值', type: 'number', min: 0, max: 255 }] };
    expect(configurationIssues(catalog, { threshold: 100, roi: { type: 'Rectangle', x: 0, y: 0, width: 0, height: 10 } })).toHaveLength(1);
    expect(configurationIssues(catalog, { threshold: 300 })).toHaveLength(1);
    expect(configurationIssues(catalog, { threshold: 100, roi: { type: 'Circle', x: 5, y: 6, radius: 2 } })).toEqual([]);
  });
});
