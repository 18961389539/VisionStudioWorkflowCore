/** 仍在目录里、但已由通用节点替代的旧类型 → 推荐替代类型（画布/配置页显示“兼容”提示）。 */
export const legacyNodeReplacements: Readonly<Record<string, string>> = {
  'image.grayscale': 'image.channel',
  'image.threshold.adaptive': 'image.threshold',
  'image.threshold.otsu': 'image.threshold',
  'text.match': 'text.validate',
  'text.regex': 'text.validate',
  'region.fromcolorrange': 'region.fromRange',
  'measure.blob': 'measure.blobs'
};

export function legacyNodeReplacement(type: string | undefined): string | undefined {
  return type ? legacyNodeReplacements[type.toLowerCase()] : undefined;
}

/**
 * 已从目录移除、加载时必须改写类型的旧节点。
 *
 * 这些类型只是通用节点的一个参数取值（例如高斯/中值滤波 = image.filter 的 method），
 * 后端目录已删除它们。加载旧流程时改写类型并补齐参数，使节点仍能编辑与运行；
 * 端口与规范节点完全一致，所以连线不需要重写。与后端 NodeTypeMigration 保持一致。
 */
const nodeTypeMigrations: Readonly<Record<string, { type: string; parameters: Readonly<Record<string, unknown>> }>> = {
  'image.blur.gaussian': { type: 'image.filter', parameters: { method: 'gaussian' } },
  'image.blur.median': { type: 'image.filter', parameters: { method: 'median' } },
  'image.tophat': { type: 'image.morphology', parameters: { operation: 'whiteTopHat' } },
  'image.bottomhat': { type: 'image.morphology', parameters: { operation: 'blackBottomHat' } },
  'image.morphgradient': { type: 'image.morphology', parameters: { operation: 'gradient' } },
  'region.fromthreshold': { type: 'region.fromRange', parameters: { mode: 'gray' } },
  'measure.perpendicularity': { type: 'measure.parallelism', parameters: { mode: 'perpendicular' } }
};

/** 该类型是否已从目录移除、需要改写。 */
export function isMigratedNodeType(type: string | undefined): boolean {
  return Boolean(type && nodeTypeMigrations[type.toLowerCase()]);
}

/** 改写单个节点：补齐参数但保留旧文档里已有的同名值。不是旧类型时原样返回。 */
export function migrateNodeType(
  type: string,
  parameters?: Record<string, unknown>
): { type: string; parameters: Record<string, unknown> } {
  const rule = nodeTypeMigrations[type.toLowerCase()];
  if (!rule) return { type, parameters: parameters ?? {} };
  return { type: rule.type, parameters: { ...rule.parameters, ...(parameters ?? {}) } };
}
