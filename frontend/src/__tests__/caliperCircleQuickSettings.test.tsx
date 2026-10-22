import { useState } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import CaliperCircleQuickSettings from '../components/CaliperCircleQuickSettings';
import type { NodeParameters } from '../nodeConfigurationState';

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mount(initial: NodeParameters) {
  function Harness() {
    const [parameters, setParameters] = useState(initial);
    return <><CaliperCircleQuickSettings parameters={parameters} disabled={false}
      onChange={patch => setParameters(current => ({ ...current, ...patch }))} />
      <output data-testid="parameters">{JSON.stringify(parameters)}</output>
      <button onClick={() => setParameters(current => ({ ...current, angleStartDeg: 270, angleEndDeg: 450 }))}>参数面板修改圆弧</button></>;
  }
  render(<Harness />);
  return () => JSON.parse(screen.getByTestId('parameters').textContent!) as NodeParameters;
}

describe('caliper-circle quick settings', () => {
  it('changes only the chosen arc and reflects equivalent angles edited in the parameter panel', () => {
    const initial = { centerX: 12, centerY: 34, radius: 22, radialBand: 5, minContrast: 18, maxFitOutliers: 3 };
    const parameters = mount(initial);
    fireEvent.click(screen.getByRole('button', { name: '上半圆' }));
    expect(parameters()).toEqual({ ...initial, angleStartDeg: -180, angleEndDeg: 0 });
    expect(screen.getByLabelText('圆卡尺采样摘要').textContent).toContain('72 条卡尺 · 180° 圆弧');
    fireEvent.click(screen.getByRole('button', { name: '参数面板修改圆弧' }));
    expect(screen.getByRole('button', { name: '右半圆' }).getAttribute('aria-pressed')).toBe('true');
  });

  it('switches legacy step sampling to an explicit count and selects an inward edge search', () => {
    const parameters = mount({ angleStepDeg: 10, angleStartDeg: -180, angleEndDeg: 180, polarity: 'Rising' });
    expect(screen.getByLabelText('圆卡尺采样摘要').textContent).toContain('36 条卡尺');
    fireEvent.click(screen.getByRole('button', { name: '144 条' }));
    fireEvent.click(screen.getByRole('radio', { name: '由外向内' }));
    expect(parameters()).toMatchObject({ samplingMode: 'count', caliperCount: 144, searchDirection: 'firstfromend', polarity: 'Rising' });
    expect(screen.getByLabelText('圆卡尺采样摘要').textContent).toContain('144 条卡尺 · 360° 圆弧 · 间距 2.5°');
  });

  it('prevents density presets below the required valid-caliper count', () => {
    mount({ minValidCalipers: 30 });
    expect((screen.getByRole('button', { name: '24 条' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '72 条' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('rotates wrapped arcs and selects the complementary arc without changing the circle or thresholds', () => {
    const initial = { centerX: 10, centerY: 20, radius: 30, radialBand: 8, minContrast: 17,
      angleStartDeg: 350, angleEndDeg: 10, samplingMode: 'step', angleStepDeg: 2 };
    const parameters = mount(initial);
    fireEvent.click(screen.getByRole('button', { name: '顺时针 15°' }));
    expect(parameters()).toEqual({ ...initial, angleStartDeg: 5, angleEndDeg: 25 });
    fireEvent.click(screen.getByRole('button', { name: '反选圆弧' }));
    expect(parameters()).toEqual({ ...initial, angleStartDeg: 25, angleEndDeg: 365 });
    expect(screen.getByLabelText('圆卡尺采样摘要').textContent).toContain('340° 圆弧');
    fireEvent.click(screen.getByRole('button', { name: '反选圆弧' }));
    fireEvent.click(screen.getByRole('button', { name: '逆时针 15°' }));
    expect(parameters()).toEqual({ ...initial, angleStartDeg: -10, angleEndDeg: 10 });
  });

  it('updates the same contrast value from numeric input and keyboard slider adjustments', () => {
    const parameters = mount({ minContrast: 8, radialBand: 6, polarity: 'Rising' });
    fireEvent.change(screen.getByRole('spinbutton', { name: '最小边缘对比度' }), { target: { value: '27' } });
    const slider = screen.getByRole('slider', { name: '最小边缘对比度滑动调节' });
    expect(slider.getAttribute('aria-valuenow')).toBe('27');
    fireEvent.keyDown(slider, { key: 'ArrowRight', keyCode: 39 });
    expect(parameters()).toEqual({ minContrast: 28, radialBand: 6, polarity: 'Rising' });
    expect((screen.getByRole('spinbutton', { name: '最小边缘对比度' }) as HTMLInputElement).value).toBe('28');
  });

  it('keeps a typed even projection width until edited while previewing the effective odd width', () => {
    const parameters = mount({ sampleWidth: 4 });
    expect((screen.getByRole('spinbutton', { name: '投影长度' }) as HTMLInputElement).value).toBe('4');
    expect(screen.getByText(/实际投影 5 px/)).toBeTruthy();
    const slider = screen.getByRole('slider', { name: '投影长度滑动调节' });
    expect(slider.getAttribute('aria-valuenow')).toBe('5');
    fireEvent.keyDown(slider, { key: 'ArrowRight', keyCode: 39 });
    expect(parameters().sampleWidth).toBe(7);
    expect((screen.getByRole('spinbutton', { name: '投影长度' }) as HTMLInputElement).value).toBe('7');
  });

  it('keeps cleared inputs invalid until the user restores or edits them', () => {
    const parameters = mount({ minContrast: 18 });
    const input = screen.getByRole('spinbutton', { name: '最小边缘对比度' });
    fireEvent.change(input, { target: { value: '' } });
    expect(parameters().minContrast).toBeNull();
    expect(input.getAttribute('aria-invalid')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: '恢复 最小边缘对比度 的默认值' }));
    expect(parameters().minContrast).toBe(8);
    expect(input.getAttribute('aria-invalid')).toBe('false');
  });

  it('freezes shortcuts while execution locks parameter editing', () => {
    const onChange = vi.fn();
    render(<CaliperCircleQuickSettings parameters={{}} disabled onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: '上半圆' }));
    fireEvent.click(screen.getByRole('button', { name: '24 条' }));
    expect((screen.getByRole('radio', { name: '由外向内' }) as HTMLInputElement).disabled).toBe(true);
    expect(onChange).not.toHaveBeenCalled();
  });
});
