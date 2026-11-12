import { describe, expect, it } from 'vitest';
import type { NodeCatalogItem } from '../types';
import { catalogNodeData, editableParameters, makeDefaultParameters, validateCatalog } from '../catalog';
import { fallbackCatalogRaw } from '../catalog';

/**
 * 离线目录校验（2026-11-12）。
 *
 * `validateCatalog` 是应用启动时兜底目录的唯一防线：后端不可用时，损坏的
 * `catalog.generated.json` 会直接决定画布能不能画节点。此前这个文件 21 个分支只覆盖 2 个，
 * 每一条错误分支都是"从没被执行过的代码"——写错了也不会有人知道。
 */
function item(overrides: Partial<NodeCatalogItem> = {}): NodeCatalogItem {
  return {
    type: 'demo.node', displayName: 'Demo', category: 'Demo', description: 'demo',
    inputs: [], outputs: [], parameters: [], actions: null, capabilities: null,
    ...overrides
  } as NodeCatalogItem;
}

const parameter = (name: string, extra: Record<string, unknown> = {}) =>
  ({ name, label: name, type: 'number', defaultValue: name === 'withDefault' ? 7 : undefined, ...extra }) as never;

describe('离线目录校验', () => {
  it('接受真实生成的目录', () => {
    expect(validateCatalog(fallbackCatalogRaw)).toHaveLength(242);
  });

  it('拒绝空/非数组/缺身份字段/端口非数组', () => {
    expect(() => validateCatalog(null)).toThrow('empty or invalid');
    expect(() => validateCatalog([])).toThrow('empty or invalid');
    expect(() => validateCatalog([null])).toThrow('identity metadata');
    expect(() => validateCatalog([item({ type: 1 } as never)])).toThrow('identity metadata');
    expect(() => validateCatalog([item({ displayName: 1 } as never)])).toThrow('identity metadata');
    expect(() => validateCatalog([item({ category: 1 } as never)])).toThrow('identity metadata');
    expect(() => validateCatalog([item({ inputs: null } as never)])).toThrow('invalid ports/parameters');
    expect(() => validateCatalog([item({ outputs: null } as never)])).toThrow('invalid ports/parameters');
    expect(() => validateCatalog([item({ parameters: null } as never)])).toThrow('invalid ports/parameters');
  });

  it('拒绝节点类型重名与参数重名（大小写不敏感）', () => {
    expect(() => validateCatalog([item(), item({ type: 'DEMO.NODE' })])).toThrow('Duplicate catalog node type');
    expect(() => validateCatalog([item({ parameters: [parameter('Gain'), parameter('gain')] })])).toThrow('duplicate parameter');
  });

  it('内部参数不出现在表单与默认值里，帧声明可为空', () => {
    const node = item({
      parameters: [parameter('withDefault'), parameter('noDefault'), parameter('hostOnly', { internal: true })]
    });
    expect(editableParameters(node).map((p) => p.name)).toEqual(['withDefault', 'noDefault']);
    expect(makeDefaultParameters(node)).toEqual({ withDefault: 7 });
    expect(catalogNodeData(node).frames).toBeNull();
    expect(catalogNodeData(item({ frames: [{ name: 'image', label: '图像' }] } as never)).frames).toHaveLength(1);
  });
});
