/** 已由通用节点替代、仅用于加载旧流程的节点类型 → 推荐替代类型。 */
export const legacyNodeReplacements: Readonly<Record<string, string>> = {
  'image.blur.gaussian': 'image.filter',
  'image.blur.median': 'image.filter',
  'image.tophat': 'image.morphology',
  'image.bottomhat': 'image.morphology',
  'image.morphgradient': 'image.morphology',
  'image.grayscale': 'image.channel',
  'image.threshold.adaptive': 'image.threshold',
  'image.threshold.otsu': 'image.threshold',
  'text.match': 'text.validate',
  'text.regex': 'text.validate',
  'region.fromthreshold': 'region.fromRange',
  'region.fromcolorrange': 'region.fromRange',
  'measure.blob': 'measure.blobs'
};

export function legacyNodeReplacement(type: string | undefined): string | undefined {
  return type ? legacyNodeReplacements[type.toLowerCase()] : undefined;
}
