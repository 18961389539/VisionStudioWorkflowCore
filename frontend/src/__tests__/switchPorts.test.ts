import { describe, expect, it } from 'vitest';
import type { NodeCatalogItem, PortDescriptor } from '../types';
import { switchPorts, withSwitchPorts } from '../switchPorts';

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
});
