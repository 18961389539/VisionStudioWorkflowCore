import { afterEach, describe, expect, it } from 'vitest';
import { automaticResultHeight, persistLayout, readLayout } from '../layout';

afterEach(() => localStorage.clear());

describe('layout without the properties sidebar', () => {
  it('reads saved layouts while discarding retired sidebar and image pane dimensions', () => {
    localStorage.setItem('visionstudio.layout', JSON.stringify({ preset: 'custom', left: 240, right: 500, rightCollapsed: false, bottom: 140, image: 62, imageCollapsed: false }));
    const layout = readLayout();
    expect(layout.left).toBe(240);
    expect(layout.bottom).toBe(140);
    expect(layout).not.toHaveProperty('image');
    expect(layout).not.toHaveProperty('imageCollapsed');
    expect(layout).not.toHaveProperty('right');
    expect(layout).not.toHaveProperty('rightCollapsed');
    persistLayout(layout);
    expect(JSON.parse(localStorage.getItem('visionstudio.layout')!)).toEqual(layout);
  });

  it('moves legacy large panels to automatic sizing and adapts to the viewport', () => {
    localStorage.setItem('visionstudio.layout', JSON.stringify({ bottom: 516, preset: 'custom' }));
    expect(readLayout().bottomSizing).toBe('auto');
    expect(automaticResultHeight(1040)).toBe(268);
    expect(automaticResultHeight(800)).toBe(203);
  });

  it('preserves a manual size after subsequent loads', () => {
    persistLayout({ ...readLayout(), bottom: 410, bottomSizing: 'manual' });
    expect(readLayout()).toMatchObject({ bottom: 410, bottomSizing: 'manual' });
  });
});
