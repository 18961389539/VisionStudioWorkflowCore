import { describe, expect, it } from 'vitest';
import { fallbackCatalogRaw } from '../catalog';
import { localizeCatalog } from '../i18n/catalog';

/**
 * Fill-rate for a node: Chinese name + description + every parameter label + every option label.
 * A slot counts only when Chinese mode replaces the English catalog text with a string that
 * contains Han characters. English mode is a pass-through and is not part of the ratio.
 */
const targets = [
  'coordinate.pose', 'coordinate.point', 'flow.if', 'logic.boolean', 'value.arithmetic',
  'collection.detections.sort', 'image.grayscale', 'device.waitTag', 'match.template', 'value.range'
];

const han = /[\u4e00-\u9fff]/;

function localizedSlots(type: string) {
  const raw = fallbackCatalogRaw.find(item => item.type === type)!;
  const translated = localizeCatalog(fallbackCatalogRaw, 'zh').find(item => item.type === type)!;
  const english = localizeCatalog(fallbackCatalogRaw, 'en').find(item => item.type === type)!;
  const slots: { name: string; filled: boolean }[] = [
    { name: 'name', filled: translated.displayName !== raw.displayName && han.test(translated.displayName) },
    { name: 'description', filled: !!translated.description && translated.description !== raw.description && han.test(translated.description) }
  ];
  raw.parameters.filter((parameter) => !parameter.internal).forEach((parameter) => {
    const index = raw.parameters.indexOf(parameter);
    const localized = translated.parameters[index];
    slots.push({
      name: `param:${parameter.name}`,
      filled: localized.label !== parameter.label && han.test(localized.label)
    });
    expect(localized.description ?? '', `${type}.${parameter.name} description`).toMatch(han);
    (parameter.options ?? []).forEach((option, optionIndex) => {
      const localizedOption = localized.options?.[optionIndex];
      slots.push({
        name: `option:${option.label}`,
        filled: !!localizedOption && localizedOption.label !== option.label && han.test(localizedOption.label) && localizedOption.value === option.value
      });
    });
  });
  expect(english.displayName).toBe(raw.displayName);
  expect(english.description).toBe(raw.description);
  return slots;
}

describe('workflow node Chinese localization', () => {
  it.each(targets)('%s is above 80% name, description, parameter and option coverage', (type) => {
    const slots = localizedSlots(type);
    const missing = slots.filter(slot => !slot.filled).map(slot => slot.name);
    const ratio = slots.filter(slot => slot.filled).length / slots.length;
    expect(missing, `${type} ${(ratio * 100).toFixed(1)}%`).toEqual([]);
    expect(ratio).toBeGreaterThan(0.8);
  });

  it('localizes every builtin node name, description, parameter label and non-native option', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    const problems: string[] = [];
    for (const item of zh) {
      if (!han.test(item.displayName)) problems.push(`${item.type} name`);
      if (!han.test(item.description ?? '')) problems.push(`${item.type} description`);
      for (const parameter of item.parameters.filter((entry) => !entry.internal)) {
        if (!han.test(parameter.label)) problems.push(`${item.type}.${parameter.name} label`);
        if (!item.type.startsWith('jlvision.')) {
          for (const option of parameter.options ?? []) {
            if (!han.test(option.label)) problems.push(`${item.type}.${parameter.name} option ${option.label}`);
          }
        }
      }
    }
    expect(problems).toEqual([]);
  });

  it('labels degree-based angle ports with their unit', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    for (const type of ['feature.line', 'measure.fitLine', 'match.shape', 'match.template', 'image.register', 'geometry.angle']) {
      const angle = zh.find(item => item.type === type)?.outputs.find(port => port.name === 'angle');
      expect(angle?.label, type).toBe('角度 (°)');
    }
    for (const type of ['measure.parallelism', 'measure.perpendicularity']) {
      const angle = zh.find(item => item.type === type)?.outputs.find(port => port.name === 'angleError');
      expect(angle?.label, type).toBe('角度误差 (°)');
    }
  });

  it('keeps shared unit and area labels meaningful in Chinese', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    const unit = zh.find(item => item.type === 'coordinate.transformConstant')!.parameters.find(parameter => parameter.name === 'unit')!;
    expect(unit.options?.map(option => option.label)).toEqual(['像素 (px)', '毫米 (mm)', '厘米 (cm)', '米 (m)']);
    const resize = zh.find(item => item.type === 'image.resize')!.parameters.find(parameter => parameter.name === 'interpolation')!;
    expect(resize.options?.find(option => option.value === 'area')?.label).toBe('面积');
  });
});

describe('matching step labels', () => {
  it('distinguishes shape training steps from template search steps', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    const parameter = (type: string, name: string) =>
      zh.find(item => item.type === type)?.parameters.find(entry => entry.name === name)?.label;
    expect(parameter('match.shape', 'angleStepDeg')).toBe('训练角度步长');
    expect(parameter('match.template', 'angleStepDeg')).toBe('搜索角度步长');
    expect(parameter('match.shape', 'scaleStepPercent')).toBe('训练缩放步长');
    expect(parameter('match.template', 'scaleStepPercent')).toBe('搜索缩放步长');
  });
});

describe('caliper width labels', () => {
  it('distinguishes the two nominal-width roles and marks pixel outputs', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    const parameter = (type: string, name: string) =>
      zh.find(item => item.type === type)?.parameters.find(entry => entry.name === name)?.label;
    expect(parameter('measure.caliperWidth', 'nominalWidth')).toBe('标称宽度（最近宽度选择）');
    expect(parameter('measure.caliperArray', 'nominalWidth')).toBe('标称宽度（仅报告偏差）');
    const array = zh.find(item => item.type === 'measure.caliperArray')!;
    expect(array.outputs.find(port => port.name === 'widthMean')?.label).toBe('平均宽度 (px)');
    const single = zh.find(item => item.type === 'measure.caliperWidth')!;
    expect(single.outputs.find(port => port.name === 'width')?.label).toBe('宽度 (px)');
  });
});

describe('measurement output units', () => {
  it('marks pixel, area and gray-level outputs', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    const output = (type: string, name: string) => zh.find(item => item.type === type)?.outputs.find(port => port.name === name)?.label;
    expect(output('measure.wallThickness', 'meanThickness')).toBe('平均壁厚 (px)');
    expect(output('measure.lineSpacing', 'distance')).toBe('距离 (px)');
    expect(output('measure.blobs', 'radius')).toBe('半径 (px)');
    expect(output('region.properties', 'area')).toBe('面积 (px²)');
    expect(output('measure.regionIntensity', 'min')).toBe('最小值 (灰度)');
  });
});

describe('calibration output labels', () => {
  it('localizes planar RMSE instead of leaving the acronym alone', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    const planar = zh.find(item => item.type === 'calibration.planar')!;
    expect(planar.outputs.find(port => port.name === 'rmse')?.label).toBe('均方根误差');
    expect(planar.outputs.find(port => port.name === 'maxError')?.label).toBe('最大误差');
  });
});
