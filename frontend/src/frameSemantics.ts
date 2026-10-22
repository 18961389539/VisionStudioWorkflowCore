import type { NodeCatalogItem, NodeFrameRule, ParameterDescriptor } from './types';

/**
 * 旧目录没有帧声明时的兼容集合（2026-10-22 之前的版本）。
 * 新目录由 `frames` 驱动；这里只在目录缺少声明时兜底，插件节点不会再被类型名猜测。
 */
export const legacyFrameChangingNodeTypes = new Set([
  'image.crop', 'image.resize', 'image.rotate', 'image.perspective', 'image.warpaffine', 'image.undistort', 'image.register'
]);

type Parameters = Record<string, unknown> | undefined;

function parameterValue(parameters: Parameters, descriptors: ParameterDescriptor[] | undefined, name: string): unknown {
  if (parameters && name in parameters) return parameters[name];
  const descriptor = descriptors?.find((item) => item.name.toLowerCase() === name.toLowerCase());
  return descriptor?.defaultValue;
}

/** 首条条件匹配的帧规则；没有声明、或没有任何规则匹配时返回 undefined。 */
export function resolveFrameRule(
  frames: NodeFrameRule[] | null | undefined,
  parameters: Parameters,
  descriptors?: ParameterDescriptor[]
): NodeFrameRule | undefined {
  if (!frames?.length) return undefined;
  for (const rule of frames) {
    if (!rule.whenParameter) return rule;
    const actual = String(parameterValue(parameters, descriptors, rule.whenParameter) ?? '').toLowerCase();
    if ((rule.whenValues ?? []).some((value) => String(value).toLowerCase() === actual)) return rule;
  }
  return undefined;
}

/**
 * 目录声明是否改变图像像素帧。
 * 返回 undefined 表示该节点没有帧声明（调用方按旧集合兜底）；false 表示声明为保持输入帧。
 */
export function declaredFrameChange(
  frames: NodeFrameRule[] | null | undefined,
  parameters: Parameters,
  descriptors?: ParameterDescriptor[]
): boolean | undefined {
  if (!frames || frames.length === 0) return undefined;
  const rule = resolveFrameRule(frames, parameters, descriptors);
  return rule ? rule.policy !== 'PreserveInput' : false;
}

/** 节点目录项是否改变图像帧；旧目录无声明时回退到类型集合。 */
export function catalogItemChangesImageFrame(item: NodeCatalogItem, parameters?: Parameters): boolean {
  const declared = declaredFrameChange(item.frames, parameters, item.parameters);
  return declared ?? legacyFrameChangingNodeTypes.has(item.type.toLowerCase());
}
