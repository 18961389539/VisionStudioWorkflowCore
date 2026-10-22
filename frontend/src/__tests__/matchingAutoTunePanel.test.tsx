import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigProvider } from 'antd';
import MatchingAutoTunePanel from '../components/MatchingAutoTunePanel';

const response = {
  nodeType: 'match.shape',
  pyramidLevels: 4,
  angleStepDeg: 0.8,
  contrast: 12,
  minContrast: 6,
  optimization: 'auto',
  inspect: { levels: 4, modelPointCount: 420, coverageRatio: 0.42, meanGradient: 88.2, quality: 'good', note: null, warnings: [] },
  warnings: [{ code: 'coarser-than-current', message: '推荐步长比当前更粗' }],
  ignoredParameters: [],
  raw: {},
  apply: { parameterSource: 'auto', pyramidLevels: 4, angleStepDeg: 0.8, minContrast: 6, autoTuneReportId: 'abc123' },
  provenance: {
    autoTuneVersion: 'auto-v1', nativeLibrary: '1.0.0', templateSha256: 'a'.repeat(64),
    templateWidth: 200, templateHeight: 120, computedAt: '2026-10-18T23:00:00+08:00'
  }
};

beforeEach(() => {
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mount(parameters: Record<string, unknown>, onApply = vi.fn()) {
  const fetchMock = vi.fn(async () => ({ ok: true, json: async () => response }) as Response);
  vi.stubGlobal('fetch', fetchMock);
  render(<ConfigProvider theme={{ token: { motion: false } }}>
    <MatchingAutoTunePanel nodeType="match.shape" parameters={parameters} onApply={onApply} />
  </ConfigProvider>);
  return { fetchMock, onApply };
}

describe('matching auto tune panel', () => {
  it('requires a template path before calling the endpoint', () => {
    const { fetchMock } = mount({ pyramidLevels: 0 });
    expect(screen.getByRole('button', { name: /自动调参/ })).toBeDisabled();
    expect(screen.getByText(/需要模板路径/)).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('posts the node parameters and shows the recommendation diff', async () => {
    const { fetchMock } = mount({ templatePath: 'templates/part.png', pyramidLevels: 0, angleStepDeg: 1, minContrast: 5, trainingAngleRangeDeg: 180 });

    fireEvent.click(screen.getByRole('button', { name: /自动调参/ }));

    await waitFor(() => expect(screen.getByText(/模板质量：良好/)).toBeInTheDocument());
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/matching/auto-tune');
    const body = JSON.parse(String(init.body)) as Record<string, unknown>;
    expect(body.templatePath).toBe('templates/part.png');
    expect(body.nodeType).toBe('match.shape');
    expect(body.angleRangeDeg).toBe(360);
    expect(screen.getByText('0.8°')).toBeInTheDocument();
    expect(document.body.textContent).toContain('模型点 420');
    expect(screen.getByText(/推荐步长比当前更粗/)).toBeInTheDocument();
  });

  it('applies the server provided payload', async () => {
    const { onApply } = mount({ templatePath: 'templates/part.png', pyramidLevels: 0, angleStepDeg: 1, minContrast: 5 });

    fireEvent.click(screen.getByRole('button', { name: /自动调参/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: '应用推荐值' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: '应用推荐值' }));

    expect(onApply).toHaveBeenCalledWith(response.apply);
    expect(screen.getByText(/已写入节点参数/)).toBeInTheDocument();
  });
});
