import { describe, expect, it } from 'vitest';
import {
  decideSaveBaseline,
  isLateLoadResponse,
  pickExpectedRevision,
  shouldApplySaveResult
} from '../documentSync';

// A03/A04/F05（2026-10-12 第三轮）验收场景的行为测试固化：
// A→B→A、加载中编辑、探测覆盖确认/取消、迟到保存。
describe('document sync decisions (A03/A04/F05)', () => {
  it('baseline is only reused for its own document (F05)', () => {
    expect(pickExpectedRevision({ id: 'A', revision: 'rev-a' }, 'A')).toBe('rev-a');
    expect(pickExpectedRevision({ id: 'A', revision: 'rev-a' }, 'B')).toBeUndefined();
    expect(pickExpectedRevision(undefined, 'A')).toBeUndefined();
  });

  it('late load is discarded after a document switch - incl. A to B to A (A03)', () => {
    const nodes = { v: 1 };
    const edges = { v: 1 };
    // 请求发起于代次 1（文档 A）；期间切到 B（代次 2）再切回 A（代次 3）——
    // 仅比较文档 ID 会放行这个迟到响应，代次判定必须丢弃它。
    expect(isLateLoadResponse(
      { epoch: 1, nodes, edges },
      { epoch: 3, nodes, edges }
    )).toBe('discard-switched');
  });

  it('late load is discarded when the canvas changed during the load (A03)', () => {
    const epoch = 4;
    expect(isLateLoadResponse(
      { epoch, nodes: { v: 1 }, edges: { v: 1 } },
      { epoch, nodes: { v: 2 }, edges: { v: 1 } }
    )).toBe('discard-edited');
    expect(isLateLoadResponse(
      { epoch, nodes: { v: 1 }, edges: { v: 1 } },
      { epoch, nodes: { v: 1 }, edges: { v: 2 } }
    )).toBe('discard-edited');
  });

  it('an unchanged load is applied (A03)', () => {
    const nodes = {};
    const edges = {};
    expect(isLateLoadResponse({ epoch: 7, nodes, edges }, { epoch: 7, nodes, edges })).toBe('apply');
  });

  it('save result only updates the baseline of the same document generation (A03)', () => {
    expect(shouldApplySaveResult({ epoch: 2, id: 'A' }, { epoch: 2, id: 'A' })).toBe(true);
    // A→B→A：ID 相同但代次不同——迟到的保存响应仍不得更新当前基线。
    expect(shouldApplySaveResult({ epoch: 1, id: 'A' }, { epoch: 3, id: 'A' })).toBe(false);
    expect(shouldApplySaveResult({ epoch: 2, id: 'A' }, { epoch: 2, id: 'B' })).toBe(false);
  });

  it('a probed remote revision is never used as an editing baseline without consent (A04)', () => {
    // 本地无基线 + 远端已存在 → 必须先取得用户显式确认，绝不静默覆盖其他编辑者的修改。
    expect(decideSaveBaseline(undefined, { kind: 'exists', revision: 'remote-latest' }, 'B'))
      .toEqual({ action: 'confirm-overwrite', revision: 'remote-latest' });
  });

  it('missing remote document saves as an explicit new-document baseline (A04/A02)', () => {
    expect(decideSaveBaseline(undefined, { kind: 'missing' }, 'B'))
      .toEqual({ action: 'save', baseline: '' });
  });

  it('probe failure aborts the save - never overwrite without a baseline (A04)', () => {
    const decision = decideSaveBaseline(undefined, { kind: 'failed' }, 'B');
    expect(decision.action).toBe('abort');
    if (decision.action === 'abort') expect(decision.reason).toContain('B');
  });

  it('a locally loaded baseline is reused without probing (F05)', () => {
    // 本地有"本次实际加载"的基线时，即使远端探测到别的修订也不改变行为（由服务端 409 兜底并发）。
    expect(decideSaveBaseline('rev-a', { kind: 'exists', revision: 'other' }, 'A'))
      .toEqual({ action: 'save', baseline: 'rev-a' });
  });
});
