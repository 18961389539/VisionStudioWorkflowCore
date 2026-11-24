import { describe, expect, it } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, PortDescriptor } from '../types';
import { removeSwitchBranchEdges, switchBranchConnections, switchPorts, withSwitchPorts } from '../switchPorts';

const ports: PortDescriptor[] = [
  { name: 'selected', dataType: 'String' },
  { name: 'case1', dataType: 'Control' },
  { name: 'case2', dataType: 'Control' },
  { name: 'case3', dataType: 'Control' },
  { name: 'case4', dataType: 'Control' },
  { name: 'default', dataType: 'Control' }
];
const catalog: NodeCatalogItem = {
  type: 'flow.switch', displayName: 'Switch', category: 'Flow', inputs: [], outputs: ports, parameters: []
};

describe('switch branch ports', () => {
  it.each([undefined, {}])('resolves omitted cases from catalog defaults: %j', (parameters) => {
    const defaulted = { ...catalog, parameters: [{ name: 'casesJson', label: 'Cases', type: 'textarea' as const, defaultValue: '["OK","NG"]' }] };
    expect(withSwitchPorts(defaulted, parameters).outputs.map(port => port.name))
      .toEqual(['selected', 'case1', 'case2', 'default']);
    expect(parameters).toEqual(parameters === undefined ? undefined : {});
  });

  it('uses the current catalog default rather than hard-coding two branches', () => {
    const defaulted = { ...catalog, parameters: [{ name: 'casesJson', label: 'Cases', type: 'textarea' as const, defaultValue: '["A","B","C"]' }] };
    expect(withSwitchPorts(defaulted, {}).outputs.map(port => port.name))
      .toEqual(['selected', 'case1', 'case2', 'case3', 'default']);
  });

  it.each([null, '', '[]', 'not-json', '["A","A"]', '["A",""]'])('does not replace explicitly invalid cases with defaults: %j', (casesJson) => {
    const defaulted = { ...catalog, parameters: [{ name: 'casesJson', label: 'Cases', type: 'textarea' as const, defaultValue: '["OK","NG"]' }] };
    expect(withSwitchPorts(defaulted, { casesJson }).outputs.map(port => port.name)).toEqual(['selected', 'default']);
  });

  it('exposes only the cases declared in casesJson', () => {
    expect(switchPorts(ports, { casesJson: '["OK","NG"]' }).map((port) => port.name))
      .toEqual(['selected', 'case1', 'case2', 'default']);
    expect(switchPorts(ports, { casesJson: '["A","B","C"]' }).map((port) => port.name))
      .toEqual(['selected', 'case1', 'case2', 'case3', 'default']);
  });

  it('drops all case ports while the case list is invalid, instead of advertising unused branches', () => {
    expect(switchPorts(ports, { casesJson: 'not-json' }).map((port) => port.name))
      .toEqual(['selected', 'default']);
    expect(switchPorts(ports, { casesJson: '["A","A"]' }).map((port) => port.name))
      .toEqual(['selected', 'default']);
  });

  it('leaves non-switch catalogs unchanged', () => {
    const other = { ...catalog, type: 'flow.merge' };
    expect(withSwitchPorts(other, { casesJson: '["A"]' })).toBe(other);
    expect(withSwitchPorts(catalog, { casesJson: '["A"]' }).outputs.map((port) => port.name))
      .toEqual(['selected', 'case1', 'default']);
  });

  it('displays labels in edit order without changing sparse or moved handle IDs', () => {
    const casesJson = '[{"port":"case3","label":"Reject"},{"port":"case1","label":"Accept"}]';
    expect(withSwitchPorts(catalog, { casesJson }).outputs.map(({ name, label }) => [name, label])).toEqual([
      ['selected', undefined], ['case3', 'Reject'], ['case1', 'Accept'], ['default', undefined]
    ]);
  });

  it.each([
    '[{"port":"case1","label":"A"},{"port":"case1","label":"B"}]',
    '[{"port":"case17","label":"A"}]',
    '[{"port":"case2","label":"   "}]',
    '["A",{"port":"case2","label":"B"}]'
  ])('hides invalid stable branch configurations: %s', (casesJson) => {
    expect(switchPorts(ports, { casesJson }).map(port => port.name)).toEqual(['selected', 'default']);
  });
});

describe('switch branch graph edits', () => {
  const node = (id: string, typeKey = 'flow.log'): Node => ({ id, position: { x: 0, y: 0 }, data: { typeKey, label: id } });
  const edge = (id: string, source: string, sourceHandle: string, target: string, targetHandle: string, kind = 'control'): Edge =>
    ({ id, source, sourceHandle, target, targetHandle, data: { kind } });
  const nodes = [node('switch', 'flow.switch'), node('inner', 'flow.switch'), node('inner-merge', 'flow.merge'),
    node('outer-merge', 'flow.merge'), ...['inner-a', 'inner-default', 'keep', 'fallback'].map(id => node(id))];
  const graph = [
    edge('remove-entry', 'switch', 'case2', 'inner', 'exec'),
    edge('remove-exit', 'inner-merge', 'next', 'outer-merge', 'case2'),
    edge('keep-entry', 'switch', 'case1', 'keep', 'exec'), edge('keep-exit', 'keep', 'next', 'outer-merge', 'case1'),
    edge('default-entry', 'switch', 'default', 'fallback', 'exec'), edge('default-exit', 'fallback', 'next', 'outer-merge', 'default'),
    edge('inner-a-entry', 'inner', 'case2', 'inner-a', 'exec'), edge('inner-a-exit', 'inner-a', 'next', 'inner-merge', 'case2'),
    edge('inner-default-entry', 'inner', 'default', 'inner-default', 'exec'), edge('inner-default-exit', 'inner-default', 'next', 'inner-merge', 'default'),
    edge('data-edge', 'keep', 'value', 'inner', 'value', 'data')
  ];
  const previous = withSwitchPorts(catalog, { casesJson: '["Keep","Remove"]' }).outputs;

  it('deletes only the removed branch boundary edges, leaving nested control and data edges alone', () => {
    expect(switchBranchConnections('switch', nodes, graph).get('case2')).toEqual({
      edgeIds: ['remove-entry', 'remove-exit'], affectedLabels: ['inner', 'inner-merge → outer-merge'], mergeResolved: true
    });
    const next = withSwitchPorts(catalog, { casesJson: '[{"port":"case1","label":"Keep"}]' }).outputs;
    expect(removeSwitchBranchEdges('switch', nodes, graph, previous, next).map(edge => edge.id))
      .toEqual(graph.slice(2).map(edge => edge.id));
  });

  it('keeps all edges when cases are moved or renamed', () => {
    const next = withSwitchPorts(catalog, { casesJson: '[{"port":"case2","label":"Updated"},{"port":"case1","label":"Keep"}]' }).outputs;
    expect(removeSwitchBranchEdges('switch', nodes, graph, previous, next)).toBe(graph);
  });

  it('only disconnects the entry if the corresponding merge cannot be identified', () => {
    const incomplete = graph.filter(edge => edge.id !== 'keep-exit');
    expect(switchBranchConnections('switch', nodes, incomplete).get('case2')).toEqual({
      edgeIds: ['remove-entry'], affectedLabels: ['inner'], mergeResolved: false
    });
  });
});
