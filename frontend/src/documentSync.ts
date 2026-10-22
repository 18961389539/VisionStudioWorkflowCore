/**
 * 编辑器文档同步的纯决策逻辑（A03/A04/F05，2026-10-12 第三轮）。
 *
 * App.tsx 持有 refs（文档代次、并发基线、画布引用），这里把"何时丢弃迟到响应、何时更新基线、
 * 探测结果如何转成保存决策"集中为**可单测的纯函数**——每个决策的输入输出明确，便于用行为测试
 * 固化验收场景：A→B→A 代次区分、加载中编辑、探测覆盖确认/取消、迟到保存。
 */

/** 并发基线：必须绑定它所属的文档 ID（F05——单一字符串会在切换文档后携带错误修订）。 */
export type DocumentBaseline = { id: string; revision: string } | undefined;

/** 迟到响应判定所需的快照（A03）：文档代次 + 画布引用（任何编辑都会替换引用）。 */
export type ResponseGuard = {
  epoch: number;
  nodes: unknown;
  edges: unknown;
};

/** F05：基线只在"属于当前文档"时沿用——否则视为未知（须重建/探测）。 */
export function pickExpectedRevision(baseline: DocumentBaseline, workflowId: string): string | undefined {
  return baseline?.id === workflowId && baseline.revision.trim().length > 0 ? baseline.revision : undefined;
}

/**
 * A03：判断加载响应是否仍可应用。文档代次变化（A→B→A 也无法骗过——代次单调递增）
 * 一律丢弃；同一文档但画布引用已变（加载期间发生编辑）同样丢弃。
 */
export function isLateLoadResponse(
  captured: ResponseGuard,
  current: ResponseGuard
): 'apply' | 'discard-switched' | 'discard-edited' {
  if (captured.epoch !== current.epoch) return 'discard-switched';
  if (captured.nodes !== current.nodes || captured.edges !== current.edges) return 'discard-edited';
  return 'apply';
}

/** 保存成功后仅当文档 ID 与生命周期代次均未变才更新基线；普通编辑不改变该代次。 */
export function shouldApplySaveResult(
  captured: { epoch: number; id: string },
  current: { epoch: number; id: string }
): boolean {
  return captured.epoch === current.epoch && captured.id === current.id;
}

/** A04：基线探测结果——远端不存在 / 已存在 / 探测失败。 */
export type RemoteBaselineProbe =
  | { kind: 'missing' }
  | { kind: 'exists'; revision: string }
  | { kind: 'failed' };

/**
 * Q07（2026-10-13 审查）：重命名决策。**只改名称的编辑也是完整文档编辑**——旧实现的加载
 * 响应保护只比较文档代次与 nodes/edges 引用，重命名三者都不变，加载在途期间的重命名会被
 * 迟到响应覆盖（复现：RENAMED-WHILE-LOADING 被 REMOTE-OLD 覆盖）。名称编辑必须递增加载保护
 * 代次；保存另用整块替换时才递增的生命周期代次。本函数判断是否真的改了名。
 */
export function renameDecision(value: string, currentName: string): { changed: boolean; name: string } {
  const name = value.trim();
  return name.length > 0 && name !== currentName
    ? { changed: true, name }
    : { changed: false, name: currentName };
}

/**
 * A04：保存基线决策。**"仅探测到"的版本不得直接当作编辑基线**：
 *  · 本地有"本次实际加载"的基线 → 直接沿用；
 *  · 远端不存在 → 以"期望新建"提交（后端会核实内容文件确实不存在，A02）；
 *  · 远端已存在 → 覆盖必须由调用方先取得用户的**显式确认**（confirm-overwrite）；
 *  · 探测失败 → 中止（绝不无基线覆盖）。
 */
export type SaveBaselineDecision =
  | { action: 'save'; baseline: string }
  | { action: 'confirm-overwrite'; revision: string }
  | { action: 'abort'; reason: string };

export function decideSaveBaseline(
  localBaseline: string | undefined,
  probe: RemoteBaselineProbe,
  workflowId: string
): SaveBaselineDecision {
  if (localBaseline !== undefined) return { action: 'save', baseline: localBaseline };
  switch (probe.kind) {
    case 'missing':
      return { action: 'save', baseline: '' };
    case 'exists':
      if (!probe.revision.trim()) return { action: 'abort', reason: `工作流 '${workflowId}' 已存在，但响应缺少有效修订号。请检查后端或代理配置后重试。` };
      return { action: 'confirm-overwrite', revision: probe.revision };
    case 'failed':
      return {
        action: 'abort',
        reason: `无法确认后端当前版本（工作流 '${workflowId}' 的基线探测失败）。请检查后端连接后重试。`
      };
  }
}
