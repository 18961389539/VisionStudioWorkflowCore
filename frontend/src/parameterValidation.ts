import type { ParameterDescriptor } from './types';

/** 与后端保持一致：省略值使用默认值，显式配置必须符合目录类型和范围。 */
export function validateParameterValues(
  descriptors: ParameterDescriptor[] | undefined,
  parameters: Record<string, unknown> | undefined
): Array<{ name: string; message: string }> {
  const issues: Array<{ name: string; message: string }> = [];
  for (const parameter of descriptors ?? []) {
    const descriptor = parameterPresentation(parameter, descriptors ?? [], parameters ?? {});
    if (!isParameterVisible(descriptor, descriptors ?? [], parameters ?? {})) continue;
    const configured = parameters?.[descriptor.name];
    const raw = configured === undefined ? descriptor.defaultValue : configured;
    if (descriptor.optional && (raw === undefined || raw === null)) continue;
    if (descriptor.type === 'number') {
      if (raw === undefined || raw === null || raw === '') {
        issues.push({ name: descriptor.name, message: '需要数字（点 ↺ 可恢复默认值）' });
        continue;
      }
      if (typeof raw !== 'number' || !Number.isFinite(raw)) {
        issues.push({ name: descriptor.name, message: '不是有效数字' });
        continue;
      }
      if (descriptor.integer && !Number.isInteger(raw)) {
        issues.push({ name: descriptor.name, message: '必须为整数' });
        continue;
      }
      if (descriptor.min !== undefined && descriptor.min !== null && raw < descriptor.min)
        issues.push({ name: descriptor.name, message: `小于最小值 ${descriptor.min}` });
      else if (descriptor.max !== undefined && descriptor.max !== null && raw > descriptor.max)
        issues.push({ name: descriptor.name, message: `大于最大值 ${descriptor.max}` });
    } else if (descriptor.type === 'select') {
      if (raw === undefined || raw === null || raw === '') {
        issues.push({ name: descriptor.name, message: '必选：尚未选择' });
        continue;
      }
      const options = descriptor.options ?? [];
      if (typeof raw !== 'string' || options.length > 0 && !options.some((option) => option.value.toLowerCase() === raw.toLowerCase()))
        issues.push({ name: descriptor.name, message: '不在可选范围内' });
    } else if (raw !== undefined && descriptor.type === 'boolean' && typeof raw !== 'boolean') {
      issues.push({ name: descriptor.name, message: '需要布尔值（开启或关闭）' });
    } else if (raw !== undefined && (descriptor.type === 'text' || descriptor.type === 'textarea') && typeof raw !== 'string') {
      issues.push({ name: descriptor.name, message: '需要文本' });
    }
  }
  return issues;
}

export function isParameterVisible(
  descriptor: ParameterDescriptor,
  descriptors: ParameterDescriptor[],
  values: Record<string, unknown>
): boolean {
  // internal 参数由画布/宿主维护（ROI 对象、基准图像、离线回放路径），既不是表单字段也不做表单校验。
  if (descriptor.internal) return false;
  const matches = (condition: { parameter: string; values: string[] }) => {
    const controller = descriptors.find((item) => item.name.toLowerCase() === condition.parameter.toLowerCase());
    if (!controller || condition.values.length === 0) return false;
    const actual = values[controller.name] === undefined ? controller.defaultValue : values[controller.name];
    return condition.values.some((expected) => String(actual ?? '').toLowerCase() === expected.toLowerCase());
  };
  return (!descriptor.visibleWhen || matches(descriptor.visibleWhen))
    && (descriptor.visibleWhenAll ?? []).every(matches);
}

export function parameterPresentation(descriptor: ParameterDescriptor, descriptors: ParameterDescriptor[], values: Record<string, unknown>): ParameterDescriptor {
  if (!descriptor.variantParameter || !descriptor.variants) return descriptor;
  const controller = descriptors.find(item => item.name === descriptor.variantParameter);
  const key = String(values[descriptor.variantParameter] ?? controller?.defaultValue ?? '');
  const variant = descriptor.variants[key] ?? Object.entries(descriptor.variants)
    .find(([name]) => name.toLowerCase() === key.toLowerCase())?.[1];
  if (!variant) return descriptor;
  return { ...descriptor, label: variant.label || descriptor.label, description: variant.description || descriptor.description,
    defaultValue: variant.defaultValue ?? descriptor.defaultValue, unit: variant.unit ?? descriptor.unit,
    min: variant.min ?? descriptor.min, max: variant.max ?? descriptor.max, step: variant.step ?? descriptor.step,
    ...(variant.suggestions ? { options: variant.suggestions } : {}) };
}
