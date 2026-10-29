/**
 * 已从目录移除、加载时必须改写类型的旧节点。
 *
 * 这些类型只是通用节点的一个参数取值（例如高斯/中值滤波 = image.filter 的 method），
 * 后端目录已删除它们。加载旧流程时改写类型并补齐参数，使节点仍能编辑与运行。
 * 与后端 NodeTypeMigration 保持一致。
 *
 * 少数规则需要「取值映射」而不是单纯补参数：image.grayscale 的 mode 决定 image.channel 的 channel。
 */
interface NodeTypeMigrationRule {
  type: string;
  parameters?: Readonly<Record<string, unknown>>;
  /** 旧参数 → 新参数的取值映射；映射成功时旧键被删除。 */
  remap?: { from: string; to: string; values: Readonly<Record<string, string>> };
}

const nodeTypeMigrations: Readonly<Record<string, NodeTypeMigrationRule>> = {
  'image.blur.gaussian': { type: 'image.filter', parameters: { method: 'gaussian' } },
  'image.blur.median': { type: 'image.filter', parameters: { method: 'median' } },
  'image.tophat': { type: 'image.morphology', parameters: { operation: 'whiteTopHat' } },
  'image.bottomhat': { type: 'image.morphology', parameters: { operation: 'blackBottomHat' } },
  'image.morphgradient': { type: 'image.morphology', parameters: { operation: 'gradient' } },
  'region.fromthreshold': { type: 'region.fromRange', parameters: { mode: 'gray' } },
  'region.fromcolorrange': { type: 'region.fromRange', parameters: { mode: 'color' } },
  'measure.perpendicularity': { type: 'measure.parallelism', parameters: { mode: 'perpendicular' } },
  'image.threshold.adaptive': { type: 'image.threshold', parameters: { mode: 'adaptive' } },
  'image.threshold.otsu': { type: 'image.threshold', parameters: { mode: 'otsu' } },
  'text.match': { type: 'text.validate' },
  'text.regex': { type: 'text.validate', parameters: { mode: 'regex', emptyInputPolicy: 'fail' } },
  'measure.blob': { type: 'measure.blobs', parameters: { mode: 'largest' } },
  'image.grayscale': {
    type: 'image.channel',
    remap: {
      from: 'mode',
      to: 'channel',
      values: { luminance: 'gray', bt601: 'gray', gray: 'gray', average: 'average', mean: 'average', max: 'max' }
    }
  }
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
  const merged: Record<string, unknown> = { ...(parameters ?? {}) };

  if (rule.remap) {
    const raw = merged[rule.remap.from];
    if (typeof raw === 'string') {
      const mapped = rule.remap.values[raw.trim().toLowerCase()];
      if (mapped) {
        delete merged[rule.remap.from];
        merged[rule.remap.to] = merged[rule.remap.to] ?? mapped;
      }
    }
  }

  for (const [key, value] of Object.entries(rule.parameters ?? {})) {
    if (merged[key] === undefined) merged[key] = value;
  }
  return { type: rule.type, parameters: merged };
}
