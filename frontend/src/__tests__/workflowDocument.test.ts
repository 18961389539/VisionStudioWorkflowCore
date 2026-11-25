import { describe, expect, it } from 'vitest';
import { isValidWorkflowId, isWorkflowDocument, parseWorkflowImportJson, suggestWorkflowId } from '../workflowDocument';

const node = { id: 'n1', type: 'image.synthetic' };
const edge = { id: 'e1', sourceNodeId: 'n1', sourcePort: 'image', targetNodeId: 'n2', targetPort: 'image' };
const workflow = { id: 'A', name: 'Workflow', nodes: [node, { ...node, id: 'n2' }], edges: [edge] };

describe('workflow document structure', () => {
  it('accepts empty workflows and optional node metadata', () => {
    expect(isWorkflowDocument({ ...workflow, nodes: [], edges: [] })).toBe(true);
    expect(isWorkflowDocument(workflow)).toBe(true);
    expect(isWorkflowDocument({ ...workflow, nodes: [{ ...node, name: null, position: null, parameters: null }] })).toBe(true);
  });

  it.each(['', ' ', '\t'])('rejects blank identifiers and node types: %j', (value) => {
    expect(isWorkflowDocument({ ...workflow, id: value })).toBe(false);
    for (const key of ['id', 'type']) {
      expect(isWorkflowDocument({ ...workflow, nodes: [{ ...node, [key]: value }] })).toBe(false);
    }
    for (const key of ['id', 'sourceNodeId', 'targetNodeId', 'sourcePort', 'targetPort']) {
      expect(isWorkflowDocument({ ...workflow, edges: [{ ...edge, [key]: value }] })).toBe(false);
    }
  });

  it('rejects duplicate node and edge identities', () => {
    expect(isWorkflowDocument({ ...workflow, nodes: [node, { ...node, type: 'image.threshold' }] })).toBe(false);
    expect(isWorkflowDocument({ ...workflow, edges: [edge, { ...edge, targetNodeId: 'n1' }] })).toBe(false);
  });

  it('keeps node and edge identity namespaces separate and case-sensitive', () => {
    expect(isWorkflowDocument({ ...workflow, nodes: [node, { ...node, id: 'N1' }], edges: [{ ...edge, id: 'n1' }] })).toBe(true);
  });

  it('leaves unknown tools, dangling edges and cycles to semantic validation', () => {
    expect(isWorkflowDocument({ ...workflow, nodes: [{ ...node, type: 'plugin.unknown' }] })).toBe(true);
    expect(isWorkflowDocument({ ...workflow, edges: [{ ...edge, targetNodeId: 'n1' }] })).toBe(true);
  });

  it.each([NaN, Infinity, -Infinity])('rejects non-finite positions: %s', (x) => {
    expect(isWorkflowDocument({ ...workflow, nodes: [{ ...node, position: { x, y: 0 } }] })).toBe(false);
  });

  it('validates workflow IDs and parses imported JSON workflows', () => {
    expect(isValidWorkflowId('line-a_01')).toBe(true);
    expect(isValidWorkflowId('bad.id')).toBe(false);
    expect(isValidWorkflowId('')).toBe(false);
    expect(suggestWorkflowId('Line A Demo')).toBe('line-a-demo');
    expect(isValidWorkflowId(suggestWorkflowId('产线流程'))).toBe(true);

    expect(parseWorkflowImportJson('not json').error).toContain('JSON');
    expect(parseWorkflowImportJson(JSON.stringify({ id: 'x' })).error).toContain('有效的工作流文档');

    const parsed = parseWorkflowImportJson(JSON.stringify({
      id: 'bad.id',
      name: '  Imported Flow  ',
      nodes: [node],
      edges: []
    }));
    expect(parsed.error).toBeUndefined();
    expect(parsed.workflow?.name).toBe('Imported Flow');
    expect(isValidWorkflowId(parsed.workflow!.id)).toBe(true);
  });
});
