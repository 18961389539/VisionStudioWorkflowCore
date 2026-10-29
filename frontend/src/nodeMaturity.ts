import type { NodeCatalogItem } from './types';
import type { Lang } from './i18n/lang';

/**
 * 节点成熟度的前端口径（2026-10-29）。
 *
 * 后端只在**非 Stable** 时写 `maturity` 字段（Stable 是默认值，不写入 JSON），
 * 所以这里必须把"字段缺失"当成 Stable——不要用 `item.maturity === 'Stable'` 判断。
 * 枚举用 PascalCase 序列化（后端全局 JsonStringEnumConverter），这里统一转小写兼容。
 */
export type MaturityLevel = 'stable' | 'preview' | 'experimental';

/**
 * 取成熟度等级。参数用 `string` 而不是 `NodeMaturity`：这个函数是**边界**，
 * 要能接住后端枚举大小写变化、旧快照缺字段、插件塞进来的未知值。
 */
export function maturityLevel(item: { maturity?: string | null } | null | undefined): MaturityLevel {
  const raw = String(item?.maturity ?? '').trim().toLowerCase();
  if (raw === 'experimental') return 'experimental';
  if (raw === 'preview') return 'preview';
  return 'stable';
}

/** 徽标上的短标签；Stable 不画徽标，因此不返回文本。 */
export function maturityLabel(level: MaturityLevel, lang: Lang): string {
  if (level === 'experimental') return lang === 'en' ? 'Experimental' : '实验';
  if (level === 'preview') return lang === 'en' ? 'Preview' : '预览';
  return '';
}

/** 悬停说明：讲清"为什么降级、该注意什么"，而不只是重复标签。 */
export function maturityTitle(level: MaturityLevel, lang: Lang): string {
  if (level === 'experimental') {
    return lang === 'en'
      ? 'Experimental: the algorithm path has no behavioural baseline (only contract/error-path tests). Verify against your own parts before using it on a line.'
      : '实验节点：算法路径没有行为基线（只有契约与错误路径测试），上线前必须用自家料件自行验证。';
  }
  if (level === 'preview') {
    return lang === 'en'
      ? 'Preview: usable, but the documentation states a limited scope (approximate algorithm, subset of features, or not for compliance reports).'
      : '预览节点：可用，但文档写明了适用范围（近似算法、只支持部分工况，或不能用于合规判定）。';
  }
  return lang === 'en'
    ? 'Stable: behavioural tests cover the algorithm and the documentation states no scope limit.'
    : '稳定节点：有覆盖算法的行为测试，文档没有适用范围保留。';
}
