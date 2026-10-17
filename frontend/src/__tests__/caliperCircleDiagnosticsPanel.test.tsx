import { useState } from 'react';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import CaliperCircleDiagnosticsPanel from '../components/CaliperCircleDiagnosticsPanel';
import { diagnosticManifest, diagnosticProfile } from './fixtures/caliperDiagnostics';

const props = () => ({ manifest: diagnosticManifest(), profile: diagnosticProfile(), index: 0, available: true,
  loading: false, stale: false, busy: false, runDisabled: false, onSelect: vi.fn(), onRetry: vi.fn(), onRun: vi.fn() });

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addEventListener() {}, removeEventListener() {} })));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('single-caliper diagnostic presentation', () => {
  it('breaks the grayscale/gradient paths at ROI gaps and plots the actual selected candidate at the same radius', () => {
    render(<CaliperCircleDiagnosticsPanel {...props()} />);
    const gray = screen.getByRole('img', { name: /所选卡尺灰度剖面/ });
    const response = screen.getByRole('img', { name: /相邻灰度差/ });
    const grayPath = gray.querySelector('path[stroke="#36a9ff"]')!.getAttribute('d')!;
    const gradientPath = response.querySelector('path[stroke="#8093ad"]')!.getAttribute('d')!;
    expect((grayPath.match(/M/g) ?? []).length).toBe(2);
    expect((gradientPath.match(/M/g) ?? []).length).toBe(2);
    expect(gray.innerHTML + response.innerHTML).not.toMatch(/NaN|Infinity/);
    const selectedRadius = gray.querySelector('line[stroke="#52c41a"]')!.getAttribute('x1');
    expect(response.querySelector('circle')!.getAttribute('cx')).toBe(selectedRadius);
    expect(response.querySelector('circle title')!.textContent).toContain('选中边缘：半径 20 px，响应 64');
    expect(response.querySelectorAll('line[stroke="#fa8c16"]')).toHaveLength(2);
  });

  it('supports index, previous/next and wrapping abnormal-spoke navigation without requesting execution', () => {
    const base = props(); base.manifest.spokes[2].status = 'radius-deviation';
    function Workspace() {
      const [index, select] = useState(0);
      return <CaliperCircleDiagnosticsPanel {...base} index={index} profile={diagnosticProfile(index)} onSelect={select} />;
    }
    render(<Workspace />);
    fireEvent.click(screen.getByRole('button', { name: '下一异常 (2)' }));
    expect((screen.getByRole('spinbutton', { name: '诊断卡尺序号' }) as HTMLInputElement).value).toBe('2');
    expect(screen.getByText('拟合离群点')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '下一异常 (2)' }));
    expect(screen.getByText('超过半径偏差')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '下一异常 (2)' }));
    expect((screen.getByRole('spinbutton', { name: '诊断卡尺序号' }) as HTMLInputElement).value).toBe('2');
    fireEvent.click(screen.getByRole('button', { name: '上一条卡尺' }));
    expect(screen.getByText('参与拟合')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '下一条卡尺' }));
    expect(screen.getByText('拟合离群点')).toBeTruthy();
    expect(base.onRun).not.toHaveBeenCalled();
  });

  it('keeps the snapshot threshold and selected edge visible when draft parameters become stale', () => {
    const base = props(); render(<CaliperCircleDiagnosticsPanel {...base} stale />);
    expect(screen.getByText('诊断已过期 · 当前参数待验证')).toBeTruthy();
    expect(screen.getByText(/图像、卡尺和曲线均保留上次运行快照/)).toBeTruthy();
    expect(screen.getByText('64 / 8')).toBeTruthy();
    expect(screen.getByRole('img', { name: /所选卡尺灰度剖面/ })).toBeTruthy();
    expect(screen.getByRole('img', { name: /相邻灰度差/ })).toBeTruthy();
    expect(base.onRun).not.toHaveBeenCalled();
  });

  it.each([
    ['outside-image', '卡尺位于图像外'], ['clipped-too-short', '裁切后短于 2 px'],
    ['profile-has-no-finite-samples', 'ROI 内无有效采样'],
    ['profile-has-insufficient-contiguous-samples', '连续有效采样不足']
  ])('explains missing profile %s instead of drawing an invented curve', (status, text) => {
    const base = props(); base.manifest.spokes[0] = { ...base.manifest.spokes[0], status, sampleCount: 0, validSampleCount: 0,
      candidateCount: 0, edgePoint: null, edgeRadius: null, edgeStrength: null };
    render(<CaliperCircleDiagnosticsPanel {...base} profile={{ ...base.profile, profile: [], candidates: [], edgeRadius: null, edgeStrength: null }} />);
    expect(screen.getByText(text, { exact: true })).toBeTruthy();
    expect(screen.queryByRole('img', { name: /所选卡尺灰度剖面/ })).toBeNull();
    expect(screen.queryByRole('img', { name: /相邻灰度差/ })).toBeNull();
    expect(screen.getByText(/此卡尺没有可绘制的剖面/)).toBeTruthy();
  });

  it('explains a failed overall fit even when the selected spoke participated', () => {
    const base = props(); base.manifest.found = false; base.manifest.reason = 'fit-error-exceeds-tolerance';
    render(<CaliperCircleDiagnosticsPanel {...base} />);
    expect(screen.getByText(/整体未通过：拟合误差超出容差/)).toBeTruthy();
    expect(screen.getByText('参与拟合')).toBeTruthy();
  });

  it('separates retrying expired reads from an explicit node execution', () => {
    const base = props(); render(<CaliperCircleDiagnosticsPanel {...base} error="诊断已过期" />);
    fireEvent.click(within(screen.getByRole('alert')).getByRole('button', { name: '重试读取' }));
    expect(base.onRetry).toHaveBeenCalledOnce(); expect(base.onRun).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '运行并更新诊断' }));
    expect(base.onRun).toHaveBeenCalledOnce();
  });
});
