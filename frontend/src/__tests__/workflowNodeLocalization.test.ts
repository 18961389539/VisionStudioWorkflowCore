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
  raw.parameters.forEach((parameter, index) => {
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

  it('keeps shared unit and area labels meaningful in Chinese', () => {
    const zh = localizeCatalog(fallbackCatalogRaw, 'zh');
    const unit = zh.find(item => item.type === 'coordinate.transformConstant')!.parameters.find(parameter => parameter.name === 'unit')!;
    expect(unit.options?.map(option => option.label)).toEqual(['像素 (px)', '毫米 (mm)', '厘米 (cm)', '米 (m)']);
    const resize = zh.find(item => item.type === 'image.resize')!.parameters.find(parameter => parameter.name === 'interpolation')!;
    expect(resize.options?.find(option => option.value === 'area')?.label).toBe('面积');
  });
});
