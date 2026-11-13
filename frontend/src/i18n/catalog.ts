/**
 * Catalog and runtime-status localization.
 *
 * The backend catalog is authored in English (node display names, categories, parameter
 * labels, options, descriptions, status words). Chinese labels live in `catalog.zh.ts`,
 * which is loaded on demand — English mode needs no table at all.
 *
 * 词表未加载时（英文模式，或 zh chunk 尚未就绪）所有 localize* 一律回落英文原文。
 */
import type { NodeCatalogItem } from '../types';
import type { Lang } from './lang';

export type CatalogLocaleData = {
  nodeNames: Record<string, string>;
  categories: Record<string, string>;
  parameterGroups: Record<string, string>;
  portNames: Record<string, string>;
  descriptions: Record<string, string>;
  parameterDescriptions: Record<string, string>;
  statusNames: Record<string, string>;
  parameterLabels: Record<string, string>;
  optionLabels: Record<string, Record<string, Record<string, string>>>;
  variantLabels: Record<string, Record<string, Record<string, string>>>;
};

let data: CatalogLocaleData | undefined;

/** 由 catalog.zh.ts 在模块求值时调用；重复加载（测试 / HMR）以最后一次为准。 */
export function installCatalogData(next: CatalogLocaleData) {
  data = next;
}

export function hasCatalogData(): boolean {
  return data !== undefined;
}

const degreeAngleOutputTypes = new Set([
  'feature.line', 'measure.fitLine', 'measure.fitEllipse', 'measure.rotatedRect',
  'match.shape', 'match.template', 'image.register', 'geometry.angle'
]);
const degreeAngleErrorTypes = new Set(['measure.parallelism', 'measure.perpendicularity']);
const pixelLengthOutputs: Record<string, string[]> = {
  'measure.wallThickness': ['centerDistance', 'minThickness', 'maxThickness', 'meanThickness', 'stdDev', 'variation'],
  'measure.lineSpacing': ['distance', 'min', 'max', 'stdDev', 'deviation'],
  'measure.contourThickness': ['minThickness', 'maxThickness', 'meanThickness', 'stdDev', 'variation'],
  'measure.blobs': ['x', 'y', 'radius'],
  'region.properties': ['perimeter', 'rectWidth', 'rectHeight', 'equivalentDiameter'],
  'contour.properties': ['perimeter', 'equivalentDiameter']
};
const pixelAreaOutputs: Record<string, string[]> = {
  'measure.blobs': ['area'],
  'measure.regionIntensity': ['area'],
  'region.properties': ['area'],
  'contour.properties': ['area']
};
const grayLevelOutputs: Record<string, string[]> = {
  'measure.regionIntensity': ['stdDev', 'min', 'max']
};

export function canonicalParameterGroup(group?: string | null): string {
  if (!group) return 'General';
  if (!data) return group;
  for (const [source, translated] of Object.entries(data.parameterGroups)) {
    if (translated === group) return source;
  }
  return group;
}

/** Localize a backend catalog for the active language. English is a pass-through.
 *  `lang` defaults to Chinese so pre-existing single-argument call sites keep compiling. */
export function localizeCatalog(items: NodeCatalogItem[], lang: Lang = 'zh'): NodeCatalogItem[] {
  if (lang === 'en' || !data) return items;
  const { nodeNames, categories, descriptions, parameterDescriptions, parameterGroups, parameterLabels, optionLabels, variantLabels } = data;
  return items.map((item) => ({
    ...item,
    displayName: nodeNames[item.type] ?? item.displayName,
    category: categories[item.category] ?? item.category,
    description: descriptions[item.type] ?? item.description,
    inputs: item.inputs.map((port) => ({ ...port, label: item.type === 'output.saveImage' && port.name === 'path' ? '基础保存路径'
      : item.type === 'output.saveImage' && port.name === 'pass' ? '检测合格'
      : item.type === 'output.httpRequest' && port.name === 'body' ? '请求正文'
      : item.type === 'output.httpRequest' && port.name === 'value' ? '请求数据（JSON）'
      : port.name === 'results' && item.type.startsWith('measure.metrology') ? '结果列表' : localizePortName(port.name) })),
    outputs: item.outputs.map((port) => ({ ...port, label: item.type === 'output.saveImage' && port.name === 'path' ? '最终目标路径'
      : item.type === 'output.saveImage' && port.name === 'reason' ? '保存状态'
      : item.type === 'measure.pointLineDistance' && port.name === 'signedDistance' ? '到直线的有向垂距'
      : item.type === 'measure.pointLineDistance' && port.name === 'projectionT' ? '实际投影参数 t'
      : item.type === 'measure.circleSpacing' && port.name === 'contained' ? '包含（含内切）'
      : item.type === 'output.httpRequest' && port.name === 'body' ? '响应正文'
      : port.name === 'angle' && degreeAngleOutputTypes.has(item.type) ? '角度 (°)'
      : port.name === 'angleError' && degreeAngleErrorTypes.has(item.type) ? '角度误差 (°)'
      : item.type === 'measure.caliperArray' && ['widthMean', 'widthMin', 'widthMax', 'widthStd'].includes(port.name) ? `${localizePortName(port.name)} (px)`
      : item.type === 'measure.caliperWidth' && port.name === 'width' ? '宽度 (px)'
      : item.type === 'calibration.planar' && port.name === 'rmse' ? '均方根误差'
      : item.type === 'calibration.planar' && port.name === 'maxError' ? '最大误差'
      : pixelLengthOutputs[item.type]?.includes(port.name) ? `${localizePortName(port.name)} (px)`
      : pixelAreaOutputs[item.type]?.includes(port.name) ? `${localizePortName(port.name)} (px²)`
      : grayLevelOutputs[item.type]?.includes(port.name) ? `${localizePortName(port.name)} (灰度)`
      : port.name === 'results' && item.type.startsWith('measure.metrology') ? '结果列表' : localizePortName(port.name) })),
    parameters: item.parameters.map((parameter) => ({
      ...parameter,
      label: parameterLabels[`${item.type}.${parameter.name}`] ?? parameter.label,
      ...(parameter.variants ? { variants: Object.fromEntries(Object.entries(parameter.variants).map(([key, variant]) => [key, {
        ...variant, label: variant.label ? variantLabels[item.type]?.[parameter.name]?.[key] ?? variant.label : variant.label,
        suggestions: variant.suggestions?.map(option => ({ ...option, label: option.label }))
      }])) } : {}),
      ...(parameter.group ? { group: parameterGroups[parameter.group] ?? parameter.group } : {}),
      description: parameterDescriptions[`${item.type}.${parameter.name}`] ?? parameter.description,
      options: parameter.options?.map((option) => ({ ...option, label: optionLabels[item.type]?.[parameter.name]?.[option.value] ?? option.label }))
    }))
  }));
}

export function localizePortName(name: string, lang: Lang = 'zh'): string {
  if (lang === 'en' || !data) return name;
  const portNames = data.portNames;
  if (portNames[name]) return portNames[name];
  const numbered = /^(case|point)(\d+)$/.exec(name);
  if (numbered) return `${numbered[1] === 'case' ? '分支' : '点'} ${numbered[2]}`;
  return name;
}

export function portDisplayName(name: string, label?: string, lang: Lang = 'zh'): string {
  if (lang === 'en') return label && label !== name ? label : name;
  if (label && label !== name && /[\u3400-\u9fff]/.test(label)) return label;
  const localized = localizePortName(name, lang);
  if (localized !== name) return localized;
  return label && label !== name ? label : name;
}

/** Localize a backend status word (role, health, lifecycle, diff kind, ...). English is a pass-through.
 *  `lang` defaults to Chinese so pre-existing single-argument call sites keep compiling. */
export const localizeStatus = (value: string | null | undefined, lang: Lang = 'zh') => {
  if (!value || lang === 'en' || !data) return value;
  return data.statusNames[value] ?? value;
};

export const localizeNodeType = (type: string, lang: Lang = 'zh') =>
  lang === 'zh' && data ? data.nodeNames[type] ?? type : type;
